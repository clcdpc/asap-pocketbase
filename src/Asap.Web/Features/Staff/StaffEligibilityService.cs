using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public enum StaffEligibilityOutcome
{
    Allowed,
    InvalidIdentity,
    Forbidden
}

public enum StaffRoleRequirement
{
    Any,
    Admin,
    SuperAdmin
}

public sealed record StaffIdentityEvidence(long StaffUserId, Guid TenantId, Guid ObjectId);

public sealed record CurrentStaff(
    long Id,
    Guid EntraTenantId,
    Guid EntraObjectId,
    string? UserPrincipalName,
    string? DisplayName,
    string? NotificationEmail,
    string Role,
    int OrganizationId,
    string OrganizationName,
    bool OrganizationIsActive,
    bool WeeklyActionSummaryEnabled,
    string? WeeklyActionSummaryEmail,
    bool PurchaseReminderDefault,
    bool AdditionalCopyReminderDefault,
    bool DefaultMineUnclaimedFilter,
    byte[] RowVersion);

public sealed record StaffEligibilityResult(
    StaffEligibilityOutcome Outcome,
    CurrentStaff? Staff,
    string Code);

public sealed class StaffEligibilityService(
    IDbContextFactory<AsapDbContext> contextFactory,
    ExternalConfiguration configuration)
{
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();

    public async Task<StaffEligibilityResult> EvaluateAsync(
        StaffIdentityEvidence evidence,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireParticipation,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await LoadAsync(context, evidence.StaffUserId, cancellationToken);
        if (row is null ||
            row.EntraTenantId != evidence.TenantId ||
            row.EntraObjectId != evidence.ObjectId ||
            !allowedTenantIds.Contains(evidence.TenantId))
        {
            return Invalid();
        }

        return await EvaluateLoadedAsync(
            context,
            row,
            requestedOrganizationId,
            roleRequirement,
            requireParticipation,
            cancellationToken);
    }

    public async Task<StaffEligibilityResult> FindByBindingAsync(
        Guid tenantId,
        Guid objectId,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireParticipation,
        CancellationToken cancellationToken)
    {
        if (!allowedTenantIds.Contains(tenantId))
        {
            return Invalid();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var id = await context.StaffUsers.AsNoTracking()
            .Where(item => item.EntraTenantId == tenantId && item.EntraObjectId == objectId)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (!id.HasValue)
        {
            return Invalid();
        }

        var row = await LoadAsync(context, id.Value, cancellationToken);
        return row is null
            ? Invalid()
            : await EvaluateLoadedAsync(
                context,
                row,
                requestedOrganizationId,
                roleRequirement,
                requireParticipation,
                cancellationToken);
    }

    public async Task<bool> HasUsableSuperAdminAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await context.StaffUsers.AsNoTracking()
            .Where(item => item.IsActive && item.Role == "super_admin" && item.OrganizationId == 1)
            .Select(item => new { item.EntraTenantId, item.EntraObjectId })
            .ToListAsync(cancellationToken);
        return candidates.Any(item =>
            item.EntraTenantId.HasValue &&
            item.EntraObjectId.HasValue &&
            allowedTenantIds.Contains(item.EntraTenantId.Value));
    }

    private async Task<StaffEligibilityResult> EvaluateLoadedAsync(
        AsapDbContext context,
        CurrentStaff row,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireParticipation,
        CancellationToken cancellationToken)
    {
        if (!IsValidRoleOrganization(row.Role, row.OrganizationId) ||
            !RoleMeets(row.Role, roleRequirement) ||
            (requestedOrganizationId.HasValue && !CanAccess(row, requestedOrganizationId.Value)))
        {
            return Forbidden();
        }

        if (requireParticipation)
        {
            var participationOrganizationId = requestedOrganizationId ??
                (row.Role == "super_admin" ? 1 : row.OrganizationId);
            var isActive = participationOrganizationId == row.OrganizationId
                ? row.OrganizationIsActive
                : await context.Organizations.AsNoTracking()
                    .Where(item => item.Id == participationOrganizationId)
                    .Select(item => item.IsActive)
                    .SingleOrDefaultAsync(cancellationToken);
            if (!isActive)
            {
                return Forbidden();
            }
        }

        return new StaffEligibilityResult(StaffEligibilityOutcome.Allowed, row, "allowed");
    }

    private static Task<CurrentStaff?> LoadAsync(
        AsapDbContext context,
        long staffUserId,
        CancellationToken cancellationToken) =>
        (from staff in context.StaffUsers.AsNoTracking()
         join organization in context.Organizations.AsNoTracking()
             on staff.OrganizationId equals organization.Id
         where staff.Id == staffUserId && staff.IsActive &&
               staff.EntraTenantId != null && staff.EntraObjectId != null
         select new CurrentStaff(
             staff.Id,
             staff.EntraTenantId!.Value,
             staff.EntraObjectId!.Value,
             staff.UserPrincipalName,
             staff.DisplayName,
             staff.NotificationEmail,
             staff.Role,
             staff.OrganizationId,
             organization.DisplayName,
             organization.IsActive,
             staff.WeeklyActionSummaryEnabled,
             staff.WeeklyActionSummaryEmail,
             staff.PurchaseReminderDefault,
             staff.AdditionalCopyReminderDefault,
             staff.DefaultMineUnclaimedFilter,
             staff.RowVersion))
        .SingleOrDefaultAsync(cancellationToken);

    private static bool IsValidRoleOrganization(string role, int organizationId) =>
        role == "super_admin" ? organizationId == 1 :
        role is "staff" or "admin" && organizationId != 1;

    private static bool RoleMeets(string role, StaffRoleRequirement requirement) => requirement switch
    {
        StaffRoleRequirement.Any => role is "staff" or "admin" or "super_admin",
        StaffRoleRequirement.Admin => role is "admin" or "super_admin",
        StaffRoleRequirement.SuperAdmin => role == "super_admin",
        _ => false
    };

    private static bool CanAccess(CurrentStaff staff, int organizationId) =>
        staff.Role == "super_admin" ||
        (organizationId != 1 && staff.OrganizationId == organizationId);

    private static StaffEligibilityResult Invalid() =>
        new(StaffEligibilityOutcome.InvalidIdentity, null, "staff_session_invalid");

    private static StaffEligibilityResult Forbidden() =>
        new(StaffEligibilityOutcome.Forbidden, null, "staff_scope_forbidden");
}
