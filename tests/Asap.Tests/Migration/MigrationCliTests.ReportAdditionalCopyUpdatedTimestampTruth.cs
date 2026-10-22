using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task AdditionalCopyUpdatedTimestampReportMustMatchSourceForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-additional-copy-timestamp-report-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationAdditionalCopyTimestampReportTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string missingNullSourceId = "copy-missing-updated-null";
        const string missingWhitespaceSourceId = "copy-missing-updated-whitespace";
        const string populatedUpdatedSourceId = "copy-populated-updated";
        const string missingNullCreatedUtc = "2030-01-02T03:04:05.1234567Z";
        const string missingWhitespaceCreatedUtc = "2030-01-03T06:07:08.7654321Z";
        const string populatedCreatedUtc = "2030-01-04T09:10:11.1111111Z";
        const string populatedUpdatedUtc = "2030-01-05T12:13:14.2222222Z";
        Directory.CreateDirectory(root);

        static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        async Task AssertAdditionalCopyTimestampSqlAsync()
        {
            var expected = new Dictionary<string, (DateTime CreatedUtc, DateTime UpdatedUtc)>(StringComparer.Ordinal)
            {
                [missingNullSourceId] = (Utc(missingNullCreatedUtc).UtcDateTime, Utc(missingNullCreatedUtc).UtcDateTime),
                [missingWhitespaceSourceId] = (Utc(missingWhitespaceCreatedUtc).UtcDateTime, Utc(missingWhitespaceCreatedUtc).UtcDateTime),
                [populatedUpdatedSourceId] = (Utc(populatedCreatedUtc).UtcDateTime, Utc(populatedUpdatedUtc).UtcDateTime)
            };
            var actual = new Dictionary<string, (DateTime CreatedUtc, DateTime UpdatedUtc)>(StringComparer.Ordinal);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT mapping.[PocketBaseId], copy.[CreatedUtc], copy.[UpdatedUtc]
                FROM [asap].[LegacyPocketBaseMapping] mapping
                JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = mapping.[NewId]
                WHERE mapping.[EntityType] = N'additional_copy';
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0), (reader.GetDateTime(1), reader.GetDateTime(2)));
            }

            Assert.AreEqual(expected.Count, actual.Count, "The imported additional-copy population must remain exact.");
            foreach (var (sourceId, expectedTimes) in expected)
            {
                Assert.IsTrue(actual.TryGetValue(sourceId, out var actualTimes), $"Additional copy {sourceId} must have an SQL mapping.");
                Assert.AreEqual(expectedTimes.CreatedUtc, actualTimes.CreatedUtc, $"Additional copy {sourceId} must preserve source creation time.");
                Assert.AreEqual(expectedTimes.UpdatedUtc, actualTimes.UpdatedUtc, $"Additional copy {sourceId} must preserve the current updated-time fallback.");
            }
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrgId] TEXT NOT NULL,
                    [bibid] TEXT NOT NULL,
                    [title] TEXT NOT NULL,
                    [status] TEXT NOT NULL,
                    [created] TEXT NOT NULL,
                    [updated] TEXT
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-missing-updated-null', '2', '7101', 'Null updated time', 'open', '2030-01-02T03:04:05.1234567Z', NULL),
                    ('copy-missing-updated-whitespace', '2', '7102', 'Whitespace updated time', 'open', '2030-01-03T06:07:08.7654321Z', '   '),
                    ('copy-populated-updated', '2', '7103', 'Populated updated time', 'open', '2030-01-04T09:10:11.1111111Z', '2030-01-05T12:13:14.2222222Z');
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
            AssertAdditionalCopyTimestampReportBaseline(validPending);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertAdditionalCopyTimestampSqlAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertAdditionalCopyTimestampSqlAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var mutations = new (string SourceId, string Property)[]
            {
                (missingNullSourceId, "sourceUpdatedUtc"),
                (missingNullSourceId, "targetUpdatedUtc"),
                (missingNullSourceId, "reason"),
                (missingWhitespaceSourceId, "sourceUpdatedUtc"),
                (missingWhitespaceSourceId, "targetUpdatedUtc"),
                (missingWhitespaceSourceId, "reason"),
                (missingNullSourceId, "sourceId")
            };

            async Task RejectMutationAsync(
                bool preparedRecovery,
                string originalReport,
                (string SourceId, string Property) mutation)
            {
                var propertyName = $"additional_copy_updated_timestamp.{mutation.SourceId}.{mutation.Property}";
                var tamperedReport = ApplyAdditionalCopyTimestampReportMutation(
                    originalReport,
                    mutation.SourceId,
                    mutation.Property,
                    missingNullSourceId,
                    missingWhitespaceSourceId,
                    missingNullCreatedUtc,
                    missingWhitespaceCreatedUtc,
                    populatedUpdatedUtc);
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
            AssertAdditionalCopyTimestampReportBaseline(validRecoveredReport);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertAdditionalCopyTimestampSqlAsync();
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
            await AssertAdditionalCopyTimestampSqlAsync();
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
                throw new InvalidOperationException("Additional-copy timestamp report-truth cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertAdditionalCopyTimestampReportBaseline(string reportJson)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["copy-missing-updated-null"] = "2030-01-02T03:04:05.1234567Z",
            ["copy-missing-updated-whitespace"] = "2030-01-03T06:07:08.7654321Z"
        };
        using var report = JsonDocument.Parse(reportJson);
        var transformations = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "additional_copy_updated_timestamp")
            .ToArray();
        Assert.AreEqual(expected.Count, transformations.Length, "Only copies with a missing updated value have timestamp transformations.");
        var actualById = transformations.ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        Assert.IsFalse(actualById.ContainsKey("copy-populated-updated"), "A populated source updated value must not have a fallback transformation.");
        foreach (var (sourceId, expectedTargetUtc) in expected)
        {
            Assert.IsTrue(actualById.TryGetValue(sourceId, out var actual), $"The report must include missing-updated source row {sourceId}.");
            Assert.AreEqual(JsonValueKind.Null, actual.GetProperty("sourceUpdatedUtc").ValueKind);
            Assert.AreEqual(
                DateTimeOffset.Parse(
                    expectedTargetUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                actual.GetProperty("targetUpdatedUtc").GetDateTimeOffset());
            Assert.AreEqual("missing_updated_uses_created", actual.GetProperty("reason").GetString());
        }
    }

    private static string ApplyAdditionalCopyTimestampReportMutation(
        string reportJson,
        string sourceId,
        string propertyName,
        string missingNullSourceId,
        string missingWhitespaceSourceId,
        string missingNullCreatedUtc,
        string missingWhitespaceCreatedUtc,
        string populatedUpdatedUtc)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var transformations = report["transformations"]!.AsArray();
        var transformation = transformations
            .Single(item => item!["entity"]!.GetValue<string>() == "additional_copy_updated_timestamp" &&
                item!["sourceId"]!.GetValue<string>() == sourceId)!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
            {
                var otherTransformation = transformations
                    .Single(item => item!["entity"]!.GetValue<string>() == "additional_copy_updated_timestamp" &&
                        item!["sourceId"]!.GetValue<string>() == missingWhitespaceSourceId)!.AsObject();
                transformation[propertyName] = missingWhitespaceSourceId;
                otherTransformation[propertyName] = missingNullSourceId;
                break;
            }
            case "sourceUpdatedUtc":
                transformation[propertyName] = populatedUpdatedUtc;
                break;
            case "targetUpdatedUtc":
                transformation[propertyName] = sourceId == missingNullSourceId
                    ? missingWhitespaceCreatedUtc
                    : missingNullCreatedUtc;
                break;
            case "reason":
                transformation[propertyName] = "missing_updated_uses_creation_time";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unsupported additional-copy timestamp report mutation.");
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
