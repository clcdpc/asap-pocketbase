using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task TitleQueueSharesOrderedResearchProvidersWithDetailsForEachAuthorizedLibrary()
    {
        await using var fixture = await SeedPolarisQueueAsync();
        using var admin = await PolarisQueueClientAsync(await ReadConfiguredSuperAdminAsync());
        using var staff = await PolarisQueueClientAsync(fixture.Actor);
        const int otherLibrary = 55102;
        const string queue = "/api/asap/staff/title-requests?scope=all";
        var titleSource = new StaffSearchProvider("i551-title", "Title source", "https://example.test/?q={{title}}", 50);
        var idSource = new StaffSearchProvider("i551-id", "ID source", "https://example.test/?id={{identifier}}", 50);
        var localSource = new StaffSearchProvider("i551-local", "Local", "https://local.example.test/{{identifier}}", 40);
        try
        {
            await ExecuteNonQueryAsync("""
                INSERT INTO asap.Organization (Id, DisplayName, OrganizationCodeId, ParentOrganizationId, IsActive)
                VALUES (@library, N'Research library', 2, 1, 1);
                DECLARE @now datetime2 = SYSUTCDATETIME();
                INSERT INTO asap.TitleRequest (LibraryOrganizationId, PatronOrganizationId, Barcode, Title, MaterialFormatId,
                    Status, AutoHold, CreatedUtc, UpdatedUtc)
                VALUES (@library, 2, 'research-a', 'Library title A', @format, 'suggestion', 0, @now, @now),
                       (@library, 2, 'research-b', 'Library title B', @format, 'pending_hold', 0, @now, @now);
                INSERT INTO asap.ExternalSearchProvider (OrganizationId, ProviderKey, IsEnabled, Label, UrlTemplate, SortOrder)
                VALUES (1,'i551-title',1,'Title source','https://example.test/?q={{title}}',50),
                       (1,'i551-id',1,'ID source','https://example.test/?id={{identifier}}',50),
                       (1,'i551-local',0,'Local','https://example.test/',40);
                INSERT INTO asap.ExternalSearchProviderOverride (LibraryOrganizationId, ExternalSearchProviderId, IsEnabled, Label, UrlTemplate)
                SELECT @library, Id,
                    CASE ProviderKey WHEN 'i551-id' THEN 0 WHEN 'i551-local' THEN 1 END,
                    CASE ProviderKey WHEN 'i551-title' THEN 'Library title search' END,
                    CASE ProviderKey WHEN 'i551-local' THEN 'https://local.example.test/{{identifier}}' END
                FROM asap.ExternalSearchProvider WHERE ProviderKey LIKE 'i551-%';
                """, ("@library", otherLibrary), ("@format", fixture.Source.MaterialFormatId));

            var all = (await admin.GetFromJsonAsync<JsonElement>(queue)).GetProperty("items").EnumerateArray().ToArray();
            var localRows = all.Where(row => row.GetProperty("libraryOrgId").GetInt32() == otherLibrary).ToArray();
            Assert.AreEqual(2, localRows.Length);
            var inherited = all.Single(row => row.GetProperty("id").GetString() == fixture.Source.Id.ToString());
            foreach (var row in localRows.Append(inherited))
            {
                var configuration = await admin.GetFromJsonAsync<JsonElement>(
                    $"/api/asap/staff/research-configuration?requestId={row.GetProperty("id").GetString()}");
                var providers = row.GetProperty("research").GetProperty("externalSearchProviders");
                Assert.IsTrue(JsonElement.DeepEquals(configuration.GetProperty("externalSearchProviders"), providers));
                var actual = providers.Deserialize<StaffSearchProvider[]>(JsonSerializerOptions.Web)!
                    .Where(item => item.Key.StartsWith("i551-", StringComparison.Ordinal)).ToArray();
                CollectionAssert.AreEqual(row.GetProperty("libraryOrgId").GetInt32() == otherLibrary
                    ? new[] { localSource, titleSource with { Label = "Library title search" } }
                    : new[] { titleSource, idSource }, actual);
            }
            var restricted = await staff.GetFromJsonAsync<JsonElement>(queue);
            Assert.IsTrue(restricted.GetProperty("items").EnumerateArray().All(row => row.GetProperty("libraryOrgId").GetInt32() == 2));
            using var forbidden = await staff.GetAsync(
                $"/api/asap/staff/research-configuration?requestId={localRows[0].GetProperty("id").GetString()}");
            Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode);

            await ExecuteNonQueryAsync("""
                UPDATE asap.ExternalSearchProviderOverride SET Label = 'Updated library source'
                WHERE LibraryOrganizationId = @library AND Label = 'Library title search';
                """, ("@library", otherLibrary));
            var refresh = await admin.GetFromJsonAsync<JsonElement>($"/api/asap/staff/title-requests?scope={otherLibrary}");
            var refreshed = refresh.GetProperty("items").EnumerateArray().ToArray();
            Assert.AreEqual(2, refreshed.Length);
            foreach (var row in refreshed)
            {
                Assert.IsTrue(row.GetProperty("research").GetProperty("externalSearchProviders").EnumerateArray()
                    .Any(p => p.GetProperty("label").GetString() == "Updated library source"));
            }
        }
        finally
        {
            await ExecuteNonQueryAsync("""
                DELETE FROM asap.ExternalSearchProviderOverride WHERE LibraryOrganizationId = @library;
                DELETE FROM asap.ExternalSearchProvider WHERE ProviderKey LIKE 'i551-%';
                DELETE FROM asap.TitleRequest WHERE LibraryOrganizationId = @library;
                DELETE FROM asap.Organization WHERE Id = @library;
                """, ("@library", otherLibrary));
        }
    }

    [TestMethod]
    public async Task ResearchConfigurationReadsPatronIdOnlyForRenderablePatronLinkAndDegradesOnFailure()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedStaffBibLookupStateAsync(Guid.Parse(identity.TenantId!));
        var originalPattern = await ReadResearchPatronPatternAsync();
        var provider = new ResearchPatronIdProvider(7001);
        await using var researchFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(provider);
            }));
        using var client = researchFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", seeded.Email);

        try
        {
            using var session = await client.GetAsync("/api/asap/staff/session");
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode, await session.Content.ReadAsStringAsync());

            foreach (var pattern in new string?[]
                     {
                         null,
                         "https://leap.example.test/patron/{{id}}",
                         "https://leap.example.test/patron/{{patron-id}}/{{author}}",
                         "javascript:open({{patronId}})",
                         "\u0085javascript:alert({{patron-id}})"
                     })
            {
                await SetResearchPatronPatternAsync(pattern);
                using var response = await client.GetAsync(
                    $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("patronId").ValueKind);
            }
            Assert.AreEqual(0, provider.PatronIdReadCount);
            Assert.AreEqual(0, provider.RefreshCount);

            await SetResearchPatronPatternAsync("https://leap.example.test/patron/{{patron-id}}");
            using var queue = await client.GetAsync("/api/asap/staff/title-requests?scope=all");
            Assert.AreEqual(HttpStatusCode.OK, queue.StatusCode);
            Assert.AreEqual(0, provider.PatronIdReadCount);
            using (var response = await client.GetAsync(
                       $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(7001, body.RootElement.GetProperty("patronId").GetInt32());
                Assert.IsTrue(body.RootElement.TryGetProperty("externalSearchProviders", out _));
            }
            Assert.AreEqual(1, provider.PatronIdReadCount);
            Assert.AreEqual(0, provider.RefreshCount);

            provider.FailPatronIdRead = true;
            using (var response = await client.GetAsync(
                       $"/api/asap/staff/research-configuration?requestId={seeded.RequestId}"))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("patronId").ValueKind);
                Assert.IsTrue(body.RootElement.TryGetProperty("leapPatronUrlPattern", out _));
                Assert.IsTrue(body.RootElement.TryGetProperty("externalSearchProviders", out _));
            }
            Assert.AreEqual(2, provider.PatronIdReadCount);
            Assert.AreEqual(0, provider.RefreshCount);
        }
        finally
        {
            await SetResearchPatronPatternAsync(originalPattern);
        }
    }

    private static async Task<string?> ReadResearchPatronPatternAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [LeapPatronUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;",
            connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : (string?)value;
    }

    private static async Task SetResearchPatronPatternAsync(string? pattern)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [asap].[SystemSettings] SET [LeapPatronUrlPattern] = @pattern WHERE [OrganizationId] = 1;",
            connection);
        command.Parameters.AddWithValue("@pattern", (object?)pattern ?? DBNull.Value);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class ResearchPatronIdProvider(int patronId) : IPatronProvider
    {
        public int PatronIdReadCount { get; private set; }
        public int RefreshCount { get; private set; }
        public bool FailPatronIdRead { get; set; }

        public Task<int?> GetPatronIdAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PatronIdReadCount++;
            if (FailPatronIdRead)
            {
                throw new PolarisOperationalException("testing_patron_id_failure", "Patron lookup failed.");
            }
            return Task.FromResult<int?>(patronId);
        }

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            return Task.FromResult(new PatronSnapshot(
                patronId,
                barcode,
                null,
                null,
                null,
                null,
                null,
                2,
                2,
                "Test Library",
                null));
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound));
    }
}
