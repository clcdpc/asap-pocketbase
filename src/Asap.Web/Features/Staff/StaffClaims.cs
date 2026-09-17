using System.Security.Claims;

namespace Asap.Web.Features.Staff;

public static class StaffClaims
{
    public const string StaffUserId = "asap_staff_user_id";
    public const string AuthenticationEmail = "asap_authentication_email";
    public const string TenantId = "tid";

    public static bool TryRead(ClaimsPrincipal principal, out StaffIdentityEvidence evidence)
    {
        evidence = default!;
        var emailClaim = principal.FindFirstValue(AuthenticationEmail);
        return long.TryParse(principal.FindFirstValue(StaffUserId), out var staffUserId) &&
               Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) &&
               StaffEmail.TryNormalizeAuthenticationEmail(emailClaim, out _, out var normalizedEmail) &&
               (evidence = new StaffIdentityEvidence(staffUserId, normalizedEmail!, tenantId)) is not null;
    }
}
