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
    public async Task PlacedBibProtectionReportMustMatchPlacedSourceEvidenceAndSqlForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-placed-bib-report-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPlacedBibReport_{Guid.NewGuid():N}";
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

        async Task AssertPlacedBibTargetAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT mapping.[PocketBaseId], request.[LegacyHoldProtected], JSON_VALUE(event.[MetadataJson], '$.sourceTitleRequestId'), JSON_VALUE(event.[MetadataJson], '$.bibId'), (SELECT COUNT(*) FROM OPENJSON(event.[MetadataJson], '$.evidence')), (SELECT COUNT(*) FROM OPENJSON(event.[MetadataJson], '$.bibSources')) FROM [asap].[LegacyPocketBaseMapping] mapping JOIN [asap].[TitleRequest] request ON request.[Id] = mapping.[NewId] JOIN [asap].[TitleRequestEvent] event ON event.[TitleRequestId] = request.[Id] AND event.[EventType] = N'legacy' AND JSON_VALUE(event.[MetadataJson], '$.transform') = N'placed_bib_protection_v1' WHERE mapping.[EntityType] = N'title_request' AND mapping.[PocketBaseId] IN (N'placed-request-2', N'placed-request-3');",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var sourceId = reader.GetString(0);
                Assert.IsTrue(seen.Add(sourceId), "Each source request must have exactly one protection marker.");
                Assert.IsTrue(reader.GetBoolean(1), "The placed request must retain its protected-hold state.");
                Assert.AreEqual(sourceId, reader.GetString(2), "Marker metadata must retain the exact source request identity.");
                Assert.AreEqual(sourceId == "placed-request-2" ? "9001" : "9002", reader.GetString(3));
                Assert.AreEqual(2, reader.GetInt32(4), "Marker metadata must preserve both independent evidence records.");
                Assert.AreEqual(2, reader.GetInt32(5), "Marker metadata must preserve request and event BIB source records.");
            }
            CollectionAssert.AreEquivalent(new[] { "placed-request-2", "placed-request-3" }, seen.ToArray());
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('placed-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [bibid] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('placed-request-2', '2', 'placed-book', 'A20000000000002', 'Placed Two', 0, 'hold_placed', '09001', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('placed-request-3', '2', 'placed-book', 'A20000000000003', 'Placed Three', 0, 'hold_placed', '09002', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL, [eventType] TEXT NOT NULL,
                    [fromStatus] TEXT, [toStatus] TEXT, [closeReason] TEXT, [actorType] TEXT NOT NULL,
                    [actorName] TEXT, [message] TEXT, [metadata] TEXT, [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('placed-event-2', 'placed-request-2', 'hold_placed', NULL, NULL, NULL, 'system', NULL, 'Placed two', '{"bibId":"09001"}', '2029-01-03T00:00:00Z'),
                    ('placed-event-3', 'placed-request-3', 'hold_placed', NULL, NULL, NULL, 'system', NULL, 'Placed three', '{"bibId":"09002"}', '2029-01-04T00:00:00Z');
                """);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "placed-BIB report validation preserves this key ring");
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
            Assert.IsTrue(File.Exists(pendingPath), "Committed placement import must leave its report pending.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            AssertPlacedBibProtectionBaseline(validPending);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertPlacedBibTargetAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                await AssertPlacedBibTargetAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var mutations = new[]
            {
                "sourceId", "libraryOrganizationId", "status", "bibId", "evidenceCount",
                "evidenceKind", "evidenceSourceCollection", "evidenceSourceRecordId", "evidenceSourceField", "evidenceValue",
                "hints", "bibSourcesCount", "bibSourcesSourceCollection", "bibSourcesSourceRecordId",
                "bibSourcesSourceField", "bibSourcesBibId", "bibSourcesSourceValue", "action"
            };
            foreach (var propertyName in mutations)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    ApplyPlacedBibProtectionMutation(validPending, propertyName),
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
            AssertPlacedBibProtectionBaseline(validRecoveredReport);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertPlacedBibTargetAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            foreach (var propertyName in mutations)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    ApplyPlacedBibProtectionMutation(validRecoveredReport, propertyName),
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored placed-BIB report");
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
                throw new InvalidOperationException("Placed-BIB report cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertPlacedBibProtectionBaseline(string reportJson)
    {
        using var report = JsonDocument.Parse(reportJson);
        var protections = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "placed_bib_protection")
            .ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        Assert.AreEqual(2, protections.Count);
        AssertPlacedBibProtection(protections["placed-request-2"], "placed-request-2", "placed-event-2", 9001, "09001");
        AssertPlacedBibProtection(protections["placed-request-3"], "placed-request-3", "placed-event-3", 9002, "09002");
    }

    private static void AssertPlacedBibProtection(
        JsonElement protection,
        string sourceId,
        string eventId,
        int bibId,
        string sourceBibValue)
    {
        Assert.AreEqual(2, protection.GetProperty("libraryOrganizationId").GetInt32());
        Assert.AreEqual("hold_placed", protection.GetProperty("status").GetString());
        Assert.AreEqual(bibId, protection.GetProperty("bibId").GetInt32());
        Assert.AreEqual(2, protection.GetProperty("evidence").GetArrayLength());
        Assert.AreEqual(0, protection.GetProperty("hints").GetArrayLength());
        Assert.AreEqual(2, protection.GetProperty("bibSources").GetArrayLength());
        Assert.AreEqual("inserted", protection.GetProperty("action").GetString());

        var evidence = protection.GetProperty("evidence").EnumerateArray().ToArray();
        var currentStatus = evidence.Single(item => item.GetProperty("kind").GetString() == "current_status");
        Assert.AreEqual("title_requests", currentStatus.GetProperty("sourceCollection").GetString());
        Assert.AreEqual(sourceId, currentStatus.GetProperty("sourceRecordId").GetString());
        Assert.AreEqual("status", currentStatus.GetProperty("sourceField").GetString());
        Assert.AreEqual("hold_placed", currentStatus.GetProperty("value").GetString());
        var dedicatedEvent = evidence.Single(item => item.GetProperty("kind").GetString() == "dedicated_event");
        Assert.AreEqual("title_request_events", dedicatedEvent.GetProperty("sourceCollection").GetString());
        Assert.AreEqual(eventId, dedicatedEvent.GetProperty("sourceRecordId").GetString());
        Assert.AreEqual("eventType", dedicatedEvent.GetProperty("sourceField").GetString());
        Assert.AreEqual("hold_placed", dedicatedEvent.GetProperty("value").GetString());

        var bibSources = protection.GetProperty("bibSources").EnumerateArray().ToArray();
        var requestBib = bibSources.Single(item => item.GetProperty("sourceCollection").GetString() == "title_requests");
        Assert.AreEqual(sourceId, requestBib.GetProperty("sourceRecordId").GetString());
        Assert.AreEqual("bibid", requestBib.GetProperty("sourceField").GetString());
        Assert.AreEqual(bibId, requestBib.GetProperty("bibId").GetInt32());
        Assert.AreEqual(sourceBibValue, requestBib.GetProperty("sourceValue").GetString());
        var eventBib = bibSources.Single(item => item.GetProperty("sourceCollection").GetString() == "title_request_events");
        Assert.AreEqual(eventId, eventBib.GetProperty("sourceRecordId").GetString());
        Assert.AreEqual("metadata.bibId", eventBib.GetProperty("sourceField").GetString());
        Assert.AreEqual(bibId, eventBib.GetProperty("bibId").GetInt32());
        Assert.AreEqual(sourceBibValue, eventBib.GetProperty("sourceValue").GetString());
    }

    private static string ApplyPlacedBibProtectionMutation(string reportJson, string propertyName)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var protections = report["transformations"]!.AsArray()
            .Where(item => item!["entity"]!.GetValue<string>() == "placed_bib_protection")
            .ToArray();
        var protection = protections.Single(item => item!["sourceId"]!.GetValue<string>() == "placed-request-2")!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
                var other = protections.Single(item => item!["sourceId"]!.GetValue<string>() == "placed-request-3")!.AsObject();
                protection["sourceId"] = "placed-request-3";
                other["sourceId"] = "placed-request-2";
                break;
            case "libraryOrganizationId":
                protection["libraryOrganizationId"] = 20;
                break;
            case "status":
                protection["status"] = "pending_hold";
                break;
            case "bibId":
                protection["bibId"] = 9002;
                break;
            case "evidenceCount":
                protection["evidence"]!.AsArray().RemoveAt(0);
                break;
            case "evidenceKind":
                FindPlacedEvidence(protection, "current_status")["kind"] = "transition_to_placed";
                break;
            case "evidenceSourceCollection":
                FindPlacedEvidence(protection, "current_status")["sourceCollection"] = "title_request_events";
                break;
            case "evidenceSourceRecordId":
                FindPlacedEvidence(protection, "current_status")["sourceRecordId"] = "placed-request-3";
                break;
            case "evidenceSourceField":
                FindPlacedEvidence(protection, "current_status")["sourceField"] = "statusRef";
                break;
            case "evidenceValue":
                FindPlacedEvidence(protection, "current_status")["value"] = "pending_hold";
                break;
            case "hints":
                protection["hints"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "recorded_bib_hint",
                        ["sourceCollection"] = "title_requests",
                        ["sourceRecordId"] = "placed-request-2",
                        ["sourceField"] = "bibid",
                        ["value"] = "09001"
                    }
                };
                break;
            case "bibSourcesCount":
                protection["bibSources"]!.AsArray().RemoveAt(0);
                break;
            case "bibSourcesSourceCollection":
                FindPlacedBibSource(protection, "title_requests")["sourceCollection"] = "title_request_events";
                break;
            case "bibSourcesSourceRecordId":
                FindPlacedBibSource(protection, "title_requests")["sourceRecordId"] = "placed-event-2";
                break;
            case "bibSourcesSourceField":
                FindPlacedBibSource(protection, "title_requests")["sourceField"] = "metadata.bibId";
                break;
            case "bibSourcesBibId":
                FindPlacedBibSource(protection, "title_requests")["bibId"] = 9002;
                break;
            case "bibSourcesSourceValue":
                FindPlacedBibSource(protection, "title_requests")["sourceValue"] = "09002";
                break;
            case "action":
                protection["action"] = "no_placement_evidence";
                break;
            default:
                Assert.Fail($"Unexpected placed-BIB mutation: {propertyName}");
                break;
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject FindPlacedEvidence(JsonObject protection, string kind) =>
        protection["evidence"]!.AsArray()
            .Single(item => item!["kind"]!.GetValue<string>() == kind)!.AsObject();

    private static JsonObject FindPlacedBibSource(JsonObject protection, string sourceCollection) =>
        protection["bibSources"]!.AsArray()
            .Single(item => item!["sourceCollection"]!.GetValue<string>() == sourceCollection)!.AsObject();
}
