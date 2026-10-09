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
    public async Task EmailTransformationReportMustMatchSourceAndTargetForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-email-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEmailReportTruth_{Guid.NewGuid():N}";
        var postmarkDatabaseName = $"AsapMigrationEmailReportTruthPostmark_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var postmarkConnectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var postmarkTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = postmarkDatabaseName }.ConnectionString;
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);

        async Task AssertEmailSettingsAsync(string? connectionString = null)
        {
            await using var connection = new SqlConnection(connectionString ?? target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [ProtectedServerToken], [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), "The imported system email settings row must remain present.");
            Assert.IsTrue(reader.IsDBNull(0), "This fixture does not provision a target email token.");
            Assert.AreEqual("notices@example.org", reader.GetString(1));
            Assert.AreEqual("ASAP Notices", reader.GetString(2));
            Assert.IsFalse(await reader.ReadAsync(), "System email settings must remain unique.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                CREATE TABLE [smtp_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [created] TEXT, [updated] TEXT,
                    [fromAddress] TEXT, [fromName] TEXT
                );
                INSERT INTO [smtp_settings] VALUES
                    ('smtp-report-truth', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z',
                     'notices@example.org', 'ASAP Notices');
                """);

            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(
                Path.Combine(keyPath, "sentinel.txt"),
                "report validation must preserve this key-ring directory");
            var externalConfigurationPath = ExternalConfigurationPath(package);
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            configuration.EmailSafety.DeliveryMode = "capture";
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
            using (var pending = JsonDocument.Parse(validPending))
            {
                var emailSettings = pending.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "email_settings");
                Assert.AreEqual("smtp-report-truth", emailSettings.GetProperty("sourceId").GetString());
                Assert.AreEqual("legacy_smtp_transport_intentionally_dropped", emailSettings.GetProperty("transport").GetString());
                Assert.AreEqual(
                    "target_email_sender_selected_by_external_configuration",
                    emailSettings.GetProperty("targetTransport").GetString());
                Assert.IsFalse(emailSettings.GetProperty("postmarkTokenProvisioned").GetBoolean());
                var token = pending.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "email_provider_token");
                Assert.AreEqual(1, token.GetProperty("organizationId").GetInt32());
                Assert.IsFalse(token.GetProperty("postmarkTokenProvisioned").GetBoolean());
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertEmailSettingsAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);
            var tamperedPending = ApplyEmailReportMutation(
                validPending,
                "transport",
                "file_email_sender");
            await File.WriteAllTextAsync(pendingPath, tamperedPending, new UTF8Encoding(false));
            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError));
                StringAssert.Contains(recoverError.ToString(), "reconciliation_report_mismatch");
            }
            Assert.AreEqual(tamperedPending, await File.ReadAllTextAsync(pendingPath),
                "Rejected recovery must preserve the tampered transport claim bytes.");
            Assert.IsFalse(File.Exists(reportPath), "Rejected recovery must not promote a false transport claim.");
            Assert.IsFalse(Directory.Exists(reportPath), "Rejected recovery must leave the final report path absent.");
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                "Rejected recovery must not change target SQL.");
            await AssertEmailSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                "Rejected recovery must not change the Data Protection key ring.");

            await File.WriteAllTextAsync(pendingPath, validPending, new UTF8Encoding(false));
            Assert.AreEqual(validPending, await File.ReadAllTextAsync(pendingPath),
                "Restoring the transport claim must restore exact original pending bytes.");
            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertEmailSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            var tamperedRecoveredReport = ApplyEmailReportMutation(
                validRecoveredReport,
                "targetTransport",
                "legacy_smtp_transport_intentionally_dropped");
            await File.WriteAllTextAsync(reportPath, tamperedRecoveredReport, new UTF8Encoding(false));
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError));
                StringAssert.Contains(reconcileError.ToString(), "reconciliation_report_mismatch");
            }
            Assert.AreEqual(tamperedRecoveredReport, await File.ReadAllTextAsync(reportPath),
                "Rejected reconciliation must preserve the tampered target-transport bytes.");
            Assert.IsFalse(File.Exists(pendingPath), "Rejected reconciliation must not create or consume a pending report.");
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                "Rejected reconciliation must not change target SQL.");
            await AssertEmailSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                "Rejected reconciliation must not change the Data Protection key ring.");

            await File.WriteAllTextAsync(reportPath, validRecoveredReport, new UTF8Encoding(false));
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath),
                "Restoring the target transport claim must restore exact original report bytes.");
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertEmailSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            configuration.EmailSafety.DeliveryMode = "postmark";
            await File.WriteAllTextAsync(
                externalConfigurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            using (var changedModeError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    RunReconcile(package, reportPath, connectionEnvironmentName, changedModeError),
                    changedModeError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath),
                "Changing the externally selected sender must not rewrite the stable migration-boundary report.");

            DeployDacpac(master, postmarkDatabaseName);
            Environment.SetEnvironmentVariable(postmarkConnectionEnvironmentName, postmarkTarget);
            var postmarkReportPath = Path.Combine(root, "postmark-import-report.json");
            using (var postmarkImportError = new StringWriter())
            {
                var postmarkImportExitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", postmarkConnectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", postmarkReportPath,
                        "--external-config", externalConfigurationPath
                    ],
                    TextWriter.Null,
                    postmarkImportError);
                Assert.AreEqual(0, postmarkImportExitCode, postmarkImportError.ToString());
            }

            var postmarkReport = await File.ReadAllTextAsync(postmarkReportPath);
            using (var report = JsonDocument.Parse(postmarkReport))
            {
                var emailSettings = report.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "email_settings");
                Assert.AreEqual("smtp-report-truth", emailSettings.GetProperty("sourceId").GetString());
                Assert.AreEqual("legacy_smtp_transport_intentionally_dropped", emailSettings.GetProperty("transport").GetString());
                Assert.AreEqual(
                    "target_email_sender_selected_by_external_configuration",
                    emailSettings.GetProperty("targetTransport").GetString());
                Assert.IsFalse(emailSettings.GetProperty("postmarkTokenProvisioned").GetBoolean());
            }
            var postmarkFingerprint = ComputeTargetFingerprintForTest(postmarkTarget);
            await AssertEmailSettingsAsync(postmarkTarget);
            using (var postmarkReconcileError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    RunReconcile(package, postmarkReportPath, postmarkConnectionEnvironmentName, postmarkReconcileError),
                    postmarkReconcileError.ToString());
            }
            Assert.AreEqual(postmarkReport, await File.ReadAllTextAsync(postmarkReportPath));
            Assert.AreEqual(postmarkFingerprint, ComputeTargetFingerprintForTest(postmarkTarget));
            await AssertEmailSettingsAsync(postmarkTarget);
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            Environment.SetEnvironmentVariable(postmarkConnectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            await DropDatabaseAsync(master, postmarkDatabaseName);
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Email report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static string ApplyEmailReportMutation(string reportJson, string propertyName, string replacement)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var emailSettings = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "email_settings")!.AsObject();
        emailSettings[propertyName] = replacement;
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
