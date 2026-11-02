using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

// Protocol assertions use the real SQL credential boundary, without a web host,
// Hangfire, certificate-store writes, global workflow cleanup or mutable app DI.
// Serial because AccessId is a shared SQL setting; each provider owns its transport.
[TestClass]
[DoNotParallelize]
public sealed partial class PolarisProtocolTests : PolarisProtocolTestSupport
{
    private static string master = null!;
    private static string database = null!;
    private static string target = null!;
    private static ServiceProvider services = null!;
    private static IntegrationCredentialProtector protector = null!;
    private readonly List<HttpClient> clients = [];
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task Initialize(TestContext _)
    {
        using var timing = FixtureTiming.Start(nameof(PolarisProtocolTests), "shared_initialization");
        master = Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True";
        database = $"AsapProtocol_{Guid.NewGuid():N}";
        target = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
        using var package = DacPackage.Load(TestArtifactPaths.FindDacpac());
        new DacServices(master).Deploy(package, database, true, new DacDeployOptions
        {
            BlockOnPossibleDataLoss = true, CreateNewDatabase = true
        });
        protector = new IntegrationCredentialProtector(new EphemeralDataProtectionProvider());
        services = new ServiceCollection()
            .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target))
            .BuildServiceProvider();
        await using var context = await services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var settings = await context.PolarisSettings.SingleAsync();
        settings.Host = "https://polaris.invalid";
        settings.AccessId = "test-access";
        settings.ProtectedApiKey = protector.Protect("test-api-key");
        settings.StaffDomain = "TEST";
        settings.AdminUser = "test-admin";
        settings.ProtectedAdminPassword = protector.Protect("test-password");
        settings.WorkstationId = 99;
        settings.SystemPolarisUserId = 42;
        await context.SaveChangesAsync();
        timing?.Complete();
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (services is not null)
        {
            await services.DisposeAsync();
        }
        if (database?.StartsWith("AsapProtocol_", StringComparison.Ordinal) == true)
        {
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var quoted = new SqlCommandBuilder().QuoteIdentifier(database);
            command.CommandText = $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};";
            await command.ExecuteNonQueryAsync();
        }
    }

    [TestCleanup]
    public void DisposeTransports()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    private async Task<PolarisPatronProvider> CreatePolarisProviderAsync(
        HttpMessageHandler handler, string accessId = "test-access",
        ILogger<PolarisPatronProvider>? cleanupLogger = null)
    {
        using var timing = FixtureTiming.Start(nameof(PolarisProtocolTests), "provider_fixture", TestContext.TestName);
        await using var connection = new SqlConnection(target);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE [asap].[PolarisSettings] SET [AccessId] = @id WHERE [OrganizationId] = 1 AND [AccessId] <> @id;";
        command.Parameters.AddWithValue("@id", accessId);
        await command.ExecuteNonQueryAsync();
        var client = new HttpClient(handler);
        clients.Add(client);
        var result = new PolarisPatronProvider(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(), protector,
            new SingleClientFactory(client), TimeProvider.System,
            cleanupLogger ?? NullLogger<PolarisPatronProvider>.Instance);
        timing?.Complete();
        return result;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
