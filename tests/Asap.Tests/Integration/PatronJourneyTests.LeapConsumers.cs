using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task LeapLinksBrowserUsesPinnedNativeIdsAcrossStaffSurfaces()
    {
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var setup = await contexts.CreateDbContextAsync();
        var system = await setup.SystemSettings.SingleAsync();
        var originalBib = system.LeapBibUrlPattern;
        var originalPatron = system.LeapPatronUrlPattern;
        system.LeapBibUrlPattern = "https://leap.example.test/#bib/{{bibid}}/{{bibid}}";
        system.LeapPatronUrlPattern = "https://leap.example.test/#patron/{{patronId}}";
        var format = await setup.MaterialFormats.FirstAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2, Barcode = "20000000000001", PatronIdSnapshot = 7001,
            NameFirst = "Native", NameLast = "Reader", Title = "LEAP pinned " + suffix,
            MaterialFormatId = format.Id, Status = "pending_hold", BibId = 9001,
            BibIdStaffVerified = true, CreatedUtc = now, UpdatedUtc = now
        };
        var legacy = new TitleRequest
        {
            LibraryOrganizationId = 2, Barcode = "legacy-no-native-id", Title = "LEAP legacy " + suffix,
            MaterialFormatId = format.Id, Status = "suggestion", CreatedUtc = now, UpdatedUtc = now
        };
        var copy = new AdditionalCopyRequest
        {
            LibraryOrganizationId = 2, BibId = 9001, Title = "LEAP copy " + suffix,
            Status = "open", CreatedUtc = now, UpdatedUtc = now
        };
        setup.TitleRequests.AddRange(request, legacy);
        setup.AdditionalCopyRequests.Add(copy);
        await setup.SaveChangesAsync();
        try
        {
            using var queueResponse = await client.GetAsync("/api/asap/staff/title-requests?scope=2");
            Assert.AreEqual(HttpStatusCode.OK, queueResponse.StatusCode);
            using var queue = JsonDocument.Parse(await queueResponse.Content.ReadAsStringAsync());
            var row = queue.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == request.Id.ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual(7001, row.GetProperty("research").GetProperty("patronId").GetInt32());
            var oldRow = queue.RootElement.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == legacy.Id.ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual(JsonValueKind.Null, oldRow.GetProperty("research").GetProperty("patronId").ValueKind);
            using var publicConfiguration = await client.GetAsync("/api/asap/config?libraryOrgId=2");
            Assert.IsFalse((await publicConfiguration.Content.ReadAsStringAsync()).Contains("leap", StringComparison.OrdinalIgnoreCase));
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"leap-consumers-{suffix}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "leap-consumers.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                request.Id.ToString(CultureInfo.InvariantCulture), legacy.Id.ToString(CultureInfo.InvariantCulture), copy.Id.ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.AdditionalCopyRequests.Where(item => item.Id == copy.Id).ExecuteDeleteAsync();
            await cleanup.TitleRequests.Where(item => item.Id == request.Id || item.Id == legacy.Id).ExecuteDeleteAsync();
            var row = await cleanup.SystemSettings.SingleAsync();
            row.LeapBibUrlPattern = originalBib; row.LeapPatronUrlPattern = originalPatron;
            await cleanup.SaveChangesAsync();
        }
    }
}
