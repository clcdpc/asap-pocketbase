using System.Globalization;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task StaffSuggestionRecoveryBrowserKeepsPartialPickupStateAndRequiresRefresh()
    {
        const string barcode = "20000000001993";
        var title = $"Suggestion recovery browser {Guid.NewGuid():N}";
        var raceFormatCode = $"browser-race-{Guid.NewGuid():N}";
        var correctedFormatCode = $"browser-current-{Guid.NewGuid():N}";
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new ControllablePickupPatronProvider();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        long raceFormatId;
        long correctedFormatId;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var raceFormat = new MaterialFormat
            {
                OwnerOrganizationId = 2,
                Code = raceFormatCode,
                Label = "Temporary eligible format",
                SortOrder = 990,
                IsEnabled = true,
                MessageBehavior = "none",
                TitleMode = "required",
                AuthorMode = "optional",
                IdentifierMode = "optional",
                PublicationMode = "optional",
                CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
                UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            var correctedFormat = new MaterialFormat
            {
                OwnerOrganizationId = 2,
                Code = correctedFormatCode,
                Label = "Current eligible format",
                SortOrder = 991,
                IsEnabled = true,
                MessageBehavior = "none",
                TitleMode = "required",
                AuthorMode = "required",
                AuthorLabel = "Refreshed format author",
                IdentifierMode = "optional",
                PublicationMode = "optional",
                CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
                UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
            };
            setup.MaterialFormats.AddRange(raceFormat, correctedFormat);
            await setup.SaveChangesAsync();
            raceFormatId = raceFormat.Id;
            correctedFormatId = correctedFormat.Id;
        }

        provider.AfterUpdate = () =>
        {
            using var connection = new SqlConnection(databaseConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [asap].[MaterialFormat] SET [MessageBehavior] = N'message' WHERE [Id] = @id;";
            command.Parameters.AddWithValue("@id", raceFormatId);
            Assert.AreEqual(1, command.ExecuteNonQuery(), "The selected format becomes referral-only after pickup succeeds.");
        };

        await using var scopedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.AddSingleton<IPatronProvider>(provider);
        }));
        scopedFactory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = scopedFactory.CreateClient();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "browser", $"staff-suggestion-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "staff-suggestion-recovery.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                actor.Id.ToString(CultureInfo.InvariantCulture), actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                "2", barcode, raceFormatCode, correctedFormatCode, title);
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

            await using var verify = await contextFactory.CreateDbContextAsync();
            var created = await verify.TitleRequests.AsNoTracking()
                .SingleAsync(item => item.LibraryOrganizationId == 2 && item.Barcode == barcode && item.Title == title);
            Assert.AreEqual(correctedFormatId, created.MaterialFormatId);
            Assert.IsNull(created.Publication, "The explicit optional publication clear is persisted as null.");
            Assert.AreEqual(102, created.PreferredPickupBranchId);
            Assert.AreEqual(1, provider.UpdateCount, "The corrected request reuses the confirmed pickup change without sending another pickup update.");
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[TitleRequestEvent]
                WHERE [TitleRequestId] IN (SELECT [Id] FROM [asap].[TitleRequest] WHERE [Barcode] = @barcode);
                DELETE FROM [asap].[TitleRequest] WHERE [Barcode] = @barcode;
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [Barcode] = @barcode;
                DELETE FROM [asap].[FormatAutoClaimRule] WHERE [MaterialFormatId] IN (@raceFormatId, @correctedFormatId);
                DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [MaterialFormatId] IN (@raceFormatId, @correctedFormatId);
                DELETE FROM [asap].[MaterialFormat] WHERE [Id] IN (@raceFormatId, @correctedFormatId);
                """, ("@barcode", barcode), ("@raceFormatId", raceFormatId), ("@correctedFormatId", correctedFormatId));
        }
    }

    [TestMethod]
    [TestCategory(TestCategories.Browser)]
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
        var excluded = patron with { Barcode = barcode + "-ineligible", PatronId = 394102, PatronCodeId = 999 };
        provider.AddPatron(excluded, [new(pickup, "Live eligible pickup")], library);
        provider.AddPatron(patron with { Barcode = barcode + "-late-denied", PatronId = 394103, PreferredPickupBranchId = pickup },
            [new(pickup, "Live eligible pickup")], library);
        provider.SetPatronSearch("Excluded Patron", library, [excluded]);
        // Read-only readiness checks must not arm the final-acceptance race.
        var requestContext = new HttpContextAccessor();
        var sender = new PolicyMutatingReadinessEmailSender(library, replaceAllowedCodes: true,
            shouldMutate: () => requestContext.HttpContext?.Request.Path == "/api/asap/staff/suggestions" &&
                provider.Calls.Any(call => call.Operation == TestingPolarisOperation.Refresh && call.Key == barcode + "-late-denied"),
            configuredWhenNotMutating: true);
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
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId],[AllowAnyRegisteredCardLogin],[PatronCodeEligibilityEnabled],[PatronCodeEligibilityMessage],[UpdatedUtc])
            VALUES (@org,1,1,N'Please contact the library.',SYSUTCDATETIME());
            INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@org);
            INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId],[PatronCodeId]) VALUES (@org,1);
            """, ("@org", library), ("@home", home), ("@registration", registration), ("@pickup", pickup));
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(sender);
            services.RemoveAll<IHttpContextAccessor>();
            services.AddSingleton<IHttpContextAccessor>(requestContext);
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
                foreach (var status in new[] { "outstanding_purchase", "hold_placed", "closed" })
                {
                    setup.TitleRequests.AddRange(Enumerable.Range(0, status == "closed" ? 27 : 1).Select(index => new TitleRequest
                    {
                        LibraryOrganizationId = library, Barcode = $"status-browser-{status}-{index:00}",
                        Title = $"Status transition {status} {index:00}", MaterialFormatId = format.Id, Status = status,
                        CloseReason = status == "closed" ? "manual" : null,
                        CreatedUtc = timeProvider.GetUtcNow().UtcDateTime.AddHours(-1).AddMinutes(-index),
                        UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
                    }));
                }
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
            var zoomStates = report.RootElement.GetProperty("nativeZoom").EnumerateArray().ToArray();
            string[] expectedZoomStates = ["table", "closed-actions", "detail", "confirmation", "new-suggestion", "workflow-picker",
                "catalog-preview", "material-formats", "format-rules-collapsed", "format-rules-expanded", "patron-placeholder", "email-template-preview", "email-template-picker"];
            Assert.HasCount(expectedZoomStates.Length * 2, zoomStates);
            foreach (var width in new[] { 1280, 1440 })
            {
                var names = zoomStates.Where(state => state.GetProperty("physicalWidth").GetInt32() == width)
                    .Select(state => state.GetProperty("name").GetString()!).ToArray();
                CollectionAssert.AreEqual(expectedZoomStates, names, $"Every required native-zoom state must run at {width}px.");
            }
            Assert.HasCount(2, report.RootElement.GetProperty("listActions").EnumerateArray().ToArray());
            Assert.HasCount(10, report.RootElement.GetProperty("statusTransitions").GetProperty("transitions").EnumerateArray().ToArray());
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
            Assert.AreEqual(1, sender.ReadinessCalls);
            Assert.IsFalse(await verify.TitleRequests.AnyAsync(item => item.LibraryOrganizationId == library && item.Barcode == barcode + "-late-denied"));
            var quickCreateNotices = await verify.EmailOutbox.AsNoTracking()
                .Where(item => item.TitleRequestId == created.Id && item.MessageKind == "new_patron_request")
                .ToArrayAsync();
            Assert.IsFalse(await verify.EmailOutbox.AsNoTracking().AnyAsync(item =>
                item.TitleRequestId == created.Id && item.MessageKind == "confirmation"),
                "The quick-create opt-out remains specific to patron confirmation.");
            Assert.IsTrue(quickCreateNotices.Any(item => item.RecipientStaffUserId == claimantId &&
                item.Status == "suppressed" && item.SuppressionReason == "recipient_missing"),
                "The successful staff quick-create has its own missing-address suppression evidence, independent of patron confirmation.");
            var listActions = report.RootElement.GetProperty("listActions").EnumerateArray().ToArray();
            var purchaseKey = "staff-patron-action:purchase_approved:" + listActions.Single(item => item.GetProperty("status").GetString() == "outstanding_purchase").GetProperty("id").GetString() + ":";
            var rejectionKey = "rejection:" + listActions.Single(item => item.GetProperty("status").GetString() == "closed").GetProperty("id").GetString() + ":";
            var notifications = await verify.EmailOutbox.Where(item => item.OrganizationId == library &&
                item.BusinessKey != null &&
                (item.BusinessKey.StartsWith(purchaseKey) || item.BusinessKey.StartsWith(rejectionKey))).ToArrayAsync();
            Assert.IsTrue(notifications.Length is >= 1 and <= 2);
            foreach (var notification in notifications)
            {
                Assert.IsTrue(notification.BusinessKey?.StartsWith(purchaseKey, StringComparison.Ordinal) == true ||
                    notification.BusinessKey?.StartsWith(rejectionKey, StringComparison.Ordinal) == true,
                    "These workflow email intents must come only from the explicit list actions.");
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
                DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId]=@org); DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId]=@org;
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
