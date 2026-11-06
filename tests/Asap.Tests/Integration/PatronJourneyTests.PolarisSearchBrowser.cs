using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task PolarisSearchBrowserQueuesAuthoritativeBibsAndRecoversCombinedCopyExactlyOnce()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var copyOff = await SeedPolarisQueueAsync();
        await using var copyOn = await SeedPolarisQueueAsync("outstanding_purchase", true);
        await using var useOff = await SeedPolarisQueueAsync();
        await using var useOn = await SeedPolarisQueueAsync("outstanding_purchase", true);
        await using var stale = await SeedPolarisQueueAsync();
        await using var research = await SeedPolarisQueueAsync("closed");
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory.CreateClient();
        var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var provider = copyOff.Provider;
        provider.SetHolds(stale.Source.Barcode, 2, []);
        var rows = Enumerable.Range(0, 12).Select(index => new StaffBibSearchRow(9001 + index,
            $"Catalog result {index + 1}: a readable title that wraps at narrow widths", "Catalog author", "2026", "Book", $"CATALOG-{index}")
            { FormatIconUrl = "/jpl.png" }).ToArray();
        foreach (var row in rows)
        {
            provider.SetBib(row.BibId, 2, new(true, row.Title, row.Author, row.Publication, row.Format, row.Identifier)
                { FormatIconUrl = row.FormatIconUrl }, new(1, 3, 4, true, true));
        }
        provider.SetBib(9002, 2, new(true, rows[1].Title, Format: "Book"), new(0, 4, 4, true, false));
        provider.SetBib(9003, 2, new(true, rows[2].Title, Format: "Book"), new(0, 0, 0, false, false));
        provider.SetBib(9004, 2, new(true, rows[3].Title, Format: "Book"), new(1, 0, 1, false, false));
        provider.SetFailure(new(TestingPolarisOperation.BibHoldings, 2, NativeId: 9005),
            new PolarisOperationalException("fixture_outage", "Deterministic holdings outage"));
        provider.SetBibSearch("title", "Queue fixture", "", "", 2, new(rows, 2951));
        provider.SetBibSearch("title_author", "", "Queue fixture", "Staff author", 2, new(rows, 2951));
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "i439", $"polaris-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        var fixtures = new[] { copyOff, copyOn, useOff, useOn, stale, research };
        var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "polaris-search.cjs", origin, artifacts,
            new[] { actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail }
                .Concat(fixtures.Select(item => item.Source.Id.ToString(CultureInfo.InvariantCulture))).ToArray());
        Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
        Assert.AreEqual(4, report.RootElement.GetProperty("committed").GetArrayLength());
        Assert.IsTrue(report.RootElement.GetProperty("identicalRecovery").GetBoolean());
        Assert.IsTrue(report.RootElement.GetProperty("staleVersionRejected").GetBoolean());
        await using var verify = await copyOff.Contexts.CreateDbContextAsync();
        foreach (var fixture in new[] { copyOff, copyOn })
        {
            var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == fixture.Source.Id);
            var task = await verify.AdditionalCopyRequests.AsNoTracking().SingleAsync(item => item.SourceTitleRequestId == saved.Id);
            Assert.IsTrue(saved.AutoHold);
            Assert.AreEqual("pending_hold", saved.Status);
            Assert.AreEqual(9001, saved.BibId);
            Assert.AreEqual(saved.BibId, task.BibId);
            Assert.AreEqual(saved.LibraryOrganizationId, task.LibraryOrganizationId);
            Assert.AreEqual(fixture.Source.MaterialFormatId, saved.MaterialFormatId);
            Assert.AreEqual(fixture.Source.Publication, saved.Publication);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == saved.Id && item.EventType == "polaris_queue_completed"));
            Assert.AreEqual(fixture == copyOff ? 1 : 0, await verify.EmailOutbox.CountAsync(item => item.BusinessKey == $"additional-copy-reminder:{task.Id}"));
        }
        foreach (var fixture in new[] { useOff, useOn })
        {
            var saved = await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == fixture.Source.Id);
            Assert.AreEqual(fixture.Source.AutoHold, saved.AutoHold);
            Assert.AreEqual(fixture.Source.AutoHold ? "pending_hold" : "closed", saved.Status);
            Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == saved.Id));
        }
        Assert.AreEqual("suggestion", (await verify.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == stale.Source.Id)).Status);
        Assert.AreEqual(0, await verify.AdditionalCopyRequests.CountAsync(item => item.SourceTitleRequestId == stale.Source.Id));
        Assert.AreEqual(0, provider.CreateCommands.Count, "Queueing cannot directly place a hold.");
        Assert.IsTrue(provider.Calls.Where(item => item.Operation is TestingPolarisOperation.BibValidation or TestingPolarisOperation.BibHoldings)
            .All(item => item.OrganizationId == 2), "All-library UI must use the request's owning library.");
    }
}
