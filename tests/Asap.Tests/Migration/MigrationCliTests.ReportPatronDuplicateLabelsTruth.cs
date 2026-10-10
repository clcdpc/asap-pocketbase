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
    public async Task PatronDuplicateLabelReportMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-patron-duplicate-label-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPatronDuplicateLabelReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string sharedSourceId = "shared-patron-label-source-id";
        const string ignoredLegacySourceId = "modern-override-legacy-source";
        const string fallbackLabel = "Legacy fallback status";
        const string losingLegacyLabel = "Legacy value replaced by modern override";
        const string modernLabel = "Modern override status";
        const string uiDisposition = "ignored_library_ui_fields_not_effective_at_pinned_source";
        const string fallbackDisposition = "applied_legacy_fallback";
        const string ignoredLegacyDisposition = "ignored_modern_override_present";
        const int fallbackOrganizationId = 2;
        const int modernOrganizationId = 3;
        string[] ignoredUiFields =
        [
            "duplicateLabelSuggestion",
            "duplicateLabelOutstandingPurchase",
            "duplicateLabelPendingHold",
            "duplicateLabelHoldPlaced",
            "duplicateLabelClosed",
            "duplicateLabelRejected",
            "duplicateLabelHoldCompleted",
            "duplicateLabelHoldNotPickedUp",
            "duplicateLabelManual",
            "duplicateLabelSilent",
            "systemNotEnabledMessage",
            "publicationOptions"
        ];
        Directory.CreateDirectory(root);

        async Task AssertPatronDuplicateLabelSqlAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await AssertSuggestionLabelAsync(connection, fallbackOrganizationId, fallbackLabel);
            await AssertSuggestionLabelAsync(connection, modernOrganizationId, modernLabel);
        }

        static async Task AssertSuggestionLabelAsync(SqlConnection connection, int organizationId, string expectedLabel)
        {
            await using var command = new SqlCommand(
                "SELECT [SuggestionStatusLabel] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;",
                connection);
            command.Parameters.AddWithValue("@organizationId", organizationId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), $"Patron settings must exist for organization {organizationId}.");
            Assert.AreEqual(expectedLabel, reader.GetString(0));
            Assert.IsFalse(await reader.ReadAsync(), $"Patron settings must remain unique for organization {organizationId}.");
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                $$"""
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Modern Test Library', 'MODERN', 1, 2, 1);
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES
                    ('pbc-report-ui-settings', 'ui_settings'),
                    ('pbc-report-patron-overrides', 'patron_settings_overrides'),
                    ('pbc-report-patron-library-settings', 'patron_library_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [duplicateLabelSuggestion] TEXT,
                    [duplicateLabelOutstandingPurchase] TEXT,
                    [duplicateLabelPendingHold] TEXT,
                    [duplicateLabelHoldPlaced] TEXT,
                    [duplicateLabelClosed] TEXT,
                    [duplicateLabelRejected] TEXT,
                    [duplicateLabelHoldCompleted] TEXT,
                    [duplicateLabelHoldNotPickedUp] TEXT,
                    [duplicateLabelManual] TEXT,
                    [duplicateLabelSilent] TEXT,
                    [systemNotEnabledMessage] TEXT,
                    [publicationOptions] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('{{sharedSourceId}}', 'library', 'pb-org-2',
                     'Dormant UI suggestion', 'Dormant UI purchase', 'Dormant UI pending hold', 'Dormant UI placed hold',
                     'Dormant UI closed', 'Dormant UI rejected', 'Dormant UI completed hold', 'Dormant UI not-picked-up hold',
                     'Dormant UI manual', 'Dormant UI silent', 'Dormant UI system message', '["Dormant UI publication"]');
                CREATE TABLE [patron_library_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrganization] TEXT NOT NULL,
                    [duplicateRequestStatusLabels] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_library_settings] VALUES
                    ('{{sharedSourceId}}', 'pb-org-2', '{"suggestion":"{{fallbackLabel}}"}', '2029-02-01T00:00:00Z'),
                    ('{{ignoredLegacySourceId}}', 'pb-org-3', '{"suggestion":"{{losingLegacyLabel}}"}', '2029-02-01T00:00:00Z');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [duplicateStatusLabels] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('modern-override', '3', '{"suggestion":"{{modernLabel}}"}', '2029-03-01T00:00:00Z');
                """,
                exportedAtUtc: "2030-01-02T03:04:05Z");

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
            AssertPatronDuplicateLabelReportBaseline(
                validPending,
                sharedSourceId,
                ignoredLegacySourceId,
                fallbackOrganizationId,
                modernOrganizationId,
                uiDisposition,
                fallbackDisposition,
                ignoredLegacyDisposition,
                ignoredUiFields);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertPatronDuplicateLabelSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertPatronDuplicateLabelSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var mutations = new[]
            {
                (Collection: "ui_settings", SourceId: sharedSourceId, Property: "sourceCollection"),
                (Collection: "ui_settings", SourceId: sharedSourceId, Property: "sourceId"),
                (Collection: "ui_settings", SourceId: sharedSourceId, Property: "organizationId"),
                (Collection: "ui_settings", SourceId: sharedSourceId, Property: "disposition"),
                (Collection: "ui_settings", SourceId: sharedSourceId, Property: "populatedFields"),
                (Collection: "patron_library_settings", SourceId: sharedSourceId, Property: "sourceCollection"),
                (Collection: "patron_library_settings", SourceId: sharedSourceId, Property: "sourceId"),
                (Collection: "patron_library_settings", SourceId: sharedSourceId, Property: "organizationId"),
                (Collection: "patron_library_settings", SourceId: sharedSourceId, Property: "disposition"),
                (Collection: "patron_library_settings", SourceId: ignoredLegacySourceId, Property: "sourceCollection"),
                (Collection: "patron_library_settings", SourceId: ignoredLegacySourceId, Property: "sourceId"),
                (Collection: "patron_library_settings", SourceId: ignoredLegacySourceId, Property: "organizationId"),
                (Collection: "patron_library_settings", SourceId: ignoredLegacySourceId, Property: "disposition")
            };

            async Task RejectMutationAsync(
                bool preparedRecovery,
                string originalReport,
                (string Collection, string SourceId, string Property) mutation)
            {
                var propertyName = $"{mutation.Collection}.{mutation.SourceId}.{mutation.Property}";
                var tamperedReport = ApplyPatronDuplicateLabelReportMutation(
                    originalReport,
                    mutation.Collection,
                    mutation.SourceId,
                    mutation.Property,
                    ignoredUiFields);
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    preparedRecovery,
                    propertyName,
                    originalReport,
                    tamperedReport,
                    error => preparedRecovery
                        ? RunRecoverReport(package, reportPath, connectionEnvironmentName, error)
                        : RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            foreach (var mutation in mutations)
            {
                await RejectMutationAsync(true, validPending, mutation);
            }

            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertPatronDuplicateLabelReportBaseline(
                validRecoveredReport,
                sharedSourceId,
                ignoredLegacySourceId,
                fallbackOrganizationId,
                modernOrganizationId,
                uiDisposition,
                fallbackDisposition,
                ignoredLegacyDisposition,
                ignoredUiFields);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertPatronDuplicateLabelSqlAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            foreach (var mutation in mutations)
            {
                await RejectMutationAsync(false, validRecoveredReport, mutation);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertPatronDuplicateLabelSqlAsync();
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
                throw new InvalidOperationException("Patron duplicate-label report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertPatronDuplicateLabelReportBaseline(
        string reportJson,
        string sharedSourceId,
        string ignoredLegacySourceId,
        int fallbackOrganizationId,
        int modernOrganizationId,
        string uiDisposition,
        string fallbackDisposition,
        string ignoredLegacyDisposition,
        IReadOnlyList<string> ignoredUiFields)
    {
        using var report = JsonDocument.Parse(reportJson);
        var transformations = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "patron_duplicate_labels")
            .ToArray();
        Assert.AreEqual(3, transformations.Length);

        var ui = transformations.Single(item => item.GetProperty("sourceCollection").GetString() == "ui_settings");
        Assert.AreEqual(sharedSourceId, ui.GetProperty("sourceId").GetString());
        Assert.AreEqual(fallbackOrganizationId, ui.GetProperty("organizationId").GetInt32());
        Assert.AreEqual(uiDisposition, ui.GetProperty("disposition").GetString());
        CollectionAssert.AreEquivalent(
            ignoredUiFields.ToArray(),
            ui.GetProperty("populatedFields").EnumerateArray().Select(item => item.GetString()!).ToArray());

        var fallback = transformations.Single(item =>
            item.GetProperty("sourceCollection").GetString() == "patron_library_settings" &&
            item.GetProperty("sourceId").GetString() == sharedSourceId);
        Assert.AreEqual(fallbackOrganizationId, fallback.GetProperty("organizationId").GetInt32());
        Assert.AreEqual(fallbackDisposition, fallback.GetProperty("disposition").GetString());
        Assert.IsFalse(fallback.TryGetProperty("populatedFields", out _));

        var ignoredLegacy = transformations.Single(item =>
            item.GetProperty("sourceCollection").GetString() == "patron_library_settings" &&
            item.GetProperty("sourceId").GetString() == ignoredLegacySourceId);
        Assert.AreEqual(modernOrganizationId, ignoredLegacy.GetProperty("organizationId").GetInt32());
        Assert.AreEqual(ignoredLegacyDisposition, ignoredLegacy.GetProperty("disposition").GetString());
        Assert.IsFalse(ignoredLegacy.TryGetProperty("populatedFields", out _));
    }

    private static string ApplyPatronDuplicateLabelReportMutation(
        string reportJson,
        string sourceCollection,
        string sourceId,
        string propertyName,
        IReadOnlyList<string> ignoredUiFields)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformation = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "patron_duplicate_labels" &&
                item!["sourceCollection"]!.GetValue<string>() == sourceCollection &&
                item!["sourceId"]!.GetValue<string>() == sourceId)!.AsObject();
        switch (propertyName)
        {
            case "sourceCollection":
                if (sourceCollection == "ui_settings")
                {
                    transformation["sourceCollection"] = "patron_library_settings";
                    transformation.Remove("populatedFields");
                }
                else
                {
                    transformation["sourceCollection"] = "ui_settings";
                    var sourceUiFields = new JsonArray();
                    foreach (var field in ignoredUiFields)
                    {
                        sourceUiFields.Add(field);
                    }
                    transformation["populatedFields"] = sourceUiFields;
                }
                break;
            case "sourceId":
                transformation[propertyName] = "different-patron-settings-source";
                break;
            case "organizationId":
                transformation[propertyName] = transformation[propertyName]!.GetValue<int>() == 2 ? 3 : 2;
                break;
            case "disposition":
                transformation[propertyName] = sourceCollection == "ui_settings" || sourceId == "modern-override-legacy-source"
                    ? "applied_legacy_fallback"
                    : "ignored_modern_override_present";
                break;
            case "populatedFields":
                var populatedFields = transformation[propertyName]!.AsArray();
                populatedFields.RemoveAt(populatedFields.Count - 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported patron duplicate-label report mutation.");
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
