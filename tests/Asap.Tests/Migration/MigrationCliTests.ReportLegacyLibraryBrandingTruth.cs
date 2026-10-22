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
    public async Task DormantLegacyLibraryBrandingReportMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-legacy-library-branding-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationLegacyLibraryBrandingReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string sourceId = "legacy-library-branding-report-truth";
        const string currentAltText = "Current library logo";
        const string dormantLogoAltText = "Dormant legacy logo";
        const string dormantLogoName = "retired-library-logo.png";
        const int organizationId = 2;
        const string disposition = "intentionally_dropped_not_effective_at_pinned_source";
        string[] populatedFields = ["logo", "logoAlt"];
        Directory.CreateDirectory(root);

        async Task AssertLibraryBrandingSqlAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var command = new SqlCommand(
                             "SELECT [LogoData], [LogoContentType], [LogoFileName], [LogoAltText] FROM [asap].[Branding] WHERE [OrganizationId] = @organizationId;",
                             connection))
            {
                command.Parameters.AddWithValue("@organizationId", organizationId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync(), "The current library UI branding record must remain present.");
                Assert.IsTrue(reader.IsDBNull(0), "Dormant legacy logo bytes must not replace the current UI value.");
                Assert.IsTrue(reader.IsDBNull(1));
                Assert.IsTrue(reader.IsDBNull(2));
                Assert.AreEqual(currentAltText, reader.GetString(3));
                Assert.IsFalse(await reader.ReadAsync(), "Current library branding must remain unique.");
            }

            await using var otherLibrary = new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 3;",
                connection);
            Assert.AreEqual(0, Convert.ToInt32(await otherLibrary.ExecuteScalarAsync()));
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                $$"""
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Test Library', 'OTHER', 1, 2, 1);
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES
                    ('pbc-ui-settings', 'ui_settings'),
                    ('pbc-library-settings', 'library_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('current-ui-branding', 'library', 'pb-org-2', NULL, '{{currentAltText}}');
                CREATE TABLE [library_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrganization] TEXT NOT NULL,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [library_settings] VALUES
                    ('{{sourceId}}', 'pb-org-2', '{{dormantLogoName}}', '{{dormantLogoAltText}}');
                """,
                exportedAtUtc: "2030-01-02T03:04:05Z");

            using (var exportedBranding = JsonDocument.Parse(
                       await File.ReadAllTextAsync(Path.Combine(package, "branding.json"))))
            {
                Assert.AreEqual(
                    0,
                    exportedBranding.RootElement.GetProperty("collections").GetProperty("branding").GetArrayLength(),
                    "The dormant legacy logo must not become an exported active branding asset.");
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
            AssertLegacyLibraryBrandingReportBaseline(
                validPending,
                sourceId,
                organizationId,
                disposition,
                populatedFields);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertLibraryBrandingSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertLibraryBrandingSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            async Task RejectPendingMutationAsync(string propertyName)
            {
                var tamperedPending = ApplyLegacyLibraryBrandingReportMutation(validPending, propertyName);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    tamperedPending,
                    error => RunRecoverReport(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectPendingMutationAsync("sourceCollection");
            await RejectPendingMutationAsync("sourceId");
            await RejectPendingMutationAsync("organizationId");
            await RejectPendingMutationAsync("populatedFields");
            await RejectPendingMutationAsync("disposition");

            var reorderedPending = JsonNode.Parse(validPending)!.AsObject();
            var reorderedFields = new JsonArray();
            reorderedFields.Add("logoAlt");
            reorderedFields.Add("logo");
            var legacyBrandingTransformation = reorderedPending["transformations"]!.AsArray()
                .Single(item => item!["entity"]!.GetValue<string>() == "legacy_library_branding")!.AsObject();
            legacyBrandingTransformation["populatedFields"] = reorderedFields;
            await File.WriteAllTextAsync(
                pendingPath,
                reorderedPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError),
                    recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertLegacyLibraryBrandingReportBaseline(
                validRecoveredReport,
                sourceId,
                organizationId,
                disposition,
                populatedFields);
            using (var reorderedReport = JsonDocument.Parse(validRecoveredReport))
            {
                var recoveredFields = reorderedReport.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "legacy_library_branding")
                    .GetProperty("populatedFields").EnumerateArray().Select(item => item.GetString()!).ToArray();
                CollectionAssert.AreEqual(new[] { "logoAlt", "logo" }, recoveredFields);
            }
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertLibraryBrandingSqlAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            async Task RejectRecoveredMutationAsync(string propertyName)
            {
                var tamperedReport = ApplyLegacyLibraryBrandingReportMutation(validRecoveredReport, propertyName);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    tamperedReport,
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            await RejectRecoveredMutationAsync("sourceCollection");
            await RejectRecoveredMutationAsync("sourceId");
            await RejectRecoveredMutationAsync("organizationId");
            await RejectRecoveredMutationAsync("populatedFields");
            await RejectRecoveredMutationAsync("disposition");

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertLibraryBrandingSqlAsync();
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
                throw new InvalidOperationException("Legacy library branding report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertLegacyLibraryBrandingReportBaseline(
        string reportJson,
        string sourceId,
        int organizationId,
        string disposition,
        IReadOnlyList<string> populatedFields)
    {
        using var report = JsonDocument.Parse(reportJson);
        var transformation = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "legacy_library_branding");
        Assert.AreEqual("library_settings", transformation.GetProperty("sourceCollection").GetString());
        Assert.AreEqual(sourceId, transformation.GetProperty("sourceId").GetString());
        Assert.AreEqual(organizationId, transformation.GetProperty("organizationId").GetInt32());
        Assert.AreEqual(disposition, transformation.GetProperty("disposition").GetString());
        CollectionAssert.AreEquivalent(
            populatedFields.ToArray(),
            transformation.GetProperty("populatedFields").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    private static string ApplyLegacyLibraryBrandingReportMutation(string reportJson, string propertyName)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformation = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "legacy_library_branding")!.AsObject();
        switch (propertyName)
        {
            case "sourceCollection":
                transformation[propertyName] = "ui_settings";
                break;
            case "sourceId":
                transformation[propertyName] = "other-library-branding-source";
                break;
            case "organizationId":
                transformation[propertyName] = 3;
                break;
            case "populatedFields":
                var populatedFields = new JsonArray();
                populatedFields.Add("logo");
                transformation[propertyName] = populatedFields;
                break;
            case "disposition":
                transformation[propertyName] = "applied_legacy_fallback";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported legacy library branding report mutation.");
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
