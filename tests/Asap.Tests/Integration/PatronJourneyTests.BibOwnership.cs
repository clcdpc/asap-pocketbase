using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
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
        string? isbnCheckStatus = "pending")
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var format = await context.MaterialFormats.SingleAsync(item => item.Code == "book");
        var now = timeProvider!.GetUtcNow().UtcDateTime;
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"bib-owner-{Guid.NewGuid():N}",
            Title = "Staff entered title",
            Author = "Staff entered author",
            Identifier = identifier,
            BibId = bibId,
            BibIdStaffVerified = staffVerified,
            MaterialFormatId = format.Id,
            Status = "suggestion",
            IsbnCheckStatus = isbnCheckStatus,
            AutoHold = false,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        context.TitleRequests.Add(request);
        await context.SaveChangesAsync();
        return (request.Id, request.RowVersion.ToArray());
    }

    private async Task<BibOwnershipRequestState> ReadBibOwnershipRequestAsync(long requestId)
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var request = await context.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == requestId);
        return new BibOwnershipRequestState(
            request.Title,
            request.Status,
            request.Author,
            request.Identifier,
            request.BibId,
            request.BibIdStaffVerified,
            request.IsbnCheckStatus,
            request.RowVersion.ToArray());
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
