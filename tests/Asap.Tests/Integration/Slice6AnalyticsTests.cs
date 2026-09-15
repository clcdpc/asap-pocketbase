using System.Data;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task AnalyticsSqlFixturePreservesScopedPopulationsIdentityAndMetricBoundaries()
    {
        var originalNow = timeProvider!.GetUtcNow();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var organizationIds = await AllocateAnalyticsOrganizationsAsync();
        var organizationA = organizationIds.OrganizationA;
        var organizationB = organizationIds.OrganizationB;
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        StaffUser? ordinaryRow = null;
        timeProvider.SetUtcNow(new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero));

        try
        {
            using var superClient = factory!.CreateClient();
            AddTestingStaffHeaders(superClient, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.EntraObjectId);
            using var baseline = await superClient.GetAsync("/api/asap/staff/analytics?scope=system&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, baseline.StatusCode, await baseline.Content.ReadAsStringAsync());
            using var baselineBody = JsonDocument.Parse(await baseline.Content.ReadAsStringAsync());

            await SeedSystemOrganizationAnalyticsFixtureAsync(suffix);

            using var allWithSystemRows = await superClient.GetAsync("/api/asap/staff/analytics?scope=all&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, allWithSystemRows.StatusCode,
                await allWithSystemRows.Content.ReadAsStringAsync());
            using var allWithSystemRowsBody = JsonDocument.Parse(await allWithSystemRows.Content.ReadAsStringAsync());
            AssertAnalyticsScope(allWithSystemRowsBody.RootElement, null, "all");
            AssertAnalyticsMetricsEqual(baselineBody.RootElement, allWithSystemRowsBody.RootElement);

            using var systemWithSystemRows = await superClient.GetAsync("/api/asap/staff/analytics?scope=system&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, systemWithSystemRows.StatusCode,
                await systemWithSystemRows.Content.ReadAsStringAsync());
            using var systemWithSystemRowsBody = JsonDocument.Parse(await systemWithSystemRows.Content.ReadAsStringAsync());
            AssertAnalyticsScope(systemWithSystemRowsBody.RootElement, null, "all");
            AssertAnalyticsMetricsEqual(baselineBody.RootElement, systemWithSystemRowsBody.RootElement);

            await SeedAnalyticsFixtureAsync(organizationA, organizationB, suffix);
            ordinaryRow = await CreateCorrectiveStaffAsync(superAdmin, "staff", organizationA);
            var ordinary = await ReadCorrectiveStaffAsync(ordinaryRow);

            using var selected = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationA}&orgId={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var selectedBody = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
            AssertAnalyticsScope(selectedBody.RootElement, organizationA, "library");
            AssertAnalyticsSummary(selectedBody.RootElement, new(8, 8, 3, 2, 1.5));
            AssertStageCounts(selectedBody.RootElement, new
            {
                suggestion = 4L,
                outstanding_purchase = 1L,
                pending_hold = 1L,
                hold_placed = 1L,
                closed = 3L,
                additional_copies = 1L
            });
            AssertClosedReasons(selectedBody.RootElement, new Dictionary<string, long>
            {
                ["hold_completed"] = 1,
                ["rejected"] = 1,
                ["unrecorded"] = 1
            });
            var aging = selectedBody.RootElement.GetProperty("aging");
            Assert.AreEqual(2, aging.GetProperty("openOlderThanThreshold").GetInt64());
            Assert.AreEqual(5, aging.GetProperty("averageAgeByStage").GetArrayLength());
            Assert.AreEqual(1, selectedBody.RootElement.GetProperty("exceptions").GetProperty("holdFailures").GetInt64());
            Assert.AreEqual(1, selectedBody.RootElement.GetProperty("exceptions").GetProperty("identifierFailures").GetInt64());
            var availableLibraryIds = selectedBody.RootElement.GetProperty("availableLibraries")
                .EnumerateArray()
                .Select(item => item.GetProperty("orgId").GetString())
                .ToArray();
            Assert.IsGreaterThanOrEqualTo(3, availableLibraryIds.Length);
            CollectionAssert.Contains(availableLibraryIds, organizationA.ToString());
            CollectionAssert.Contains(availableLibraryIds, organizationB.ToString());

            using var other = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode, await other.Content.ReadAsStringAsync());
            using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
            AssertAnalyticsScope(otherBody.RootElement, organizationB, "library");
            AssertAnalyticsSummary(otherBody.RootElement, new(3, 2, 2, 0, 0));
            AssertStageCounts(otherBody.RootElement, new
            {
                suggestion = 1L,
                outstanding_purchase = 0L,
                pending_hold = 0L,
                hold_placed = 0L,
                closed = 2L,
                additional_copies = 1L
            });
            AssertClosedReasons(otherBody.RootElement, new Dictionary<string, long>
            {
                ["manual"] = 1,
                ["unrecorded"] = 1
            });

            using var all = await superClient.GetAsync("/api/asap/staff/analytics?scope=all&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, all.StatusCode, await all.Content.ReadAsStringAsync());
            using var allBody = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
            AssertAnalyticsScope(allBody.RootElement, null, "all");
            AssertAnalyticsFixtureDeltas(baselineBody.RootElement, allBody.RootElement);

            await DeactivateAnalyticsOrganizationAsync(organizationB);

            using var systemAfterDeactivation = await superClient.GetAsync(
                "/api/asap/staff/analytics?scope=system&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, systemAfterDeactivation.StatusCode,
                await systemAfterDeactivation.Content.ReadAsStringAsync());
            using var systemAfterDeactivationBody =
                JsonDocument.Parse(await systemAfterDeactivation.Content.ReadAsStringAsync());
            AssertAnalyticsScope(systemAfterDeactivationBody.RootElement, null, "all");
            AssertAnalyticsMetricsEqual(allBody.RootElement, systemAfterDeactivationBody.RootElement);

            var availableAfterDeactivation = systemAfterDeactivationBody.RootElement.GetProperty("availableLibraries")
                .EnumerateArray()
                .Select(item => item.GetProperty("orgId").GetString())
                .ToArray();
            CollectionAssert.Contains(availableAfterDeactivation, organizationA.ToString());
            Assert.IsFalse(availableAfterDeactivation.Contains(organizationB.ToString(), StringComparer.Ordinal));

            using var inactive = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.BadRequest, inactive.StatusCode, await inactive.Content.ReadAsStringAsync());
            using var inactiveBody = JsonDocument.Parse(await inactive.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_scope", inactiveBody.RootElement.GetProperty("code").GetString());

            using var activeAfterDeactivation = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationA}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, activeAfterDeactivation.StatusCode,
                await activeAfterDeactivation.Content.ReadAsStringAsync());
            using var activeAfterDeactivationBody =
                JsonDocument.Parse(await activeAfterDeactivation.Content.ReadAsStringAsync());
            AssertAnalyticsScope(activeAfterDeactivationBody.RootElement, organizationA, "library");
            AssertAnalyticsMetricsEqual(selectedBody.RootElement, activeAfterDeactivationBody.RootElement);

            using var ordinaryClient = factory.CreateClient();
            AddTestingStaffHeaders(ordinaryClient, ordinary.Id, ordinary.EntraTenantId, ordinary.EntraObjectId);
            using var forged = await ordinaryClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationB}&orgId={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, forged.StatusCode, await forged.Content.ReadAsStringAsync());
            using var forgedBody = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
            AssertAnalyticsScope(forgedBody.RootElement, organizationA, "library");
            AssertAnalyticsMetricsEqual(selectedBody.RootElement, forgedBody.RootElement);
            Assert.AreEqual(0, forgedBody.RootElement.GetProperty("availableLibraries").GetArrayLength());

            using var invalid = await superClient.GetAsync("/api/asap/staff/analytics?scope=999999999&range=last30");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
            using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_scope", invalidBody.RootElement.GetProperty("code").GetString());

            using var systemOrganization = await superClient.GetAsync("/api/asap/staff/analytics?scope=1&range=last30");
            Assert.AreEqual(HttpStatusCode.BadRequest, systemOrganization.StatusCode,
                await systemOrganization.Content.ReadAsStringAsync());
            using var systemOrganizationBody = JsonDocument.Parse(await systemOrganization.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_scope", systemOrganizationBody.RootElement.GetProperty("code").GetString());

            using var lastMonth = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationA}&range=lastMonth");
            Assert.AreEqual(HttpStatusCode.OK, lastMonth.StatusCode, await lastMonth.Content.ReadAsStringAsync());
            using var lastMonthBody = JsonDocument.Parse(await lastMonth.Content.ReadAsStringAsync());
            var lastMonthSummary = lastMonthBody.RootElement.GetProperty("summary");
            Assert.AreEqual(2, lastMonthSummary.GetProperty("newSuggestions").GetInt64());
            Assert.AreEqual(0, lastMonthSummary.GetProperty("closedRequests").GetInt64());
            Assert.AreEqual(1, lastMonthSummary.GetProperty("heldRequests").GetInt64());
        }
        finally
        {
            await CleanupAnalyticsFixtureAsync(organizationA, organizationB, ordinaryRow?.Id ?? 0, suffix);
            timeProvider.SetUtcNow(originalNow);
        }
    }

    private static async Task<(int OrganizationA, int OrganizationB)> AllocateAnalyticsOrganizationsAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        while (true)
        {
            var organizationA = Random.Shared.Next(950000, 959000);
            var organizationB = organizationA + 1;
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] IN (@organizationA, @organizationB);";
            command.Parameters.Add("@organizationA", SqlDbType.Int).Value = organizationA;
            command.Parameters.Add("@organizationB", SqlDbType.Int).Value = organizationB;
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
            {
                return (organizationA, organizationB);
            }
        }
    }

    private static async Task SeedAnalyticsFixtureAsync(int organizationA, int organizationB, string suffix)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat]
                WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
            VALUES (@organizationA, N'Analytics A ' + @suffix, N'AN-A', 1),
                   (@organizationB, N'Analytics B ' + @suffix, N'AN-B', 1);

            DECLARE @t1 bigint, @t2 bigint, @t3 bigint, @t4 bigint, @t5 bigint, @t6 bigint, @t7 bigint, @t8 bigint;
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-1-' + @suffix, N'Analytics A T1 ' + @suffix, 0, @formatId, N'suggestion', '2026-09-02T12:00:00', '2026-09-02T12:00:00');
            SET @t1 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
            -- S6-P2-1: the first in-range literal hold precedes request creation and contributes 0.0 days.
            VALUES (@organizationA, N'analytics-a-2-' + @suffix, N'Analytics A T2 ' + @suffix, 0, @formatId, N'closed', N'rejected', '2026-09-06T12:00:00', '2026-09-10T12:00:00');
            SET @t2 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-3-' + @suffix, N'Analytics A T3 ' + @suffix, 0, @formatId, N'closed', N'hold_completed', '2026-08-10T12:00:00', '2026-09-04T12:00:00');
            SET @t3 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-4-' + @suffix, N'Analytics A T4 ' + @suffix, 0, @formatId, N'pending_hold', '2026-07-01T12:00:00', '2026-07-01T12:00:00');
            SET @t4 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-5-' + @suffix, N'Analytics A T5 ' + @suffix, 0, @formatId, N'hold_placed', N'error_max_retries', '2026-09-05T12:00:00', '2026-09-08T12:00:00');
            SET @t5 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-6-' + @suffix, N'Analytics A T6 ' + @suffix, 0, @formatId, N'suggestion', '2026-09-06T12:00:00', '2026-09-06T12:00:00');
            SET @t6 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-7-' + @suffix, N'Analytics A T7 ' + @suffix, 0, @formatId, N'outstanding_purchase', '2026-07-15T12:00:00', '2026-07-15T12:00:00');
            SET @t7 = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationA, N'analytics-a-8-' + @suffix, N'Analytics A T8 ' + @suffix, 0, @formatId, N'suggestion', '2026-08-16T16:00:00', '2026-08-16T16:00:00');
            SET @t8 = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequestEvent] ([TitleRequestId], [EventType], [ActorType], [CreatedUtc])
            VALUES (@t2, N'hold_placed', N'system', '2026-09-05T12:00:00'),
                   (@t2, N'hold_placed', N'system', '2026-09-06T12:00:00'),
                   (@t3, N'hold_placed', N'system', '2026-08-10T12:00:00'),
                   (@t5, N'hold_skipped', N'system', '2026-09-07T12:00:00'),
                   (@t5, N'hold_placed', N'system', '2026-09-08T12:00:00'),
                   (@t6, N'hold_skipped', N'system', '2026-09-07T12:00:00'),
                   (@t1, N'status_changed', N'system', '2026-09-09T12:00:00');

            DECLARE @tagOne bigint, @tagTwo bigint;
            INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder])
            VALUES (N'analytics-hold-failed-one-' + @suffix, N'Hold Failed - Polaris', 10);
            SET @tagOne = SCOPE_IDENTITY();
            INSERT INTO [asap].[WorkflowTag] ([Code], [Label], [SortOrder])
            VALUES (N'analytics-hold-failed-two-' + @suffix, N'hold failed: retry', 20);
            SET @tagTwo = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
            VALUES (@t5, @tagOne), (@t5, @tagTwo);

            DECLARE @collisionId bigint;
            SELECT @collisionId = CASE WHEN titleIds.[MaxId] >= copyIds.[MaxId]
                                       THEN titleIds.[MaxId] + 1000 ELSE copyIds.[MaxId] + 1000 END
            FROM (SELECT COALESCE(MAX([Id]), 0) AS [MaxId] FROM [asap].[TitleRequest]) AS titleIds
            CROSS JOIN (SELECT COALESCE(MAX([Id]), 0) AS [MaxId] FROM [asap].[AdditionalCopyRequest]) AS copyIds;
            SET IDENTITY_INSERT [asap].[TitleRequest] ON;
            INSERT INTO [asap].[TitleRequest]
                ([Id], [LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@collisionId, @organizationA, N'analytics-a-collision-' + @suffix,
                    N'Analytics A collision title ' + @suffix, 0, @formatId, N'suggestion', '2026-09-02T13:00:00', '2026-09-02T13:00:00');
            SET IDENTITY_INSERT [asap].[TitleRequest] OFF;
            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] ON;
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([Id], [LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@collisionId, @organizationA, N'analytics-copy-collision-' + @suffix,
                    N'Analytics A collision copy ' + @suffix, N'open', '2026-09-04T12:00:00', '2026-09-04T12:00:00');
            SET IDENTITY_INSERT [asap].[AdditionalCopyRequest] OFF;
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc], [ClosedUtc])
            VALUES (@organizationA, N'analytics-copy-closed-' + @suffix, N'Analytics A closed copy ' + @suffix,
                    N'closed', '2026-09-07T12:00:00', '2026-09-08T12:00:00', '2026-09-08T12:00:00');

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationB, N'analytics-b-1-' + @suffix, N'Analytics B T1 ' + @suffix, 0, @formatId, N'suggestion', '2026-09-02T12:00:00', '2026-09-02T12:00:00');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationB, N'analytics-b-2-' + @suffix, N'Analytics B T2 ' + @suffix, 0, @formatId, N'closed', N'manual', '2026-09-03T12:00:00', '2026-09-03T12:00:00');

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationB, N'analytics-b-copy-open-' + @suffix, N'Analytics B open copy ' + @suffix,
                    N'open', '2026-08-01T12:00:00', '2026-08-01T12:00:00');
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc], [ClosedUtc])
            VALUES (@organizationB, N'analytics-b-copy-closed-' + @suffix, N'Analytics B closed copy ' + @suffix,
                    N'closed', '2026-09-02T12:00:00', '2026-09-03T12:00:00', '2026-09-03T12:00:00');
            """;
        command.Parameters.Add("@organizationA", SqlDbType.Int).Value = organizationA;
        command.Parameters.Add("@organizationB", SqlDbType.Int).Value = organizationB;
        command.Parameters.Add("@suffix", SqlDbType.NVarChar, 32).Value = suffix;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedSystemOrganizationAnalyticsFixtureAsync(string suffix)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc])
            VALUES (1, N'analytics-system-copy-open-' + @suffix, N'Analytics system open copy ' + @suffix,
                    N'open', '2026-07-01T12:00:00', '2026-07-01T12:00:00');
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [BibId], [Title], [Status], [CreatedUtc], [UpdatedUtc], [ClosedUtc])
            VALUES (1, N'analytics-system-copy-closed-' + @suffix, N'Analytics system closed copy ' + @suffix,
                    N'closed', '2026-09-02T12:00:00', '2026-09-03T12:00:00', '2026-09-03T12:00:00');
            """;
        command.Parameters.Add("@suffix", SqlDbType.NVarChar, 32).Value = suffix;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAnalyticsFixtureAsync(int organizationA, int organizationB, long staffId, string suffix)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE link
            FROM [asap].[TitleRequestWorkflowTag] AS link
            INNER JOIN [asap].[TitleRequest] AS request ON request.[Id] = link.[TitleRequestId]
            WHERE request.[LibraryOrganizationId] IN (@organizationA, @organizationB);
            DELETE FROM [asap].[WorkflowTag]
            WHERE [Code] IN (N'analytics-hold-failed-one-' + @suffix, N'analytics-hold-failed-two-' + @suffix);
            DELETE FROM [asap].[AdditionalCopyRequest]
            WHERE [LibraryOrganizationId] IN (@organizationA, @organizationB)
               OR [BibId] IN (N'analytics-system-copy-open-' + @suffix, N'analytics-system-copy-closed-' + @suffix);
            DELETE FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] IN (@organizationA, @organizationB);
            DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] IN (@organizationA, @organizationB);
            DELETE FROM [asap].[StaffUser] WHERE [Id] = @staffId;
            DELETE FROM [asap].[Organization] WHERE [Id] IN (@organizationA, @organizationB);
            """;
        command.Parameters.Add("@organizationA", SqlDbType.Int).Value = organizationA;
        command.Parameters.Add("@organizationB", SqlDbType.Int).Value = organizationB;
        command.Parameters.Add("@staffId", SqlDbType.BigInt).Value = staffId;
        command.Parameters.Add("@suffix", SqlDbType.NVarChar, 32).Value = suffix;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeactivateAnalyticsOrganizationAsync(int organizationId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @organizationId;";
        command.Parameters.Add("@organizationId", SqlDbType.Int).Value = organizationId;
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    private static void AssertAnalyticsScope(JsonElement body, int? organizationId, string mode)
    {
        var scope = body.GetProperty("scope");
        Assert.AreEqual(mode, scope.GetProperty("mode").GetString());
        Assert.AreEqual(organizationId?.ToString() ?? string.Empty, scope.GetProperty("libraryOrgId").GetString());
    }

    private static void AssertAnalyticsSummary(JsonElement body, AnalyticsSummaryExpectation expected)
    {
        var summary = body.GetProperty("summary");
        Assert.AreEqual(expected.NewSuggestions, summary.GetProperty("newSuggestions").GetInt64());
        Assert.AreEqual(expected.OpenRequests, summary.GetProperty("openRequests").GetInt64());
        Assert.AreEqual(expected.ClosedRequests, summary.GetProperty("closedRequests").GetInt64());
        Assert.AreEqual(expected.HeldRequests, summary.GetProperty("heldRequests").GetInt64());
        Assert.AreEqual(expected.AverageDaysToHold, summary.GetProperty("averageDaysToHold").GetDouble(), 0.0001);
    }

    private static void AssertStageCounts(JsonElement body, object expected)
    {
        var stages = body.GetProperty("stageCounts");
        foreach (var property in expected.GetType().GetProperties())
        {
            Assert.AreEqual(
                (long)property.GetValue(expected)!,
                stages.GetProperty(property.Name).GetInt64(),
                property.Name);
        }
    }

    private static void AssertClosedReasons(JsonElement body, IReadOnlyDictionary<string, long> expected)
    {
        var actual = ReadClosedReasons(body);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var item in expected) Assert.AreEqual(item.Value, actual[item.Key], item.Key);
    }

    private static void AssertAnalyticsFixtureDeltas(JsonElement before, JsonElement after)
    {
        var beforeSummary = before.GetProperty("summary");
        var afterSummary = after.GetProperty("summary");
        Assert.AreEqual(beforeSummary.GetProperty("newSuggestions").GetInt64() + 11,
            afterSummary.GetProperty("newSuggestions").GetInt64());
        Assert.AreEqual(beforeSummary.GetProperty("openRequests").GetInt64() + 10,
            afterSummary.GetProperty("openRequests").GetInt64());
        Assert.AreEqual(beforeSummary.GetProperty("closedRequests").GetInt64() + 5,
            afterSummary.GetProperty("closedRequests").GetInt64());
        Assert.AreEqual(beforeSummary.GetProperty("heldRequests").GetInt64() + 2,
            afterSummary.GetProperty("heldRequests").GetInt64());
        var expectedAverage = (
            beforeSummary.GetProperty("heldRequests").GetInt64() * beforeSummary.GetProperty("averageDaysToHold").GetDouble() + 3.0)
            / (beforeSummary.GetProperty("heldRequests").GetInt64() + 2);
        Assert.AreEqual(expectedAverage, afterSummary.GetProperty("averageDaysToHold").GetDouble(), 0.0001);

        AssertStageCountDeltas(before, after, new Dictionary<string, long>
        {
            ["suggestion"] = 5,
            ["outstanding_purchase"] = 1,
            ["pending_hold"] = 1,
            ["hold_placed"] = 1,
            ["closed"] = 5,
            ["additional_copies"] = 2
        });
        AssertClosedReasonDeltas(before, after, new Dictionary<string, long>
        {
            ["hold_completed"] = 1,
            ["rejected"] = 1,
            ["unrecorded"] = 2,
            ["manual"] = 1
        });

        var beforeAging = before.GetProperty("aging");
        var afterAging = after.GetProperty("aging");
        Assert.AreEqual(beforeAging.GetProperty("openOlderThanThreshold").GetInt64() + 3,
            afterAging.GetProperty("openOlderThanThreshold").GetInt64());
        AssertStageCountDeltas(beforeAging, afterAging, new Dictionary<string, long>
        {
            ["suggestion"] = 5,
            ["outstanding_purchase"] = 1,
            ["pending_hold"] = 1,
            ["hold_placed"] = 1,
            ["additional_copies"] = 2
        }, "averageAgeByStage");

        var beforeExceptions = before.GetProperty("exceptions");
        var afterExceptions = after.GetProperty("exceptions");
        Assert.AreEqual(beforeExceptions.GetProperty("holdFailures").GetInt64() + 1,
            afterExceptions.GetProperty("holdFailures").GetInt64());
        Assert.AreEqual(beforeExceptions.GetProperty("identifierFailures").GetInt64() + 1,
            afterExceptions.GetProperty("identifierFailures").GetInt64());
    }

    private static void AssertAnalyticsMetricsEqual(JsonElement expected, JsonElement actual)
    {
        var expectedSummary = expected.GetProperty("summary");
        var actualSummary = actual.GetProperty("summary");
        Assert.AreEqual(expectedSummary.GetProperty("newSuggestions").GetInt64(), actualSummary.GetProperty("newSuggestions").GetInt64());
        Assert.AreEqual(expectedSummary.GetProperty("openRequests").GetInt64(), actualSummary.GetProperty("openRequests").GetInt64());
        Assert.AreEqual(expectedSummary.GetProperty("closedRequests").GetInt64(), actualSummary.GetProperty("closedRequests").GetInt64());
        Assert.AreEqual(expectedSummary.GetProperty("heldRequests").GetInt64(), actualSummary.GetProperty("heldRequests").GetInt64());
        Assert.AreEqual(expectedSummary.GetProperty("averageDaysToHold").GetDouble(),
            actualSummary.GetProperty("averageDaysToHold").GetDouble(), 0.0001);
        AssertStageCountsEqual(expected, actual);
        AssertClosedReasonsEqual(expected, actual);

        var expectedAging = expected.GetProperty("aging");
        var actualAging = actual.GetProperty("aging");
        Assert.AreEqual(expectedAging.GetProperty("thresholdDays").GetInt32(), actualAging.GetProperty("thresholdDays").GetInt32());
        Assert.AreEqual(expectedAging.GetProperty("openOlderThanThreshold").GetInt64(),
            actualAging.GetProperty("openOlderThanThreshold").GetInt64());
        AssertStageCountsEqual(expectedAging, actualAging, "averageAgeByStage", includeAverage: true);

        var expectedExceptions = expected.GetProperty("exceptions");
        var actualExceptions = actual.GetProperty("exceptions");
        Assert.AreEqual(expectedExceptions.GetProperty("holdFailures").GetInt64(),
            actualExceptions.GetProperty("holdFailures").GetInt64());
        Assert.AreEqual(expectedExceptions.GetProperty("identifierFailures").GetInt64(),
            actualExceptions.GetProperty("identifierFailures").GetInt64());
    }

    private static void AssertStageCountDeltas(
        JsonElement before,
        JsonElement after,
        IReadOnlyDictionary<string, long> expectedDeltas,
        string propertyName = "stageCounts")
    {
        var beforeStages = before.GetProperty(propertyName);
        var afterStages = after.GetProperty(propertyName);
        foreach (var item in expectedDeltas)
        {
            var beforeCount = propertyName == "stageCounts"
                ? beforeStages.GetProperty(item.Key).GetInt64()
                : beforeStages.EnumerateArray().Single(row => row.GetProperty("status").GetString() == item.Key).GetProperty("count").GetInt64();
            var afterCount = propertyName == "stageCounts"
                ? afterStages.GetProperty(item.Key).GetInt64()
                : afterStages.EnumerateArray().Single(row => row.GetProperty("status").GetString() == item.Key).GetProperty("count").GetInt64();
            Assert.AreEqual(beforeCount + item.Value, afterCount, item.Key);
        }
    }

    private static void AssertStageCountsEqual(
        JsonElement expected,
        JsonElement actual,
        string propertyName = "stageCounts",
        bool includeAverage = false)
    {
        var expectedStages = expected.GetProperty(propertyName);
        var actualStages = actual.GetProperty(propertyName);
        if (propertyName == "stageCounts")
        {
            foreach (var stage in new[] { "suggestion", "outstanding_purchase", "pending_hold", "hold_placed", "closed", "additional_copies" })
            {
                Assert.AreEqual(expectedStages.GetProperty(stage).GetInt64(), actualStages.GetProperty(stage).GetInt64(), stage);
            }
        }
        else
        {
            foreach (var expectedStage in expectedStages.EnumerateArray())
            {
                var status = expectedStage.GetProperty("status").GetString();
                var actualStage = actualStages.EnumerateArray().Single(row => row.GetProperty("status").GetString() == status);
                Assert.AreEqual(expectedStage.GetProperty("count").GetInt64(), actualStage.GetProperty("count").GetInt64(), status);
                if (includeAverage)
                {
                    Assert.AreEqual(expectedStage.GetProperty("averageAgeDays").GetDouble(),
                        actualStage.GetProperty("averageAgeDays").GetDouble(), 0.0001, status);
                }
            }
        }
    }

    private static void AssertClosedReasonDeltas(
        JsonElement before,
        JsonElement after,
        IReadOnlyDictionary<string, long> expectedDeltas)
    {
        var beforeReasons = ReadClosedReasons(before);
        var afterReasons = ReadClosedReasons(after);
        foreach (var reason in beforeReasons.Keys.Union(afterReasons.Keys).Union(expectedDeltas.Keys))
        {
            beforeReasons.TryGetValue(reason, out var beforeCount);
            afterReasons.TryGetValue(reason, out var afterCount);
            expectedDeltas.TryGetValue(reason, out var expectedDelta);
            Assert.AreEqual(beforeCount + expectedDelta, afterCount, reason);
        }
    }

    private static void AssertClosedReasonsEqual(JsonElement expected, JsonElement actual)
    {
        CollectionAssert.AreEquivalent(ReadClosedReasons(expected).Keys.ToArray(), ReadClosedReasons(actual).Keys.ToArray());
        foreach (var item in ReadClosedReasons(expected))
        {
            Assert.AreEqual(item.Value, ReadClosedReasons(actual)[item.Key], item.Key);
        }
    }

    private static Dictionary<string, long> ReadClosedReasons(JsonElement body) => body.GetProperty("closedReasons")
        .EnumerateArray()
        .ToDictionary(item => item.GetProperty("reason").GetString()!, item => item.GetProperty("count").GetInt64());

    private readonly record struct AnalyticsSummaryExpectation(
        long NewSuggestions,
        long OpenRequests,
        long ClosedRequests,
        long HeldRequests,
        double AverageDaysToHold);
}
