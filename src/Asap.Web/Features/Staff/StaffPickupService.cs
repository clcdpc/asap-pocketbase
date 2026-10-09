using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;

namespace Asap.Web.Features.Staff;

public sealed record PickupPreferenceInput(
    string? Version,
    int? PreferredPickupBranchId,
    int? CurrentPreferredPickupBranchIdAtLoad,
    bool? CurrentPreferredPickupBranchObservedAtLoad = null);

public sealed record StaffPickupOptions(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long RequestId,
    string Version,
    string Status,
    bool ReadOnly,
    int? RequestSnapshotPickupBranchId,
    string? RequestSnapshotPickupBranchName,
    IReadOnlyList<PickupBranch> PickupBranches,
    DateTimeOffset PickupBranchesRefreshedAt,
    int? CurrentPreferredPickupBranchId,
    string? CurrentPreferredPickupBranchName,
    int? SelectedPickupBranchId,
    string? SelectedPickupBranchName,
    bool CurrentPreferenceAllowed,
    string? PickupBranchWarning,
    bool PickupOptionsUnavailable);

public sealed record StaffPickupResult(
    string Code,
    StaffPickupOptions? Options = null,
    bool PickupChanged = false,
    bool SnapshotChanged = false,
    Guid? OperationId = null,
    string? LocalFailureCode = null,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long? RequestId = null,
    string? FinalStatus = null);

