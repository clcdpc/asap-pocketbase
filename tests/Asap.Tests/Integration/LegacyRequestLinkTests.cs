using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LegacyRequestLinksResolveByTypeThenApplyRealStaffScope()
    {
        var seeded = await SeedLegacyRequestLinkFixtureAsync();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var tenantId = Guid.Parse(identity.TenantId!);

        using var anonymous = factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var anonymousTitle = await anonymous.GetAsync(
            $"/api/asap/staff/title-requests/{seeded.SharedLegacyId}");
        Assert.AreEqual(HttpStatusCode.Redirect, anonymousTitle.StatusCode, $"Anonymous request returned {anonymousTitle.StatusCode}.");

        using var superAdmin = factory.CreateClient();
        AddTestingStaffHeaders(superAdmin, seeded.SuperId, tenantId, Guid.Parse(identity.ObjectId!));
        using var title = await superAdmin.GetAsync(
            $"/api/asap/staff/title-requests/{seeded.SharedLegacyId}");
        using var copy = await superAdmin.GetAsync(
            $"/api/asap/staff/additional-copies/{seeded.SharedLegacyId}");
        Assert.AreEqual(HttpStatusCode.OK, title.StatusCode, await title.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, copy.StatusCode, await copy.Content.ReadAsStringAsync());
        using var titleBody = JsonDocument.Parse(await title.Content.ReadAsStringAsync());
        using var copyBody = JsonDocument.Parse(await copy.Content.ReadAsStringAsync());
        Assert.AreEqual(seeded.SharedTitleId.ToString(), titleBody.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("title_request", titleBody.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("Legacy title target", titleBody.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(seeded.SharedCopyId.ToString(), copyBody.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("additional_copy", copyBody.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("Legacy additional-copy target", copyBody.RootElement.GetProperty("title").GetString());

        using var ordinary = factory.CreateClient();
        AddTestingStaffHeaders(ordinary, seeded.StaffId, tenantId, seeded.StaffObjectId);
        foreach (var request in new[]
                 {
                     ("title", seeded.NumericLegacyId, seeded.NumericTitleId.ToString(CultureInfo.InvariantCulture)),
                     ("copy", seeded.NumericLegacyId, seeded.NumericCopyId.ToString(CultureInfo.InvariantCulture)),
                     ("title", seeded.SharedTitleId.ToString(CultureInfo.InvariantCulture), seeded.SharedTitleId.ToString(CultureInfo.InvariantCulture)),
                     ("copy", seeded.SharedCopyId.ToString(CultureInfo.InvariantCulture), seeded.SharedCopyId.ToString(CultureInfo.InvariantCulture)),
                 })
        {
            var path = request.Item1 == "title"
                ? $"/api/asap/staff/title-requests/{request.Item2}"
                : $"/api/asap/staff/additional-copies/{request.Item2}";
            using var response = await ordinary.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(request.Item3, body.RootElement.GetProperty("id").GetString(), request.Item1);
        }

        foreach (var request in new[]
                 {
                     ("title", seeded.MissingTitleLegacyId),
                     ("title", seeded.DeletedTitleLegacyId),
                     ("title", seeded.OutOfScopeTitleLegacyId),
                     ("copy", seeded.MissingCopyLegacyId),
                     ("copy", seeded.DeletedCopyLegacyId),
                     ("copy", seeded.OutOfScopeCopyLegacyId)
                 })
        {
            var path = request.Item1 == "title"
                ? $"/api/asap/staff/title-requests/{request.Item2}"
                : $"/api/asap/staff/additional-copies/{request.Item2}";
            using var response = await ordinary.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, request.Item1);
            Assert.IsFalse(body.Contains("Out-of-scope", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("Legacy title target", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("Legacy additional-copy target", StringComparison.Ordinal));
        }

        await using var context = await factory.Services
            .GetRequiredService<IDbContextFactory<AsapDbContext>>()
            .CreateDbContextAsync();
        var retainedMappings = await context.LegacyPocketBaseMappings.AsNoTracking()
            .Where(item => item.PocketBaseId == seeded.DeletedTitleLegacyId ||
                           item.PocketBaseId == seeded.DeletedCopyLegacyId)
            .CountAsync();
        Assert.AreEqual(2, retainedMappings, "Read failures must not automatically purge mappings.");
    }

    [TestMethod]
    public async Task LegacyRequestLinksReplaceDesktopAndMobileUrlsWithoutLeakingFailures()
    {
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var baseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedLegacyRequestLinkFixtureAsync();
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            $"asap-slice07-package3-legacy-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);

        var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "legacy-links.cjs"));
        startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(artifactDirectory);
        startInfo.ArgumentList.Add(seeded.SuperId.ToString());
        startInfo.ArgumentList.Add(identity.TenantId!);
        startInfo.ArgumentList.Add(identity.ObjectId!);
        startInfo.ArgumentList.Add(seeded.StaffId.ToString());
        startInfo.ArgumentList.Add(seeded.StaffObjectId.ToString());
        startInfo.ArgumentList.Add(seeded.SharedLegacyId);
        startInfo.ArgumentList.Add(seeded.NumericLegacyId);
        startInfo.ArgumentList.Add(seeded.SharedTitleId.ToString());
        startInfo.ArgumentList.Add(seeded.SharedCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.NumericTitleId.ToString());
        startInfo.ArgumentList.Add(seeded.NumericCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.MissingTitleLegacyId);
        startInfo.ArgumentList.Add(seeded.DeletedTitleLegacyId);
        startInfo.ArgumentList.Add(seeded.OutOfScopeTitleLegacyId);
        startInfo.ArgumentList.Add(seeded.MissingCopyLegacyId);
        startInfo.ArgumentList.Add(seeded.DeletedCopyLegacyId);
        startInfo.ArgumentList.Add(seeded.OutOfScopeCopyLegacyId);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the legacy-link browser runner.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.AreEqual(0, process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");

        using var report = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "legacy-links-results.json")));
        Assert.AreEqual(18, report.RootElement.GetProperty("states").GetArrayLength());
        foreach (var state in report.RootElement.GetProperty("states").EnumerateArray())
        {
            Assert.AreEqual(0, state.GetProperty("accessibility").GetArrayLength());
            Assert.IsTrue(
                state.GetProperty("layout").GetProperty("scrollWidth").GetInt32() <=
                state.GetProperty("layout").GetProperty("width").GetInt32());
        }
    }

    private async Task<LegacyRequestLinkSeed> SeedLegacyRequestLinkFixtureAsync()
    {
        const int outOfScopeOrganizationId = 99173;
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var staffObjectId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var sharedLegacyId = $"legacy-shared-{suffix}";
        var numericLegacyId =
            (9007199254740993L + Convert.ToInt64(suffix[..8], 16)).ToString(CultureInfo.InvariantCulture);
        var missingTitleLegacyId = $"legacy-missing-title-{suffix}";
        var deletedTitleLegacyId = $"legacy-deleted-title-{suffix}";
        var outOfScopeTitleLegacyId = $"legacy-out-title-{suffix}";
        var missingCopyLegacyId = $"legacy-missing-copy-{suffix}";
        var deletedCopyLegacyId = $"legacy-deleted-copy-{suffix}";
        var outOfScopeCopyLegacyId = $"legacy-out-copy-{suffix}";
        var now = DateTime.UtcNow;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();

        await using var context = await contextFactory.CreateDbContextAsync();
        if (await context.Organizations.FindAsync(outOfScopeOrganizationId) is null)
        {
            context.Organizations.Add(new Organization
            {
                Id = outOfScopeOrganizationId,
                DisplayName = "Out-of-scope legacy-link library",
                Abbreviation = "OLL",
                IsActive = true
            });
        }

        var format = await context.MaterialFormats.AsNoTracking()
            .SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var staff = new StaffUser
        {
            EntraTenantId = tenantId,
            EntraObjectId = staffObjectId,
            UserPrincipalName = $"legacy-links-{suffix}@example.org",
            NormalizedUserPrincipalName = $"LEGACY-LINKS-{suffix}@EXAMPLE.ORG",
            DisplayName = "Legacy-link staff",
            NotificationEmail = $"legacy-links-{suffix}@example.org",
            Role = "staff",
            OrganizationId = 2,
            IsActive = true
        };
        context.StaffUsers.Add(staff);

        var sharedTitle = new TitleRequest
        {
            LegacyId = sharedLegacyId,
            LibraryOrganizationId = 2,
            Barcode = $"legacy-title-{suffix}",
            Title = "Legacy title target",
            MaterialFormatId = format.Id,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var numericTitle = new TitleRequest
        {
            LegacyId = numericLegacyId,
            LibraryOrganizationId = 2,
            Barcode = $"numeric-title-{suffix}",
            Title = "Numeric legacy title target",
            MaterialFormatId = format.Id,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var outOfScopeTitle = new TitleRequest
        {
            LibraryOrganizationId = outOfScopeOrganizationId,
            Barcode = $"out-title-{suffix}",
            Title = "Out-of-scope title target",
            MaterialFormatId = format.Id,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        context.TitleRequests.AddRange(sharedTitle, numericTitle, outOfScopeTitle);
        await context.SaveChangesAsync();

        var sharedCopy = CreateCopy(sharedLegacyId, "Legacy additional-copy target", 2, sharedTitle.Id, format.Id, now, suffix);
        var numericCopy = CreateCopy(numericLegacyId, "Numeric legacy additional-copy target", 2, sharedTitle.Id, format.Id, now, suffix);
        var outOfScopeCopy = CreateCopy(outOfScopeCopyLegacyId, "Out-of-scope additional-copy target", outOfScopeOrganizationId, outOfScopeTitle.Id, format.Id, now, suffix);
        context.AdditionalCopyRequests.AddRange(sharedCopy, numericCopy, outOfScopeCopy);
        await context.SaveChangesAsync();

        context.LegacyPocketBaseMappings.AddRange(
            Mapping(LegacyRequestLinkResolver.TitleRequestEntityType, sharedLegacyId, sharedTitle.Id),
            Mapping(LegacyRequestLinkResolver.AdditionalCopyEntityType, sharedLegacyId, sharedCopy.Id),
            Mapping(LegacyRequestLinkResolver.TitleRequestEntityType, numericLegacyId, numericTitle.Id),
            Mapping(LegacyRequestLinkResolver.AdditionalCopyEntityType, numericLegacyId, numericCopy.Id),
            Mapping(LegacyRequestLinkResolver.TitleRequestEntityType, missingTitleLegacyId, long.MaxValue),
            Mapping(LegacyRequestLinkResolver.TitleRequestEntityType, deletedTitleLegacyId, long.MaxValue),
            Mapping(LegacyRequestLinkResolver.TitleRequestEntityType, outOfScopeTitleLegacyId, outOfScopeTitle.Id),
            Mapping(LegacyRequestLinkResolver.AdditionalCopyEntityType, missingCopyLegacyId, long.MaxValue),
            Mapping(LegacyRequestLinkResolver.AdditionalCopyEntityType, deletedCopyLegacyId, long.MaxValue),
            Mapping(LegacyRequestLinkResolver.AdditionalCopyEntityType, outOfScopeCopyLegacyId, outOfScopeCopy.Id));
        await context.SaveChangesAsync();

        var superAdmin = await ReadConfiguredSuperAdminAsync();
        return new LegacyRequestLinkSeed(
            superAdmin.Id,
            staff.Id,
            staffObjectId,
            sharedLegacyId,
            numericLegacyId,
            sharedTitle.Id,
            sharedCopy.Id,
            numericTitle.Id,
            numericCopy.Id,
            missingTitleLegacyId,
            deletedTitleLegacyId,
            outOfScopeTitleLegacyId,
            missingCopyLegacyId,
            deletedCopyLegacyId,
            outOfScopeCopyLegacyId);

        static LegacyPocketBaseMapping Mapping(string entityType, string pocketBaseId, long newId) => new()
        {
            EntityType = entityType,
            PocketBaseId = pocketBaseId,
            NewId = newId
        };

        static AdditionalCopyRequest CreateCopy(
            string legacyId,
            string title,
            int organizationId,
            long sourceTitleRequestId,
            long formatId,
            DateTime createdUtc,
            string suffix) => new()
        {
            LegacyId = legacyId,
            SourceTitleRequestId = sourceTitleRequestId,
            LibraryOrganizationId = organizationId,
            LibraryNameSnapshot = organizationId == 2 ? "Test Library" : "Out-of-scope library",
            BibId = $"legacy-copy-{suffix}",
            Title = title,
            MaterialFormatId = formatId,
            FormatSnapshot = "book",
            Status = "open",
            CreatedUtc = createdUtc,
            UpdatedUtc = createdUtc
        };
    }

    private sealed record LegacyRequestLinkSeed(
        long SuperId,
        long StaffId,
        Guid StaffObjectId,
        string SharedLegacyId,
        string NumericLegacyId,
        long SharedTitleId,
        long SharedCopyId,
        long NumericTitleId,
        long NumericCopyId,
        string MissingTitleLegacyId,
        string DeletedTitleLegacyId,
        string OutOfScopeTitleLegacyId,
        string MissingCopyLegacyId,
        string DeletedCopyLegacyId,
        string OutOfScopeCopyLegacyId);
}
