using System.Data;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task AnalyticsCustomRangeValidatesCalendarDatesWithoutPresetFallback()
    {
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail);
        client.Timeout = TimeSpan.FromSeconds(10);
        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.Transaction = (SqlTransaction)transaction;
        // An aggregate read would block here; invalid dates must complete before touching its population.
        lockCommand.CommandText = "SELECT COUNT_BIG(*) FROM [asap].[TitleRequest] WITH (TABLOCKX, HOLDLOCK);";
        await lockCommand.ExecuteScalarAsync();

        (string? Start, string? End, string Fields)[] invalidRanges =
        [
            (null, null, "startDate,endDate"),
            (null, "2026-03-08", "startDate"),
            ("2026-03-08", null, "endDate"),
            ("", "2026-03-08", "startDate"),
            ("2026-03-08", "", "endDate"),
            ("2026-3-08", "2026-03-08", "startDate"),
            ("03/08/2026", "2026-03-08", "startDate"),
            ("2026-03-08T00:00:00Z", "2026-03-08", "startDate"),
            ("2026-03-08", "2026-03-08+05:00", "endDate"),
            (" 2026-03-08", "2026-03-08", "startDate"),
            ("2026-03-08", "2026-03-08 ", "endDate"),
            ("2026-02-29", "2026-03-01", "startDate"),
            ("2024-02-29", "2024-02-30", "endDate"),
            ("0000-01-01", "2026-03-08", "startDate"),
            ("2026-03-08", "10000-01-01", "endDate"),
            ("2026-03-09", "2026-03-08", "endDate"),
            ("9999-12-30", "9999-12-31", "endDate"),
            ("9999-12-31", "9999-12-31", "endDate")
        ];
        foreach (var item in invalidRanges)
        {
            var query = "/api/asap/staff/analytics?scope=all&range=custom";
            if (item.Start is not null)
            {
                query += "&startDate=" + Uri.EscapeDataString(item.Start);
            }
            if (item.End is not null)
            {
                query += "&endDate=" + Uri.EscapeDataString(item.End);
            }

            using var response = await client.GetAsync(query);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, query);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_date_range", body.RootElement.GetProperty("code").GetString(), query);
            Assert.IsFalse(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("message").GetString()), query);
            var errors = body.RootElement.GetProperty("errors");
            CollectionAssert.AreEquivalent(item.Fields.Split(','), errors.EnumerateObject().Select(field => field.Name).ToArray(), query);
            foreach (var field in errors.EnumerateObject())
            {
                Assert.IsGreaterThan(0, field.Value.GetArrayLength(), query);
                Assert.IsFalse(string.IsNullOrWhiteSpace(field.Value[0].GetString()), query);
            }
        }
    }

    [TestMethod]
    public async Task AnalyticsPresetsAndUnknownDefaultIgnoreExtraCustomDateParameters()
    {
        var originalNow = timeProvider!.GetUtcNow();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero));
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient();
        AddTestingStaffHeaders(client, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail);
        try
        {
            foreach (var key in new[] { "", "unknown", "last30", "last90", "lastMonth", "thisMonth" })
            {
                var query = "/api/asap/staff/analytics?scope=all" + (key.Length == 0 ? "" : "&range=" + key);
                using var baseline = await ReadAnalyticsReportAsync(client, query);
                using var extraDates = await ReadAnalyticsReportAsync(client,
                    query + "&startDate=invalid&endDate=9999-12-31");
                var expectedKey = key is "" or "unknown" ? "last30" : key;
                Assert.AreEqual(expectedKey, extraDates.RootElement.GetProperty("dateRange").GetProperty("key").GetString());
                Assert.AreEqual(baseline.RootElement.GetProperty("dateRange").GetRawText(),
                    extraDates.RootElement.GetProperty("dateRange").GetRawText(), key);
                AssertAnalyticsMetricsEqual(baseline.RootElement, extraDates.RootElement);
            }
        }
        finally
        {
            timeProvider.SetUtcNow(originalNow);
        }
    }

    [TestMethod]
    [DataRow("2026-03-08", 3, 8, 5, 9, 4, 23)]
    [DataRow("2026-11-01", 11, 1, 4, 2, 5, 25)]
    public async Task AnalyticsCustomDayPreservesSqlBoundariesCurrentMetricsAndDurableAuthority(
        string date,
        int month,
        int startDay,
        int startHour,
        int endDay,
        int endHour,
        int hours)
    {
        var originalNow = timeProvider!.GetUtcNow();
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (organizationA, organizationB) = await AllocateAnalyticsOrganizationsAsync();
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        StaffUser? ordinaryRow = null;
        var startUtc = new DateTime(2026, month, startDay, startHour, 0, 0, DateTimeKind.Utc);
        var endExclusiveUtc = new DateTime(2026, month, endDay, endHour, 0, 0, DateTimeKind.Utc);
        var customQuery = $"range=custom&startDate={date}&endDate={date}";

        try
        {
            await SeedAnalyticsFixtureAsync(organizationA, organizationB, suffix);
            ordinaryRow = await CreateCorrectiveStaffAsync(superAdmin, "staff", organizationA);
            var ordinary = await ReadCorrectiveStaffAsync(ordinaryRow);
            using var superClient = factory!.CreateClient();
            AddTestingStaffHeaders(superClient, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.AuthenticationEmail);
            using var allBefore = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope=all&{customQuery}");
            await SeedAnalyticsDateBoundariesAsync(organizationA, suffix, startUtc, endExclusiveUtc);

            var provider = factory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            var sender = (RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>();
            var beforeRead = await ReadAnalyticsDurableStateAsync();
            var beforeVersions = await ReadAnalyticsPersistedVersionsAsync();
            var providerCalls = provider.Calls.Count;
            var sentEmails = sender.Envelopes.Count;
            var dispatchedEmails = dispatcher!.EnqueuedIds.Count;

            using var selected = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope={organizationA}&{customQuery}");
            var body = selected.RootElement;
            AssertAnalyticsScope(body, organizationA, "library");
            AssertAnalyticsSummary(body, new(4, 8, 4, 2, 0));
            AssertClosedReasons(body, new Dictionary<string, long> { ["manual"] = 2, ["unrecorded"] = 2 });
            var range = body.GetProperty("dateRange");
            Assert.AreEqual("custom", range.GetProperty("key").GetString());
            Assert.AreEqual(date, range.GetProperty("startDate").GetString());
            Assert.AreEqual(date, range.GetProperty("endDate").GetString());
            Assert.AreEqual("America/New_York", range.GetProperty("timeZoneId").GetString());
            Assert.AreEqual(new DateTimeOffset(startUtc), range.GetProperty("start").GetDateTimeOffset());
            Assert.AreEqual(new DateTimeOffset(endExclusiveUtc), range.GetProperty("endExclusiveUtc").GetDateTimeOffset());
            Assert.AreEqual(new DateTimeOffset(endExclusiveUtc.AddTicks(-1)), range.GetProperty("end").GetDateTimeOffset());
            Assert.AreEqual(hours, (range.GetProperty("endExclusiveUtc").GetDateTimeOffset()
                - range.GetProperty("start").GetDateTimeOffset()).TotalHours);

            using var future = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope={organizationA}&range=custom&startDate=2050-01-01&endDate=2051-12-31");
            AssertAnalyticsSummary(future.RootElement, new(0, 8, 0, 0, 0));
            AssertAnalyticsCurrentMetricsEqual(body, future.RootElement);
            using var preset = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope={organizationA}&range=lastMonth");
            AssertAnalyticsCurrentMetricsEqual(body, preset.RootElement);

            using var other = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope={organizationB}&{customQuery}");
            AssertAnalyticsScope(other.RootElement, organizationB, "library");
            AssertAnalyticsSummary(other.RootElement, new(0, 2, 0, 0, 0));
            using var all = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope=all&{customQuery}");
            AssertAnalyticsScope(all.RootElement, null, "all");
            Assert.AreEqual(allBefore.RootElement.GetProperty("summary").GetProperty("newSuggestions").GetInt64() + 4,
                all.RootElement.GetProperty("summary").GetProperty("newSuggestions").GetInt64());
            Assert.AreEqual(allBefore.RootElement.GetProperty("summary").GetProperty("closedRequests").GetInt64() + 4,
                all.RootElement.GetProperty("summary").GetProperty("closedRequests").GetInt64());
            Assert.AreEqual(allBefore.RootElement.GetProperty("summary").GetProperty("heldRequests").GetInt64() + 2,
                all.RootElement.GetProperty("summary").GetProperty("heldRequests").GetInt64());

            using var ordinaryClient = factory.CreateClient();
            AddTestingStaffHeaders(ordinaryClient, ordinary.Id, ordinary.EntraTenantId, ordinary.AuthenticationEmail);
            foreach (var forgedScope in new[] { organizationA.ToString(), organizationB.ToString(), "all" })
            {
                using var forged = await ReadAnalyticsReportAsync(ordinaryClient,
                    $"/api/asap/staff/analytics?scope={forgedScope}&{customQuery}");
                AssertAnalyticsScope(forged.RootElement, organizationA, "library");
                AssertAnalyticsMetricsEqual(body, forged.RootElement);
                Assert.AreEqual(0, forged.RootElement.GetProperty("availableLibraries").GetArrayLength());
            }

            var afterVersions = await ReadAnalyticsPersistedVersionsAsync();
            Assert.AreEqual(beforeVersions.Rows, afterVersions.Rows, "Analytics reads must preserve every persisted rowversion.");
            TestContext.WriteLine($"Analytics read-only check: DBTS {beforeVersions.Counter} -> {afterVersions.Counter}; all persisted rowversions unchanged.");
            Assert.AreEqual(beforeRead, await ReadAnalyticsDurableStateAsync(), "Analytics reads must not write data, events, outbox or schema.");
            Assert.AreEqual(providerCalls, provider.Calls.Count, "Analytics must not call Polaris.");
            Assert.AreEqual(sentEmails, sender.Envelopes.Count);
            Assert.AreEqual(dispatchedEmails, dispatcher.EnqueuedIds.Count);

            await SetAnalyticsStaffAuthorityAsync(ordinary.Id, "super_admin", 1, true);
            using var elevated = await ReadAnalyticsReportAsync(ordinaryClient,
                $"/api/asap/staff/analytics?scope=all&{customQuery}");
            AssertAnalyticsMetricsEqual(all.RootElement, elevated.RootElement);
            await SetAnalyticsStaffAuthorityAsync(ordinary.Id, "staff", organizationB, true);
            using var contracted = await ReadAnalyticsReportAsync(ordinaryClient,
                $"/api/asap/staff/analytics?scope=all&{customQuery}");
            AssertAnalyticsScope(contracted.RootElement, organizationB, "library");
            AssertAnalyticsMetricsEqual(other.RootElement, contracted.RootElement);
            await SetAnalyticsStaffAuthorityAsync(ordinary.Id, "staff", organizationB, false);
            using var revoked = await ordinaryClient.GetAsync($"/api/asap/staff/analytics?scope=all&{customQuery}");
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);

            await DeactivateAnalyticsOrganizationAsync(organizationB);
            using var inactiveHistory = await ReadAnalyticsReportAsync(superClient,
                $"/api/asap/staff/analytics?scope=all&{customQuery}");
            AssertAnalyticsMetricsEqual(all.RootElement, inactiveHistory.RootElement);
            Assert.IsFalse(inactiveHistory.RootElement.GetProperty("availableLibraries").EnumerateArray()
                .Any(library => library.GetProperty("orgId").GetInt32() == organizationB));
            foreach (var invalidScope in new[] { organizationB.ToString(), "999999999", "system", "1" })
            {
                using var invalid = await superClient.GetAsync(
                    $"/api/asap/staff/analytics?scope={invalidScope}&{customQuery}");
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
                Assert.AreEqual("invalid_scope", invalidBody.RootElement.GetProperty("code").GetString());
            }
        }
        finally
        {
            await CleanupAnalyticsFixtureAsync(organizationA, organizationB, ordinaryRow?.Id ?? 0, suffix);
            timeProvider.SetUtcNow(originalNow);
        }
    }

    private static async Task<JsonDocument> ReadAnalyticsReportAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(query);
        var json = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
        return JsonDocument.Parse(json);
    }

    private static async Task SeedAnalyticsDateBoundariesAsync(
        int organizationId,
        string suffix,
        DateTime startUtc,
        DateTime endExclusiveUtc)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @times TABLE ([BoundaryUtc] datetime2(7) NOT NULL);
            INSERT INTO @times VALUES (@beforeStart), (@atStart), (@beforeEnd), (@atEnd);
            DECLARE @titles TABLE ([Id] bigint NOT NULL, [CreatedUtc] datetime2(7) NOT NULL);
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId],
                 [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
            OUTPUT INSERTED.[Id], INSERTED.[CreatedUtc] INTO @titles
            SELECT @organizationId, N'analytics-boundary-' + @suffix, N'Analytics date boundary ' + @suffix,
                0, @formatId, N'closed', N'manual', [BoundaryUtc], [BoundaryUtc]
            FROM @times;
            INSERT INTO [asap].[TitleRequestEvent] ([TitleRequestId], [EventType], [ActorType], [CreatedUtc])
            SELECT [Id], N'hold_placed', N'system', [CreatedUtc] FROM @titles;
            -- A repeated in-range hold cannot turn an earlier first hold into a period hold.
            INSERT INTO [asap].[TitleRequestEvent] ([TitleRequestId], [EventType], [ActorType], [CreatedUtc])
            SELECT [Id], N'hold_placed', N'system', @atStart FROM @titles WHERE [CreatedUtc] = @beforeStart;
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc], [ClosedUtc])
            SELECT @organizationId, 98007, N'Analytics copy date boundary ' + @suffix,
                N'closed', [BoundaryUtc], [BoundaryUtc], [BoundaryUtc]
            FROM @times;
            """;
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        command.Parameters.Add("@suffix", SqlDbType.NVarChar, 32).Value = suffix;
        // The DACPAC columns are datetime2(7): one tick is an exact SQL boundary distinction.
        foreach (var (name, value) in new[]
                 {
                     ("@beforeStart", startUtc.AddTicks(-1)), ("@atStart", startUtc),
                     ("@beforeEnd", endExclusiveUtc.AddTicks(-1)), ("@atEnd", endExclusiveUtc)
                 })
        {
            var parameter = command.Parameters.Add(name, SqlDbType.DateTime2);
            parameter.Scale = 7;
            parameter.Value = value;
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadAnalyticsDurableStateAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT (SELECT COUNT_BIG(*) FROM [asap].[TitleRequest]) AS [Titles],
                (SELECT COUNT_BIG(*) FROM [asap].[AdditionalCopyRequest]) AS [Copies],
                (SELECT COUNT_BIG(*) FROM [asap].[TitleRequestEvent]) AS [Events],
                (SELECT COUNT_BIG(*) FROM [asap].[TitleRequestWorkflowTag]) AS [Tags],
                (SELECT COUNT_BIG(*) FROM [asap].[EmailOutbox]) AS [Outbox],
                (SELECT COUNT_BIG(*) FROM [asap].[EmailDeliveryEvent]) AS [Deliveries],
                (SELECT COUNT_BIG(*) FROM [asap].[AdministrativeAudit]) AS [Audit],
                (SELECT COUNT_BIG(*) FROM [asap].[HoldPlacementOperation]) AS [HoldOperations],
                (SELECT COUNT_BIG(*) FROM [asap].[PickupPreferenceOperation]) AS [PickupOperations],
                (SELECT CHECKSUM_AGG(BINARY_CHECKSUM([object_id], [name], [type], [modify_date]))
                 FROM sys.objects WHERE [is_ms_shipped] = 0) AS [Schema]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Counter, string Rows)> ReadAnalyticsPersistedVersionsAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(varchar(18), @@DBTS, 1);";
        var counter = (string)(await command.ExecuteScalarAsync())!;
        command.CommandText =
            """
            SELECT schemas.[name], tables.[name], columns.[name]
            FROM sys.tables AS tables
            INNER JOIN sys.schemas AS schemas ON schemas.[schema_id] = tables.[schema_id]
            INNER JOIN sys.columns AS columns ON columns.[object_id] = tables.[object_id]
            WHERE schemas.[name] IN (N'asap', N'HangFire') AND columns.[system_type_id] = 189
            ORDER BY schemas.[name], tables.[name];
            """;
        var tables = new List<(string Schema, string Table, string Column)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // Compare actual stored versions: the global allocation counter alone does not identify changed rows.
        var rows = new List<string>();
        using var identifiers = new SqlCommandBuilder();
        foreach (var table in tables)
        {
            var column = identifiers.QuoteIdentifier(table.Column);
            var name = identifiers.QuoteIdentifier(table.Schema) + "." + identifiers.QuoteIdentifier(table.Table);
            command.CommandText = $"SELECT CONVERT(varchar(18), {column}, 1) FROM {name} ORDER BY {column};";
            await using var reader = await command.ExecuteReaderAsync();
            rows.Add(name);
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }
        }

        return (counter, string.Join('\n', rows));
    }

    private static async Task SetAnalyticsStaffAuthorityAsync(long staffId, string role, int organizationId, bool active)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE [asap].[StaffUser] SET [Role] = @role, [OrganizationId] = @organizationId, [IsActive] = @active WHERE [Id] = @staffId;";
        command.Parameters.Add("@staffId", SqlDbType.BigInt).Value = staffId;
        command.Parameters.Add("@role", SqlDbType.NVarChar, 32).Value = role;
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        command.Parameters.Add("@active", SqlDbType.Bit).Value = active;
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    private static void AssertAnalyticsCurrentMetricsEqual(JsonElement expected, JsonElement actual)
    {
        Assert.AreEqual(expected.GetProperty("summary").GetProperty("openRequests").GetInt64(),
            actual.GetProperty("summary").GetProperty("openRequests").GetInt64());
        AssertStageCountsEqual(expected, actual);
        Assert.AreEqual(expected.GetProperty("aging").GetRawText(), actual.GetProperty("aging").GetRawText());
        Assert.AreEqual(expected.GetProperty("exceptions").GetRawText(), actual.GetProperty("exceptions").GetRawText());
    }
}
