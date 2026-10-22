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
    public async Task PatronUiInheritanceReportMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-patron-ui-inheritance-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPatronUiInheritanceReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string libraryTwoSourceId = "ui-inheritance-library-two";
        const string libraryThreeSourceId = "ui-inheritance-library-three";
        const string libraryTwoOverrideId = "ui-inheritance-override-two";
        const string libraryThreeOverrideId = "ui-inheritance-override-three";
        const int libraryTwoOrganizationId = 2;
        const int libraryThreeOrganizationId = 3;
        const string configuredSystemPageTitle = "Configured system page title";
        const string libraryTwoPageTitle = "Library two custom page title";
        const string libraryTwoEbookMessage = "Library two modern eBook message";
        const string libraryTwoEaudiobookMessage = "Library two modern eAudiobook message";
        const string libraryThreeBarcodeLabel = "Library three custom barcode label";
        const string libraryThreeLoginPrompt = "Library three custom login prompt";
        const string libraryThreeSuccessTitle = "Library three custom success title";
        Directory.CreateDirectory(root);

        string[] allTextFields =
        [
            "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
            "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
            "eaudiobookMessage"
        ];
        string[] libraryTwoAffectedFields =
        [
            "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "noEmailMessage", "successTitle",
            "successMessage"
        ];
        string[] libraryThreeAffectedFields =
        [
            "pageTitle", "pinLabel", "loginNote", "suggestionFormNote", "noEmailMessage", "successMessage",
            "alreadySubmittedMessage", "ebookMessage", "eaudiobookMessage"
        ];
        var systemValues = new string?[]
        {
            configuredSystemPageTitle,
            "Configured system barcode label",
            "Configured system pin label",
            "Configured system login prompt",
            "Configured system login note",
            "Configured system suggestion form note",
            "Configured system no-email message",
            "Configured system success title",
            "Configured system success message",
            "Configured system already-submitted message",
            "Configured system eBook message",
            "Configured system eAudiobook message"
        };
        var libraryTwoValues = new string?[]
        {
            libraryTwoPageTitle,
            null,
            null,
            null,
            null,
            "Library two custom suggestion form note",
            null,
            null,
            null,
            "Library two custom already-submitted message",
            libraryTwoEbookMessage,
            libraryTwoEaudiobookMessage
        };
        var libraryThreeValues = new string?[]
        {
            null,
            libraryThreeBarcodeLabel,
            null,
            libraryThreeLoginPrompt,
            null,
            null,
            null,
            libraryThreeSuccessTitle,
            null,
            null,
            null,
            null
        };

        async Task AssertPatronUiInheritanceSqlAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            CollectionAssert.AreEqual(systemValues, await ReadPatronTextValuesAsync(connection, 1));
            CollectionAssert.AreEqual(libraryTwoValues, await ReadPatronTextValuesAsync(connection, libraryTwoOrganizationId));
            CollectionAssert.AreEqual(libraryThreeValues, await ReadPatronTextValuesAsync(connection, libraryThreeOrganizationId));
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                $$"""
                INSERT INTO [polaris_organizations] VALUES
                    ('pb-org-3', '3', 'Third Test Library', 'TEST3', 1, 2, 1);
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [updated] TEXT,
                    [pageTitle] TEXT,
                    [barcodeLabel] TEXT,
                    [pinLabel] TEXT,
                    [loginPrompt] TEXT,
                    [loginNote] TEXT,
                    [suggestionFormNote] TEXT,
                    [noEmailMessage] TEXT,
                    [successTitle] TEXT,
                    [successMessage] TEXT,
                    [alreadySubmittedMessage] TEXT,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('ui-system', 'system', NULL, '2029-12-01T00:00:00Z',
                     '{{configuredSystemPageTitle}}', 'Configured system barcode label', 'Configured system pin label',
                     'Configured system login prompt', 'Configured system login note', 'Configured system suggestion form note',
                     'Configured system no-email message', 'Configured system success title', 'Configured system success message',
                     'Configured system already-submitted message', 'Configured system eBook message', 'Configured system eAudiobook message'),
                    ('{{libraryTwoSourceId}}', 'library', 'pb-org-2', '2029-12-02T00:00:00Z',
                     '{{libraryTwoPageTitle}}', NULL, NULL, NULL, NULL, 'Library two custom suggestion form note',
                     NULL, NULL, NULL, 'Library two custom already-submitted message', NULL, NULL),
                    ('{{libraryThreeSourceId}}', 'library', 'pb-org-3', '2029-12-03T00:00:00Z',
                     NULL, '{{libraryThreeBarcodeLabel}}', NULL, '{{libraryThreeLoginPrompt}}', NULL, NULL,
                     NULL, '{{libraryThreeSuccessTitle}}', NULL, NULL, NULL, NULL);
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('{{libraryTwoOverrideId}}', '2', '{{libraryTwoEbookMessage}}', '{{libraryTwoEaudiobookMessage}}', '2029-12-04T00:00:00Z'),
                    ('{{libraryThreeOverrideId}}', '3', '', NULL, '2029-12-05T00:00:00Z');
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES ('pbc_fixture_ui_settings', 'ui_settings');
                INSERT INTO [_collections] VALUES ('pbc_report_patron_overrides', 'patron_settings_overrides');
                """);

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
            var authenticPendingReport = await File.ReadAllTextAsync(pendingPath);

            // This assertion is the first contract check: today the producer omits this documented projection.
            AssertPatronUiInheritanceReportBaseline(
                authenticPendingReport,
                libraryTwoSourceId,
                libraryTwoOrganizationId,
                libraryTwoAffectedFields,
                libraryThreeSourceId,
                libraryThreeOrganizationId,
                libraryThreeAffectedFields,
                allTextFields);
            using (var pending = JsonDocument.Parse(authenticPendingReport))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertPatronUiInheritanceSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertPatronUiInheritanceSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var mutations = new[]
            {
                "sourceCollection",
                "sourceId_pair_swap",
                "organizationId",
                "disposition",
                "affectedFields_omitted",
                "affectedFields_extra",
                "affectedFields_duplicate",
                "affectedFields_false_populated_field"
            };

            async Task RejectMutationAsync(bool preparedRecovery, string originalReport, string mutation)
            {
                var propertyName = $"patron_ui_inheritance.{mutation}";
                var tamperedReport = ApplyPatronUiInheritanceReportMutation(
                    originalReport,
                    mutation,
                    libraryTwoSourceId,
                    libraryThreeSourceId,
                    libraryThreeOrganizationId);
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
                await RejectMutationAsync(true, authenticPendingReport, mutation);
            }

            var reorderedPendingReport = ReorderPatronUiInheritanceFieldSets(authenticPendingReport);
            await File.WriteAllTextAsync(pendingPath, reorderedPendingReport, new UTF8Encoding(false));
            Assert.AreEqual(reorderedPendingReport, await File.ReadAllTextAsync(pendingPath));
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
            var expectedRecoveredReport = JsonNode.Parse(reorderedPendingReport)!.AsObject();
            expectedRecoveredReport["reportState"] = "recovered";
            expectedRecoveredReport["reconciliationPassed"] = true;
            Assert.AreEqual(
                expectedRecoveredReport.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                validRecoveredReport);
            AssertPatronUiInheritanceReportBaseline(
                validRecoveredReport,
                libraryTwoSourceId,
                libraryTwoOrganizationId,
                libraryTwoAffectedFields,
                libraryThreeSourceId,
                libraryThreeOrganizationId,
                libraryThreeAffectedFields,
                allTextFields);
            await AssertExternalStateUnchangedAsync("valid field-set reordering");

            foreach (var mutation in mutations)
            {
                await RejectMutationAsync(false, validRecoveredReport, mutation);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored authentic report");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Patron UI report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static async Task<string?[]> ReadPatronTextValuesAsync(SqlConnection connection, int organizationId)
    {
        await using var command = new SqlCommand(
            "SELECT [PageTitle], [BarcodeLabel], [PinLabel], [LoginPrompt], [LoginNote], [SuggestionFormNote], [NoEmailMessage], [SuccessTitle], [SuccessMessage], [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage] FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;",
            connection);
        command.Parameters.AddWithValue("@organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), $"Patron settings must exist for organization {organizationId}.");
        var values = new string?[12];
        for (var ordinal = 0; ordinal < values.Length; ordinal++)
        {
            values[ordinal] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        Assert.IsFalse(await reader.ReadAsync(), $"Patron settings must remain unique for organization {organizationId}.");
        return values;
    }

    private static void AssertPatronUiInheritanceReportBaseline(
        string reportJson,
        string libraryTwoSourceId,
        int libraryTwoOrganizationId,
        IReadOnlyList<string> libraryTwoAffectedFields,
        string libraryThreeSourceId,
        int libraryThreeOrganizationId,
        IReadOnlyList<string> libraryThreeAffectedFields,
        IReadOnlyList<string> allTextFields)
    {
        using var report = JsonDocument.Parse(reportJson);
        var transformations = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("entity", out var entity) &&
                entity.ValueKind == JsonValueKind.String &&
                string.Equals(entity.GetString(), "patron_ui_inheritance", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(2, transformations.Length, "Both partial library UI rows must report their distinct field-level inheritance corrections.");

        var bySourceId = transformations.ToDictionary(
            item => item.GetProperty("sourceId").GetString()!,
            StringComparer.Ordinal);
        AssertPatronUiInheritanceEntry(
            bySourceId[libraryTwoSourceId],
            libraryTwoSourceId,
            libraryTwoOrganizationId,
            libraryTwoAffectedFields);
        AssertPatronUiInheritanceEntry(
            bySourceId[libraryThreeSourceId],
            libraryThreeSourceId,
            libraryThreeOrganizationId,
            libraryThreeAffectedFields);

        var actualUnion = transformations
            .SelectMany(item => item.GetProperty("affectedFields").EnumerateArray())
            .Select(item => item.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            allTextFields.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            actualUnion,
            "The two report entries together must cover every ordinary patron UI text field.");
    }

    private static void AssertPatronUiInheritanceEntry(
        JsonElement entry,
        string sourceId,
        int organizationId,
        IReadOnlyList<string> expectedFields)
    {
        var propertyNames = entry.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "affectedFields", "disposition", "entity", "organizationId", "sourceCollection", "sourceId" },
            propertyNames,
            "The patron UI inheritance projection must retain its exact six-property shape.");
        Assert.AreEqual("ui_settings", entry.GetProperty("sourceCollection").GetString());
        Assert.AreEqual(sourceId, entry.GetProperty("sourceId").GetString());
        Assert.AreEqual(organizationId, entry.GetProperty("organizationId").GetInt32());
        Assert.AreEqual("blank_library_ui_fields_inherit_system_value", entry.GetProperty("disposition").GetString());

        var fields = entry.GetProperty("affectedFields").EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new AssertFailedException("Affected patron UI fields must be strings."))
            .ToArray();
        Assert.AreEqual(fields.Length, fields.Distinct(StringComparer.Ordinal).Count(), "Affected fields must be unique.");
        CollectionAssert.AreEquivalent(expectedFields.ToArray(), fields, "Affected fields are a set, not an ordering contract.");
    }

    private static string ReorderPatronUiInheritanceFieldSets(string reportJson)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformations = report["transformations"]!.AsArray();
        foreach (var transformation in transformations.Select(item => item!.AsObject())
                     .Where(item => string.Equals(item["entity"]!.GetValue<string>(), "patron_ui_inheritance", StringComparison.Ordinal)))
        {
            var fields = transformation["affectedFields"]!.AsArray();
            var reordered = fields.Select(item => item!.GetValue<string>()).Reverse().ToArray();
            fields.Clear();
            foreach (var field in reordered)
            {
                fields.Add(field);
            }
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string ApplyPatronUiInheritanceReportMutation(
        string reportJson,
        string mutation,
        string libraryTwoSourceId,
        string libraryThreeSourceId,
        int libraryThreeOrganizationId)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformations = report["transformations"]!.AsArray();
        var libraryTwo = FindPatronUiInheritanceEntry(transformations, libraryTwoSourceId);
        var libraryThree = FindPatronUiInheritanceEntry(transformations, libraryThreeSourceId);

        switch (mutation)
        {
            case "sourceCollection":
                libraryTwo["sourceCollection"] = "patron_settings_overrides";
                break;
            case "sourceId_pair_swap":
                libraryTwo["sourceId"] = libraryThreeSourceId;
                libraryThree["sourceId"] = libraryTwoSourceId;
                break;
            case "organizationId":
                libraryTwo["organizationId"] = libraryThreeOrganizationId;
                break;
            case "disposition":
                libraryTwo["disposition"] = "blank_library_ui_fields_use_source_default";
                break;
            case "affectedFields_omitted":
                libraryTwo["affectedFields"]!.AsArray().RemoveAt(0);
                break;
            case "affectedFields_extra":
                libraryTwo["affectedFields"]!.AsArray().Add("unsupportedPatronTextField");
                break;
            case "affectedFields_duplicate":
            {
                var fields = libraryTwo["affectedFields"]!.AsArray();
                fields.Add(fields[0]!.GetValue<string>());
                break;
            }
            case "affectedFields_false_populated_field":
            {
                var fields = libraryTwo["affectedFields"]!.AsArray();
                var barcodeIndex = Enumerable.Range(0, fields.Count)
                    .Single(index => string.Equals(fields[index]!.GetValue<string>(), "barcodeLabel", StringComparison.Ordinal));
                fields[barcodeIndex] = "pageTitle";
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown patron UI inheritance report mutation {mutation}.");
        }

        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonObject FindPatronUiInheritanceEntry(JsonArray transformations, string sourceId) =>
        transformations
            .Select(item => item!.AsObject())
            .Single(item =>
                string.Equals(item["entity"]!.GetValue<string>(), "patron_ui_inheritance", StringComparison.Ordinal) &&
                string.Equals(item["sourceId"]!.GetValue<string>(), sourceId, StringComparison.Ordinal));
}
