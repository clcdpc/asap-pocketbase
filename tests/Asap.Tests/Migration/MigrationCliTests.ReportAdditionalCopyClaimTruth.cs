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
    public async Task AdditionalCopyClaimReportMustMatchSourceAndMappedSqlForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-copy-claim-report-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationCopyClaimReport_{Guid.NewGuid():N}";
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

        async Task<Dictionary<string, long>> AssertTargetCopyClaimsAsync()
        {
            var staffIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var claims = new Dictionary<string, (string? StaffSourceId, string? DisplayName, DateTime? ClaimedAtUtc, string Status, int BibId, string? Notes)>(StringComparer.Ordinal);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var command = new SqlCommand(
                             "SELECT [PocketBaseId], [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] IN (N'copy-claim-staff-2', N'copy-claim-staff-3');",
                             connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    staffIds.Add(reader.GetString(0), reader.GetInt64(1));
                }
            }
            await using (var command = new SqlCommand(
                             "SELECT copyMap.[PocketBaseId], staffMap.[PocketBaseId], copy.[ClaimedByDisplayName], copy.[ClaimedAtUtc], copy.[Status], copy.[BibId], copy.[Notes] FROM [asap].[LegacyPocketBaseMapping] copyMap JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = copyMap.[NewId] LEFT JOIN [asap].[LegacyPocketBaseMapping] staffMap ON staffMap.[EntityType] = N'staff_user' AND staffMap.[NewId] = copy.[ClaimedByStaffUserId] WHERE copyMap.[EntityType] = N'additional_copy' AND copyMap.[PocketBaseId] IN (N'copy-claim-2', N'copy-claim-3');",
                             connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    claims.Add(
                        reader.GetString(0),
                        (reader.IsDBNull(1) ? null : reader.GetString(1),
                         reader.IsDBNull(2) ? null : reader.GetString(2),
                         reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                         reader.GetString(4),
                         reader.GetInt32(5),
                         reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }
            Assert.AreEqual(2, claims.Count, "Both source copies must retain their mapped target rows.");
            Assert.AreEqual("copy-claim-staff-2", claims["copy-claim-2"].StaffSourceId);
            Assert.AreEqual("Copy Claimant Two", claims["copy-claim-2"].DisplayName);
            Assert.AreEqual(new DateTime(2030, 1, 2, 10, 11, 12).Ticks, claims["copy-claim-2"].ClaimedAtUtc!.Value.Ticks);
            Assert.AreEqual("open", claims["copy-claim-2"].Status);
            Assert.AreEqual(7002, claims["copy-claim-2"].BibId);
            Assert.IsNull(claims["copy-claim-2"].Notes, "An eligible claim must not create a migration annotation.");
            Assert.AreEqual("copy-claim-staff-3", claims["copy-claim-3"].StaffSourceId);
            Assert.AreEqual("Copy Claimant Three", claims["copy-claim-3"].DisplayName);
            Assert.AreEqual(new DateTime(2030, 1, 3, 10, 11, 12).Ticks, claims["copy-claim-3"].ClaimedAtUtc!.Value.Ticks);
            Assert.AreEqual("open", claims["copy-claim-3"].Status);
            Assert.AreEqual(7003, claims["copy-claim-3"].BibId);
            Assert.IsNull(claims["copy-claim-3"].Notes, "An eligible claim must not create a migration annotation.");
            return staffIds;
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                INSERT INTO [staff_users] VALUES
                    ('copy-claim-staff-2', 'copy-claimant-two@example.org', 'copy-claimant-two', 'Copy Claimant Two', 'staff', 1, '2', 0, NULL, 0, 0, 0),
                    ('copy-claim-staff-3', 'copy-claimant-three@example.org', 'copy-claimant-three', 'Copy Claimant Three', 'staff', 1, '2', 0, NULL, 0, 0, 0);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('copy-claim-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [sourceTitleRequest] TEXT, [libraryOrgId] TEXT NOT NULL,
                    [libraryOrgName] TEXT, [bibid] TEXT NOT NULL, [title] TEXT, [author] TEXT, [format] TEXT,
                    [identifier] TEXT, [publication] TEXT, [status] TEXT NOT NULL, [notes] TEXT,
                    [createdByStaff] TEXT, [createdByUsername] TEXT, [closedByStaff] TEXT,
                    [closedByUsername] TEXT, [closedAt] TEXT, [created] TEXT, [updated] TEXT,
                    [claimedByStaffUserId] TEXT, [claimedByDisplayName] TEXT, [claimedAt] TEXT
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-claim-2', '', '2', 'Test Library', '7002', 'Copy Claim Two', 'Author Two', 'book', NULL, NULL, 'open', NULL,
                     'pb-staff-1', 'Source Administrator', NULL, NULL, NULL, '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z',
                     'copy-claim-staff-2', 'Copy Claimant Two', '2030-01-02T10:11:12Z'),
                    ('copy-claim-3', '', '2', 'Test Library', '7003', 'Copy Claim Three', 'Author Three', 'book', NULL, NULL, 'open', NULL,
                     'pb-staff-1', 'Source Administrator', NULL, NULL, NULL, '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z',
                     'copy-claim-staff-3', 'Copy Claimant Three', '2030-01-03T10:11:12Z');
                """);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "copy claim report validation preserves this key ring");
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
            Assert.IsTrue(File.Exists(pendingPath), "Committed copy import must leave its report pending.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            var targetStaffIds = await AssertTargetCopyClaimsAsync();
            AssertAdditionalCopyClaimBaseline(validPending, targetStaffIds);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                var currentStaffIds = await AssertTargetCopyClaimsAsync();
                CollectionAssert.AreEquivalent(targetStaffIds.Keys.ToArray(), currentStaffIds.Keys.ToArray());
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var properties = new[]
            {
                "sourceId", "sourceClaimantId", "mappedStaffUserId", "effectiveStaffUserId",
                "sourceDisplayName", "sourceClaimedAtUtc", "reason", "migrationAnnotationInserted"
            };
            foreach (var propertyName in properties)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    ApplyAdditionalCopyClaimMutation(validPending, propertyName, targetStaffIds["copy-claim-staff-3"]),
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
            AssertAdditionalCopyClaimBaseline(validRecoveredReport, targetStaffIds);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            CollectionAssert.AreEquivalent(targetStaffIds.Keys.ToArray(), (await AssertTargetCopyClaimsAsync()).Keys.ToArray());
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            foreach (var propertyName in properties)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    ApplyAdditionalCopyClaimMutation(validRecoveredReport, propertyName, targetStaffIds["copy-claim-staff-3"]),
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored additional-copy claim report");
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
                throw new InvalidOperationException("Additional-copy claim report cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertAdditionalCopyClaimBaseline(string reportJson, IReadOnlyDictionary<string, long> staffIds)
    {
        using var report = JsonDocument.Parse(reportJson);
        var claims = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "additional_copy_claim")
            .ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        Assert.AreEqual(2, claims.Count);
        AssertAdditionalCopyClaim(claims["copy-claim-2"], "copy-claim-staff-2", staffIds["copy-claim-staff-2"], "Copy Claimant Two", "2030-01-02T10:11:12Z");
        AssertAdditionalCopyClaim(claims["copy-claim-3"], "copy-claim-staff-3", staffIds["copy-claim-staff-3"], "Copy Claimant Three", "2030-01-03T10:11:12Z");
    }

    private static void AssertAdditionalCopyClaim(
        JsonElement claim,
        string claimantSourceId,
        long targetStaffId,
        string displayName,
        string claimedAtUtc)
    {
        Assert.AreEqual(claimantSourceId, claim.GetProperty("sourceClaimantId").GetString());
        Assert.AreEqual(targetStaffId, claim.GetProperty("mappedStaffUserId").GetInt64());
        Assert.AreEqual(targetStaffId, claim.GetProperty("effectiveStaffUserId").GetInt64());
        Assert.AreEqual(displayName, claim.GetProperty("sourceDisplayName").GetString());
        Assert.AreEqual(claimedAtUtc, claim.GetProperty("sourceClaimedAtUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.AreEqual("eligible", claim.GetProperty("reason").GetString());
        Assert.IsFalse(claim.GetProperty("migrationAnnotationInserted").GetBoolean());
    }

    private static string ApplyAdditionalCopyClaimMutation(string reportJson, string propertyName, long otherStaffTargetId)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var claims = report["transformations"]!.AsArray()
            .Where(item => item!["entity"]!.GetValue<string>() == "additional_copy_claim")
            .ToArray();
        var claim = claims.Single(item => item!["sourceId"]!.GetValue<string>() == "copy-claim-2")!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
                var other = claims.Single(item => item!["sourceId"]!.GetValue<string>() == "copy-claim-3")!.AsObject();
                claim["sourceId"] = "copy-claim-3";
                other["sourceId"] = "copy-claim-2";
                break;
            case "sourceClaimantId":
                claim["sourceClaimantId"] = "copy-claim-staff-3";
                break;
            case "mappedStaffUserId":
                claim["mappedStaffUserId"] = otherStaffTargetId;
                break;
            case "effectiveStaffUserId":
                claim["effectiveStaffUserId"] = otherStaffTargetId;
                break;
            case "sourceDisplayName":
                claim["sourceDisplayName"] = "Different Valid Copy Claimant";
                break;
            case "sourceClaimedAtUtc":
                claim["sourceClaimedAtUtc"] = "2030-02-01T10:11:12Z";
                break;
            case "reason":
                claim["reason"] = "claimant_unmapped";
                break;
            case "migrationAnnotationInserted":
                claim["migrationAnnotationInserted"] = true;
                break;
            default:
                Assert.Fail($"Unexpected additional-copy claim mutation: {propertyName}");
                break;
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
