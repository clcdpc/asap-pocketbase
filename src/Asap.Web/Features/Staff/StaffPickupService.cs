using System.Data;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record PickupOptionsInput(bool ForceRefresh = false);

public sealed record PickupPreferenceInput(
    string? Version,
    int? PreferredPickupBranchId,
    int? CurrentPreferredPickupBranchIdAtLoad);

public sealed record StaffPickupOptions(
    long RequestId,
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
    bool SnapshotChanged = false);

public sealed class StaffPickupService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IPatronProvider patronProvider,
    ExternalConfiguration configuration,
    TimeProvider timeProvider)
{
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();

    public async Task<StaffPickupResult> GetOptionsAsync(
        CurrentStaff actor,
        long requestId,
        PickupOptionsInput input,
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
                .AnyAsync(item => item.Id == request.LibraryOrganizationId && item.IsActive, cancellationToken))
        {
            return new StaffPickupResult("organization_inactive");
        }
        if (string.IsNullOrWhiteSpace(request.Barcode))
        {
            return new StaffPickupResult("patron_barcode_missing");
        }

        try
        {
            var patron = await patronProvider.RefreshAsync(request.Barcode, cancellationToken);
            var branches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
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
            input.PreferredPickupBranchId is not > 0)
        {
            return new StaffPickupResult("invalid_pickup");
        }

        string barcode;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var locked = await LockAsync(context, actor, requestId, cancellationToken);
            if (locked.Code != "locked") return new StaffPickupResult(locked.Code);
            var request = locked.Request!;
            if (!request.RowVersion.SequenceEqual(expectedVersion)) return new StaffPickupResult("stale_version");
            if (request.Status is "hold_placed" or "closed") return new StaffPickupResult("pickup_read_only");
            if (await context.HoldPlacementOperations.AnyAsync(
                    item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                    cancellationToken))
            {
                return new StaffPickupResult("hold_operation_incomplete");
            }
            barcode = request.Barcode;
            await transaction.CommitAsync(cancellationToken);
        }

        PatronSnapshot patron;
        PickupBranch selectedBranch;
        bool pickupChanged;
        string? oldName;
        try
        {
            patron = await patronProvider.RefreshAsync(barcode, cancellationToken);
            var branches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
            selectedBranch = branches.SingleOrDefault(item => item.Id == input.PreferredPickupBranchId.Value)
                ?? throw new InvalidPickupSelectionException();
            var liveCurrentId = patron.PreferredPickupBranchId;
            if (input.CurrentPreferredPickupBranchIdAtLoad.HasValue &&
                liveCurrentId != input.CurrentPreferredPickupBranchIdAtLoad &&
                selectedBranch.Id != liveCurrentId)
            {
                return new StaffPickupResult("pickup_changed_since_load");
            }
            oldName = branches.SingleOrDefault(item => item.Id == liveCurrentId)?.Label;
            pickupChanged = selectedBranch.Id != liveCurrentId;
            if (pickupChanged)
            {
                await patronProvider.UpdatePreferredPickupBranchAsync(patron, selectedBranch.Id, cancellationToken);
            }
        }
        catch (InvalidPickupSelectionException)
        {
            return new StaffPickupResult("invalid_pickup");
        }
        catch (PolarisOperationalException)
        {
            return new StaffPickupResult("pickup_provider_error");
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var locked = await LockAsync(context, actor, requestId, cancellationToken);
            if (locked.Code != "locked") return new StaffPickupResult(locked.Code);
            var request = locked.Request!;
            if (!request.RowVersion.SequenceEqual(expectedVersion)) return new StaffPickupResult("stale_version");
            if (request.Status is "hold_placed" or "closed") return new StaffPickupResult("pickup_read_only");

            var snapshotChanged = request.PreferredPickupBranchId != selectedBranch.Id ||
                                  !string.Equals(request.PreferredPickupBranchName, selectedBranch.Label, StringComparison.Ordinal);
            if (snapshotChanged || pickupChanged)
            {
                request.PreferredPickupBranchId = selectedBranch.Id;
                request.PreferredPickupBranchName = selectedBranch.Label;
                request.UpdatedUtc = DateTime.UtcNow;
            }
            if (pickupChanged)
            {
                var note = $"Preferred pickup location changed from {oldName ?? "not set"} to {selectedBranch.Label} by {actor.DisplayName ?? actor.UserPrincipalName ?? "staff"}.";
                request.Notes = AppendNote(request.Notes, note);
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "pickup_preference_changed",
                    Status = request.Status,
                    ActorType = "staff",
                    StaffUserId = actor.Id,
                    ActorName = actor.DisplayName ?? actor.UserPrincipalName,
                    Message = note,
                    MetadataJson = JsonSerializer.Serialize(new
                    {
                        fromPickupBranchId = patron.PreferredPickupBranchId,
                        fromPickupBranchName = oldName,
                        toPickupBranchId = selectedBranch.Id,
                        toPickupBranchName = selectedBranch.Label
                    }),
                    CreatedUtc = DateTime.UtcNow
                });
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StaffPickupResult("updated", PickupChanged: pickupChanged, SnapshotChanged: snapshotChanged);
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
            request.Status is "hold_placed" or "closed",
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
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {snapshot.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (organization?.IsActive != true) return new LockedPickup("organization_inactive");
        var staff = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {actor.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (staff is null || !IsCurrentAndEligible(actor, staff, snapshot.LibraryOrganizationId))
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
        row.IsActive && allowedTenantIds.Contains(actor.EntraTenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, actor.AuthenticationEmail) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role is "staff" or "admin" && row.OrganizationId == organizationId);

    private static string AppendNote(string? current, string note) =>
        string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";

    private sealed record LockedPickup(string Code, TitleRequest? Request = null);
    private sealed class InvalidPickupSelectionException : Exception;
}
