using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    public async Task NativeSchemaUpgradeMaterializesLegacyProtectionAndLaterJsonCannotChangeAuthority(int sourceVersion)
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        try
        {
            await NonQuery(connection, """
                INSERT INTO [asap].[Organization] ([Id],[DisplayName],[IsActive]) VALUES (3492,N'Policy upgrade library',1);
                DECLARE @format bigint = (SELECT TOP(1) [Id] FROM [asap].[MaterialFormat] ORDER BY [Id]);
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId],[Barcode],[Title],[MaterialFormatId],[Status],[CloseReason],[AutoHold],[CreatedUtc],[UpdatedUtc])
                VALUES (3492,N'34900000000001',N'349 protected',@format,N'closed',N'manual',0,SYSUTCDATETIME(),SYSUTCDATETIME()),
                       (3492,N'34900000000002',N'349 unprotected',@format,N'closed',N'manual',0,SYSUTCDATETIME(),SYSUTCDATETIME());
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId],[EventType],[ActorType],[MetadataJson],[CreatedUtc])
                SELECT [Id],N'legacy',N'system',
                    CASE WHEN [Title] = N'349 protected' THEN N'{"legacyBibProtection":true}' ELSE N'{"legacyBibProtection":"true"}' END,
                    SYSUTCDATETIME()
                FROM [asap].[TitleRequest] WHERE [Barcode] IN (N'34900000000001',N'34900000000002');
                ALTER TABLE [asap].[TitleRequest] DROP CONSTRAINT [DF_TitleRequest_LegacyHoldProtected];
                ALTER TABLE [asap].[TitleRequest] DROP COLUMN [LegacyHoldProtected];
                """);
            await NonQuery(connection, $"UPDATE [asap].[SchemaVersion] SET [Version] = {sourceVersion} WHERE [Id] = 1;");
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            Assert.AreEqual(12, Convert.ToInt32(await Scalar(connection,
                "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
            Assert.AreEqual(true, Convert.ToBoolean(await Scalar(connection,
                "SELECT [LegacyHoldProtected] FROM [asap].[TitleRequest] WHERE [Barcode] = N'34900000000001';")));
            // A JSON string that reads "true" is not the typed historical Boolean marker.
            Assert.AreEqual(false, Convert.ToBoolean(await Scalar(connection,
                "SELECT [LegacyHoldProtected] FROM [asap].[TitleRequest] WHERE [Barcode] = N'34900000000002';")));
            await NonQuery(connection, """
                UPDATE history SET [MetadataJson] = CASE WHEN request.[Title] = N'349 protected'
                    THEN N'{"legacyBibProtection":false}' ELSE N'{"legacyBibProtection":true}' END
                FROM [asap].[TitleRequestEvent] history
                JOIN [asap].[TitleRequest] request ON request.[Id] = history.[TitleRequestId]
                WHERE request.[Barcode] IN (N'34900000000001',N'34900000000002');
                """);
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            Assert.AreEqual(true, Convert.ToBoolean(await Scalar(connection,
                "SELECT [LegacyHoldProtected] FROM [asap].[TitleRequest] WHERE [Barcode] = N'34900000000001';")));
            // Later Boolean history edits cannot grant or revoke materialized authority.
            Assert.AreEqual(false, Convert.ToBoolean(await Scalar(connection,
                "SELECT [LegacyHoldProtected] FROM [asap].[TitleRequest] WHERE [Barcode] = N'34900000000002';")));
        }
        finally
        {
            // Restore the package-owned schema if a failing assertion interrupted the upgrade.
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            await NonQuery(connection, "DELETE FROM [asap].[TitleRequest] WHERE [Barcode] IN (N'34900000000001',N'34900000000002'); DELETE FROM [asap].[Organization] WHERE [Id] = 3492;");
        }
    }
}
