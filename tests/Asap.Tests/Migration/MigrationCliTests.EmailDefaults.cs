using System.Text;
using Asap.Tests.Email;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DoNotParallelize]
    public async Task FiveEmailDefaultsPreserveSourceMappingsAndIndependentSeedPopulation(bool sourceOwnsSystem, bool historicalSeed)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-five-email-{Guid.NewGuid():N}");
        var database = $"AsapMigrationFiveEmail_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var sourceSql = new StringBuilder("""
                CREATE TABLE [email_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                    [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                );
                """);
            foreach (var template in BuiltInEmailTemplateExpectations.All)
            {
                if (sourceOwnsSystem)
                {
                    sourceSql.AppendLine($"INSERT INTO [email_templates] VALUES ('system-{template.Key}', 'system', NULL, '{template.Key}', 'Source {template.Key}', 'Source subject {template.Key}', 'Source body {template.Key}', 1, NULL);");
                }
                // Both implicit seed lineage and explicit source-owned lineage
                // must preserve sparse raw values and exact source identities.
                var sourceReference = sourceOwnsSystem ? $"'system-{template.Key}'" : "NULL";
                sourceSql.AppendLine($"INSERT INTO [email_templates] VALUES ('library-{template.Key}', 'library', 'pb-org-2', '{template.Key}', NULL, NULL, 'Library body {template.Key}', 0, {sourceReference});");
            }
            var package = CreateMinimalPackage(Path.Combine(root, "source"), sourceSql.ToString());
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, database);
            if (historicalSeed)
            {
                await using var upgrade = new SqlConnection(target);
                await upgrade.OpenAsync();
                await using (var oldSeed = new SqlCommand("""
                    DELETE FROM [asap].[EmailTemplate] WHERE [TemplateKey] <> N'suggestion_submitted';
                    UPDATE [asap].[EmailTemplate] SET [BodyTemplate] = @body WHERE [TemplateKey] = N'suggestion_submitted';
                    """, upgrade))
                {
                    oldSeed.Parameters.AddWithValue("@body", BuiltInEmailTemplateExpectations.HistoricalSubmissionBody);
                    await oldSeed.ExecuteNonQueryAsync();
                }
                var originalId = await ReadLongAsync(upgrade,
                    "SELECT [Id] FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'suggestion_submitted';");
                // The import fixture helper deliberately creates a new database;
                // exercise the real in-place upgrade path for this old seed.
                new Asap.Web.Infrastructure.Development.DacpacDeploymentService().Deploy(target, FindDacpac());
                Assert.AreEqual(originalId, await ReadLongAsync(upgrade,
                    "SELECT [Id] FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'suggestion_submitted';"));
                await using var preserved = new SqlCommand(
                    "SELECT [BodyTemplate] FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'suggestion_submitted';", upgrade);
                Assert.AreEqual(BuiltInEmailTemplateExpectations.HistoricalSubmissionBody,
                    await preserved.ExecuteScalarAsync());
            }
            Environment.SetEnvironmentVariable(environmentName, target);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, report, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());
            }
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(10, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailTemplate];"));
            foreach (var template in BuiltInEmailTemplateExpectations.All)
            {
                await using var command = new SqlCommand("""
                    SELECT parent.[SubjectTemplate], parent.[BodyTemplate], child.[SubjectTemplate], child.[BodyTemplate],
                           child.[IsCustom], child.[IsHidden], child.[SortOrder], child.[SourceTemplateId], parent.[Id]
                    FROM [asap].[LegacyPocketBaseMapping] mapping
                    JOIN [asap].[EmailTemplate] child ON mapping.[EntityType] = N'email_template' AND mapping.[NewId] = child.[Id]
                    JOIN [asap].[EmailTemplate] parent ON parent.[OrganizationId] = 1 AND parent.[TemplateKey] = @key
                    WHERE mapping.[PocketBaseId] = @sourceId AND child.[TemplateKey] = @key AND child.[OrganizationId] = 2;
                    """, connection);
                command.Parameters.AddWithValue("@key", template.Key);
                command.Parameters.AddWithValue("@sourceId", "library-" + template.Key);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync(), template.Key);
                Assert.AreEqual(sourceOwnsSystem ? "Source subject " + template.Key : template.Subject, reader.GetString(0));
                var seededBody = historicalSeed && template.Key == "suggestion_submitted"
                    ? BuiltInEmailTemplateExpectations.HistoricalSubmissionBody : template.Body;
                Assert.AreEqual(sourceOwnsSystem ? "Source body " + template.Key : seededBody, reader.GetString(1));
                Assert.IsTrue(reader.IsDBNull(2));
                Assert.AreEqual("Library body " + template.Key, reader.GetString(3));
                Assert.IsFalse(reader.GetBoolean(4));
                Assert.IsTrue(reader.GetBoolean(5));
                Assert.AreEqual(0, reader.GetInt32(6));
                Assert.AreEqual(reader.GetInt64(8), reader.GetInt64(7), template.Key);
                Assert.IsFalse(await reader.ReadAsync());
            }
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, environmentName, reconcile), reconcile.ToString());
            }
            foreach (var template in BuiltInEmailTemplateExpectations.All)
            {
                var expectedBody = sourceOwnsSystem ? "Source body " + template.Key :
                    historicalSeed && template.Key == "suggestion_submitted"
                        ? BuiltInEmailTemplateExpectations.HistoricalSubmissionBody : template.Body;
                var quotedBody = expectedBody.Replace("'", "''", StringComparison.Ordinal);
                var driftBody = historicalSeed && template.Key == "suggestion_submitted"
                    ? expectedBody + "\nAltered." : "Drifted default";
                var quotedDrift = driftBody.Replace("'", "''", StringComparison.Ordinal);
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection, target, package, report, environmentName, "email default " + template.Key,
                    $"UPDATE [asap].[EmailTemplate] SET [BodyTemplate] = N'{quotedDrift}' WHERE [OrganizationId] = 1 AND [TemplateKey] = N'{template.Key}';",
                    $"SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'{template.Key}' AND [BodyTemplate] = N'{quotedDrift}';", 1,
                    $"UPDATE [asap].[EmailTemplate] SET [BodyTemplate] = N'{quotedBody}' WHERE [OrganizationId] = 1 AND [TemplateKey] = N'{template.Key}';",
                    $"SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'{template.Key}' AND [BodyTemplate] = N'{quotedBody}';", 1);
            }
            await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                connection, target, package, report, environmentName, "unaccounted email template",
                "INSERT INTO [asap].[EmailTemplate] ([OrganizationId], [TemplateKey], [SubjectTemplate], [BodyTemplate], [IsHidden], [IsCustom], [SortOrder]) VALUES (1, N'arbitrary_extra', N'Extra', N'Extra', 0, 0, 90);",
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'arbitrary_extra';", 1,
                "DELETE FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'arbitrary_extra';",
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [TemplateKey] = N'arbitrary_extra';", 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, database);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FourNewEmailSeedsRejectCaseOnlySourceIdentityCollisionsWithoutWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-email-seed-collision-{Guid.NewGuid():N}");
        var database = $"AsapMigrationEmailSeedCollision_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, database);
            Environment.SetEnvironmentVariable(environmentName, target);
            var before = ComputeTargetFingerprintForTest(target);
            foreach (var key in new[] { "purchase_approved", "already_owned", "hold_placed", "rejected" })
            {
                foreach (var scope in new[] { "system", "library" })
                {
                    var sourceLibrary = scope == "library" ? "'pb-org-2'" : "NULL";
                    var package = CreateMinimalPackage(Path.Combine(root, key + scope), $"""
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('case-collision', '{scope}', {sourceLibrary}, '{key.ToUpperInvariant()}', 'Case changed', 'Subject', 'Body', 1, NULL);
                        """);
                    var report = Path.Combine(root, key + scope + ".json");
                    using var error = new StringWriter();
                    Assert.AreEqual(1, RunImport(package, report, environmentName,
                        Guid.Parse("00000000-0000-0000-0000-000000000002"), error), key + scope);
                    StringAssert.Contains(error.ToString(), "email_template_seed_identity_collision", key + scope);
                    Assert.AreEqual(before, ComputeTargetFingerprintForTest(target));
                    Assert.IsFalse(File.Exists(report));
                    Assert.IsFalse(File.Exists(report + ".pending"));
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, database);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
