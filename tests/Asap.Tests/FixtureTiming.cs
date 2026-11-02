using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asap.Tests;

// Opt-in CI diagnostics. Constants identify operations; no SQL, names or paths are exported.
internal sealed class FixtureTiming : IDisposable
{
    private static readonly object WriteLock = new();
    private static readonly AsyncLocal<FixtureTiming?> Current = new();
    private readonly FixtureTiming? parent;
    private readonly string span = Guid.NewGuid().ToString("N");
    private readonly string? test;
    private readonly string path;
    private readonly string group;
    private readonly string operation;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly long started = Stopwatch.GetTimestamp();
    private bool succeeded;
    private int disposed;

    private FixtureTiming(string path, string group, string operation, string? test)
    {
        parent = Current.Value;
        Current.Value = this;
        this.test = test is null ? parent?.test : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(test))).ToLowerInvariant();
        this.path = path;
        this.group = group;
        this.operation = operation;
    }

    public static FixtureTiming? Start(string group, string operation, string? test = null)
    {
        var path = Environment.GetEnvironmentVariable("ASAP_CI_FIXTURE_TIMINGS");
        return string.IsNullOrWhiteSpace(path) ? null : new FixtureTiming(path, group, operation, test);
    }

    public void Complete()
    {
        succeeded = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        Current.Value = parent;
        var finishedUtc = DateTimeOffset.UtcNow;
        var duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        try
        {
            var row = JsonSerializer.Serialize(new
            {
                group,
                operation,
                span,
                parent_span = parent?.span,
                test_id = test,
                process_id = Environment.ProcessId,
                started_ticks = started,
                finished_ticks = Stopwatch.GetTimestamp(),
                tick_frequency = Stopwatch.Frequency,
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
