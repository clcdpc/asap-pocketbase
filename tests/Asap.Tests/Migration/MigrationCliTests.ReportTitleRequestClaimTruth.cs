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
    public async Task TitleRequestClaimReportMustMatchSourceAndMappedSqlForRecoveryAndReconciliation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-title-claim-report-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationTitleClaimReport_{Guid.NewGuid():N}";
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

        async Task<Dictionary<string, long>> AssertTargetClaimsAsync()
        {
            var staffIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var claims = new Dictionary<string, (string? StaffSourceId, string? DisplayName, DateTime? ClaimedAtUtc, string? ClaimType, string? RuleSourceId)>(StringComparer.Ordinal);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var command = new SqlCommand(
                             "SELECT [PocketBaseId], [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] IN (N'claim-staff-2', N'claim-staff-3');",
                             connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    staffIds.Add(reader.GetString(0), reader.GetInt64(1));
                }
            }
            await using (var command = new SqlCommand(
                             "SELECT requestMap.[PocketBaseId], staffMap.[PocketBaseId], targetRequest.[ClaimedByDisplayName], targetRequest.[ClaimedAtUtc], targetRequest.[ClaimType], ruleMap.[PocketBaseId] FROM [asap].[LegacyPocketBaseMapping] requestMap JOIN [asap].[TitleRequest] targetRequest ON targetRequest.[Id] = requestMap.[NewId] LEFT JOIN [asap].[LegacyPocketBaseMapping] staffMap ON staffMap.[EntityType] = N'staff_user' AND staffMap.[NewId] = targetRequest.[ClaimedByStaffUserId] LEFT JOIN [asap].[LegacyPocketBaseMapping] ruleMap ON ruleMap.[EntityType] = N'format_auto_claim_rule' AND ruleMap.[NewId] = targetRequest.[ClaimRuleId] WHERE requestMap.[EntityType] = N'title_request' AND requestMap.[PocketBaseId] IN (N'claim-request-2', N'claim-request-3');",
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
                         reader.IsDBNull(4) ? null : reader.GetString(4),
                         reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }
            Assert.AreEqual(2, claims.Count, "Both source requests must retain their mapped target SQL rows.");
            Assert.AreEqual("claim-staff-2", claims["claim-request-2"].StaffSourceId);
            Assert.AreEqual("Claimant Two", claims["claim-request-2"].DisplayName);
            Assert.AreEqual(new DateTime(2030, 1, 2, 10, 11, 12).Ticks, claims["claim-request-2"].ClaimedAtUtc!.Value.Ticks);
            Assert.AreEqual("automatic_format_rule", claims["claim-request-2"].ClaimType);
            Assert.AreEqual("claim-rule-2", claims["claim-request-2"].RuleSourceId);
            Assert.AreEqual("claim-staff-3", claims["claim-request-3"].StaffSourceId);
            Assert.AreEqual("Claimant Three", claims["claim-request-3"].DisplayName);
            Assert.AreEqual(new DateTime(2030, 1, 3, 10, 11, 12).Ticks, claims["claim-request-3"].ClaimedAtUtc!.Value.Ticks);
            Assert.AreEqual("automatic_format_rule", claims["claim-request-3"].ClaimType);
            Assert.AreEqual("claim-rule-3", claims["claim-request-3"].RuleSourceId);
            Assert.AreEqual(
                0,
                await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] event JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'title_request' AND mapping.[NewId] = event.[TitleRequestId] WHERE mapping.[PocketBaseId] IN (N'claim-request-2', N'claim-request-3') AND event.[EventType] = N'legacy' AND JSON_VALUE(event.[MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';"),
                "Eligible claim attributions must not insert normalization annotations.");
            return staffIds;
        }

        try
        {
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                INSERT INTO [staff_users] VALUES
                    ('claim-staff-2', 'claimant-two@example.org', 'claimant-two', 'Claimant Two', 'staff', 1, '2', 0, NULL, 0, 0, 0),
                    ('claim-staff-3', 'claimant-three@example.org', 'claimant-three', 'Claimant Three', 'staff', 1, '2', 0, NULL, 0, 0, 0);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES
                    ('claim-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('claim-dvd', 'system', NULL, 'dvd', 'DVD', 1, 20);
                CREATE TABLE [format_claim_rules]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [format] TEXT NOT NULL,
                    [staffUserId] TEXT, [active] INTEGER NOT NULL, [created] TEXT, [updated] TEXT
                );
                INSERT INTO [format_claim_rules] VALUES
                    ('claim-rule-2', '2', 'book', 'claim-staff-2', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('claim-rule-3', '2', 'dvd', 'claim-staff-3', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [claimedByStaffUserId] TEXT, [claimedByDisplayName] TEXT,
                    [claimedAt] TEXT, [claimType] TEXT, [claimRuleId] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('claim-request-2', '2', 'claim-book', 'A20000000000002', 'Claim Two', 0, 'suggestion',
                     'claim-staff-2', 'Claimant Two', '2030-01-02T10:11:12Z', 'automatic_format_rule', 'claim-rule-2',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('claim-request-3', '2', 'claim-dvd', 'A20000000000003', 'Claim Three', 0, 'suggestion',
                     'claim-staff-3', 'Claimant Three', '2030-01-03T10:11:12Z', 'automatic_format_rule', 'claim-rule-3',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "claim report validation preserves this key ring");
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
            Assert.IsTrue(File.Exists(pendingPath), "Committed claim import must leave its report pending.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must remain blocked until recovery.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            var targetStaffIds = await AssertTargetClaimsAsync();
            AssertTitleRequestClaimBaseline(validPending, targetStaffIds);
            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Directory.Delete(reportPath);

            async Task AssertExternalStateUnchangedAsync(string propertyName)
            {
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected report validation must not change SQL for {propertyName}.");
                var currentStaffIds = await AssertTargetClaimsAsync();
                CollectionAssert.AreEquivalent(targetStaffIds.Keys.ToArray(), currentStaffIds.Keys.ToArray());
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected report validation must not change the key ring for {propertyName}.");
            }

            var otherStaffTargetId = targetStaffIds["claim-staff-3"];
            var otherRuleSourceId = "claim-rule-3";
            var properties = new[]
            {
                "sourceId", "sourceClaimantId", "mappedStaffUserId", "effectiveStaffUserId",
                "sourceDisplayName", "sourceClaimedAtUtc", "sourceClaimType", "sourceClaimRuleId",
                "reason", "migrationAnnotationInserted"
            };
            foreach (var propertyName in properties)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    true,
                    propertyName,
                    validPending,
                    ApplyTitleRequestClaimMutation(validPending, propertyName, otherStaffTargetId, otherRuleSourceId),
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
            AssertTitleRequestClaimBaseline(validRecoveredReport, targetStaffIds);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            CollectionAssert.AreEquivalent(targetStaffIds.Keys.ToArray(), (await AssertTargetClaimsAsync()).Keys.ToArray());
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            foreach (var propertyName in properties)
            {
                await AssertRejectedReportTruthMutationAsync(
                    reportPath,
                    false,
                    propertyName,
                    validRecoveredReport,
                    ApplyTitleRequestClaimMutation(validRecoveredReport, propertyName, otherStaffTargetId, otherRuleSourceId),
                    error => RunReconcile(package, reportPath, connectionEnvironmentName, error),
                    AssertExternalStateUnchangedAsync);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            await AssertExternalStateUnchangedAsync("restored title-request claim report");
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
                throw new InvalidOperationException("Title-request claim report cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertTitleRequestClaimBaseline(
        string reportJson,
        IReadOnlyDictionary<string, long> staffIds)
    {
        using var report = JsonDocument.Parse(reportJson);
        var claims = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Where(item => item.GetProperty("entity").GetString() == "title_request_claim")
            .ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
        Assert.AreEqual(2, claims.Count);
        AssertTitleRequestClaim(claims["claim-request-2"], "claim-staff-2", staffIds["claim-staff-2"], "claim-rule-2", "Claimant Two", "2030-01-02T10:11:12Z");
        AssertTitleRequestClaim(claims["claim-request-3"], "claim-staff-3", staffIds["claim-staff-3"], "claim-rule-3", "Claimant Three", "2030-01-03T10:11:12Z");
    }

    private static void AssertTitleRequestClaim(
        JsonElement claim,
        string claimantSourceId,
        long targetStaffId,
        string sourceRuleId,
        string displayName,
        string claimedAtUtc)
    {
        Assert.AreEqual(claimantSourceId, claim.GetProperty("sourceClaimantId").GetString());
        Assert.AreEqual(targetStaffId, claim.GetProperty("mappedStaffUserId").GetInt64());
        Assert.AreEqual(targetStaffId, claim.GetProperty("effectiveStaffUserId").GetInt64());
        Assert.AreEqual(displayName, claim.GetProperty("sourceDisplayName").GetString());
        Assert.AreEqual(claimedAtUtc, claim.GetProperty("sourceClaimedAtUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.AreEqual("automatic_format_rule", claim.GetProperty("sourceClaimType").GetString());
        Assert.AreEqual(sourceRuleId, claim.GetProperty("sourceClaimRuleId").GetString());
        Assert.AreEqual("eligible", claim.GetProperty("reason").GetString());
        Assert.IsFalse(claim.GetProperty("migrationAnnotationInserted").GetBoolean());
    }

    private static string ApplyTitleRequestClaimMutation(
        string reportJson,
        string propertyName,
        long otherStaffTargetId,
        string otherRuleSourceId)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var claims = report["transformations"]!.AsArray()
            .Where(item => item!["entity"]!.GetValue<string>() == "title_request_claim")
            .ToArray();
        var claim = claims.Single(item => item!["sourceId"]!.GetValue<string>() == "claim-request-2")!.AsObject();
        switch (propertyName)
        {
            case "sourceId":
                var other = claims.Single(item => item!["sourceId"]!.GetValue<string>() == "claim-request-3")!.AsObject();
                claim["sourceId"] = "claim-request-3";
                other["sourceId"] = "claim-request-2";
                break;
            case "sourceClaimantId":
                claim["sourceClaimantId"] = "claim-staff-3";
                break;
            case "mappedStaffUserId":
                claim["mappedStaffUserId"] = otherStaffTargetId;
                break;
            case "effectiveStaffUserId":
                claim["effectiveStaffUserId"] = otherStaffTargetId;
                break;
            case "sourceDisplayName":
                claim["sourceDisplayName"] = "Different Valid Claimant";
                break;
            case "sourceClaimedAtUtc":
                claim["sourceClaimedAtUtc"] = "2030-02-01T10:11:12Z";
                break;
            case "sourceClaimType":
                claim["sourceClaimType"] = "manual";
                break;
            case "sourceClaimRuleId":
                claim["sourceClaimRuleId"] = otherRuleSourceId;
                break;
            case "reason":
                claim["reason"] = "claim_rule_unmapped_normalized";
                break;
            case "migrationAnnotationInserted":
                claim["migrationAnnotationInserted"] = true;
                break;
            default:
                Assert.Fail($"Unexpected title-request claim mutation: {propertyName}");
                break;
        }
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
