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
            INSERT INTO asap.ExternalSearchProviderOverride (LibraryOrganizationId, ExternalSearchProviderId, IsEnabled, Label, UrlTemplate)
            SELECT @library, Id, CASE WHEN ProviderKey IN ('external_search_1','external_search_2','external_search_4') THEN 1 ELSE 0 END,
                CASE ProviderKey WHEN 'external_search_1' THEN 'Local index' WHEN 'external_search_2' THEN 'Catalog'
                    WHEN 'external_search_4' THEN 'ConsortiumBibliographicResearchDirectoryWithALongLabel' END,
                CASE WHEN ProviderKey = 'external_search_1' THEN 'https://research.example.test/?q={{title}}&id={{identifier}}'
                    ELSE 'https://research.example.test/catalog/{{identifier}}' END
            FROM asap.ExternalSearchProvider;
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
                (Status: "pending_hold", Count: 1),
                (Status: "hold_placed", Count: 3),
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
                            : status == "suggestion" ? "Title layout suggestion 0 — Café & O'Brien / \"Why?\"" : $"Title layout {status} {index}",
                        Author = longTitle
                            ? string.Join(' ', Enumerable.Repeat("Long author name segment", 10))
                            : $"Layout author {index}",
                        MaterialFormatId = format.Id,
                        Identifier = status switch
                        {
                            "suggestion" => "012345678X",
                            "outstanding_purchase" => "012345678905",
                            "hold_placed" when index == 0 => "012345678X",
                            "hold_placed" when index == 2 => "012345678905",
                            "closed" when longTitle => string.Concat(Enumerable.Repeat("0123456789-", 8)),
                            "closed" => "978-0-123456-78-9",
                            _ => null
                        },
                        Status = status,
                        IsbnCheckStatus = status is "outstanding_purchase" or "closed" ? "not_found" : null,
                        IsbnCheckLastErrorCode = longTitle ? "polaris_identifier_results_incomplete" : null,
                        LastPromoterResult = status == "outstanding_purchase" ? "No eligible Polaris BIB was found." : null,
                        Notes = status == "closed" && !longTitle
                            ? "Identifier number verification completed: only e-materials found matching identifier 978-0-123456-78-9; these cannot be placed on hold in Polaris."
                            : null,
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
            var tagIds = await setup.WorkflowTags.ToDictionaryAsync(tag => tag.Code, tag => tag.Id);
            foreach (var row in setup.TitleRequests.Local)
            {
                string[] codes = row.Barcode switch
                {
                    "LAYOUT-suggestion-0" => ["polaris_bib_found", "duplicate_suggestion"],
                    "LAYOUT-outstanding_purchase-0" => ["polaris_bib_not_found"],
                    "LAYOUT-hold_placed-0" => ["polaris_bib_found"],
                    "LAYOUT-hold_placed-1" => ["polaris_bib_found", "polaris_multiple_matches"],
                    "LAYOUT-hold_placed-2" => ["polaris_bib_not_found", "polaris_ematerials_only"],
                    _ => []
                };
                foreach (var code in codes)
                {
                    setup.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag { TitleRequestId = row.Id, WorkflowTagId = tagIds[code] });
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

        await using var settingsContext = await contexts.CreateDbContextAsync();
        var system = await settingsContext.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalPattern = system.LeapBibUrlPattern;
        try
        {
            system.LeapBibUrlPattern = "https://leap.example.test/bib/{{bibid}}";
            await settingsContext.SaveChangesAsync();
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "title-requests-layout.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(8, report.RootElement.GetProperty("viewportMatrix").GetArrayLength());
            Assert.AreEqual(16, report.RootElement.GetProperty("resizeOnly").GetArrayLength());
            Assert.AreEqual(2, report.RootElement.GetProperty("nativeZoom").GetArrayLength());
        }
        finally
        {
            system.LeapBibUrlPattern = originalPattern;
            await settingsContext.SaveChangesAsync();
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == library).ExecuteDeleteAsync();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == library)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        }
    }
}
