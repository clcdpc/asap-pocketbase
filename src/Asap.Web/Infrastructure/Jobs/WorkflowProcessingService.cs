using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Jobs;

public sealed record WorkflowRunResult(
    string Code,
    int Visited = 0,
    int Changed = 0,
    int Skipped = 0,
    Guid? ManualRunId = null);

internal sealed record WorkflowItemResult(
    string Code,
    bool Changed = false,
    bool Stop = false,
    bool FenceLost = false,
    bool LocalCommit = false,
    byte[]? ProgressVersion = null);

internal sealed record WorkflowCycleState(
    QueueProgress Progress,
    bool Inactive = false,
    bool FenceLost = false);

internal sealed record WeeklySummaryItem(string Title, string? Author);

public sealed partial class WorkflowProcessingService(
    IDbContextFactory<AsapDbContext> contextFactory,
    QueueProgressService progressService,
    HoldPlacementService holdPlacement,
    PatronSuggestionService suggestionService,
    IStaffPolarisProvider polaris,
    IPolarisReferenceProvider referenceProvider,
    ExternalConfiguration configuration,
    WorkflowProcessingGuard workflowGuard,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailSender emailSender,
    IEmailOutboxDispatcher outboxDispatcher,
    TimeProvider timeProvider,
    ILogger<WorkflowProcessingService> logger,
    StaffEligibilityService staffEligibility)
{
    private static readonly string[] IdentifierDerivedTagCodes =
        ["polaris_bib_found", "polaris_bib_not_found", "polaris_multiple_matches"];

    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        configuration.Application.BusinessTimeZone!);

    public async Task<WorkflowRunResult> ProcessIdentifierAsync(
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null)
        {
            return new WorkflowRunResult("workflow_processing_busy");
        }

        try
        {
            return await ProcessTitleQueueAsync(
                QueueNames.IdentifierProcessing,
                scopeOrganizationId,
                item => item.Status == RequestStatus.Suggestion && item.IsbnCheckStatus == IdentifierCheckState.Pending,
                ProcessIdentifierRowAsync,
                cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError(exception, "SQL failure stopped identifier processing.");
            return new WorkflowRunResult("sql_failure");
        }
    }

    public async Task<WorkflowRunResult> ProcessWorkflowAsync(
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null)
        {
            return new WorkflowRunResult("workflow_processing_busy");
        }

        var result = new WorkflowRunResult("completed");
        var phases = new Func<Task<WorkflowRunResult>>[]
        {
            () => ProcessRecoveryQueueAsync(scopeOrganizationId, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.OutstandingTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.PendingHoldTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.HoldPickupTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.AdditionalCopyTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.PurchasePromotion,
                scopeOrganizationId,
                item => item.Status == RequestStatus.OutstandingPurchase,
                (item, scanScope, expectedVersion, token) => PromoteRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.HoldPlacement,
                scopeOrganizationId,
                TitleRequestWorkflowPolicy.HoldPlacementStage,
                (item, scanScope, expectedVersion, token) => PlaceHoldRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.FulfillmentTracking,
                scopeOrganizationId,
                item => item.Status == RequestStatus.HoldPlaced,
                (item, scanScope, expectedVersion, token) => FulfillRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken)
        };
        foreach (var phase in phases)
        {
            WorkflowRunResult phaseResult;
            try
            {
                phaseResult = await phase();
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                logger.LogError(exception, "SQL failure stopped workflow processing.");
                phaseResult = new WorkflowRunResult("sql_failure");
            }
            result = Add(result, phaseResult);
            if (StopsWorkflow(phaseResult.Code))
            {
                break;
            }
        }
        return result;
    }

    public async Task<WorkflowRunResult> RefreshOrganizationsAsync(CancellationToken cancellationToken = default)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null)
        {
            return new WorkflowRunResult("workflow_processing_busy");
        }

        var snapshots = await referenceProvider.GetOrganizationsAsync(cancellationToken);
        if (snapshots.Count == 0)
        {
            return new WorkflowRunResult("organization_snapshot_empty");
        }
        if (snapshots.Any(item => item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.DisplayName)) ||
            snapshots.Select(item => item.Id).Distinct().Count() != snapshots.Count)
        {
            return new WorkflowRunResult("organization_snapshot_invalid");
        }

        var patronCodes = await referenceProvider.GetPatronCodesAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var changed = 0;
        foreach (var snapshot in snapshots.Where(item => item.Id > LibraryScope.SystemOrganizationId))
        {
            var organization = await context.Organizations.SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);
            if (organization is null)
            {
                organization = new Organization
                {
                    Id = snapshot.Id,
                    DisplayName = snapshot.DisplayName,
                    Abbreviation = snapshot.Abbreviation,
                    OrganizationCodeId = snapshot.OrganizationCodeId,
                    ParentOrganizationId = snapshot.ParentOrganizationId,
                    IsActive = false
                };
                context.Organizations.Add(organization);
                changed++;
            }
            else
            {
                if (organization.DisplayName != snapshot.DisplayName || organization.Abbreviation != snapshot.Abbreviation ||
                    organization.OrganizationCodeId != snapshot.OrganizationCodeId ||
                    organization.ParentOrganizationId != snapshot.ParentOrganizationId)
                {
                    organization.DisplayName = snapshot.DisplayName;
                    organization.Abbreviation = snapshot.Abbreviation;
                    organization.OrganizationCodeId = snapshot.OrganizationCodeId;
                    organization.ParentOrganizationId = snapshot.ParentOrganizationId;
                    changed++;
                }
            }
            organization.LastSyncedUtc = UtcNow();

            if (!OrganizationAuthority.IsLibrary(snapshot.Id, snapshot.OrganizationCodeId))
            {
                organization.IsActive = false;
                var sessions = await context.PatronSessions
                    .Where(item => item.EffectiveOrganizationId == snapshot.Id && item.RevokedUtc == null)
                    .ToListAsync(cancellationToken);
                var revokedUtc = UtcNow();
                foreach (var session in sessions)
                {
                    session.RevokedUtc = revokedUtc;
                }
            }
        }
        await context.SaveChangesAsync(cancellationToken);
        return new WorkflowRunResult("completed", snapshots.Count + patronCodes.Count, changed);
    }

    public async Task<int> CleanupSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcNow();
        var rows = await context.PatronSessions
            .Where(item => item.ExpiresUtc <= now || item.RevokedUtc != null && item.RevokedUtc <= now.AddDays(-1))
            .ToListAsync(cancellationToken);
        context.PatronSessions.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    public async Task<int> CleanupEmailPayloadsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = UtcNow().AddDays(-90);
        var rows = await context.EmailOutbox
            .Where(item => (item.Status == "sent" && item.SentUtc < cutoff) ||
                           (item.Status == "suppressed" && item.SuppressedUtc < cutoff))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Subject = null;
            row.BodyText = null;
            row.BodyHtml = null;
        }
        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private async Task<WorkflowItemResult> ProcessIdentifierRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Identifier))
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            var request = organization is null
                ? null
                : await context.TitleRequests.FromSqlInterpolated(
                        $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                    .SingleOrDefaultAsync(cancellationToken);
            var eligible = organization is not null && OrganizationAuthority.IsActiveLibrary(organization) && request is not null &&
                          request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
                          request.RowVersion.SequenceEqual(candidate.RowVersion) &&
                          request.Status == RequestStatus.Suggestion && request.IsbnCheckStatus == IdentifierCheckState.Pending;
            var protectedByHistoryOrOperation = eligible &&
                await IsIdentifierMutationProtectedAsync(context, request!.Id, cancellationToken);
            var changed = eligible && !protectedByHistoryOrOperation;
            if (changed)
            {
                var derivedTags = await (
                    from link in context.TitleRequestWorkflowTags
                    join tag in context.WorkflowTags on link.WorkflowTagId equals tag.Id
                    where link.TitleRequestId == request!.Id && IdentifierDerivedTagCodes.Contains(tag.Code)
                    select link).ToListAsync(cancellationToken);
                context.TitleRequestWorkflowTags.RemoveRange(derivedTags);
                if (!request!.BibIdStaffVerified)
                {
                    request.BibId = null;
                }
                request!.IsbnCheckStatus = IdentifierCheckState.SkippedNoIdentifier;
                request.IsbnCheckRetryCount = 0;
                request.IsbnCheckResult = null;
                request.IsbnCheckLastErrorCode = null;
                request.LastCheckedUtc = null;
                request.UpdatedUtc = UtcNow();
            }
            return await CommitLocalOutcomeAsync(
                context,
                transaction,
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                candidate.CreatedUtc,
                candidate.Id,
                "processed",
                changed,
                cancellationToken);
        }

        if (!await IsProgressFenceCurrentAsync(
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        try
        {
            var lookup = await suggestionService.ProcessIdentifierLookupForQueueAsync(
                candidate.Id,
                candidate.Identifier,
                candidate.LibraryOrganizationId,
                candidate.RowVersion,
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                candidate.CreatedUtc,
                candidate.Id,
                cancellationToken);
            return new WorkflowItemResult(
                lookup.Outcome == IdentifierLookupOutcome.OperationalFailure ? "operational_failure" : "processed",
                Stop: lookup.Outcome == IdentifierLookupOutcome.OperationalFailure,
                LocalCommit: lookup.QueueProgressVersion is not null,
                ProgressVersion: lookup.QueueProgressVersion);
        }
        catch (PolarisOperationalException)
        {
            return new WorkflowItemResult("operational_failure", Stop: true);
        }
    }

    private async Task<WorkflowItemResult> PromoteRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = organization is null
            ? null
            : await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var code = "skipped";
        var changed = false;
        if (organization is not null && OrganizationAuthority.IsActiveLibrary(organization) && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == RequestStatus.OutstandingPurchase)
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var incomplete = await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken) ||
                await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            if (incomplete)
            {
                code = "hold_operation_incomplete";
            }
            else if (settings.AutoPromote == true && request.BibId is > 0)
            {
                var otherOpenBibs = request.AutoHold ? await context.TitleRequests.AsNoTracking().Where(item =>
                    item.LibraryOrganizationId == request.LibraryOrganizationId &&
                    ((request.PatronIdSnapshot.HasValue && item.PatronIdSnapshot == request.PatronIdSnapshot) ||
                     item.PatronIdSnapshot == null && item.Barcode == request.Barcode) && item.BibId != null &&
                    item.Id != request.Id && item.Status != RequestStatus.Closed)
                    .Select(item => item.BibId!)
                    .ToListAsync(cancellationToken) : [];
                var duplicate = otherOpenBibs.Contains(request.BibId);
                if (duplicate)
                {
                    code = "duplicate_open_request";
                }
                else if (request.AutoHold && !request.BibIdStaffVerified)
                {
                    code = "bib_unverified";
                }
                else
                {
                    request.Status = TitleRequestWorkflowPolicy.AfterCatalogMatch(request.AutoHold);
                    request.CloseReason = request.AutoHold ? null : "purchased_no_hold";
                    request.LastPromoterCheckUtc = UtcNow();
                    request.UpdatedUtc = UtcNow();
                    context.TitleRequestEvents.Add(new TitleRequestEvent
                    {
                        TitleRequestId = request.Id,
                        EventType = "promoted",
                        Status = request.Status,
                        CloseReason = request.CloseReason,
                        ActorType = "system",
                        Message = request.AutoHold ? "Purchase promoted to hold placement." : "Purchase completed without an automatic hold.",
                        CreatedUtc = UtcNow()
                    });
                    code = "changed";
                    changed = true;
                }
            }
        }

        return await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.PurchasePromotion,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
    }

    private async Task<WorkflowItemResult> PlaceHoldRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        if (!await IsProgressFenceCurrentAsync(
                QueueNames.HoldPlacement,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        var result = await holdPlacement.PlaceBackgroundAsync(
            candidate.Id,
            candidate.RowVersion,
            QueueNames.HoldPlacement,
            scanScope,
            expectedProgressVersion,
            cancellationToken,
            manualActorEvidence);
        return new WorkflowItemResult(
            result.Code == "updated" ? "changed" : result.Code,
            Changed: result.Code == "updated",
            Stop: result.Code is "hold_provider_error" or "operation_ownership_lost");
    }

    private static async Task<bool> IsIdentifierMutationProtectedAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (await PickupPreferenceMutationService.HasIncompleteAsync(context, requestId, cancellationToken))
        {
            return true;
        }
        if (await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == requestId && item.CompletedUtc == null,
                cancellationToken) ||
            await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == requestId && item.State == HoldOperationState.Succeeded,
                cancellationToken))
        {
            return true;
        }

        return await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId)
            .Select(item => item.LegacyHoldProtected).SingleAsync(cancellationToken);
    }

    private async Task<bool> IsManualActorAllowedLockedAsync(
        AsapDbContext context,
        StaffIdentityEvidence? evidence,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        if (evidence is null)
        {
            return true;
        }

        var actor = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {evidence.StaffUserId}")
            .SingleOrDefaultAsync(cancellationToken);
        return actor is not null && StaffEligibilityService.HasLockedActiveOrganization(context, actor) &&
            staffEligibility.IsCurrentAndEligible(evidence, actor, targetOrganizationId, StaffRoleRequirement.Admin);
    }

    private static async Task<bool> LockManualOrganizationsAsync(
        AsapDbContext context,
        StaffIdentityEvidence? evidence,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        if (evidence is null)
        {
            return true;
        }

        foreach (var organizationId in new[] { 1, targetOrganizationId }.Distinct().Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<WorkflowSettings> EffectiveWorkflowAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken = default)
    {
        var system = await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken) ?? new WorkflowSettings();
        var library = organizationId == LibraryScope.SystemOrganizationId ? null : await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return new WorkflowSettings
        {
            OrganizationId = organizationId,
            AutoPromote = library?.AutoPromote ?? system.AutoPromote,
            OutstandingTimeoutEnabled = library?.OutstandingTimeoutEnabled ?? system.OutstandingTimeoutEnabled,
            OutstandingTimeoutDays = library?.OutstandingTimeoutDays ?? system.OutstandingTimeoutDays,
            OutstandingTimeoutSendEmail = library?.OutstandingTimeoutSendEmail ?? system.OutstandingTimeoutSendEmail,
            OutstandingTimeoutRejectionTemplateId = library?.OutstandingTimeoutRejectionTemplateId ?? system.OutstandingTimeoutRejectionTemplateId,
            PendingHoldTimeoutEnabled = library?.PendingHoldTimeoutEnabled ?? system.PendingHoldTimeoutEnabled,
            PendingHoldTimeoutDays = library?.PendingHoldTimeoutDays ?? system.PendingHoldTimeoutDays,
            HoldPickupTimeoutEnabled = library?.HoldPickupTimeoutEnabled ?? system.HoldPickupTimeoutEnabled,
            HoldPickupTimeoutDays = library?.HoldPickupTimeoutDays ?? system.HoldPickupTimeoutDays,
            AdditionalCopyTimeoutEnabled = library?.AdditionalCopyTimeoutEnabled ?? system.AdditionalCopyTimeoutEnabled,
            AdditionalCopyTimeoutDays = library?.AdditionalCopyTimeoutDays ?? system.AdditionalCopyTimeoutDays
        };
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AppendNote(string? current, string note) => string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";
    private static async Task<bool> SaveWithConcurrencyAsync(AsapDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private static bool StopsWorkflow(string code) =>
        code is "sql_failure" or "stale_progress_fence" or "operational_failure";

    private static WorkflowRunResult Add(WorkflowRunResult left, WorkflowRunResult right)
    {
        var code = StopsWorkflow(right.Code)
            ? right.Code
            : left.Code == "completed" && right.Code != "completed" && right.Code != "organization_inactive"
                ? right.Code
                : left.Code;
        return new WorkflowRunResult(code, left.Visited + right.Visited, left.Changed + right.Changed, left.Skipped + right.Skipped);
    }
}
