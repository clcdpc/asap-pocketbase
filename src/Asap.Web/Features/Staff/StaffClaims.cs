using System.Security.Claims;

namespace Asap.Web.Features.Staff;

public static class StaffClaims
{
    public const string StaffUserId = "asap_staff_user_id";
    public const string TenantId = "tid";
    public const string ObjectId = "oid";

    public static bool TryRead(ClaimsPrincipal principal, out StaffIdentityEvidence evidence)
    {
        evidence = default!;
        return long.TryParse(principal.FindFirstValue(StaffUserId), out var staffUserId) &&
               Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) &&
               Guid.TryParse(principal.FindFirstValue(ObjectId), out var objectId) &&
               (evidence = new StaffIdentityEvidence(staffUserId, tenantId, objectId)) is not null;
    }
}
