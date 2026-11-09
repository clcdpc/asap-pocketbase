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
    public async Task NativeRequestLinksApplyRealStaffScopeAndRejectUnknownIdentifiers()
    {
        var seeded = await SeedNativeRequestLinkFixtureAsync();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var tenantId = Guid.Parse(identity.TenantId!);

        using var anonymous = factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var anonymousTitle = await anonymous.GetAsync(
            $"/api/asap/staff/title-requests/{seeded.SharedTitleId}");
        Assert.AreEqual(HttpStatusCode.Redirect, anonymousTitle.StatusCode, $"Anonymous request returned {anonymousTitle.StatusCode}.");

        using var superAdmin = factory.CreateClient();
        AddTestingStaffHeaders(superAdmin, seeded.SuperId, tenantId, seeded.SuperAuthenticationEmail);
        using var title = await superAdmin.GetAsync(
            $"/api/asap/staff/title-requests/{seeded.SharedTitleId}");
        using var copy = await superAdmin.GetAsync(
            $"/api/asap/staff/additional-copies/{seeded.SharedCopyId}");
        Assert.AreEqual(HttpStatusCode.OK, title.StatusCode, await title.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, copy.StatusCode, await copy.Content.ReadAsStringAsync());
        using var titleBody = JsonDocument.Parse(await title.Content.ReadAsStringAsync());
        using var copyBody = JsonDocument.Parse(await copy.Content.ReadAsStringAsync());
        Assert.AreEqual(seeded.SharedTitleId.ToString(), titleBody.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("title_request", titleBody.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("Native title target", titleBody.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(seeded.SharedCopyId.ToString(), copyBody.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("additional_copy", copyBody.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("Native additional-copy target", copyBody.RootElement.GetProperty("title").GetString());

        using var ordinary = factory.CreateClient();
        AddTestingStaffHeaders(ordinary, seeded.StaffId, tenantId, seeded.StaffAuthenticationEmail);
        foreach (var request in new[]
                 {
                     ("title", seeded.NumericTitleId.ToString(CultureInfo.InvariantCulture), seeded.NumericTitleId.ToString(CultureInfo.InvariantCulture)),
                     ("copy", seeded.NumericCopyId.ToString(CultureInfo.InvariantCulture), seeded.NumericCopyId.ToString(CultureInfo.InvariantCulture)),
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
                     ("title", seeded.MissingTitleId),
                     ("title", seeded.DeletedTitleId),
                     ("title", seeded.OutOfScopeTitleId),
                     ("copy", seeded.MissingCopyId),
                     ("copy", seeded.DeletedCopyId),
                     ("copy", seeded.OutOfScopeCopyId)
                 })
        {
            var path = request.Item1 == "title"
                ? $"/api/asap/staff/title-requests/{request.Item2}"
                : $"/api/asap/staff/additional-copies/{request.Item2}";
            using var response = await ordinary.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, request.Item1);
            Assert.IsFalse(body.Contains("Out-of-scope", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("Native title target", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("Native additional-copy target", StringComparison.Ordinal));
        }


    }

    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task NativeRequestLinksReplaceDesktopAndMobileUrlsWithoutLeakingFailures()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var baseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedNativeRequestLinkFixtureAsync();
        var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifactDirectory = Path.Combine(
            repositoryRoot,
            ".artifacts",
            "browser",
            $"legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "request-links.cjs"));
        startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(artifactDirectory);
        startInfo.ArgumentList.Add(seeded.SuperId.ToString());
        startInfo.ArgumentList.Add(identity.TenantId!);
        startInfo.ArgumentList.Add(seeded.SuperAuthenticationEmail);
        startInfo.ArgumentList.Add(seeded.StaffId.ToString());
        startInfo.ArgumentList.Add(seeded.StaffAuthenticationEmail);
        startInfo.ArgumentList.Add(seeded.SharedTitleId.ToString());
        startInfo.ArgumentList.Add(seeded.SharedCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.NumericTitleId.ToString());
        startInfo.ArgumentList.Add(seeded.NumericCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.MissingTitleId);
        startInfo.ArgumentList.Add(seeded.DeletedTitleId);
        startInfo.ArgumentList.Add(seeded.OutOfScopeTitleId);
        startInfo.ArgumentList.Add(seeded.MissingCopyId);
        startInfo.ArgumentList.Add(seeded.DeletedCopyId);
        startInfo.ArgumentList.Add(seeded.OutOfScopeCopyId);

        var (exitCode, stdout, stderr) = await OwnedTestProcess.RunAsync(startInfo, TestContext.CancellationToken);
        Assert.AreEqual(0, exitCode, $"{stdout}{Environment.NewLine}{stderr}");

        using var report = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "request-links-results.json")));
        Assert.AreEqual(18, report.RootElement.GetProperty("states").GetArrayLength());
        foreach (var state in report.RootElement.GetProperty("states").EnumerateArray())
        {
            Assert.AreEqual(0, state.GetProperty("accessibility").GetArrayLength());
            Assert.IsTrue(
                state.GetProperty("layout").GetProperty("scrollWidth").GetInt32() <=
                state.GetProperty("layout").GetProperty("width").GetInt32());
        }
    }

    private async Task<NativeRequestLinkSeed> SeedNativeRequestLinkFixtureAsync()
    {
        const int outOfScopeOrganizationId = 99173;
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var staffObjectId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var missingTitleId = "unknown-title-" + suffix;
        var deletedTitleId = long.MaxValue.ToString(CultureInfo.InvariantCulture);
        var missingCopyId = "unknown-copy-" + suffix;
        var deletedCopyId = long.MaxValue.ToString(CultureInfo.InvariantCulture);
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();

        await using var context = await contextFactory.CreateDbContextAsync();
        if (await context.Organizations.FindAsync(outOfScopeOrganizationId) is null)
        {
            context.Organizations.Add(new Organization
            {
                Id = outOfScopeOrganizationId,
                DisplayName = "Out-of-scope request-link library",
                Abbreviation = "OLL",
                OrganizationCodeId = 2,
                ParentOrganizationId = 1,
                IsActive = true
            });
        }

        var format = await context.MaterialFormats.AsNoTracking()
            .SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var staff = new StaffUser
        {
            EntraTenantId = tenantId,
            EntraObjectId = staffObjectId,
            UserPrincipalName = $"request-links-{suffix}@example.org",
            NormalizedUserPrincipalName = $"request-links-{suffix}@example.org".ToUpperInvariant(),
            DisplayName = "Legacy-link staff",
            NotificationEmail = $"request-links-{suffix}@example.org",
            Role = "staff",
            OrganizationId = 2,
            IsActive = true
        };
        context.StaffUsers.Add(staff);

        var sharedTitle = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"legacy-title-{suffix}",
            Title = "Native title target",
            MaterialFormatId = format.Id,
            Status = "suggestion",
            AutoHold = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        var numericTitle = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"numeric-title-{suffix}",
            Title = "Second native title target",
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

        var sharedCopy = CreateCopy("Native additional-copy target", 2, sharedTitle.Id, format.Id, now, suffix);
        var numericCopy = CreateCopy("Second native additional-copy target", 2, sharedTitle.Id, format.Id, now, suffix);
        var outOfScopeCopy = CreateCopy("Out-of-scope additional-copy target", outOfScopeOrganizationId, outOfScopeTitle.Id, format.Id, now, suffix);
        context.AdditionalCopyRequests.AddRange(sharedCopy, numericCopy, outOfScopeCopy);
        await context.SaveChangesAsync();


        var superAdmin = await ReadConfiguredSuperAdminAsync();
        return new NativeRequestLinkSeed(
            superAdmin.Id,
            superAdmin.AuthenticationEmail,
            staff.Id,
            staff.NormalizedUserPrincipalName!,
            sharedTitle.Id,
            sharedCopy.Id,
            numericTitle.Id,
            numericCopy.Id,
            missingTitleId,
            deletedTitleId,
            outOfScopeTitle.Id.ToString(CultureInfo.InvariantCulture),
            missingCopyId,
            deletedCopyId,
            outOfScopeCopy.Id.ToString(CultureInfo.InvariantCulture));

        static AdditionalCopyRequest CreateCopy(
            string title,
            int organizationId,
            long sourceTitleRequestId,
            long formatId,
            DateTime createdUtc,
            string suffix) => new()
        {
            SourceTitleRequestId = sourceTitleRequestId,
            LibraryOrganizationId = organizationId,
            LibraryNameSnapshot = organizationId == 2 ? "Test Library" : "Out-of-scope library",
            BibId = 9342,
            Title = title,
            MaterialFormatId = formatId,
            FormatSnapshot = "book",
            Status = "open",
            CreatedUtc = createdUtc,
            UpdatedUtc = createdUtc
        };
    }

    private sealed record NativeRequestLinkSeed(
        long SuperId,
        string SuperAuthenticationEmail,
        long StaffId,
        string StaffAuthenticationEmail,
        long SharedTitleId,
        long SharedCopyId,
        long NumericTitleId,
        long NumericCopyId,
        string MissingTitleId,
        string DeletedTitleId,
        string OutOfScopeTitleId,
        string MissingCopyId,
        string DeletedCopyId,
        string OutOfScopeCopyId);
}
