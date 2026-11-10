using System.Security.Claims;
using System.Net;
using Asap.Web.Features.Staff;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class StaffAuthenticationTests
{
    [TestMethod]
    [DataRow(null, "/staff/")]
    [DataRow("", "/staff/")]
    [DataRow(" /staff/?status=suggestion#request-42 ", "/staff/?status=suggestion#request-42")]
    [DataRow("/", "/")]
    [DataRow("https://example.org/staff/", "/staff/")]
    [DataRow("//example.org/staff/", "/staff/")]
    [DataRow("/\\example.org/staff/", "/staff/")]
    [DataRow("javascript:alert(1)", "/staff/")]
    [DataRow("/staff/\r\nLocation: https://example.org", "/staff/")]
    public void StaffReturnUrlAllowsOnlyLocalPaths(string? value, string expected)
    {
        Assert.AreEqual(expected, StaffAuthenticationEndpoints.LocalReturnUrl(value));
    }

    [TestMethod]
    public void EntraIssuerMustBeAllowedAndMatchTheTenantClaim()
    {
        var allowedTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var allowedIssuer = $"https://login.microsoftonline.com/{allowedTenant:D}/v2.0";
        var allowed = new HashSet<Guid> { allowedTenant };

        Assert.AreEqual(
            allowedIssuer + "/",
            StaffAuthenticationRegistration.ValidateIssuer(allowedIssuer + "/", allowed));
        Assert.IsTrue(StaffAuthenticationRegistration.IssuerMatchesTenant(allowedIssuer, allowedTenant));
        Assert.IsFalse(StaffAuthenticationRegistration.IssuerMatchesTenant(allowedIssuer, otherTenant));
        Assert.ThrowsExactly<SecurityTokenInvalidIssuerException>(() =>
            StaffAuthenticationRegistration.ValidateIssuer(
                $"https://login.microsoftonline.com/{otherTenant:D}/v2.0",
                allowed));
        Assert.ThrowsExactly<SecurityTokenInvalidIssuerException>(() =>
            StaffAuthenticationRegistration.ValidateIssuer(
                $"https://login.microsoftonline.com/{allowedTenant:D}/v1.0",
                allowed));
    }

    [TestMethod]
    public void EntraAuthenticationEmailPrefersEmailAndFallsBackToValidPreferredUsername()
    {
        static ClaimsPrincipal Principal(params Claim[] claims) =>
            new(new ClaimsIdentity(claims, "test"));

        Assert.AreEqual(
            "primary@example.org",
            StaffAuthenticationRegistration.ResolveAuthenticationEmail(Principal(
                new Claim("email", " primary@example.org "),
                new Claim("preferred_username", "fallback@example.org"))));
        Assert.AreEqual(
            "fallback@example.org",
            StaffAuthenticationRegistration.ResolveAuthenticationEmail(Principal(
                new Claim("email", "not-an-email"),
                new Claim("preferred_username", " fallback@example.org "))));
        Assert.IsNull(StaffAuthenticationRegistration.ResolveAuthenticationEmail(Principal(
            new Claim("email", "legacy@staff.asap.local"),
            new Claim("preferred_username", "also-invalid"))));
    }

    [TestMethod]
    public void EntraIdentityRequiresBothNonemptyGuidClaims()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(StaffClaims.TenantId, tenantId.ToString("D")),
            new Claim("oid", objectId.ToString("D"))
        ], "test"));

        Assert.IsTrue(StaffAuthenticationRegistration.TryReadEntraIdentity(principal, out var actualTenant, out var actualObject));
        Assert.AreEqual(tenantId, actualTenant);
        Assert.AreEqual(objectId, actualObject);

        var missingObjectId = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(StaffClaims.TenantId, tenantId.ToString("D"))], "test"));
        Assert.IsFalse(StaffAuthenticationRegistration.TryReadEntraIdentity(missingObjectId, out _, out _));

        var emptyIdentity = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(StaffClaims.TenantId, Guid.Empty.ToString("D")),
            new Claim("oid", objectId.ToString("D"))
        ], "test"));
        Assert.IsFalse(StaffAuthenticationRegistration.TryReadEntraIdentity(emptyIdentity, out _, out _));
    }

    [TestMethod]
    public async Task OidcFailureEventsReturnSafeLocalResponsesAndStableReasonLogs()
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddStaffAuthentication(
            TestConfigurationFactory.Create(),
            new StubWebHostEnvironment("Testing"));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(StaffAuthenticationRegistration.EntraScheme);
        var scheme = new AuthenticationScheme(
            StaffAuthenticationRegistration.EntraScheme,
            StaffAuthenticationRegistration.EntraScheme,
            typeof(OpenIdConnectHandler));

        var issuerHttp = NewFailureHttpContext(provider);
        var issuerFailure = new AuthenticationFailedContext(issuerHttp, scheme, options)
        {
            Exception = new SecurityTokenInvalidIssuerException("private-tenant-detail")
        };
        await options.Events.AuthenticationFailed(issuerFailure);

        Assert.IsTrue(issuerFailure.Result?.Handled);
        Assert.AreEqual("/staff/?signInError=denied", issuerHttp.Response.Headers.Location.ToString());
        Assert.AreEqual(StaffAuthenticationRegistration.TenantNotAllowedReason,
            StaffAuthenticationRegistration.ClassifyFailure(issuerFailure.Exception));

        var remoteHttp = NewFailureHttpContext(provider);
        var remoteFailure = new RemoteFailureContext(
            remoteHttp,
            scheme,
            options,
            new OpenIdConnectProtocolException("private-provider-detail"));
        await options.Events.RemoteFailure(remoteFailure);

        Assert.IsTrue(remoteFailure.Result?.Handled);
        Assert.AreEqual("/staff/?signInError=temporary", remoteHttp.Response.Headers.Location.ToString());
        Assert.AreEqual(StaffAuthenticationRegistration.ExternalAuthenticationFailedReason,
            StaffAuthenticationRegistration.ClassifyFailure(remoteFailure.Failure));

        var deniedHttp = NewFailureHttpContext(provider);
        var accessDenied = new AccessDeniedContext(deniedHttp, scheme, options);
        await options.Events.AccessDenied(accessDenied);

        Assert.IsTrue(accessDenied.Result?.Handled);
        Assert.AreEqual("/staff/?signInError=cancelled", deniedHttp.Response.Headers.Location.ToString());

        var unexpectedHttp = NewFailureHttpContext(provider);
        var unexpectedFailure = new AuthenticationFailedContext(unexpectedHttp, scheme, options)
        {
            Exception = new InvalidOperationException("private-database-detail")
        };
        await options.Events.AuthenticationFailed(unexpectedFailure);

        Assert.IsTrue(unexpectedFailure.Result?.Handled);
        Assert.AreEqual("/staff/?signInError=temporary", unexpectedHttp.Response.Headers.Location.ToString());
        Assert.AreEqual(StaffAuthenticationRegistration.UnexpectedAuthenticationErrorReason,
            StaffAuthenticationRegistration.ClassifyFailure(unexpectedFailure.Exception));
        Assert.IsTrue(logs.Entries.Any(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("tenant_not_allowed", StringComparison.Ordinal) &&
            entry.Message.Contains("auth-correlation", StringComparison.Ordinal)));
        Assert.IsTrue(logs.Entries.Any(entry => entry.Level == LogLevel.Error &&
            entry.Message.Contains("unexpected_authentication_error", StringComparison.Ordinal) &&
            entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal)));
        Assert.IsFalse(string.Join('\n', logs.Entries.Select(entry => entry.Message))
            .Contains("private-", StringComparison.Ordinal));
        Assert.AreEqual("/staff/?signInError=denied",
            StaffAuthenticationRegistration.FailureRedirectUri(StaffAuthenticationRegistration.StaffNotAuthorizedReason));
        Assert.AreEqual("/staff/?signInError=temporary",
            StaffAuthenticationRegistration.FailureRedirectUri(StaffAuthenticationRegistration.UnexpectedAuthenticationErrorReason));
    }

    [TestMethod]
    public async Task OidcAccessDeniedCallbackUsesTheControlledStaffFailurePage()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .UseEnvironment("Testing")
                .ConfigureServices(services => services.AddStaffAuthentication(
                    TestConfigurationFactory.Create(),
                    new StubWebHostEnvironment("Testing")))
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.Run(async context =>
                    {
                        if (context.Request.Path == "/challenge")
                        {
                            await context.ChallengeAsync(
                                StaffAuthenticationRegistration.EntraScheme,
                                new OpenIdConnectChallengeProperties { RedirectUri = "/staff/" });
                            return;
                        }

                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    });
                }))
            .StartAsync();
        using var client = host.GetTestClient();

        using var challenge = await client.GetAsync("/challenge");
        Assert.AreEqual(HttpStatusCode.Redirect, challenge.StatusCode);
        var challengeLocation = challenge.Headers.Location!;
        var query = QueryHelpers.ParseQuery(challengeLocation.Query);
        var cookies = challenge.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';', 2)[0]);
        using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["error"] = "access_denied",
                ["state"] = query["state"].ToString()
            })
        };
        callbackRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));

        using var callback = await client.SendAsync(callbackRequest);

        Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.AreEqual("/staff/?signInError=cancelled", callback.Headers.Location?.ToString());
    }

    [TestMethod]
    public async Task HeaderBasedStaffAuthenticationSchemeExistsOnlyInTesting()
    {
        foreach (var (environmentName, expected) in new[]
                 {
                     ("Testing", true),
                     ("Development", false),
                     ("Production", false)
                 })
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddStaffAuthentication(
                TestConfigurationFactory.Create(),
                new StubWebHostEnvironment(environmentName));
            await using var provider = services.BuildServiceProvider();
            var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
                .GetAllSchemesAsync();

            Assert.AreEqual(
                expected,
                schemes.Any(item => item.Name == TestingStaffAuthenticationHandler.SchemeName),
                environmentName);
            Assert.AreEqual(typeof(CookieAuthenticationHandler),
                schemes.Single(item => item.Name == StaffAuthenticationRegistration.CookieScheme).HandlerType,
                environmentName);
            Assert.AreEqual(typeof(OpenIdConnectHandler),
                schemes.Single(item => item.Name == StaffAuthenticationRegistration.EntraScheme).HandlerType,
                environmentName);
            var options = provider.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
            Assert.AreEqual(StaffAuthenticationRegistration.CookieScheme, options.DefaultSignInScheme, environmentName);
            Assert.AreEqual(StaffAuthenticationRegistration.EntraScheme, options.DefaultChallengeScheme, environmentName);

            var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
                .Get(StaffAuthenticationRegistration.EntraScheme);
            Assert.AreEqual(TestConfigurationFactory.Create().Authentication.Entra.ClientId, oidc.ClientId, environmentName);
            Assert.IsTrue(string.IsNullOrEmpty(oidc.ClientSecret), environmentName);
            Assert.AreEqual(OpenIdConnectResponseType.IdToken, oidc.ResponseType, environmentName);
            Assert.AreEqual(OpenIdConnectResponseMode.FormPost, oidc.ResponseMode, environmentName);
            Assert.IsFalse(oidc.UsePkce, environmentName);
            Assert.IsFalse(oidc.MapInboundClaims, environmentName);
            Assert.IsFalse(oidc.SaveTokens, environmentName);
            Assert.IsFalse(oidc.GetClaimsFromUserInfoEndpoint, environmentName);
            CollectionAssert.AreEquivalent(
                new[] { OpenIdConnectScope.OpenId, OpenIdConnectScope.Profile, OpenIdConnectScope.Email },
                oidc.Scope.ToArray(),
                environmentName);
        }
    }

    private sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Asap.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static DefaultHttpContext NewFailureHttpContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.TraceIdentifier = "auth-correlation-42";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedLog> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Category, string Message);

    private sealed class CapturingLogger(string category, List<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new CapturedLog(logLevel, category, formatter(state, exception)));
        }
    }
}
