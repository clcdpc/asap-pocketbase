using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task TitleRequestsTableLayoutFitsWideViewsAndContainsNarrowScrolling()
    {
        const int library = 42801;
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@library,N'Title layout library',2,1,1);
            """, ("@library", library));

        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long[] closedIds;
        long suggestionId;
        await using (var setup = await contexts.CreateDbContextAsync())
        {
            var format = await setup.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
            var now = timeProvider!.GetUtcNow().UtcDateTime;
            var rows = new[]
            {
                (Status: "suggestion", Count: 1),
                (Status: "outstanding_purchase", Count: 1),
                (Status: "pending_hold", Count: 0),
                (Status: "hold_placed", Count: 1),
                (Status: "closed", Count: 2)
            };
            foreach (var (status, count) in rows)
            {
                for (var index = 0; index < count; index++)
                {
                    var longTitle = status == "closed" && index == 1;
                    setup.TitleRequests.Add(new TitleRequest
                    {
                        LibraryOrganizationId = library,
                        Barcode = $"LAYOUT-{status}-{index}",
                        Title = longTitle
                            ? string.Join(' ', Enumerable.Repeat("Long bibliographic title segment", 12))
                            : $"Title layout {status} {index}",
                        Author = longTitle
                            ? string.Join(' ', Enumerable.Repeat("Long author name segment", 10))
                            : $"Layout author {index}",
                        MaterialFormatId = format.Id,
                        Status = status,
                        AutoHold = false,
                        BibId = status is "pending_hold" or "hold_placed" ? 428900 + index : null,
                        BibIdStaffVerified = status is "pending_hold" or "hold_placed",
                        CloseReason = status == "closed" ? "manual" : null,
                        CreatedUtc = now.AddMinutes(-index),
                        UpdatedUtc = now
                    });
                }
            }

            await setup.SaveChangesAsync();
            suggestionId = await setup.TitleRequests.Where(item => item.LibraryOrganizationId == library && item.Status == "suggestion")
                .Select(item => item.Id).SingleAsync();
            closedIds = await setup.TitleRequests.Where(item => item.LibraryOrganizationId == library && item.Status == "closed")
                .OrderBy(item => item.Id).Select(item => item.Id).ToArrayAsync();
        }

        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "i439", "i428", $"title-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "fixture.json"), JsonSerializer.Serialize(new
        {
            library = library.ToString(CultureInfo.InvariantCulture),
            suggestionId = suggestionId.ToString(CultureInfo.InvariantCulture),
            closedIds = closedIds.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
            super = new
            {
                id = actor.Id.ToString(CultureInfo.InvariantCulture),
                tenantId = actor.EntraTenantId.ToString(),
                email = actor.AuthenticationEmail
            }
        }));

        try
        {
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "title-requests-layout.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(4, report.RootElement.GetProperty("viewportMatrix").GetArrayLength());
            Assert.AreEqual(12, report.RootElement.GetProperty("resizeOnly").GetArrayLength());
            Assert.AreEqual(2, report.RootElement.GetProperty("nativeZoom").GetArrayLength());
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == library)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        }
    }
}
