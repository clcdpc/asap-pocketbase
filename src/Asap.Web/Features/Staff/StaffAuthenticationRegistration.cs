using System.Security.Claims;
using Asap.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Asap.Web.Features.Staff;

public static class StaffAuthenticationRegistration
{
    public const string CookieScheme = "AsapStaffCookie";
    public const string EntraScheme = "AsapEntra";
    internal const string TenantNotAllowedReason = "tenant_not_allowed";
    internal const string InvalidIdentityReason = "invalid_identity";
    internal const string StaffNotAuthorizedReason = "staff_not_authorized";
    internal const string ExternalAuthenticationFailedReason = "external_authentication_failed";
    internal const string ExternalAuthenticationCancelledReason = "external_authentication_cancelled";
    internal const string UnexpectedAuthenticationErrorReason = "unexpected_authentication_error";
    private const string SelectorScheme = "AsapStaffSelector";
    private const string FailureReturnUrlItemKey = "AsapStaffFailureReturnUrl";

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
            options.MapInboundClaims = false;
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
            options.Events.OnMessageReceived = context =>
            {
                if (!string.IsNullOrWhiteSpace(context.Properties?.RedirectUri))
                {
                    context.HttpContext.Items[FailureReturnUrlItemKey] = context.Properties.RedirectUri;
                }

                return Task.CompletedTask;
            };
            options.Events.OnAuthenticationFailed = context =>
            {
                HandleAuthenticationFailure(context.HttpContext, context.Exception,
                    exceptionAlreadyLogged: true,
                    returnUrl: context.HttpContext.Items[FailureReturnUrlItemKey] as string);
                context.HandleResponse();
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                HandleAuthenticationFailure(context.HttpContext, context.Failure,
                    exceptionAlreadyLogged: false,
                    returnUrl: context.Properties?.RedirectUri);
                context.HandleResponse();
                return Task.CompletedTask;
            };
            options.Events.OnAccessDenied = context =>
            {
                HandleAuthenticationFailure(
                    context.HttpContext,
                    null,
                    ExternalAuthenticationCancelledReason,
                    returnUrl: context.Properties?.RedirectUri);
                context.HandleResponse();
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                if (principal is null || !TryReadEntraIdentity(principal, out var tenantId, out var objectId))
                {
                    context.Fail(new StaffAuthenticationRejectionException(InvalidIdentityReason));
                    return;
                }

                if (context.SecurityToken is null ||
                    !IssuerMatchesTenant(context.SecurityToken.Issuer, tenantId))
                {
                    context.Fail(new StaffAuthenticationRejectionException(InvalidIdentityReason));
                    return;
                }

                var authenticationEmail = ResolveAuthenticationEmail(principal);
                if (!StaffEmail.TryNormalizeAuthenticationEmail(
                        authenticationEmail,
                        out _,
                        out var normalizedAuthenticationEmail))
                {
                    context.Fail(new StaffAuthenticationRejectionException(InvalidIdentityReason));
                    return;
                }

                var eligibility = context.HttpContext.RequestServices.GetRequiredService<StaffEligibilityService>();
                var result = await eligibility.FindByEmailAsync(
                    normalizedAuthenticationEmail!,
                    tenantId,
                    null,
                    StaffRoleRequirement.Any,
                    requireParticipation: true,
                    context.HttpContext.RequestAborted);
                if (result.Outcome != StaffEligibilityOutcome.Allowed)
                {
                    context.Fail(new StaffAuthenticationRejectionException(StaffNotAuthorizedReason));
                    return;
                }

                var staff = result.Staff!;
                var identity = (ClaimsIdentity)principal.Identity!;
                identity.AddClaim(new Claim(StaffClaims.StaffUserId, staff.Id.ToString()));
                identity.AddClaim(new Claim(StaffClaims.AuthenticationEmail, normalizedAuthenticationEmail!));
                await context.HttpContext.RequestServices.GetRequiredService<StaffSignInService>()
                    .RecordSuccessfulSignInAsync(
                        staff.Id,
                        normalizedAuthenticationEmail!,
                        tenantId,
                        objectId,
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

    internal static string? ResolveAuthenticationEmail(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "email", "preferred_username" })
        {
            if (StaffEmail.TryNormalizeAuthenticationEmail(
                    principal.FindFirstValue(claimType),
                    out var email,
                    out _))
            {
                return email;
            }
        }

        return null;
    }

