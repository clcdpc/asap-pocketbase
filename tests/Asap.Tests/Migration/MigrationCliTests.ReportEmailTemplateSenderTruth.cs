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
    public async Task EmailTemplateSenderReportMustMatchLibrarySourceGroupForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-email-template-sender-report-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEmailTemplateSenderReport_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var databaseDeployed = false;
        Directory.CreateDirectory(root);

        async Task AssertLibraryEmailSettingsAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 2;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), "The sender group must materialize one library email-settings row.");
            Assert.AreEqual("branch-notices@example.org", reader.GetString(0));
            Assert.AreEqual("Branch Notices", reader.GetString(1));
            Assert.IsFalse(await reader.ReadAsync(), "The sender group must not create duplicate library settings rows.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                CREATE TABLE [email_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                    [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT, [fromAddress] TEXT, [fromName] TEXT
                );
                INSERT INTO [email_templates] VALUES
                    ('a-address', 'library', 'pb-org-2', 'branch_notice', 'Branch Notice', 'Notice', '<p>Notice</p>', 1, NULL, 'branch-notices@example.org', NULL),
                    ('b-name', 'library', 'pb-org-2', 'branch_receipt', 'Branch Receipt', 'Receipt', '<p>Receipt</p>', 1, NULL, NULL, 'Branch Notices'),
                    ('c-no-sender', 'library', 'pb-org-2', 'branch_no_sender', 'Branch No Sender', 'Other', '<p>Other</p>', 1, NULL, NULL, NULL);
                """);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "sender report validation preserves this key ring");
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            var externalConfigurationPath = ExternalConfigurationPath(package);
            await File.WriteAllTextAsync(
                externalConfigurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

            DeployDacpac(master, databaseName);
            databaseDeployed = true;
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var reportPath = Path.Combine(root, "blocked-report-path");
            Directory.CreateDirectory(reportPath);
            using (var importError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", reportPath,
                        "--external-config", externalConfigurationPath
                    ],
                    TextWriter.Null,
                    importError);
                Assert.AreEqual(1, exitCode, importError.ToString());
                StringAssert.Contains(importError.ToString(), "import_committed_report_failed");
            }

            var pendingPath = reportPath + ".pending";
            Assert.IsTrue(File.Exists(pendingPath), "The committed import must leave its authentic sender report pending.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            AssertEmailTemplateSenderBaseline(validPending);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertLibraryEmailSettingsAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertLibraryEmailSettingsAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            foreach (var propertyName in new[] { "sourceIds", "organizationId", "fromAddress", "fromName", "disposition" })
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    ApplyEmailTemplateSenderMutation(validPending, propertyName),
                    error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertEmailTemplateSenderBaseline(validRecoveredReport);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertLibraryEmailSettingsAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            foreach (var propertyName in new[] { "sourceIds", "organizationId", "fromAddress", "fromName", "disposition" })
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    ApplyEmailTemplateSenderMutation(validRecoveredReport, propertyName),
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored sender report");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            if (databaseDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Email-template sender report cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertEmailTemplateSenderBaseline(string reportJson)
    {
        using var report = JsonDocument.Parse(reportJson);
        var sender = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "email_template_sender");
        CollectionAssert.AreEqual(
            new[] { "a-address", "b-name", "c-no-sender" },
            sender.GetProperty("sourceIds").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.AreEqual(2, sender.GetProperty("organizationId").GetInt32());
        Assert.AreEqual("branch-notices@example.org", sender.GetProperty("fromAddress").GetString());
        Assert.AreEqual("Branch Notices", sender.GetProperty("fromName").GetString());
        Assert.AreEqual("moved_to_scoped_email_settings", sender.GetProperty("disposition").GetString());
    }

    private static string ApplyEmailTemplateSenderMutation(string reportJson, string propertyName)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var sender = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "email_template_sender")!.AsObject();
        switch (propertyName)
        {
            case "sourceIds":
                sender["sourceIds"]!.AsArray()[1] = "b-unrelated-template";
                break;
            case "organizationId":
                sender["organizationId"] = 20;
                break;
            case "fromAddress":
                sender["fromAddress"] = "different-notices@example.org";
                break;
            case "fromName":
                sender["fromName"] = "Different Notices";
                break;
            case "disposition":
                sender["disposition"] = "moved_to_system_email_settings";
                break;
            default:
                Assert.Fail($"Unexpected sender mutation: {propertyName}");
                break;
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
