using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace Asap.Tests.Unit;

[TestClass]
public sealed class ExternalConfigurationLoaderTests
{
    [TestMethod]
    public void LoadsOneStartupOnlyJsonFile()
    {
        using var file = TemporaryFile.WithContent(
            JsonSerializer.Serialize(TestConfigurationFactory.Create()));
        var bootstrap = Bootstrap(file.Path);

        var result = ExternalConfigurationLoader.Load(
            bootstrap,
            Path.GetDirectoryName(file.Path)!,
            allowFileWithinContentRoot: true);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Testing", result.Value!.Environment.Name);
    }

    [TestMethod]
    public void SqlAuthenticationAllowanceComesFromTrustedHostNotJson()
    {
        var configuration = TestConfigurationFactory.Create(isNonProduction: true);
        configuration.Environment.Name = "Testing";
        configuration.ConnectionStrings.AsapDatabase =
            "Server=localhost;Database=AsapTests;User ID=sa;Password=test-only-secret;TrustServerCertificate=True";
        configuration.ConnectionStrings.HangfireDatabase = configuration.ConnectionStrings.AsapDatabase;
        using var file = TemporaryFile.WithContent(JsonSerializer.Serialize(configuration));

        var strictResult = ExternalConfigurationLoader.Load(
            Bootstrap(file.Path),
            Path.GetDirectoryName(file.Path)!,
            allowFileWithinContentRoot: true);
        Assert.IsFalse(strictResult.IsValid);
        CollectionAssert.Contains(
            strictResult.Errors.ToList(),
            "asap_database_integrated_security_required");

        var trustedTestingResult = ExternalConfigurationLoader.Load(
            Bootstrap(file.Path),
            Path.GetDirectoryName(file.Path)!,
            allowFileWithinContentRoot: true,
            allowSqlAuthenticationForTesting: true);
        Assert.IsTrue(trustedTestingResult.IsValid);
    }

    [TestMethod]
    public void MissingPatronLoginRateLimitUsesDocumentedDefaults()
    {
        var document = JsonNode.Parse(JsonSerializer.Serialize(TestConfigurationFactory.Create()))!.AsObject();
        document.Remove("PatronLoginRateLimit");
        using var file = TemporaryFile.WithContent(document.ToJsonString());

        var result = ExternalConfigurationLoader.Load(
            Bootstrap(file.Path),
            Path.GetDirectoryName(file.Path)!,
            allowFileWithinContentRoot: true);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(20, result.Value!.PatronLoginRateLimit.PermitLimit);
        Assert.AreEqual(300, result.Value.PatronLoginRateLimit.WindowSeconds);
    }

    [TestMethod]
    public void MalformedJsonReturnsSafeError()
    {
        using var file = TemporaryFile.WithContent("{ invalid json");

        var result = ExternalConfigurationLoader.Load(
            Bootstrap(file.Path),
            Path.GetDirectoryName(file.Path)!,
            allowFileWithinContentRoot: true);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEqual(new[] { "config_file_json_invalid" }, result.Errors.ToArray());
    }

    [TestMethod]
    public void ProductionConfigurationMustBeOutsideContentRoot()
    {
        var contentRoot = Directory.CreateTempSubdirectory("asap-content-");
        try
        {
            var path = Path.Combine(contentRoot.FullName, "environment.json");
            File.WriteAllText(path, JsonSerializer.Serialize(TestConfigurationFactory.Create()));

            var result = ExternalConfigurationLoader.Load(
                Bootstrap(path),
                contentRoot.FullName,
                allowFileWithinContentRoot: false);

            Assert.IsFalse(result.IsValid);
            CollectionAssert.AreEqual(
                new[] { "config_file_must_be_external" },
                result.Errors.ToArray());
        }
        finally
        {
            contentRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void MissingPointerReturnsSafeError()
    {
        var result = ExternalConfigurationLoader.Load(
            new ConfigurationBuilder().Build(),
            Path.GetTempPath(),
            allowFileWithinContentRoot: true);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEqual(new[] { "config_file_not_configured" }, result.Errors.ToArray());
    }

    [TestMethod]
    public void MissingPointedFileReturnsSafeError()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"missing-asap-config-{Guid.NewGuid():N}.json");

        var result = ExternalConfigurationLoader.Load(
            Bootstrap(path),
            System.IO.Path.GetTempPath(),
            allowFileWithinContentRoot: true);

        Assert.IsFalse(result.IsValid);
        CollectionAssert.AreEqual(new[] { "config_file_not_found" }, result.Errors.ToArray());
    }

    private static IConfiguration Bootstrap(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Asap:ConfigFile"] = path
            })
            .Build();

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryFile WithContent(string content)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"asap-config-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, content);
            return new TemporaryFile(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
