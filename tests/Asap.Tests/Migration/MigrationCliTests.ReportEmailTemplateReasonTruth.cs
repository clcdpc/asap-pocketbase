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
    public async Task EmailTemplateTransformationReasonMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-email-template-reason-report-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEmailTemplateReasonReport_{Guid.NewGuid():N}";
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

        async Task AssertTemplateAndSenderSqlAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var templateCommand = new SqlCommand(
                             "SELECT COUNT(*) FROM [asap].[EmailTemplate] template JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'email_template' AND mapping.[PocketBaseId] = N'reason-template' AND mapping.[NewId] = template.[Id] WHERE template.[OrganizationId] = 2 AND template.[TemplateKey] = N'branch_receipt' AND template.[SubjectTemplate] = N'Branch receipt subject' AND template.[BodyTemplate] = N'<p>Branch receipt</p>' AND template.[DisplayName] = N'Branch Receipt';",
                             connection))
            {
                Assert.AreEqual(1, Convert.ToInt32(await templateCommand.ExecuteScalarAsync()),
                    "The ordinary library template must remain mapped to its imported SQL row.");
            }
            await using var senderCommand = new SqlCommand(
                "SELECT [FromAddress], [FromName] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 2;",
                connection);
            await using var reader = await senderCommand.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("reason-notices@example.org", reader.GetString(0));
            Assert.AreEqual("Reason Notices", reader.GetString(1));
            Assert.IsFalse(await reader.ReadAsync());
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
                    ('reason-template', 'library', 'pb-org-2', 'branch_receipt', 'Branch Receipt',
                     'Branch receipt subject', '<p>Branch receipt</p>', 1, NULL,
                     'reason-notices@example.org', 'Reason Notices');
                """);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "template reason report validation preserves this key ring");
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
            Assert.IsTrue(File.Exists(pendingPath), "The committed import must leave its authentic email-template report pending.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            AssertEmailTemplateReasonBaseline(validPending);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertTemplateAndSenderSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertTemplateAndSenderSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            await AssertRejectedReportTruthMutationAsync(
                reportPath,
                true,
                "transformation",
                validPending,
                ApplyEmailTemplateReasonMutation(validPending),
                error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                AssertExternalStateUnchangedAsync);

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertEmailTemplateReasonBaseline(validRecoveredReport);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertTemplateAndSenderSqlAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            await AssertRejectedReportTruthMutationAsync(
                reportPath,
                false,
                "transformation",
                validRecoveredReport,
                ApplyEmailTemplateReasonMutation(validRecoveredReport),
                error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                AssertExternalStateUnchangedAsync);

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored email-template report");
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
                throw new InvalidOperationException("Email-template reason report cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertEmailTemplateReasonBaseline(string reportJson)
    {
        using var report = JsonDocument.Parse(reportJson);
        var sender = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "email_template" &&
                item.GetProperty("sourceId").GetString() == "reason-template");
        Assert.AreEqual("template_sender_fields_moved_to_scoped_email_settings", sender.GetProperty("transformation").GetString());
    }

    private static string ApplyEmailTemplateReasonMutation(string reportJson)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var template = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "email_template" &&
                item!["sourceId"]!.GetValue<string>() == "reason-template")!.AsObject();
        template["transformation"] = "template_sender_fields_moved_to_system_email_settings";
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
