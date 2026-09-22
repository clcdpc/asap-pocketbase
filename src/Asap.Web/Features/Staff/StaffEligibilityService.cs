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

public sealed record StaffIdentityEvidence(long StaffUserId, string AuthenticationEmail, Guid TenantId);

public sealed record CurrentStaff(
    long Id,
    string AuthenticationEmail,
    Guid EntraTenantId,
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

    public bool IsAssignmentEligible(StaffUser row, int organizationId) =>
        row.IsActive && HasValidAuthenticationEmail(row) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role is "staff" or "admin" && row.OrganizationId == organizationId);

    public async Task<StaffEligibilityResult> EvaluateAsync(
        StaffIdentityEvidence evidence,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireParticipation,
        CancellationToken cancellationToken)
    {
        if (evidence.TenantId == Guid.Empty ||
            !allowedTenantIds.Contains(evidence.TenantId) ||
            !StaffEmail.TryNormalizeAuthenticationEmail(
                evidence.AuthenticationEmail,
                out _,
                out var normalizedAuthenticationEmail))
        {
            return Invalid();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await LoadAsync(context, evidence.StaffUserId, evidence.TenantId, cancellationToken);
        if (row is null ||
            !string.Equals(
                row.AuthenticationEmail,
                normalizedAuthenticationEmail,
                StringComparison.Ordinal))
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

    public async Task<StaffEligibilityResult> FindByEmailAsync(
        string normalizedAuthenticationEmail,
        Guid tenantId,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireParticipation,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty ||
            !allowedTenantIds.Contains(tenantId) ||
            !StaffEmail.TryNormalizeAuthenticationEmail(
                normalizedAuthenticationEmail,
                out _,
                out var normalizedEmail))
        {
            return Invalid();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var id = await context.StaffUsers.AsNoTracking()
            .Where(item => item.IsActive &&
                           item.NormalizedUserPrincipalName == normalizedEmail)
            .Select(item => (long?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (!id.HasValue)
        {
            return Invalid();
        }

        var row = await LoadAsync(context, id.Value, tenantId, cancellationToken);
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

    // Administrative mutations lock organizations first, then the actor row. Revalidate the
    // cookie evidence on that same transaction so a role, authentication-email, or tenant-scope change cannot
    // authorize a later write merely because the request started with a valid cookie.
    public async Task<StaffEligibilityResult> RevalidateLockedAsync(
        AsapDbContext context,
        CurrentStaff ticket,
        int? requestedOrganizationId,
        StaffRoleRequirement roleRequirement,
        bool requireActorParticipation,
        IReadOnlySet<int> lockedOrganizationIds,
        CancellationToken cancellationToken)
    {
        var row = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {ticket.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null ||
            !row.IsActive ||
            !allowedTenantIds.Contains(ticket.EntraTenantId) ||
            !StaffEmail.TryNormalizeAuthenticationEmail(
                row.UserPrincipalName,
                out _,
                out var normalizedAuthenticationEmail) ||
            !string.Equals(
                normalizedAuthenticationEmail,
                row.NormalizedUserPrincipalName,
                StringComparison.Ordinal) ||
            !string.Equals(
                normalizedAuthenticationEmail,
                ticket.AuthenticationEmail,
                StringComparison.Ordinal))
        {
            return Invalid();
        }

        // The administration caller locks all routing organizations first. Do not issue a
        // fallback organization read here: an actor moved out of scope must fail instead of retaining a
        // shared lock on an organization outside that ordered set.
        if (!lockedOrganizationIds.Contains(row.OrganizationId))
        {
            return Invalid();
        }
        var organization = context.Organizations.Local
            .SingleOrDefault(item => item.Id == row.OrganizationId);
        if (organization is null)
        {
            return Invalid();
        }

        var current = new CurrentStaff(
            row.Id,
            normalizedAuthenticationEmail!,
            ticket.EntraTenantId,
            row.UserPrincipalName,
            row.DisplayName,
            row.NotificationEmail,
            row.Role,
            row.OrganizationId,
            organization.DisplayName,
            organization.IsActive,
            row.WeeklyActionSummaryEnabled,
            row.WeeklyActionSummaryEmail,
            row.PurchaseReminderDefault,
            row.AdditionalCopyReminderDefault,
            row.DefaultMineUnclaimedFilter,
            row.RowVersion);

        var result = await EvaluateLoadedAsync(
            context,
            current,
            requestedOrganizationId,
            roleRequirement,
            requireParticipation: false,
            cancellationToken);
        return result.Outcome == StaffEligibilityOutcome.Allowed &&
               requireActorParticipation &&
               !current.OrganizationIsActive
            ? Forbidden()
            : result;
    }

    public async Task<bool> HasUsableSuperAdminAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await context.StaffUsers.AsNoTracking()
            .Where(item => item.IsActive && item.Role == "super_admin" && item.OrganizationId == 1)
            .Select(item => new { item.UserPrincipalName, item.NormalizedUserPrincipalName })
            .ToListAsync(cancellationToken);
        return candidates.Any(item => HasValidAuthenticationEmail(
            item.UserPrincipalName,
            item.NormalizedUserPrincipalName));
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

    private static async Task<CurrentStaff?> LoadAsync(
        AsapDbContext context,
        long staffUserId,
        Guid authenticationTenantId,
        CancellationToken cancellationToken) =>
        await (from staff in context.StaffUsers.AsNoTracking()
         join organization in context.Organizations.AsNoTracking()
             on staff.OrganizationId equals organization.Id
         where staff.Id == staffUserId && staff.IsActive
         select new { Staff = staff, Organization = organization })
        .SingleOrDefaultAsync(cancellationToken) is { } loaded &&
        StaffEmail.TryNormalizeAuthenticationEmail(
            loaded.Staff.UserPrincipalName,
            out _,
            out var normalizedAuthenticationEmail) &&
        string.Equals(
            normalizedAuthenticationEmail,
            loaded.Staff.NormalizedUserPrincipalName,
            StringComparison.Ordinal)
            ? new CurrentStaff(
                loaded.Staff.Id,
                normalizedAuthenticationEmail!,
                authenticationTenantId,
                loaded.Staff.UserPrincipalName,
                loaded.Staff.DisplayName,
                loaded.Staff.NotificationEmail,
                loaded.Staff.Role,
                loaded.Staff.OrganizationId,
                loaded.Organization.DisplayName,
                loaded.Organization.IsActive,
                loaded.Staff.WeeklyActionSummaryEnabled,
                loaded.Staff.WeeklyActionSummaryEmail,
                loaded.Staff.PurchaseReminderDefault,
                loaded.Staff.AdditionalCopyReminderDefault,
                loaded.Staff.DefaultMineUnclaimedFilter,
                loaded.Staff.RowVersion)
            : null;

    private static bool HasValidAuthenticationEmail(StaffUser row) =>
        HasValidAuthenticationEmail(row.UserPrincipalName, row.NormalizedUserPrincipalName);

    private static bool HasValidAuthenticationEmail(string? email, string? normalizedEmail) =>
        StaffEmail.TryNormalizeAuthenticationEmail(email, out _, out var expectedNormalizedEmail) &&
        string.Equals(expectedNormalizedEmail, normalizedEmail, StringComparison.Ordinal);

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
