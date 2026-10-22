using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task ImportedSeedMessagesPresentAsPlainTextWithoutChangingSourceOrCustomizedValues()
    {
        var library = await ReserveSettingsCourseLibraryIdAsync();
        var old = new[]
        {
            "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
            "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>",
            "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
            "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"
        };
        var expected = new[]
        {
            "You have successfully submitted your material suggestion! Check your email inbox for status updates.\n\nThank you for using our suggestion service.",
            "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.\n\nThank you for using this library's suggestion service.",
            "This is an eBook suggestion, please use Libby to notify us of your interest.\n\nLearn how to suggest a purchase using Libby here:\nhttps://help.libbyapp.com/en-us/6260.htm",
            "This is an eAudiobook suggestion, please use Libby to notify us of your interest.\n\nLearn how to suggest a purchase using Libby here:\nhttps://help.libbyapp.com/en-us/6260.htm"
        };
        object originalSystemMessage;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT [Message] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=1 AND [Code]=N'ebook';", connection);
            originalSystemMessage = (await command.ExecuteScalarAsync())!;
        }
        await ExecuteNonQueryAsync("""
            INSERT INTO [asap].[Organization] ([Id],[DisplayName],[OrganizationCodeId],[ParentOrganizationId],[IsActive])
            VALUES (@org,N'Imported plain message evidence',2,1,1);
            INSERT INTO [asap].[PatronSettings] ([OrganizationId],[SuccessMessage],[AlreadySubmittedMessage],[EbookMessage],[EaudiobookMessage],[UpdatedUtc])
            VALUES (@org,@success,@duplicate,@ebook,@audio,SYSUTCDATETIME());
            INSERT INTO [asap].[MaterialFormat] ([OwnerOrganizationId],[Code],[Label],[SortOrder],[IsEnabled],[MessageBehavior],[Message],[CreatedUtc],[UpdatedUtc])
            VALUES (@org,N'legacy_message',N'Imported message format',1000,1,N'message',@ebook,SYSUTCDATETIME(),SYSUTCDATETIME());
            INSERT INTO [asap].[MaterialFormatOverride] ([LibraryOrganizationId],[MaterialFormatId],[Message])
            SELECT @org,[Id],@audio FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=1 AND [Code]=N'ebook';
            """, ("@org", library), ("@success", old[0]), ("@duplicate", old[1]), ("@ebook", old[2]), ("@audio", old[3]));
        try
        {
            await ExecuteNonQueryAsync("UPDATE [asap].[MaterialFormat] SET [Message]=@value WHERE [OwnerOrganizationId]=1 AND [Code]=N'ebook';", ("@value", old[2]));
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            var keys = new[] { "successMessage", "alreadySubmittedMessage", "ebookMessage", "eaudiobookMessage" };
            using var response = await client.GetAsync($"/api/asap/config?libraryOrgId={library}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var configuration = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            for (var index = 0; index < keys.Length; index++)
            {
                Assert.AreEqual(expected[index], configuration.RootElement.GetProperty(keys[index]).GetString());
            }
            using var settingsResponse = await client.GetAsync($"/api/asap/staff/settings?orgId={library}");
            Assert.AreEqual(HttpStatusCode.OK, settingsResponse.StatusCode);
            using var settings = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
            var stored = settings.RootElement.GetProperty("stored");
            for (var index = 0; index < keys.Length; index++)
            {
                Assert.AreEqual(expected[index], stored.GetProperty("patron").GetProperty(keys[index]).GetString());
            }
            var formats = stored.GetProperty("formats").EnumerateArray().ToArray();
            Assert.AreEqual(expected[2], formats.Single(item => item.GetProperty("code").GetString() == "legacy_message").GetProperty("message").GetString());
            Assert.AreEqual(expected[3], formats.Single(item => item.GetProperty("code").GetString() == "ebook").GetProperty("message").GetString());
            var systemFormats = stored.GetProperty("configuredSystem").GetProperty("formats").EnumerateArray().ToArray();
            Assert.AreEqual(expected[2], systemFormats.Single(item => item.GetProperty("code").GetString() == "ebook").GetProperty("message").GetString(), "The System format editor must receive plain text too.");
            var libraryFormats = stored.GetProperty("libraryOverride").GetProperty("formats").EnumerateArray().ToArray();
            Assert.AreEqual(expected[2], libraryFormats.Single(item => item.GetProperty("kind").GetString() == "custom").GetProperty("message").GetString());
            Assert.AreEqual(expected[3], libraryFormats.Single(item => item.GetProperty("kind").GetString() == "systemOverride").GetProperty("message").GetString());
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("SELECT [SuccessMessage],[AlreadySubmittedMessage],[EbookMessage],[EaudiobookMessage] FROM [asap].[PatronSettings] WHERE [OrganizationId]=@org;", connection);
            command.Parameters.AddWithValue("@org", library);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync());
                for (var index = 0; index < old.Length; index++)
                {
                    Assert.AreEqual(old[index], reader.GetString(index), "Reads preserve exact imported source text.");
                }
            }
            foreach (var suffix in new[] { " ", "\0", "\nCustomized" })
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[PatronSettings] SET [SuccessMessage]=@value WHERE [OrganizationId]=@org;", ("@value", old[0] + suffix), ("@org", library));
                using var customResponse = await client.GetAsync($"/api/asap/config?libraryOrgId={library}");
                using var custom = JsonDocument.Parse(await customResponse.Content.ReadAsStringAsync());
                Assert.AreEqual(old[0] + suffix, custom.RootElement.GetProperty("successMessage").GetString());
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("UPDATE [asap].[MaterialFormat] SET [Message]=@value WHERE [OwnerOrganizationId]=1 AND [Code]=N'ebook';", ("@value", originalSystemMessage));
            await ExecuteNonQueryAsync("DELETE FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId]=@org;", ("@org", library));
            await CleanupScalarSettingsTestDataAsync(library, "", "");
        }
    }
}
