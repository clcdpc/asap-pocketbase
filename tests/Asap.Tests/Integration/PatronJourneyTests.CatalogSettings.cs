using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Features.Staff;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task CatalogSettingsPersistScopedOverridesPreviewAndResetWithoutCachingPresentation()
    {
        const int first = 383502;
        const int second = 383503;
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var system = await context.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
        var original = (system.PacBaseUrl, system.MaterialTypeIconUrlPattern, system.LeapBibUrlPattern, system.LeapPatronUrlPattern);
        var polaris = await context.PolarisSettings.SingleAsync(item => item.OrganizationId == 1);
        var originalPolaris = context.Entry(polaris).CurrentValues.Clone();
        context.Organizations.AddRange(new[] { first, second }.Select(id => new Organization
        {
            Id = id, DisplayName = $"Catalog settings {id}", OrganizationCodeId = 2, ParentOrganizationId = 1, IsActive = true
        }));
        await context.SaveChangesAsync();
        async Task SaveAsync(string scope, object catalog, object? legacy = null)
        {
            using var before = await ReadSettingsDocumentAsync(client, scope);
            var payload = new Dictionary<string, object?> { ["orgId"] = scope, ["version"] = before.RootElement.GetProperty("version").GetString(), ["catalog"] = catalog };
            if (legacy is not null)
            {
                payload["systemSettings"] = legacy;
            }
            using var saved = await client.PostAsJsonAsync("/api/asap/staff/settings", payload);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        }
        try
        {
            // The original editor/API name continues to write the same system default.
            await SaveAsync("system", new { pacBaseUrl = "https://system.example/polaris" }, new { formatIconUrlPattern = "/legacy/{id2}.gif" });
            var handler = new CatalogMaterialHandler();
            var provider = await CreatePolarisProviderAsync(handler);
            var inherited = await provider.SearchBibsAsync("title", "Title", "", "", first, CancellationToken.None);
            Assert.AreEqual("/legacy/53.gif", inherited.Results.First().FormatIconUrl);
            Assert.AreEqual("/legacy/53.gif", (await provider.ValidateBibAsync(9001, first, CancellationToken.None)).FormatIconUrl);
            const string pattern = "{pacBaseUrl}/custom/{pacOrgId}/format{id2}/{SearchCode}.gif";
            await SaveAsync(first.ToString(), new { pacBaseUrl = "https://alpha.example/polaris/", formatIconUrlPattern = pattern, pacOrganizationId = 27 });
            await SaveAsync(second.ToString(), new { pacBaseUrl = "https://beta.example/catalog", formatIconUrlPattern = pattern, pacOrganizationId = 41 });
            foreach (var (library, expected) in new[]
                {
                    (first, "https://alpha.example/polaris/custom/27/format53/lp.gif"),
                    (second, "https://beta.example/catalog/custom/41/format53/lp.gif")
                })
            {
                var search = await provider.SearchBibsAsync("title", "Title", "", "", library, CancellationToken.None);
                Assert.AreEqual(expected, search.Results.First().FormatIconUrl);
                Assert.AreEqual(expected, (await provider.ValidateBibAsync(9001, library, CancellationToken.None)).FormatIconUrl);
                using var preview = await client.PostAsJsonAsync("/api/asap/staff/settings/catalog-preview", new { orgId = library.ToString(), materialTypeId = 53, materialTypeSearchCode = "lp", catalog = new { } });
                Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode, await preview.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
                Assert.AreEqual(expected, body.RootElement.GetProperty("formatIconUrl").GetString());
            }
            Assert.AreEqual(2, handler.ReferenceCalls, "Only material definitions are cached per organization; presentation follows current settings.");
            using var staleSnapshot = await ReadSettingsDocumentAsync(client, first.ToString());
            await SaveAsync(first.ToString(), new { formatIconUrlPattern = "" });
            using (var stale = await client.PostAsJsonAsync("/api/asap/staff/settings", new
                { orgId = first.ToString(), version = staleSnapshot.RootElement.GetProperty("version").GetString(), catalog = new { pacBaseUrl = "https://stale.example" } }))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            }
            using (var blank = await ReadSettingsDocumentAsync(client, first.ToString()))
            {
                Assert.AreEqual("", blank.RootElement.GetProperty("stored").GetProperty("libraryOverride").GetProperty("catalog").GetProperty("formatIconUrlPattern").GetString());
                Assert.AreEqual("", blank.RootElement.GetProperty("catalog").GetProperty("formatIconUrlPattern").GetString());
            }
            Assert.IsNull((await provider.ValidateBibAsync(9001, first, CancellationToken.None)).FormatIconUrl);
            Assert.AreEqual("https://beta.example/catalog/custom/41/format53/lp.gif", (await provider.ValidateBibAsync(9001, second, CancellationToken.None)).FormatIconUrl);
            using (var beforeReset = await ReadSettingsDocumentAsync(client, first.ToString()))
            using (var reset = await client.PostAsJsonAsync($"/api/asap/staff/settings/reset?organizationId={first}", new { version = beforeReset.RootElement.GetProperty("version").GetString() }))
            {
                Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            }
            using var afterReset = await ReadSettingsDocumentAsync(client, first.ToString());
            var raw = afterReset.RootElement.GetProperty("stored").GetProperty("libraryOverride").GetProperty("catalog");
            Assert.AreEqual(JsonValueKind.Null, raw.GetProperty("pacBaseUrl").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, raw.GetProperty("formatIconUrlPattern").ValueKind);
            Assert.AreEqual(27, raw.GetProperty("pacOrganizationId").GetInt32(), "Reset preserves library-owned catalog identity.");
            Assert.AreEqual("/legacy/53.gif", (await provider.ValidateBibAsync(9001, first, CancellationToken.None)).FormatIconUrl);
            await SaveAsync(first.ToString(), new { pacBaseUrl = "", formatIconUrlPattern = "{pacBaseUrl}/icons/{id}.gif" });
            Assert.IsNull((await provider.ValidateBibAsync(9001, first, CancellationToken.None)).FormatIconUrl, "Blank base disables dependent patterns instead of inheriting.");
            await SaveAsync(first.ToString(), new { pacBaseUrl = (string?)null });
            Assert.AreEqual("https://system.example/polaris/icons/53.gif", (await provider.ValidateBibAsync(9001, first, CancellationToken.None)).FormatIconUrl);
            await context.Entry(system).ReloadAsync();
            Assert.AreEqual(original.LeapBibUrlPattern, system.LeapBibUrlPattern);
            Assert.AreEqual(original.LeapPatronUrlPattern, system.LeapPatronUrlPattern);
        }
        finally
        {
            await context.Entry(system).ReloadAsync();
            system.PacBaseUrl = original.PacBaseUrl;
            system.MaterialTypeIconUrlPattern = original.MaterialTypeIconUrlPattern;
            await context.Entry(polaris).ReloadAsync();
            context.Entry(polaris).CurrentValues.SetValues(originalPolaris);
            await context.SaveChangesAsync();
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] IN (@first, @second);
                DELETE FROM [asap].[LibraryCatalogSettings] WHERE [OrganizationId] IN (@first, @second);
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] IN (@first, @second);
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] IN (@first, @second);
                DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] IN (@first, @second);
                DELETE FROM [asap].[Organization] WHERE [Id] IN (@first, @second);
                """, ("@first", first), ("@second", second));
        }
    }

    [TestMethod]
    [DataRow("{\"materialTypeId\":\"1\"}")]
    [DataRow("{\"materialTypeId\":null}")]
    [DataRow("{\"materialTypeId\":{}}")]
    [DataRow("{\"materialTypeId\":0}")]
    [DataRow("{\"materialTypeSearchCode\":{}}")]
    [DataRow("{\"catalog\":{\"pacOrganizationId\":-1}}")]
    [DataRow("{\"catalog\":{\"pacBaseUrl\":\"javascript:alert(1)\"}}")]
    [DataRow("{\"catalog\":{\"formatIconUrlPattern\":\"https://icons.example/{unsupported}.gif\"}}")]
    public async Task CatalogPreviewRejectsInvalidDraftsWithoutSaving(string json)
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        using var before = await ReadSettingsDocumentAsync(client, "2");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        payload["orgId"] = "2";
        using var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var preview = await client.PostAsync("/api/asap/staff/settings/catalog-preview", content);
        Assert.AreEqual(HttpStatusCode.BadRequest, preview.StatusCode, await preview.Content.ReadAsStringAsync());
        if (payload.ContainsKey("catalog"))
        {
            payload["version"] = before.RootElement.GetProperty("version").GetString();
            using var save = await client.PostAsync("/api/asap/staff/settings",
                new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, save.StatusCode, await save.Content.ReadAsStringAsync());
        }
        using var after = await ReadSettingsDocumentAsync(client, "2");
        Assert.AreEqual(before.RootElement.GetProperty("version").GetString(), after.RootElement.GetProperty("version").GetString());
    }

    [TestMethod]
    public async Task CatalogSettingsAndPreviewRecheckLibraryAuthorityAndActiveIdentity()
    {
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var systemActor = await ReadConfiguredSuperAdminAsync();
        var email = $"catalog-admin-{Guid.NewGuid():N}@example.org";
        var admin = new StaffUser { UserPrincipalName = email, NormalizedUserPrincipalName = email.ToUpperInvariant(),
            EntraTenantId = systemActor.EntraTenantId, EntraObjectId = Guid.NewGuid(), Role = StaffRole.Admin, OrganizationId = 2, IsActive = true };
        context.StaffUsers.Add(admin);
        await context.SaveChangesAsync();
        try
        {
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, admin.Id, admin.EntraTenantId!.Value, email);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            foreach (var scope in new[] { "system", "3" })
            {
                using var preview = await client.PostAsJsonAsync("/api/asap/staff/settings/catalog-preview", new { orgId = scope, catalog = new { pacBaseUrl = "https://forbidden.example" } });
                Assert.AreEqual(HttpStatusCode.Forbidden, preview.StatusCode);
                using var save = await client.PostAsJsonAsync("/api/asap/staff/settings", new { orgId = scope, catalog = new { formatIconUrlPattern = "/icons/{id}.gif" } });
                Assert.AreEqual(HttpStatusCode.Forbidden, save.StatusCode);
            }
            using (var preview = await client.PostAsJsonAsync("/api/asap/staff/settings/catalog-preview", new { orgId = "2", catalog = new { formatIconUrlPattern = "/icons/{id}.gif" } }))
            {
                Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
            }
            admin.IsActive = false;
            await context.SaveChangesAsync();
            using var inactive = await client.PostAsJsonAsync("/api/asap/staff/settings/catalog-preview", new { orgId = "2", catalog = new { } });
            Assert.AreEqual(HttpStatusCode.Unauthorized, inactive.StatusCode);
        }
        finally
        {
            context.StaffUsers.Remove(admin);
            await context.SaveChangesAsync();
        }
    }
}
