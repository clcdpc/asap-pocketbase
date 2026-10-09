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
    public async Task FormatAutoClaimRuleReportMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-auto-claim-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationFormatAutoClaimReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string activeRuleId = "rule-active-mapped";
        const string inactiveRuleId = "rule-source-inactive";
        const string unmappedRuleId = "rule-assignee-unmapped";
        const string ineligibleRuleId = "rule-assignee-ineligible";
        const string activeStaffSourceId = "claimant-active";
        const string inactiveStaffSourceId = "claimant-inactive";
        Directory.CreateDirectory(root);

        async Task<long> ReadStaffTargetIdAsync(string sourceId)
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] = @sourceId;",
                connection);
            command.Parameters.AddWithValue("@sourceId", sourceId);
            var value = await command.ExecuteScalarAsync();
            Assert.IsNotNull(value, $"Staff source record {sourceId} must have a target mapping.");
            return Convert.ToInt64(value);
        }

        async Task AssertFormatAutoClaimSqlAsync(long activeStaffTargetId, long inactiveStaffTargetId)
        {
            var expected = new Dictionary<string, (string Format, long? StaffUserId, bool Active)>(StringComparer.Ordinal)
            {
                [activeRuleId] = ("book", activeStaffTargetId, true),
                [inactiveRuleId] = ("dvd", activeStaffTargetId, false),
                [unmappedRuleId] = ("ebook", null, false),
                [ineligibleRuleId] = ("music_cd", inactiveStaffTargetId, false)
            };
            var actual = new Dictionary<string, (int LibraryId, string Format, long? StaffUserId, bool Active)>(StringComparer.Ordinal);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT mapping.[PocketBaseId], claimRule.[LibraryOrganizationId], format.[Code], claimRule.[StaffUserId], claimRule.[IsActive]
                FROM [asap].[LegacyPocketBaseMapping] mapping
                JOIN [asap].[FormatAutoClaimRule] claimRule ON claimRule.[Id] = mapping.[NewId]
                JOIN [asap].[MaterialFormat] format ON format.[Id] = claimRule.[MaterialFormatId]
                WHERE mapping.[EntityType] = N'format_auto_claim_rule';
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual.Add(
                    reader.GetString(0),
                    (
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.GetBoolean(4)));
            }

            Assert.AreEqual(expected.Count, actual.Count, "The imported claim-rule SQL rows must match the complete source population.");
            foreach (var (sourceId, expectedRow) in expected)
            {
                Assert.IsTrue(actual.TryGetValue(sourceId, out var actualRow), $"Claim rule {sourceId} must have an imported target row.");
                Assert.AreEqual(2, actualRow.LibraryId, $"Claim rule {sourceId} must remain scoped to its source library.");
                Assert.AreEqual(expectedRow.Format, actualRow.Format, $"Claim rule {sourceId} must retain its source format.");
                Assert.AreEqual(expectedRow.StaffUserId, actualRow.StaffUserId, $"Claim rule {sourceId} must retain its source staff mapping.");
                Assert.AreEqual(expectedRow.Active, actualRow.Active, $"Claim rule {sourceId} must retain its independently derived eligibility state.");
            }
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                INSERT INTO [staff_users] VALUES
                    ('claimant-active', 'active-claimant@example.org', 'active-claimant', 'Active Claimant',
                     'staff', 1, '2', 0, NULL, 0, 0, 0),
                    ('claimant-inactive', 'inactive@staff.asap.local', 'inactive-claimant', 'Inactive Claimant',
                     'staff', 0, '2', 0, NULL, 0, 0, 0);
                CREATE TABLE [format_claim_rules]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrgId] TEXT NOT NULL,
                    [format] TEXT NOT NULL,
                    [staffUserId] TEXT,
                    [staffUser] TEXT,
                    [active] INTEGER NOT NULL,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [format_claim_rules] VALUES
                    ('rule-active-mapped', '2', 'book', 'claimant-active', NULL, 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-source-inactive', '2', 'dvd', NULL, 'claimant-active', 0, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-assignee-unmapped', '2', 'ebook', 'missing-claimant', NULL, 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-assignee-ineligible', '2', 'music_cd', NULL, 'claimant-inactive', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
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
            var validPending = await File.ReadAllTextAsync(pendingPath);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var activeStaffTargetId = await ReadStaffTargetIdAsync(activeStaffSourceId);
            var inactiveStaffTargetId = await ReadStaffTargetIdAsync(inactiveStaffSourceId);
            AssertFormatAutoClaimRuleReportBaseline(validPending, activeStaffTargetId, inactiveStaffTargetId);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertFormatAutoClaimSqlAsync(activeStaffTargetId, inactiveStaffTargetId);
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertFormatAutoClaimSqlAsync(activeStaffTargetId, inactiveStaffTargetId);
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            (string SourceId, string Property)[] mutations =
            [
                (activeRuleId, "reason"),
                (activeRuleId, "sourceId"),
                (activeRuleId, "sourceStaffUserId"),
                (activeRuleId, "targetStaffUserId"),
                (activeRuleId, "sourceActive"),
                (activeRuleId, "targetActive"),
                (inactiveRuleId, "sourceStaffUserId"),
                (inactiveRuleId, "targetStaffUserId"),
                (inactiveRuleId, "sourceActive"),
                (inactiveRuleId, "targetActive"),
                (inactiveRuleId, "reason"),
                (unmappedRuleId, "sourceStaffUserId"),
                (unmappedRuleId, "targetStaffUserId"),
                (unmappedRuleId, "targetActive"),
                (unmappedRuleId, "reason"),
                (ineligibleRuleId, "sourceStaffUserId"),
                (ineligibleRuleId, "targetStaffUserId"),
                (ineligibleRuleId, "targetActive"),
                (ineligibleRuleId, "reason")
            ];

            async Task RejectMutationAsync(
                bool preparedRecovery,
                string originalReport,
                (string SourceId, string Property) mutation)
            {
                var propertyName = $"format_auto_claim_rule.{mutation.SourceId}.{mutation.Property}";
                var tamperedReport = ApplyFormatAutoClaimRuleReportMutation(
                    originalReport,
                    mutation.SourceId,
                    mutation.Property,
                    activeStaffTargetId,
                    inactiveStaffTargetId);
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
            AssertFormatAutoClaimRuleReportBaseline(validRecoveredReport, activeStaffTargetId, inactiveStaffTargetId);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertFormatAutoClaimSqlAsync(activeStaffTargetId, inactiveStaffTargetId);
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
            await AssertFormatAutoClaimSqlAsync(activeStaffTargetId, inactiveStaffTargetId);
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
                throw new InvalidOperationException("Format auto-claim report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertFormatAutoClaimRuleReportBaseline(
        string reportJson,
        long activeStaffTargetId,
        long inactiveStaffTargetId)
    {
        var expected = new Dictionary<string, (string? SourceStaffId, long? TargetStaffId, bool SourceActive, bool TargetActive, string Reason)>(StringComparer.Ordinal)
        {
            ["rule-active-mapped"] = ("claimant-active", activeStaffTargetId, true, true, "eligible"),
            ["rule-source-inactive"] = ("claimant-active", activeStaffTargetId, false, false, "source_inactive"),
            ["rule-assignee-unmapped"] = ("missing-claimant", null, true, false, "assignee_unmapped"),
            ["rule-assignee-ineligible"] = ("claimant-inactive", inactiveStaffTargetId, true, false, "assignee_ineligible")
        };
        using var report = JsonDocument.Parse(reportJson);
        var transformations = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "format_auto_claim_rule")
            .ToArray();
        Assert.AreEqual(expected.Count, transformations.Length);
        var actualById = transformations.ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        foreach (var (sourceId, expectedRow) in expected)
        {
            Assert.IsTrue(actualById.TryGetValue(sourceId, out var actual), $"The report must include source rule {sourceId} exactly once.");
            var sourceStaffId = actual.GetProperty("sourceStaffUserId").ValueKind == JsonValueKind.Null
                ? null
                : actual.GetProperty("sourceStaffUserId").GetString();
            var targetStaffNode = actual.GetProperty("targetStaffUserId");
            long? targetStaffId = targetStaffNode.ValueKind == JsonValueKind.Null ? null : targetStaffNode.GetInt64();
            Assert.AreEqual(expectedRow.SourceStaffId, sourceStaffId, $"The report source staff reference for {sourceId} must remain exact.");
            Assert.AreEqual(expectedRow.TargetStaffId, targetStaffId, $"The report target staff mapping for {sourceId} must match SQL.");
            Assert.AreEqual(expectedRow.SourceActive, actual.GetProperty("sourceActive").GetBoolean(), $"The report must retain source activity for {sourceId}.");
            Assert.AreEqual(expectedRow.TargetActive, actual.GetProperty("targetActive").GetBoolean(), $"The report must retain derived target activity for {sourceId}.");
            Assert.AreEqual(expectedRow.Reason, actual.GetProperty("reason").GetString(), $"The report must retain the derived eligibility reason for {sourceId}.");
        }
    }

    private static string ApplyFormatAutoClaimRuleReportMutation(
        string reportJson,
        string sourceId,
        string propertyName,
        long activeStaffTargetId,
        long inactiveStaffTargetId)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformation = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "format_auto_claim_rule" &&
                item!["sourceId"]!.GetValue<string>() == sourceId)!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
            {
                var inactiveTransformation = report["transformations"]!.AsArray()
                    .Single(item => item!["entity"]!.GetValue<string>() == "format_auto_claim_rule" &&
                        item!["sourceId"]!.GetValue<string>() == "rule-source-inactive")!.AsObject();
                transformation[propertyName] = "rule-source-inactive";
                inactiveTransformation[propertyName] = "rule-active-mapped";
                break;
            }
            case "sourceStaffUserId":
                transformation[propertyName] = sourceId == "rule-assignee-unmapped" || sourceId == "rule-assignee-ineligible"
                    ? "claimant-active"
                    : "claimant-inactive";
                break;
            case "targetStaffUserId":
                transformation[propertyName] = sourceId == "rule-assignee-unmapped"
                    ? activeStaffTargetId
                    : sourceId == "rule-assignee-ineligible"
                        ? activeStaffTargetId
                        : sourceId == "rule-source-inactive"
                            ? inactiveStaffTargetId
                            : inactiveStaffTargetId;
                break;
            case "sourceActive":
            case "targetActive":
                transformation[propertyName] = !transformation[propertyName]!.GetValue<bool>();
                break;
            case "reason":
                transformation[propertyName] = sourceId switch
                {
                    "rule-active-mapped" => "assignee_ineligible",
                    "rule-source-inactive" => "eligible",
                    "rule-assignee-unmapped" => "eligible",
                    "rule-assignee-ineligible" => "eligible",
                    _ => throw new ArgumentOutOfRangeException(nameof(sourceId), sourceId, "Unsupported claim-rule report mutation.")
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported claim-rule report mutation.");
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
