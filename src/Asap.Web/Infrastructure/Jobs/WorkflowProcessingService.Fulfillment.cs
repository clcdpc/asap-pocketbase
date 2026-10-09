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

public sealed partial class WorkflowProcessingService
{
    private async Task<WorkflowItemResult> FulfillRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        if (!await IsProgressFenceCurrentAsync(
                QueueNames.FulfillmentTracking,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        // A bounded HoldPickupTimeout scan may leave a due row for this later phase.
        // Check the current row before calling Polaris so fulfillment cannot outrun it.
        if (await IsFulfillmentTimeoutDueAsync(candidate, cancellationToken))
        {
            return new WorkflowItemResult("deferred_timeout");
        }

        var operation = await LatestTrackedOperationAsync(candidate.Id, cancellationToken);
        IReadOnlyList<PolarisCheckoutSnapshot> checkouts;
        try
        {
            checkouts = await polaris.GetPatronCheckoutsAsync(candidate.Barcode, candidate.LibraryOrganizationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            logger.LogWarning(exception, "Patron checkout evidence unavailable for request {RequestId}.", candidate.Id);
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (checkouts.Any(item => item.BibId <= 0 || item.PatronBarcode is not null &&
                                  !string.Equals(item.PatronBarcode, candidate.Barcode, StringComparison.Ordinal)))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }
        if (checkouts.Any(item => item.BibId == candidate.BibId))
        {
            return await CloseFulfilledAsync(
                candidate,
                scanScope,
                new FulfillmentEvidence(null, operation),
                expectedProgressVersion,
                cancellationToken,
                manualActorEvidence);
        }

        IReadOnlyList<PolarisHoldSnapshot> holds;
        try
        {
            holds = await polaris.GetPatronHoldsAsync(candidate.Barcode, candidate.LibraryOrganizationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException exception)
        {
            logger.LogWarning(exception, "Patron hold evidence unavailable for request {RequestId}.", candidate.Id);
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (holds.Any(item => item.HoldRequestId <= 0 || item.BibId <= 0 || item.PatronBarcode is not null &&
                              !string.Equals(item.PatronBarcode, candidate.Barcode, StringComparison.Ordinal)))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (operation is null || operation.PolarisHoldId is not > 0)
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_unavailable",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }
        if (operation.BibIdSnapshot != candidate.BibId ||
            !string.Equals(operation.PatronBarcodeSnapshot, candidate.Barcode, StringComparison.Ordinal))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        var tracked = holds.Where(item => item.HoldRequestId == operation.PolarisHoldId).ToList();
        if (tracked.Count != 1)
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                tracked.Count == 0 ? "hold_identity_unavailable" : "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        var trackedHold = tracked[0];
        if (trackedHold.BibId != candidate.BibId)
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        var terminalReason = PolarisHoldStatusPolicy.TerminalReason(trackedHold.StatusId);
        if (terminalReason is null)
        {
            return new WorkflowItemResult("not_terminal");
        }

        return await CloseFulfilledAsync(
            candidate,
            scanScope,
            new FulfillmentEvidence(terminalReason, operation),
            expectedProgressVersion,
            cancellationToken,
            manualActorEvidence);
    }

    private async Task<WorkflowItemResult> CloseFulfilledAsync(
        TitleRequest candidate,
        int scanScope,
        FulfillmentEvidence evidence,
        byte[] expectedProgressVersion,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
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
            request.Status == RequestStatus.HoldPlaced &&
            request.BibId == candidate.BibId &&
            string.Equals(request.Barcode, candidate.Barcode, StringComparison.Ordinal))
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var (timeoutEnabled, timeoutDays) = TimeoutSetting(settings, TimeoutFamily.HoldPickupTimeout);
            var incomplete = await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken) ||
                await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            if (incomplete)
            {
                code = "hold_operation_incomplete";
            }
            else if (timeoutEnabled && timeoutDays.HasValue &&
                     TimeoutSemantics.IsExpired(request.UpdatedUtc, timeProvider.GetUtcNow(), businessTimeZone, timeoutDays.Value))
            {
                code = "deferred_timeout";
            }
            else if (evidence.Operation is not null)
            {
                var operation = await LoadLockedOperationAsync(context, evidence.Operation.Id, cancellationToken);
                var latest = await LoadLatestSucceededOperationAsync(context, request.Id, cancellationToken);
                if (operation is null || latest is null || !SameOperationIdentity(latest, evidence.Operation) ||
                    !SameOperationIdentity(operation, evidence.Operation) ||
                    operation.BibIdSnapshot != request.BibId ||
                    !string.Equals(operation.PatronBarcodeSnapshot, request.Barcode, StringComparison.Ordinal))
                {
                    code = "hold_identity_ambiguous";
                }
                else
                {
                    code = "changed";
                    changed = true;
                }
            }
            else
            {
                var latest = await LoadLatestSucceededOperationAsync(context, request.Id, cancellationToken);
                if (latest is not null)
                {
                    code = "hold_identity_ambiguous";
                }
                else
                {
                    code = "changed";
                    changed = true;
                }
            }

            if (changed)
            {
                request.Status = RequestStatus.Closed;
                request.CloseReason = evidence.TerminalReason ?? "hold_completed";
                request.UpdatedUtc = UtcNow();
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "fulfilled",
                    Status = RequestStatus.Closed,
                    CloseReason = request.CloseReason,
                    ActorType = "system",
                    Message = "Polaris checkout/hold evidence completed this request.",
                    CreatedUtc = UtcNow()
                });
            }
        }

