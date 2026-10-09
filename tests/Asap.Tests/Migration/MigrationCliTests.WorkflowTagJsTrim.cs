using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportUsesPinnedWorkflowTagTrimmingAndPreservesSourceMappings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-workflow-tag-js-trim-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationWorkflowTagJsTrim_{Guid.NewGuid():N}";
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
        var feff = "\uFEFF";
        var nel = "\u0085";
        var nelAliasCode = nel + "dupe found in Polaris";
        var additionalSql = $"""
            CREATE TABLE [workflow_tags]
            (
                [id] TEXT NOT NULL PRIMARY KEY,
                [code] TEXT NOT NULL,
                [label] TEXT NOT NULL,
                [sortOrder] INTEGER
            );
            INSERT INTO [workflow_tags] ([id], [code], [label], [sortOrder]) VALUES
                ('source-tag-feff-capital', '{feff}Dupe found in Polaris', 'Identifier found', 10),
                ('source-tag-nel-lower', '{nelAliasCode}', 'NEL distinct tag', 20),
                ('source-tag-isbn-alias', 'ISBN not found in system', 'Identifier number not found in system', 30),
                ('source-tag-custom', 'Custom_Case_Tag', 'Custom_Case_Tag', 40);
            CREATE TABLE [material_formats]
            (
                [id] TEXT NOT NULL PRIMARY KEY,
                [scope] TEXT NOT NULL,
                [libraryOrganization] TEXT,
                [code] TEXT NOT NULL,
                [label] TEXT NOT NULL,
                [enabled] INTEGER NOT NULL,
                [sortOrder] INTEGER NOT NULL
            );
            INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, '0', 'Book', 1, 10);
            CREATE TABLE [title_requests]
            (
                [id] TEXT NOT NULL PRIMARY KEY,
                [libraryOrgId] TEXT NOT NULL,
                [formatRef] TEXT,
                [barcode] TEXT NOT NULL,
                [title] TEXT NOT NULL,
                [autohold] INTEGER NOT NULL,
                [status] TEXT NOT NULL,
                [created] TEXT NOT NULL,
                [updated] TEXT NOT NULL
            );
            INSERT INTO [title_requests] VALUES
                ('source-request-tags', '2', 'fmt-book', 'TAG-PROOF-001', 'Workflow tag identity proof', 0, 'suggestion',
                 '2030-01-02T03:04:05Z', '2030-01-02T03:04:05Z');
            CREATE TABLE [title_request_tags]
            (
                [id] TEXT NOT NULL PRIMARY KEY,
                [titleRequest] TEXT NOT NULL,
                [tag] TEXT NOT NULL
            );
            INSERT INTO [title_request_tags] ([id], [titleRequest], [tag]) VALUES
                ('source-link-feff', 'source-request-tags', 'source-tag-feff-capital'),
                ('source-link-nel', 'source-request-tags', 'source-tag-nel-lower'),
                ('source-link-isbn', 'source-request-tags', 'source-tag-isbn-alias'),
                ('source-link-custom', 'source-request-tags', 'source-tag-custom');
            """;
        Directory.CreateDirectory(root);

        try
        {
            var package = CreateMinimalPackage(root, additionalSql);
            using (var validateError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validateError),
                    validateError.ToString());
            }

            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            var expectedCodes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source-tag-feff-capital"] = "polaris_bib_found",
                ["source-tag-nel-lower"] = nelAliasCode,
                ["source-tag-isbn-alias"] = "polaris_bib_not_found",
                ["source-tag-custom"] = "Custom_Case_Tag"
            };
            var tagMappings = new Dictionary<string, (long Id, string Code)>(StringComparer.Ordinal);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var readTags = connection.CreateCommand())
            {
                readTags.CommandText =
                    "SELECT m.[PocketBaseId], m.[NewId], t.[Code] " +
                    "FROM [asap].[LegacyPocketBaseMapping] AS m " +
                    "JOIN [asap].[WorkflowTag] AS t ON t.[Id] = m.[NewId] " +
                    "WHERE m.[EntityType] = N'workflow_tag';";
                await using var reader = await readTags.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Assert.IsTrue(
                        tagMappings.TryAdd(reader.GetString(0), (reader.GetInt64(1), reader.GetString(2))),
                        "Each exact workflow-tag source ID must have one target mapping.");
                }
            }

            Assert.AreEqual(expectedCodes.Count, tagMappings.Count, "The target mapping population must match the four source rows.");
            foreach (var (sourceId, expectedCode) in expectedCodes)
            {
                Assert.AreEqual(expectedCode, tagMappings[sourceId].Code, $"Unexpected target code for source tag {sourceId}.");
            }

            Assert.AreNotEqual(
                tagMappings["source-tag-feff-capital"].Id,
                tagMappings["source-tag-nel-lower"].Id,
                "The FEFF-trimmed alias and NEL-prefixed source-distinct tag must not collapse to one target identity.");

            var targetCodes = new HashSet<string>(StringComparer.Ordinal);
            await using (var readTargetCodes = connection.CreateCommand())
            {
                readTargetCodes.CommandText = "SELECT [Code] FROM [asap].[WorkflowTag];";
                await using var reader = await readTargetCodes.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    targetCodes.Add(reader.GetString(0));
                }
            }

            var expectedTargetCodes = new HashSet<string>(StringComparer.Ordinal)
            {
                "duplicate_suggestion",
                "polaris_bib_found",
                "polaris_bib_not_found",
                "polaris_multiple_matches",
                nelAliasCode,
                "Custom_Case_Tag"
            };
            Assert.IsTrue(expectedTargetCodes.SetEquals(targetCodes), "The four protected target seeds and two distinct source tags must be preserved exactly.");

            var actualLinks = new HashSet<(string RequestId, string TagId, string TargetCode)>();
            await using (var readLinks = connection.CreateCommand())
            {
                readLinks.CommandText =
                    "SELECT requestMap.[PocketBaseId], tagMap.[PocketBaseId], tag.[Code] " +
                    "FROM [asap].[LegacyPocketBaseMapping] AS requestMap " +
                    "JOIN [asap].[TitleRequestWorkflowTag] AS link ON link.[TitleRequestId] = requestMap.[NewId] " +
                    "JOIN [asap].[LegacyPocketBaseMapping] AS tagMap " +
                    "  ON tagMap.[EntityType] = N'workflow_tag' AND tagMap.[NewId] = link.[WorkflowTagId] " +
                    "JOIN [asap].[WorkflowTag] AS tag ON tag.[Id] = link.[WorkflowTagId] " +
                    "WHERE requestMap.[EntityType] = N'title_request' " +
                    "  AND requestMap.[PocketBaseId] = N'source-request-tags';";
                await using var reader = await readLinks.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    actualLinks.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                }
            }

            var expectedLinks = expectedCodes
                .Select(item => ("source-request-tags", item.Key, item.Value))
                .ToHashSet();
            Assert.IsTrue(expectedLinks.SetEquals(actualLinks), "Every source tag link must resolve through its exact request and tag source-ID mappings.");

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            var originalFingerprint = ComputeTargetFingerprintForTest(target);
            var nelTagId = tagMappings["source-tag-nel-lower"].Id;
            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = "UPDATE [asap].[WorkflowTag] SET [Code] = N'drifted_nel_tag' WHERE [Id] = @id;";
                drift.Parameters.AddWithValue("@id", nelTagId);
                Assert.AreEqual(1, await drift.ExecuteNonQueryAsync());
            }

            RefreshReportFingerprint(reportPath, target);
            var refreshedDriftReport = await File.ReadAllBytesAsync(reportPath);
            var driftFingerprint = ComputeTargetFingerprintForTest(target);
            using (var driftError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, reportPath, connectionEnvironmentName, driftError));
                StringAssert.Contains(driftError.ToString(), "reconciliation_failed");
            }

            Assert.AreEqual(driftFingerprint, ComputeTargetFingerprintForTest(target), "Reconcile must not repair a refreshed-fingerprint tag-code drift.");
            CollectionAssert.AreEqual(refreshedDriftReport, await File.ReadAllBytesAsync(reportPath), "Failed reconciliation must not rewrite the refreshed report.");

            await using (var restore = connection.CreateCommand())
            {
                restore.CommandText = "UPDATE [asap].[WorkflowTag] SET [Code] = @code WHERE [Id] = @id;";
                restore.Parameters.AddWithValue("@code", nelAliasCode);
                restore.Parameters.AddWithValue("@id", nelTagId);
                Assert.AreEqual(1, await restore.ExecuteNonQueryAsync());
            }

            Assert.AreEqual(originalFingerprint, ComputeTargetFingerprintForTest(target), "Restoring the exact source-derived code must restore the original SQL fingerprint.");
            RefreshReportFingerprint(reportPath, target);
            using (var restoredReconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, restoredReconcileError), restoredReconcileError.ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            await DropDatabaseAsync(master, databaseName);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
