using Asap.Web.Infrastructure.Development;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    public async Task SchemaThirteenUpgradePreservesHistoricalEmailWithoutInventingLiveAttemptEvidence()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        long id = 0;
        try
        {
            id = Convert.ToInt64(await Scalar(connection, """
                INSERT INTO [asap].[EmailOutbox]
                    ([OrganizationId], [DeliveryClass], [ToAddress], [FromAddress], [Status],
                     [CreatedUtc], [SentUtc], [ProviderMessageId], [Subject], [BodyText])
                VALUES (1, N'operational_test', N'historical@example.org', N'sender@example.org', N'sent',
                    '2026-01-15T15:00:00.1234567', '2026-01-15T15:01:00.7654321', N'historical-provider',
                    N'Historical fixture', N'Historical body');
                SELECT CONVERT(bigint, SCOPE_IDENTITY());
                """));
            await NonQuery(connection, """
                DROP INDEX [IX_EmailOutbox_TestActorTime] ON [asap].[EmailOutbox];
                ALTER TABLE [asap].[EmailOutbox] DROP COLUMN [TestRequestedByStaffUserId], [TestConfigurationVersion], [IntendedDeliveryMode], [AttemptDeliveryMode];
                UPDATE [asap].[SchemaVersion] SET [Version] = 12 WHERE [Id] = 1;
                """);
            var version = (byte[])(await Scalar(connection, $"SELECT [RowVersion] FROM [asap].[EmailOutbox] WHERE [Id] = {id};"))!;
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            Assert.AreEqual(13, Convert.ToInt32(await Scalar(connection, "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
            Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, $"""
                SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [Id] = {id}
                    AND [Status] = N'sent' AND [ProviderMessageId] = N'historical-provider'
                    AND [Subject] = N'Historical fixture' AND [BodyText] = N'Historical body'
                    AND [CreatedUtc] = '2026-01-15T15:00:00.1234567' AND [SentUtc] = '2026-01-15T15:01:00.7654321'
                    AND [TestRequestedByStaffUserId] IS NULL AND [TestConfigurationVersion] IS NULL
                    AND [IntendedDeliveryMode] IS NULL AND [AttemptDeliveryMode] IS NULL;
                """)));
            CollectionAssert.AreEqual(version,
                (byte[])(await Scalar(connection, $"SELECT [RowVersion] FROM [asap].[EmailOutbox] WHERE [Id] = {id};"))!,
                "Schema upgrade adds nullable metadata without rewriting historical email records.");
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            CollectionAssert.AreEqual(version,
                (byte[])(await Scalar(connection, $"SELECT [RowVersion] FROM [asap].[EmailOutbox] WHERE [Id] = {id};"))!);
        }
        finally
        {
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            await NonQuery(connection, $"DELETE FROM [asap].[EmailOutbox] WHERE [Id] = {id};");
        }
    }
}
