using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PublicSuggestionStaysAcceptedWhenIdentifierSqlFollowupFailsAndBigintIdsAreStrings()
    {
        const string triggerName = "[asap].[TR_Test_PublicIdentifierFollowupFailure]";
        const string barcode = "349-sql-followup";
        const int nativePatronId = 989801;
        const string identifier = "9780000000002";
        var titleSuffix = Guid.NewGuid().ToString("N");
        var title = $"Post-commit Recovery {char.ToUpperInvariant(titleSuffix[0])}{titleSuffix[1..]}";
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        provider.AddPatron(
            new PatronSnapshot(nativePatronId, barcode, "sql-followup@example.org", "Sql", "Followup",
                1, "Adult", 101, 2, "Test Library", 101),
            [new PickupBranch(101, "Main Library")], 2);
        provider.SetIdentifierResult(identifier, 2, new(IdentifierLookupOutcome.Found, 9001));
        long previousIdentity;
        long desiredIdentity;
        await using (var identity = new SqlConnection(databaseConnectionString))
        {
            await identity.OpenAsync();
            await using var command = identity.CreateCommand();
            command.CommandText = "SELECT CONVERT(bigint, IDENT_CURRENT(N'[asap].[TitleRequest]')), COALESCE(MAX([Id]), 0) FROM [asap].[TitleRequest];";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            previousIdentity = reader.GetInt64(0);
            desiredIdentity = Math.Max(9_007_199_254_740_992L, reader.GetInt64(1)) + 1000;
        }
        using var client = factory!.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
        {
            barcode,
            pin = "1234",
            libraryOrgId = 2
        });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginBody.RootElement.GetProperty("token").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await using (var reseed = new SqlConnection(databaseConnectionString))
        {
            await reseed.OpenAsync();
            await using var command = reseed.CreateCommand();
            command.CommandText = $"DBCC CHECKIDENT ('[asap].[TitleRequest]', RESEED, {desiredIdentity});";
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteNonQueryAsync($"""
            CREATE OR ALTER TRIGGER {triggerName} ON [asap].[TitleRequest]
            AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF UPDATE([IsbnCheckStatus])
                    THROW 51073, N'Declared identifier follow-up failure after suggestion commit.', 1;
            END;
            """);

        try
        {
            var payload = new
            {
                format = "book",
                title,
                author = "Ada Example",
                isbn = identifier,
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            };
            using var created = await client.PostAsJsonAsync("/api/asap/patron/suggestions", payload);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            Assert.AreEqual(1, provider.Calls.Count(call =>
                call.Operation == TestingPolarisOperation.IdentifierLookup &&
                call.OrganizationId == 2 && call.Key == identifier),
                "The configured Found response must reach the SQL identifier follow-up that the trigger rejects.");
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var idElement = createdBody.RootElement.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, idElement.ValueKind,
                "Public SQL bigint identities must be strings at the HTTP boundary.");
            var idText = idElement.GetString();
            Assert.AreEqual((desiredIdentity + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), idText);
            Assert.AreEqual(title, await ReadCreatedRequestTitleAsync(long.Parse(idText!)));

            await using (var verify = new SqlConnection(databaseConnectionString))
            {
                await verify.OpenAsync();
                await using var command = verify.CreateCommand();
                command.CommandText = """
                    SELECT request.[IsbnCheckStatus], request.[BibId],
                           (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] event WHERE event.[TitleRequestId] = request.[Id]),
                           outbox.[Status], outbox.[BusinessKey],
                           (SELECT COUNT(*) FROM [asap].[TitleRequest] duplicate WHERE duplicate.[Barcode] = request.[Barcode] AND duplicate.[Title] = request.[Title])
                    FROM [asap].[TitleRequest] request
                    JOIN [asap].[EmailOutbox] outbox ON outbox.[BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), request.[Id])
                    WHERE request.[Id] = @requestId;
                    """;
                command.Parameters.AddWithValue("@requestId", long.Parse(idText!));
                await using var row = await command.ExecuteReaderAsync();
                Assert.IsTrue(await row.ReadAsync(), "The request, event, and outbox must remain durable after the follow-up failure.");
                Assert.AreEqual("pending", row.GetString(0));
                Assert.IsTrue(row.IsDBNull(1));
                Assert.IsGreaterThanOrEqualTo(1, row.GetInt32(2));
                Assert.AreEqual("pending", row.GetString(3));
                Assert.AreEqual($"patron-submission:{idText}", row.GetString(4));
                Assert.AreEqual(1, row.GetInt32(5));
            }

            // Keep the injected failure limited to the post-commit identifier update.
            // The subsequent staff edit is a separate EF mutation and must run without
            // the trigger so SQL Server's trigger/OUTPUT restriction does not interfere.
            await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS {triggerName};");

            var staffActor = await ReadConfiguredSuperAdminAsync();
            using var staffClient = factory!.CreateClient();
            AddTestingStaffHeaders(staffClient, staffActor.Id, staffActor.EntraTenantId, staffActor.AuthenticationEmail);
            staffClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(staffClient));
            using var pickupOptions = await staffClient.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{idText}/pickup-options", new { });
            Assert.AreEqual(HttpStatusCode.OK, pickupOptions.StatusCode, await pickupOptions.Content.ReadAsStringAsync());
            using var pickupOptionsBody = JsonDocument.Parse(await pickupOptions.Content.ReadAsStringAsync());
            var pickupRequestId = pickupOptionsBody.RootElement.GetProperty("requestId");
            Assert.AreEqual(JsonValueKind.String, pickupRequestId.ValueKind,
                "Staff pickup option request identities must remain exact decimal strings above JavaScript's safe integer range.");
            Assert.AreEqual(idText, pickupRequestId.GetString());
            var pickupBranchId = pickupOptionsBody.RootElement.GetProperty("currentPreferredPickupBranchId");
            Assert.AreEqual(JsonValueKind.Number, pickupBranchId.ValueKind,
                "Polaris native Int32 branch identifiers must remain JSON numbers.");
            Assert.AreEqual(101, pickupBranchId.GetInt32());

            using var staffEdit = await staffClient.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{idText}/action",
                new
                {
                    version = pickupOptionsBody.RootElement.GetProperty("version").GetString(),
                    action = "edit",
                    title = $"Staff edit {title}"
                });
            Assert.AreEqual(HttpStatusCode.OK, staffEdit.StatusCode, await staffEdit.Content.ReadAsStringAsync());
            using var staffEditBody = JsonDocument.Parse(await staffEdit.Content.ReadAsStringAsync());
            var staffRequestId = staffEditBody.RootElement.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, staffRequestId.ValueKind,
                "Staff mutation request identities must remain exact decimal strings above JavaScript's safe integer range.");
            Assert.AreEqual(idText, staffRequestId.GetString());

            using var duplicate = await client.PostAsJsonAsync("/api/asap/patron/suggestions", payload);
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());
            using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            Assert.AreEqual(JsonValueKind.String,
                duplicateBody.RootElement.GetProperty("duplicate").GetProperty("id").ValueKind,
                "Conflict identities above JavaScript's safe integer range must also be strings.");
            Assert.AreEqual(idText, duplicateBody.RootElement.GetProperty("duplicate").GetProperty("id").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync($"DROP TRIGGER IF EXISTS {triggerName};");
            var requestId = desiredIdentity + 1;
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id; " +
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), @id); " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id; " +
                "DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;",
                ("@id", requestId), ("@barcode", barcode));
            await ExecuteNonQueryAsync($"DBCC CHECKIDENT ('[asap].[TitleRequest]', RESEED, {previousIdentity});");
            try
            {
                using var logout = await client.PostAsync("/api/asap/patron/logout", content: null);
            }
            catch (HttpRequestException)
            {
                // The accepted request and SQL state are already asserted above; host cleanup revokes leftovers.
            }
        }
    }

    [TestMethod]
    public async Task PublicSuggestionStaysAcceptedWhenIdentifierProviderThrowsNonDbException()
    {
        const string barcode = "349-nondb-followup";
        const int nativePatronId = 989802;
        const string identifier = "9780000000003";
        var titleSuffix = Guid.NewGuid().ToString("N");
        var title = $"Non-database Follow-up Recovery {titleSuffix}";
        var acceptedTitle = $"Non-database Follow-up Recovery {char.ToUpperInvariant(titleSuffix[0])}{titleSuffix[1..]}";
        var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        provider.AddPatron(
            new PatronSnapshot(nativePatronId, barcode, "nondb-followup@example.org", "NonDb", "Followup",
                1, "Adult", 101, 2, "Test Library", 101),
            [new PickupBranch(101, "Main Library")], 2);
        provider.SetFailure(
            new TestingPolarisCall(TestingPolarisOperation.IdentifierLookup, 2, identifier),
            new InvalidOperationException("Deterministic unexpected provider failure after commit."));

        using var client = factory.CreateClient();
        try
        {
            using var login = await client.PostAsJsonAsync("/api/asap/patron/login", new
            {
                barcode,
                pin = "1234",
                libraryOrgId = 2
            });
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginBody.RootElement.GetProperty("token").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(token));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var payload = new
            {
                format = "book",
                title,
                author = "Ada Example",
                isbn = identifier,
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            };
            using var created = await client.PostAsJsonAsync("/api/asap/patron/suggestions", payload);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var idElement = createdBody.RootElement.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, idElement.ValueKind);
            var idText = idElement.GetString();
            Assert.IsNotNull(idText);
            Assert.IsTrue(long.TryParse(idText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var requestId));
            Assert.AreEqual(requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), idText);
            Assert.AreEqual(acceptedTitle, await ReadCreatedRequestTitleAsync(requestId));
            Assert.AreEqual(1, provider.Calls.Count(call =>
                call.Operation == TestingPolarisOperation.IdentifierLookup && call.Key == identifier));

            await using (var verify = new SqlConnection(databaseConnectionString))
            {
                await verify.OpenAsync();
                await using var command = verify.CreateCommand();
                command.CommandText = """
                    SELECT request.[IsbnCheckStatus], request.[BibId],
                           (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] event WHERE event.[TitleRequestId] = request.[Id]),
                           (SELECT COUNT(*) FROM [asap].[EmailOutbox] outbox WHERE outbox.[BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), request.[Id])),
                           (SELECT COUNT(*) FROM [asap].[TitleRequest] duplicate WHERE duplicate.[Barcode] = request.[Barcode] AND duplicate.[Identifier] = request.[Identifier]),
                           (SELECT [Status] FROM [asap].[EmailOutbox] outbox WHERE outbox.[BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), request.[Id]))
                    FROM [asap].[TitleRequest] request WHERE request.[Id] = @requestId;
                    """;
                command.Parameters.AddWithValue("@requestId", requestId);
                await using var row = await command.ExecuteReaderAsync();
                Assert.IsTrue(await row.ReadAsync(), "The committed request must remain visible after the provider exception.");
                Assert.AreEqual("pending", row.GetString(0));
                Assert.IsTrue(row.IsDBNull(1));
                Assert.IsGreaterThanOrEqualTo(1, row.GetInt32(2));
                Assert.AreEqual(1, row.GetInt32(3), "The accepted request must have one durable outbox row.");
                Assert.AreEqual(1, row.GetInt32(4), "The provider exception must not cause a second request insert.");
                Assert.AreEqual("pending", row.GetString(5));
            }

            using var duplicate = await client.PostAsJsonAsync("/api/asap/patron/suggestions", payload);
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());
            using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
            var duplicateId = duplicateBody.RootElement.GetProperty("duplicate").GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, duplicateId.ValueKind);
            Assert.AreEqual(idText, duplicateId.GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DECLARE @requestId bigint =
                    (SELECT TOP (1) [Id] FROM [asap].[TitleRequest] WHERE [Barcode] = @barcode AND [Title] = @title);
                DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId;
                DELETE FROM [asap].[EmailOutbox]
                    WHERE [BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), @requestId);
                DELETE FROM [asap].[TitleRequest] WHERE [Id] = @requestId;
                DELETE FROM [asap].[PatronSession] WHERE [Barcode] = @barcode;
                """, ("@barcode", barcode), ("@title", title));
            try
            {
                using var logout = await client.PostAsync("/api/asap/patron/logout", content: null);
            }
            catch (HttpRequestException)
            {
                // The accepted request and SQL state are already asserted above; host cleanup revokes leftovers.
            }
        }
    }

    [TestMethod]
    public async Task TitleActionRejectsWhitespaceAndOverlongTitleAndExposesCorruptStoredFields()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var formatId = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Persisted title",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        context.TitleRequests.Add(request);
        await context.SaveChangesAsync();
        var corrupt = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Corrupt snapshot title",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CustomFieldsJson = "{\"legacy_field\":\"unsupported scalar\"}",
            CreatedUtc = timeProvider.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        context.TitleRequests.Add(corrupt);
        await context.SaveChangesAsync();

        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        try
        {
            foreach (var invalidTitle in new[] { "   ", new string('x', 501) })
            {
                var before = await ReadTitleRequestSnapshotAsync(request.Id);
                using var response = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{request.Id}/action", new
                {
                    version = before.Version,
                    action = "edit",
                    title = invalidTitle
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(invalidTitle.Length > 500 ? "title_too_long" : "invalid_title",
                    error.RootElement.GetProperty("code").GetString());
                await AssertTitleRequestUnchangedAsync(request.Id, before);
            }

            using var corruptDetail = await client.GetAsync($"/api/asap/staff/title-requests/{corrupt.Id}?scope=all");
            Assert.AreEqual(HttpStatusCode.OK, corruptDetail.StatusCode, await corruptDetail.Content.ReadAsStringAsync());
            using var corruptDetailBody = JsonDocument.Parse(await corruptDetail.Content.ReadAsStringAsync());
            Assert.IsFalse(corruptDetailBody.RootElement.GetProperty("customFieldsValid").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, corruptDetailBody.RootElement.GetProperty("customFields").ValueKind,
                "A malformed custom-field history must be surfaced as unavailable, not represented as an empty object.");
            var corruptBefore = await ReadTitleRequestSnapshotAsync(corrupt.Id);
            using var rejectedCorruptEdit = await client.PostAsJsonAsync($"/api/asap/staff/title-requests/{corrupt.Id}/action", new
            {
                version = corruptBefore.Version,
                action = "edit",
                title = "Must not persist",
                customFields = new { }
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejectedCorruptEdit.StatusCode, await rejectedCorruptEdit.Content.ReadAsStringAsync());
            using var corruptError = JsonDocument.Parse(await rejectedCorruptEdit.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_custom_fields", corruptError.RootElement.GetProperty("code").GetString());
            await AssertTitleRequestUnchangedAsync(corrupt.Id, corruptBefore);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (@first, @second); " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] IN (@first, @second);",
                ("@first", request.Id), ("@second", corrupt.Id));
        }
    }

    [TestMethod]
    public async Task TitleActionRejectsAmbiguousCustomFieldClearsOverHttpWithoutWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var seed = await contexts.CreateDbContextAsync();
        var formatId = await seed.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 && item.Code == "book")
            .Select(item => item.Id).SingleAsync();
        var fieldKey = $"nested_clear_{Guid.NewGuid():N}";
        var field = new PatronCustomField
        {
            LibraryOrganizationId = 2,
            FieldKey = fieldKey,
            FieldType = "text",
            Label = "Nested clear note",
            IsEnabled = true,
            SortOrder = 996
        };
        seed.PatronCustomFields.Add(field);
        await seed.SaveChangesAsync();
        var rule = new MaterialFormatCustomFieldRule
        {
            LibraryOrganizationId = 2,
            MaterialFormatId = formatId,
            PatronCustomFieldId = field.Id,
            Mode = "optional"
        };
        var request = new TitleRequest
        {
            LibraryOrganizationId = 2,
            Barcode = $"2{Guid.NewGuid():N}"[..14],
            Title = "Nested clear source",
            MaterialFormatId = formatId,
            Status = "suggestion",
            AutoHold = true,
            CustomFieldsJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [fieldKey] = new { label = "Stored custom label", type = "text", value = "keep" }
            }),
            CreatedUtc = timeProvider!.GetUtcNow().UtcDateTime,
            UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime
        };
        seed.MaterialFormatCustomFieldRules.Add(rule);
        seed.TitleRequests.Add(request);
        await seed.SaveChangesAsync();

        using var client = factory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        try
        {
            var before = await ReadTitleRequestSnapshotAsync(request.Id);
            var duplicateNested = JsonDocument.Parse($"{{\"{fieldKey}\":{{\"value\":\"keep\",\"value\":null}}}}");
            Assert.AreEqual("invalid_custom_fields", new TitleRequestActionInput
            {
                Version = before.Version,
                Action = "edit",
                CustomFields = duplicateNested.RootElement.Clone()
            }.ToCommand().ValidationError);
            duplicateNested.Dispose();

            var caseAliasNested = JsonDocument.Parse($"{{\"{fieldKey}\":{{\"label\":\"Legacy label\",\"type\":\"text\",\"value\":\"keep\",\"Value\":null}}}}");
            Assert.AreEqual("invalid_custom_fields", new TitleRequestActionInput
            {
                Version = before.Version,
                Action = "edit",
                CustomFields = caseAliasNested.RootElement.Clone()
            }.ToCommand().ValidationError);
            caseAliasNested.Dispose();

            var validLegacy = JsonDocument.Parse($"{{\"{fieldKey}\":{{\"label\":\"Legacy label\",\"type\":\"text\",\"value\":\"keep\"}}}}");
            Assert.IsNull(new TitleRequestActionInput
            {
                Version = before.Version,
                Action = "edit",
                CustomFields = validLegacy.RootElement.Clone()
            }.ToCommand().ValidationError, "Legacy label/type/value objects must remain supported.");
            validLegacy.Dispose();

            var header = "\"version\":" + JsonSerializer.Serialize(before.Version) +
                ",\"action\":\"edit\",\"title\":\"Must remain unchanged\",";
            string NestedBody(string nested) => "{" + header + "\"customFields\":{\"" + fieldKey + "\":" + nested + "}}";
            var invalidBodies = new[]
            {
                NestedBody("{\"label\":\"Legacy label\",\"type\":\"text\",\"value\":\"keep\",\"value\":null}"),
                NestedBody("{\"label\":\"Legacy label\",\"type\":\"text\",\"value\":\"keep\",\"Value\":null}"),
                NestedBody("{\"label\":\"Legacy label\",\"type\":\"text\",\"value\":123}"),
                NestedBody("{\"label\":\"Legacy label\",\"type\":false,\"value\":null}"),
                "{" + header + "\"customFields\":{\"" + fieldKey + "\":{\"value\":\"keep\"}}," +
                    "\"customFields\":{\"" + fieldKey + "\":null}}",
                "{" + header + "\"customFields\":{\"" + fieldKey + "\":{\"value\":\"keep\"}}," +
                    "\"CustomFields\":{\"" + fieldKey + "\":null}}"
            };

            foreach (var body in invalidBodies)
            {
                using var rejected = await client.PostAsync(
                    $"/api/asap/staff/title-requests/{request.Id}/action",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
                await AssertTitleRequestUnchangedAsync(request.Id, before);
            }

            var validBody = "{" + header.Replace("Must remain unchanged", "Legacy object accepted", StringComparison.Ordinal) +
                "\"customFields\":{\"" + fieldKey + "\":{\"label\":\"Client label\",\"type\":\"text\",\"value\":\"keep\"}}}";
            using var accepted = await client.PostAsync(
                $"/api/asap/staff/title-requests/{request.Id}/action",
                new StringContent(validBody, Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
            var after = await ReadTitleRequestSnapshotAsync(request.Id);
            Assert.AreEqual("Legacy object accepted", after.Title);
            Assert.IsFalse(before.RowVersion.SequenceEqual(after.RowVersion),
                "The accepted title edit should advance its row version while preserving custom-field history.");
            Assert.AreEqual(before.CustomFieldsJson, after.CustomFieldsJson);
            using var storedFields = JsonDocument.Parse(after.CustomFieldsJson!);
            var storedField = storedFields.RootElement.GetProperty(fieldKey);
            Assert.AreEqual("Stored custom label", storedField.GetProperty("label").GetString());
            Assert.AreEqual("text", storedField.GetProperty("type").GetString());
            Assert.AreEqual("keep", storedField.GetProperty("value").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId; " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @requestId; " +
                "DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [Id] = @ruleId; " +
                "DELETE FROM [asap].[PatronCustomField] WHERE [Id] = @fieldId;",
                ("@requestId", request.Id), ("@ruleId", rule.Id), ("@fieldId", field.Id));
        }
    }

    private sealed record TitleRequestSnapshot(string Version, byte[] RowVersion, string Title,
        string? CustomFieldsJson, DateTime UpdatedUtc, int EventCount, int OutboxCount);

    private static async Task<TitleRequestSnapshot> ReadTitleRequestSnapshotAsync(long requestId, string? connectionString = null)
    {
        await using var connection = new SqlConnection(connectionString ?? databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request.[RowVersion], request.[Title], request.[CustomFieldsJson], request.[UpdatedUtc],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] event WHERE event.[TitleRequestId] = request.[Id]),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox] outbox WHERE outbox.[BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), request.[Id]))
            FROM [asap].[TitleRequest] request WHERE request.[Id] = @id;
            """;
        command.Parameters.AddWithValue("@id", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        var rowVersion = reader.GetFieldValue<byte[]>(0);
        return new TitleRequestSnapshot(Convert.ToBase64String(rowVersion), rowVersion, reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDateTime(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    private static async Task AssertTitleRequestUnchangedAsync(
        long requestId,
        TitleRequestSnapshot expected,
        string? connectionString = null)
    {
        var actual = await ReadTitleRequestSnapshotAsync(requestId, connectionString);
        CollectionAssert.AreEqual(expected.RowVersion, actual.RowVersion);
        Assert.AreEqual(expected.Title, actual.Title);
        Assert.AreEqual(expected.CustomFieldsJson, actual.CustomFieldsJson);
        Assert.AreEqual(expected.UpdatedUtc, actual.UpdatedUtc);
        Assert.AreEqual(expected.EventCount, actual.EventCount);
        Assert.AreEqual(expected.OutboxCount, actual.OutboxCount);
    }

    private static async Task<string> ReadCreatedRequestTitleAsync(long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT [Title] FROM [asap].[TitleRequest] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", requestId);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The accepted request did not remain in SQL."));
    }
}
