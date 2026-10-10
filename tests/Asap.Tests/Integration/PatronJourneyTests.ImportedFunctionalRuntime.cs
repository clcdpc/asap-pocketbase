using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    // Called only after the imported target has reconciled, and before the historical editor/reset journey.
    // Runtime mutations intentionally end the pre-activation reconciliation lifecycle.
    private async Task VerifyImportedFunctionalRuntimeAsync(WebApplicationFactory<Program> importedFactory,
        IDbContextFactory<AsapDbContext> contexts, string target, long actorId, Guid tenantId,
        string actorEmail, string fieldKey, string retiredSelectKey, long localFormatId, long autoClaimRuleId)
    {
        using var admin = importedFactory.CreateClient();
        AddTestingStaffHeaders(admin, actorId, tenantId, actorEmail);
        admin.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(admin));
        using var settingsResponse = await admin.GetAsync("/api/asap/staff/settings?orgId=2");
        Assert.AreEqual(HttpStatusCode.OK, settingsResponse.StatusCode);
        using var settings = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
        var workflow = settings.RootElement.GetProperty("effective").GetProperty("workflow");
        Assert.AreEqual(5, workflow.GetProperty("suggestionLimit").GetInt32(),
            "An imported library zero owns the legacy default, rather than inheriting system 9.");
        foreach (var (key, value) in new[] { ("outstandingTimeoutDays", 30), ("holdPickupTimeoutDays", 14),
                     ("pendingHoldTimeoutDays", 14), ("additionalCopyTimeoutDays", 14) })
        {
            Assert.AreEqual(value, workflow.GetProperty(key).GetInt32(), key);
        }
        Assert.IsTrue(workflow.GetProperty("allowPatronAutoholdOptOut").GetBoolean());
        using var publicConfigResponse = await admin.GetAsync("/api/asap/config?libraryOrgId=2");
        Assert.AreEqual(HttpStatusCode.OK, publicConfigResponse.StatusCode);
        using var config = JsonDocument.Parse(await publicConfigResponse.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "Coming soon", "Imported local publication" },
            config.RootElement.GetProperty("publicationOptions").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.IsTrue(config.RootElement.GetProperty("availableFormats").EnumerateArray()
            .Any(item => item.GetString() == "imported_local"));
        Assert.AreEqual("Imported local format", config.RootElement.GetProperty("formatLabels")
            .GetProperty("imported_local").GetString());
        Assert.AreEqual("required", config.RootElement.GetProperty("formatRules").GetProperty("imported_local")
            .GetProperty("customFields").GetProperty(fieldKey).GetProperty("mode").GetString());
        var select = config.RootElement.GetProperty("additionalFieldDefinitions").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == retiredSelectKey);
        Assert.IsFalse(select.GetProperty("options").EnumerateArray().Any(item =>
            item.GetProperty("id").GetString() == "retired-choice" && item.GetProperty("enabled").GetBoolean()));
        using var branch = await admin.GetAsync("/api/asap/config?libraryOrgId=20");
        Assert.AreEqual(HttpStatusCode.NotFound, branch.StatusCode);

        await using var sql = await contexts.CreateDbContextAsync();
        var importedWorkflow = await sql.WorkflowSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == 2);
        Assert.AreEqual(5, importedWorkflow.SuggestionLimit);
        Assert.AreEqual(9, (await sql.WorkflowSettings.AsNoTracking().SingleAsync(item => item.OrganizationId == 1)).SuggestionLimit);
        var claimant = await sql.StaffUsers.AsNoTracking().SingleAsync(item => item.UserPrincipalName == "imported-selector@example.org");
        using var staff = importedFactory.CreateClient();
        AddTestingStaffHeaders(staff, claimant.Id, tenantId, claimant.UserPrincipalName!);
        using var staffSession = await staff.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, staffSession.StatusCode);
        using var staffSessionBody = JsonDocument.Parse(await staffSession.Content.ReadAsStringAsync());
        Assert.IsTrue(staffSessionBody.RootElement.GetProperty("accessAllowed").GetBoolean());
        Assert.AreEqual("staff", staffSessionBody.RootElement.GetProperty("staff").GetProperty("role").GetString());
        Assert.AreEqual(2, staffSessionBody.RootElement.GetProperty("staff").GetProperty("organizationId").GetInt32());
        using var forbiddenSettings = await staff.GetAsync("/api/asap/staff/settings?orgId=system");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbiddenSettings.StatusCode);

        var heldId = await sql.LegacyPocketBaseMappings.AsNoTracking().Where(item =>
            item.EntityType == "title_request" && item.PocketBaseId == "imported-held-history")
            .Select(item => item.NewId).SingleAsync();
        var held = await sql.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == heldId);
        Assert.IsTrue(held.LegacyHoldProtected);
        Assert.IsNull(held.PatronIdSnapshot, "Migration cannot invent a missing historical native identity.");
        var before = await ReadTitleRequestSnapshotAsync(held.Id, target);
        var protectedOutboxes = await sql.EmailOutbox.CountAsync();
        var protectedHoldOperations = await sql.HoldPlacementOperations.CountAsync();
        var protectedPickupOperations = await sql.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM [asap].[PickupPreferenceOperation]").SingleAsync();
        var provider = importedFactory.Services.GetRequiredService<DeterministicTestingPatronProvider>();
        var providerCallsBefore = provider.Calls.Count;
        using var heldViewResponse = await admin.GetAsync("/api/asap/staff/title-requests/imported-held-history");
        Assert.AreEqual(HttpStatusCode.OK, heldViewResponse.StatusCode);
        using var heldView = JsonDocument.Parse(await heldViewResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(held.Id.ToString(), heldView.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("hold_history_retained", heldView.RootElement.GetProperty("capabilities").GetProperty("blockingReason").GetString());
        using var reopen = await admin.PostAsJsonAsync($"/api/asap/staff/title-requests/{held.Id}/action",
            new { version = before.Version, action = "reopen" });
        Assert.AreEqual(HttpStatusCode.Conflict, reopen.StatusCode);
        using var reopenBody = JsonDocument.Parse(await reopen.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_history_retained", reopenBody.RootElement.GetProperty("code").GetString());
        await AssertTitleRequestUnchangedAsync(held.Id, before, target);
        Assert.AreEqual(providerCallsBefore, provider.Calls.Count);
        Assert.AreEqual(protectedOutboxes, await sql.EmailOutbox.CountAsync());
        Assert.AreEqual(protectedHoldOperations, await sql.HoldPlacementOperations.CountAsync());
        Assert.AreEqual(protectedPickupOperations, await sql.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM [asap].[PickupPreferenceOperation]").SingleAsync());

        var copyId = await sql.LegacyPocketBaseMappings.AsNoTracking().Where(item =>
            item.EntityType == "additional_copy" && item.PocketBaseId == "imported-copy")
            .Select(item => item.NewId).SingleAsync();
        using var copyResponse = await staff.GetAsync("/api/asap/staff/additional-copies/imported-copy");
        Assert.AreEqual(HttpStatusCode.OK, copyResponse.StatusCode);
        using var copy = JsonDocument.Parse(await copyResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(copyId.ToString(), copy.RootElement.GetProperty("id").GetString());
        Assert.AreEqual("additional_copy", copy.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(held.Id.ToString(), copy.RootElement.GetProperty("sourceTitleRequest").GetString());
        Assert.AreEqual("closed", copy.RootElement.GetProperty("sourceStatus").GetString());
        Assert.AreEqual(2, copy.RootElement.GetProperty("libraryOrgId").GetInt32());
        Assert.AreEqual("Frozen imported library", copy.RootElement.GetProperty("libraryOrgName").GetString());
        Assert.AreEqual(9001, copy.RootElement.GetProperty("bibid").GetInt64());
        Assert.AreEqual("Frozen imported copy", copy.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("book", copy.RootElement.GetProperty("format").GetString());
        Assert.AreEqual("Book", copy.RootElement.GetProperty("formatLabel").GetString());
        Assert.AreEqual("open", copy.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(14, copy.RootElement.GetProperty("timeoutContext").GetProperty("days").GetInt32());
        Assert.IsTrue(copy.RootElement.GetProperty("capabilities").GetProperty("canClaim").GetBoolean());
        Assert.IsFalse(copy.RootElement.GetProperty("capabilities").GetProperty("canDelete").GetBoolean());

        // The provider's current branch must exist in this imported source catalog.
        provider.AddPatron(new PatronSnapshot(7801, "20000000000801", "imported-runtime@example.org",
            "Ada", "Reader", 1, "Adult", 20, 2, "Test Library", 20), [new(20, "Test Branch")], 2);
        using var patron = importedFactory.CreateClient();
        using var login = await patron.PostAsJsonAsync("/api/asap/patron/login",
            new { barcode = "20000000000801", pin = "1234", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        patron.DefaultRequestHeaders.Authorization = new("Bearer", loginBody.RootElement.GetProperty("token").GetString());
        var requestCount = await sql.TitleRequests.CountAsync();
        var eventCount = await sql.TitleRequestEvents.CountAsync();
        var outboxCount = await sql.EmailOutbox.CountAsync();
        var input = new
        {
            format = "imported_local", title = "Imported Runtime Submission", author = "Runtime Author",
            publication = "Imported local publication", preferredPickupBranchId = 20, autohold = false,
            customFields = new Dictionary<string, string>()
        };
        using var invalid = await patron.PostAsJsonAsync("/api/asap/patron/suggestions", input);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
        Assert.AreEqual(requestCount, await sql.TitleRequests.CountAsync());
        Assert.AreEqual(eventCount, await sql.TitleRequestEvents.CountAsync());
        Assert.AreEqual(outboxCount, await sql.EmailOutbox.CountAsync());
        input.customFields[fieldKey] = "Runtime imported note";
        using var accepted = await patron.PostAsJsonAsync("/api/asap/patron/suggestions", input);
        Assert.AreEqual(HttpStatusCode.Created, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        using var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var id = long.Parse(acceptedBody.RootElement.GetProperty("id").GetString()!);
        var persisted = await sql.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.AreEqual(2, persisted.LibraryOrganizationId);
        Assert.AreEqual(7801, persisted.PatronIdSnapshot);
        Assert.AreEqual(20, persisted.PreferredPickupBranchId);
        Assert.AreEqual(localFormatId, persisted.MaterialFormatId);
        Assert.AreEqual("Imported local publication", persisted.Publication);
        Assert.IsFalse(persisted.AutoHold);
        Assert.AreEqual("automatic_format_rule", persisted.ClaimType);
        Assert.AreEqual(autoClaimRuleId, persisted.ClaimRuleId);
        Assert.AreEqual(claimant.Id, persisted.ClaimedByStaffUserId);
        using var snapshot = JsonDocument.Parse(persisted.CustomFieldsJson!);
        var field = snapshot.RootElement.GetProperty(fieldKey);
        Assert.AreEqual("Imported clear note", field.GetProperty("label").GetString());
        Assert.AreEqual("text", field.GetProperty("type").GetString());
        Assert.AreEqual("Runtime imported note", field.GetProperty("value").GetString());
        Assert.AreEqual(requestCount + 1, await sql.TitleRequests.CountAsync());
        Assert.AreEqual(0, provider.Calls.Count(item =>
            item.Operation == TestingPolarisOperation.PickupUpdate),
            "Current pickup is already selected; the imported runtime must not dispatch a preference write.");
        Assert.AreEqual(0, provider.CreateCommands.Count);
        Assert.AreEqual(0, provider.ReplyCommands.Count);
        Assert.AreEqual(1, await sql.EmailOutbox.CountAsync(item =>
            item.BusinessKey == $"patron-submission:{id}"));
    }
}
