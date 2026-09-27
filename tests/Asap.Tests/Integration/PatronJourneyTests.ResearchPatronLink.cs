using System.Net;
using System.Text.Json;
using Asap.Web.Features.Patron;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task PolarisPatronIdReadUsesOnlyTheBasicPatronOperation()
    {
        const string barcode = "20000000000036";
        var handler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"PatronBasicData":{"PatronID":123,"Barcode":"20000000000036","PatronOrgID":300}}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var patronId = await provider.GetPatronIdAsync(barcode, CancellationToken.None);

        Assert.AreEqual<int?>(123, patronId);
        var patronReads = handler.RequestUris.Count(uri =>
            uri.AbsolutePath.Contains($"/patron/{barcode}", StringComparison.Ordinal));
        var staffAuthentications = handler.RequestUris.Count(uri =>
            uri.AbsolutePath.Contains("/authenticator/staff", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, patronReads, "Exactly one basic patron-data read should resolve the patron ID.");
        Assert.IsTrue(staffAuthentications <= 1, "At most one protected-token handshake should be needed.");
        Assert.AreEqual(patronReads + staffAuthentications, handler.RequestCount,
            "The patron-ID operation should not make unrelated Polaris calls.");
        Assert.IsFalse(handler.RequestUris.Any(uri =>
            uri.AbsolutePath.Contains("/organizations", StringComparison.OrdinalIgnoreCase)));
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
                         "javascript:open({{patronId}})"
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

        public Task<int?> GetPatronIdAsync(string barcode, CancellationToken cancellationToken)
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

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
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
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.DefinitiveNotFound));
    }
}
