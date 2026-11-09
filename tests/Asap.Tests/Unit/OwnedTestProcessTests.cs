using System.Diagnostics;
using System.Text.Json;

namespace Asap.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class OwnedTestProcessTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(23)]
    public async Task PreservesArgumentsEnvironmentDirectoryStreamsAndExitCode(int code)
    {
        var arguments = new[] { "", "two words", "quotes\"'", "é/資料", "trailing\\", "$(literal)" };
        var command = Node("""
            console.log(JSON.stringify({ args: process.argv.slice(1), sentinel: process.env.ASAP_PROCESS_SENTINEL,
              cwd: process.cwd(), timezone: process.env.TZ ?? null }));
            console.error('independent stderr');
            process.exitCode = Number(process.env.ASAP_PROCESS_EXIT);
            """, arguments);
        command.Environment["ASAP_PROCESS_SENTINEL"] = "sentinel value";
        command.Environment["ASAP_PROCESS_EXIT"] = code.ToString();
        command.Environment.Remove("TZ");
        if (code == 0)
        {
            command.Environment["TZ"] = "America/New_York";
        }
        var result = await OwnedTestProcess.RunAsync(command);
        Assert.AreEqual(code, result.ExitCode);
        using var json = JsonDocument.Parse(result.Stdout);
        CollectionAssert.AreEqual(arguments, json.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.AreEqual("sentinel value", json.RootElement.GetProperty("sentinel").GetString());
        Assert.AreEqual(code == 0 ? "America/New_York" : null, json.RootElement.GetProperty("timezone").GetString());
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(command.WorkingDirectory)), json.RootElement.GetProperty("cwd").GetString());
        Assert.AreEqual("independent stderr", result.Stderr.Trim());
    }

    [TestMethod]
    public async Task DrainsBothStreamsBeyondPipeCapacity()
    {
        var result = await OwnedTestProcess.RunAsync(Node("""
            const { once } = require('node:events');
            async function write(stream, character) {
              for (let i=0;i<256;i++) {
                if (!stream.write(character.repeat(8192))) await once(stream, 'drain');
              }
            }
            Promise.all([write(process.stdout, 'o'), write(process.stderr, 'e')]).catch(() => { process.exitCode=1; });
            """));
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(new string('o', 2 * 1024 * 1024), result.Stdout);
        Assert.AreEqual(new string('e', 2 * 1024 * 1024), result.Stderr);
    }

    [TestMethod]
    public async Task StartupFailureIsReportedAndPrecancelledCallDoesNotLaunch()
    {
        var command = new ProcessStartInfo("asap-missing-executable-" + Guid.NewGuid().ToString("N"))
        {
            WorkingDirectory = Environment.CurrentDirectory
        };
        var result = await OwnedTestProcess.RunAsync(command);
        Assert.AreEqual(127, result.ExitCode);
        StringAssert.Contains(result.Stderr, command.FileName);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => OwnedTestProcess.RunAsync(command, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task DeadlineAndCancellationReapDescendantsIncludingExitedLaunchers(bool cancel, bool exitLauncher)
    {
        var directory = Directory.CreateTempSubdirectory("asap-process-regression-");
        var marker = Path.Combine(directory.FullName, "pids");
        using var cancellation = new CancellationTokenSource();
        var command = Node(Tree, marker, "0", exitLauncher ? "exit" : "hang");
        var elapsed = Stopwatch.StartNew();
        var running = OwnedTestProcess.RunAsync(command, cancellation.Token, TimeSpan.FromSeconds(5));
        var pids = Array.Empty<int>();
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (pids.Length != 3)
            {
                Assert.IsFalse(running.IsCompleted, "Fixture must establish all three independently observed PIDs before expiry.");
                pids = ReadPids(marker);
                await Task.Delay(20, startup.Token);
            }
            if (exitLauncher)
            {
                while (IsAlive(pids[0]))
                {
                    await Task.Delay(20, startup.Token);
                }
                if (OperatingSystem.IsLinux())
                {
                    Assert.IsFalse(running.IsCompleted, "A descendant still owns both redirected output handles.");
                }
            }
            // Windows Node may close its inherited pipe endpoint on launcher
            // exit. Even on that path, successful return must kill all remaining
            // job members. Linux retains the handles and exercises drain expiry.
            if (cancel)
            {
                cancellation.Cancel();
            }
            try
            {
                var result = await running;
                Assert.IsTrue(exitLauncher && OperatingSystem.IsWindows(), "A hanging process or retained Linux pipe must reach cancellation/deadline.");
                Assert.AreEqual(0, result.ExitCode);
                StringAssert.Contains(result.Stdout, "partial stdout");
                StringAssert.Contains(result.Stderr, "partial stderr");
            }
            catch (OperationCanceledException error) when (cancel)
            {
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
                StringAssert.Contains(error.Message, "partial stdout");
                StringAssert.Contains(error.Message, "partial stderr");
                Assert.IsFalse(error.Message.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase), error.Message);
            }
            catch (TimeoutException error) when (!cancel)
            {
                StringAssert.Contains(error.Message, "partial stdout");
                StringAssert.Contains(error.Message, "partial stderr");
                Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
                Assert.IsFalse(error.Message.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase), error.Message);
            }
            Assert.IsLessThan(16, elapsed.Elapsed.TotalSeconds, "Operation and its separate cleanup budget must remain bounded.");
            foreach (var pid in pids)
            {
                Assert.IsFalse(IsAlive(pid), $"Owned PID {pid} survived completion.");
            }
        }
        finally
        {
            // Independent fixture cleanup protects the machine even if the
            // helper or an assertion regresses. Do not rely on its cleanup proof.
            cancellation.Cancel();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                // The expected failure has already been asserted above.
            }
            foreach (var pid in ReadPids(marker).Reverse())
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException)
                {
                    // Already reaped.
                }
                catch (InvalidOperationException)
                {
                    // Exit/kill race.
                }
            }
            directory.Delete(recursive: true);
        }
    }

    private static ProcessStartInfo Node(string source, params string[] arguments)
    {
        var command = new ProcessStartInfo("node") { WorkingDirectory = Path.GetTempPath() };
        command.ArgumentList.Add("-e");
        command.ArgumentList.Add(source);
        foreach (var argument in arguments)
        {
            command.ArgumentList.Add(argument);
        }
        return command;
    }

    private static int[] ReadPids(string marker)
    {
        return Enumerable.Range(0, 3).Select(depth => marker + depth + ".pid")
            .Where(File.Exists).Select(file => int.Parse(File.ReadAllText(file))).ToArray();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private const string Tree = """
        (function tree() {
          const fs = require('node:fs'), {spawn} = require('node:child_process');
          const [marker, depth, mode] = process.argv.slice(1);
          fs.writeFileSync(marker+depth+'.pending', String(process.pid));
          fs.renameSync(marker+depth+'.pending', marker+depth+'.pid');
          console.log('partial stdout'); console.error('partial stderr');
          if (Number(depth)<2) spawn(process.execPath, ['-e', '('+tree.toString()+')()', marker, String(Number(depth)+1), mode],
            {stdio:['ignore','inherit','inherit'],windowsHide:true});
          setInterval(()=>{
            if(depth==='0' && mode==='exit' && fs.existsSync(marker+'2.pid')) process.exit(0);
          },20);
        })();
        """;
}
