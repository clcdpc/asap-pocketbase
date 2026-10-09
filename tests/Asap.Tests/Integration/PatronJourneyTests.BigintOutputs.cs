using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task FormatDeleteReturnsLargeSqlBigintIdentityAsDecimalString()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var code = $"bigint_{suffix}";
        var label = $"Bigint output {suffix}";
        long previousIdentity;
        long desiredIdentity;
        await using (var identity = new SqlConnection(databaseConnectionString))
        {
            await identity.OpenAsync();
            await using var command = identity.CreateCommand();
            command.CommandText = "SELECT CONVERT(bigint, IDENT_CURRENT(N'[asap].[MaterialFormat]')), COALESCE(MAX([Id]), 0) FROM [asap].[MaterialFormat];";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            previousIdentity = reader.GetInt64(0);
            desiredIdentity = Math.Max(9_007_199_254_740_992L, reader.GetInt64(1)) + 1000;
        }

        var identityReseeded = false;
        long formatId = 0;
        try
        {
            await using (var reseed = new SqlConnection(databaseConnectionString))
            {
                await reseed.OpenAsync();
                await using var command = reseed.CreateCommand();
                command.CommandText = $"DBCC CHECKIDENT ('[asap].[MaterialFormat]', RESEED, {desiredIdentity});";
                await command.ExecuteNonQueryAsync();
                identityReseeded = true;
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO [asap].[MaterialFormat]
                        ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
                    VALUES (2, @code, @label, 997, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                    SELECT CONVERT(bigint, SCOPE_IDENTITY());
                    """;
                insert.Parameters.AddWithValue("@code", code);
                insert.Parameters.AddWithValue("@label", label);
                formatId = Convert.ToInt64(await insert.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            Assert.AreEqual(desiredIdentity + 1, formatId);
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using var settings = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode, await settings.Content.ReadAsStringAsync());
            using var settingsBody = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
            var storedFormats = settingsBody.RootElement.GetProperty("stored")
                .GetProperty("libraryOverride").GetProperty("formats");
            var format = storedFormats.EnumerateArray().Single(item => item.GetProperty("code").GetString() == code);
            var formatIdText = formatId.ToString(CultureInfo.InvariantCulture);
            Assert.AreEqual(formatIdText, format.GetProperty("id").GetString());
            var version = format.GetProperty("version").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(version));

            using var deleted = await client.DeleteAsync(
                $"/api/asap/staff/settings/formats/{formatIdText}?version={Uri.EscapeDataString(version!)}");
            Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
            using var deletedBody = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.AreEqual("format_deleted", deletedBody.RootElement.GetProperty("code").GetString());
            var returnedId = deletedBody.RootElement.GetProperty("data").GetProperty("formatId");
            Assert.AreEqual(JsonValueKind.String, returnedId.ValueKind,
                "Format-delete SQL bigint identities must be exact decimal strings at the HTTP boundary.");
            Assert.AreEqual(formatIdText, returnedId.GetString());

            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var count = verify.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [Id] = @id;";
            count.Parameters.AddWithValue("@id", formatId);
            Assert.AreEqual(0, Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            await using var cleanup = new SqlConnection(databaseConnectionString);
            await cleanup.OpenAsync();
            await using var command = cleanup.CreateCommand();
            command.CommandText = """
                DELETE FROM [asap].[AdministrativeAudit]
                WHERE [Action] = N'custom_format_deleted' AND [TargetType] = N'MaterialFormat' AND [TargetId] = @id;
                DELETE FROM [asap].[MaterialFormat]
                WHERE [Id] = @id OR ([OwnerOrganizationId] = 2 AND [Code] = @code);
                """;
            command.Parameters.AddWithValue("@id", formatId.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@code", code);
            await command.ExecuteNonQueryAsync();
            if (identityReseeded)
            {
                command.Parameters.Clear();
                command.CommandText = $"DBCC CHECKIDENT ('[asap].[MaterialFormat]', RESEED, {previousIdentity});";
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
