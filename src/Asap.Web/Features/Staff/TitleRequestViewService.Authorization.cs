using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed partial class TitleRequestViewService
{
    private sealed record RequestReadScope(long Id, int LibraryOrganizationId);

    private static async Task<RequestReadScope?> ResolveRequestReadScopeAsync(
        AsapDbContext context, CurrentStaff staff, string id, CancellationToken cancellationToken)
    {
        var requestId = await RequestLinkResolver.ResolveAsync(context.TitleRequests.Select(item => item.Id), id, cancellationToken);
        if (!requestId.HasValue)
        {
            return null;
        }
        var scope = await context.TitleRequests.AsNoTracking().Where(item => item.Id == requestId.Value)
            .Select(item => new RequestReadScope(item.Id, item.LibraryOrganizationId)).SingleOrDefaultAsync(cancellationToken);
        return scope is not null && CanAccess(staff, scope.LibraryOrganizationId) ? scope : null;
    }

    // Both callers own a serializable transaction and reread the request's library
    // after waiting for these locks. A preliminary scope check is not authorization.
    private async Task<CurrentStaff?> LockAndRevalidateRequestReaderAsync(
        AsapDbContext context, CurrentStaff staff, RequestReadScope request, CancellationToken cancellationToken)
    {
        var lockedIds = new[] { LibraryScope.SystemOrganizationId, staff.OrganizationId, request.LibraryOrganizationId }.ToHashSet();
        if (!await StaffEligibilityService.LockOrganizationsAsync(context, lockedIds, cancellationToken))
        {
            return null;
        }
        var current = await staffEligibility.RevalidateLockedAsync(context, staff, request.LibraryOrganizationId,
            StaffRoleRequirement.Any, requireActorParticipation: true, lockedIds, cancellationToken);
        return current.Outcome == StaffEligibilityOutcome.Allowed && current.Staff!.Role == staff.Role &&
            current.Staff.OrganizationId == staff.OrganizationId ? current.Staff : null;
    }

    private static async Task<(bool Allowed, int? OrganizationId)> ResolveRelatedReadScopeAsync(
        AsapDbContext context, CurrentStaff staff, int libraryId, LibraryScope? scope, bool searchResult,
        CancellationToken cancellationToken)
    {
        // Search and explicitly selected libraries require active participation;
        // ordinary details also serve authorized historical/recovery access.
        if (!CanAccess(staff, libraryId) || !await context.Organizations.AsNoTracking().AnyAsync(item =>
                item.Id == libraryId && item.Id > LibraryScope.SystemOrganizationId &&
                item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId && (!searchResult || item.IsActive),
                cancellationToken))
        {
            return (false, null);
        }
        int? relatedOrganizationId = staff.Role == StaffRole.SuperAdmin ? null : staff.OrganizationId;
        if (staff.Role == StaffRole.SuperAdmin && scope is { Kind: not LibraryScopeKind.All } selectedScope)
        {
            if (selectedScope.Kind != LibraryScopeKind.Library || selectedScope.OrganizationId != libraryId ||
                !await context.Organizations.AsNoTracking().AnyAsync(item => item.Id == selectedScope.OrganizationId &&
                    item.Id > LibraryScope.SystemOrganizationId && item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId && item.IsActive,
                    cancellationToken))
            {
                return (false, null);
            }
            relatedOrganizationId = selectedScope.OrganizationId;
        }
        return (true, relatedOrganizationId);
    }
}
