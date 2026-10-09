using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Staff;

public sealed record PickupReconciliationInput(
    string? Version,
    int? CurrentPreferredPickupBranchIdAtLoad,
    bool CurrentPreferredPickupBranchObservedAtLoad,
    bool ConfirmOriginalDispatchEnded);

internal sealed record PickupRecoveryRow(Guid Id, string Barcode, int PatronId, int LibraryOrganizationId,
    long? TitleRequestId, int State);

public sealed partial class StaffPickupService
{
    // A super-admin explicitly accepts the live preference after inspecting the
    // original invocation. This never sends a provider write and does not label
    // observation as success of that original invocation.
    public async Task<StaffPickupResult> ReconcileAsync(
        CurrentStaff actor, Guid operationId, PickupReconciliationInput input,
        CancellationToken cancellationToken)
    {
        if (!input.ConfirmOriginalDispatchEnded || !input.CurrentPreferredPickupBranchObservedAtLoad)
        {
            return new StaffPickupResult("pickup_reconciliation_required");
        }
        PickupRecoveryRow? operation;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            operation = await context.Database.SqlQuery<PickupRecoveryRow>($"""
                SELECT [Id], [Barcode], [PatronId], [LibraryOrganizationId], [TitleRequestId], [State]
                FROM [asap].[PickupPreferenceOperation] WHERE [Id] = {operationId}
                """).SingleOrDefaultAsync(cancellationToken);
        }
        if (operation is null || actor.Role != StaffRole.SuperAdmin)
        {
            return new StaffPickupResult("not_found");
        }
        if (pickupMutations.IsDispatchActive(operationId))
        {
            return new StaffPickupResult("pickup_reconciliation_required");
        }
        byte[]? expectedVersion = null;
        if (operation.TitleRequestId is not null)
        {
            if (!StaffVersion.TryDecode(input.Version, out var decoded))
            {
                return new StaffPickupResult("stale_version");
            }
            expectedVersion = decoded;
        }
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            var gate = await LockRecoveryAsync(context, transaction, actor, operation, expectedVersion, cancellationToken);
            if (gate.Code != "locked")
            {
                return new StaffPickupResult(gate.Code);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        PatronSnapshot patron;
        IReadOnlyList<PickupBranch> branches;
        try
        {
            patron = await patronProvider.RefreshAsync(operation.Barcode, operation.LibraryOrganizationId, cancellationToken);
            branches = await patronProvider.GetPickupBranchesAsync(patron, operation.LibraryOrganizationId, cancellationToken);
        }
        catch (PolarisOperationalException)
        {
            return new StaffPickupResult("pickup_provider_error");
        }
        if (patron.PatronId != operation.PatronId ||
            !patron.KnownBarcodeAliases.Contains(operation.Barcode, StringComparer.OrdinalIgnoreCase) ||
            patron.PreferredPickupBranchId != input.CurrentPreferredPickupBranchIdAtLoad)
        {
            return new StaffPickupResult("pickup_changed_since_load");
        }
        var selected = branches.SingleOrDefault(branch => branch.Id == patron.PreferredPickupBranchId);
        if (patron.PreferredPickupBranchId is not null && selected is null)
        {
            return new StaffPickupResult("invalid_pickup");
        }

        await using var finalContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var finalTransaction = await finalContext.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockRecoveryAsync(finalContext, finalTransaction, actor, operation, expectedVersion, cancellationToken);
        if (locked.Code != "locked")
        {
            return new StaffPickupResult(locked.Code);
        }
        if (pickupMutations.IsDispatchActive(operationId))
        {
            return new StaffPickupResult("pickup_reconciliation_required");
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var snapshotChanged = false;
        if (locked.Request is { } request)
        {
            if (request.Status is not (RequestStatus.Closed or RequestStatus.HoldPlaced))
            {
                snapshotChanged = request.PreferredPickupBranchId != patron.PreferredPickupBranchId ||
                                  request.PreferredPickupBranchName != selected?.Label;
                request.PreferredPickupBranchId = patron.PreferredPickupBranchId;
                request.PreferredPickupBranchName = selected?.Label;
            }
            var note = $"Pickup operation reconciled to observed live preference {selected?.Label ?? "not set"} by {actor.DisplayName ?? actor.AuthenticationEmail}.";
            request.Notes = AppendNote(request.Notes, note);
            request.UpdatedUtc = now;
            finalContext.TitleRequestEvents.Add(new TitleRequestEvent {
                TitleRequestId = request.Id, EventType = "pickup_preference_reconciled", Status = request.Status,
                ActorType = StaffRole.Staff, StaffUserId = actor.Id, ActorName = actor.DisplayName,
                Message = note, MetadataJson = JsonSerializer.Serialize(new { operationId,
                    observedPickupBranchId = patron.PreferredPickupBranchId, confirmedByRead = true }), CreatedUtc = now });
        }
        var changed = await finalContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE [asap].[PickupPreferenceOperation]
            SET [State] = 3, [CompletedUtc] = {now}, [DispatchFinishedUtc] = COALESCE([DispatchFinishedUtc], {now}),
                [ProviderConfirmedUtc] = {now}, [ConfirmedByRead] = CAST(1 AS bit),
                [ObservedPickupBranchId] = {patron.PreferredPickupBranchId}, [ResolvedByStaffUserId] = {actor.Id}
            WHERE [Id] = {operationId} AND [CompletedUtc] IS NULL
              AND [PatronId] = {patron.PatronId} AND [LibraryOrganizationId] = {operation.LibraryOrganizationId};
            """, cancellationToken);
        if (changed != 1)
        {
            return new StaffPickupResult("pickup_reconciliation_required");
        }
        await finalContext.SaveChangesAsync(cancellationToken);
        await finalTransaction.CommitAsync(cancellationToken);
        return new StaffPickupResult("updated", SnapshotChanged: snapshotChanged, OperationId: operationId,
            RequestId: operation.TitleRequestId, FinalStatus: locked.Request?.Status);
    }

    private async Task<LockedPickup> LockRecoveryAsync(
        AsapDbContext context, IDbContextTransaction transaction, CurrentStaff actor,
        PickupRecoveryRow operation, byte[]? expectedVersion, CancellationToken cancellationToken)
    {
        if (!await PatronMutationLock.TryAcquireAsync((SqlConnection)context.Database.GetDbConnection(),
                (SqlTransaction)transaction.GetDbTransaction(), operation.PatronId, [operation.Barcode], cancellationToken))
        {
            return new LockedPickup("pickup_reconciliation_required");
        }
        var lockedIds = new HashSet<int>();
        foreach (var id in new[] { 1, operation.LibraryOrganizationId }.Distinct().Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null)
            {
                return new LockedPickup("not_found");
            }
            lockedIds.Add(id);
        }
        var authority = await staffEligibility.RevalidateLockedAsync(context, actor, null,
            StaffRoleRequirement.SuperAdmin, requireActorParticipation: true, lockedIds, cancellationToken);
        if (authority.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return new LockedPickup("staff_scope_forbidden");
        }
        if (operation.TitleRequestId is not { } requestId)
        {
            return new LockedPickup("locked");
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
            $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null || request.LibraryOrganizationId != operation.LibraryOrganizationId ||
            request.PatronIdSnapshot is > 0 && request.PatronIdSnapshot != operation.PatronId)
        {
            return new LockedPickup("not_found");
        }
        return expectedVersion is null || !request.RowVersion.SequenceEqual(expectedVersion)
            ? new LockedPickup("stale_version") : new LockedPickup("locked", request);
    }
}
