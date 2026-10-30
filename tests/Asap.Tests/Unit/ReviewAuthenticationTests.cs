using Asap.ReviewProviders;
using Asap.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Asap.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class ReviewAuthenticationTests
{
    [TestMethod]
    public void BootstrapRequiresOneWellFormedStrongCredential()
    {
        var credential = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Assert.IsTrue(ReviewAuthenticationEndpoint.MatchesCredential(credential, credential));
        foreach (var supplied in new[] { null, "", new string('0', 64), credential + "," + credential,
                     credential[..63], credential + "\n", credential.ToUpperInvariant(), new string('g', 64) })
        {
            Assert.IsFalse(ReviewAuthenticationEndpoint.MatchesCredential(credential, supplied));
        }
        Assert.IsFalse(ReviewAuthenticationEndpoint.MatchesCredential("short", "short"));
        Assert.IsFalse(ReviewAuthenticationEndpoint.MatchesCredential(new string('g', 64), new string('g', 64)));
    }

    [TestMethod]
    public void BootstrapRequiresEveryExplicitReviewRuntimeGate()
    {
        var values = RuntimeEnvironment();
        var environment = new ReviewTestEnvironment();
        Assert.IsTrue(ReviewAuthenticationEndpoint.IsReviewRuntime(environment, name => values.GetValueOrDefault(name)));
        foreach (var name in values.Keys)
        {
            var changed = new Dictionary<string, string>(values);
            changed.Remove(name);
            Assert.IsFalse(ReviewAuthenticationEndpoint.IsReviewRuntime(environment, key => changed.GetValueOrDefault(key)), name);
        }
        values["REVIEW_AUTH_MODE"] = "Entra";
        Assert.IsFalse(ReviewAuthenticationEndpoint.IsReviewRuntime(environment, name => values.GetValueOrDefault(name)));
        values = RuntimeEnvironment();
        foreach (var name in new[] { "Production", "Development", "Review" })
        {
            environment.EnvironmentName = name;
            Assert.IsFalse(ReviewAuthenticationEndpoint.IsReviewRuntime(environment, key => values.GetValueOrDefault(key)));
        }
    }

    [TestMethod]
    public void LocalConfigurationCannotTargetOtherDatabasesOrIdentities()
    {
        Assert.IsTrue(ReviewAuthenticationEndpoint.HasLocalConfiguration(LocalConfiguration()));
        foreach (var change in new Action<ExternalConfiguration>[] {
            c => c.Environment.Name = "Production", c => c.Environment.IsNonProduction = false,
            c => c.EmailSafety.DeliveryMode = "postmark",
            c => c.ConnectionStrings.AsapDatabase = "Server=localhost;Database=Asap;Integrated Security=true",
            c => c.ConnectionStrings.HangfireDatabase = "Server=localhost,1433;Database=Other",
            c => c.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "other@example.org",
            c => c.Authentication.Entra.InitialSuperAdmin.DisplayName = "Other",
            c => c.Authentication.Entra.ClientId = Guid.Empty.ToString(),
            c => c.Authentication.Entra.AllowedTenantIds = [Guid.Empty.ToString()],
            c => c.Authentication.Entra.AllowedTenantIds = [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]
        })
        {
            var configuration = LocalConfiguration();
            change(configuration);
            Assert.IsFalse(ReviewAuthenticationEndpoint.HasLocalConfiguration(configuration));
        }
    }

    [TestMethod]
    public async Task ReviewBoundaryRejectsIdentityHeadersAndBootstrapInputs()
    {
        var values = RuntimeEnvironment();
        var saved = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var credentialPath = Path.Combine(Path.GetTempPath(), $"asap-bootstrap-{Guid.NewGuid():N}");
        var credential = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try
        {
            await File.WriteAllTextAsync(credentialPath, credential);
            foreach (var pair in values)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
            using var services = new ServiceCollection().BuildServiceProvider();
            var builder = new ApplicationBuilder(services);
            new ReviewAuthenticationEndpoint(new ReviewTestEnvironment(), credentialPath).Configure(app => app.Run(context =>
            {
                context.Response.StatusCode = 418;
                return Task.CompletedTask;
            }))(builder);
            var pipeline = builder.Build();
            foreach (var supplied in new[] { null, "", new string('0', 64), credential + "," + credential, "malformed" })
            {
                var context = Request(ReviewAuthenticationEndpoint.SessionPath);
                if (supplied is not null)
                {
                    context.Request.Headers[ReviewAuthenticationEndpoint.CredentialHeader] = supplied;
                }
                await pipeline(context);
                Assert.AreEqual(404, context.Response.StatusCode);
                Assert.IsFalse(context.Response.Headers.ContainsKey("Set-Cookie"));
            }
            foreach (var header in new[] { "X-ASAP-Test-Staff-Id", "x-asap-test-tenant-id", "X-ASAP-Test-Staff-Email" })
            {
                var context = Request("/api/asap/staff/session");
                context.Request.Headers[header] = "arbitrary";
                await pipeline(context);
                Assert.AreEqual(400, context.Response.StatusCode);
            }
            foreach (var change in new Action<HttpContext>[] {
                c => c.Request.QueryString = new QueryString("?role=super_admin"),
                c => c.Request.ContentLength = 1,
                c => c.Features.Set<IHttpRequestBodyDetectionFeature>(new ReviewTestBodyFeature()),
                c => c.Request.Headers.Origin = "https://other.example.org",
                c => c.Request.Headers.TransferEncoding = "chunked"
            })
            {
                var context = Request(ReviewAuthenticationEndpoint.SessionPath);
                change(context);
                await pipeline(context);
                Assert.AreEqual(400, context.Response.StatusCode);
                Assert.IsFalse(context.Response.Headers.ContainsKey("Set-Cookie"));
                Assert.IsFalse(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
            }
            var get = Request(ReviewAuthenticationEndpoint.SessionPath);
            get.Request.Method = "GET";
            await pipeline(get);
            Assert.AreEqual(405, get.Response.StatusCode);
            foreach (var path in new[] { "/api/asap/staff/sign-in", "/signin-oidc" })
            {
                var context = Request(path);
                await pipeline(context);
                Assert.AreEqual(404, context.Response.StatusCode);
            }
            foreach (var mode in new[] { "Entra", "", "local" })
            {
                Environment.SetEnvironmentVariable("REVIEW_AUTH_MODE", mode);
                var context = Request(ReviewAuthenticationEndpoint.SessionPath);
                await pipeline(context);
                Assert.AreEqual(404, context.Response.StatusCode);
            }
        }
        finally
        {
            File.Delete(credentialPath);
            foreach (var pair in saved)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }

    private static DefaultHttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "POST";
        return context;
    }

    private static ExternalConfiguration LocalConfiguration()
    {
        var configuration = TestConfigurationFactory.Create();
        configuration.Environment.Name = "Review";
        configuration.ConnectionStrings.AsapDatabase = "Server=localhost,1433;Database=AsapReview;User ID=asap_review_runtime";
        configuration.ConnectionStrings.HangfireDatabase = configuration.ConnectionStrings.AsapDatabase;
        configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "reviewer@example.org";
        configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "ASAP Reviewer";
        return configuration;
    }

    private static Dictionary<string, string> RuntimeEnvironment() => new()
    {
        ["REVIEW_AUTH_MODE"] = "Local", ["Testing__UseDeterministicPatronProvider"] = "true",
        ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = "Asap.ReviewProviders"
    };

    private sealed class ReviewTestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Asap.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ReviewTestBodyFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