        return await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.FulfillmentTracking,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
    }

    private async Task<WorkflowItemResult> RecordFulfillmentDiagnosticAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        HoldPlacementOperation? expectedOperation,
        string code,
        bool stop,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        _ = await context.Organizations.FromSqlInterpolated(
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
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (expectedOperation is not null && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == RequestStatus.HoldPlaced)
        {
            var operation = await LoadLockedOperationAsync(context, expectedOperation.Id, cancellationToken);
            if (operation is not null && SameOperationIdentity(operation, expectedOperation))
            {
                operation.LastErrorCode = code;
            }
        }

        var result = await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.FulfillmentTracking,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed: false,
            cancellationToken);
        return result with { Stop = stop };
    }

    private async Task<bool> IsFulfillmentTimeoutDueAsync(
        TitleRequest candidate,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidate.Id, cancellationToken);
        if (request is null || request.LibraryOrganizationId != candidate.LibraryOrganizationId ||
            !request.RowVersion.SequenceEqual(candidate.RowVersion) || request.Status != RequestStatus.HoldPlaced)
        {
            return false;
        }

        var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
        var (enabled, days) = TimeoutSetting(settings, TimeoutFamily.HoldPickupTimeout);
        return enabled && days.HasValue && TimeoutSemantics.IsExpired(
            request.UpdatedUtc,
            timeProvider.GetUtcNow(),
            businessTimeZone,
            days.Value);
    }

    private async Task<HoldPlacementOperation?> LatestTrackedOperationAsync(
        long requestId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => item.TitleRequestId == requestId && item.State == HoldOperationState.Succeeded && item.CompletedUtc != null)
            .OrderByDescending(item => item.AttemptNumber)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<HoldPlacementOperation?> LoadLockedOperationAsync(
        AsapDbContext context,
        long operationId,
        CancellationToken cancellationToken) =>
        await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<HoldPlacementOperation?> LoadLatestSucceededOperationAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken) =>
        await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT TOP (1) * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [TitleRequestId] = {requestId} AND [State] = N'succeeded' AND [CompletedUtc] IS NOT NULL ORDER BY [AttemptNumber] DESC, [Id] DESC")
            .SingleOrDefaultAsync(cancellationToken);

    private static bool SameOperationIdentity(HoldPlacementOperation current, HoldPlacementOperation expected) =>
        current.Id == expected.Id && current.AttemptNumber == expected.AttemptNumber &&
        current.State == HoldOperationState.Succeeded && current.CompletedUtc.HasValue &&
        current.RowVersion.SequenceEqual(expected.RowVersion) &&
        string.Equals(current.PatronBarcodeSnapshot, expected.PatronBarcodeSnapshot, StringComparison.Ordinal) &&
        current.BibIdSnapshot == expected.BibIdSnapshot &&
        current.PolarisHoldId == expected.PolarisHoldId;

    private sealed record FulfillmentEvidence(
        string? TerminalReason,
        HoldPlacementOperation? Operation);

}
