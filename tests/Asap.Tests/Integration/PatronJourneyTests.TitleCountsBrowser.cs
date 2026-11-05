using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task TitleTabCountsBrowserUsesCompleteAuthorizedSqlDataAndRefreshesAfterMutation()
    {
        const int library = 44201;
        const int otherLibrary = 44202;
        const int branch = 442011;
        string[] stages = ["suggestion", "outstanding_purchase", "pending_hold", "hold_placed", "closed"];
        int[] libraryCounts = [28, 2, 0, 1, 3];
        int[] otherCounts = [1, 0, 4, 0, 1];
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@library,N'Tab count library',2,1,1),(@other,N'Other tab count library',2,1,1),
                (@branch,N'Tab count branch',3,@library,1);
            """, ("@library", library), ("@other", otherLibrary), ("@branch", branch));
        var ordinaryRow = await CreateCorrectiveStaffAsync(actor, StaffRole.Staff, library);
        var ordinary = await ReadCorrectiveStaffAsync(ordinaryRow);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long mutationId;
        int[] allCounts;
        await using (var setup = await contexts.CreateDbContextAsync())
        {
            var format = await setup.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
            foreach (var (organization, totals) in new[] { (library, libraryCounts), (otherLibrary, otherCounts) })
            {
                for (var stage = 0; stage < stages.Length; stage++)
                {
                    setup.TitleRequests.AddRange(Enumerable.Range(0, totals[stage]).Select(index => new TitleRequest
                    {
                        LibraryOrganizationId = organization, Barcode = $"counts-{organization}-{stage}-{index}",
                        Title = $"Tab counts {organization} {stages[stage]} {index:00}",
                        MaterialFormatId = format.Id, Status = stages[stage], AutoHold = false,
                        BibId = stages[stage] is "pending_hold" or "hold_placed" ? 442900 + index : null,
                        BibIdStaffVerified = stages[stage] is "pending_hold" or "hold_placed",
                        CloseReason = stages[stage] == "closed" ? "manual" : null,
                        CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-index),
                        UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                    }));
                }
            }
            await setup.SaveChangesAsync();
            var mutation = await setup.TitleRequests.Where(item => item.LibraryOrganizationId == library && item.Status == "suggestion")
                .OrderBy(item => item.Id).FirstAsync();
            mutation.PatronIdSnapshot = 442101;
            mutation.PatronOrganizationId = branch;
            mutation.NameFirst = "Count";
            mutation.NameLast = "Patron";
            mutation.PreferredPickupBranchId = branch;
            mutation.PreferredPickupBranchName = "Tab count branch";
            await setup.SaveChangesAsync();
            mutationId = mutation.Id;
            var populations = await setup.TitleRequests.GroupBy(item => item.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync();
            allCounts = stages.Select(stage => populations.SingleOrDefault(item => item.Status == stage)?.Count ?? 0).ToArray();
        }
        var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        provider.AddPatron(new PatronSnapshot(442101, $"counts-{library}-0-0", null, "Count", "Patron",
            1, "Adult", branch, library, "Tab count library", branch), [new(branch, "Tab count branch")], library);
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "i439", "i442", $"title-counts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "fixture.json"), JsonSerializer.Serialize(new
        {
            library = library.ToString(CultureInfo.InvariantCulture), otherLibrary = otherLibrary.ToString(CultureInfo.InvariantCulture),
            libraryCounts, otherCounts, allCounts, mutationId = mutationId.ToString(CultureInfo.InvariantCulture),
            super = new { id = actor.Id.ToString(CultureInfo.InvariantCulture), tenantId = actor.EntraTenantId.ToString(), email = actor.AuthenticationEmail },
            ordinary = new { id = ordinary.Id.ToString(CultureInfo.InvariantCulture), tenantId = ordinary.EntraTenantId.ToString(), email = ordinary.AuthenticationEmail }
        }));
        try
        {
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "title-counts.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.IsTrue(report.RootElement.GetProperty("mutation").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("ordinaryScope").GetBoolean());
            Assert.AreEqual(2, report.RootElement.GetProperty("nativeZoom").GetArrayLength());
            await using var verify = await contexts.CreateDbContextAsync();
            Assert.AreEqual("outstanding_purchase", (await verify.TitleRequests.SingleAsync(item => item.Id == mutationId)).Status);
            Assert.AreEqual(1, await verify.TitleRequestEvents.CountAsync(item => item.TitleRequestId == mutationId && item.EventType == "status_changed"));
            Assert.AreEqual(1, provider.Calls.Count(item => item.Operation == TestingPolarisOperation.Refresh && item.OrganizationId == library));
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(ordinary.Id);
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.TitleRequestEvents.Where(item => item.TitleRequestId == mutationId).ExecuteDeleteAsync();
            await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library || item.LibraryOrganizationId == otherLibrary).ExecuteDeleteAsync();
            await cleanup.Organizations.Where(item => item.Id == library || item.Id == otherLibrary || item.Id == branch)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
        }
    }
}
