using Microsoft.Data.SqlClient;

namespace Asap.Tests.Sql;

public sealed partial class EmailDefaultsDeploymentTests
{
    [TestMethod]
    public async Task RequestEmailEvidenceDacpacUpgradesExistingRowsWithoutResetAndRetainsDeletionFences()
    {
        await using var connection = new SqlConnection(target);
        await connection.OpenAsync();
        // Model the supported schema-13 shape immediately before request mail
        // metadata, preserving actual old rows and rowversions through deployment.
        await ExecuteAsync(connection, """
            DROP INDEX [IX_EmailOutbox_TitleRequest] ON [asap].[EmailOutbox];
            ALTER TABLE [asap].[EmailOutbox] DROP CONSTRAINT [FK_EmailOutbox_TitleRequest], [CK_EmailOutbox_RequestMessage];
            ALTER TABLE [asap].[EmailOutbox] DROP COLUMN [TitleRequestId], [MessageKind];
            DROP INDEX [UX_EmailDeliveryEvent_LocalEvent] ON [asap].[EmailDeliveryEvent];
            ALTER TABLE [asap].[EmailDeliveryEvent] DROP COLUMN [LocalEventKey], [ReasonCode], [DeliveryMode], [TimestampKind];
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (2, N'Activity deployment library', 2, 1, 1);
            INSERT INTO [asap].[TitleRequest] ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
            SELECT 2, N'activity-deployment', N'Existing request', 0, [Id], N'closed', N'rejected', '2026-01-01T00:00:00.1234567', '2026-01-01T00:00:00.1234567'
            FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book';
            INSERT INTO [asap].[EmailOutbox] ([OrganizationId], [BusinessKey], [DeliveryClass], [ToAddress], [FromAddress], [Subject], [BodyText],
                [Status], [CreatedUtc], [SentUtc], [ProviderMessageId])
            SELECT 2, CONCAT(N'patron-submission:', [Id]), N'business_event', N'reader@example.org', N'library@example.org', N'Old subject', N'Old body',
                N'sent', '2026-01-01T00:00:00.1234567', '2026-01-01T00:01:00.7654321', N'real-provider-message'
            FROM [asap].[TitleRequest] WHERE [Barcode] = N'activity-deployment';
            INSERT INTO [asap].[EmailDeliveryEvent] ([EmailOutboxId], [ProviderMessageId], [ProviderEventId], [EventType], [ReceivedUtc], [MetadataJson])
            SELECT [Id], N'real-provider-message', N'real-provider-event', N'delivered', '2026-01-01T00:02:00.1234567', N'{"private":"provider payload"}'
            FROM [asap].[EmailOutbox];
            """);
        byte[] originalVersion;
        await using (var version = new SqlCommand("SELECT [RowVersion] FROM [asap].[EmailOutbox];", connection))
        {
            originalVersion = (byte[])(await version.ExecuteScalarAsync())!;
        }
        for (var index = 0; index < 2; index++)
        {
            Deploy();
            await using var verify = new SqlCommand("""
                SELECT [TitleRequestId], [MessageKind], [RowVersion], [Subject], [SentUtc],
                    (SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent]),
                    (SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent] WHERE [LocalEventKey] IS NULL AND [ProviderEventId] = N'real-provider-event')
                FROM [asap].[EmailOutbox];
                """, connection);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.IsTrue(reader.IsDBNull(0));
            Assert.IsTrue(reader.IsDBNull(1));
            CollectionAssert.AreEqual(originalVersion, (byte[])reader[2]);
            Assert.AreEqual("Old subject", reader.GetString(3));
            Assert.AreEqual(7654321L, reader.GetDateTime(4).Ticks % TimeSpan.TicksPerSecond);
            Assert.AreEqual(1, reader.GetInt32(5), "Deployment must not fabricate historical local events.");
            Assert.AreEqual(1, reader.GetInt32(6));
        }
        await ExecuteAsync(connection, """
            UPDATE [asap].[EmailOutbox] SET [TitleRequestId] = (SELECT [Id] FROM [asap].[TitleRequest] WHERE [Barcode] = N'activity-deployment'),
                [MessageKind] = N'confirmation';
            INSERT INTO [asap].[EmailDeliveryEvent] ([EmailOutboxId], [LocalEventKey], [EventType], [ReceivedUtc])
            SELECT [Id], N'lease:one:claimed', N'claimed', SYSUTCDATETIME() FROM [asap].[EmailOutbox];
            DELETE FROM [asap].[TitleRequest] WHERE [Barcode] = N'activity-deployment';
            """);
        await using (var detached = new SqlCommand("""
            SELECT COUNT(*) FROM [asap].[EmailOutbox] AS mail
            INNER JOIN [asap].[EmailDeliveryEvent] AS evidence ON evidence.[EmailOutboxId] = mail.[Id]
            WHERE mail.[TitleRequestId] IS NULL AND mail.[MessageKind] = N'confirmation';
            """, connection))
        {
            Assert.AreEqual(2, Convert.ToInt32(await detached.ExecuteScalarAsync()), "Request deletion retains both immutable event/operation relationships.");
        }
        var deleteFailure = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, "DELETE FROM [asap].[EmailOutbox];"));
        Assert.AreEqual(547, deleteFailure.Number, "Existing durable-evidence deletion policy remains intact.");
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, """
            INSERT INTO [asap].[EmailDeliveryEvent] ([EmailOutboxId], [LocalEventKey], [EventType], [ReceivedUtc])
            SELECT [Id], N'lease:one:claimed', N'claimed', SYSUTCDATETIME() FROM [asap].[EmailOutbox];
            """));
        Assert.AreEqual(2601, duplicate.Number);
    }
}
