using System.Data;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Jobs;

public sealed partial class WorkflowProcessingService
{
    private async Task<WorkflowItemResult> PromoteRowAsync(
        TitleRequest candidate, int scanScope, byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence, CancellationToken cancellationToken)
    {
        if (!await IsProgressFenceCurrentAsync(QueueNames.PurchasePromotion, scanScope, expectedProgressVersion, cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        // The first transaction closes before the network read. The second checks the same
        // authority, rowversion and operation barriers before committing the lookup and cursor together.
        var (skipped, exclusions) = await CheckPurchaseAsync(candidate, scanScope, expectedProgressVersion, manualActorEvidence, null, null, cancellationToken);
        if (skipped is not null)
        {
            return skipped;
        }
        var lookup = await EvaluatePurchaseAsync(candidate, exclusions!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (await CheckPurchaseAsync(candidate, scanScope, expectedProgressVersion, manualActorEvidence, lookup, exclusions, cancellationToken)).Result!;
    }

    private async Task<(WorkflowItemResult? Result, string[]? Exclusions)> CheckPurchaseAsync(TitleRequest candidate, int scanScope,
        byte[] expectedVersion, StaffIdentityEvidence? actor, IdentifierLookupResult? lookup, string[]? lookedUpWith, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        Task<WorkflowItemResult> CheckpointAsync(string code) => CommitLocalOutcomeAsync(context, transaction,
            QueueNames.PurchasePromotion, scanScope, expectedVersion, candidate.CreatedUtc, candidate.Id, code, code == "changed", cancellationToken);

        if (!await LockManualOrganizationsAsync(context, actor, candidate.LibraryOrganizationId, cancellationToken))
        {
            return (new WorkflowItemResult("staff_scope_forbidden", Stop: true), null);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(context, actor, candidate.LibraryOrganizationId, cancellationToken))
        {
            return (new WorkflowItemResult("staff_scope_forbidden", Stop: true), null);
        }
        var settings = await EffectiveWorkflowAsync(context, candidate.LibraryOrganizationId, cancellationToken);
        var exclusions = settings.ReadExcludedPromotionToms();
        if (organization is null || !OrganizationAuthority.IsActiveLibrary(organization) || settings.AutoPromote != true ||
            lookedUpWith is not null && !lookedUpWith.SequenceEqual(exclusions))
        {
            return (await CheckpointAsync("skipped"), null);
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request?.LibraryOrganizationId != organization.Id || request.Status != RequestStatus.OutstandingPurchase ||
            !request.RowVersion.SequenceEqual(candidate.RowVersion))
        {
            return (await CheckpointAsync("skipped"), null);
        }
        if (await IsIdentifierMutationProtectedAsync(context, request.Id, cancellationToken))
        {
            return (await CheckpointAsync("hold_operation_incomplete"), null);
        }

        var targetBib = request.BibIdStaffVerified ? request.BibId : lookup?.BibId;
        var duplicate = targetBib is > 0 && (request.BibId is null || request.BibId == targetBib) &&
            await context.TitleRequests.AnyAsync(item => item.Id != request.Id && item.LibraryOrganizationId == organization.Id &&
                item.Status != RequestStatus.Closed && item.BibId == targetBib &&
                ((request.PatronIdSnapshot.HasValue && item.PatronIdSnapshot == request.PatronIdSnapshot) ||
                 ((!request.PatronIdSnapshot.HasValue || !item.PatronIdSnapshot.HasValue) && item.Barcode == request.Barcode)),
                cancellationToken);
        if (lookup is null)
        {
            return (duplicate ? await CheckpointAsync("duplicate_open_request") : null, exclusions);
        }
        var outcome = await ApplyPurchaseLookupAsync(context, request, lookup, duplicate, cancellationToken);
        return (await CheckpointAsync(outcome), null);
    }

    private async Task<IdentifierLookupResult> EvaluatePurchaseAsync(TitleRequest request, string[] exclusions, CancellationToken cancellationToken)
    {
        try
        {
            if (request.BibIdStaffVerified && request.BibId is > 0)
            {
                return await polaris.ValidatePurchaseBibAsync(request.BibId.Value, request.LibraryOrganizationId, exclusions, cancellationToken);
            }
            if (!PolarisPatronProvider.SupportsPurchaseIdentifier(request.Identifier))
            {
                return new IdentifierLookupResult(IdentifierLookupOutcome.OperationalFailure, ErrorCode: "unsupported_identifier");
            }
            return await polaris.LookupPurchaseIdentifierAsync(request.Identifier!, request.LibraryOrganizationId, exclusions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is PolarisOperationalException or TimeoutException or HttpRequestException or OperationCanceledException)
        {
            return new IdentifierLookupResult(IdentifierLookupOutcome.OperationalFailure);
        }
    }

    private async Task<string> ApplyPurchaseLookupAsync(AsapDbContext context, TitleRequest request,
        IdentifierLookupResult lookup, bool duplicate, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        request.LastPromoterCheckUtc = now;
        var (code, tag, message) = lookup switch
        {
            { Outcome: IdentifierLookupOutcome.Found, MultipleMatches: true } =>
                ("multiple_matches", "polaris_multiple_matches", "Multiple eligible Polaris BIBs match. Select the correct BIB to continue."),
            { Outcome: IdentifierLookupOutcome.Found, BibId: > 0 } when request.BibId.HasValue && request.BibId != lookup.BibId =>
                ("bib_conflict", "polaris_bib_found", $"The identifier matches BIB {lookup.BibId}, but BIB {request.BibId} is assigned. Review the selection."),
            { Outcome: IdentifierLookupOutcome.Found, BibId: > 0 } when duplicate =>
                ("duplicate_open_request", "polaris_bib_found", $"Another open request for this patron already has BIB {lookup.BibId}."),
            { Outcome: IdentifierLookupOutcome.Found, BibId: > 0 } =>
                ("changed", "polaris_bib_found", request.BibIdStaffVerified ? "Staff-selected BIB confirmed in Polaris." : "BIB verified by a unique Polaris identifier match."),
            { Outcome: IdentifierLookupOutcome.DefinitiveNotFound } when request.BibIdStaffVerified =>
                ("no_eligible_bib", null, "The staff-selected BIB could not be confirmed as eligible for automatic promotion."),
            { Outcome: IdentifierLookupOutcome.DefinitiveNotFound } =>
                ("no_eligible_bib", "polaris_bib_not_found", "No eligible Polaris BIB was found."),
            { ErrorCode: "unsupported_identifier" } => ("unsupported_identifier", null, "A supported identifier or staff-selected BIB is required."),
            _ => ("purchase_lookup_failed", null, "Polaris could not confirm the catalog result. ASAP will check again.")
        };
        request.LastPromoterResult = message;
        if (!request.BibIdStaffVerified)
        {
            var links = await (from link in context.TitleRequestWorkflowTags
                               join definition in context.WorkflowTags on link.WorkflowTagId equals definition.Id
                               where link.TitleRequestId == request.Id && IdentifierDerivedTagCodes.Contains(definition.Code)
                               select new { Link = link, definition.Code }).ToListAsync(cancellationToken);
            context.TitleRequestWorkflowTags.RemoveRange(links.Where(item => item.Code != tag).Select(item => item.Link));
            if (tag is not null && !links.Any(item => item.Code == tag))
            {
                var tagId = await context.WorkflowTags.Where(item => item.Code == tag).Select(item => item.Id).SingleAsync(cancellationToken);
                context.TitleRequestWorkflowTags.Add(new TitleRequestWorkflowTag { TitleRequestId = request.Id, WorkflowTagId = tagId });
            }
            if (tag is not null)
            {
                // A blocked match has no accepted BIB. Keep its evidence in the result without granting found status.
                request.IsbnCheckStatus = code switch
                {
                    "changed" => IdentifierCheckState.Found,
                    "no_eligible_bib" => IdentifierCheckState.NotFound,
                    _ => null
                };
                request.IsbnCheckResult = code == "changed" ? $"Polaris identifier search confirmed BIB {lookup.BibId}." : message;
                request.IsbnCheckRetryCount = 0;
                request.IsbnCheckLastErrorCode = null;
                request.LastCheckedUtc = now;
            }
        }
        if (code != "changed")
        {
            return code;
        }
        if (!request.BibIdStaffVerified)
        {
            request.AutoVerifiedBibId = request.BibId = lookup.BibId;
            request.AutoVerifiedIdentifier = request.Identifier;
            request.Title = MergePurchaseMetadata(lookup.CatalogTitle, request.Title) ?? request.Title;
            request.Author = MergePurchaseMetadata(lookup.CatalogAuthor, request.Author);
        }
        request.Status = TitleRequestWorkflowPolicy.AfterCatalogMatch(request.AutoHold);
        request.CloseReason = request.AutoHold ? null : "purchased_no_hold";
        request.UpdatedUtc = now;
        context.TitleRequestEvents.Add(new TitleRequestEvent
        {
            TitleRequestId = request.Id, EventType = "promoted", Status = request.Status, CloseReason = request.CloseReason,
            ActorType = "system", Message = request.AutoHold ? "Purchase promoted to hold placement." : "Purchase completed without an automatic hold.",
            CreatedUtc = now
        });
        return code;
    }

    private static string? MergePurchaseMetadata(string? catalog, string? original)
    {
        var merged = PatronSuggestionService.MergeCatalogValue(catalog, original);
        return merged.Length is > 0 and <= 500 ? merged : original;
    }
}
