using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PolarisSetupRemainsReachableWhileSparseReadinessRequiresLocalConfiguration()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var initial = await contexts.CreateDbContextAsync();
        var settings = await initial.PolarisSettings.SingleAsync();
        var original = initial.Entry(settings).CurrentValues.Clone();
        settings.Host = null;
        await initial.SaveChangesAsync();
        try
        {
            using var unready = await client.GetAsync("/health/ready");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unready.StatusCode);
            Assert.AreEqual("{\"status\":\"unhealthy\"}", await unready.Content.ReadAsStringAsync());
            using var current = await ReadSettingsDocumentAsync(client, "system");
            using var saved = await SaveSettingsDocumentAsync(client, current.RootElement, "system",
                new Dictionary<string, object?> { ["polaris"] = new {
                    host = "https://unreachable.polaris.invalid", accessId = "local-config-test",
                    apiKey = "test-api-key", staffDomain = "TEST", adminUser = "test-admin",
                    adminPassword = "test-password", workstationId = 99, systemPolarisUserId = 42 } });
            using var ready = await client.GetAsync("/health/ready");
            Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
            Assert.AreEqual("{\"status\":\"healthy\"}", await ready.Content.ReadAsStringAsync());
            using var reloaded = await ReadSettingsDocumentAsync(client, "system");
            var polaris = reloaded.RootElement.GetProperty("stored").GetProperty("polaris");
            Assert.AreEqual(JsonValueKind.Number, polaris.GetProperty("workstationId").ValueKind);
            Assert.IsFalse(polaris.TryGetProperty("organizationIdForRequests", out _));
            Assert.IsFalse(polaris.TryGetProperty("pickupOrganizationId", out _));
        }
        finally
        {
            await using var restore = await contexts.CreateDbContextAsync();
            var row = await restore.PolarisSettings.SingleAsync();
            restore.Entry(row).CurrentValues.SetValues(original);
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task PolarisSettingsRejectInvalidIdentitiesRetiredContextAndLibraryWrites()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        using var system = await ReadSettingsDocumentAsync(client, "system");
        var cases = new (string Scope, string Field, object Value, string Code)[] {
            ("system", "workstationId", 0, "polaris_identity_invalid"),
            ("system", "workstationId", -1, "polaris_identity_invalid"),
            ("system", "workstationId", "2147483648", "polaris_identity_invalid"),
            ("system", "systemPolarisUserId", "bad", "polaris_identity_invalid"),
            ("system", "systemPolarisUserId", 0, "polaris_identity_invalid"),
            ("system", "host", "file:///bad", "polaris_host_invalid"),
            ("system", "host", 1234, "polaris_host_invalid"),
            ("system", "organizationIdForRequests", 3, "polaris_context_retired"),
            ("system", "requestingOrgId", 3, "polaris_context_retired"),
            ("system", "pickupOrganizationId", 0, "polaris_context_retired"),
            ("system", "pickupOrgId", 0, "polaris_context_retired"),
            ("2", "workstationId", 99, "polaris_settings_system_only") };
        foreach (var item in cases)
        {
            using var response = await client.PostAsJsonAsync("/api/asap/staff/settings", new {
                orgId = item.Scope, version = system.RootElement.GetProperty("version").GetString(),
                polaris = new Dictionary<string, object?> { [item.Field] = item.Value } });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(item.Code, body.RootElement.GetProperty("code").GetString());
        }
        using var after = await ReadSettingsDocumentAsync(client, "system");
        Assert.AreEqual(system.RootElement.GetProperty("version").GetString(),
            after.RootElement.GetProperty("version").GetString(), "Rejected input must not persist settings.");
    }
}
