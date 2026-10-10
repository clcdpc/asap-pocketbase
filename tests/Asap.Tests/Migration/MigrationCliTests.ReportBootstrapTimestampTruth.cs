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
    public async Task BootstrapAppliedAtUtcMustMatchManifestDuringRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-bootstrap-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationBootstrapReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var objectId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        const string bootstrapUpn = "bootstrap-report-truth@example.org";
        const string exportedAtUtc = "2030-01-02T03:04:05Z";
        const string falseAppliedAtUtc = "2030-01-03T03:04:05Z";
        var databaseDeployed = false;
        Directory.CreateDirectory(root);

        async Task AssertBootstrapStaffRowAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @upn AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;",
                connection);
            command.Parameters.AddWithValue("@upn", bootstrapUpn.ToUpperInvariant());
            Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync()),
                "The target bootstrap administrator must remain present and usable.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                "UPDATE [staff_users] SET [role] = 'admin', [libraryOrgId] = '2';",
                exportedAtUtc: exportedAtUtc);
            var manifestPath = Path.Combine(package, "manifest.json");
            DateTime expectedAppliedAtUtc;
            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
            {
                expectedAppliedAtUtc = manifest.RootElement
                    .GetProperty("exportedAtUtc")
                    .GetDateTime()
                    .ToUniversalTime();
            }

            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(
                Path.Combine(keyPath, "sentinel.txt"),
                "bootstrap report validation must preserve this key-ring directory");
            var externalConfigurationPath = ExternalConfigurationPath(package);
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            configuration.Authentication.Entra.InitialSuperAdmin.TenantId = tenantId.ToString();
            configuration.Authentication.Entra.InitialSuperAdmin.ObjectId = objectId.ToString();
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = bootstrapUpn;
            configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "Bootstrap Report Truth";
            configuration.Authentication.Entra.InitialSuperAdmin.NotificationEmail = "bootstrap-report-notify@example.org";
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
            Assert.IsTrue(File.Exists(pendingPath), "Committed import must leave its authentic report for recovery.");
            Assert.IsTrue(Directory.Exists(reportPath), "The report publication failure must come from the blocked final path.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            using (var pending = JsonDocument.Parse(validPending))
            {
                var bootstrap = pending.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "migration_bootstrap_super_admin");
                Assert.AreEqual("inserted", bootstrap.GetProperty("action").GetString());
                Assert.AreEqual(bootstrapUpn, bootstrap.GetProperty("authenticationEmail").GetString());
                Assert.AreEqual(expectedAppliedAtUtc, bootstrap.GetProperty("appliedAtUtc").GetDateTime().ToUniversalTime(),
                    "The authentic bootstrap timestamp must equal immutable package manifest time.");
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertBootstrapStaffRowAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertBootstrapStaffRowAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var tamperedPending = ApplyBootstrapAppliedAtMutation(validPending, falseAppliedAtUtc);
            await AssertRejectedReportTruthMutationAsync(
                reportPath,
                true,
                "appliedAtUtc",
                validPending,
                tamperedPending,
                error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                AssertExternalStateUnchangedAsync);

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertBootstrapStaffRowAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            var tamperedRecoveredReport = ApplyBootstrapAppliedAtMutation(validRecoveredReport, falseAppliedAtUtc);
            await AssertRejectedReportTruthMutationAsync(
                reportPath,
                false,
                "appliedAtUtc",
                validRecoveredReport,
                tamperedRecoveredReport,
                error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                AssertExternalStateUnchangedAsync);

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath),
                "Restoring the bootstrap timestamp must restore exact original report bytes.");
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertBootstrapStaffRowAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
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
                throw new InvalidOperationException("Bootstrap report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static string ApplyBootstrapAppliedAtMutation(string reportJson, string replacement)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var bootstrap = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "migration_bootstrap_super_admin")!.AsObject();
        bootstrap["appliedAtUtc"] = replacement;
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
