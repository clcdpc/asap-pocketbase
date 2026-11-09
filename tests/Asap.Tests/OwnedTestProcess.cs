using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Asap.Tests;

// Windows gates a Node launcher until its job is assigned. Linux uses a
// dedicated Python subreaper, so detached Chromium groups remain owned without
// changing the test application's process-wide child/adoption policy.
internal static class OwnedTestProcess
{
    private const string Launcher = """
        const { spawn } = require('node:child_process');
        const [executable, args] = JSON.parse(process.argv[1]);
        process.stdin.once('data', () => {
          process.stdin.destroy();
          const child = spawn(executable, args, { stdio: ['ignore', 'inherit', 'inherit'], windowsHide: true });
          child.once('error', error => { console.error(error.message); process.exitCode = 127; });
          child.once('exit', (code, signal) => { process.exitCode = signal ? 1 : code; });
        });
        """;
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(10);
    private const string LinuxLauncher = """
        import ctypes, json, os, signal, subprocess, sys, time

        stopping = False
        def stop(_signal, _frame):
            global stopping
            stopping = True

        libc = ctypes.CDLL(None, use_errno=True)
        if libc.prctl(36, 1, 0, 0, 0) != 0: # PR_SET_CHILD_SUBREAPER
            raise OSError(ctypes.get_errno(), 'Could not establish owned child reaping')
        signal.signal(signal.SIGTERM, stop)
        signal.signal(signal.SIGINT, stop)
        parent = os.getppid()
        os.setsid()

        def main():
            if sys.stdin.readline().strip() != 'start' or stopping:
                return 125
            executable, arguments = json.loads(sys.argv[1])
            try:
                target = subprocess.Popen([executable, *arguments], stdin=subprocess.DEVNULL)
            except OSError as error:
                print(str(error), file=sys.stderr, flush=True)
                return 127
            root_code = None
            while True:
                # This process creates only the requested target. Every adopted
                # child belongs to that tree, regardless of session/group changes.
                while True:
                    try:
                        pid, status = os.waitpid(-1, os.WNOHANG)
                    except ChildProcessError:
                        return root_code if root_code is not None else 125
                    if pid == 0:
                        break
                    if pid == target.pid:
                        root_code = os.waitstatus_to_exitcode(status)
                        target.returncode = root_code
                if stopping or root_code is not None or os.getppid() != parent:
                    with open('/proc/self/task/' + str(os.getpid()) + '/children') as children:
                        owned = [int(pid) for pid in children.read().split()]
                    for pid in owned:
                        try:
                            os.kill(pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    # Killing an intermediate owner adopts its descendants here.
                    # Continue until waitpid explicitly reports no children.
                time.sleep(0.02)

        code = main()
        sys.exit(code if code >= 0 else 128 - code)
        """;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo command, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(4));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var stopDrains = new CancellationTokenSource();
        using var job = OperatingSystem.IsWindows() ? CreateJob() : null;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Owned test processes require Windows or Linux.");
        }

        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "node" : "python3")
        {
            WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var entry in command.Environment)
        {
            start.Environment[entry.Key] = entry.Value;
        }
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "-e" : "-c");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? Launcher : LinuxLauncher);
        if (!string.IsNullOrEmpty(command.Arguments))
        {
            throw new ArgumentException("Owned test commands must use ArgumentList.", nameof(command));
        }
        start.ArgumentList.Add(JsonSerializer.Serialize(new object[] { command.FileName, command.ArgumentList.ToArray() }));

        using var process = new Process { StartInfo = start };
        var stdout = new Capture();
        var stderr = new Capture();
        Task drains = Task.CompletedTask;
        Exception? failure = null;
        string? cleanupFailure = null;
        var started = false;
        var exitCode = -1;
        try
        {
            started = process.Start();
            if (!started)
            {
                throw new InvalidOperationException("Could not start the owned test launcher.");
            }
            if (job is not null)
            {
                CheckNative(AssignProcessToJobObject(job, process.Handle));
            }
            drains = Task.WhenAll(stdout.DrainAsync(process.StandardOutput, stopDrains.Token),
                stderr.DrainAsync(process.StandardError, stopDrains.Token));
            await process.StandardInput.WriteLineAsync("start".AsMemory(), operation.Token);
            process.StandardInput.Close();
            // WaitForExit alone does not cover inherited output handles. The
            // complete exit-and-drain operation shares the caller/deadline token.
            await Task.WhenAll(process.WaitForExitAsync(operation.Token), drains).WaitAsync(operation.Token);
            exitCode = process.ExitCode;
        }
        catch (Exception error)
        {
            failure = error;
        }

        if (started)
        {
            using var cleanup = new CancellationTokenSource(CleanupBudget);
            try
            {
                process.StandardInput.Close();
                if (job is not null)
                {
                    CheckNative(TerminateJobObject(job, 1));
                }
                else if (!process.HasExited)
                {
                    CheckNative(kill(process.Id, 15) == 0 || Marshal.GetLastPInvokeError() == 3);
                    try
                    {
                        await process.WaitForExitAsync(cleanup.Token).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        cleanupFailure = "Linux child reaper did not finish; forcing its remaining process tree to stop.";
                        process.Kill(entireProcessTree: true);
                    }
                }
                if (!process.HasExited)
                {
                    // Also covers failure before the launcher entered its group/job.
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                }
                await process.WaitForExitAsync(cleanup.Token);
                // Linux's dedicated reaper exits only after waitpid reports
                // ECHILD for its complete tree, including detached/adopted PIDs.
                // A forced fallback above is always reported as cleanup failure.
                while (job is not null && HasOwnedProcesses(job))
                {
                    await Task.Delay(20, cleanup.Token);
                }
                await drains.WaitAsync(cleanup.Token);
            }
            catch (Exception error)
            {
                cleanupFailure = error.ToString();
            }
            finally
            {
                await stopDrains.CancelAsync();
                // A failed cleanup must not leave a faulted drain task
                // unobserved, or wait without a budget for an inherited handle.
                _ = drains.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        var output = stdout.Text;
        var errors = stderr.Text;
        var diagnostics = $"{command.FileName}{Environment.NewLine}stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{errors}";
        if (cleanupFailure is not null)
        {
            diagnostics += $"{Environment.NewLine}Owned process cleanup failed: {cleanupFailure}";
        }
        if (failure is OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException($"Caller cancelled {diagnostics}", failure, cancellationToken);
            }
            throw new TimeoutException($"Process deadline expired: {diagnostics}", failure);
        }
        if (failure is not null || cleanupFailure is not null)
        {
            throw new InvalidOperationException($"Test process failed: {diagnostics}", failure);
        }
        return (exitCode, output, errors);
    }

    private sealed class Capture
    {
        private readonly StringBuilder buffer = new();
        private bool truncated;
        internal string Text
        {
            get
            {
                lock (buffer)
                {
                    return buffer + (truncated ? "\n[output truncated after 4 Mi characters]" : "");
                }
            }
        }
        internal async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var chunk = new char[8192];
            while (true)
            {
                var count = await reader.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    return;
                }
                lock (buffer)
                {
                    var retain = Math.Min(count, 4 * 1024 * 1024 - buffer.Length);
                    buffer.Append(chunk, 0, retain);
                    truncated |= retain != count;
                }
            }
        }
    }

    private static bool HasOwnedProcesses(SafeFileHandle job)
    {
        CheckNative(QueryInformationJobObject(job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
        return accounting.ActiveProcesses != 0;
    }

    private static SafeFileHandle CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        CheckNative(!job.IsInvalid);
        try
        {
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            CheckNative(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()));
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static void CheckNative(bool succeeded)
    {
        if (!succeeded)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        internal BasicLimits Basic;
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobAccounting information, int size, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
