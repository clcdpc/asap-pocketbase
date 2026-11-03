using System.Globalization;
using System.Net;
using System.Text.Json;
using Asap.Web.Features.Administration;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    private const int AuditLibrary = 384434;
    private const string AuditMarker = "i434-fixture";
    private const long AuditHighId = 9_007_199_254_741_000;

    [TestMethod]
    public async Task AdministrationAuditQueriesFullAuthorizedHistoryWithStablePagingAndSafeRecordedDetails()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedAdministrationAuditAsync(actor.Id);
        try
        {
            using var first = await AuditGetAsync(client, $"scope={AuditLibrary}");
            var data = first.RootElement.GetProperty("data");
            Assert.AreEqual(268, data.GetProperty("count").GetInt32());
            Assert.AreEqual(25, data.GetProperty("items").GetArrayLength());
            var firstIds = data.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
            Assert.AreEqual((AuditHighId + 6).ToString(CultureInfo.InvariantCulture), firstIds[0]);
            Assert.AreEqual((AuditHighId + 5).ToString(CultureInfo.InvariantCulture), firstIds[1]);
            Assert.IsTrue(data.GetProperty("actors").EnumerateArray().Any(row => row.GetProperty("name").GetString() == "Recorded actor <b>history</b>"));
            Assert.IsFalse(first.RootElement.GetRawText().Contains("private-secret-token", StringComparison.Ordinal));
            foreach (var row in data.GetProperty("items").EnumerateArray())
            {
                Assert.AreEqual(JsonValueKind.String, row.GetProperty("id").ValueKind);
                Assert.IsTrue(row.GetProperty("createdUtc").GetString()!.EndsWith('Z'));
                if (row.GetProperty("actorStaffUserId").ValueKind != JsonValueKind.Null)
                {
                    Assert.AreEqual("Recorded actor <b>history</b>", row.GetProperty("actorName").GetString());
                }
                Assert.AreEqual("Audit Library <b>recorded scope</b>", row.GetProperty("scopeName").GetString());
                Assert.IsFalse(row.TryGetProperty("detailsJson", out _));
            }
            using var second = await AuditGetAsync(client, $"scope={AuditLibrary}&page=2");
            Assert.IsFalse(second.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
                .Any(row => firstIds.Contains(row.GetProperty("id").GetString())));
            using var oldest = await AuditGetAsync(client, $"scope={AuditLibrary}&sort=oldest");
            Assert.AreEqual("older-than-200", oldest.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("targetId").GetString());
            using var old = await AuditGetAsync(client, $"scope={AuditLibrary}&search=older-than-200");
            Assert.AreEqual(1, old.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            Assert.AreEqual("older-than-200", old.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("targetId").GetString());
            foreach (var search in new[] { "Recorded actor", "account added", "saved-person@example.org", "fixture-format" })
            {
                using var found = await AuditGetAsync(client, $"scope={AuditLibrary}&search={Uri.EscapeDataString(search)}");
                Assert.IsTrue(found.RootElement.GetProperty("data").GetProperty("count").GetInt64() > 0, search);
            }
            using var hidden = await AuditGetAsync(client, $"scope={AuditLibrary}&search=private-secret-token");
            Assert.AreEqual(0, hidden.RootElement.GetProperty("data").GetProperty("count").GetInt32(), "Hidden JSON cannot influence result counts.");
            using var staff = await AuditGetAsync(client, $"scope={AuditLibrary}&category=staff&actorId={actor.Id}");
            Assert.AreEqual(261, staff.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            using var systemActor = await AuditGetAsync(client, $"scope={AuditLibrary}&actorId=system");
            Assert.AreEqual(1, systemActor.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            using var system = await AuditGetAsync(client, "scope=system&search=i434-fixture");
            Assert.AreEqual(2, system.RootElement.GetProperty("data").GetProperty("count").GetInt32(), "System includes recorded null-scope synchronization and organization 1, not library history.");
            using var all = await AuditGetAsync(client, "scope=all&search=i434-fixture");
            Assert.AreEqual(269, all.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            using var clamped = await AuditGetAsync(client, $"scope={AuditLibrary}&page=999");
            Assert.AreEqual(11, clamped.RootElement.GetProperty("data").GetProperty("page").GetInt32());
            foreach (var bad in new[] { "page=0", "pageSize=101", "sort=bad", "category=bad", "actorId=9223372036854775808", "search=" + new string('x', 201), "fromUtc=2026-11-02T00%3A00%3A00Z&beforeUtc=2026-11-01T00%3A00%3A00Z" })
            {
                using var response = await client.GetAsync($"/api/asap/staff/audit?scope={AuditLibrary}&{bad}");
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
            }
        }
        finally
        {
            await CleanupAdministrationAuditAsync();
        }
    }

    [TestMethod]
    public async Task AdministrationAuditActorChoicesNarrowFullHistoryAndRetainOnlyAuthorizedRecordedIdentities()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, AuditLibrary);
        long selected;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            var people = Enumerable.Range(0, 105).Select(index => new StaffUser
            {
                UserPrincipalName = $"i434.actor.{index}@example.org", NormalizedUserPrincipalName = $"I434.ACTOR.{index}@EXAMPLE.ORG",
                DisplayName = "Current name must not replace history", OrganizationId = AuditLibrary, Role = StaffRole.Staff, IsActive = true
            }).ToArray();
            context.StaffUsers.AddRange(people); await context.SaveChangesAsync();
            selected = people[104].Id;
            context.AdministrativeAudits.AddRange(people.Select((person, index) => new AdministrativeAudit
            {
                ActorStaffUserId = person.Id, ActorName = $"Actor {index:D3}", OrganizationId = AuditLibrary,
                Action = "staff_created", TargetType = "StaffUser", TargetId = $"{AuditMarker}-actor-{index}", CreatedUtc = new DateTime(2026, 1, 1)
            }));
            context.AdministrativeAudits.AddRange(Enumerable.Range(0, 120).Select(index => new AdministrativeAudit
            {
                ActorStaffUserId = people[0].Id, ActorName = $"Earlier recorded name {index:D3}", OrganizationId = AuditLibrary,
                Action = "staff_created", TargetType = "StaffUser", TargetId = $"{AuditMarker}-rename-{index}", CreatedUtc = new DateTime(2025, 1, 1)
            }));
            context.AdministrativeAudits.Add(new AdministrativeAudit
            {
                ActorName = "Specific name without a recorded identity", OrganizationId = AuditLibrary,
                Action = "staff_created", TargetType = "StaffUser", TargetId = $"{AuditMarker}-unknown-actor", CreatedUtc = new DateTime(2026, 1, 1)
            });
            await context.SaveChangesAsync();
        }
        try
        {
            using var first = await AuditGetAsync(client, $"scope={AuditLibrary}");
            var data = first.RootElement.GetProperty("data");
            Assert.AreEqual(100, data.GetProperty("actors").GetArrayLength());
            Assert.IsTrue(data.GetProperty("actorsTruncated").GetBoolean());
            Assert.AreEqual(1, data.GetProperty("actors").EnumerateArray().Count(item => item.GetProperty("name").GetString() == "Actor 000"), "Renamed historical records occupy one identity choice.");
            using var narrowed = await AuditGetAsync(client, $"scope={AuditLibrary}&search=Actor%20104");
            var choices = narrowed.RootElement.GetProperty("data").GetProperty("actors");
            Assert.AreEqual(1, choices.GetArrayLength());
            Assert.AreEqual(selected.ToString(CultureInfo.InvariantCulture), choices[0].GetProperty("id").GetString());
            using var retained = await AuditGetAsync(client, $"scope={AuditLibrary}&actorId={selected}&search=no-matching-record");
            Assert.AreEqual(0, retained.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            Assert.AreEqual("Actor 104", retained.RootElement.GetProperty("data").GetProperty("actors")[0].GetProperty("name").GetString());
            using var missing = await AuditGetAsync(client, $"scope={AuditLibrary}&actorId=system");
            Assert.IsTrue(missing.RootElement.GetProperty("data").GetProperty("actors").EnumerateArray()
                .Any(item => item.GetProperty("id").GetString() == "system" && item.GetProperty("name").GetString() == "Actor not recorded"));
            Assert.IsFalse(missing.RootElement.GetRawText().Contains("Current name must not replace history", StringComparison.Ordinal));
        }
        finally
        {
            await CleanupAdministrationAuditAsync();
            await using var cleanup = await contexts.CreateDbContextAsync();
            await cleanup.StaffUsers.Where(item => item.UserPrincipalName != null && item.UserPrincipalName.StartsWith("i434.actor.")).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    [DataRow("2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z", 2)]
    [DataRow("2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z", 2)]
    public async Task AdministrationAuditDateBoundsAreHalfOpenAcrossDstDays(string from, string before, int expected)
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        await SeedAdministrationAuditAsync(actor.Id);
        try
        {
            using var result = await AuditGetAsync(client, $"scope={AuditLibrary}&category=organizations&fromUtc={Uri.EscapeDataString(from)}&beforeUtc={Uri.EscapeDataString(before)}");
            var data = result.RootElement.GetProperty("data");
            Assert.AreEqual(expected, data.GetProperty("count").GetInt32());
            var lower = DateTimeOffset.Parse(from, CultureInfo.InvariantCulture);
            var upper = DateTimeOffset.Parse(before, CultureInfo.InvariantCulture);
            foreach (var row in data.GetProperty("items").EnumerateArray())
            {
                var timestamp = row.GetProperty("createdUtc").GetDateTimeOffset();
                Assert.IsTrue(timestamp >= lower && timestamp < upper);
            }
        }
        finally
        {
            await CleanupAdministrationAuditAsync();
        }
    }

    [TestMethod]
    public async Task AdministrationAuditLibraryQueriesIsolateRowsCountsActorsAndDetails()
    {
        using var client = factory!.CreateClient();
        var super = await ReadConfiguredSuperAdminAsync();
        await SeedAdministrationAuditAsync(super.Id);
        var admin = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, AuditLibrary);
        var actor = await ReadCorrectiveStaffAsync(admin);
        try
        {
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            using var own = await AuditGetAsync(client, "");
            var data = own.RootElement.GetProperty("data");
            Assert.IsTrue(data.GetProperty("items").EnumerateArray().All(row => row.GetProperty("organizationId").GetInt32() == AuditLibrary));
            Assert.AreEqual(1, data.GetProperty("libraries").GetArrayLength());
            Assert.IsFalse(own.RootElement.GetRawText().Contains("i434-system-actor", StringComparison.Ordinal));
            foreach (var scope in new[] { "all", "system", "2" })
            {
                using var forbidden = await client.GetAsync($"/api/asap/staff/audit?scope={scope}&actorId={super.Id}&search=private-secret-token");
                Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.IsFalse((await forbidden.Content.ReadAsStringAsync()).Contains("actors", StringComparison.Ordinal));
            }
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(admin.Id);
            await CleanupAdministrationAuditAsync();
        }
    }

    [TestMethod]
    public async Task AdministrationAuditRejectsSuperAdminTicketAfterValidDemotionToRequestedLibrary()
    {
        using var client = factory!.CreateClient();
        var authorizingSuper = await ReadConfiguredSuperAdminAsync();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, AuditLibrary);
        var created = await CreateCorrectiveStaffAsync(authorizingSuper, StaffRole.SuperAdmin, LibraryScope.SystemOrganizationId);
        var staleSuper = await ReadCorrectiveStaffAsync(created);
        try
        {
            // Keep the original super-admin ticket while the real lifecycle
            // boundary atomically changes both role and organization. The
            // independent bootstrap super-admin keeps that change legitimate.
            var changed = await factory.Services.GetRequiredService<StaffLifecycleService>().ChangeRoleAsync(
                authorizingSuper, created.Id,
                new StaffRoleInput(StaffVersion.Encode(created.RowVersion), StaffRole.Admin, AuditLibrary), CancellationToken.None);
            Assert.AreEqual("updated", changed.Code);
            var service = factory.Services.GetRequiredService<AdministrationService>();
            foreach (var scope in new[] { LibraryScope.ForLibrary(AuditLibrary), LibraryScope.All, LibraryScope.System })
            {
                var stale = await service.ListAuditAsync(staleSuper, new AdministrationAuditQuery(scope), CancellationToken.None);
                Assert.AreNotEqual("ok", stale.Code);
                Assert.IsNull(stale.Data, "A formerly global ticket must receive no history, count, details or library choices.");
            }
            var current = await ReadCorrectiveStaffAsync(changed.User!);
            Assert.AreEqual(StaffRole.Admin, current.Role);
            Assert.AreEqual(AuditLibrary, current.OrganizationId);
            var own = await service.ListAuditAsync(current, new AdministrationAuditQuery(LibraryScope.ForLibrary(AuditLibrary)), CancellationToken.None);
            Assert.AreEqual("ok", own.Code);
            using var projection = JsonDocument.Parse(JsonSerializer.Serialize(own.Data));
            Assert.AreEqual(1, projection.RootElement.GetProperty("libraries").GetArrayLength());
            Assert.AreEqual(AuditLibrary, projection.RootElement.GetProperty("libraries")[0].GetProperty("Id").GetInt32());
            foreach (var forbidden in new[] { LibraryScope.All, LibraryScope.System, LibraryScope.ForLibrary(2) })
            {
                var rejected = await service.ListAuditAsync(current, new AdministrationAuditQuery(forbidden), CancellationToken.None);
                Assert.AreEqual("staff_scope_forbidden", rejected.Code); Assert.IsNull(rejected.Data);
            }
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(created.Id);
            await using var cleanup = await contexts.CreateDbContextAsync();
            var targetId = created.Id.ToString(CultureInfo.InvariantCulture);
            await cleanup.AdministrativeAudits.Where(item => item.TargetType == "StaffUser" && item.TargetId == targetId).ExecuteDeleteAsync();
        }
    }

    [TestMethod]
    [DataRow("inactive")]
    [DataRow("role")]
    [DataRow("email")]
    [DataRow("organization")]
    public async Task AdministrationAuditRevalidatesStaleActorBeforeEveryProjection(string mutation)
    {
        using var client = factory!.CreateClient();
        var super = await ReadConfiguredSuperAdminAsync();
        var admin = await CreateCorrectiveStaffAsync(super, StaffRole.Admin, 2);
        var actor = await ReadCorrectiveStaffAsync(admin);
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, AuditLibrary);
        try
        {
            await using (var context = await contexts.CreateDbContextAsync())
            {
                var current = await context.StaffUsers.SingleAsync(item => item.Id == actor.Id);
                if (mutation == "inactive") { current.IsActive = false; }
                if (mutation == "role") { current.Role = StaffRole.Staff; }
                if (mutation == "email") { current.UserPrincipalName = "i434.changed@example.org"; current.NormalizedUserPrincipalName = "I434.CHANGED@EXAMPLE.ORG"; }
                if (mutation == "organization") { current.OrganizationId = AuditLibrary; }
                await context.SaveChangesAsync();
            }
            var service = factory.Services.GetRequiredService<AdministrationService>();
            var result = await service.ListAuditAsync(actor, new AdministrationAuditQuery(LibraryScope.ForLibrary(2)), CancellationToken.None);
            Assert.AreNotEqual("ok", result.Code);
            Assert.IsNull(result.Data, "Stale authority must receive no rows, options, counts or details.");
        }
        finally
        {
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }

    private static async Task<JsonDocument> AuditGetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync($"/api/asap/staff/audit?{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        return JsonDocument.Parse(body);
    }

    private async Task SeedAdministrationAuditAsync(long actorId)
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, AuditLibrary);
        await using var context = await contexts.CreateDbContextAsync();
        var library = await context.Organizations.SingleAsync(item => item.Id == AuditLibrary);
        library.DisplayName = "Audit Library <b>recorded scope</b>";
        var rows = Enumerable.Range(0, 260).Select(index => new AdministrativeAudit
        {
            ActorStaffUserId = actorId, ActorName = "Recorded actor <b>history</b>", OrganizationId = AuditLibrary,
            Action = "staff_created", TargetType = "StaffUser", TargetId = index == 0 ? "older-than-200" : $"{AuditMarker}-{index}",
            CreatedUtc = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            DetailsJson = "{\"role\":\"admin\",\"credential\":\"private-secret-token\",\"nested\":{\"password\":\"private-secret-token\"}}"
        }).ToArray();
        context.AdministrativeAudits.AddRange(rows);
        await context.SaveChangesAsync();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET IDENTITY_INSERT [asap].[AdministrativeAudit] ON;
            INSERT INTO [asap].[AdministrativeAudit] ([Id],[ActorStaffUserId],[ActorName],[OrganizationId],[Action],[TargetType],[TargetId],[DetailsJson],[CreatedUtc]) VALUES
            (@id,@actor,N'Recorded actor <b>history</b>',@scope,N'organization_activated',N'Organization',N'i434-fixture-spring-start',N'{"active":true}', '2026-03-08T05:00:00'),
            (@id+1,@actor,N'Recorded actor <b>history</b>',@scope,N'organization_activated',N'Organization',N'i434-fixture-spring-end',N'{"active":true}', '2026-03-09T03:59:59.9999999'),
            (@id+2,@actor,N'Recorded actor <b>history</b>',@scope,N'organization_activated',N'Organization',N'i434-fixture-fall-start',N'{"active":true}', '2026-11-01T04:00:00'),
            (@id+3,@actor,N'Recorded actor <b>history</b>',@scope,N'organization_activated',N'Organization',N'i434-fixture-fall-end',N'{"active":true}', '2026-11-02T04:59:59.9999999'),
            (@id+4,NULL,NULL,@scope,N'custom_format_deleted',N'MaterialFormat',N'i434-fixture-format',N'{"Label":"fixture-format <img src=x>","Code":"fixture","token":"private-secret-token"}', '2026-11-02T05:00:00'),
            (@id+5,@actor,N'Recorded actor <b>history</b>',@scope,N'staff_metadata_updated',N'StaffUser',N'i434-fixture-person',N'{"authenticationEmail":"saved-person@example.org","token":"private-secret-token"}', '2026-11-03T15:00:00.1234567'),
            (@id+6,@actor,N'Recorded actor <b>history</b>',@scope,N'weekly_summary_force_queued',N'WeeklyStaffSummary',N'i434-fixture-summary',N'{"manualRunId":"11111111-1111-4111-8111-111111111111"}', '2026-11-03T15:00:00.1234567'),
            (@id+7,NULL,N'i434-system-actor',1,N'system_settings_updated',N'Configuration',N'i434-fixture-system',N'{"sections":["email"],"password":"private-secret-token"}', '2026-11-03T15:01:00'),
            (@id+8,NULL,N'i434-system-actor',NULL,N'organizations_synced',N'Organization',N'i434-fixture-sync',N'{"received":20,"changed":1,"token":"private-secret-token"}', '2026-11-03T15:02:00'),
            (@id+9,@actor,N'Recorded actor <b>history</b>',@scope,N'organization_activated',N'Organization',N'i434-fixture-spring-exclusive',N'{"active":true}', '2026-03-09T04:00:00');
            SET IDENTITY_INSERT [asap].[AdministrativeAudit] OFF;
            """;
        command.Parameters.AddWithValue("@id", AuditHighId);
        command.Parameters.AddWithValue("@actor", actorId);
        command.Parameters.AddWithValue("@scope", AuditLibrary);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAdministrationAuditAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId]=@scope OR [Id] BETWEEN @id AND @id+9;
            DECLARE @maximum bigint = COALESCE((SELECT MAX([Id]) FROM [asap].[AdministrativeAudit]),0);
            DBCC CHECKIDENT ('asap.AdministrativeAudit', RESEED, @maximum) WITH NO_INFOMSGS;
            """;
        command.Parameters.AddWithValue("@scope", AuditLibrary);
        command.Parameters.AddWithValue("@id", AuditHighId);
        await command.ExecuteNonQueryAsync();
    }
}
