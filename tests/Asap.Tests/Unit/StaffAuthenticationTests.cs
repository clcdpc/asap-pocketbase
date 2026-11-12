using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Logging;
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
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NLog.Config;
using NLog.Targets;
using NLog.Web;

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
    [DataRow("/staff/\\..\\external", "/staff/")]
    [DataRow("/staff/%2f%2fexample.org", "/staff/")]
    [DataRow("/staff/%252f%252fexample.org", "/staff/")]
    [DataRow("/staff/%5c%5cexample.org", "/staff/")]
    [DataRow("/staff/%0d%0aLocation%3aexample.org", "/staff/")]
    [DataRow("/staff/?filter=%0d%0aLocation%3aexample.org", "/staff/")]
    [DataRow("/staff/?filter=%5cexample.org", "/staff/")]
    [DataRow("/staff/%zz", "/staff/")]
    [DataRow("javascript:alert(1)", "/staff/")]
    [DataRow("/staff/\r\nLocation: https://example.org", "/staff/")]
    [DataRow("/staff/\t?request=42", "/staff/")]
    [DataRow("/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed#copy-task",
        "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed#copy-task")]
    public void StaffReturnUrlAllowsOnlyLocalPaths(string? value, string expected)
    {
        Assert.AreEqual(expected, StaffAuthenticationEndpoints.LocalReturnUrl(value));
    }

    [TestMethod]
    public async Task StaffSignInEndpointCarriesTheValidatedDestinationIntoTheOidcChallenge()
    {
        var configuration = TestConfigurationFactory.Create();
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .UseEnvironment("Testing")
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddStaffAuthentication(configuration, new StubWebHostEnvironment("Testing"));
                    services.AddSingleton<StaffProfileService>(_ => throw new InvalidOperationException(
                        "The sign-in route does not resolve profile services."));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapStaffAuthenticationEndpoints());
                }))
            .StartAsync();
        using var client = host.GetTestClient();
        const string returnUrl = "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed#copy-task";

        using var response = await client.GetAsync(QueryHelpers.AddQueryString(
            "/api/asap/staff/sign-in",
            "returnUrl",
            returnUrl));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var challengeQuery = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var oidc = host.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(StaffAuthenticationRegistration.EntraScheme);
        var challenge = oidc.StateDataFormat!.Unprotect(challengeQuery["state"].ToString());
        Assert.AreEqual(returnUrl, challenge?.RedirectUri,
            "The actual sign-in route carries the complete local task URL in the protected OIDC challenge state.");
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

        var unexpectedRemoteException = CreateThrownRemoteFailure();
        var unexpectedRemoteHttp = NewFailureHttpContext(provider);
        unexpectedRemoteHttp.TraceIdentifier = "remote-correlation-43";
        var unexpectedRemoteFailure = new RemoteFailureContext(
            unexpectedRemoteHttp,
            scheme,
            options,
            unexpectedRemoteException);
        await options.Events.RemoteFailure(unexpectedRemoteFailure);

        Assert.IsTrue(unexpectedRemoteFailure.Result?.Handled);
        Assert.AreEqual("/staff/?signInError=temporary", unexpectedRemoteHttp.Response.Headers.Location.ToString());
        Assert.AreEqual(StaffAuthenticationRegistration.UnexpectedAuthenticationErrorReason,
            StaffAuthenticationRegistration.ClassifyFailure(unexpectedRemoteFailure.Failure));

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
        var authenticationFailedLog = logs.Entries.Single(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("unexpected_authentication_error", StringComparison.Ordinal) &&
            entry.Message.Contains("auth-correlation-42", StringComparison.Ordinal));
        Assert.IsNull(authenticationFailedLog.Exception,
            "OnAuthenticationFailed relies on the framework's exception log rather than duplicating it.");
        var remoteFailureLog = logs.Entries.Single(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("unexpected_authentication_error", StringComparison.Ordinal) &&
            entry.Message.Contains("remote-correlation-43", StringComparison.Ordinal));
        Assert.AreSame(unexpectedRemoteException, remoteFailureLog.Exception,
            "Unexpected remote failures retain the exception object on the application log event.");
        StringAssert.Contains(remoteFailureLog.Exception!.ToString(), "private-remote-detail");
        StringAssert.Contains(remoteFailureLog.Exception.ToString(), "at ");
        Assert.IsFalse(string.Join('\n', logs.Entries.Select(entry => entry.Message))
            .Contains("private-", StringComparison.Ordinal));
        Assert.AreEqual("/staff/?signInError=denied",
            StaffAuthenticationRegistration.FailureRedirectUri(StaffAuthenticationRegistration.StaffNotAuthorizedReason));
        Assert.AreEqual("/staff/?signInError=temporary",
            StaffAuthenticationRegistration.FailureRedirectUri(StaffAuthenticationRegistration.UnexpectedAuthenticationErrorReason));
        const string returnUrl = "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed#copy-task";
        Assert.AreEqual(
            "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed&signInError=cancelled#copy-task",
            StaffAuthenticationRegistration.FailureRedirectUri(
                StaffAuthenticationRegistration.ExternalAuthenticationCancelledReason,
                returnUrl));
        Assert.AreEqual("/base/staff/?signInError=temporary",
            StaffAuthenticationRegistration.FailureRedirectUri(
                StaffAuthenticationRegistration.UnexpectedAuthenticationErrorReason,
                "//evil.example",
                "/base/staff/"));
    }

    [TestMethod]
    public async Task OidcAccessDeniedCallbackReturnsToTheProtectedStaffDestinationWithoutBrowserStorage()
    {
        const string returnUrl = "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed#copy-task";
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
                                new OpenIdConnectChallengeProperties { RedirectUri = returnUrl });
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
        Assert.AreEqual(
            "/staff/?request=9223372036854775807&stage=additional_copies&scope=2&copyStatus=closed&signInError=cancelled#copy-task",
            callback.Headers.Location?.ToString());
    }

    [TestMethod]
    public async Task DisallowedIssuerCallbackLogsOnceAtWarning()
    {
        const string returnUrl = "/staff/?request=9223372036854775807&stage=closed&scope=2#request-notes";
        var previousNLogConfiguration = NLog.LogManager.Configuration;
        var temporaryRoot = Directory.CreateTempSubdirectory("asap-oidc-auth-log-test-");
        try
        {
            NLogConfiguration.Configure(Path.Combine(temporaryRoot.FullName, "logs"), FindRepositoryRoot());
            var nlogConfiguration = NLog.LogManager.Configuration!;
            var capture = new MemoryTarget("oidc-auth-capture")
            {
                Layout = new NLog.Layouts.JsonLayout
                {
                    Attributes =
                    {
                        new NLog.Layouts.JsonAttribute("level", "${level:uppercase=true}"),
                        new NLog.Layouts.JsonAttribute("logger", "${logger}"),
                        new NLog.Layouts.JsonAttribute("eventId", "${event-properties:item=EventId}"),
                        new NLog.Layouts.JsonAttribute("eventName", "${event-properties:item=EventName}"),
                        new NLog.Layouts.JsonAttribute("message", "${message}"),
                        new NLog.Layouts.JsonAttribute("exception", "${exception:format=tostring}")
                    }
                }
            };
            nlogConfiguration.AddTarget(capture);
            foreach (var rule in nlogConfiguration.LoggingRules)
            {
                rule.Targets.Add(capture);
            }
            NLog.LogManager.Configuration = nlogConfiguration;
            NLog.LogManager.ReconfigExistingLoggers();

            var externalConfiguration = TestConfigurationFactory.Create();
            var allowedTenantId = Guid.Parse(externalConfiguration.Authentication.Entra.AllowedTenantIds![0]);
            var allowedIssuer = $"https://login.microsoftonline.com/{allowedTenantId:D}/v2.0";
            var rejectedIssuer = $"https://login.microsoftonline.com/{Guid.NewGuid():D}/v2.0";
            var signingKey = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
            var oidcConfiguration = new OpenIdConnectConfiguration
            {
                Issuer = allowedIssuer,
                AuthorizationEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize",
                TokenEndpoint = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token"
            };
            oidcConfiguration.SigningKeys.Add(signingKey);

            using var host = await new HostBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .UseEnvironment("Testing")
                    .ConfigureServices(services =>
                    {
                        services.AddStaffAuthentication(
                            externalConfiguration,
                            new StubWebHostEnvironment("Testing"));
                        services.PostConfigure<OpenIdConnectOptions>(
                            StaffAuthenticationRegistration.EntraScheme,
                            options => options.ConfigurationManager =
                                new StaticConfigurationManager<OpenIdConnectConfiguration>(oidcConfiguration));
                    })
                    .Configure(app =>
                    {
                        app.UseAuthentication();
                        app.Run(async context =>
                        {
                            if (context.Request.Path == "/challenge")
                            {
                                await context.ChallengeAsync(
                                    StaffAuthenticationRegistration.EntraScheme,
                                    new OpenIdConnectChallengeProperties { RedirectUri = returnUrl });
                                return;
                            }

                            context.Response.StatusCode = StatusCodes.Status404NotFound;
                        });
                    }))
                .UseNLog(new NLogAspNetCoreOptions { RemoveLoggerFactoryFilter = false })
                .StartAsync();

            using var client = host.GetTestClient();
            using var challenge = await client.GetAsync("/challenge");
            Assert.AreEqual(HttpStatusCode.Redirect, challenge.StatusCode);
            var challengeLocation = challenge.Headers.Location!;
            var query = QueryHelpers.ParseQuery(challengeLocation.Query);
            var cookies = challenge.Headers.GetValues("Set-Cookie")
                .Select(value => value.Split(';', 2)[0]);
            var idToken = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: rejectedIssuer,
                audience: externalConfiguration.Authentication.Entra.ClientId,
                claims: [new Claim("sub", "synthetic-issuer-rejection"), new Claim("nonce", query["nonce"].ToString())],
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)));
            using var callbackRequest = new HttpRequestMessage(HttpMethod.Post, "/signin-oidc")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["id_token"] = idToken,
                    ["state"] = query["state"].ToString()
                })
            };
            callbackRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));

            using var callback = await client.SendAsync(callbackRequest);
            Assert.AreEqual(HttpStatusCode.Redirect, callback.StatusCode);
            Assert.AreEqual(
                "/staff/?request=9223372036854775807&stage=closed&scope=2&signInError=denied#request-notes",
                callback.Headers.Location?.ToString());

            NLog.LogManager.Flush();
            var entries = capture.Logs.Select(ParseLogEntry).ToArray();
            Assert.IsFalse(entries.Any(entry =>
                entry.GetProperty("logger").GetString() == "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler" &&
                entry.GetProperty("message").GetString() == "Exception occurred while processing message."));

            var staffRejections = entries.Where(entry =>
                entry.GetProperty("logger").GetString() == "Asap.Web.Features.Staff.StaffAuthentication" &&
                entry.GetProperty("message").GetString()!.Contains(
                    StaffAuthenticationRegistration.TenantNotAllowedReason,
                    StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(1, staffRejections.Length);
            var staffRejection = staffRejections[0];
            Assert.AreEqual("WARN", staffRejection.GetProperty("level").GetString());
        }
        finally
        {
            NLog.LogManager.Flush();
            NLog.LogManager.Configuration = previousNLogConfiguration;
            NLog.LogManager.ReconfigExistingLoggers();
            temporaryRoot.Delete(recursive: true);
        }
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Asap.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static InvalidOperationException CreateThrownRemoteFailure()
    {
        try
        {
            throw new InvalidOperationException("private-remote-detail");
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static JsonElement ParseLogEntry(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.Clone();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedLog> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed record CapturedLog(LogLevel Level, string Category, string Message, Exception? Exception);

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
            entries.Add(new CapturedLog(logLevel, category, formatter(state, exception), exception));
        }
    }
}
