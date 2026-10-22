using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;

namespace Asap.Web.Infrastructure.Logging;

public static class NLogConfiguration
{
    public static void Configure(string? configuredLogPath, string contentRootPath)
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

        configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, console);
        configuration.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, file);
        LogManager.Configuration = configuration;
    }
}
