using System.Data;
using System.Text.Json;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Staff;

public sealed record StaffCreateInput(
    string? TenantId,
    string? ObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    string? NotificationEmail,
    string? Role,
    int? OrganizationId);

public sealed record StaffMetadataInput(
    string? Version,
    string? UserPrincipalName,
    string? DisplayName,
    string? NotificationEmail);

public sealed record StaffRoleInput(string? Version, string? Role, int? OrganizationId);
public sealed record StaffDeactivateInput(string? Version);
public sealed record StaffRebindInput(
    string? Version,
    string? TenantId,
    string? ObjectId,
    bool Confirmed,
    string? Reason);

public sealed record StaffLifecycleResult(
    string Code,
    StaffUser? User = null,
    int RulesDeactivated = 0,
    int OpenTitleClaimsCleared = 0);

public sealed class StaffLifecycleService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration)
{
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();

    public async Task<IReadOnlyList<StaffUser>> ListAsync(
        CurrentStaff actor,
        int? organizationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.StaffUsers.AsNoTracking();
        if (actor.Role != "super_admin")
        {
            query = query.Where(item => item.OrganizationId == actor.OrganizationId && item.Role != "super_admin");
        }
        else if (organizationId.HasValue)
        {
            query = query.Where(item => item.OrganizationId == organizationId.Value);
        }

        return await query
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.UserPrincipalName)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<StaffLifecycleResult> CreateAsync(
        CurrentStaff actor,
        StaffCreateInput input,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(input.TenantId, out var tenantId) ||
            !Guid.TryParse(input.ObjectId, out var objectId) ||
            !allowedTenantIds.Contains(tenantId) ||
            !TryNormalizeRoleOrganization(input.Role, input.OrganizationId, out var role, out var organizationId) ||
            !StaffEmail.TryNormalize(input.NotificationEmail, out var notificationEmail))
        {
            return new StaffLifecycleResult("invalid_staff_user");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await AcquireLifecycleLockAsync(context, cancellationToken))
        {
            return new StaffLifecycleResult("staff_invariant_busy");
        }

