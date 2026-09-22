using System.Security.Claims;
using System.Text.Encodings.Web;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Asap.Web.Features.Staff;

public sealed class TestingStaffAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AsapTestingStaff";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var staffUserId = Request.Headers["X-ASAP-Test-Staff-Id"].ToString();
        var tenantId = Request.Headers["X-ASAP-Test-Tenant-Id"].ToString();
        var authenticationEmail = Request.Headers["X-ASAP-Test-Staff-Email"].ToString();
        if (string.IsNullOrWhiteSpace(staffUserId) &&
            string.IsNullOrWhiteSpace(tenantId) &&
            string.IsNullOrWhiteSpace(authenticationEmail))
        {
            return AuthenticateResult.NoResult();
        }

        if (!long.TryParse(staffUserId, out var parsedStaffUserId) ||
            !Guid.TryParse(tenantId, out _))
        {
            return AuthenticateResult.Fail("Invalid testing staff identity headers.");
        }

        if (!StaffEmail.TryNormalizeAuthenticationEmail(authenticationEmail, out _, out var normalizedEmail))
        {
            var contextFactory = Context.RequestServices.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using var context = await contextFactory.CreateDbContextAsync(Context.RequestAborted);
            normalizedEmail = await context.StaffUsers.AsNoTracking()
                .Where(item => item.Id == parsedStaffUserId)
                .Select(item => item.NormalizedUserPrincipalName)
                .SingleOrDefaultAsync(Context.RequestAborted);
            if (!StaffEmail.TryNormalizeAuthenticationEmail(normalizedEmail, out _, out normalizedEmail))
            {
                return AuthenticateResult.Fail("Invalid testing staff identity headers.");
            }
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(StaffClaims.StaffUserId, staffUserId),
                new Claim(StaffClaims.TenantId, tenantId),
                new Claim(StaffClaims.AuthenticationEmail, normalizedEmail!)
            ],
            SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
