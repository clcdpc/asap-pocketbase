using System.Net;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task ProfileAndMetadataHttpFormsRejectOmissionsAndInvalidNullsWithoutWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var profileTarget = await CreateCorrectiveStaffAsync(actor, "staff", 2);
        var profileActor = await ReadCorrectiveStaffAsync(profileTarget);
        using var profileClient = factory!.CreateClient();
        AddTestingStaffHeaders(profileClient, profileActor.Id, profileActor.EntraTenantId, profileActor.AuthenticationEmail);
        profileClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(profileClient));

        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var profile = await ReadProfileSnapshotAsync(profileTarget.Id);
        var profilePayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = profile.Version,
            ["weeklyActionSummaryEnabled"] = true,
            ["weeklyActionSummaryEmail"] = $"weekly.{Guid.NewGuid():N}@example.org",
            ["purchaseReminderDefault"] = true,
            ["additionalCopyReminderDefault"] = false,
            ["defaultMineUnclaimedFilter"] = true
        };
        foreach (var property in profilePayload.Keys.ToArray())
        {
            var omitted = new Dictionary<string, object?>(profilePayload, StringComparer.Ordinal);
            omitted.Remove(property);
            using var response = await PostJsonAsync(profileClient, "/api/asap/staff/profile", omitted);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Omitted profile property {property} was accepted: {await response.Content.ReadAsStringAsync()}");
            await AssertProfileUnchangedAsync(profileTarget.Id, profile);
        }

        foreach (var property in new[]
                 {
                     "version", "weeklyActionSummaryEnabled", "purchaseReminderDefault",
                     "additionalCopyReminderDefault", "defaultMineUnclaimedFilter"
                 })
        {
            var nullValue = new Dictionary<string, object?>(profilePayload, StringComparer.Ordinal)
            {
                [property] = null
            };
            using var response = await PostJsonAsync(profileClient, "/api/asap/staff/profile", nullValue);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Null profile property {property} was accepted: {await response.Content.ReadAsStringAsync()}");
            await AssertProfileUnchangedAsync(profileTarget.Id, profile);
        }

        var duplicateVersion = $$"""{"version":"{{profile.Version}}","version":"{{profile.Version}}","weeklyActionSummaryEnabled":true,"weeklyActionSummaryEmail":null,"purchaseReminderDefault":false,"additionalCopyReminderDefault":false,"defaultMineUnclaimedFilter":false}""";
        using (var duplicate = await PostRawJsonAsync(profileClient, "/api/asap/staff/profile", duplicateVersion))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode,
                $"Duplicate profile property was accepted: {await duplicate.Content.ReadAsStringAsync()}");
        }
        var duplicateAlias = $$"""{"version":"{{profile.Version}}","Version":"{{profile.Version}}","weeklyActionSummaryEnabled":true,"weeklyActionSummaryEmail":null,"purchaseReminderDefault":false,"additionalCopyReminderDefault":false,"defaultMineUnclaimedFilter":false}""";
        using (var duplicate = await PostRawJsonAsync(profileClient, "/api/asap/staff/profile", duplicateAlias))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode,
                $"Case-insensitive duplicate profile property was accepted: {await duplicate.Content.ReadAsStringAsync()}");
        }
        await AssertProfileUnchangedAsync(profileTarget.Id, profile);

        using var acceptedProfile = await PostJsonAsync(profileClient, "/api/asap/staff/profile", profilePayload);
        Assert.AreEqual(HttpStatusCode.OK, acceptedProfile.StatusCode, await acceptedProfile.Content.ReadAsStringAsync());
        using var acceptedProfileBody = JsonDocument.Parse(await acceptedProfile.Content.ReadAsStringAsync());
        var savedProfile = acceptedProfileBody.RootElement.GetProperty("staff");
        Assert.AreEqual(profilePayload["weeklyActionSummaryEmail"], savedProfile.GetProperty("weeklyActionSummaryEmail").GetString());

        var clearProfilePayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = savedProfile.GetProperty("version").GetString(),
            ["weeklyActionSummaryEnabled"] = true,
            ["weeklyActionSummaryEmail"] = null,
            ["purchaseReminderDefault"] = true,
            ["additionalCopyReminderDefault"] = false,
            ["defaultMineUnclaimedFilter"] = true
        };
        using var clearedProfile = await PostJsonAsync(profileClient, "/api/asap/staff/profile", clearProfilePayload);
        Assert.AreEqual(HttpStatusCode.OK, clearedProfile.StatusCode, await clearedProfile.Content.ReadAsStringAsync());
        using var clearedProfileBody = JsonDocument.Parse(await clearedProfile.Content.ReadAsStringAsync());
        Assert.AreEqual(JsonValueKind.Null,
            clearedProfileBody.RootElement.GetProperty("staff").GetProperty("weeklyActionSummaryEmail").ValueKind);

        var metadataTarget = profileTarget;
        var initialMetadata = await ReadMetadataSnapshotAsync(metadataTarget.Id);
        var metadataPayload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"] = initialMetadata.Version,
            ["email"] = metadataTarget.UserPrincipalName,
            ["displayName"] = "Readable staff name",
            ["notificationEmail"] = "separate-notification@example.org"
        };
        foreach (var property in metadataPayload.Keys.ToArray())
        {
            var omitted = new Dictionary<string, object?>(metadataPayload, StringComparer.Ordinal);
            omitted.Remove(property);
            using var response = await PatchJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", omitted);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Omitted metadata property {property} was accepted: {await response.Content.ReadAsStringAsync()}");
            await AssertMetadataUnchangedAsync(metadataTarget.Id, initialMetadata);
        }

        foreach (var property in new[] { "version", "email" })
        {
            var nullValue = new Dictionary<string, object?>(metadataPayload, StringComparer.Ordinal)
            {
                [property] = null
            };
            using var response = await PatchJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", nullValue);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                $"Null metadata property {property} was accepted: {await response.Content.ReadAsStringAsync()}");
            await AssertMetadataUnchangedAsync(metadataTarget.Id, initialMetadata);
        }

        var duplicateExact = $$"""{"version":"{{initialMetadata.Version}}","email":"{{metadataTarget.UserPrincipalName}}","email":"{{metadataTarget.UserPrincipalName}}","displayName":null,"notificationEmail":null}""";
        using (var duplicate = await PatchRawJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", duplicateExact))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode,
                $"Duplicate metadata property was accepted: {await duplicate.Content.ReadAsStringAsync()}");
        }
        var duplicateMetadataAlias = $$"""{"version":"{{initialMetadata.Version}}","email":"{{metadataTarget.UserPrincipalName}}","Email":"{{metadataTarget.UserPrincipalName}}","displayName":null,"notificationEmail":null}""";
        using (var duplicate = await PatchRawJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", duplicateMetadataAlias))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode,
                $"Case-insensitive duplicate metadata property was accepted: {await duplicate.Content.ReadAsStringAsync()}");
        }
        await AssertMetadataUnchangedAsync(metadataTarget.Id, initialMetadata);

        using var fullMetadata = await PatchJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", metadataPayload);
        Assert.AreEqual(HttpStatusCode.OK, fullMetadata.StatusCode, await fullMetadata.Content.ReadAsStringAsync());
        using var fullMetadataBody = JsonDocument.Parse(await fullMetadata.Content.ReadAsStringAsync());
        var fullySaved = fullMetadataBody.RootElement.GetProperty("user");
        Assert.AreEqual("Readable staff name", fullySaved.GetProperty("displayName").GetString());
        Assert.AreEqual("separate-notification@example.org", fullySaved.GetProperty("notificationEmail").GetString());

        metadataPayload["version"] = fullySaved.GetProperty("version").GetString();
        metadataPayload["displayName"] = null;
        using var clearDisplayName = await PatchJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", metadataPayload);
        Assert.AreEqual(HttpStatusCode.OK, clearDisplayName.StatusCode, await clearDisplayName.Content.ReadAsStringAsync());
        using var clearDisplayBody = JsonDocument.Parse(await clearDisplayName.Content.ReadAsStringAsync());
        var displayCleared = clearDisplayBody.RootElement.GetProperty("user");
        Assert.AreEqual(JsonValueKind.Null, displayCleared.GetProperty("displayName").ValueKind);
        Assert.AreEqual("separate-notification@example.org", displayCleared.GetProperty("notificationEmail").GetString());

        metadataPayload["version"] = displayCleared.GetProperty("version").GetString();
        metadataPayload["displayName"] = "Readable staff name";
        metadataPayload["notificationEmail"] = null;
        using var clearNotification = await PatchJsonAsync(client, $"/api/asap/staff/users/{metadataTarget.Id}", metadataPayload);
        Assert.AreEqual(HttpStatusCode.OK, clearNotification.StatusCode, await clearNotification.Content.ReadAsStringAsync());
        using var clearNotificationBody = JsonDocument.Parse(await clearNotification.Content.ReadAsStringAsync());
        var notificationCleared = clearNotificationBody.RootElement.GetProperty("user");
        Assert.AreEqual("Readable staff name", notificationCleared.GetProperty("displayName").GetString());
        Assert.AreEqual(JsonValueKind.Null, notificationCleared.GetProperty("notificationEmail").ValueKind);

        await ExecuteNonQueryAsync(
            "DELETE FROM [asap].[AdministrativeAudit] WHERE [TargetType] = N'StaffUser' AND [TargetId] = CONVERT(nvarchar(40), @id); " +
            "DELETE FROM [asap].[StaffUser] WHERE [Id] = @id;",
            ("@id", profileTarget.Id));
    }

    private sealed record ProfileSnapshot(string Version, byte[] RowVersion, bool WeeklyEnabled,
        string? WeeklyEmail, bool PurchaseDefault, bool AdditionalCopyDefault, bool MineDefault);

    private sealed record MetadataSnapshot(string Version, byte[] RowVersion, string Email,
        string? DisplayName, string? NotificationEmail, int AuditCount);

    private static async Task<ProfileSnapshot> ReadProfileSnapshotAsync(long staffId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [RowVersion], [WeeklyActionSummaryEnabled], [WeeklyActionSummaryEmail], [PurchaseReminderDefault], [AdditionalCopyReminderDefault], [DefaultMineUnclaimedFilter] FROM [asap].[StaffUser] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", staffId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        var rowVersion = reader.GetFieldValue<byte[]>(0);
        return new ProfileSnapshot(Convert.ToBase64String(rowVersion), rowVersion, reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
    }

    private static async Task AssertProfileUnchangedAsync(long staffId, ProfileSnapshot expected)
    {
        var actual = await ReadProfileSnapshotAsync(staffId);
        CollectionAssert.AreEqual(expected.RowVersion, actual.RowVersion);
        Assert.AreEqual(expected.WeeklyEnabled, actual.WeeklyEnabled);
        Assert.AreEqual(expected.WeeklyEmail, actual.WeeklyEmail);
        Assert.AreEqual(expected.PurchaseDefault, actual.PurchaseDefault);
        Assert.AreEqual(expected.AdditionalCopyDefault, actual.AdditionalCopyDefault);
        Assert.AreEqual(expected.MineDefault, actual.MineDefault);
    }

    private static async Task<MetadataSnapshot> ReadMetadataSnapshotAsync(long staffId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT staff.[RowVersion], staff.[UserPrincipalName], staff.[DisplayName], staff.[NotificationEmail], (SELECT COUNT(*) FROM [asap].[AdministrativeAudit] audit WHERE audit.[TargetType] = N'StaffUser' AND audit.[TargetId] = CONVERT(nvarchar(40), staff.[Id]) AND audit.[Action] = N'staff_metadata_updated') FROM [asap].[StaffUser] staff WHERE staff.[Id] = @id;";
        command.Parameters.AddWithValue("@id", staffId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        var rowVersion = reader.GetFieldValue<byte[]>(0);
        return new MetadataSnapshot(Convert.ToBase64String(rowVersion), rowVersion, reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4));
    }

    private static async Task AssertMetadataUnchangedAsync(long staffId, MetadataSnapshot expected)
    {
        var actual = await ReadMetadataSnapshotAsync(staffId);
        CollectionAssert.AreEqual(expected.RowVersion, actual.RowVersion);
        Assert.AreEqual(expected.Email, actual.Email);
        Assert.AreEqual(expected.DisplayName, actual.DisplayName);
        Assert.AreEqual(expected.NotificationEmail, actual.NotificationEmail);
        Assert.AreEqual(expected.AuditCount, actual.AuditCount);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string route, object payload) =>
        PostRawJsonAsync(client, route, JsonSerializer.Serialize(payload));

    private static Task<HttpResponseMessage> PostRawJsonAsync(HttpClient client, string route, string json) =>
        client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> PatchJsonAsync(HttpClient client, string route, object payload) =>
        PatchRawJsonAsync(client, route, JsonSerializer.Serialize(payload));

    private static Task<HttpResponseMessage> PatchRawJsonAsync(HttpClient client, string route, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return client.SendAsync(request);
    }
}
