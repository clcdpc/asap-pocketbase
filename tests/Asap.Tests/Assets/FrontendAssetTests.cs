using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Asap.Tests.Assets;

[TestClass]
public sealed class FrontendAssetTests
{
    [TestMethod]
    public void VendoredFilesMatchRecordedHashesAndRequiredVersions()
    {
        var root = FindRepoRoot();
        var frontendRoot = Path.Combine(root, "src", "Asap.Web", "Frontend");
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(frontendRoot, "vendor-manifest.json")));

        var packages = manifest.RootElement.GetProperty("packages").EnumerateArray().ToList();
        CollectionAssert.AreEquivalent(
            new[] { "bootstrap@4.1.3", "font-awesome@4.7.0", "gridjs@6.2.0" },
            packages.Select(package =>
                $"{package.GetProperty("name").GetString()}@{package.GetProperty("version").GetString()}").ToArray());

        foreach (var package in packages)
        {
            foreach (var file in package.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(frontendRoot, file.Name.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), $"Missing vendored file: {file.Name}");
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                Assert.AreEqual(file.Value.GetString(), actual, $"Hash mismatch: {file.Name}");
            }
        }
    }

    [TestMethod]
    public void FontAwesomeTransitiveFontAssetsArePresent()
    {
        var root = FindRepoRoot();
        var cssPath = Path.Combine(
            root,
            "src",
            "Asap.Web",
            "Frontend",
            "vendor",
            "font-awesome",
            "4.7.0",
            "css",
            "font-awesome.css");
        var css = File.ReadAllText(cssPath);
        var fontsRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cssPath)!, "..", "fonts"));

        foreach (var extension in new[] { "eot", "svg", "ttf", "woff", "woff2" })
        {
            Assert.IsTrue(css.Contains($"fontawesome-webfont.{extension}", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(Path.Combine(fontsRoot, $"fontawesome-webfont.{extension}")));
        }
    }

    [TestMethod]
    public void BuildTargetsDoNotInvokeNodeOrNpm()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Asap.Web",
            "Asap.Web.csproj"));

        Assert.IsFalse(project.Contains("<Exec", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(project.Contains("node ", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(project.Contains("npm ", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task GitCheckoutPreservesVendorBytesWhenAutoCrlfIsEnabled()
    {
        var root = FindRepoRoot();
        var frontendRoot = Path.Combine(root, "src", "Asap.Web", "Frontend");
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(frontendRoot, "vendor-manifest.json")));
        var files = manifest.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .SelectMany(package => package.GetProperty("files").EnumerateObject())
            .Select(file => (Path: file.Name, Hash: file.Value.GetString()!))
            .ToList();
        Assert.HasCount(17, files);

        var scratch = Directory.CreateTempSubdirectory("asap-vendor-git-");
        try
        {
            File.Copy(Path.Combine(root, ".gitattributes"), Path.Combine(scratch.FullName, ".gitattributes"));
            foreach (var file in files)
            {
                var source = Path.Combine(frontendRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(
                    scratch.FullName,
                    "src",
                    "Asap.Web",
                    "Frontend",
                    file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }

            await RunGit(scratch.FullName, "init", "--quiet");
            await RunGit(
                scratch.FullName,
                "-c",
                "core.autocrlf=true",
                "add",
                "--",
                ".gitattributes",
                "src/Asap.Web/Frontend/vendor");

            var checkoutRoot = Directory.CreateDirectory(Path.Combine(scratch.FullName, "checkout"));
            var checkoutPrefix = checkoutRoot.FullName.Replace('\\', '/') + "/";
            await RunGit(
                scratch.FullName,
                "-c",
                "core.autocrlf=true",
                "checkout-index",
                "--all",
                "--prefix=" + checkoutPrefix);

            foreach (var file in files)
            {
                var checkedOut = Path.Combine(
                    checkoutRoot.FullName,
                    "src",
                    "Asap.Web",
                    "Frontend",
                    file.Path.Replace('/', Path.DirectorySeparatorChar));
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(checkedOut))).ToLowerInvariant();
                Assert.AreEqual(
                    file.Hash,
                    actual,
                    "Fresh Git checkout changed vendor bytes: " + file.Path);
            }
        }
        finally
        {
            foreach (var file in scratch.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.IsReadOnly = false;
            }
            scratch.Delete(recursive: true);
        }
    }

    private static async Task RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.IsTrue(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(
            0,
            process.ExitCode,
            "git " + string.Join(' ', arguments) + " failed: " + await error + " " + await output);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Asap.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
