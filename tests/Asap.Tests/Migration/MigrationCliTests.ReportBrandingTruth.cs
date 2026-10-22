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
    public async Task BrandingReportMustMatchSourceAssetForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-branding-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationBrandingReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string collectionId = "pbc_branding_fixture";
        const string sourceId = "ui-settings-record";
        const string fileName = "logo_fixture.png";
        const string altText = "Fixture logo";
        Directory.CreateDirectory(root);

        var expectedBytes = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"));
        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expectedBytes));

        async Task AssertBrandingSqlAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [LogoData], [LogoContentType], [LogoFileName], [LogoAltText] FROM [asap].[Branding] WHERE [OrganizationId] = 1;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), "System branding must remain present.");
            CollectionAssert.AreEqual(expectedBytes, reader.GetFieldValue<byte[]>(0));
            Assert.AreEqual("image/png", reader.GetString(1));
            Assert.AreEqual(fileName, reader.GetString(2));
            Assert.AreEqual(altText, reader.GetString(3));
            Assert.IsFalse(await reader.ReadAsync(), "System branding must remain unique.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                $$"""
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES ('{{collectionId}}', 'ui_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('{{sourceId}}', 'system', NULL, '{{fileName}}', '{{altText}}');
                """,
                storage =>
                {
                    var directory = Path.Combine(storage, collectionId, sourceId);
                    Directory.CreateDirectory(directory);
                    File.Copy(
                        Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"),
                        Path.Combine(directory, fileName));
                });

            using (var exportedBranding = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(package, "branding.json"))))
            {
                var row = exportedBranding.RootElement.GetProperty("collections").GetProperty("branding")[0];
                Assert.AreEqual(collectionId, row.GetProperty("sourceCollectionId").GetString());
                Assert.AreEqual(sourceId, row.GetProperty("sourceRecordId").GetString());
                Assert.AreEqual("system", row.GetProperty("scope").GetString());
                Assert.AreEqual("image/png", row.GetProperty("contentType").GetString());
                Assert.AreEqual(expectedHash, row.GetProperty("sha256").GetString());
                Assert.AreEqual(expectedBytes.LongLength, row.GetProperty("length").GetInt64());
            }

            using (var authenticExportValidationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, authenticExportValidationError),
                    authenticExportValidationError.ToString());
            }

            var brandingPath = Path.Combine(package, "branding.json");
            var compatiblePackageBranding = JsonNode.Parse(await File.ReadAllTextAsync(brandingPath))!.AsObject();
            compatiblePackageBranding["collections"]!["branding"]!.AsArray()[0]!["scope"] = " System ";
            await File.WriteAllTextAsync(
                brandingPath,
                compatiblePackageBranding.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            UpdateManifestEntry(package, "branding.json");
            using (var compatiblePackageValidationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, compatiblePackageValidationError),
                    compatiblePackageValidationError.ToString());
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
            AssertBrandingReportBaseline(validPending, sourceId, expectedHash, expectedBytes.LongLength);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertBrandingSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for branding {propertyName}.");
                await AssertBrandingSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for branding {propertyName}.");
            }

            async Task RejectPendingMutationAsync(string propertyName)
            {
                var tamperedPending = ApplyBrandingReportTruthMutation(validPending, propertyName);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    tamperedPending,
                    error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectPendingMutationAsync("sourceId");
            await RejectPendingMutationAsync("organizationId");
            await RejectPendingMutationAsync("assetSha256");
            await RejectPendingMutationAsync("length");
            await RejectPendingMutationAsync("contentType");

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertBrandingReportBaseline(validRecoveredReport, sourceId, expectedHash, expectedBytes.LongLength);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertBrandingSqlAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            async Task RejectRecoveredMutationAsync(string propertyName)
            {
                var tamperedReport = ApplyBrandingReportTruthMutation(validRecoveredReport, propertyName);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    tamperedReport,
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectRecoveredMutationAsync("sourceId");
            await RejectRecoveredMutationAsync("organizationId");
            await RejectRecoveredMutationAsync("assetSha256");
            await RejectRecoveredMutationAsync("length");
            await RejectRecoveredMutationAsync("contentType");

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertBrandingSqlAsync();
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
                throw new InvalidOperationException("Branding report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertBrandingReportBaseline(
        string reportJson,
        string sourceId,
        string assetSha256,
        long length)
    {
        using var report = JsonDocument.Parse(reportJson);
        var branding = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "branding");
        Assert.AreEqual(sourceId, branding.GetProperty("sourceId").GetString());
        Assert.AreEqual(1, branding.GetProperty("organizationId").GetInt32());
        Assert.AreEqual(assetSha256, branding.GetProperty("assetSha256").GetString());
        Assert.AreEqual(length, branding.GetProperty("length").GetInt64());
        Assert.AreEqual("image/png", branding.GetProperty("contentType").GetString());
    }

    private static string ApplyBrandingReportTruthMutation(string reportJson, string propertyName)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var branding = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "branding")!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
                branding[propertyName] = "other-branding-source";
                break;
            case "organizationId":
                branding[propertyName] = 2;
                break;
            case "assetSha256":
                var currentHash = branding[propertyName]!.GetValue<string>();
                var replacementCharacter = currentHash[0] == '0' ? '1' : '0';
                branding[propertyName] = new string(replacementCharacter, 64);
                break;
            case "length":
                branding[propertyName] = branding[propertyName]!.GetValue<long>() + 1;
                break;
            case "contentType":
                branding[propertyName] = "image/jpeg";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported branding report mutation.");
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
