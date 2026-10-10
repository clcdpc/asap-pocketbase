using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow(3472)]
    [DataRow(3473)]
    public async Task ServiceFlowsKeepTheirEffectiveSelectedAndRequestLibraryContext(int organizationId)
    {
        var provider = new ServicingContextProvider(organizationId);
        var changedHomeOrganizationId = 500_000 + Random.Shared.Next(10_000, 99_999);
        var changedBranchOrganizationId = checked(changedHomeOrganizationId * 10 + 1);
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@org, N'Context Library', 2, 1, 1);
            IF @org <> 3472
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (3472, N'Patron Home Library', 2, 1, 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@branch, N'Context Branch', 3, 3472, 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@branch2, N'Context Branch Two', 3, 3472, 1),
                   (@branch3, N'Context Branch Three', 3, 3472, 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@changedHome, N'Changed Patron Home', 2, 1, 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (@changedBranch, N'Changed Registration Branch', 3, @changedHome, 1);
            INSERT INTO [asap].[WorkflowSettings] ([OrganizationId], [AllowAnyRegisteredCardLogin], [UpdatedUtc])
                VALUES (@org, 1, SYSUTCDATETIME());
            """, ("@org", organizationId), ("@branch", provider.RegisteredBranchId),
            ("@branch2", provider.RegisteredBranchId + 1), ("@branch3", provider.RegisteredBranchId + 2),
            ("@changedHome", changedHomeOrganizationId), ("@changedBranch", changedBranchOrganizationId));
        var requests = new List<long>();
        await using var scopedFactory = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services => {
            services.RemoveAll<IPatronProvider>();
            services.RemoveAll<IStaffPolarisProvider>();
            services.AddSingleton<IPatronProvider>(provider);
            services.AddSingleton<IStaffPolarisProvider>(provider);
        }));
        var contexts = scopedFactory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        try
        {
            using var patron = scopedFactory.CreateClient();
            using var login = await patron.PostAsJsonAsync("/api/asap/patron/login", new {
                barcode = provider.Barcode, pin = "1234", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            using var session = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            patron.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                session.RootElement.GetProperty("token").GetString());
            using var continued = await patron.GetAsync("/api/asap/patron/session");
            Assert.AreEqual(HttpStatusCode.OK, continued.StatusCode, await continued.Content.ReadAsStringAsync());

            using var staff = scopedFactory.CreateClient();
            var actor = await ReadConfiguredSuperAdminAsync();
            AddTestingStaffHeaders(staff, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            staff.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staff));
            using var searched = await staff.PostAsJsonAsync("/api/asap/staff/patron-lookup", new {
                query = "Context Patron", libraryOrgId = organizationId });
            Assert.AreEqual(HttpStatusCode.OK, searched.StatusCode, await searched.Content.ReadAsStringAsync());
            foreach (var input in new object[] {
                new { mode = "title", query = "Context Title", libraryOrgId = organizationId },
                new { mode = "bib", bibId = 9001, libraryOrgId = organizationId } })
            {
                using var result = await staff.PostAsJsonAsync("/api/asap/staff/bib-lookup", input);
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            }

            async Task<TitleRequest> AddRequest(string status, bool verified = true, int bibId = 9001)
            {
                await using var db = await contexts.CreateDbContextAsync();
                var row = new TitleRequest {
                    LibraryOrganizationId = organizationId, PatronOrganizationId = provider.RegisteredBranchId,
                    Barcode = provider.Barcode, Title = "Context " + Guid.NewGuid(), AutoHold = true,
                    MaterialFormatId = await db.MaterialFormats.Where(x => x.OwnerOrganizationId == 1 && x.Code == "book")
                        .Select(x => x.Id).SingleAsync(), Status = status, BibId = verified ? bibId : null,
                    BibIdStaffVerified = verified, Identifier = "FOUND", IsbnCheckStatus = verified ? "found" : "pending",
                    PreferredPickupBranchId = provider.CurrentPickup, PreferredPickupBranchName = "Context Branch",
                    CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime, UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime };
                db.TitleRequests.Add(row);
                await db.SaveChangesAsync();
                requests.Add(row.Id);
                return row;
            }

            var pending = await AddRequest("pending_hold");
            using var options = await staff.PostAsJsonAsync($"/api/asap/staff/title-requests/{pending.Id}/pickup-options", new { });
            Assert.AreEqual(HttpStatusCode.OK, options.StatusCode, await options.Content.ReadAsStringAsync());
            using var optionsBody = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
            using var updated = await staff.PostAsJsonAsync($"/api/asap/staff/title-requests/{pending.Id}/pickup-preference", new {
                version = optionsBody.RootElement.GetProperty("version").GetString(),
                preferredPickupBranchId = provider.RegisteredBranchId + 1,
                currentPreferredPickupBranchIdAtLoad = provider.CurrentPickup });
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            await using (var db = await contexts.CreateDbContextAsync())
            {
                pending = await db.TitleRequests.SingleAsync(x => x.Id == pending.Id);
            }
            provider.CurrentPickup = provider.RegisteredBranchId + 2;
            var placement = scopedFactory.Services.GetRequiredService<HoldPlacementService>();
            var placed = await placement.PlaceBackgroundAsync(pending.Id, pending.RowVersion, CancellationToken.None);
            Assert.AreEqual("updated", placed.Code);
            Assert.AreEqual("hold_placed", placed.FinalStatus);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var operation = await db.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == pending.Id);
                Assert.AreEqual(provider.RegisteredBranchId, operation.RequestingOrganizationIdSnapshot,
                    "The journal freezes the live native patron registration for the create and reply member route.");
                Assert.AreEqual(provider.RegisteredBranchId + 2, operation.PickupBranchIdSnapshot,
                    "A stale request display value must not override the live eligible pickup default.");
            }
            await PrepareSingleItemCycleAsync(QueueNames.FulfillmentTracking, organizationId, pending.Id);
            var workflow = scopedFactory.Services.GetRequiredService<WorkflowProcessingService>();
            Assert.AreEqual("completed", (await workflow.ProcessWorkflowAsync(organizationId, CancellationToken.None)).Code);

            var identifier = await AddRequest("suggestion", verified: false);
            await PrepareSingleItemCycleAsync(QueueNames.IdentifierProcessing, organizationId, identifier.Id);
            Assert.AreEqual("completed", (await workflow.ProcessIdentifierAsync(organizationId, CancellationToken.None)).Code);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                Assert.AreEqual(9001, (await db.TitleRequests.SingleAsync(x => x.Id == identifier.Id)).BibId);
            }

            // Reconciliation observes the same owning library without dispatching another write.
            provider.HoldExists = false;
            provider.AmbiguousCreate = true;
            provider.Barcode += "R";
            var recovering = await AddRequest("pending_hold");
            var ambiguous = await placement.PlaceBackgroundAsync(recovering.Id, recovering.RowVersion, CancellationToken.None);
            Assert.IsNotNull(ambiguous.OperationId, ambiguous.Code);
            provider.HoldExists = true;
            var recovered = await placement.RecoverBackgroundOperationAsync(ambiguous.OperationId.Value,
                organizationId, CancellationToken.None);
            Assert.AreEqual("hold_operator_required", recovered.Code);
            Assert.IsNull(recovered.FinalStatus);
            Assert.AreEqual(2, provider.CreateCount);
            Assert.AreEqual(1, provider.ReplyCount);
            await using (var db = await contexts.CreateDbContextAsync())
            {
                var evidence = await db.HoldPlacementOperations.SingleAsync(x => x.Id == ambiguous.OperationId.Value);
                Assert.AreEqual("operator_required", evidence.State);
                Assert.IsNull(evidence.PolarisHoldId, "A same-BIB hold is not proof of this attempted write.");
            }

            if (organizationId == 3472)
            {
                provider.AmbiguousCreate = false;
                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId + 1;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.AvailablePickupBranchIds = [provider.RegisteredBranchId, provider.RegisteredBranchId + 1,
                    provider.RegisteredBranchId + 2];
                provider.AfterHoldRead = () => provider.CurrentPickup = provider.RegisteredBranchId + 2;
                provider.ExpectedBibId = 9002;
                var pickupMovedAfterHoldRead = await AddRequest("pending_hold", bibId: 9002);
                var beforeRefreshRaceCreateCount = provider.CreateCount;
                var pickupMovedAfterHoldReadResult = await placement.PlaceBackgroundAsync(
                    pickupMovedAfterHoldRead.Id, pickupMovedAfterHoldRead.RowVersion, CancellationToken.None);
                Assert.AreEqual("updated", pickupMovedAfterHoldReadResult.Code);
                Assert.AreEqual(beforeRefreshRaceCreateCount + 1, provider.CreateCount);
                Assert.AreEqual(provider.RegisteredBranchId + 2, provider.LastCreateCommand!.PickupBranchId,
                    "The pre-marker refresh must use the live default that changed after the initial hold-list read.");
                await using (var verifyPickupRefreshRace = await contexts.CreateDbContextAsync())
                {
                    var operation = await verifyPickupRefreshRace.HoldPlacementOperations.SingleAsync(item =>
                        item.TitleRequestId == pickupMovedAfterHoldRead.Id);
                    Assert.AreEqual(provider.RegisteredBranchId + 2, operation.PickupBranchIdSnapshot);
                }
                provider.AfterHoldRead = null;

                await ExecuteNonQueryAsync("""
                    UPDATE [asap].[WorkflowSettings]
                    SET [AllowAnyRegisteredCardLogin] = 1, [PatronCodeEligibilityEnabled] = 1,
                        [UpdatedUtc] = SYSUTCDATETIME()
                    WHERE [OrganizationId] = @org;
                    DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @org;
                    IF NOT EXISTS (SELECT 1 FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @org)
                        INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (@org);
                    INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId])
                    VALUES (@org, 999);
                    """, ("@org", organizationId));

                // A changed registration inside the same home library still rechecks code eligibility.
                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId + 1;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId + 1;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.AvailablePickupBranchIds = [provider.RegisteredBranchId + 1];
                provider.ExpectedBibId = 9011;
                var sameHomeCodeBeforeIntent = await AddRequest("pending_hold", bibId: 9011);
                var sameHomeHoldReadsBefore = provider.HoldReadCount;
                int sameHomeCodeEventsBefore;
                int sameHomeCodeOutboxBefore;
                await using (var beforeSameHomeCode = await contexts.CreateDbContextAsync())
                {
                    sameHomeCodeEventsBefore = await beforeSameHomeCode.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == sameHomeCodeBeforeIntent.Id);
                    sameHomeCodeOutboxBefore = await beforeSameHomeCode.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith(
                            $"title-hold-placed:{sameHomeCodeBeforeIntent.Id}:"));
                }
                var sameHomeCreateBefore = provider.CreateCount;
                var sameHomeCodeBeforeIntentResult = await placement.PlaceBackgroundAsync(
                    sameHomeCodeBeforeIntent.Id, sameHomeCodeBeforeIntent.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", sameHomeCodeBeforeIntentResult.Code);
                Assert.IsNull(sameHomeCodeBeforeIntentResult.OperationId,
                    "A same-home registration change with a disallowed current code must not acquire a new intent.");
                Assert.AreEqual(sameHomeHoldReadsBefore, provider.HoldReadCount);
                Assert.AreEqual(sameHomeCreateBefore, provider.CreateCount);
                await using (var verifySameHomeCode = await contexts.CreateDbContextAsync())
                {
                    Assert.IsFalse(await verifySameHomeCode.HoldPlacementOperations.AnyAsync(item =>
                        item.TitleRequestId == sameHomeCodeBeforeIntent.Id));
                    Assert.AreEqual("pending_hold", (await verifySameHomeCode.TitleRequests.SingleAsync(item =>
                        item.Id == sameHomeCodeBeforeIntent.Id)).Status);
                    Assert.AreEqual(sameHomeCodeEventsBefore,
                        await verifySameHomeCode.TitleRequestEvents.CountAsync(item =>
                            item.TitleRequestId == sameHomeCodeBeforeIntent.Id));
                    Assert.AreEqual(sameHomeCodeOutboxBefore,
                        await verifySameHomeCode.EmailOutbox.CountAsync(item =>
                            item.BusinessKey != null && item.BusinessKey.StartsWith(
                                $"title-hold-placed:{sameHomeCodeBeforeIntent.Id}:")));
                }

                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.AvailablePickupBranchIds = [provider.RegisteredBranchId, provider.RegisteredBranchId + 1];
                provider.AfterHoldRead = () =>
                {
                    provider.CurrentRegisteredBranchId = provider.RegisteredBranchId + 1;
                    provider.CurrentHomeLibraryOrganizationId = 3472;
                    provider.CurrentPickup = provider.RegisteredBranchId + 1;
                };
                provider.ExpectedBibId = 9012;
                var sameHomeCodeBeforeMarker = await AddRequest("pending_hold", bibId: 9012);
                int sameHomeMarkerEventsBefore;
                int sameHomeMarkerOutboxBefore;
                await using (var beforeSameHomeMarker = await contexts.CreateDbContextAsync())
                {
                    sameHomeMarkerEventsBefore = await beforeSameHomeMarker.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == sameHomeCodeBeforeMarker.Id);
                    sameHomeMarkerOutboxBefore = await beforeSameHomeMarker.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith(
                            $"title-hold-placed:{sameHomeCodeBeforeMarker.Id}:"));
                }
                var sameHomeMarkerCreateBefore = provider.CreateCount;
                var sameHomeCodeBeforeMarkerResult = await placement.PlaceBackgroundAsync(
                    sameHomeCodeBeforeMarker.Id, sameHomeCodeBeforeMarker.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", sameHomeCodeBeforeMarkerResult.Code);
                Assert.AreEqual(sameHomeMarkerCreateBefore, provider.CreateCount,
                    "The pre-create gate must recheck a same-home registration change against current code policy.");
                await using (var verifySameHomeMarker = await contexts.CreateDbContextAsync())
                {
                    var request = await verifySameHomeMarker.TitleRequests.SingleAsync(item =>
                        item.Id == sameHomeCodeBeforeMarker.Id);
                    var operation = await verifySameHomeMarker.HoldPlacementOperations.SingleAsync(item =>
                        item.TitleRequestId == sameHomeCodeBeforeMarker.Id);
                    Assert.AreEqual("pending_hold", request.Status);
                    Assert.IsNull(operation.CreateStartedUtc);
                    Assert.AreEqual("patron_scope_changed", operation.LastErrorCode);
                    Assert.AreEqual(sameHomeMarkerEventsBefore,
                        await verifySameHomeMarker.TitleRequestEvents.CountAsync(item =>
                            item.TitleRequestId == sameHomeCodeBeforeMarker.Id));
                    Assert.AreEqual(sameHomeMarkerOutboxBefore,
                        await verifySameHomeMarker.EmailOutbox.CountAsync(item =>
                            item.BusinessKey != null && item.BusinessKey.StartsWith(
                                $"title-hold-placed:{sameHomeCodeBeforeMarker.Id}:")));
                }
                provider.AfterHoldRead = null;

                provider.CurrentPickup = changedBranchOrganizationId;
                provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.AvailablePickupBranchIds = [changedBranchOrganizationId];
                provider.ExpectedBibId = 9003;
                var movedCodeBeforeIntent = await AddRequest("pending_hold", bibId: 9003);
                int movedCodeEventsBefore;
                int movedCodeOutboxBefore;
                await using (var beforeMovedCode = await contexts.CreateDbContextAsync())
                {
                    movedCodeEventsBefore = await beforeMovedCode.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == movedCodeBeforeIntent.Id);
                    movedCodeOutboxBefore = await beforeMovedCode.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedCodeBeforeIntent.Id}:"));
                }
                var createBeforeMovedCodeIntent = provider.CreateCount;
                var movedCodeBeforeIntentResult = await placement.PlaceBackgroundAsync(
                    movedCodeBeforeIntent.Id, movedCodeBeforeIntent.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", movedCodeBeforeIntentResult.Code);
                Assert.IsNull(movedCodeBeforeIntentResult.OperationId,
                    "A moved patron with a disallowed current code must not acquire a new operation.");
                Assert.AreEqual(createBeforeMovedCodeIntent, provider.CreateCount);
                await using (var verifyMovedCodeIntent = await contexts.CreateDbContextAsync())
                {
                    Assert.IsFalse(await verifyMovedCodeIntent.HoldPlacementOperations.AnyAsync(item =>
                        item.TitleRequestId == movedCodeBeforeIntent.Id));
                    Assert.AreEqual("pending_hold", (await verifyMovedCodeIntent.TitleRequests.SingleAsync(item =>
                        item.Id == movedCodeBeforeIntent.Id)).Status);
                    Assert.AreEqual(movedCodeEventsBefore, await verifyMovedCodeIntent.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == movedCodeBeforeIntent.Id));
                    Assert.AreEqual(movedCodeOutboxBefore, await verifyMovedCodeIntent.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedCodeBeforeIntent.Id}:")));
                }

                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Absent;
                provider.AvailablePickupBranchIds = [changedBranchOrganizationId];
                provider.AfterHoldRead = () =>
                {
                    provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                    provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                    provider.CurrentPickup = changedBranchOrganizationId;
                    provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                };
                provider.ExpectedBibId = 9004;
                var movedCodeBeforeMarker = await AddRequest("pending_hold", bibId: 9004);
                int movedCodeMarkerEventsBefore;
                int movedCodeMarkerOutboxBefore;
                await using (var beforeMovedCodeMarker = await contexts.CreateDbContextAsync())
                {
                    movedCodeMarkerEventsBefore = await beforeMovedCodeMarker.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == movedCodeBeforeMarker.Id);
                    movedCodeMarkerOutboxBefore = await beforeMovedCodeMarker.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedCodeBeforeMarker.Id}:"));
                }
                var createBeforeMovedCodeMarker = provider.CreateCount;
                var movedCodeBeforeMarkerResult = await placement.PlaceBackgroundAsync(
                    movedCodeBeforeMarker.Id, movedCodeBeforeMarker.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", movedCodeBeforeMarkerResult.Code);
                Assert.AreEqual(createBeforeMovedCodeMarker, provider.CreateCount,
                    "The locked pre-create gate must reject a registration move that has become code-ineligible.");
                await using (var verifyMovedCodeMarker = await contexts.CreateDbContextAsync())
                {
                    var request = await verifyMovedCodeMarker.TitleRequests.SingleAsync(item =>
                        item.Id == movedCodeBeforeMarker.Id);
                    var operation = await verifyMovedCodeMarker.HoldPlacementOperations.SingleAsync(item =>
                        item.TitleRequestId == movedCodeBeforeMarker.Id);
                    Assert.AreEqual("pending_hold", request.Status);
                    Assert.IsNull(operation.CreateStartedUtc);
                    Assert.AreEqual("patron_scope_changed", operation.LastErrorCode);
                    Assert.AreEqual(movedCodeMarkerEventsBefore,
                        await verifyMovedCodeMarker.TitleRequestEvents.CountAsync(item =>
                            item.TitleRequestId == movedCodeBeforeMarker.Id));
                    Assert.AreEqual(movedCodeMarkerOutboxBefore,
                        await verifyMovedCodeMarker.EmailOutbox.CountAsync(item =>
                            item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedCodeBeforeMarker.Id}:")));
                }

                await ExecuteNonQueryAsync("""
                    DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @org;
                    INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId])
                    VALUES (@org, 1);
                    """, ("@org", organizationId));
                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Absent;
                provider.AvailablePickupBranchIds = [changedBranchOrganizationId];
                provider.AfterHoldRead = () =>
                {
                    provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                    provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                    provider.CurrentPickup = changedBranchOrganizationId;
                    provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                };
                provider.ExpectedBibId = 9005;
                var movedCodeAllowed = await AddRequest("pending_hold", bibId: 9005);
                var createBeforeAllowedMove = provider.CreateCount;
                var movedCodeAllowedResult = await placement.PlaceBackgroundAsync(
                    movedCodeAllowed.Id, movedCodeAllowed.RowVersion, CancellationToken.None);
                Assert.AreEqual("updated", movedCodeAllowedResult.Code,
                    "A moved registration remains eligible when the current effective patron-code set allows it.");
                Assert.AreEqual(createBeforeAllowedMove + 1, provider.CreateCount);
                await using (var verifyMovedCodeAllowed = await contexts.CreateDbContextAsync())
                {
                    var request = await verifyMovedCodeAllowed.TitleRequests.SingleAsync(item =>
                        item.Id == movedCodeAllowed.Id);
                    var operation = await verifyMovedCodeAllowed.HoldPlacementOperations.SingleAsync(item =>
                        item.TitleRequestId == movedCodeAllowed.Id);
                    Assert.AreEqual(organizationId, request.LibraryOrganizationId,
                        "A legitimate registration move does not transfer servicing-library ownership.");
                    Assert.AreEqual(changedBranchOrganizationId, operation.RequestingOrganizationIdSnapshot);
                    Assert.AreEqual(changedBranchOrganizationId, operation.PickupBranchIdSnapshot);
                }

                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Absent;
                provider.AvailablePickupBranchIds = [changedBranchOrganizationId];
                provider.CurrentPatronCodeId = null;
                provider.AfterHoldRead = () =>
                {
                    provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                    provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                    provider.CurrentPickup = changedBranchOrganizationId;
                    provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                };
                provider.ExpectedBibId = 9006;
                var movedCodeUnknown = await AddRequest("pending_hold", bibId: 9006);
                var createBeforeUnknownCodeMove = provider.CreateCount;
                var movedCodeUnknownResult = await placement.PlaceBackgroundAsync(
                    movedCodeUnknown.Id, movedCodeUnknown.RowVersion, CancellationToken.None);
                Assert.AreEqual("updated", movedCodeUnknownResult.Code,
                    "The existing policy permits an unknown code when the effective allowed set is nonempty.");
                Assert.AreEqual(createBeforeUnknownCodeMove + 1, provider.CreateCount);
                await using (var verifyMovedCodeUnknown = await contexts.CreateDbContextAsync())
                {
                    var operation = await verifyMovedCodeUnknown.HoldPlacementOperations.SingleAsync(item =>
                        item.TitleRequestId == movedCodeUnknown.Id);
                    Assert.AreEqual(changedBranchOrganizationId, operation.RequestingOrganizationIdSnapshot);
                }

                provider.AfterHoldRead = null;
                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.CurrentPatronCodeId = 1;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Absent;
                provider.AvailablePickupBranchIds = null;

                await ExecuteNonQueryAsync(
                    "UPDATE [asap].[WorkflowSettings] SET [AllowAnyRegisteredCardLogin] = 0, [UpdatedUtc] = SYSUTCDATETIME() WHERE [OrganizationId] = @org;",
                    ("@org", organizationId));
                provider.HoldExists = false;
                provider.AfterHoldRead = () =>
                {
                    provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                    provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                    provider.CurrentPickup = changedBranchOrganizationId;
                };
                provider.ExpectedBibId = 9007;
                var moved = await AddRequest("pending_hold", bibId: 9007);
                int movedEventCount;
                int movedOutboxCount;
                await using (var movedEventsBefore = await contexts.CreateDbContextAsync())
                {
                    movedEventCount = await movedEventsBefore.TitleRequestEvents.CountAsync(item => item.TitleRequestId == moved.Id);
                    movedOutboxCount = await movedEventsBefore.EmailOutbox.CountAsync();
                }

                var beforeCreateCount = provider.CreateCount;
                var movedResult = await placement.PlaceBackgroundAsync(moved.Id, moved.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", movedResult.Code);
                Assert.AreEqual(beforeCreateCount, provider.CreateCount,
                    "A registration move to a nonparticipating home must stop before the create marker and provider call.");
                await using var verifyMoved = await contexts.CreateDbContextAsync();
                Assert.AreEqual("pending_hold", (await verifyMoved.TitleRequests.SingleAsync(item => item.Id == moved.Id)).Status);
                Assert.AreEqual(movedEventCount, await verifyMoved.TitleRequestEvents.CountAsync(item => item.TitleRequestId == moved.Id));
                Assert.AreEqual(movedOutboxCount, await verifyMoved.EmailOutbox.CountAsync());
                var movedOperation = await verifyMoved.HoldPlacementOperations.SingleAsync(item => item.TitleRequestId == moved.Id);
                Assert.IsNull(movedOperation.CreateStartedUtc);
                Assert.IsNull(movedOperation.PickupBranchIdSnapshot);
                Assert.AreEqual("patron_scope_changed", movedOperation.LastErrorCode);
                provider.AfterHoldRead = null;

                provider.CurrentRegisteredBranchId = changedBranchOrganizationId;
                provider.CurrentHomeLibraryOrganizationId = changedHomeOrganizationId;
                provider.CurrentPickup = changedBranchOrganizationId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.AvailablePickupBranchIds = [changedBranchOrganizationId];
                provider.ExpectedBibId = 9008;
                var movedBeforeAcquire = await AddRequest("pending_hold", bibId: 9008);
                var readsBeforeMovedAcquire = provider.HoldReadCount;
                int movedBeforeAcquireEvents;
                int movedBeforeAcquireOutbox;
                await using (var beforeMovedAcquire = await contexts.CreateDbContextAsync())
                {
                    movedBeforeAcquireEvents = await beforeMovedAcquire.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == movedBeforeAcquire.Id);
                    movedBeforeAcquireOutbox = await beforeMovedAcquire.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedBeforeAcquire.Id}:"));
                }

                var movedBeforeAcquireResult = await placement.PlaceBackgroundAsync(
                    movedBeforeAcquire.Id, movedBeforeAcquire.RowVersion, CancellationToken.None);
                Assert.AreEqual("patron_scope_changed", movedBeforeAcquireResult.Code,
                    "A current registration move must fail the new-intent gate before an operation is acquired.");
                Assert.IsNull(movedBeforeAcquireResult.OperationId);
                Assert.AreEqual(readsBeforeMovedAcquire, provider.HoldReadCount,
                    "The provider hold list must not be read for a patron outside the current request scope.");
                await using (var verifyMovedAcquire = await contexts.CreateDbContextAsync())
                {
                    Assert.AreEqual("pending_hold", (await verifyMovedAcquire.TitleRequests.SingleAsync(item =>
                        item.Id == movedBeforeAcquire.Id)).Status);
                    Assert.AreEqual(movedBeforeAcquireEvents, await verifyMovedAcquire.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == movedBeforeAcquire.Id));
                    Assert.AreEqual(movedBeforeAcquireOutbox, await verifyMovedAcquire.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{movedBeforeAcquire.Id}:")));
                    Assert.IsFalse(await verifyMovedAcquire.HoldPlacementOperations.AnyAsync(item =>
                        item.TitleRequestId == movedBeforeAcquire.Id));
                }

                provider.CurrentRegisteredBranchId = provider.RegisteredBranchId;
                provider.CurrentHomeLibraryOrganizationId = 3472;
                provider.CurrentPickup = provider.RegisteredBranchId;
                provider.PickupPreferenceState = PatronPickupPreferenceState.Absent;
                provider.AvailablePickupBranchIds = [provider.RegisteredBranchId + 1];
                provider.ExpectedBibId = 9009;
                var unavailableFallback = await AddRequest("pending_hold", bibId: 9009);
                int fallbackEventsBefore;
                int fallbackOutboxBefore;
                await using (var beforeFallback = await contexts.CreateDbContextAsync())
                {
                    fallbackEventsBefore = await beforeFallback.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == unavailableFallback.Id);
                    fallbackOutboxBefore = await beforeFallback.EmailOutbox.CountAsync();
                }

                var fallbackCreateCount = provider.CreateCount;
                var fallbackResult = await placement.PlaceBackgroundAsync(
                    unavailableFallback.Id, unavailableFallback.RowVersion, CancellationToken.None);
                Assert.AreEqual("pickup_missing", fallbackResult.Code,
                    "An omitted preference cannot fall back when the registered branch is no longer eligible.");
                Assert.AreEqual(fallbackCreateCount, provider.CreateCount);
                await using var verifyFallback = await contexts.CreateDbContextAsync();
                Assert.AreEqual("pending_hold",
                    (await verifyFallback.TitleRequests.SingleAsync(item => item.Id == unavailableFallback.Id)).Status);
                Assert.AreEqual(fallbackEventsBefore, await verifyFallback.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == unavailableFallback.Id));
                Assert.AreEqual(fallbackOutboxBefore, await verifyFallback.EmailOutbox.CountAsync());
                var fallbackOperation = await verifyFallback.HoldPlacementOperations.SingleAsync(item =>
                    item.TitleRequestId == unavailableFallback.Id);
                Assert.IsNull(fallbackOperation.CreateStartedUtc);
                Assert.AreEqual("pickup_missing", fallbackOperation.LastErrorCode);

                provider.PickupPreferenceState = PatronPickupPreferenceState.Current;
                provider.CurrentPickup = changedBranchOrganizationId;
                provider.AvailablePickupBranchIds = [provider.RegisteredBranchId];
                provider.ExpectedBibId = 9010;
                var unavailableCurrent = await AddRequest("pending_hold", bibId: 9010);
                int currentEventsBefore;
                int currentOutboxBefore;
                await using (var beforeCurrent = await contexts.CreateDbContextAsync())
                {
                    currentEventsBefore = await beforeCurrent.TitleRequestEvents.CountAsync(item =>
                        item.TitleRequestId == unavailableCurrent.Id);
                    currentOutboxBefore = await beforeCurrent.EmailOutbox.CountAsync(item =>
                        item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{unavailableCurrent.Id}:"));
                }

                var invalidCurrentCreateCount = provider.CreateCount;
                var invalidCurrentResult = await placement.PlaceBackgroundAsync(
                    unavailableCurrent.Id, unavailableCurrent.RowVersion, CancellationToken.None);
                Assert.AreEqual("pickup_invalid", invalidCurrentResult.Code,
                    "An unavailable positive current default must not redirect to the eligible registered branch.");
                Assert.AreEqual(invalidCurrentCreateCount, provider.CreateCount);
                await using var verifyCurrent = await contexts.CreateDbContextAsync();
                Assert.AreEqual("pending_hold", (await verifyCurrent.TitleRequests.SingleAsync(item =>
                    item.Id == unavailableCurrent.Id)).Status);
                Assert.AreEqual(currentEventsBefore, await verifyCurrent.TitleRequestEvents.CountAsync(item =>
                    item.TitleRequestId == unavailableCurrent.Id));
                Assert.AreEqual(currentOutboxBefore, await verifyCurrent.EmailOutbox.CountAsync(item =>
                    item.BusinessKey != null && item.BusinessKey.StartsWith($"title-hold-placed:{unavailableCurrent.Id}:")));
                var currentOperation = await verifyCurrent.HoldPlacementOperations.SingleAsync(item =>
                    item.TitleRequestId == unavailableCurrent.Id);
                Assert.IsNull(currentOperation.CreateStartedUtc);
                Assert.IsNull(currentOperation.PickupBranchIdSnapshot);
                Assert.AreEqual("pickup_invalid", currentOperation.LastErrorCode);
            }
            CollectionAssert.IsSubsetOf(new[] { "refresh", "pickup-read", "pickup-write", "patron-search",
                "bib-search", "bib-detail", "holdings", "identifier", "holds", "checkouts", "create", "reply" },
                provider.Calls.ToArray());
        }
        finally
        {
            foreach (var request in requests)
            {
                await DeleteRequestAsync(request);
            }
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN
                    (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org);
                DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronSession] WHERE [EffectiveOrganizationId] = @org;
                DELETE FROM [asap].[QueueProgress] WHERE [ScopeOrganizationId] = @org;
                DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @org;
                DELETE FROM [asap].[PickupPreferenceOperation] WHERE [LibraryOrganizationId] = @org;
                DELETE FROM [asap].[Organization]
                WHERE [Id] IN (@org, 3472, @branch, @branch2, @branch3, @changedHome, @changedBranch);
                """, ("@org", organizationId), ("@branch", provider.RegisteredBranchId),
                ("@branch2", provider.RegisteredBranchId + 1), ("@branch3", provider.RegisteredBranchId + 2),
                ("@changedHome", changedHomeOrganizationId), ("@changedBranch", changedBranchOrganizationId));
        }
    }

    private sealed class ServicingContextProvider : IPatronProvider, IStaffPolarisProvider
    {
        private readonly int organizationId;

        public ServicingContextProvider(
            int organizationId,
            int registeredBranchId = 347201,
            int homeLibraryOrganizationId = 3472)
        {
            this.organizationId = organizationId;
            RegisteredBranchId = registeredBranchId;
            CurrentRegisteredBranchId = registeredBranchId;
            CurrentHomeLibraryOrganizationId = homeLibraryOrganizationId;
            CurrentPickup = registeredBranchId;
            Barcode = "200000000" + organizationId;
        }

        public string Barcode { get; set; }
        public int RegisteredBranchId { get; }
        public int CurrentRegisteredBranchId { get; set; }
        public int CurrentHomeLibraryOrganizationId { get; set; }
        public int? CurrentPatronCodeId { get; set; } = 1;
        public int ExpectedBibId { get; set; } = 9001;
        public PatronPickupPreferenceState? PickupPreferenceState { get; set; }
        public IReadOnlyList<int>? AvailablePickupBranchIds { get; set; }
        public int CurrentPickup { get; set; }
        public bool HoldExists { get; set; }
        public bool AmbiguousCreate { get; set; }
        public int CreateCount { get; private set; }
        public int ReplyCount { get; private set; }
        public int HoldReadCount { get; private set; }
        public HashSet<string> Calls { get; } = [];
        public Action? AfterHoldRead { get; set; }
        public HoldCreateCommand? LastCreateCommand { get; private set; }
        private readonly Guid conversation = Guid.NewGuid();
        private void Check(string operation, int context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(organizationId, context, operation);
            Calls.Add(operation);
        }
        private PatronSnapshot Patron(string barcode) => new(7001, barcode, "context@example.org", "Context", "Patron",
            CurrentPatronCodeId, "Adult", CurrentRegisteredBranchId, CurrentHomeLibraryOrganizationId, "Patron Home Library", CurrentPickup,
            PickupPreferenceState: PickupPreferenceState);
        public Task<PatronSnapshot> AuthenticateAsync(string barcode, string pin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.AreEqual(Barcode, barcode);
            Assert.AreEqual("1234", pin);
            return Task.FromResult(Patron(barcode));
        }
        public Task<PatronSnapshot> RefreshAsync(string barcode, int context, CancellationToken token)
        {
            Check("refresh", context, token);
            Assert.AreEqual(Barcode, barcode);
            return Task.FromResult(Patron(barcode));
        }
        public Task<int?> GetPatronIdAsync(string barcode, int context, CancellationToken token)
        {
            Check("patron-id", context, token);
            return Task.FromResult<int?>(7001);
        }
        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(PatronSnapshot patron, int context, CancellationToken token)
        {
            Check("pickup-read", context, token);
            var branchIds = AvailablePickupBranchIds ?? [
                CurrentRegisteredBranchId, CurrentRegisteredBranchId + 1, CurrentRegisteredBranchId + 2];
            return Task.FromResult<IReadOnlyList<PickupBranch>>(branchIds.Select(id => new PickupBranch(id, "Context Branch")).ToArray());
        }
        public Task UpdatePreferredPickupBranchAsync(string barcode, int pickup, int context, CancellationToken token)
        {
            Check("pickup-write", context, token);
            Assert.AreEqual(RegisteredBranchId + 1, pickup);
            CurrentPickup = pickup;
            return Task.CompletedTask;
        }
        public Task<IdentifierLookupResult> LookupIdentifierAsync(string identifier, int context, CancellationToken token)
        {
            Check("identifier", context, token);
            return Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.Found, 9001));
        }
        public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(string query, int context, CancellationToken token)
        {
            Check("patron-search", context, token);
            return Task.FromResult<IReadOnlyList<PatronSnapshot>>([Patron(Barcode)]);
        }
        public Task<StaffBibSearchResult> SearchBibsAsync(string mode, string query, string title, string author,
            int context, CancellationToken token)
        {
            Check("bib-search", context, token);
            return Task.FromResult(new StaffBibSearchResult([new(9001, "Context Title", null, null, "Book", "FOUND")], 1));
        }
        public Task<BibValidationResult> ValidateBibAsync(int bibId, int context, CancellationToken token)
        {
            Check("bib-detail", context, token);
            return Task.FromResult(new BibValidationResult(true, "Context Title"));
        }
        public Task<StaffBibHoldingsSummary> GetBibHoldingsAsync(int bibId, int context, CancellationToken token)
        {
            Check("holdings", context, token);
            return Task.FromResult(new StaffBibHoldingsSummary(1, 0, 1, true, true));
        }
        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(string barcode, int context, CancellationToken token)
        {
            Check("holds", context, token);
            HoldReadCount++;
            AfterHoldRead?.Invoke();
            return Task.FromResult<IReadOnlyList<PolarisHoldSnapshot>>(HoldExists
                ? [new(8123, 9001, 1, "Active", CurrentPickup, Barcode)] : []);
        }
        public Task<IReadOnlyList<PolarisCheckoutSnapshot>> GetPatronCheckoutsAsync(string barcode, int context, CancellationToken token)
        {
            Check("checkouts", context, token);
            return Task.FromResult<IReadOnlyList<PolarisCheckoutSnapshot>>([]);
        }
        public Task<HoldProviderResult> CreateHoldAsync(HoldCreateCommand command, CancellationToken token)
        {
            Check("create", organizationId, token);
            CreateCount++;
            LastCreateCommand = command;
            Assert.AreEqual(CurrentRegisteredBranchId, command.RequestingOrganizationId);
            Assert.AreEqual(CurrentPickup, command.PickupBranchId);
            Assert.AreEqual(7001, command.PatronId);
            Assert.AreEqual(ExpectedBibId, command.BibId);
            Assert.AreEqual(99, command.WorkstationId);
            Assert.AreEqual(42, command.PolarisUserId);
            return Task.FromResult(AmbiguousCreate
                ? new HoldProviderResult(HoldProviderOutcome.Ambiguous, null, null, null, null, null, null, "test_ambiguous")
                : new HoldProviderResult(HoldProviderOutcome.ReplyRequired, conversation, null, "group", "qualifier", 3, 5, "reply_required"));
        }
        public Task<HoldProviderResult> ReplyToHoldAsync(HoldReplyCommand command, CancellationToken token)
        {
            Check("reply", organizationId, token);
            ReplyCount++;
            Assert.AreEqual(CurrentRegisteredBranchId, command.RequestingOrganizationId);
            Assert.AreEqual(conversation, command.RequestGuid);
            HoldExists = true;
            return Task.FromResult(new HoldProviderResult(HoldProviderOutcome.FinalSuccess,
                conversation, 8123, "group", "qualifier", 2, 1, "success"));
        }
    }
}
