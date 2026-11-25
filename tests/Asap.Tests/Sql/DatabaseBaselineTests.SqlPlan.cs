using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    public async Task ProtectedPublishRetiresPopulatedArtworkSettingsAndPreservesHistory()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        const string retiredCount = """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'asap.LibraryCatalogSettings')
               OR (object_id = OBJECT_ID(N'asap.SystemSettings') AND name IN (N'PacBaseUrl', N'MaterialTypeIconUrlPattern'));
            """;
        Assert.AreEqual(0, await Scalar(connection, retiredCount));
        var message = await Scalar(connection, "SELECT SystemNotEnabledMessage FROM asap.SystemSettings WHERE OrganizationId = 1;");
        Assert.IsFalse(string.IsNullOrWhiteSpace(message as string));
        try
        {
            await NonQuery(connection, """
                INSERT INTO asap.Organization (Id, DisplayName, IsActive) VALUES (53402, N'Artwork upgrade library', 1);
                INSERT INTO asap.AdministrativeAudit (OrganizationId, Action, CreatedUtc)
                    VALUES (53402, N'Library participation changed', SYSUTCDATETIME());
                ALTER TABLE asap.SystemSettings ADD PacBaseUrl nvarchar(2048) NULL, MaterialTypeIconUrlPattern nvarchar(2048) NULL;
                CREATE TABLE asap.LibraryCatalogSettings (
                    OrganizationId int PRIMARY KEY REFERENCES asap.Organization(Id) CHECK (OrganizationId > 1),
                    PacBaseUrl nvarchar(2048) NULL, MaterialTypeIconUrlPattern nvarchar(2048) NULL,
                    PacOrganizationId int NULL CHECK (PacOrganizationId > 0), RowVersion rowversion NOT NULL
                );
                INSERT INTO asap.LibraryCatalogSettings (OrganizationId, PacBaseUrl, MaterialTypeIconUrlPattern, PacOrganizationId)
                    VALUES (53402, N'https://library.example', N'/icons/{id2}', 27);
                """);
            await NonQuery(connection, """
                UPDATE asap.SystemSettings SET PacBaseUrl = N'https://system.example', MaterialTypeIconUrlPattern = N'/shared/{id}';
                """);

            using var package = DacPackage.Load(_dacpacPath);
            var services = new DacServices(_masterConnectionString);
            var options = new DacDeployOptions { BlockOnPossibleDataLoss = true, DropObjectsNotInSource = false, ScriptDatabaseOptions = false };
            var before = services.GenerateDeployReport(package, _databaseName, options);
            StringAssert.Contains(before, "[asap].[SystemSettings].[PacBaseUrl]");
            StringAssert.Contains(before, "[asap].[SystemSettings].[MaterialTypeIconUrlPattern]");

            var publisher = new DacpacDeploymentService();
            publisher.Deploy(_databaseConnectionString, _dacpacPath);
            var after = services.GenerateDeployReport(package, _databaseName, options);
            Assert.IsFalse(after.Contains("PacBaseUrl", StringComparison.Ordinal));
            Assert.IsFalse(after.Contains("MaterialTypeIconUrlPattern", StringComparison.Ordinal));
            publisher.Deploy(_databaseConnectionString, _dacpacPath);
            Assert.AreEqual(0, await Scalar(connection, retiredCount));
            Assert.AreEqual(message, await Scalar(connection, "SELECT SystemNotEnabledMessage FROM asap.SystemSettings WHERE OrganizationId = 1;"));
            Assert.AreEqual("Library participation changed", await Scalar(connection, """
                SELECT a.Action FROM asap.AdministrativeAudit a JOIN asap.Organization o ON a.OrganizationId = o.Id
                WHERE o.Id = 53402 AND o.DisplayName = N'Artwork upgrade library';
                """));
        }
        finally
        {
            await NonQuery(connection, """
                DROP TABLE IF EXISTS asap.LibraryCatalogSettings;
                ALTER TABLE asap.SystemSettings DROP COLUMN IF EXISTS PacBaseUrl;
                ALTER TABLE asap.SystemSettings DROP COLUMN IF EXISTS MaterialTypeIconUrlPattern;
                DELETE FROM asap.AdministrativeAudit WHERE OrganizationId = 53402;
                DELETE FROM asap.Organization WHERE Id = 53402;
                """);
        }
    }

    [TestMethod]
    public async Task CandidateSqlPlanIsReadOnlyRepeatableAndChangesForAdditionalLoss()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await NonQuery(connection, "ALTER TABLE [asap].[Organization] ADD [LegacyPlanProbe] int NULL;");
        try
        {
            await NonQuery(connection, "UPDATE [asap].[Organization] SET [LegacyPlanProbe] = 42 WHERE [Id] = 1;");
            using var package = DacPackage.Load(_dacpacPath);
            var services = new DacServices(_masterConnectionString);
            var options = new DacDeployOptions
            {
                BlockOnPossibleDataLoss = false,
                CreateNewDatabase = false,
                DropObjectsNotInSource = false,
                ScriptDatabaseOptions = false
            };
            var script = services.GenerateDeployScript(package, _databaseName, options);
            var report = services.GenerateDeployReport(package, _databaseName, options);
            StringAssert.Contains(script, "[LegacyPlanProbe]");
            StringAssert.Contains(script, "DROP COLUMN");
            StringAssert.Contains(report, "LegacyPlanProbe");
            StringAssert.Contains(script, "An existing application database has no schema identity.");
            Assert.AreEqual(script, services.GenerateDeployScript(package, _databaseName, options),
                "Exact-byte approval requires repeatable script generation for unchanged source/target.");
            Assert.AreEqual(report, services.GenerateDeployReport(package, _databaseName, options),
                "Exact-byte approval requires repeatable report generation for unchanged source/target.");
            Assert.AreEqual(42, Convert.ToInt32(await Scalar(connection,
                "SELECT [LegacyPlanProbe] FROM [asap].[Organization] WHERE [Id] = 1;")),
                "Candidate planning must not drop the column or its current data.");

            Assert.Throws<DacServicesException>(() =>
                new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath));
            Assert.AreEqual(42, Convert.ToInt32(await Scalar(connection,
                "SELECT [LegacyPlanProbe] FROM [asap].[Organization] WHERE [Id] = 1;")),
                "The protected default must still reject removal from a populated table.");

            await NonQuery(connection, "ALTER TABLE [asap].[Organization] ADD [UnreviewedPlanProbe] int NULL;");
            Assert.AreNotEqual(script, services.GenerateDeployScript(package, _databaseName, options));
            Assert.AreNotEqual(report, services.GenerateDeployReport(package, _databaseName, options));
        }
        finally
        {
            await NonQuery(connection, """
                IF COL_LENGTH(N'asap.Organization', N'UnreviewedPlanProbe') IS NOT NULL
                    ALTER TABLE [asap].[Organization] DROP COLUMN [UnreviewedPlanProbe];
                ALTER TABLE [asap].[Organization] DROP COLUMN [LegacyPlanProbe];
                """);
        }
    }
}
