using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task HashOnlyStaffUrlBranchesMatchPinnedFallbacksAndRefuseUnrepresentableValues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-staff-url-hash-only-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationStaffUrlHashOnly_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var queueNames = new[]
        {
            "pending_suggestion_isbn_checks", "outstanding_purchases", "pending_holds", "checked_out",
            "outstanding_timeout", "pending_hold_timeout", "hold_pickup_timeout", "additional_copy_timeout"
        };
        var environmentNames = new[]
            {
                "ASAP_STAFF_URL", "ASAP_PUBLIC_URL", "ASAP_BASE_URL",
                "ASAP_CRON_SCHEDULE", "ASAP_ISBN_CHECK_CRON_SCHEDULE", "ASAP_ORG_SYNC_CRON_SCHEDULE",
                "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", "ASAP_JOB_PAGE_SIZE", "ASAP_JOB_MAX_PER_RUN",
                "ASAP_TIMEOUT_PAGE_SIZE", "ASAP_TIMEOUT_MAX_PER_RUN", "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE",
                "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"
            }
            .Concat(queueNames.SelectMany(name => new[]
            {
                $"ASAP_{name.ToUpperInvariant()}_PAGE_SIZE",
                $"ASAP_{name.ToUpperInvariant()}_MAX_PER_RUN"
            }))
            .ToArray();
        var previousEnvironment = environmentNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var keyPath = Path.Combine(root, "keys");
        var targetDeployed = false;
        Directory.CreateDirectory(root);

        void SetSourceEnvironment(string? staffUrl, string? publicUrl, string? baseUrl)
        {
            Environment.SetEnvironmentVariable("ASAP_STAFF_URL", staffUrl);
            Environment.SetEnvironmentVariable("ASAP_PUBLIC_URL", publicUrl);
            Environment.SetEnvironmentVariable("ASAP_BASE_URL", baseUrl);
        }

        string CreatePackage(string name, string sourceSql, string? staffUrl, string? publicUrl, string? baseUrl)
        {
            SetSourceEnvironment(staffUrl, publicUrl, baseUrl);
            return CreateMinimalPackage(Path.Combine(root, name), sourceSql);
        }

        string SetExternalConfiguration(string package)
        {
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            var path = ExternalConfigurationPath(package);
            File.WriteAllText(path, JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }

        void AssertStaffUrlMetadata(
            string package,
            string expectedValue,
            string expectedProvenance,
            string expectedSource)
        {
            using var runtime = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-runtime-config.json")));
            var staff = runtime.RootElement.GetProperty("settings").GetProperty("StaffApplicationUrl");
            Assert.AreEqual(expectedValue, staff.GetProperty("value").GetString());
            Assert.AreEqual(expectedProvenance, staff.GetProperty("provenance").GetString());
            Assert.AreEqual(expectedSource, staff.GetProperty("source").GetString());
        }

        int ValidatePackage(string package, string externalConfigurationPath, StringWriter error) => MigrationCli.Run(
            ["validate", "--package", package, "--external-config", externalConfigurationPath],
            TextWriter.Null,
            error);

        try
        {
            foreach (var name in environmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Directory.CreateDirectory(keyPath);
            File.WriteAllText(Path.Combine(keyPath, "sentinel.txt"), "Hash-only URL refusal must not change key material.");
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);

            const string persistedHashOnlySql = """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES ('settings0000001', '#fragment', NULL);
                """;
            const string blankSystemSql = """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES ('settings0000002', '', NULL);
                """;
            const string emptySystemSql = """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                """;

            var persistedPackage = CreatePackage("persisted", persistedHashOnlySql, null, null, null);
            var persistedExternal = SetExternalConfiguration(persistedPackage);
            AssertStaffUrlMetadata(persistedPackage, "", "persisted_database", "system_settings.staffUrl");

            var environmentPackage = CreatePackage(
                "staff-environment",
                blankSystemSql,
                "#fragment",
                "https://must-not-be-used.example.org",
                null);
            var environmentExternal = SetExternalConfiguration(environmentPackage);
            AssertStaffUrlMetadata(environmentPackage, "", "environment_fallback", "ASAP_STAFF_URL");

            var noRowStaffPackage = CreatePackage(
                "no-row-staff-environment",
                emptySystemSql,
                "#fragment",
                "https://must-not-be-used.example.org",
                null);
            var noRowStaffExternal = SetExternalConfiguration(noRowStaffPackage);
            AssertStaffUrlMetadata(noRowStaffPackage, "", "environment_fallback", "ASAP_STAFF_URL");

            var publicFallbackPackage = CreatePackage(
                "base-hash-public-fallback",
                emptySystemSql,
                null,
                "https://public-fallback.example.org",
                "#fragment");
            var publicFallbackExternal = SetExternalConfiguration(publicFallbackPackage);
            AssertStaffUrlMetadata(
                publicFallbackPackage,
                "https://public-fallback.example.org/staff/",
                "environment_fallback",
                "ASAP_PUBLIC_URL");

            var defaultFallbackPackage = CreatePackage(
                "base-hash-default-fallback",
                emptySystemSql,
                null,
                null,
                "#fragment");
            var defaultFallbackExternal = SetExternalConfiguration(defaultFallbackPackage);
            AssertStaffUrlMetadata(
                defaultFallbackPackage,
                "http://localhost:8090/staff/",
                "code_default",
                "settings.staffUrl.localhost");

            var noBaseDefaultPackage = CreatePackage(
                "no-base-default-with-public",
                emptySystemSql,
                null,
                "https://must-not-be-used.example.org",
                null);
            var noBaseDefaultExternal = SetExternalConfiguration(noBaseDefaultPackage);
            AssertStaffUrlMetadata(
                noBaseDefaultPackage,
                "http://localhost:8090/staff/",
                "code_default",
                "settings.staffUrl.localhost");

            DeployDacpac(master, databaseName);
            targetDeployed = true;
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);

            foreach (var invalid in new[]
                     {
                         (Package: persistedPackage, External: persistedExternal, Name: "persisted staff URL"),
                         (Package: environmentPackage, External: environmentExternal, Name: "staff environment URL"),
                         (Package: noRowStaffPackage, External: noRowStaffExternal, Name: "no-row staff environment URL")
                     })
            {
                using (var validationError = new StringWriter())
                {
                    Assert.AreEqual(1, ValidatePackage(invalid.Package, invalid.External, validationError), validationError.ToString());
                    StringAssert.Contains(validationError.ToString(), "package_metadata_invalid", invalid.Name);
                }

                var reportPath = Path.Combine(root, $"{invalid.Name.Replace(' ', '-')}-report.json");
                using (var importError = new StringWriter())
                {
                    Assert.AreEqual(
                        1,
                        RunImport(invalid.Package, reportPath, connectionEnvironmentName, tenantId, importError),
                        importError.ToString());
                    StringAssert.Contains(importError.ToString(), "package_metadata_invalid", invalid.Name);
                }

                Assert.IsFalse(File.Exists(reportPath), $"{invalid.Name} must not create a report.");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"{invalid.Name} must not prepare a report.");
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath), $"{invalid.Name} must not write Data Protection keys.");
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target), $"{invalid.Name} must not change SQL.");
            }

            foreach (var valid in new[]
                     {
                         (Package: publicFallbackPackage, External: publicFallbackExternal, Name: "public fallback"),
                         (Package: defaultFallbackPackage, External: defaultFallbackExternal, Name: "code default fallback"),
                         (Package: noBaseDefaultPackage, External: noBaseDefaultExternal, Name: "no-base code default fallback")
                     })
            {
                using var validationError = new StringWriter();
                Assert.AreEqual(0, ValidatePackage(valid.Package, valid.External, validationError), validationError.ToString());
            }

            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
        }
        finally
        {
            foreach (var item in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Staff URL hash-only cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
