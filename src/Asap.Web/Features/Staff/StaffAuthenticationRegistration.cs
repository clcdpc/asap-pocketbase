using System.Security.Claims;
using Asap.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Asap.Web.Features.Staff;

public static class StaffAuthenticationRegistration
{
    public const string CookieScheme = "AsapStaffCookie";
    public const string EntraScheme = "AsapEntra";
    private const string SelectorScheme = "AsapStaffSelector";

    public static IServiceCollection AddStaffAuthentication(
        this IServiceCollection services,
        ExternalConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var entra = configuration.Authentication.Entra;
        var allowedTenants = entra.AllowedTenantIds!.Select(Guid.Parse).ToHashSet();
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SelectorScheme;
            options.DefaultSignInScheme = CookieScheme;
            options.DefaultChallengeScheme = EntraScheme;
        });

        authentication.AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
        {
            options.ForwardDefaultSelector = context =>
                environment.IsEnvironment("Testing") && context.Request.Headers.ContainsKey("X-ASAP-Test-Staff-Id")
                    ? TestingStaffAuthenticationHandler.SchemeName
                    : CookieScheme;
        });
        authentication.AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = "__Host-ASAP.Staff";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => JsonStatus(context, StatusCodes.Status401Unauthorized, "staff_session_invalid");
            options.Events.OnRedirectToAccessDenied = context => JsonStatus(context, StatusCodes.Status403Forbidden, "staff_scope_forbidden");
        });
        authentication.AddOpenIdConnect(EntraScheme, options =>
        {
            options.Authority = "https://login.microsoftonline.com/organizations/v2.0";
            options.ClientId = entra.ClientId!;
            options.ResponseType = OpenIdConnectResponseType.IdToken;
            options.ResponseMode = OpenIdConnectResponseMode.FormPost;
            options.UsePkce = false;
            options.SignInScheme = CookieScheme;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.Scope.Clear();
            options.Scope.Add(OpenIdConnectScope.OpenId);
            options.Scope.Add(OpenIdConnectScope.Profile);
            options.Scope.Add(OpenIdConnectScope.Email);
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                ValidateIssuer(issuer, allowedTenants);
            options.Events.OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                if (!Guid.TryParse(principal.FindFirstValue(StaffClaims.TenantId), out var tenantId) ||
                    !Guid.TryParse(principal.FindFirstValue(StaffClaims.ObjectId), out var objectId))
                {
                    context.Fail("Required Entra identity claims are missing.");
                    return;
                }

                if (!IssuerMatchesTenant(context.SecurityToken.Issuer, tenantId))
                {
                    context.Fail("The Entra issuer does not match the tenant claim.");
                    return;
                }

                var eligibility = context.HttpContext.RequestServices.GetRequiredService<StaffEligibilityService>();
                var result = await eligibility.FindByBindingAsync(
                    tenantId,
                    objectId,
                    null,
                    StaffRoleRequirement.Any,
                    requireParticipation: true,
                    context.HttpContext.RequestAborted);
                if (result.Outcome != StaffEligibilityOutcome.Allowed)
                {
                    context.Fail(result.Code);
                    return;
                }

                var staff = result.Staff!;
                var identity = (ClaimsIdentity)principal.Identity!;
                identity.AddClaim(new Claim(StaffClaims.StaffUserId, staff.Id.ToString()));
                await context.HttpContext.RequestServices.GetRequiredService<StaffSignInService>()
                    .RecordSuccessfulSignInAsync(
                        new StaffIdentityEvidence(staff.Id, tenantId, objectId),
                        principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue(ClaimTypes.Upn),
                        principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name),
                        context.HttpContext.RequestAborted);
            };
        });

        if (environment.IsEnvironment("Testing"))
        {
            authentication.AddScheme<AuthenticationSchemeOptions, TestingStaffAuthenticationHandler>(
                TestingStaffAuthenticationHandler.SchemeName,
                _ => { });
        }

        services.AddAuthorization();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-ASAP-Antiforgery";
            options.Cookie.Name = environment.IsEnvironment("Testing") ? "ASAP-AF" : "__Host-ASAP-AF";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });
        services.AddSingleton<StaffAntiforgeryFilter>();
        return services;
    }

    internal static string ValidateIssuer(string issuer, IReadOnlySet<Guid> allowedTenants)
    {
        if (allowedTenants.Any(tenant => IssuerMatchesTenant(issuer, tenant)))
        {
            return issuer;
        }
        throw new SecurityTokenInvalidIssuerException("The Entra issuer tenant is not allowed.");
    }

    internal static bool IssuerMatchesTenant(string issuer, Guid tenantId) =>
        string.Equals(
            issuer.TrimEnd('/'),
            $"https://login.microsoftonline.com/{tenantId:D}/v2.0",
            StringComparison.OrdinalIgnoreCase);

    private static Task JsonStatus(RedirectContext<CookieAuthenticationOptions> context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code, message = status == 401 ? "Authentication is required." : "Access is denied." });
    }
}
