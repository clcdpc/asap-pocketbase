using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportAndReconcileEmbedOriginsUsingPinnedEcmaScriptNormalization()
    {
        var expectedOrigins = new[]
        {
            "https://safe.example.org",
            "https://*.example.org",
            "http://localhost",
            "https://port.example.org:443"
        };
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-embed-javascript-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEmbedJs_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnection = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var targetDeployed = false;

        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [patronEmbedAllowedOrigins] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings0000001', char(65279) || 'HTTPS://Safe.Example.org' || char(65279) ||
                     ',https://*.Example.org' || char(13) || char(10) || 'http://localhost' ||
                     char(13) || char(10) || 'https://port.example.org:443');
                """);
            var report = Path.Combine(root, "report.json");
            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validationError),
                    validationError.ToString());
            }

            targetDeployed = true;
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    RunImport(package, report, connectionEnvironmentName, tenantId, importError),
                    importError.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT [Origin], [NormalizedOrigin] FROM [asap].[PatronEmbedAllowedOrigin] ORDER BY [Id];";
                await using var reader = await command.ExecuteReaderAsync();
                var origins = new List<string>();
                var normalized = new List<string>();
                while (await reader.ReadAsync())
                {
                    origins.Add(reader.GetString(0));
                    normalized.Add(reader.GetString(1));
                }

                CollectionAssert.AreEqual(expectedOrigins, origins.ToArray(), "SQL origins must match the pinned JS-normalized source list.");
                CollectionAssert.AreEqual(expectedOrigins, normalized.ToArray(), "Stored normalized origins must match the pinned JS result.");
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnection);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Embed-origin cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportRejectsEmbedTokensThatPinnedEcmaScriptRejects()
    {
        var invalidCases = new[]
        {
            (Name: "nel-wrapped-origin", SqlValue: "char(133) || 'https://safe.example.org' || char(133)"),
            (Name: "semicolon-is-not-a-separator", SqlValue: "'https://one.example.org;https://two.example.org'"),
            (Name: "http-wildcard", SqlValue: "'http://*.example.org'"),
            (Name: "remote-http", SqlValue: "'http://remote.example.org'")
        };
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-invalid-embed-javascript-{Guid.NewGuid():N}");
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnection = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var deployedDatabaseNames = new List<string>();
        var failures = new List<string>();

        try
        {
            foreach (var item in invalidCases)
            {
                var caseRoot = Path.Combine(root, item.Name);
                var databaseName = $"AsapMigrationBadEmbed_{Guid.NewGuid():N}";
                var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
                var package = CreateMinimalPackage(
                    caseRoot,
                    $$"""
                    CREATE TABLE [system_settings]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [patronEmbedAllowedOrigins] TEXT
                    );
                    INSERT INTO [system_settings] VALUES ('settings0000001', {{item.SqlValue}});
                    """);
                var report = Path.Combine(caseRoot, "report.json");
                var keysPath = Path.Combine(caseRoot, "keys");
                var externalConfiguration = TestConfigurationFactory.Create();
                externalConfiguration.Application.DataProtectionKeysPath = keysPath;
                await File.WriteAllTextAsync(
                    ExternalConfigurationPath(package),
                    JsonSerializer.Serialize(externalConfiguration, new JsonSerializerOptions { WriteIndented = true }));

                deployedDatabaseNames.Add(databaseName);
                DeployDacpac(master, databaseName);
                Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
                var targetFingerprint = ComputeTargetFingerprintForTest(target);
                using var importError = new StringWriter();
                var exitCode = RunImport(package, report, connectionEnvironmentName, tenantId, importError);
                var diagnostic = importError.ToString();
                if (exitCode != 1)
                {
                    failures.Add($"{item.Name}: expected import exit 1, actual {exitCode}; {diagnostic.Trim()}");
                }
                if (!diagnostic.Contains("patron_embed_origin_invalid", StringComparison.Ordinal))
                {
                    failures.Add($"{item.Name}: expected patron_embed_origin_invalid; {diagnostic.Trim()}");
                }
                if (File.Exists(report) || File.Exists(report + ".pending"))
                {
                    failures.Add($"{item.Name}: invalid origin published a report or pending report.");
                }
                if (Directory.Exists(keysPath))
                {
                    failures.Add($"{item.Name}: invalid origin created a Data Protection key directory.");
                }

                var resultingFingerprint = ComputeTargetFingerprintForTest(target);
                if (!string.Equals(targetFingerprint, resultingFingerprint, StringComparison.Ordinal))
                {
                    failures.Add($"{item.Name}: invalid origin changed the target SQL fingerprint.");
                }
            }

            Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnection);
            foreach (var databaseName in deployedDatabaseNames)
            {
                await DropDatabaseAsync(master, databaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Invalid embed-origin cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
