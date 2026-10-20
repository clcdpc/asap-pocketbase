using System.Text.Json.Nodes;
using Asap.Migration;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task PublicationCalendarDatesSurviveImportAndIndependentReportRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-publication-date-truth-{Guid.NewGuid():N}");
        var databaseName = $"AsapPublicationDateTruth_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            // Offset forms exercise the existing accepted input grammar on every
            // host timezone. PocketBase normally stores its date field at UTC midnight.
            var package = CreateMinimalPackage(root, """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT PRIMARY KEY, [scope] TEXT, [code] TEXT, [label] TEXT,
                    [enabled] INTEGER, [sortOrder] INTEGER
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT,
                    [barcode] TEXT, [title] TEXT, [exactPublicationDate] TEXT,
                    [autohold] INTEGER, [status] TEXT, [created] TEXT, [updated] TEXT
                );
                INSERT INTO [title_requests] VALUES
                    ('date-only', '2', 'fmt-book', '20000000000101', 'Calendar date',
                     '2030-05-06', 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z'),
                    ('date-utc', '2', 'fmt-book', '20000000000102', 'UTC midnight',
                     '2030-05-06T00:00:00.000Z', 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z'),
                    ('date-pocketbase', '2', 'fmt-book', '20000000000103', 'PocketBase timestamp',
                     '2030-05-06 00:00:00.000Z', 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z'),
                    ('date-positive-offset', '2', 'fmt-book', '20000000000104', 'Positive offset',
                     '2030-05-06T00:00:00+14:00', 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z'),
                    ('date-negative-offset', '2', 'fmt-book', '20000000000105', 'Negative offset',
                     '2030-05-06T23:30:00-12:00', 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z'),
                    ('date-absent', '2', 'fmt-book', '20000000000106', 'No date',
                     NULL, 0, 'suggestion', '2030-01-01T00:00:00Z', '2030-01-01T00:00:00Z');
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var report = Path.Combine(root, "report.json");
            using var importError = new StringWriter();
            Assert.AreEqual(0, RunImport(package, report, environmentName,
                Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(5, await ScalarAsync(connection, """
                SELECT COUNT(*) FROM [asap].[TitleRequest]
                WHERE [ExactPublicationDate] = CONVERT(date, '2030-05-06', 23);
                """), "The source calendar day must survive independently of the importing host's timezone.");
            Assert.AreEqual(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [ExactPublicationDate] IS NULL;"));
            using var reconcileError = new StringWriter();
            Assert.AreEqual(0, RunReconcile(package, report, environmentName, reconcileError), reconcileError.ToString());
            var authenticReport = await File.ReadAllBytesAsync(report);

            await using (var drift = connection.CreateCommand())
            {
                drift.CommandText = """
                    UPDATE request SET [ExactPublicationDate] = '2030-05-05'
                    FROM [asap].[TitleRequest] request
                    JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[NewId] = request.[Id]
                    WHERE mapping.[EntityType] = N'title_request' AND mapping.[PocketBaseId] = N'date-utc';
                    """;
                Assert.AreEqual(1, await drift.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var driftFingerprint = ComputeTargetFingerprintForTest(target);
            using var rejectedReconcile = new StringWriter();
            Assert.AreEqual(1, RunReconcile(package, report, environmentName, rejectedReconcile),
                "A refreshed fingerprint cannot authorize a shifted source calendar day.");

            var pending = JsonNode.Parse(await File.ReadAllTextAsync(report))!.AsObject();
            pending["reportState"] = "commit_pending";
            pending["reconciliationPassed"] = false;
            await File.WriteAllTextAsync(report + ".pending", pending.ToJsonString());
            File.Delete(report);
            using var rejectedRecovery = new StringWriter();
            Assert.AreEqual(1, RunRecoverReport(package, report, environmentName, rejectedRecovery));
            Assert.IsFalse(File.Exists(report));
            Assert.AreEqual(driftFingerprint, ComputeTargetFingerprintForTest(target),
                "Rejected verification must not repair the target.");

            await using (var restore = connection.CreateCommand())
            {
                restore.CommandText = """
                    UPDATE request SET [ExactPublicationDate] = '2030-05-06'
                    FROM [asap].[TitleRequest] request
                    JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[NewId] = request.[Id]
                    WHERE mapping.[EntityType] = N'title_request' AND mapping.[PocketBaseId] = N'date-utc';
                    """;
                Assert.AreEqual(1, await restore.ExecuteNonQueryAsync());
            }
            await File.WriteAllBytesAsync(report + ".pending", authenticReport);
            var restoredFingerprint = ComputeTargetFingerprintForTest(target);
            using var recoveryError = new StringWriter();
            Assert.AreEqual(0, RunRecoverReport(package, report, environmentName, recoveryError), recoveryError.ToString());
            Assert.AreEqual(0, RunRecoverReport(package, report, environmentName, recoveryError), recoveryError.ToString());
            Assert.AreEqual(restoredFingerprint, ComputeTargetFingerprintForTest(target));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }
}
