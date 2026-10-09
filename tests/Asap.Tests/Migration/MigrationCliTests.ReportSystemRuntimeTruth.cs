using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task EffectiveSystemRuntimeReportMustMatchSourceMetadataForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-system-runtime-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationSystemRuntimeReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string sourceId = "system-runtime-report-truth";
        const string staffUrl = "https://runtime-report.example.org/staff/";
        const string iconPattern = "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";
        Directory.CreateDirectory(root);

        async Task AssertSystemSettingsAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [StaffApplicationUrl], [MaterialTypeIconUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), "The imported System1 settings row must remain present.");
            Assert.AreEqual(staffUrl, reader.GetString(0));
            Assert.AreEqual(iconPattern, reader.GetString(1));
            Assert.IsFalse(await reader.ReadAsync(), "System settings must remain unique.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('system-runtime-report-truth', 'https://runtime-report.example.org/staff/', NULL);
                """);
            var runtimeMetadataPath = Path.Combine(package, "effective-legacy-runtime-config.json");
            using (var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(runtimeMetadataPath)))
            {
                var settings = runtime.RootElement.GetProperty("settings");
                Assert.AreEqual(
                    "system_settings.staffUrl",
                    settings.GetProperty("StaffApplicationUrl").GetProperty("source").GetString());
                Assert.AreEqual(
                    "normalization.defaultFormatIconUrlPattern",
                    settings.GetProperty("MaterialTypeIconUrlPattern").GetProperty("source").GetString());
            }

            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(
                Path.Combine(keyPath, "sentinel.txt"),
                "report validation must preserve this key-ring directory");
            var externalConfigurationPath = ExternalConfigurationPath(package);
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            await File.WriteAllTextAsync(
                externalConfigurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var reportPath = Path.Combine(root, "blocked-report-path");
            Directory.CreateDirectory(reportPath);
            using (var importError = new StringWriter())
            {
                var importExitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", reportPath,
                        "--external-config", externalConfigurationPath
                    ],
                    TextWriter.Null,
                    importError);
                Assert.AreEqual(1, importExitCode, importError.ToString());
                StringAssert.Contains(importError.ToString(), "import_committed_report_failed");
            }

            var pendingPath = reportPath + ".pending";
            Assert.IsTrue(File.Exists(pendingPath), "The committed import must leave its authentic prepared report for recovery.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery is tested.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            AssertSystemRuntimeReportBaseline(validPending, sourceId);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertSystemSettingsAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertSystemSettingsAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            async Task RejectPendingMutationAsync(string propertyName, string replacement)
            {
                var tamperedPending = ApplySystemRuntimeReportMutation(validPending, propertyName, replacement);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    tamperedPending,
                    error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectPendingMutationAsync("sourceId", "system-runtime-other-source");
            await RejectPendingMutationAsync("staffApplicationUrlSource", "ASAP_STAFF_URL");
            await RejectPendingMutationAsync("materialTypeIconUrlPatternSource", "system_settings.formatIconUrlPattern");

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertSystemRuntimeReportBaseline(validRecoveredReport, sourceId);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertSystemSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            async Task RejectRecoveredMutationAsync(string propertyName, string replacement)
            {
                var tamperedReport = ApplySystemRuntimeReportMutation(validRecoveredReport, propertyName, replacement);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    tamperedReport,
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectRecoveredMutationAsync("misconfiguredMessage", "source_configuration_default");
            await RejectRecoveredMutationAsync("misconfiguredMessageReason", "source_field_omitted_from_export");

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertSystemSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("System runtime report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertSystemRuntimeReportBaseline(string reportJson, string sourceId)
    {
        using var report = JsonDocument.Parse(reportJson);
        var runtime = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "system_settings_effective_runtime");
        Assert.AreEqual(sourceId, runtime.GetProperty("sourceId").GetString());
        Assert.AreEqual("system_settings.staffUrl", runtime.GetProperty("staffApplicationUrlSource").GetString());
        Assert.AreEqual(
            "normalization.defaultFormatIconUrlPattern",
            runtime.GetProperty("materialTypeIconUrlPatternSource").GetString());
        Assert.AreEqual("target_code_default", runtime.GetProperty("misconfiguredMessage").GetString());
        Assert.AreEqual(
            "pinned_ui_settings_schema_has_no_persisted_field",
            runtime.GetProperty("misconfiguredMessageReason").GetString());
    }

    private static string ApplySystemRuntimeReportMutation(string reportJson, string propertyName, string replacement)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var runtime = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "system_settings_effective_runtime")!.AsObject();
        runtime[propertyName] = replacement;
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
