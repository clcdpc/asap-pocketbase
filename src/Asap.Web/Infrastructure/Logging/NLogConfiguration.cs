using Microsoft.IdentityModel.Tokens;
using NLog;
using NLog.Config;
using NLog.Filters;
using NLog.Layouts;
using NLog.Targets;

namespace Asap.Web.Infrastructure.Logging;

public static class NLogConfiguration
{
    private const string OpenIdConnectHandlerLogger =
        "Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler";
    private const int OpenIdConnectExceptionProcessingEventId = 17;

    public static void Configure(
        string? configuredLogPath,
        string contentRootPath,
        IReadOnlyDictionary<string, string?>? logLevelOverrides = null)
    {
        var logPath = string.IsNullOrWhiteSpace(configuredLogPath)
            ? Path.Combine(contentRootPath, "logs")
            : configuredLogPath;

        var json = new JsonLayout
        {
            Attributes =
            {
                new JsonAttribute("timestamp", "${date:format=o}"),
                new JsonAttribute("level", "${level:uppercase=true}"),
                new JsonAttribute("logger", "${logger}"),
                new JsonAttribute("correlationId", "${scopeproperty:item=CorrelationId}"),
                new JsonAttribute("message", "${message}"),
                new JsonAttribute("exception", "${exception:format=tostring}")
            }
        };

        var configuration = new LoggingConfiguration();
        var console = new ConsoleTarget("console") { Layout = json };
        var file = new FileTarget("file")
        {
            FileName = Path.Combine(logPath, "asap-${shortdate}.log"),
            Layout = json,
            ArchiveAboveSize = 10 * 1024 * 1024,
            MaxArchiveDays = 30,
            MaxArchiveFiles = 300
        };

        configuration.AddTarget(console);
        configuration.AddTarget(file);
        Target[] targets = [console, file];
        foreach (var (category, level) in (logLevelOverrides ?? new Dictionary<string, string?>())
                     .OrderByDescending(item => item.Key.Length))
        {
            AddRule(configuration, $"{category}*", ParseOverrideLevel(level), targets);
        }

        AddRule(configuration, "Asap.Web.*", NLog.LogLevel.Info, targets);
        AddRule(configuration, "Hangfire.*", NLog.LogLevel.Info, targets);
        AddRule(configuration, "Microsoft.Hosting.Lifetime", NLog.LogLevel.Info, targets);
        AddRule(configuration, "Microsoft.AspNetCore.*", NLog.LogLevel.Warn, targets);
        AddRule(configuration, "Microsoft.EntityFrameworkCore.*", NLog.LogLevel.Warn, targets);
        AddRule(configuration, "System.Net.Http.HttpClient.*", NLog.LogLevel.Warn, targets);
        AddRule(configuration, "*", NLog.LogLevel.Warn, targets);
        foreach (var rule in configuration.LoggingRules.Where(rule => rule.NameMatches(OpenIdConnectHandlerLogger)))
        {
            // OpenIdConnectHandler logs token-validation failures before invoking OnAuthenticationFailed.
            // The app records this expected issuer rejection once at Warning with a stable reason.
            rule.FilterDefaultAction = FilterResult.Log;
            rule.Filters.Add(new WhenMethodFilter(logEvent =>
                IsExpectedIssuerRejection(logEvent) ? FilterResult.Ignore : FilterResult.Neutral));
        }

        LogManager.Configuration = configuration;
    }

    private static NLog.LogLevel ParseOverrideLevel(string? value) => value?.ToUpperInvariant() switch
    {
        "DEBUG" => NLog.LogLevel.Debug,
        "TRACE" => NLog.LogLevel.Trace,
        _ => throw new ArgumentException("Log level overrides must be Debug or Trace.", nameof(value))
    };

    private static void AddRule(
        LoggingConfiguration configuration,
        string categoryPattern,
        NLog.LogLevel minimumLevel,
        IReadOnlyList<Target> targets)
    {
        var rule = new LoggingRule(categoryPattern, minimumLevel, NLog.LogLevel.Fatal, targets[0]);
        for (var index = 1; index < targets.Count; index++)
        {
            rule.Targets.Add(targets[index]);
        }

        rule.Final = true;
        configuration.LoggingRules.Add(rule);
    }

    private static bool IsExpectedIssuerRejection(LogEventInfo logEvent) =>
        string.Equals(logEvent.LoggerName, OpenIdConnectHandlerLogger, StringComparison.Ordinal) &&
        logEvent.Level == NLog.LogLevel.Error &&
        logEvent.Message == "Exception occurred while processing message." &&
        logEvent.Exception is SecurityTokenInvalidIssuerException &&
        logEvent.Properties.TryGetValue("EventId", out var eventId) &&
        eventId is int id && id == OpenIdConnectExceptionProcessingEventId;
}