        var actorSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actorSnapshot is null)
        {
            return new StaffLifecycleResult("staff_session_invalid");
        }

        var organizationIds = new[] { actorSnapshot.OrganizationId, organizationId }.Distinct().Order().ToArray();
        var organizations = new Dictionary<int, Organization>();
        foreach (var id in organizationIds)
        {
            var organization = await LockOrganizationAsync(context, id, cancellationToken);
            if (organization is null)
            {
                return new StaffLifecycleResult("organization_not_found");
            }
            organizations[id] = organization;
        }

        var lockedActor = await LockStaffAsync(context, actor.Id, cancellationToken);
        if (!CanManage(
                actor,
                lockedActor,
                organizations.GetValueOrDefault(lockedActor?.OrganizationId ?? 0),
                organizationId,
                role))
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }
        if (role != "super_admin" && !organizations[organizationId].IsActive)
        {
            return new StaffLifecycleResult("organization_inactive");
        }

        var existing = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [EntraTenantId] = {tenantId} AND [EntraObjectId] = {objectId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            if (existing.IsActive)
            {
                return new StaffLifecycleResult("identity_already_exists");
            }
            if (!CanManage(
                    actor,
                    lockedActor,
                    organizations.GetValueOrDefault(lockedActor?.OrganizationId ?? 0),
                    existing.OrganizationId,
                    existing.Role))
            {
                return new StaffLifecycleResult("staff_scope_forbidden");
            }

            existing.IsActive = true;
            existing.Role = role;
            existing.OrganizationId = organizationId;
            existing.UserPrincipalName = Clean(input.UserPrincipalName);
            existing.NormalizedUserPrincipalName = NormalizeUpn(input.UserPrincipalName);
            existing.DisplayName = Clean(input.DisplayName);
            existing.NotificationEmail = notificationEmail;
            AddAudit(context, actor, existing.Id, organizationId, "staff_reactivated", new { role });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StaffLifecycleResult("created", existing);
        }

        var user = new StaffUser
        {
            EntraTenantId = tenantId,
            EntraObjectId = objectId,
            UserPrincipalName = Clean(input.UserPrincipalName),
            NormalizedUserPrincipalName = NormalizeUpn(input.UserPrincipalName),
            DisplayName = Clean(input.DisplayName),
            NotificationEmail = notificationEmail,
            Role = role,
            OrganizationId = organizationId,
            IsActive = true
        };
        context.StaffUsers.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        AddAudit(context, actor, user.Id, organizationId, "staff_created", new { role });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StaffLifecycleResult("created", user);
    }

    public async Task<StaffLifecycleResult> UpdateMetadataAsync(
        CurrentStaff actor,
        long targetId,
        StaffMetadataInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) ||
            !StaffEmail.TryNormalize(input.NotificationEmail, out var notificationEmail))
        {
            return new StaffLifecycleResult("invalid_staff_user");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await AcquireLifecycleLockAsync(context, cancellationToken))
        {
            return new StaffLifecycleResult("staff_invariant_busy");
        }

        var targetSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == targetId, cancellationToken);
        if (targetSnapshot is null)
        {
            return new StaffLifecycleResult("not_found");
        }
        var actorSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actorSnapshot is null)
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }

        var organizations = new Dictionary<int, Organization>();
        foreach (var id in new[] { actorSnapshot.OrganizationId, targetSnapshot.OrganizationId }.Distinct().Order())
        {
            var organization = await LockOrganizationAsync(context, id, cancellationToken);
            if (organization is null)
            {
                return new StaffLifecycleResult("organization_not_found");
            }
            organizations[id] = organization;
        }

        var locked = new Dictionary<long, StaffUser>();
        foreach (var id in new[] { actor.Id, targetId }.Distinct().Order())
        {
            var row = await LockStaffAsync(context, id, cancellationToken);
            if (row is null)
            {
                return new StaffLifecycleResult(id == targetId ? "not_found" : "staff_scope_forbidden");
            }
            locked[id] = row;
        }

        var lockedActor = locked[actor.Id];
        var target = locked[targetId];
        if (!CanManage(
                actor,
                lockedActor,
                organizations.GetValueOrDefault(lockedActor.OrganizationId),
                target.OrganizationId,
                target.Role))
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }
        if (!target.RowVersion.SequenceEqual(expectedVersion))
        {
            return new StaffLifecycleResult("stale_version");
        }

        target.UserPrincipalName = Clean(input.UserPrincipalName);
        target.NormalizedUserPrincipalName = NormalizeUpn(input.UserPrincipalName);
        target.DisplayName = Clean(input.DisplayName);
        target.NotificationEmail = notificationEmail;
        AddAudit(context, actor, target.Id, target.OrganizationId, "staff_metadata_updated", new { });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StaffLifecycleResult("updated", target);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new StaffLifecycleResult("stale_version");
        }
    }

    public Task<StaffLifecycleResult> ChangeRoleAsync(
        CurrentStaff actor,
        long targetId,
        StaffRoleInput input,
        CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(actor, targetId, input.Version, true, input.Role, input.OrganizationId, cancellationToken);

    public Task<StaffLifecycleResult> DeactivateAsync(
        CurrentStaff actor,
        long targetId,
        StaffDeactivateInput input,
        CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(actor, targetId, input.Version, false, null, null, cancellationToken);

    public async Task<StaffLifecycleResult> RebindAsync(
        CurrentStaff actor,
        long targetId,
        StaffRebindInput input,
        CancellationToken cancellationToken)
    {
        if (!input.Confirmed || string.IsNullOrWhiteSpace(input.Reason) ||
            !StaffVersion.TryDecode(input.Version, out var expectedVersion) ||
            !Guid.TryParse(input.TenantId, out var tenantId) ||
            !Guid.TryParse(input.ObjectId, out var objectId) ||
            !allowedTenantIds.Contains(tenantId) || actor.Role != "super_admin")
        {
            return new StaffLifecycleResult("rebind_not_confirmed");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await AcquireLifecycleLockAsync(context, cancellationToken))
        {
            return new StaffLifecycleResult("staff_invariant_busy");
        }

        var actorSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actorSnapshot is null)
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }
        var targetSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == targetId, cancellationToken);
        if (targetSnapshot is null)
        {
            return new StaffLifecycleResult("not_found");
        }
        var organizations = new Dictionary<int, Organization>();
        foreach (var organizationId in new[] { actorSnapshot.OrganizationId, targetSnapshot.OrganizationId }.Distinct().Order())
        {
            var organization = await LockOrganizationAsync(context, organizationId, cancellationToken);
            if (organization is null)
            {
                return new StaffLifecycleResult("organization_not_found");
            }
            organizations[organizationId] = organization;
        }

        var locked = new Dictionary<long, StaffUser>();
        foreach (var id in new[] { actor.Id, targetId }.Distinct().Order())
        {
            var row = await LockStaffAsync(context, id, cancellationToken);
            if (row is null)
            {
                return new StaffLifecycleResult("not_found");
            }
            locked[id] = row;
        }
        if (!CanManage(
                actor,
                locked[actor.Id],
                organizations.GetValueOrDefault(locked[actor.Id].OrganizationId),
                locked[targetId].OrganizationId,
                locked[targetId].Role))
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }

        var duplicate = await context.StaffUsers.AsNoTracking().AnyAsync(
            item => item.Id != targetId && item.IsActive &&
                    item.EntraTenantId == tenantId && item.EntraObjectId == objectId,
            cancellationToken);
        if (duplicate)
        {
            return new StaffLifecycleResult("identity_already_exists");
        }

        var target = locked[targetId];
        if (!target.RowVersion.SequenceEqual(expectedVersion))
        {
            return new StaffLifecycleResult("stale_version");
        }
        var oldTenantId = target.EntraTenantId;
        var oldObjectId = target.EntraObjectId;
        target.EntraTenantId = tenantId;
        target.EntraObjectId = objectId;
        AddAudit(context, actor, target.Id, target.OrganizationId, "staff_identity_rebound", new
        {
            oldTenantId,
            oldObjectId,
            newTenantId = tenantId,
            newObjectId = objectId,
            reason = input.Reason!.Trim()
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StaffLifecycleResult("updated", target);
    }

    private async Task<StaffLifecycleResult> ChangeLifecycleAsync(
        CurrentStaff actor,
        long targetId,
        string? encodedVersion,
        bool newActive,
        string? requestedRole,
        int? requestedOrganizationId,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(encodedVersion, out var expectedVersion))
        {
            return new StaffLifecycleResult("invalid_version");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await AcquireLifecycleLockAsync(context, cancellationToken))
        {
            return new StaffLifecycleResult("staff_invariant_busy");
        }

        var actorSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
        if (actorSnapshot is null)
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }
        var targetSnapshot = await context.StaffUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == targetId, cancellationToken);
        if (targetSnapshot is null)
        {
            return new StaffLifecycleResult("not_found");
        }
        var role = requestedRole is null ? targetSnapshot.Role : requestedRole.Trim().ToLowerInvariant();
        var organizationId = requestedOrganizationId ??
            (role == "super_admin" ? 1 : targetSnapshot.OrganizationId);
        if (!TryNormalizeRoleOrganization(role, organizationId, out role, out organizationId))
        {
            return new StaffLifecycleResult("invalid_role_scope");
        }

        var organizations = new Dictionary<int, Organization>();
        foreach (var id in new[] { actorSnapshot.OrganizationId, targetSnapshot.OrganizationId, organizationId }.Distinct().Order())
        {
            var organization = await LockOrganizationAsync(context, id, cancellationToken);
            if (organization is null)
            {
                return new StaffLifecycleResult("organization_not_found");
            }
            if (id == organizationId && newActive && role != "super_admin" && !organization.IsActive)
            {
                return new StaffLifecycleResult("organization_inactive");
            }
            organizations[id] = organization;
        }

        var locked = new Dictionary<long, StaffUser>();
        foreach (var id in new[] { actor.Id, targetId }.Distinct().Order())
        {
            var row = await LockStaffAsync(context, id, cancellationToken);
            if (row is null)
            {
                return new StaffLifecycleResult("not_found");
            }
            locked[id] = row;
        }
        var lockedActor = locked[actor.Id];
        var target = locked[targetId];
        if (!CanManage(
                actor,
                lockedActor,
                organizations.GetValueOrDefault(lockedActor.OrganizationId),
                target.OrganizationId,
                target.Role) ||
            (role == "super_admin" && lockedActor.Role != "super_admin"))
        {
            return new StaffLifecycleResult("staff_scope_forbidden");
        }
        if (!target.RowVersion.SequenceEqual(expectedVersion))
        {
            return new StaffLifecycleResult("stale_version");
        }

        var targetWillBeUsableSuperAdmin = newActive && role == "super_admin" && organizationId == 1 &&
                                           target.EntraTenantId.HasValue &&
                                           target.EntraObjectId.HasValue &&
                                           allowedTenantIds.Contains(target.EntraTenantId.Value);
        var otherUsableSuperAdmins = await context.StaffUsers.AsNoTracking().CountAsync(
            item => item.Id != target.Id && item.IsActive && item.Role == "super_admin" &&
                    item.OrganizationId == 1 && item.EntraTenantId.HasValue && item.EntraObjectId.HasValue &&
                    allowedTenantIds.Contains(item.EntraTenantId.Value),
            cancellationToken);
        if (!targetWillBeUsableSuperAdmin && otherUsableSuperAdmins == 0)
        {
            return new StaffLifecycleResult("active_super_admin_required");
        }

        var scopeContracts = target.IsActive &&
                             (!newActive || target.Role == "super_admin" && role != "super_admin" ||
                              target.Role != "super_admin" &&
                              (role == "super_admin" ? false : target.OrganizationId != organizationId));
        var rulesDeactivated = 0;
        var titleClaimsCleared = 0;
        if (scopeContracts)
        {
            var rules = await context.FormatAutoClaimRules
                .Where(item => item.IsActive && item.StaffUserId == target.Id &&
                               (!newActive || role != "super_admin" && item.LibraryOrganizationId != organizationId))
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var rule in rules)
            {
                rule.IsActive = false;
                rule.DeactivatedUtc = DateTime.UtcNow;
            }
            rulesDeactivated = rules.Count;

            var requests = await context.TitleRequests
                .Where(item => item.ClaimedByStaffUserId == target.Id && item.Status != "closed" &&
                               (!newActive || role != "super_admin" && item.LibraryOrganizationId != organizationId))
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            foreach (var request in requests)
            {
                request.ClaimedByStaffUserId = null;
                request.ClaimedByDisplayName = null;
                request.ClaimedAtUtc = null;
                request.ClaimType = null;
                request.ClaimRuleId = null;
                request.UpdatedUtc = DateTime.UtcNow;
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "claim_cleared",
                    Status = request.Status,
                    ActorType = "system",
                    StaffUserId = actor.Id,
                    ActorName = actor.DisplayName,
                    Message = "Claim cleared because the assignee's staff access changed.",
                    MetadataJson = JsonSerializer.Serialize(new { reason = "staff_scope_contracted", targetStaffUserId = target.Id }),
                    CreatedUtc = DateTime.UtcNow
                });
            }
            titleClaimsCleared = requests.Count;
        }

        target.IsActive = newActive;
        target.Role = role;
        target.OrganizationId = organizationId;
        AddAudit(context, actor, target.Id, organizationId, newActive ? "staff_lifecycle_updated" : "staff_deactivated", new
        {
            role,
            rulesDeactivated,
            openTitleClaimsCleared = titleClaimsCleared,
            openAdditionalCopyClaimsCleared = 0
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new StaffLifecycleResult("updated", target, rulesDeactivated, titleClaimsCleared);
    }

    private bool CanManage(
        CurrentStaff ticketActor,
        StaffUser? lockedActor,
        Organization? lockedActorOrganization,
        int targetOrganizationId,
        string targetRole) =>
        lockedActor is not null && lockedActor.IsActive &&
        lockedActorOrganization is not null && lockedActorOrganization.IsActive &&
        lockedActorOrganization.Id == lockedActor.OrganizationId &&
        allowedTenantIds.Contains(ticketActor.EntraTenantId) &&
        lockedActor.EntraTenantId == ticketActor.EntraTenantId &&
        lockedActor.EntraObjectId == ticketActor.EntraObjectId &&
        (lockedActor.Role == "super_admin" && lockedActor.OrganizationId == 1 ||
         lockedActor.Role == "admin" && lockedActor.OrganizationId > 1) &&
        (lockedActor.Role == "super_admin" ||
         lockedActor.OrganizationId == targetOrganizationId && targetRole != "super_admin");

    private static bool TryNormalizeRoleOrganization(
        string? roleValue,
        int? organizationIdValue,
        out string role,
        out int organizationId)
    {
        role = roleValue?.Trim().ToLowerInvariant() ?? "staff";
        organizationId = organizationIdValue ?? 0;
        if (role == "super_admin")
        {
            organizationId = 1;
            return true;
        }
        return role is "staff" or "admin" && organizationId > 1;
    }

    private static async Task<bool> AcquireLifecycleLockAsync(
        AsapDbContext context,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=N'ASAP:ActiveSuperAdminInvariant', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000; SELECT @result;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) >= 0;
    }

    private static Task<Organization?> LockOrganizationAsync(
        AsapDbContext context,
        int id,
        CancellationToken cancellationToken) =>
        context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(cancellationToken);

    private static Task<StaffUser?> LockStaffAsync(
        AsapDbContext context,
        long id,
        CancellationToken cancellationToken) =>
        context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(cancellationToken);

    private static void AddAudit(
        AsapDbContext context,
        CurrentStaff actor,
        long targetId,
        int organizationId,
        string action,
        object details) =>
        context.AdministrativeAudits.Add(new AdministrativeAudit
        {
            ActorStaffUserId = actor.Id,
            ActorName = actor.DisplayName,
            OrganizationId = organizationId,
            Action = action,
            TargetType = "StaffUser",
            TargetId = targetId.ToString(),
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedUtc = DateTime.UtcNow
        });

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeUpn(string? value) => Clean(value)?.ToUpperInvariant();
}
