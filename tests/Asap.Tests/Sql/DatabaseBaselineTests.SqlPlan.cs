using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
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
