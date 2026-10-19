using System.Security.Cryptography;
using Asap.Tests;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Development;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Sql;

public sealed partial class DatabaseBaselineTests
{
    [TestMethod]
    public async Task NativeSchemaElevenUpgradeLeavesUnknownPatronIdentitySnapshotsNull()
    {
        const int libraryId = 99881;
        const string barcode = "schema-eleven-unknown-patron";
        const string title = "Schema eleven identity preservation";
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        long sessionId = 0;
        long requestId = 0;
        long eventId = 0;
        long? freshSessionId = null;
        byte[] originalRowVersion = [];
        try
        {
            await NonQuery(connection, $"""
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES ({libraryId}, N'Schema Eleven Native Identity Library', N'S11I', 2, 1, 1);
                """);
            await NonQuery(connection, """
                DROP INDEX [IX_PatronSession_NativePatronId] ON [asap].[PatronSession];
                ALTER TABLE [asap].[PatronSession] DROP CONSTRAINT [CK_PatronSession_NativePatronId];
                ALTER TABLE [asap].[PatronSession] DROP COLUMN [NativePatronId];
                DROP INDEX [IX_TitleRequest_PatronIdentityLimit] ON [asap].[TitleRequest];
                ALTER TABLE [asap].[TitleRequest] DROP CONSTRAINT [CK_TitleRequest_PatronIdSnapshot];
                ALTER TABLE [asap].[TitleRequest] DROP COLUMN [PatronIdSnapshot];
                """);

            await using (var insertSession = new SqlCommand("""
                INSERT INTO [asap].[PatronSession]
                    ([TokenHash], [Barcode], [HomeOrganizationId], [ExperienceOrganizationId],
                     [EffectiveOrganizationId], [CreatedUtc], [ExpiresUtc])
                OUTPUT inserted.[Id]
                VALUES (@tokenHash, @barcode, @libraryId, @libraryId, @libraryId,
                    SYSUTCDATETIME(), DATEADD(day, 30, SYSUTCDATETIME()));
                """, connection))
            {
                insertSession.Parameters.Add("@tokenHash", System.Data.SqlDbType.Binary, 32).Value = tokenHash;
                insertSession.Parameters.Add("@barcode", System.Data.SqlDbType.NVarChar, 50).Value = barcode;
                insertSession.Parameters.Add("@libraryId", System.Data.SqlDbType.Int).Value = libraryId;
                sessionId = Convert.ToInt64(await insertSession.ExecuteScalarAsync());
            }

            await using (var insertRequest = new SqlCommand("""
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [AutoHold],
                     [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
                OUTPUT inserted.[Id]
                SELECT @libraryId, @libraryId, @barcode, @title, 0, format.[Id], N'closed', N'manual',
                    SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM [asap].[MaterialFormat] AS format
                WHERE format.[OwnerOrganizationId] = 1 AND format.[Code] = N'book';
                """, connection))
            {
                insertRequest.Parameters.Add("@barcode", System.Data.SqlDbType.NVarChar, 50).Value = barcode;
                insertRequest.Parameters.Add("@title", System.Data.SqlDbType.NVarChar, 500).Value = title;
                insertRequest.Parameters.Add("@libraryId", System.Data.SqlDbType.Int).Value = libraryId;
                requestId = Convert.ToInt64(await insertRequest.ExecuteScalarAsync());
            }

            await using (var insertEvent = new SqlCommand("""
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [CloseReason], [ActorType], [MetadataJson], [CreatedUtc])
                OUTPUT inserted.[Id]
                VALUES (@requestId, N'identity_snapshot_history', N'closed', N'manual', N'system',
                    N'{"preserved":true}', SYSUTCDATETIME());
                """, connection))
            {
                insertEvent.Parameters.Add("@requestId", System.Data.SqlDbType.BigInt).Value = requestId;
                eventId = Convert.ToInt64(await insertEvent.ExecuteScalarAsync());
            }

            await using (var readVersion = new SqlCommand(
                "SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = @id;", connection))
            {
                readVersion.Parameters.Add("@id", System.Data.SqlDbType.BigInt).Value = requestId;
                originalRowVersion = (byte[])(await readVersion.ExecuteScalarAsync())!;
            }

            await NonQuery(connection,
                "UPDATE [asap].[SchemaVersion] SET [Version] = 11 WHERE [Id] = 1;");
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);

            Assert.AreEqual(12, Convert.ToInt32(await Scalar(connection,
                "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
            Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, $"""
                SELECT COUNT(*) FROM [asap].[PatronSession]
                WHERE [Id] = {sessionId} AND [Barcode] = N'schema-eleven-unknown-patron' AND [NativePatronId] IS NULL;
                """)));
            Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, $"""
                SELECT COUNT(*) FROM [asap].[TitleRequest]
                WHERE [Id] = {requestId} AND [Barcode] = N'schema-eleven-unknown-patron' AND [PatronIdSnapshot] IS NULL;
                """)));
            var upgradedRowVersion = (byte[])(await Scalar(connection,
                $"SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id] = {requestId};"))!;
            Assert.IsTrue(originalRowVersion.SequenceEqual(upgradedRowVersion));
            Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection,
                $"SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [Id] = {eventId} AND [TitleRequestId] = {requestId} AND [EventType] = N'identity_snapshot_history';")));

            var configuration = TestConfigurationFactory.Create();
            configuration.ConnectionStrings.AsapDatabase = _databaseConnectionString;
            var sessions = new PatronSessionService(configuration);
            Assert.IsNull(await sessions.AuthenticateAsync(token, CancellationToken.None),
                "An upgraded unbound session token must not pass public session authentication.");
            var fresh = await sessions.IssueAsync("schema-twelve-verified-patron", 653211,
                libraryId, libraryId, libraryId, CancellationToken.None);
            Assert.IsNotNull(fresh, "A current active native library should accept a newly verified identity.");
            freshSessionId = fresh.Context.Id;
            Assert.AreEqual(653211, (await sessions.AuthenticateAsync(fresh.Token, CancellationToken.None))?.NativePatronId);
        }
        finally
        {
            if (freshSessionId is > 0)
            {
                await NonQuery(connection, $"DELETE FROM [asap].[PatronSession] WHERE [Id] = {freshSessionId.Value};");
            }
            if (requestId > 0)
            {
                await NonQuery(connection, $"DELETE FROM [asap].[TitleRequest] WHERE [Id] = {requestId};");
            }
            if (sessionId > 0)
            {
                await NonQuery(connection, $"DELETE FROM [asap].[PatronSession] WHERE [Id] = {sessionId};");
            }
            await NonQuery(connection, $"DELETE FROM [asap].[Organization] WHERE [Id] = {libraryId};");

            // Restore schema 12 even when a regression assertion fails after the
            // old shape has been published, then restore this isolated fixture's pin.
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 11 WHERE [Id] = 1;");
            new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 12 WHERE [Id] = 1;");
        }
    }
}
