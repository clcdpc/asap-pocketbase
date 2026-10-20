using System.Diagnostics;
using System.Text.RegularExpressions;
using Asap.Tests.Integration;

namespace Asap.Tests.Assets;

[TestClass]
public sealed class FrontendPublishTests
{
    [TestMethod]
    public async Task CleanAndIncrementalPublishVersionTheActualPayloadWithoutChangingTemplates()
    {
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var scratch = Directory.CreateTempSubdirectory("asap-frontend-publish-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        try
        {
            var frontend = Path.Combine(scratch.FullName, "Frontend");
            foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "src", "Asap.Web", "Frontend"), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(root, "src", "Asap.Web", "Frontend"), source);
                var destination = Path.Combine(frontend, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            File.Copy(Path.Combine(root, "src", "Asap.Web", "Build", "FrontendAssets.targets"),
                Path.Combine(scratch.FullName, "FrontendAssets.targets"));
            await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "Fixture.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Content Remove="Frontend\**\*" /></ItemGroup>
                  <Import Project="FrontendAssets.targets" />
                </Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "Program.cs"), """
                public static class Program { public static void Main() { } }
                """, cancellationToken);
            var template = await File.ReadAllTextAsync(Path.Combine(frontend, "patron", "index.html"), cancellationToken);
            var metadataFixture = Path.Combine(frontend, "shared", "metadata-fixture.js");
            await File.WriteAllTextAsync(metadataFixture, "export const value = 'A';", cancellationToken);
            // Put publish outside the project so subsequent publishes cannot collect it as source.
            var output = Path.Combine(scratch.Parent!.FullName, scratch.Name + "-output");
            try
            {
                await RunAsync(scratch.FullName, cancellationToken, "dotnet", "publish", "Fixture.csproj", "-c", "Release", "-o", output);
                await VerifyAsync(root, output, cancellationToken);
                var firstVersion = ReadVersion(output);
                var htmlTime = File.GetLastWriteTimeUtc(Path.Combine(scratch.FullName, "wwwroot", "staff", "index.html"));

                await RunAsync(scratch.FullName, cancellationToken, "dotnet", "publish", "Fixture.csproj", "-c", "Release", "-o", output);
                await VerifyAsync(root, output, cancellationToken);
                Assert.AreEqual(firstVersion, ReadVersion(output));
                Assert.AreEqual(htmlTime, File.GetLastWriteTimeUtc(Path.Combine(scratch.FullName, "wwwroot", "staff", "index.html")));

                // A copy must reflect content, even when size and source mtime match.
                var sourceTime = File.GetLastWriteTimeUtc(metadataFixture);
                await File.WriteAllTextAsync(metadataFixture, "export const value = 'B';", cancellationToken);
                File.SetLastWriteTimeUtc(metadataFixture, sourceTime);
                await RunAsync(scratch.FullName, cancellationToken, "dotnet", "publish", "Fixture.csproj", "-c", "Release", "--no-build", "-o", output);
                await VerifyAsync(root, output, cancellationToken);
                Assert.AreNotEqual(firstVersion, ReadVersion(output));

                foreach (var relative in new[] { "staff/js/settings.js", "patron/js/bootstrap.js", "shared/http.js", "staff/styles.css" })
                {
                    var previousVersion = ReadVersion(output);
                    await File.AppendAllTextAsync(Path.Combine(frontend, relative), "\n/* Changed frontend fixture */\n", cancellationToken);
                    await RunAsync(scratch.FullName, cancellationToken, "dotnet", "publish", "Fixture.csproj", "-c", "Release", "--no-build", "-o", output);
                    await VerifyAsync(root, output, cancellationToken);
                    Assert.AreNotEqual(previousVersion, ReadVersion(output), relative);
                }
                // Stale generated files must not enter the next payload or its fingerprint.
                var stale = Path.Combine(scratch.FullName, "wwwroot", "staff", "obsolete.js");
                await File.WriteAllTextAsync(stale, "obsolete", cancellationToken);
                await RunAsync(scratch.FullName, cancellationToken, "dotnet", "publish", "Fixture.csproj", "-c", "Release", "--no-build", "-o", output);
                Assert.IsFalse(File.Exists(stale));
                await VerifyAsync(root, output, cancellationToken);
                Assert.AreEqual(template, await File.ReadAllTextAsync(Path.Combine(frontend, "patron", "index.html"), cancellationToken));
                StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(output, "wwwroot", "patron", "index.html"), cancellationToken), "<base href=\"/patron/\">");
            }
            finally
            {
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    internal static Task VerifyAsync(string root, string publish, CancellationToken cancellationToken) =>
        RunAsync(root, cancellationToken, "pwsh", "-NoProfile", "-File", Path.Combine(root, "scripts", "build", "Test-FrontendPublish.ps1"), "-PublishRoot", publish);

    private static string ReadVersion(string publish) => Regex.Match(
        File.ReadAllText(Path.Combine(publish, "wwwroot", "staff", "index.html")), "app.js\\?v=([a-f0-9]{64})").Groups[1].Value;

    internal static async Task RunAsync(string directory, CancellationToken cancellationToken, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.AreEqual(0, process.ExitCode, string.Join(' ', arguments) + Environment.NewLine + await stdout + Environment.NewLine + await stderr);
    }
}
