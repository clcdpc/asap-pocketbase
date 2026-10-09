using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    public async Task MigrationPreflightRetirementsSurviveBlockedDacFxDeploymentWithoutPublishingVersion()
    {
        var databaseName = $"AsapMigrationPreflight_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
        try
        {
            new DacpacDeploymentService().Deploy(connectionString, _dacpacPath);
            object? expectedDacpacHash;
            object? expectedDeployedUtc;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await NonQuery(connection, """
                    ALTER TABLE [asap].[PolarisSettings]
                        ADD [OrganizationIdForRequests] int NULL, [PickupOrganizationId] int NULL;
                    """);
                await NonQuery(connection, """
                    ALTER TABLE [asap].[PolarisSettings] ALTER COLUMN [AccessId] nvarchar(512) NULL;
                    """);
                await NonQuery(connection, """
                    UPDATE [asap].[PolarisSettings]
                    SET [OrganizationIdForRequests] = 8, [PickupOrganizationId] = 9,
                        [WorkstationId] = -2, [SystemPolarisUserId] = -3,
                        [AccessId] = REPLICATE(N'X', 300)
                    WHERE [OrganizationId] = 1;
                    UPDATE [asap].[SchemaVersion] SET [Version] = 7 WHERE [Id] = 1;
                    """);
                expectedDacpacHash = await Scalar(connection,
                    "SELECT [LastDacpacSha256] FROM [asap].[DeploymentState] WHERE [Id] = 1;");
                expectedDeployedUtc = await Scalar(connection,
                    "SELECT [LastDeployedUtc] FROM [asap].[DeploymentState] WHERE [Id] = 1;");
            }

            Assert.Throws<DacServicesException>(() => new DacpacDeploymentService().Deploy(connectionString, _dacpacPath));

            await using var verify = new SqlConnection(connectionString);
            await verify.OpenAsync();
            Assert.AreEqual(7, Convert.ToInt32(await Scalar(verify,
                "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
            Assert.AreEqual(0, Convert.ToInt32(await Scalar(verify,
                "SELECT COUNT(*) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[asap].[PolarisSettings]') AND [name] IN (N'OrganizationIdForRequests', N'PickupOrganizationId');")));
            Assert.AreEqual(1, Convert.ToInt32(await Scalar(verify,
                "SELECT COUNT(*) FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1 AND [WorkstationId] IS NULL AND [SystemPolarisUserId] IS NULL AND LEN([AccessId]) = 300;")));
            Assert.AreEqual(expectedDacpacHash, await Scalar(verify,
                "SELECT [LastDacpacSha256] FROM [asap].[DeploymentState] WHERE [Id] = 1;"));
            Assert.AreEqual(expectedDeployedUtc, await Scalar(verify,
                "SELECT [LastDeployedUtc] FROM [asap].[DeploymentState] WHERE [Id] = 1;"));
        }
        finally
        {
            await using var master = new SqlConnection(_masterConnectionString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = """
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    DECLARE @statement nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@name) +
                        N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';';
                    EXEC sys.sp_executesql @statement;
                END;
                """;
            drop.Parameters.AddWithValue("@name", databaseName);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
