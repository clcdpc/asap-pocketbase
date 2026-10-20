using System.Globalization;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffQueueAndDetailQuickCreateBrowserUsesFreshContextAndExplicitAllScope()
    {
        const int library = 39401;
        const int home = 39402;
        const int registration = 394021;
        const int pickup = 394031;
        const string barcode = "workflow-live-patron";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new DeterministicTestingPatronProvider();
        var patron = new PatronSnapshot(394101, barcode, "fresh-browser@example.org", "Fresh", "Browser Patron", 1, "Adult",
            registration, home, "Recorded home library", null);
        provider.AddPatron(patron, [new(pickup, "Live eligible pickup")], library);
        provider.AllowPickupUpdate(barcode, library, pickup);
        provider.SetIdentifierResult("BROWSER-MATCH", library, new(IdentifierLookupOutcome.DefinitiveNotFound));
        provider.SetBibSearch("title", "Table browser record 00", "", "", library,
            new([new(394301, "Stale Title lookup result must never replace Author results", "Catalog author", null, "book", null)], 1));
        provider.SetBibSearch("author", "Table browser author 00", "", "", library,
            new([new(394302, "Current Author lookup result retained", "Table browser author 00", null, "book", null)], 1));
        for (var index = 0; index < 2; index++)
        {
            provider.AddPatron(new PatronSnapshot(394200 + index, $"table-browser-{index:00}", null, "Table", "Browser patron", 1, "Adult",
                registration, home, "Recorded home library", null), [new(pickup, "Live eligible pickup")], library);
        }
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@org,N'Browser servicing library with a long readable name that wraps across several lines in the native zoom request context',2,1,1),(@home,N'Recorded home library',2,1,1),
                (@registration,N'Fresh registered branch',3,@home,0),(@pickup,N'Live eligible pickup',3,@home,0);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId],[AllowAnyRegisteredCardLogin],[UpdatedUtc]) VALUES (@org,1,SYSUTCDATETIME());
            """, ("@org", library), ("@home", home), ("@registration", registration), ("@pickup", pickup));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        scopedFactory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = scopedFactory.CreateClient();
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long sourceId = 0;
        long claimantId = 0;
        var title = "Quick Create Browser " + Guid.NewGuid().ToString("N");
        try
        {
            await using (var setup = await contexts.CreateDbContextAsync())
            {
                var format = await setup.MaterialFormats.SingleAsync(item => item.OwnerOrganizationId == 1 && item.Code == "book");
                var claimant = new StaffUser { OrganizationId = library, Role = "staff", IsActive = true,
                    EntraTenantId = actor.EntraTenantId, UserPrincipalName = "workflow-claimant@example.org",
                    NormalizedUserPrincipalName = "WORKFLOW-CLAIMANT@EXAMPLE.ORG", DisplayName = "Browser claimant" };
                setup.StaffUsers.Add(claimant);
                var source = new TitleRequest { LibraryOrganizationId = library, Barcode = barcode, PatronIdSnapshot = 394199,
                    PatronOrganizationId = registration, NameFirst = new string('A', 80), NameLast = new string('B', 80), Email = new string('e', 180) + "@example.org",
                    PreferredPickupBranchId = 101, PreferredPickupBranchName = "Historical ASAP pickup with a long branch name retained for history and wrapping at native zoom", Title = "Quick-create source",
                    Author = "Old author", Identifier = "BROWSER-MATCH", MaterialFormatId = format.Id, Status = "suggestion",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                setup.TitleRequests.Add(source);
                await setup.SaveChangesAsync();
                sourceId = source.Id; claimantId = claimant.Id;
                setup.TitleRequests.AddRange(Enumerable.Range(0, 55).Select(index => new TitleRequest
                {
                    LibraryOrganizationId = library, Barcode = $"table-browser-{index:00}",
                    NameFirst = "A long patron name", NameLast = $"for readable request rows {index:00}",
                    Title = $"Table browser record {index:00}", Author = $"Table browser author {index:00}",
                    Identifier = $"TABLE-BROWSER-{index:00}", MaterialFormatId = format.Id,
                    Status = "suggestion", AutoHold = true,
                    CreatedUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-index - 1),
                    UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                }));
                setup.TitleRequestEvents.AddRange(Enumerable.Range(0, 80).Select(index => new TitleRequestEvent
                {
                    TitleRequestId = source.Id, EventType = "legacy", ActorType = "system",
                    Message = $"Deterministic long activity entry {index + 1}: retained history and context.",
                    CreatedUtc = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(index)
                }));
                setup.FormatAutoClaimRules.Add(new FormatAutoClaimRule { LibraryOrganizationId = library,
                    MaterialFormatId = format.Id, StaffUserId = claimant.Id, IsActive = true,
                    CreatedUtc = timeProvider.GetUtcNow().UtcDateTime });
                await setup.SaveChangesAsync();
            }
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"staff-workflow-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "staff-workflow-completion.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                sourceId.ToString(CultureInfo.InvariantCulture), library.ToString(CultureInfo.InvariantCulture), barcode,
                pickup.ToString(CultureInfo.InvariantCulture), title);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.HasCount(8, report.RootElement.GetProperty("nativeZoom").EnumerateArray().ToArray());
            Assert.HasCount(2, report.RootElement.GetProperty("listActions").EnumerateArray().ToArray());
            foreach (var action in report.RootElement.GetProperty("listActions").EnumerateArray())
            {
                var id = long.Parse(action.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
                await using var actioned = await contexts.CreateDbContextAsync();
                var saved = await actioned.TitleRequests.SingleAsync(item => item.Id == id);
                Assert.AreEqual(action.GetProperty("status").GetString(), saved.Status);
                Assert.IsTrue(await actioned.TitleRequestEvents.AnyAsync(item => item.TitleRequestId == id && item.StaffUserId == actor.Id));
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var created = await verify.TitleRequests.SingleAsync(item => item.LibraryOrganizationId == library && item.Title == title);
            Assert.AreEqual(patron.PatronId, created.PatronIdSnapshot);
            Assert.AreEqual(patron.Email, created.Email);
            Assert.AreEqual(pickup, created.PreferredPickupBranchId);
            Assert.AreEqual(registration, created.PatronOrganizationId);
            Assert.AreEqual(library, created.StaffLibraryOrganizationIdCreatedBy);
            Assert.AreEqual(claimantId, created.ClaimedByStaffUserId);
            Assert.AreEqual("automatic_format_rule", created.ClaimType);
            Assert.AreEqual("not_found", created.IsbnCheckStatus);
            Assert.IsTrue(await (from link in verify.TitleRequestWorkflowTags join tag in verify.WorkflowTags on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == created.Id && tag.Code == "duplicate_suggestion" select link).AnyAsync());
            Assert.AreEqual(1, provider.Calls.Count(call => call.Operation == TestingPolarisOperation.PickupUpdate));
            var listActions = report.RootElement.GetProperty("listActions").EnumerateArray().ToArray();
            var purchaseKey = "staff-patron-action:purchase_approved:" + listActions.Single(item => item.GetProperty("status").GetString() == "outstanding_purchase").GetProperty("id").GetString() + ":";
            var rejectionKey = "rejection:" + listActions.Single(item => item.GetProperty("status").GetString() == "closed").GetProperty("id").GetString() + ":";
            var notifications = await verify.EmailOutbox.Where(item => item.OrganizationId == library).ToArrayAsync();
            Assert.IsTrue(notifications.Length is >= 1 and <= 2);
            foreach (var notification in notifications)
            {
                Assert.IsTrue(notification.BusinessKey?.StartsWith(purchaseKey, StringComparison.Ordinal) == true ||
                    notification.BusinessKey?.StartsWith(rejectionKey, StringComparison.Ordinal) == true,
                    "Only the explicit list actions may record email intents; quick creation requested none.");
                Assert.AreEqual("suppressed", notification.Status, "The list patrons have no email address; no delivery is attempted.");
            }
            Assert.IsTrue(provider.Calls.Where(call => call.Operation == TestingPolarisOperation.Refresh).All(call => call.OrganizationId == library));
        }
        finally
        {
            await using var cleanup = await contexts.CreateDbContextAsync();
            foreach (var id in await cleanup.TitleRequests.Where(item => item.LibraryOrganizationId == library).Select(item => item.Id).ToArrayAsync())
            {
                await DeleteRequestAsync(id);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId]=@org);
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId]=@org;
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId]=@org;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId]=@org;
                DELETE FROM [asap].[StaffUser] WHERE [Id]=@claimant;
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@registration,@pickup);
                """, ("@org", library), ("@claimant", claimantId), ("@registration", registration), ("@pickup", pickup));
            await CleanupScalarSettingsTestDataAsync(library, "", "");
            await ExecuteNonQueryAsync("DELETE FROM [asap].[Organization] WHERE [Id]=@home;", ("@home", home));
        }
    }
}