public sealed partial class StaffPickupService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IPatronProvider patronProvider,
    TimeProvider timeProvider,
    PickupPreferenceMutationService pickupMutations,
    StaffEligibilityService staffEligibility)
{

    public async Task<StaffPickupResult> GetOptionsAsync(
        CurrentStaff actor,
        long requestId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (request is null || !TitleRequestViewService.CanAccess(actor, request.LibraryOrganizationId))
        {
            return new StaffPickupResult("not_found");
        }
        if (!await context.Organizations.AsNoTracking()
                .AnyAsync(item => item.Id == request.LibraryOrganizationId && item.Id > 1 &&
                                  item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId &&
                                  item.IsActive, cancellationToken))
        {
            return new StaffPickupResult("organization_inactive");
        }
        if (string.IsNullOrWhiteSpace(request.Barcode))
        {
            return new StaffPickupResult("patron_barcode_missing");
        }

        try
        {
            var patron = await patronProvider.RefreshAsync(request.Barcode, request.LibraryOrganizationId, cancellationToken);
            if (!MatchesRequestIdentity(request.Barcode, request.PatronIdSnapshot, patron))
            {
                return new StaffPickupResult("pickup_changed_since_load");
            }
            var branches = await patronProvider.GetPickupBranchesAsync(patron, request.LibraryOrganizationId, cancellationToken);
            var options = BuildOptions(request, patron, branches, timeProvider.GetUtcNow());
            return new StaffPickupResult("loaded", options);
        }
        catch (PolarisOperationalException)
        {
            return new StaffPickupResult("pickup_provider_error");
        }
    }

    public async Task<StaffPickupResult> UpdateAsync(
        CurrentStaff actor,
        long requestId,
        PickupPreferenceInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) ||
            input.PreferredPickupBranchId is not > PolarisConfigurationValidation.SystemOrganizationId)
        {
            return new StaffPickupResult("invalid_pickup");
        }

        string barcode;
        int organizationId;
        int? requestPatronId;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var locked = await LockAsync(context, actor, requestId, cancellationToken);
            if (locked.Code != "locked")
            {
                return new StaffPickupResult(locked.Code);
            }

            var request = locked.Request!;
            if (!request.RowVersion.SequenceEqual(expectedVersion))
            {
                return new StaffPickupResult("stale_version");
            }

            if (request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed)
            {
                return new StaffPickupResult("pickup_read_only");
            }

            if (await context.HoldPlacementOperations.AnyAsync(
                    item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                    cancellationToken))
            {
                return new StaffPickupResult("hold_operation_incomplete");
            }
            barcode = request.Barcode;
            organizationId = request.LibraryOrganizationId;
            requestPatronId = request.PatronIdSnapshot;
            await transaction.CommitAsync(cancellationToken);
        }

        PatronSnapshot patron;
        PickupBranch selectedBranch;
        bool pickupChanged;
        PickupMutationReceipt? receipt;
        string? oldName;
        try
        {
            patron = await patronProvider.RefreshAsync(barcode, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesRequestIdentity(barcode, requestPatronId, patron))
            {
                return new StaffPickupResult("pickup_changed_since_load");
            }
            var branches = await patronProvider.GetPickupBranchesAsync(patron, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            selectedBranch = branches.SingleOrDefault(item => item.Id == input.PreferredPickupBranchId.Value)
                ?? throw new InvalidPickupSelectionException();
            var liveCurrentId = patron.PreferredPickupBranchId;
            if ((input.CurrentPreferredPickupBranchObservedAtLoad == true ||
                 input.CurrentPreferredPickupBranchIdAtLoad.HasValue) &&
                liveCurrentId != input.CurrentPreferredPickupBranchIdAtLoad &&
                selectedBranch.Id != liveCurrentId)
            {
                return new StaffPickupResult("pickup_changed_since_load");
            }
            oldName = branches.SingleOrDefault(item => item.Id == liveCurrentId)?.Label;
            var dispatchGate = await RevalidateDispatchAsync(actor, requestId, expectedVersion,
                organizationId, barcode, patron.PatronId, cancellationToken);
            if (dispatchGate != "allowed")
            {
                return new StaffPickupResult(dispatchGate);
            }
            receipt = await pickupMutations.ChangeAsync(patron, organizationId, selectedBranch,
                oldName, "request", requestId, actor.Id,
                (connection, transaction, token) => ValidatePickupIntentAsync(
                    contextFactory, actor, requestId, expectedVersion, organizationId, barcode, patron.PatronId,
                    connection, transaction, token),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            pickupChanged = receipt is not null;
            oldName = receipt?.FromBranchName ?? oldName;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (InvalidPickupSelectionException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new StaffPickupResult("invalid_pickup");
        }
        catch (PolarisOperationalException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new StaffPickupResult("pickup_provider_error");
        }
        catch (PickupMutationException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new StaffPickupResult(exception.Code, PickupChanged: exception.PickupPreferenceChanged,
                OperationId: exception.OperationId);
        }
        catch (PickupMutationBlockedException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new StaffPickupResult(exception.Code);
        }

        try
        {
            await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
            await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                var locked = await LockAsync(context, actor, requestId, cancellationToken);
                if (locked.Code != "locked")
                {
                    return LocalFailure(locked.Code, receipt);
                }
                var request = locked.Request!;
                if (!request.RowVersion.SequenceEqual(expectedVersion))
                {
                    return LocalFailure("stale_version", receipt);
                }
                if (request.PatronIdSnapshot is > 0 && request.PatronIdSnapshot != patron.PatronId)
                {
                    return LocalFailure("pickup_changed_since_load", receipt);
                }
                if (request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed)
                {
                    return LocalFailure("pickup_read_only", receipt);
                }
                if (await context.HoldPlacementOperations.AnyAsync(
                        item => item.TitleRequestId == request.Id && item.CompletedUtc == null, cancellationToken))
                {
                    return LocalFailure("hold_operation_incomplete", receipt);
                }

                var snapshotChanged = request.PreferredPickupBranchId != selectedBranch.Id ||
                                      !string.Equals(request.PreferredPickupBranchName, selectedBranch.Label, StringComparison.Ordinal);
                if (snapshotChanged || pickupChanged)
                {
                    request.PreferredPickupBranchId = selectedBranch.Id;
                    request.PreferredPickupBranchName = selectedBranch.Label;
                    request.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
                }
                if (pickupChanged)
                {
                    var note = receipt!.ConfirmedByRead
                        ? $"Preferred pickup location reconciled to live value {selectedBranch.Label} by {actor.DisplayName ?? actor.UserPrincipalName ?? "staff"}."
                        : $"Preferred pickup location changed from {oldName ?? "not set"} to {selectedBranch.Label} by {actor.DisplayName ?? actor.UserPrincipalName ?? "staff"}.";
                    request.Notes = AppendNote(request.Notes, note);
                    context.TitleRequestEvents.Add(new TitleRequestEvent
                    {
                        TitleRequestId = request.Id,
                        EventType = receipt.ConfirmedByRead ? "pickup_preference_reconciled" : "pickup_preference_changed",
                        Status = request.Status,
                        ActorType = "staff",
                        StaffUserId = actor.Id,
                        ActorName = actor.DisplayName ?? actor.UserPrincipalName,
                        Message = note,
                        MetadataJson = JsonSerializer.Serialize(new
                        {
                            pickupOperationId = receipt!.OperationId,
                            confirmedByRead = receipt.ConfirmedByRead,
                            fromPickupBranchId = receipt.FromBranchId,
                            fromPickupBranchName = oldName,
                            toPickupBranchId = selectedBranch.Id,
                            toPickupBranchName = selectedBranch.Label
                        }),
                        CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
                    });
                }
                await context.SaveChangesAsync(cancellationToken);
                await PickupPreferenceMutationService.CompleteAsync(
                    (SqlConnection)context.Database.GetDbConnection(), (SqlTransaction)transaction.GetDbTransaction(),
                    receipt, request.Id, patron.PatronId, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new StaffPickupResult("updated", PickupChanged: pickupChanged, SnapshotChanged: snapshotChanged,
                    OperationId: receipt?.OperationId);
            }
        }
        catch (Exception exception) when (receipt is not null &&
                                        exception is SqlException or DbUpdateException or PickupMutationException)
        {
            // The pre-dispatch journal remains pending if this local transaction
            // rolls back. Cancellation propagates, also leaving that durable intent.
            cancellationToken.ThrowIfCancellationRequested();
            return LocalFailure("local_persistence_failed", receipt);
        }
    }

    private static StaffPickupResult LocalFailure(string code, PickupMutationReceipt? receipt) => receipt is null
        ? new StaffPickupResult(code)
        : new StaffPickupResult("pickup_changed_request_not_updated", PickupChanged: true,
            OperationId: receipt.OperationId, LocalFailureCode: code);

    private async Task<string> RevalidateDispatchAsync(
        CurrentStaff actor, long requestId, byte[] expectedVersion, int organizationId, string barcode,
        int verifiedPatronId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockAsync(context, actor, requestId, cancellationToken);
        if (locked.Code != "locked")
        {
            return locked.Code;
        }
        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) || request.LibraryOrganizationId != organizationId ||
            request.Barcode != barcode ||
            request.PatronIdSnapshot is > 0 && request.PatronIdSnapshot != verifiedPatronId)
        {
            return "stale_version";
        }
        if (request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed)
        {
            return "pickup_read_only";
        }
        if (await context.HoldPlacementOperations.AnyAsync(
                x => x.TitleRequestId == requestId && x.CompletedUtc == null, cancellationToken))
        {
            return "hold_operation_incomplete";
        }
        await transaction.CommitAsync(cancellationToken);
        return "allowed";
    }

    private async Task ValidatePickupIntentAsync(
        IDbContextFactory<AsapDbContext> contextFactory,
        CurrentStaff actor,
        long requestId,
        byte[] expectedVersion,
        int organizationId,
        string barcode,
        int verifiedPatronId,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var context = await PickupPreferenceMutationService.BindContextAsync(
            contextFactory, connection, transaction, cancellationToken);
        var locked = await LockAsync(context, actor, requestId, cancellationToken);
        if (locked.Code != "locked")
        {
            throw new PickupMutationBlockedException(locked.Code);
        }

        var request = locked.Request!;
        if (!request.RowVersion.SequenceEqual(expectedVersion) ||
            request.LibraryOrganizationId != organizationId || request.Barcode != barcode ||
            request.PatronIdSnapshot is > 0 && request.PatronIdSnapshot != verifiedPatronId)
        {
            throw new PickupMutationBlockedException("stale_version");
        }
        if (request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed)
        {
            throw new PickupMutationBlockedException("pickup_read_only");
        }
        if (await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == requestId && item.CompletedUtc == null,
                cancellationToken))
        {
            throw new PickupMutationBlockedException("hold_operation_incomplete");
        }
    }

    private StaffPickupOptions BuildOptions(
        TitleRequest request,
        PatronSnapshot patron,
        IReadOnlyList<PickupBranch> branches,
        DateTimeOffset refreshedAt)
    {
        var current = branches.SingleOrDefault(item => item.Id == patron.PreferredPickupBranchId);
        return new StaffPickupOptions(
            request.Id,
            StaffVersion.Encode(request.RowVersion),
            request.Status,
            request.Status is RequestStatus.HoldPlaced or RequestStatus.Closed,
            request.PreferredPickupBranchId,
            request.PreferredPickupBranchName,
            branches,
            refreshedAt,
            patron.PreferredPickupBranchId,
            current?.Label,
            current?.Id,
            current?.Label,
            current is not null,
            current is null ? "Choose a preferred pickup location." : null,
            branches.Count == 0);
    }

    private static bool MatchesRequestIdentity(string requestBarcode, int? requestPatronId, PatronSnapshot patron) =>
        patron.PatronId > 0 &&
        (requestPatronId is not > 0 || requestPatronId == patron.PatronId) &&
        patron.KnownBarcodeAliases.Contains(requestBarcode, StringComparer.OrdinalIgnoreCase);

    private async Task<LockedPickup> LockAsync(
        AsapDbContext context,
        CurrentStaff actor,
        long requestId,
        CancellationToken cancellationToken)
    {
        var snapshot = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (snapshot is null || !TitleRequestViewService.CanAccess(actor, snapshot.LibraryOrganizationId))
        {
            return new LockedPickup("not_found");
        }
        if (!await StaffEligibilityService.LockOrganizationsAsync(context,
                [LibraryScope.SystemOrganizationId, snapshot.LibraryOrganizationId], cancellationToken))
        {
            return new LockedPickup("organization_inactive");
        }
        var organization = context.Organizations.Local.Single(item => item.Id == snapshot.LibraryOrganizationId);
        if (!OrganizationAuthority.IsActiveLibrary(organization))
        {
            return new LockedPickup("organization_inactive");
        }

        var staff = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {actor.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (staff is null || !IsCurrentAndEligible(actor, staff, snapshot.LibraryOrganizationId) ||
            !StaffEligibilityService.HasLockedActiveOrganization(context, staff))
        {
            return new LockedPickup("staff_scope_forbidden");
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestId}")
            .SingleOrDefaultAsync(cancellationToken);
        return request is null || request.LibraryOrganizationId != snapshot.LibraryOrganizationId
            ? new LockedPickup("not_found")
            : new LockedPickup("locked", request);
    }

    private bool IsCurrentAndEligible(CurrentStaff actor, StaffUser row, int organizationId) =>
        staffEligibility.IsCurrentAndEligible(actor, row, organizationId);

    private static string AppendNote(string? current, string note) =>
        string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";

    private sealed record LockedPickup(string Code, TitleRequest? Request = null);
    private sealed class InvalidPickupSelectionException : Exception;
}
