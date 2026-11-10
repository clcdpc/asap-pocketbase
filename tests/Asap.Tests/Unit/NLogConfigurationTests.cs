using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Web;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class NLogConfigurationTests
{
    [TestMethod]
    public void MicrosoftAndNlogFiltersSuppressFrameworkNoiseAndKeepOperationalLogs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var temporaryRoot = Directory.CreateTempSubdirectory("asap-nlog-test-");

        try
        {
            var capture = ConfigureCapture(
                Path.Combine(temporaryRoot.FullName, "logs"),
                repositoryRoot,
                null,
                out var configuration);
            var file = configuration.FindTargetByName<FileTarget>("file")!;
            var console = configuration.FindTargetByName<ConsoleTarget>("console")!;
            Assert.AreEqual(30, file.MaxArchiveDays);
            Assert.AreEqual(300, file.MaxArchiveFiles);
            Assert.AreEqual(10 * 1024 * 1024, file.ArchiveAboveSize);
            Assert.IsInstanceOfType<JsonLayout>(console.Layout);
            Assert.IsTrue(configuration.LoggingRules.Count > 0);
            foreach (var rule in configuration.LoggingRules)
            {
                Assert.IsTrue(rule.Targets.Contains(console));
                Assert.IsTrue(rule.Targets.Contains(file));
            }

            using (var application = CreateWebApplication(repositoryRoot, null))
            {
                var loggerFactory = application.Services.GetRequiredService<ILoggerFactory>();
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                    .LogInformation("routine-sql-command");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
                    .LogInformation("request-start-end");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware")
                    .LogInformation("static-file-diagnostic");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware")
                    .LogInformation("endpoint-execution");
                loggerFactory.CreateLogger("System.Net.Http.HttpClient.Polaris.LogicalHandler")
                    .LogInformation("http-client-request");

                var aspNet = loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
                aspNet.LogWarning("framework-warning");
                aspNet.LogError(new InvalidOperationException("framework-stack-detail"), "framework-error");
                var oidcHandler = loggerFactory.CreateLogger(
                    "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler");
                oidcHandler.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    new EventId(17, "ExceptionProcessingMessage"),
                    new SecurityTokenInvalidIssuerException("expected-issuer-detail"),
                    "Exception occurred while processing message.");
                oidcHandler.Log(
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    new EventId(17, "ExceptionProcessingMessage"),
                    new InvalidOperationException("unexpected-oidc-detail"),
                    "Exception occurred while processing message.");
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                    .LogWarning("sql-warning");
                var authenticationLogger = loggerFactory.CreateLogger("Asap.Web.Features.Staff.StaffAuthentication");
                authenticationLogger.LogInformation("first-party-information");
                authenticationLogger.LogWarning("first-party-warning");
                loggerFactory.CreateLogger("Hangfire.Server.BackgroundServerProcess")
                    .LogInformation("hangfire-lifecycle-information");
                loggerFactory.CreateLogger("Microsoft.Hosting.Lifetime")
                    .LogInformation("host-lifecycle-information");
                using (ScopeContext.PushProperty("CorrelationId", "safe-correlation-42"))
                {
                    authenticationLogger.LogWarning(
                        "Staff authentication was rejected ({FailureReason}).",
                        "tenant_not_allowed");
                    authenticationLogger.LogError(
                        new InvalidOperationException("first-party-exception-detail"),
                        "first-party-failure");
                }
            }
            LogManager.Flush();

            var entries = ParseEntries(capture);
            Assert.IsFalse(ContainsMessage(entries, "routine-sql-command"));
            Assert.IsFalse(ContainsMessage(entries, "request-start-end"));
            Assert.IsFalse(ContainsMessage(entries, "static-file-diagnostic"));
            Assert.IsFalse(ContainsMessage(entries, "endpoint-execution"));
            Assert.IsFalse(ContainsMessage(entries, "http-client-request"));
            Assert.IsTrue(ContainsMessage(entries, "framework-warning"));
            Assert.IsTrue(ContainsMessage(entries, "framework-error"));
            var oidcHandlerFailure = entries.Single(entry =>
                entry.GetProperty("logger").GetString() == "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler" &&
                entry.GetProperty("message").GetString() == "Exception occurred while processing message.");
            Assert.AreEqual("ERROR", oidcHandlerFailure.GetProperty("level").GetString());
            StringAssert.Contains(oidcHandlerFailure.GetProperty("exception").GetString()!, "unexpected-oidc-detail");
            Assert.IsFalse(entries.Any(entry => entry.TryGetProperty("exception", out var exception) &&
                exception.GetString()!.Contains("expected-issuer-detail", StringComparison.Ordinal)));
            Assert.IsTrue(ContainsMessage(entries, "sql-warning"));
            Assert.IsTrue(ContainsMessage(entries, "first-party-information"));
            Assert.IsTrue(ContainsMessage(entries, "first-party-warning"));
            Assert.IsTrue(ContainsMessage(entries, "hangfire-lifecycle-information"));
            Assert.IsTrue(ContainsMessage(entries, "host-lifecycle-information"));

            var authenticationFailure = entries.Single(entry =>
                entry.GetProperty("message").GetString() == "Staff authentication was rejected (tenant_not_allowed).");
            Assert.AreEqual("WARN", authenticationFailure.GetProperty("level").GetString());
            Assert.AreEqual("safe-correlation-42", authenticationFailure.GetProperty("correlationId").GetString());
            var appFailure = entries.Single(entry => entry.GetProperty("message").GetString() == "first-party-failure");
            Assert.AreEqual("ERROR", appFailure.GetProperty("level").GetString());
            Assert.AreEqual("Asap.Web.Features.Staff.StaffAuthentication", appFailure.GetProperty("logger").GetString());
            Assert.AreEqual("safe-correlation-42", appFailure.GetProperty("correlationId").GetString());
            StringAssert.Contains(appFailure.GetProperty("exception").GetString()!, "first-party-exception-detail");
            Assert.IsTrue(entries.All(entry => entry.TryGetProperty("timestamp", out var timestamp) &&
                DateTimeOffset.TryParse(timestamp.GetString(), out _)), "Every emitted entry remains valid structured JSON with a timestamp.");

            var options = new DbContextOptionsBuilder<AsapDbContext>()
                .UseSqlServer("Server=localhost;Database=AsapLoggingTests;Integrated Security=True;TrustServerCertificate=True")
                .Options;
            var coreOptions = options.Extensions.OfType<CoreOptionsExtension>().Single();
            Assert.IsFalse(coreOptions.IsSensitiveDataLoggingEnabled);

            var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.EntityFrameworkCore.Database.Command"] = "Debug",
                ["Microsoft.AspNetCore.Hosting.Diagnostics"] = "Debug",
                ["Asap.Web.Features.Staff.StaffAuthentication"] = "Trace"
            };
            var overrideCapture = ConfigureCapture(
                Path.Combine(temporaryRoot.FullName, "override-logs"),
                repositoryRoot,
                overrides,
                out _);
            using (var application = CreateWebApplication(repositoryRoot, overrides))
            {
                var loggerFactory = application.Services.GetRequiredService<ILoggerFactory>();
                loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                    .LogDebug("targeted-sql-debug");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
                    .LogDebug("targeted-request-debug");
                loggerFactory.CreateLogger("Asap.Web.Features.Staff.StaffAuthentication")
                    .LogTrace("targeted-application-trace");
                loggerFactory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware")
                    .LogInformation("unselected-endpoint-information");
            }
            LogManager.Flush();

            var overrideEntries = ParseEntries(overrideCapture);
            Assert.IsTrue(ContainsMessage(overrideEntries, "targeted-sql-debug"));
            Assert.IsTrue(ContainsMessage(overrideEntries, "targeted-request-debug"));
            Assert.IsTrue(ContainsMessage(overrideEntries, "targeted-application-trace"));
            Assert.IsFalse(ContainsMessage(overrideEntries, "unselected-endpoint-information"));
        }
        finally
        {
            LogManager.Flush();
            LogManager.Configuration = null;
            temporaryRoot.Delete(recursive: true);
        }
    }

    private static MemoryTarget ConfigureCapture(
        string logPath,
        string repositoryRoot,
        IReadOnlyDictionary<string, string?>? overrides,
        out LoggingConfiguration configuration)
    {
        NLogConfiguration.Configure(logPath, repositoryRoot, overrides);
        configuration = LogManager.Configuration!;
        var console = configuration.FindTargetByName<ConsoleTarget>("console")!;
        var capture = new MemoryTarget("capture") { Layout = console.Layout };
        configuration.AddTarget(capture);
        foreach (var rule in configuration.LoggingRules)
        {
            rule.Targets.Add(capture);
        }

        LogManager.Configuration = configuration;
        LogManager.ReconfigExistingLoggers();
        return capture;
    }

    private static WebApplication CreateWebApplication(
        string repositoryRoot,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        var contentRoot = Path.Combine(repositoryRoot, "src", "Asap.Web");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            EnvironmentName = "Testing"
        });
        if (overrides is { Count: > 0 })
        {
            builder.Configuration.AddInMemoryCollection(overrides.Select(item =>
                new KeyValuePair<string, string?>($"Logging:LogLevel:{item.Key}", item.Value)));
        }

        builder.Logging.ClearProviders();
        builder.Host.UseNLog(new NLogAspNetCoreOptions { RemoveLoggerFactoryFilter = false });
        return builder.Build();
    }

    private static List<JsonElement> ParseEntries(MemoryTarget target) => target.Logs
        .Select(message => JsonDocument.Parse(message).RootElement.Clone())
        .ToList();

    private static bool ContainsMessage(IEnumerable<JsonElement> entries, string message) =>
        entries.Any(entry => entry.GetProperty("message").GetString() == message);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Asap.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
