using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportPreservesNonemptyWhitespaceOnlyAdditionalCopyClaimDisplay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-copy-claim-presence-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationCopyClaimPresence_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var queueNames = new[]
        {
            "pending_suggestion_isbn_checks", "outstanding_purchases", "pending_holds", "checked_out",
            "outstanding_timeout", "pending_hold_timeout", "hold_pickup_timeout", "additional_copy_timeout"
        };
        var environmentNames = new[]
            {
                "ASAP_STAFF_URL", "ASAP_PUBLIC_URL", "ASAP_BASE_URL",
                "ASAP_CRON_SCHEDULE", "ASAP_ISBN_CHECK_CRON_SCHEDULE", "ASAP_ORG_SYNC_CRON_SCHEDULE",
                "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", "ASAP_JOB_PAGE_SIZE", "ASAP_JOB_MAX_PER_RUN",
                "ASAP_TIMEOUT_PAGE_SIZE", "ASAP_TIMEOUT_MAX_PER_RUN", "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE",
                "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"
            }
            .Concat(queueNames.SelectMany(name => new[]
            {
                $"ASAP_{name.ToUpperInvariant()}_PAGE_SIZE",
                $"ASAP_{name.ToUpperInvariant()}_MAX_PER_RUN"
            }))
            .ToArray();
        var previousEnvironment = environmentNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var targetDeployed = false;
        Directory.CreateDirectory(root);

        try
        {
            foreach (var name in environmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            var package = CreateMinimalPackage(
                root,
                """
                UPDATE [staff_users] SET [displayName] = '   ' WHERE [id] = 'pb-staff-1';
                INSERT INTO [staff_users] VALUES
                    ('pb-staff-2', 'inactive-copy-claimant@example.org', 'inactive-copy-claimant',
                     'Inactive Copy Claimant', 'staff', 0, '2', 0, NULL, 0, 0, 0);
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [sourceTitleRequest] TEXT,
                    [libraryOrgId] TEXT NOT NULL,
                    [libraryOrgName] TEXT,
                    [bibid] TEXT NOT NULL,
                    [title] TEXT,
                    [author] TEXT,
                    [format] TEXT,
                    [identifier] TEXT,
                    [publication] TEXT,
                    [status] TEXT NOT NULL,
                    [notes] TEXT,
                    [createdByStaff] TEXT,
                    [createdByUsername] TEXT,
                    [closedByStaff] TEXT,
                    [closedByUsername] TEXT,
                    [closedAt] TEXT,
                    [created] TEXT,
                    [updated] TEXT,
                    [claimedByStaffUserId] TEXT,
                    [claimedByDisplayName] TEXT,
                    [claimedAt] TEXT
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-claim-whitespace', '', '2', 'Test Library', '7001', 'Whitespace claim', NULL,
                     'book', NULL, NULL, 'open', NULL, 'pb-staff-1', 'Copy creator', NULL, NULL, NULL,
                     '2030-01-01T00:00:00Z', '2030-01-02T03:00:00Z', 'pb-staff-1', '   ', '2030-01-02T02:03:04Z'),
                    ('copy-claim-null', '', '2', 'Test Library', '7002', 'Unclaimed copy', NULL,
                     'book', NULL, NULL, 'open', NULL, 'pb-staff-1', 'Copy creator', NULL, NULL, NULL,
                     '2030-01-01T00:00:00Z', '2030-01-02T03:00:00Z', NULL, NULL, NULL),
                    ('copy-claim-empty', '', '2', 'Test Library', '7003', 'Empty display copy', NULL,
                     'book', NULL, NULL, 'open', NULL, 'pb-staff-1', 'Copy creator', NULL, NULL, NULL,
                     '2030-01-01T00:00:00Z', '2030-01-02T03:00:00Z', 'pb-staff-1', '', '2030-01-02T02:03:04Z'),
                    ('copy-claim-unmapped', '', '2', 'Test Library', '7004', 'Unmapped copy', NULL,
                     'book', NULL, NULL, 'open', NULL, 'pb-staff-1', 'Copy creator', NULL, NULL, NULL,
                     '2030-01-01T00:00:00Z', '2030-01-02T03:00:00Z', 'pb-staff-missing', '   ', '2030-01-02T02:03:04Z'),
                    ('copy-claim-inactive', '', '2', 'Test Library', '7005', 'Inactive claimant copy', NULL,
                     'book', NULL, NULL, 'open', NULL, 'pb-staff-1', 'Copy creator', NULL, NULL, NULL,
                     '2030-01-01T00:00:00Z', '2030-01-02T03:00:00Z', 'pb-staff-2', '   ', '2030-01-02T02:03:04Z');
                """);
            var reportPath = Path.Combine(root, "report.json");
            var externalConfigurationPath = ExternalConfigurationPath(package);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            File.WriteAllText(Path.Combine(keyPath, "sentinel.txt"), "Migration must not modify Data Protection keys.");
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            File.WriteAllText(
                externalConfigurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

            DeployDacpac(master, databaseName);
            targetDeployed = true;
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(
                        ["validate", "--package", package, "--external-config", externalConfigurationPath],
                        TextWriter.Null,
                        validationError),
                    validationError.ToString());
            }
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            var copyStates = new Dictionary<string, (long? ClaimantId, string? DisplayName, DateTime? ClaimedAtUtc, string? Notes)>(StringComparer.Ordinal);
            long mappedClaimantId;
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using (var claimantCommand = new SqlCommand(
                                 "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] = N'pb-staff-1';",
                                 connection))
                {
                    mappedClaimantId = Convert.ToInt64(await claimantCommand.ExecuteScalarAsync());
                }

                await using (var command = new SqlCommand(
                                 "SELECT mapping.[PocketBaseId], copy.[ClaimedByStaffUserId], copy.[ClaimedByDisplayName], copy.[ClaimedAtUtc], copy.[Notes] FROM [asap].[LegacyPocketBaseMapping] mapping JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = mapping.[NewId] WHERE mapping.[EntityType] = N'additional_copy';",
                                 connection))
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        copyStates.Add(
                            reader.GetString(0),
                            (reader.IsDBNull(1) ? null : reader.GetInt64(1),
                             reader.IsDBNull(2) ? null : reader.GetString(2),
                             reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                             reader.IsDBNull(4) ? null : reader.GetString(4)));
                    }
                }
            }

            Assert.AreEqual(5, copyStates.Count);
            var accepted = copyStates["copy-claim-whitespace"];
            Assert.AreEqual(mappedClaimantId, accepted.ClaimantId);
            Assert.AreEqual("   ", accepted.DisplayName);
            Assert.IsTrue(accepted.ClaimedAtUtc.HasValue);
            Assert.AreEqual(new DateTime(2030, 1, 2, 2, 3, 4), accepted.ClaimedAtUtc.Value);
            Assert.IsNull(accepted.Notes);

            var unclaimed = copyStates["copy-claim-null"];
            Assert.IsNull(unclaimed.ClaimantId);
            Assert.IsNull(unclaimed.DisplayName);
            Assert.IsNull(unclaimed.ClaimedAtUtc);
            Assert.IsNull(unclaimed.Notes);

            var emptyDisplay = copyStates["copy-claim-empty"];
            Assert.IsNull(emptyDisplay.ClaimantId);
            Assert.IsNull(emptyDisplay.DisplayName);
            Assert.IsNull(emptyDisplay.ClaimedAtUtc);
            StringAssert.Contains(emptyDisplay.Notes!, "Cleared open claim (claim_metadata_incomplete)");

            var unmapped = copyStates["copy-claim-unmapped"];
            Assert.IsNull(unmapped.ClaimantId);
            Assert.IsNull(unmapped.DisplayName);
            Assert.IsNull(unmapped.ClaimedAtUtc);
            StringAssert.Contains(unmapped.Notes!, "Cleared open claim (claimant_unmapped)");

            var inactive = copyStates["copy-claim-inactive"];
            Assert.IsNull(inactive.ClaimantId);
            Assert.IsNull(inactive.DisplayName);
            Assert.IsNull(inactive.ClaimedAtUtc);
            StringAssert.Contains(inactive.Notes!, "Cleared open claim (claimant_inactive)");

            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var claims = report.RootElement.GetProperty("transformations").EnumerateArray()
                    .Where(item => item.GetProperty("entity").GetString() == "additional_copy_claim")
                    .ToDictionary(item => item.GetProperty("sourceId").GetString()!, StringComparer.Ordinal);
                Assert.AreEqual(4, claims.Count);

                var acceptedClaim = claims["copy-claim-whitespace"];
                Assert.AreEqual("   ", acceptedClaim.GetProperty("sourceDisplayName").GetString());
                Assert.AreEqual("eligible", acceptedClaim.GetProperty("reason").GetString());
                Assert.IsFalse(acceptedClaim.GetProperty("migrationAnnotationInserted").GetBoolean());
                Assert.AreEqual(mappedClaimantId, acceptedClaim.GetProperty("effectiveStaffUserId").GetInt64());

                Assert.AreEqual("claim_metadata_incomplete", claims["copy-claim-empty"].GetProperty("reason").GetString());
                Assert.AreEqual("claimant_unmapped", claims["copy-claim-unmapped"].GetProperty("reason").GetString());
                Assert.AreEqual("claimant_inactive", claims["copy-claim-inactive"].GetProperty("reason").GetString());
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection,
                    target,
                    package,
                    reportPath,
                    connectionEnvironmentName,
                    "whitespace-only accepted additional-copy claim display",
                    "UPDATE copy SET [ClaimedByDisplayName] = N'Drifted claim display' FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-claim-whitespace';",
                    "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-claim-whitespace' AND copy.[ClaimedByDisplayName] = N'Drifted claim display';",
                    1,
                    "UPDATE copy SET [ClaimedByDisplayName] = N'   ' FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-claim-whitespace';",
                    "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-claim-whitespace' AND copy.[ClaimedByDisplayName] = N'   ';",
                    1);
            }

            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
        }
        finally
        {
            foreach (var item in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Additional-copy claim-presence cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
