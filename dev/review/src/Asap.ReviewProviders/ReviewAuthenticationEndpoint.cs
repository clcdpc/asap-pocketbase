using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Asap.Web.Features.Staff;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Asap.ReviewProviders;

// This assembly is copied only into the disposable review image, never Web's
// publish output. No identity input is accepted from the bootstrap caller.
internal sealed class ReviewAuthenticationEndpoint(IHostEnvironment environment) : IStartupFilter
{
    internal const string SessionPath = "/_review/auth/session";
    internal const string CredentialHeader = "X-ASAP-Review-Auth";
    private readonly string credentialPath = "/review/auth/credential";

    // Tests use a temporary credential file; runtime DI uses the fixed mount.
    internal ReviewAuthenticationEndpoint(IHostEnvironment environment, string credentialPath) : this(environment)
    {
        this.credentialPath = credentialPath;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            // Testing headers remain supported by lower-level application tests,
            // but must never authenticate callers of the published review port.
            if (context.Request.Headers.Keys.Any(key => key.StartsWith("X-ASAP-Test-", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var local = Environment.GetEnvironmentVariable("REVIEW_AUTH_MODE") == "Local";
            if (local && (context.Request.Path == "/api/asap/staff/sign-in" || context.Request.Path == "/signin-oidc"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (context.Request.Path != SessionPath)
            {
                await following(context);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (!IsReviewRuntime(environment, Environment.GetEnvironmentVariable))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            // No CORS, query parameters, body, or browser-origin requests. The
            // credential is sent only by Playwright's trusted APIRequestContext.
            if (context.Request.QueryString.HasValue || context.Request.ContentLength is > 0 ||
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true ||
                context.Request.Headers.ContainsKey("Transfer-Encoding") || context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var expected = await File.ReadAllTextAsync(credentialPath, timeout.Token);
                var supplied = context.Request.Headers[CredentialHeader];
                if (supplied.Count != 1 || !MatchesCredential(expected, supplied[0]))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                var configuration = context.RequestServices.GetRequiredService<ExternalConfiguration>();
                if (!HasLocalConfiguration(configuration))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                var provider = context.RequestServices.GetRequiredService<DeterministicTestingPatronProvider>();
                if (!ReferenceEquals(provider, context.RequestServices.GetRequiredService<IPatronProvider>()) ||
                    !ReferenceEquals(provider, context.RequestServices.GetRequiredService<IStaffPolarisProvider>()) ||
                    !ReferenceEquals(provider, context.RequestServices.GetRequiredService<IPolarisReferenceProvider>()))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                // Reuse the live fixed SQL/provider isolation proof, including
                // SQL Server 2022 Developer, runtime login and Review Library.
                await AcceptanceEvidence.ReadAsync(timeout.Token);
                await CreateSessionAsync(context, configuration, timeout.Token);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Never log supplied credentials, cookies, SQL or exception text.
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
        });
        next(app);
    };

    internal static bool IsReviewRuntime(IHostEnvironment environment, Func<string, string?> readEnvironment) =>
        environment.IsEnvironment("Testing") &&
        readEnvironment("REVIEW_AUTH_MODE") == "Local" &&
        readEnvironment("Testing__UseDeterministicPatronProvider") == "true" &&
        readEnvironment("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES") == "Asap.ReviewProviders" &&
        Regex.IsMatch(readEnvironment("REVIEW_PROJECT_NAME") ?? "", "^asap-(acceptance-[a-f0-9]{24}|review-[a-f0-9]{12})$");

    internal static bool MatchesCredential(string expected, string? supplied) =>
        expected.Length == 64 && supplied?.Length == 64 &&
        expected.All(char.IsAsciiHexDigitLower) && supplied.All(char.IsAsciiHexDigitLower) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));

    internal static bool HasLocalConfiguration(ExternalConfiguration configuration)
    {
        var sql = new SqlConnectionStringBuilder(configuration.ConnectionStrings.AsapDatabase);
        var entra = configuration.Authentication.Entra;
        return configuration.Environment.Name == "Review" && configuration.Environment.IsNonProduction == true &&
            configuration.EmailSafety.DeliveryMode == "capture" &&
            sql.DataSource == "sql,1433" && sql.InitialCatalog == "AsapReview" &&
            sql.UserID == "asap_review_runtime" && !sql.IntegratedSecurity &&
            configuration.ConnectionStrings.AsapDatabase == configuration.ConnectionStrings.HangfireDatabase &&
            Guid.TryParse(entra.ClientId, out var client) && client != Guid.Empty &&
            entra.AllowedTenantIds is { Count: 1 } && Guid.TryParse(entra.AllowedTenantIds[0], out var tenant) && tenant != Guid.Empty &&
            entra.InitialSuperAdmin.UserPrincipalName == "reviewer@example.org" &&
            entra.InitialSuperAdmin.DisplayName == "ASAP Reviewer";
    }

    internal static async Task CreateSessionAsync(HttpContext context, ExternalConfiguration configuration, CancellationToken cancellationToken)
    {
        var entra = configuration.Authentication.Entra;
        var eligibility = context.RequestServices.GetRequiredService<StaffEligibilityService>();
        var result = await eligibility.FindByEmailAsync(
            "reviewer@example.org", Guid.Parse(entra.AllowedTenantIds!.Single()), 2,
            StaffRoleRequirement.SuperAdmin, requireParticipation: true, cancellationToken);
        if (result.Outcome != StaffEligibilityOutcome.Allowed || result.Staff is not { OrganizationId: 1, OrganizationIsActive: true } staff)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(StaffClaims.StaffUserId, staff.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(StaffClaims.AuthenticationEmail, staff.AuthenticationEmail),
            new Claim(StaffClaims.TenantId, staff.EntraTenantId.ToString("D"))
        ], StaffAuthenticationRegistration.CookieScheme));
        cancellationToken.ThrowIfCancellationRequested();
        await context.SignInAsync(StaffAuthenticationRegistration.CookieScheme, principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
