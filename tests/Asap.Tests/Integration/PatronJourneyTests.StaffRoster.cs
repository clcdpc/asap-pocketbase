using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task StaffUnifiedSaveRejectsInvalidDuplicateAndStaleInputsWithoutPartialWrites()
    {
        using var client = factory!.CreateClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);
        var seed = await SeedStaffMetadataRaceAsync("roster-atomic");
        var actor = await ReadStaffMetadataActorAsync(seed);
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        await using (var context = await UnifiedStaffContextAsync())
        {
            var target = await context.StaffUsers.SingleAsync(item => item.Id == seed.TargetId);
            target.NotificationEmail = "separate-primary@example.org";
            target.WeeklyActionSummaryEmail = "separate-weekly@example.org";
            target.LastLoginUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            await context.SaveChangesAsync();
        }
        var before = await ReadUnifiedStaffAsync(seed.TargetId);
        var input = new StaffUpdateInput(StaffVersion.Encode(before.RowVersion), " unified@example.org ", "Unified Person", "admin", 2);
        foreach (var rejected in new[]
                 {
                     (Input: input with { Email = "invalid" }, Code: "invalid_staff_user"),
                     (Input: input with { Email = actor.AuthenticationEmail }, Code: "identity_already_exists"),
                     (Input: input with { Role = "invented" }, Code: "invalid_staff_user"),
                     (Input: input with { DisplayName = new string('x', 257) }, Code: "invalid_staff_user"),
                     (Input: input with { Version = StaffVersion.Encode(seed.TargetVersion) }, Code: "stale_version")
                 })
        {
            Assert.AreEqual(rejected.Code, (await service.UpdateAsync(actor, seed.TargetId, rejected.Input, CancellationToken.None)).Code);
            await AssertUnifiedStaffUnchangedAsync(before, 0);
        }

        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        using (var noAntiforgery = await client.PutAsJsonAsync($"/api/asap/staff/users/{seed.TargetId}", input))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, noAntiforgery.StatusCode);
        }
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        using var changed = await client.PutAsJsonAsync($"/api/asap/staff/users/{seed.TargetId}", input);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        var saved = await ReadUnifiedStaffAsync(seed.TargetId);
        Assert.AreEqual("unified@example.org", saved.UserPrincipalName);
        Assert.AreEqual("UNIFIED@EXAMPLE.ORG", saved.NormalizedUserPrincipalName);
        Assert.AreEqual(saved.UserPrincipalName, saved.NotificationEmail);
        Assert.AreEqual("Unified Person", saved.DisplayName);
        Assert.AreEqual("admin", saved.Role);
        Assert.AreEqual(2, saved.OrganizationId);
        Assert.AreEqual("separate-weekly@example.org", saved.WeeklyActionSummaryEmail);
        Assert.IsNull(saved.EntraTenantId);
        Assert.IsNull(saved.EntraObjectId);
        Assert.IsNull(saved.LastLoginUtc);
        CollectionAssert.AreEquivalent(new[] { "staff_metadata_updated", "staff_lifecycle_updated" }, await UnifiedStaffAuditsAsync(seed.TargetId));

        using var staleSession = factory.CreateClient();
        AddTestingStaffHeaders(staleSession, seed.TargetId, seed.TenantId, before.UserPrincipalName!);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await staleSession.GetAsync("/api/asap/staff/session")).StatusCode,
            "Changing authentication email invalidates evidence for the former email.");
        Assert.AreEqual("stale_version", (await service.UpdateAsync(actor, seed.TargetId, input, CancellationToken.None)).Code);
        await AssertUnifiedStaffUnchangedAsync(saved, 2);
    }

    [TestMethod]
    public async Task StaffUnifiedConcurrentSavesAcceptExactlyOneCompleteDraft()
    {
        using var client = factory!.CreateClient();
        await client.GetAsync("/api/asap/staff/session");
        var seed = await SeedStaffMetadataRaceAsync("roster-concurrent");
        var actor = await ReadStaffMetadataActorAsync(seed);
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        var first = new StaffUpdateInput(StaffVersion.Encode(seed.TargetVersion), "roster-first@example.org", "First Complete", "admin", 2);
        var second = first with { Email = "roster-second@example.org", DisplayName = "Second Complete" };
        var results = await Task.WhenAll(service.UpdateAsync(actor, seed.TargetId, first, CancellationToken.None),
            service.UpdateAsync(actor, seed.TargetId, second, CancellationToken.None));
        CollectionAssert.AreEquivalent(new[] { "updated", "stale_version" }, results.Select(item => item.Code).ToArray());
        var accepted = results[0].Code == "updated" ? first : second;
        var saved = await ReadUnifiedStaffAsync(seed.TargetId);
        Assert.AreEqual(accepted.Email, saved.UserPrincipalName);
        Assert.AreEqual(accepted.Email, saved.NotificationEmail);
        Assert.AreEqual(accepted.DisplayName, saved.DisplayName);
        Assert.AreEqual(accepted.Role, saved.Role);
        Assert.AreEqual(accepted.OrganizationId, saved.OrganizationId);
        Assert.AreEqual(2, (await UnifiedStaffAuditsAsync(seed.TargetId)).Length);
    }

    [TestMethod]
    [DataRow("outgoing-scope")]
    [DataRow("incoming-scope")]
    [DataRow("super-admin")]
    [DataRow("actor-inactive")]
    [DataRow("actor-email")]
    [DataRow("actor-demoted")]
    [DataRow("actor-library-inactive")]
    public async Task StaffUnifiedSaveRevalidatesBothScopesAndCurrentActor(string boundary)
    {
        using var client = factory!.CreateClient();
        await client.GetAsync("/api/asap/staff/session");
        var seed = await SeedStaffMetadataRaceAsync($"roster-{boundary}");
        var actor = await ReadStaffMetadataActorAsync(seed);
        try
        {
            await using (var context = await UnifiedStaffContextAsync())
            {
                if (!await context.Organizations.AnyAsync(item => item.Id == 94351))
                {
                    context.Organizations.Add(new Organization { Id = 94351, DisplayName = "Roster other library", OrganizationCodeId = 2, IsActive = true, ParentOrganizationId = 1 });
                }
                var currentActor = await context.StaffUsers.SingleAsync(item => item.Id == actor.Id);
                if (boundary == "actor-inactive")
                {
                    currentActor.IsActive = false;
                }
                if (boundary == "actor-email")
                {
                    currentActor.UserPrincipalName = "roster-changed-actor@example.org";
                    currentActor.NormalizedUserPrincipalName = "ROSTER-CHANGED-ACTOR@EXAMPLE.ORG";
                }
                if (boundary == "actor-demoted")
                {
                    currentActor.Role = "staff";
                }
                if (boundary == "actor-library-inactive")
                {
                    (await context.Organizations.SingleAsync(item => item.Id == 2)).IsActive = false;
                }
                if (boundary == "outgoing-scope")
                {
                    (await context.StaffUsers.SingleAsync(item => item.Id == seed.TargetId)).OrganizationId = 94351;
                }
                await context.SaveChangesAsync();
            }
            var before = await ReadUnifiedStaffAsync(seed.TargetId);
            var input = new StaffUpdateInput(StaffVersion.Encode(before.RowVersion), "forbidden-draft@example.org", "Forbidden draft",
                boundary == "super-admin" ? "super_admin" : "admin", boundary == "incoming-scope" ? 94351 : 2);
            Assert.AreEqual("staff_scope_forbidden", (await factory.Services.GetRequiredService<StaffLifecycleService>()
                .UpdateAsync(actor, seed.TargetId, input, CancellationToken.None)).Code, boundary);
            await AssertUnifiedStaffUnchangedAsync(before, 0);
        }
        finally
        {
            if (boundary == "actor-library-inactive")
            {
                await using var context = await UnifiedStaffContextAsync();
                await context.Organizations.Where(item => item.Id == 2).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, true));
            }
        }
    }

    [TestMethod]
    public async Task StaffUnifiedMetadataSaveOnUnchangedInactiveLibraryPreservesObservedIdentity()
    {
        using var client = factory!.CreateClient();
        await client.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        var seed = await SeedStaffMetadataRaceAsync("roster-inactive-library");
        await using (var context = await UnifiedStaffContextAsync())
        {
            context.Organizations.Add(new Organization { Id = 94352, DisplayName = "Roster inactive library", OrganizationCodeId = 2, IsActive = false, ParentOrganizationId = 1 });
            var target = await context.StaffUsers.SingleAsync(item => item.Id == seed.TargetId);
            target.OrganizationId = 94352;
            target.NotificationEmail = "old-primary@example.org";
            await context.SaveChangesAsync();
        }
        var before = await ReadUnifiedStaffAsync(seed.TargetId);
        var input = new StaffUpdateInput(StaffVersion.Encode(before.RowVersion), before.UserPrincipalName, "Metadata only", before.Role, before.OrganizationId);
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        Assert.AreEqual("updated", (await service.UpdateAsync(actor, seed.TargetId, input, CancellationToken.None)).Code);
        var saved = await ReadUnifiedStaffAsync(seed.TargetId);
        Assert.AreEqual(before.UserPrincipalName, saved.NotificationEmail);
        Assert.AreEqual(before.EntraTenantId, saved.EntraTenantId);
        Assert.AreEqual(before.EntraObjectId, saved.EntraObjectId);
        Assert.AreEqual(before.LastLoginUtc, saved.LastLoginUtc);
        CollectionAssert.AreEqual(new[] { "staff_metadata_updated" }, await UnifiedStaffAuditsAsync(seed.TargetId));
        Assert.AreEqual("updated", (await service.UpdateAsync(actor, seed.TargetId,
            input with { Version = StaffVersion.Encode(saved.RowVersion) }, CancellationToken.None)).Code);
        await AssertUnifiedStaffUnchangedAsync(saved, 1);
        Assert.AreEqual("organization_inactive", (await service.UpdateAsync(actor, seed.TargetId,
            input with { Version = StaffVersion.Encode(saved.RowVersion), Role = "admin", DisplayName = "Must not persist" }, CancellationToken.None)).Code);
        await AssertUnifiedStaffUnchangedAsync(saved, 1);
    }

    [TestMethod]
    public async Task StaffUnifiedSaveCannotDemoteTheLastUsableSuperAdministrator()
    {
        using var client = factory!.CreateClient();
        await client.GetAsync("/api/asap/staff/session");
        var actor = await ReadConfiguredSuperAdminAsync();
        long[] otherActiveAdmins;
        await using (var context = await UnifiedStaffContextAsync())
        {
            otherActiveAdmins = await context.StaffUsers.Where(item => item.Id != actor.Id && item.IsActive && item.Role == "super_admin")
                .Select(item => item.Id).ToArrayAsync();
            await context.StaffUsers.Where(item => otherActiveAdmins.Contains(item.Id)).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        }
        try
        {
            var before = await ReadUnifiedStaffAsync(actor.Id);
            var auditCount = (await UnifiedStaffAuditsAsync(actor.Id)).Length;
            var result = await factory.Services.GetRequiredService<StaffLifecycleService>().UpdateAsync(actor, actor.Id,
                new StaffUpdateInput(StaffVersion.Encode(before.RowVersion), "must-not-change@example.org", "Must not change", "staff", 2), CancellationToken.None);
            Assert.AreEqual("active_super_admin_required", result.Code);
            await AssertUnifiedStaffUnchangedAsync(before, auditCount);
        }
        finally
        {
            await using var context = await UnifiedStaffContextAsync();
            await context.StaffUsers.Where(item => otherActiveAdmins.Contains(item.Id)).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, true));
        }
    }

    [TestMethod]
    public async Task StaffReactivationKeepsAcceptedProfileWhileExplicitCreateSynchronizesPrimaryEmail()
    {
        using var client = factory!.CreateClient();
        await client.GetAsync("/api/asap/staff/session");
        var seed = await SeedStaffMetadataRaceAsync("roster-reactivate");
        var actor = await ReadStaffMetadataActorAsync(seed);
        var service = factory.Services.GetRequiredService<StaffLifecycleService>();
        await using (var context = await UnifiedStaffContextAsync())
        {
            var target = await context.StaffUsers.SingleAsync(item => item.Id == seed.TargetId);
            target.IsActive = false;
            target.NotificationEmail = "accepted-primary@example.org";
            target.WeeklyActionSummaryEmail = "weekly-override@example.org";
            await context.SaveChangesAsync();
        }
        var before = await ReadUnifiedStaffAsync(seed.TargetId);
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var oversizedEmail = new string('a', 330) + "@example.org";
        Assert.AreEqual(342, oversizedEmail.Length);
        int staffCountBefore;
        int auditCountBefore;
        await using (var context = await UnifiedStaffContextAsync())
        {
            staffCountBefore = await context.StaffUsers.CountAsync();
            auditCountBefore = await context.AdministrativeAudits.CountAsync();
        }
        Assert.AreEqual("invalid_staff_user", (await service.CreateAsync(actor,
            new StaffCreateInput(oversizedEmail, "staff", 2), CancellationToken.None)).Code);
        using (var oversizedCreate = await client.PostAsJsonAsync("/api/asap/staff/users", new { email = oversizedEmail, role = "staff", organizationId = 2 }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, oversizedCreate.StatusCode, await oversizedCreate.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await oversizedCreate.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid_staff_user", body.RootElement.GetProperty("code").GetString());
        }
        await using (var context = await UnifiedStaffContextAsync())
        {
            Assert.AreEqual(staffCountBefore, await context.StaffUsers.CountAsync(), "Rejected Create must not leave a staff record.");
            Assert.AreEqual(auditCountBefore, await context.AdministrativeAudits.CountAsync(), "Rejected Create must not append successful audit history.");
        }
        using var reactivated = await client.PostAsJsonAsync($"/api/asap/staff/users/{seed.TargetId}/reactivate", new { version = StaffVersion.Encode(before.RowVersion) });
        Assert.AreEqual(HttpStatusCode.OK, reactivated.StatusCode, await reactivated.Content.ReadAsStringAsync());
        var active = await ReadUnifiedStaffAsync(seed.TargetId);
        Assert.IsTrue(active.IsActive);
        CollectionAssert.AreEqual(new[] { "staff_reactivated" }, await UnifiedStaffAuditsAsync(seed.TargetId),
            "The versioned Reactivate action must record reactivation before any later explicit Create.");
        Assert.AreEqual(before.NotificationEmail, active.NotificationEmail);
        Assert.AreEqual(before.DisplayName, active.DisplayName);
        Assert.AreEqual(before.EntraObjectId, active.EntraObjectId);
        Assert.AreEqual(before.Role, active.Role);
        Assert.AreEqual(before.OrganizationId, active.OrganizationId);
        Assert.AreEqual("identity_already_exists", (await service.ReactivateAsync(actor, seed.TargetId,
            new StaffDeactivateInput(StaffVersion.Encode(active.RowVersion)), CancellationToken.None)).Code);
        using (var replay = await client.PostAsJsonAsync($"/api/asap/staff/users/{seed.TargetId}/reactivate",
                   new { version = StaffVersion.Encode(active.RowVersion) }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, replay.StatusCode, await replay.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.AreEqual("identity_already_exists", body.RootElement.GetProperty("code").GetString());
        }
        await AssertUnifiedStaffUnchangedAsync(active, 1);
        CollectionAssert.AreEqual(new[] { "staff_reactivated" }, await UnifiedStaffAuditsAsync(seed.TargetId),
            "Already-active service and HTTP replays must preserve the original reactivation history exactly.");
        Assert.AreEqual("stale_version", (await service.ReactivateAsync(actor, seed.TargetId,
            new StaffDeactivateInput(StaffVersion.Encode(before.RowVersion)), CancellationToken.None)).Code);
        Assert.AreEqual("updated", (await service.DeactivateAsync(actor, seed.TargetId,
            new StaffDeactivateInput(StaffVersion.Encode(active.RowVersion)), CancellationToken.None)).Code);
        var recreated = await service.CreateAsync(actor, new StaffCreateInput(before.UserPrincipalName, before.Role, before.OrganizationId), CancellationToken.None);
        Assert.AreEqual("created", recreated.Code);
        Assert.AreEqual(seed.TargetId, recreated.User!.Id);
        Assert.AreEqual(before.UserPrincipalName, recreated.User.NotificationEmail);
        Assert.AreEqual("weekly-override@example.org", recreated.User.WeeklyActionSummaryEmail);
        Assert.AreEqual(before.DisplayName, recreated.User.DisplayName);
        var created = await service.CreateAsync(actor, new StaffCreateInput("roster-new@example.org", "staff", 2), CancellationToken.None);
        Assert.AreEqual("created", created.Code);
        Assert.AreEqual(created.User!.UserPrincipalName, created.User.NotificationEmail);
        Assert.IsNull(created.User.DisplayName, "Production Create does not fabricate a display name.");
        await using (var context = await UnifiedStaffContextAsync())
        {
            var invalid = await context.StaffUsers.SingleAsync(item => item.Id == created.User.Id);
            invalid.IsActive = false;
            invalid.NormalizedUserPrincipalName = null;
            await context.SaveChangesAsync();
        }
        var invalidIdentity = await ReadUnifiedStaffAsync(created.User.Id);
        Assert.AreEqual("invalid_staff_user", (await service.ReactivateAsync(actor, invalidIdentity.Id,
            new StaffDeactivateInput(StaffVersion.Encode(invalidIdentity.RowVersion)), CancellationToken.None)).Code);
        await AssertUnifiedStaffUnchangedAsync(invalidIdentity, 1);
    }

    private Task<AsapDbContext> UnifiedStaffContextAsync() =>
        factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();

    private async Task<StaffUser> ReadUnifiedStaffAsync(long id)
    {
        await using var context = await UnifiedStaffContextAsync();
        return await context.StaffUsers.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private async Task<string[]> UnifiedStaffAuditsAsync(long id)
    {
        await using var context = await UnifiedStaffContextAsync();
        return await context.AdministrativeAudits.Where(item => item.TargetType == "StaffUser" && item.TargetId == id.ToString())
            .OrderBy(item => item.Id).Select(item => item.Action).ToArrayAsync();
    }

    private async Task AssertUnifiedStaffUnchangedAsync(StaffUser before, int auditCount)
    {
        var after = await ReadUnifiedStaffAsync(before.Id);
        CollectionAssert.AreEqual(before.RowVersion, after.RowVersion, "A rejected draft must not partially advance the row.");
        Assert.AreEqual(before.UserPrincipalName, after.UserPrincipalName);
        Assert.AreEqual(before.NotificationEmail, after.NotificationEmail);
        Assert.AreEqual(before.DisplayName, after.DisplayName);
        Assert.AreEqual(before.Role, after.Role);
        Assert.AreEqual(before.OrganizationId, after.OrganizationId);
        Assert.AreEqual(before.EntraObjectId, after.EntraObjectId);
        Assert.AreEqual(before.WeeklyActionSummaryEmail, after.WeeklyActionSummaryEmail);
        Assert.AreEqual(auditCount, (await UnifiedStaffAuditsAsync(before.Id)).Length);
    }
}
