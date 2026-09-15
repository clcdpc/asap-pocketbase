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
            await SeedAnalyticsFixtureAsync(organizationA, organizationB, suffix);
            ordinaryRow = await CreateCorrectiveStaffAsync(superAdmin, "staff", organizationA);
            var ordinary = await ReadCorrectiveStaffAsync(ordinaryRow);

            using var superClient = factory!.CreateClient();
            AddTestingStaffHeaders(superClient, superAdmin.Id, superAdmin.EntraTenantId, superAdmin.EntraObjectId);
            using var selected = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationA}&orgId={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
            using var selectedBody = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
            AssertAnalyticsScope(selectedBody.RootElement, organizationA, "library");
            AssertAnalyticsSummary(selectedBody.RootElement, new(8, 8, 3, 2, 2.5));
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

            using var ordinaryClient = factory.CreateClient();
            AddTestingStaffHeaders(ordinaryClient, ordinary.Id, ordinary.EntraTenantId, ordinary.EntraObjectId);
            using var forged = await ordinaryClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationB}&orgId={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, forged.StatusCode, await forged.Content.ReadAsStringAsync());
            using var forgedBody = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
            AssertAnalyticsScope(forgedBody.RootElement, organizationA, "library");
            AssertAnalyticsSummary(forgedBody.RootElement, new(8, 8, 3, 2, 2.5));
            Assert.AreEqual(0, forgedBody.RootElement.GetProperty("availableLibraries").GetArrayLength());

            using var invalid = await superClient.GetAsync("/api/asap/staff/analytics?scope=999999999&range=last30");
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
            using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_scope", invalidBody.RootElement.GetProperty("code").GetString());

            using var other = await superClient.GetAsync(
                $"/api/asap/staff/analytics?scope={organizationB}&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode, await other.Content.ReadAsStringAsync());
            using var otherBody = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
            AssertAnalyticsScope(otherBody.RootElement, organizationB, "library");
            AssertAnalyticsSummary(otherBody.RootElement, new(2, 1, 1, 0, 0));

            using var all = await superClient.GetAsync("/api/asap/staff/analytics?scope=system&range=last30");
            Assert.AreEqual(HttpStatusCode.OK, all.StatusCode, await all.Content.ReadAsStringAsync());
            using var allBody = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
            AssertAnalyticsScope(allBody.RootElement, null, "all");
            var allSummary = allBody.RootElement.GetProperty("summary");
            Assert.IsTrue(allSummary.GetProperty("newSuggestions").GetInt64() >= 10);
            Assert.IsTrue(allSummary.GetProperty("openRequests").GetInt64() >= 9);
            Assert.IsTrue(allSummary.GetProperty("closedRequests").GetInt64() >= 4);
            Assert.IsTrue(allSummary.GetProperty("heldRequests").GetInt64() >= 2);

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
            VALUES (@organizationA, N'analytics-a-2-' + @suffix, N'Analytics A T2 ' + @suffix, 0, @formatId, N'closed', N'rejected', '2026-09-03T12:00:00', '2026-09-10T12:00:00');
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
            """;
        command.Parameters.Add("@organizationA", SqlDbType.Int).Value = organizationA;
        command.Parameters.Add("@organizationB", SqlDbType.Int).Value = organizationB;
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
            DELETE FROM [asap].[AdditionalCopyRequest] WHERE [LibraryOrganizationId] IN (@organizationA, @organizationB);
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
        var actual = body.GetProperty("closedReasons").EnumerateArray()
            .ToDictionary(item => item.GetProperty("reason").GetString()!, item => item.GetProperty("count").GetInt64());
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var item in expected) Assert.AreEqual(item.Value, actual[item.Key], item.Key);
    }

    private readonly record struct AnalyticsSummaryExpectation(
        long NewSuggestions,
        long OpenRequests,
        long ClosedRequests,
        long HeldRequests,
        double AverageDaysToHold);
}
