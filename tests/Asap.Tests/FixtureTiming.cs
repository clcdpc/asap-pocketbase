using System.Diagnostics;
using System.Text.Json;

namespace Asap.Tests;

// Opt-in CI diagnostics. Constants identify operations; no SQL, names or paths are exported.
internal sealed class FixtureTiming : IDisposable
{
    private static readonly object WriteLock = new();
    private readonly string path;
    private readonly string group;
    private readonly string operation;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly long started = Stopwatch.GetTimestamp();
    private bool succeeded;

    private FixtureTiming(string path, string group, string operation)
    {
        this.path = path;
        this.group = group;
        this.operation = operation;
    }

    public static FixtureTiming? Start(string group, string operation)
    {
        var path = Environment.GetEnvironmentVariable("ASAP_CI_FIXTURE_TIMINGS");
        return string.IsNullOrWhiteSpace(path) ? null : new FixtureTiming(path, group, operation);
    }

    public void Complete()
    {
        succeeded = true;
    }

    public void Dispose()
    {
        var finishedUtc = DateTimeOffset.UtcNow;
        var duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        try
        {
            var row = JsonSerializer.Serialize(new
            {
                group,
                operation,
                started_utc = startedUtc,
                timestamp_utc = finishedUtc,
                duration_seconds = duration,
                succeeded
            });
            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, row + "\n");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            // A failed diagnostics sink must neither replace nor hide a fixture/test exception.
        }
    }
}
