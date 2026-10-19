using Asap.Migration;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportAndReconcileNormalizeOnlyEmptyAdditionalCopyAttributionSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-copy-attribution-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationCopyAttribution_{Guid.NewGuid():N}";
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
        var databaseDeployed = false;

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
                INSERT INTO [material_formats] VALUES ('copy-attribution-book', 'system', NULL, 'book', 'Book', 1, 10);
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
                    ('copy-reopened', '', '2', 'Test Library', '7101', 'Reopened copy', NULL, 'book', NULL, NULL,
                     'open', NULL, 'pb-staff-1', '   ', '', '', '', '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z', NULL, NULL, NULL),
                    ('copy-no-actor', '', '2', 'Test Library', '7102', 'Closed without actor', NULL, 'book', NULL, NULL,
                     'closed', NULL, '', '', '', '', '2030-01-03T00:00:00Z', '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z', NULL, NULL, NULL),
                    ('copy-padded', '', '2', 'Test Library', '7103', 'Padded attribution', NULL, 'book', NULL, NULL,
                     'closed', NULL, 'pb-staff-1', ' Historical Creator ', 'pb-staff-1', ' Closing Staff ',
                     '2030-01-03T00:00:00Z', '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z', NULL, NULL, NULL),
                    ('copy-whitespace', '', '2', 'Test Library', '7104', 'Whitespace attribution', NULL, 'book', NULL, NULL,
                     'closed', NULL, '', '   ', '', '   ', '2030-01-03T00:00:00Z',
                     '2030-01-01T00:00:00Z', '2030-01-02T00:00:00Z', NULL, NULL, NULL);
                """);
            var reportPath = Path.Combine(root, "report.json");
            DeployDacpac(master, databaseName);
            databaseDeployed = true;
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            using (var importError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", reportPath,
                        "--external-config", ExternalConfigurationPath(package)
                    ],
                    TextWriter.Null,
                    importError);
                Assert.AreEqual(0, exitCode, importError.ToString());
            }

            var copies = new Dictionary<string, (string Status, long? CreatedByStaffUserId, string? CreatedByDisplayName,
                long? ClosedByStaffUserId, string? ClosedByDisplayName, DateTime? ClosedUtc)>(StringComparer.Ordinal);
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT mapping.[PocketBaseId], copy.[Status], copy.[CreatedByStaffUserId], copy.[CreatedByDisplayName],
                           copy.[ClosedByStaffUserId], copy.[ClosedByDisplayName], copy.[ClosedUtc]
                    FROM [asap].[LegacyPocketBaseMapping] mapping
                    JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = mapping.[NewId]
                    WHERE mapping.[EntityType] = N'additional_copy';
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    copies.Add(
                        reader.GetString(0),
                        (reader.GetString(1),
                         reader.IsDBNull(2) ? null : reader.GetInt64(2),
                         reader.IsDBNull(3) ? null : reader.GetString(3),
                         reader.IsDBNull(4) ? null : reader.GetInt64(4),
                         reader.IsDBNull(5) ? null : reader.GetString(5),
                         reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
                }
            }

            Assert.AreEqual(4, copies.Count);
            var reopened = copies["copy-reopened"];
            Assert.AreEqual("open", reopened.Status);
            Assert.AreEqual("   ", reopened.CreatedByDisplayName);
            Assert.IsNull(reopened.ClosedByStaffUserId);
            Assert.IsNull(reopened.ClosedByDisplayName);
            Assert.IsNull(reopened.ClosedUtc);

            var noActor = copies["copy-no-actor"];
            Assert.AreEqual("closed", noActor.Status);
            Assert.IsNull(noActor.CreatedByStaffUserId);
            Assert.IsNull(noActor.CreatedByDisplayName);
            Assert.IsNull(noActor.ClosedByStaffUserId);
            Assert.IsNull(noActor.ClosedByDisplayName);
            Assert.AreEqual(new DateTime(2030, 1, 3), noActor.ClosedUtc);

            var padded = copies["copy-padded"];
            Assert.AreEqual(" Historical Creator ", padded.CreatedByDisplayName);
            Assert.AreEqual(" Closing Staff ", padded.ClosedByDisplayName);
            Assert.IsTrue(padded.CreatedByStaffUserId.HasValue);
            Assert.IsTrue(padded.ClosedByStaffUserId.HasValue);

            var whitespace = copies["copy-whitespace"];
            Assert.AreEqual("   ", whitespace.CreatedByDisplayName);
            Assert.AreEqual("   ", whitespace.ClosedByDisplayName);

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
                    "empty closed no-actor additional-copy closer snapshot",
                    "UPDATE copy SET [ClosedByDisplayName] = N'' FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-no-actor' AND copy.[ClosedByDisplayName] IS NULL;",
                    "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-no-actor' AND copy.[ClosedByDisplayName] = N'';",
                    1,
                    "UPDATE copy SET [ClosedByDisplayName] = NULL FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-no-actor' AND copy.[ClosedByDisplayName] = N'';",
                    "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] copy JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'additional_copy' AND mapping.[NewId] = copy.[Id] WHERE mapping.[PocketBaseId] = N'copy-no-actor' AND copy.[ClosedByDisplayName] IS NULL;",
                    1);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            if (databaseDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Additional-copy attribution cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