    internal static bool TryReadEntraIdentity(
        ClaimsPrincipal principal,
        out Guid tenantId,
        out Guid objectId)
    {
        var hasTenantId = Guid.TryParse(principal.FindFirstValue(StaffClaims.TenantId), out tenantId);
        var hasObjectId = Guid.TryParse(principal.FindFirstValue("oid"), out objectId);
        return hasTenantId && hasObjectId && tenantId != Guid.Empty && objectId != Guid.Empty;
    }

    internal static string FailureRedirectUri(string reason, string? returnUrl = null, string fallback = "/staff/")
    {
        return QueryHelpers.AddQueryString(
            StaffAuthenticationEndpoints.LocalReturnUrl(returnUrl, fallback),
            "signInError",
            FailurePresentation(reason));
    }

    internal static string FailurePresentation(string reason) => reason switch
    {
        TenantNotAllowedReason or InvalidIdentityReason or StaffNotAuthorizedReason => "denied",
        ExternalAuthenticationCancelledReason => "cancelled",
        _ => "temporary"
    };

    internal static string ClassifyFailure(Exception? exception, string? fixedReason = null)
    {
        if (fixedReason is not null)
        {
            return fixedReason;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is StaffAuthenticationRejectionException rejection)
            {
                return rejection.Reason;
            }

            if (current is SecurityTokenInvalidIssuerException)
            {
                return TenantNotAllowedReason;
            }

            if (current is OpenIdConnectProtocolException or SecurityTokenException)
            {
                return ExternalAuthenticationFailedReason;
            }
        }

        return exception is null || exception is AuthenticationFailureException { InnerException: null }
            ? ExternalAuthenticationFailedReason
            : UnexpectedAuthenticationErrorReason;
    }

    private static void HandleAuthenticationFailure(
        HttpContext context,
        Exception? exception,
        string? fixedReason = null,
        bool exceptionAlreadyLogged = false,
        string? returnUrl = null)
    {
        var reason = ClassifyFailure(exception, fixedReason);
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Asap.Web.Features.Staff.StaffAuthentication");
        if (reason == ExternalAuthenticationCancelledReason)
        {
            logger.LogInformation(
                "Staff sign-in ended before authentication ({FailureReason}) for correlation {CorrelationId}.",
                reason,
                context.TraceIdentifier);
        }
        else if (reason == UnexpectedAuthenticationErrorReason)
        {
            if (exceptionAlreadyLogged)
            {
                // OpenIdConnectHandler logs this exception and stack before invoking
                // OnAuthenticationFailed. Keep the stable reason and correlation without duplicating it.
                logger.LogError(
                    "Staff authentication failed ({FailureReason}, {ExceptionType}) for correlation {CorrelationId}.",
                    reason,
                    exception?.GetType().Name ?? "unknown",
                    context.TraceIdentifier);
            }
            else
            {
                logger.LogError(
                    exception,
                    "Staff authentication failed ({FailureReason}, {ExceptionType}) for correlation {CorrelationId}.",
                    reason,
                    exception?.GetType().Name ?? "unknown",
                    context.TraceIdentifier);
            }
        }
        else
        {
            logger.LogWarning(
                "Staff authentication was rejected ({FailureReason}) for correlation {CorrelationId}.",
                reason,
                context.TraceIdentifier);
        }

        var path = context.Request.PathBase.Add("/staff/").Value ?? "/staff/";
        context.Response.Redirect(FailureRedirectUri(reason, returnUrl, path));
    }

    private sealed class StaffAuthenticationRejectionException(string reason)
        : AuthenticationFailureException("Staff authentication was rejected.")
    {
        public string Reason { get; } = reason;
    }

    private static Task JsonStatus(RedirectContext<CookieAuthenticationOptions> context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code, message = status == 401 ? "Authentication is required." : "Access is denied." });
    }
}
