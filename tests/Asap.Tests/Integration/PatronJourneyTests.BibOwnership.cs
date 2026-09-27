using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffSelectedBibSurvivesIdentifierReconciliationOutcomesIncludingIssnNoMatch()
    {
        var actor = await GetOwnershipTestActorAsync();
        var mutations = factory!.Services.GetRequiredService<TitleRequestMutationService>();
        var requests = new List<long>();
        var cases = new (string Scenario, string Identifier, IdentifierLookupResult Result)[]
        {
            ("found", "ownership-found", new IdentifierLookupResult(
                IdentifierLookupOutcome.Found,
                "456",
                CatalogTitle: "Different catalog title",
                CatalogAuthor: "Different catalog author")),
            ("not-found", "ownership-not-found", new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound)),
            ("transient", "ownership-transient", new IdentifierLookupResult(
                IdentifierLookupOutcome.TransientFailure,
                ErrorCode: "testing_transient_failure")),
            ("operational", "ownership-operational", new IdentifierLookupResult(
                IdentifierLookupOutcome.OperationalFailure,
                ErrorCode: "testing_operational_failure")),
            ("issn-background-not-found", "2049-3630",
                new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound))
        };

        try
        {
            foreach (var (scenario, identifier, lookupResult) in cases)
            {
                var seeded = await SeedBibOwnershipRequestAsync(
                    $"previous-{scenario}",
                    null,
                    staffVerified: false);
                requests.Add(seeded.Id);
                using var selection = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    identifier,
                    bibid = "123",
                    staffSelectedBibId = "123"
                }));
                var input = new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(seeded.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = selection.RootElement.GetProperty("identifier"),
                    Bibid = selection.RootElement.GetProperty("bibid"),
                    StaffSelectedBibId = selection.RootElement.GetProperty("staffSelectedBibId")
                };

                var mutation = await mutations.ActionAsync(actor, seeded.Id, input, CancellationToken.None);
                Assert.AreEqual("updated", mutation.Code);

                var afterSelection = await ReadBibOwnershipRequestAsync(seeded.Id);
                Assert.AreEqual(identifier, afterSelection.Identifier,
                    "The explicitly selected BIB and the edited identifier must persist together.");
                Assert.AreEqual("123", afterSelection.BibId);
                Assert.IsTrue(afterSelection.BibIdStaffVerified);
                Assert.AreEqual("pending", afterSelection.IsbnCheckStatus);

                var lookupService = CreateBibOwnershipSuggestionService(lookupResult);
                var outcome = await lookupService.ProcessIdentifierLookupAsync(
                    seeded.Id,
                    identifier,
                    2,
                    afterSelection.RowVersion,
                    CancellationToken.None);

                Assert.AreEqual(lookupResult.Outcome, outcome, scenario);
                var afterLookup = await ReadBibOwnershipRequestAsync(seeded.Id);
                Assert.AreEqual("123", afterLookup.BibId, $"The {scenario} lookup replaced the staff-selected BIB.");
                Assert.IsTrue(afterLookup.BibIdStaffVerified);
                Assert.AreEqual("Staff entered title", afterLookup.Title);
                Assert.AreEqual("Staff entered author", afterLookup.Author);
            }
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync(requests);
        }
    }

    [TestMethod]
    public async Task IdentifierEditClearsAutomationBibAndIncidentalBibDoesNotGainStaffAuthority()
    {
        var actor = await GetOwnershipTestActorAsync();
        var mutations = factory!.Services.GetRequiredService<TitleRequestMutationService>();
        var requests = new List<long>();

        try
        {
            var changedIdentifier = await SeedBibOwnershipRequestAsync(
                "old-identifier", "456", staffVerified: false, isbnCheckStatus: "found");
            requests.Add(changedIdentifier.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"new-identifier","bibid":"456"}"""))
            {
                var result = await mutations.ActionAsync(actor, changedIdentifier.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(changedIdentifier.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterIdentifierEdit = await ReadBibOwnershipRequestAsync(changedIdentifier.Id);
            Assert.AreEqual("new-identifier", afterIdentifierEdit.Identifier);
            Assert.IsNull(afterIdentifierEdit.BibId);
            Assert.IsFalse(afterIdentifierEdit.BibIdStaffVerified);
            Assert.AreEqual("pending", afterIdentifierEdit.IsbnCheckStatus);

            var incidentalBib = await SeedBibOwnershipRequestAsync(
                "unchanged-identifier", "789", staffVerified: false, isbnCheckStatus: "found");
            requests.Add(incidentalBib.Id);
            using (var edit = JsonDocument.Parse("""{"title":"Edited title","identifier":"unchanged-identifier","bibid":"789"}"""))
            {
                var result = await mutations.ActionAsync(actor, incidentalBib.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(incidentalBib.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Title = edit.RootElement.GetProperty("title").GetString(),
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterIncidentalEdit = await ReadBibOwnershipRequestAsync(incidentalBib.Id);
            Assert.AreEqual("789", afterIncidentalEdit.BibId);
            Assert.IsFalse(afterIncidentalEdit.BibIdStaffVerified,
                "An unchanged BIB carried by an ordinary edit must not gain staff authority.");

            var automationBibPendingTransition = await SeedBibOwnershipRequestAsync(
                "pending-old-identifier", "654", staffVerified: false, isbnCheckStatus: "found");
            requests.Add(automationBibPendingTransition.Id);
            using (var action = JsonDocument.Parse("""{"identifier":"pending-new-identifier","bibid":"654"}"""))
            {
                var result = await mutations.ActionAsync(actor, automationBibPendingTransition.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(automationBibPendingTransition.RowVersion),
                    Action = "catalogFound",
                    Status = "pending_hold",
                    Identifier = action.RootElement.GetProperty("identifier"),
                    Bibid = action.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("bib_required", result.Code,
                    "A transition must not pass using an automation BIB that the identifier edit will clear.");
            }

            var afterBlockedPendingTransition = await ReadBibOwnershipRequestAsync(automationBibPendingTransition.Id);
            Assert.AreEqual("pending-old-identifier", afterBlockedPendingTransition.Identifier);
            Assert.AreEqual("654", afterBlockedPendingTransition.BibId);
            Assert.IsFalse(afterBlockedPendingTransition.BibIdStaffVerified);
            Assert.AreEqual("suggestion", afterBlockedPendingTransition.Status);

            var explicitlySelectedExistingBib = await SeedBibOwnershipRequestAsync(
                "selected-existing", "321", staffVerified: false, isbnCheckStatus: "found");
            requests.Add(explicitlySelectedExistingBib.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"selected-existing","bibid":"321","staffSelectedBibId":"321"}"""))
            {
                var result = await mutations.ActionAsync(actor, explicitlySelectedExistingBib.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(explicitlySelectedExistingBib.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid"),
                    StaffSelectedBibId = edit.RootElement.GetProperty("staffSelectedBibId")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterExplicitSelection = await ReadBibOwnershipRequestAsync(explicitlySelectedExistingBib.Id);
            Assert.AreEqual("321", afterExplicitSelection.BibId);
            Assert.IsTrue(afterExplicitSelection.BibIdStaffVerified,
                "An explicit same-BIB selection gains authority only after server validation.");
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync(requests);
        }
    }

    [TestMethod]
    public async Task IdentifierEditInvalidatesStaffBibUnlessSameMutationExplicitlyRevalidatesIt()
    {
        var actor = await GetOwnershipTestActorAsync();
        var mutations = factory!.Services.GetRequiredService<TitleRequestMutationService>();
        var requests = new List<long>();
        var checkedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-20);

        try
        {
            var incidental = await SeedBibOwnershipRequestAsync(
                "identifier-A",
                "123",
                staffVerified: true,
                isbnCheckStatus: "pending",
                retryCount: 4,
                checkResult: "Old identifier result.",
                lastErrorCode: "provider_timeout",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(incidental.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-B","bibid":"123"}"""))
            {
                var result = await mutations.ActionAsync(actor, incidental.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(incidental.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterIncidental = await ReadBibOwnershipRequestAsync(incidental.Id);
            Assert.AreEqual("identifier-B", afterIncidental.Identifier);
            Assert.IsNull(afterIncidental.BibId, "The unchanged payload BIB must not cross the identifier invalidation boundary.");
            Assert.IsFalse(afterIncidental.BibIdStaffVerified);
            Assert.AreEqual("pending", afterIncidental.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterIncidental);

            var clearedIdentifier = await SeedBibOwnershipRequestAsync(
                "identifier-to-clear",
                "123",
                staffVerified: true,
                isbnCheckStatus: "found",
                checkResult: "Old identifier result.",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(clearedIdentifier.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":null,"bibid":"123"}"""))
            {
                var result = await mutations.ActionAsync(actor, clearedIdentifier.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(clearedIdentifier.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterClear = await ReadBibOwnershipRequestAsync(clearedIdentifier.Id);
            Assert.IsNull(afterClear.Identifier);
            Assert.IsNull(afterClear.BibId);
            Assert.IsFalse(afterClear.BibIdStaffVerified);
            Assert.AreEqual("skipped_no_isbn", afterClear.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterClear);

            var explicitlyReselected = await SeedBibOwnershipRequestAsync(
                "identifier-A-same-bib",
                "123",
                staffVerified: true,
                isbnCheckStatus: "found",
                checkResult: "Old identifier result.",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(explicitlyReselected.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-B-same-bib","bibid":"123","staffSelectedBibId":"123"}"""))
            {
                var result = await mutations.ActionAsync(actor, explicitlyReselected.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(explicitlyReselected.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid"),
                    StaffSelectedBibId = edit.RootElement.GetProperty("staffSelectedBibId")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterReselection = await ReadBibOwnershipRequestAsync(explicitlyReselected.Id);
            Assert.AreEqual("123", afterReselection.BibId);
            Assert.IsTrue(afterReselection.BibIdStaffVerified);
            Assert.AreEqual("pending", afterReselection.IsbnCheckStatus,
                "Validating a manual BIB does not create an identifier-found result.");
            AssertIdentifierEvidenceWasCleared(afterReselection);

            var explicitlyChanged = await SeedBibOwnershipRequestAsync(
                "identifier-A-changed-bib",
                "123",
                staffVerified: true,
                isbnCheckStatus: "found",
                checkResult: "Old identifier result.",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(explicitlyChanged.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-B-changed-bib","bibid":"456"}"""))
            {
                var result = await mutations.ActionAsync(actor, explicitlyChanged.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(explicitlyChanged.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterChangedBib = await ReadBibOwnershipRequestAsync(explicitlyChanged.Id);
            Assert.AreEqual("456", afterChangedBib.BibId);
            Assert.IsTrue(afterChangedBib.BibIdStaffVerified);
            Assert.AreEqual("pending", afterChangedBib.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterChangedBib);
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync(requests);
        }
    }

    [TestMethod]
    public async Task ExplicitBibEditsInvalidateIdentifierEvidenceAndRejectedMutationIsAtomic()
    {
        var actor = await GetOwnershipTestActorAsync();
        var mutations = factory!.Services.GetRequiredService<TitleRequestMutationService>();
        var requests = new List<long>();
        var checkedUtc = timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-20);

        try
        {
            var cleared = await SeedBibOwnershipRequestAsync(
                "identifier-found-clear",
                "123",
                staffVerified: false,
                isbnCheckStatus: "found",
                checkResult: "Identifier found BIB 123.",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(cleared.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-found-clear","bibid":null}"""))
            {
                var result = await mutations.ActionAsync(actor, cleared.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(cleared.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code,
                    "Clearing an identifier-derived BIB must complete without violating CK_TitleRequest_FoundHasBib.");
            }

            var afterClear = await ReadBibOwnershipRequestAsync(cleared.Id);
            Assert.IsNull(afterClear.BibId);
            Assert.IsFalse(afterClear.BibIdStaffVerified);
            Assert.AreEqual("pending", afterClear.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterClear);

            var replaced = await SeedBibOwnershipRequestAsync(
                "identifier-found-replace",
                "123",
                staffVerified: false,
                isbnCheckStatus: "found",
                checkResult: "Identifier found BIB 123.",
                lastCheckedUtc: checkedUtc,
                identifierTags: true);
            requests.Add(replaced.Id);
            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-found-replace","bibid":"456"}"""))
            {
                var result = await mutations.ActionAsync(actor, replaced.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(replaced.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterReplacement = await ReadBibOwnershipRequestAsync(replaced.Id);
            Assert.AreEqual("456", afterReplacement.BibId);
            Assert.IsTrue(afterReplacement.BibIdStaffVerified);
            Assert.AreEqual("pending", afterReplacement.IsbnCheckStatus,
                "A manually validated BIB must not inherit the previous identifier lookup's found state.");
            AssertIdentifierEvidenceWasCleared(afterReplacement);

            var notFound = await SeedBibOwnershipRequestAsync(
                "identifier-not-found-replace",
                "789",
                staffVerified: true,
                isbnCheckStatus: "not_found",
                checkResult: "Identifier not found.",
                lastCheckedUtc: checkedUtc,
                identifierNotFoundTag: true);
            requests.Add(notFound.Id);
            using (var rejectedEdit = JsonDocument.Parse("""{"identifier":"identifier-not-found-replace","bibid":"0"}"""))
            {
                var result = await mutations.ActionAsync(actor, notFound.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(notFound.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = rejectedEdit.RootElement.GetProperty("identifier"),
                    Bibid = rejectedEdit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("invalid_bib", result.Code);
            }

            var afterRejectedEdit = await ReadBibOwnershipRequestAsync(notFound.Id);
            Assert.AreEqual("789", afterRejectedEdit.BibId);
            Assert.IsTrue(afterRejectedEdit.BibIdStaffVerified);
            Assert.AreEqual("not_found", afterRejectedEdit.IsbnCheckStatus);
            Assert.AreEqual("Identifier not found.", afterRejectedEdit.IsbnCheckResult);
            Assert.AreEqual(checkedUtc, afterRejectedEdit.LastCheckedUtc);
            Assert.IsTrue(notFound.RowVersion.SequenceEqual(afterRejectedEdit.RowVersion));
            CollectionAssert.AreEqual(new[] { "polaris_bib_not_found" }, afterRejectedEdit.IdentifierTags.ToArray());

            using (var edit = JsonDocument.Parse("""{"identifier":"identifier-not-found-replace","bibid":"456"}"""))
            {
                var result = await mutations.ActionAsync(actor, notFound.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(afterRejectedEdit.RowVersion),
                    Action = "edit",
                    Status = "suggestion",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterNotFoundReplacement = await ReadBibOwnershipRequestAsync(notFound.Id);
            Assert.AreEqual("456", afterNotFoundReplacement.BibId);
            Assert.IsTrue(afterNotFoundReplacement.BibIdStaffVerified);
            Assert.AreEqual("pending", afterNotFoundReplacement.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterNotFoundReplacement);
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync(requests);
        }
    }

    [TestMethod]
    public async Task IdentifierEditClearsStaffBibBeforePurchasePromotionWorkflow()
    {
        var actor = await GetOwnershipTestActorAsync();
        var mutations = factory!.Services.GetRequiredService<TitleRequestMutationService>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var scope = Slice5IsolatedLibraryId;
        await EnsureSlice5IsolatedLibraryAsync(contextFactory, scope);
        WorkflowSettings? settingsBefore;
        var settingsExisted = false;
        await using (var setup = await contextFactory.CreateDbContextAsync())
        {
            var settings = await setup.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            settingsExisted = settings is not null;
            settingsBefore = settings is null ? null : new WorkflowSettings
            {
                OrganizationId = scope,
                AutoPromote = settings.AutoPromote,
                UpdatedUtc = settings.UpdatedUtc
            };
            if (settings is null)
            {
                settings = new WorkflowSettings { OrganizationId = scope, UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime };
                setup.WorkflowSettings.Add(settings);
            }
            settings.AutoPromote = true;
            settings.UpdatedUtc = timeProvider!.GetUtcNow().UtcDateTime;
            await setup.SaveChangesAsync();
        }

        var seeded = await SeedBibOwnershipRequestAsync(
            "purchase-identifier-A",
            "123",
            staffVerified: true,
            isbnCheckStatus: "found",
            status: "outstanding_purchase",
            autoHold: true,
            checkResult: "Identifier found BIB 123.",
            lastCheckedUtc: timeProvider!.GetUtcNow().UtcDateTime.AddMinutes(-10),
            identifierTags: true,
            libraryOrganizationId: scope);

        try
        {
            using (var edit = JsonDocument.Parse("""{"identifier":"purchase-identifier-B","bibid":"123"}"""))
            {
                var result = await mutations.ActionAsync(actor, seeded.Id, new TitleRequestActionInput
                {
                    Version = StaffVersion.Encode(seeded.RowVersion),
                    Action = "edit",
                    Status = "outstanding_purchase",
                    Identifier = edit.RootElement.GetProperty("identifier"),
                    Bibid = edit.RootElement.GetProperty("bibid")
                }, CancellationToken.None);
                Assert.AreEqual("updated", result.Code);
            }

            var afterEdit = await ReadBibOwnershipRequestAsync(seeded.Id);
            Assert.AreEqual("purchase-identifier-B", afterEdit.Identifier);
            Assert.IsNull(afterEdit.BibId);
            Assert.IsFalse(afterEdit.BibIdStaffVerified);
            Assert.AreEqual("pending", afterEdit.IsbnCheckStatus);
            AssertIdentifierEvidenceWasCleared(afterEdit);

            await PrepareSingleItemCycleAsync(QueueNames.PurchasePromotion, scope, seeded.Id);
            var workflow = factory.Services.GetRequiredService<WorkflowProcessingService>();
            var workflowResult = await workflow.ProcessWorkflowAsync(scope, CancellationToken.None);
            Assert.AreEqual("completed", workflowResult.Code);

            var afterPromotion = await ReadBibOwnershipRequestAsync(seeded.Id);
            Assert.AreEqual("outstanding_purchase", afterPromotion.Status,
                "Automatic promotion cannot enter pending_hold based on the stale staff-authoritative BIB.");
            Assert.IsNull(afterPromotion.BibId);
            await using var verify = await contextFactory.CreateDbContextAsync();
            Assert.IsFalse(await verify.TitleRequestEvents.AnyAsync(item =>
                item.TitleRequestId == seeded.Id && item.EventType == "promoted"));
        }
        finally
        {
            await DeleteBibOwnershipRequestsAsync([seeded.Id]);
            await using var restore = await contextFactory.CreateDbContextAsync();
            var settings = await restore.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == scope);
            if (!settingsExisted)
            {
                if (settings is not null)
                {
                    restore.WorkflowSettings.Remove(settings);
                }
            }
            else if (settings is not null)
            {
                settings.AutoPromote = settingsBefore!.AutoPromote;
                settings.UpdatedUtc = settingsBefore.UpdatedUtc;
            }
            await restore.SaveChangesAsync();
        }
    }

    private async Task<CurrentStaff> GetOwnershipTestActorAsync()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var result = await factory!.Services.GetRequiredService<StaffEligibilityService>().FindByEmailAsync(
            identity.UserPrincipalName!,
            Guid.Parse(identity.TenantId!),
            2,
            StaffRoleRequirement.Any,
            requireParticipation: true,
            CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, result.Outcome);
        return result.Staff!;
    }

    private async Task<(long Id, byte[] RowVersion)> SeedBibOwnershipRequestAsync(
        string identifier,
        string? bibId,
        bool staffVerified,
        string? isbnCheckStatus = "pending",
        int retryCount = 0,
        string? checkResult = null,
        string? lastErrorCode = null,
        DateTime? lastCheckedUtc = null,
        string status = "suggestion",
        bool autoHold = false,
        bool identifierTags = false,
        bool identifierNotFoundTag = false,
        int libraryOrganizationId = 2)
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.Code == "book");
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var request = new TitleRequest
        {
            LibraryOrganizationId = libraryOrganizationId,
            Barcode = $"bib-owner-{Guid.NewGuid():N}",
            Title = "Staff entered title",
            Author = "Staff entered author",
            Identifier = identifier,
            BibId = bibId,
            BibIdStaffVerified = staffVerified,
            MaterialFormatId = format.Id,
            Status = status,
            IsbnCheckStatus = isbnCheckStatus,
            IsbnCheckResult = checkResult,
            IsbnCheckRetryCount = retryCount,
            IsbnCheckLastErrorCode = lastErrorCode,
            LastCheckedUtc = lastCheckedUtc,
            AutoHold = autoHold,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        context.TitleRequests.Add(request);
        await context.SaveChangesAsync();
        if (identifierTags || identifierNotFoundTag)
        {
            var tagCodes = new List<string>();
            if (identifierTags)
            {
                tagCodes.AddRange(["polaris_bib_found", "polaris_multiple_matches"]);
            }
            if (identifierNotFoundTag)
            {
                tagCodes.Add("polaris_bib_not_found");
            }
            var tagIds = await context.WorkflowTags.AsNoTracking()
                .Where(item => tagCodes.Contains(item.Code))
                .Select(item => item.Id)
                .ToListAsync();
            context.TitleRequestWorkflowTags.AddRange(tagIds.Select(tagId => new TitleRequestWorkflowTag
            {
                TitleRequestId = request.Id,
                WorkflowTagId = tagId
            }));
            await context.SaveChangesAsync();
        }
        return (request.Id, request.RowVersion.ToArray());
    }

    private async Task<BibOwnershipRequestState> ReadBibOwnershipRequestAsync(long requestId)
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        var identifierTags = await (
                from link in context.TitleRequestWorkflowTags.AsNoTracking()
                join tag in context.WorkflowTags.AsNoTracking() on link.WorkflowTagId equals tag.Id
                where link.TitleRequestId == requestId &&
                      (tag.Code == "polaris_bib_found" || tag.Code == "polaris_bib_not_found" ||
                       tag.Code == "polaris_multiple_matches")
                select tag.Code)
            .ToListAsync();
        return new BibOwnershipRequestState(
            request.Title,
            request.Status,
            request.Author,
            request.Identifier,
            request.BibId,
            request.BibIdStaffVerified,
            request.IsbnCheckStatus,
            request.IsbnCheckResult,
            request.IsbnCheckRetryCount,
            request.IsbnCheckLastErrorCode,
            request.LastCheckedUtc,
            identifierTags,
            request.RowVersion.ToArray());
    }

    private static void AssertIdentifierEvidenceWasCleared(BibOwnershipRequestState request)
    {
        Assert.IsNull(request.IsbnCheckResult);
        Assert.AreEqual(0, request.IsbnCheckRetryCount);
        Assert.IsNull(request.IsbnCheckLastErrorCode);
        Assert.IsNull(request.LastCheckedUtc);
        Assert.AreEqual(0, request.IdentifierTags.Count,
            "Identifier-derived tags must be removed when the BIB/check evidence is invalidated.");
    }

    private PatronSuggestionService CreateBibOwnershipSuggestionService(IdentifierLookupResult lookupResult) => new(
        factory!.Services.GetRequiredService<ExternalConfiguration>(),
        factory.Services.GetRequiredService<PatronConfigurationService>(),
        new BibOwnershipIdentifierProvider(lookupResult),
        factory.Services.GetRequiredService<IEmailOutboxDispatcher>(),
        factory.Services.GetRequiredService<IEmailSender>(),
        factory.Services.GetRequiredService<RecipientDomainPolicy>(),
        timeProvider!,
        NullLogger<PatronSuggestionService>.Instance);

    private static async Task DeleteBibOwnershipRequestsAsync(IEnumerable<long> requestIds)
    {
        foreach (var requestId in requestIds)
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] = @id; " +
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id; " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;",
                ("@id", requestId));
        }
    }

    private sealed record BibOwnershipRequestState(
        string Title,
        string Status,
        string? Author,
        string? Identifier,
        string? BibId,
        bool BibIdStaffVerified,
        string? IsbnCheckStatus,
        string? IsbnCheckResult,
        int IsbnCheckRetryCount,
        string? IsbnCheckLastErrorCode,
        DateTime? LastCheckedUtc,
        IReadOnlyList<string> IdentifierTags,
        byte[] RowVersion);

    private sealed class BibOwnershipIdentifierProvider(IdentifierLookupResult result) : IPatronProvider
    {
        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PatronSnapshot> RefreshAsync(
            string barcode,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
