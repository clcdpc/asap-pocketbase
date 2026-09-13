using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Asap.Web.Features.Staff;

public sealed class TestingStaffAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AsapTestingStaff";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var staffUserId = Request.Headers["X-ASAP-Test-Staff-Id"].ToString();
        var tenantId = Request.Headers["X-ASAP-Test-Tenant-Id"].ToString();
        var objectId = Request.Headers["X-ASAP-Test-Object-Id"].ToString();
        if (string.IsNullOrWhiteSpace(staffUserId) &&
            string.IsNullOrWhiteSpace(tenantId) &&
            string.IsNullOrWhiteSpace(objectId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!long.TryParse(staffUserId, out _) ||
            !Guid.TryParse(tenantId, out _) ||
            !Guid.TryParse(objectId, out _))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid testing staff identity headers."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(StaffClaims.StaffUserId, staffUserId),
                new Claim(StaffClaims.TenantId, tenantId),
                new Claim(StaffClaims.ObjectId, objectId)
            ],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
