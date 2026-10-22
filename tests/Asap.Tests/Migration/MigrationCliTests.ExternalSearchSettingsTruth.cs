using Asap.Migration;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportAndReconcilePreservePinnedExternalSearchScopeSemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-external-search-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationExternalSearch_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);

        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [workflow_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [externalSearch1Enabled] INTEGER,
                    [externalSearch1Label] TEXT,
                    [externalSearch1UrlTemplate] TEXT,
                    [externalSearch2Enabled] INTEGER,
                    [externalSearch2Label] TEXT,
                    [externalSearch2UrlTemplate] TEXT,
                    [externalSearch3Enabled] INTEGER,
                    [externalSearch3Label] TEXT,
                    [externalSearch3UrlTemplate] TEXT
                );
                INSERT INTO [workflow_settings] VALUES
                    ('workflow-system', 'system', NULL, 1, '', '', 1, '', '', 1, '', ''),
                    ('workflow-library', 'library', 'pb-org-2',
                     0, '  Local Catalog  ', '  https://catalog.example/search?q={{title}}  ',
                     NULL, char(65279), NULL,
                     NULL, NULL, char(133));
                """);
            var report = Path.Combine(root, "report.json");

            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validationError),
                    validationError.ToString());
            }

            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, report, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            var expectedSystemProviders = new[]
            {
                (Key: "external_search_1", Enabled: true, Label: "Search Amazon", Url: "https://www.amazon.com/s?k={{title}}", SortOrder: 10),
                (Key: "external_search_2", Enabled: true, Label: "Search Goodreads", Url: "https://www.goodreads.com/search?q={{title}}", SortOrder: 20),
                (Key: "external_search_3", Enabled: true, Label: "Search WorldCat", Url: "https://www.worldcat.org/search?q={{title}}", SortOrder: 30),
                (Key: "external_search_4", Enabled: false, Label: "", Url: "", SortOrder: 40)
            };
            var paddedLabel = "  Local Catalog  ";
            var paddedUrl = "  https://catalog.example/search?q={{title}}  ";
            const string nelOnly = "\u0085";

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                var systemProviders = new List<(int OrganizationId, string Key, bool Enabled, string Label, string Url, int SortOrder)>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT [OrganizationId], [ProviderKey], [IsEnabled], [Label], [UrlTemplate], [SortOrder] FROM [asap].[ExternalSearchProvider] ORDER BY [ProviderKey];";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        systemProviders.Add((
                            reader.GetInt32(0),
                            reader.GetString(1),
                            reader.GetBoolean(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetInt32(5)));
                    }
                }

                Assert.AreEqual(4, systemProviders.Count, "The target system seed contains four fixed provider identities.");
                for (var index = 0; index < expectedSystemProviders.Length; index++)
                {
                    var expected = expectedSystemProviders[index];
                    Assert.AreEqual(
                        (1, expected.Key, expected.Enabled, expected.Label, expected.Url, expected.SortOrder),
                        systemProviders[index],
                        $"System provider {expected.Key} must retain its pinned identity, defaults, and order.");
                }

                var libraryOverrides = new List<(string Key, bool? Enabled, string? Label, string? Url)>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT p.[ProviderKey], o.[IsEnabled], o.[Label], o.[UrlTemplate]
                        FROM [asap].[ExternalSearchProviderOverride] o
                        JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId]
                        WHERE o.[LibraryOrganizationId] = 2
                        ORDER BY p.[ProviderKey];
                        """;
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        libraryOverrides.Add((
                            reader.GetString(0),
                            reader.IsDBNull(1) ? null : reader.GetBoolean(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3)));
                    }
                }

                Assert.AreEqual(2, libraryOverrides.Count, "Only providers with source-meaningful library fields have sparse override rows.");
                Assert.AreEqual(("external_search_1", (bool?)false, paddedLabel, paddedUrl), libraryOverrides[0]);
                Assert.AreEqual(("external_search_3", (bool?)null, (string?)null, nelOnly), libraryOverrides[1]);

                using var services = new ServiceCollection()
                    .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target))
                    .BuildServiceProvider();
                var configuration = new PatronConfigurationService(
                    services.GetRequiredService<IDbContextFactory<AsapDbContext>>());
                var effective = await configuration.GetAsync(2, CancellationToken.None);
                Assert.IsNotNull(effective);
                var providersByKey = effective.ExternalSearchProviders.ToDictionary(item => item.Key, StringComparer.Ordinal);
                Assert.AreEqual(false, providersByKey["external_search_1"].IsEnabled);
                Assert.AreEqual(paddedLabel, providersByKey["external_search_1"].Label);
                Assert.AreEqual(paddedUrl, providersByKey["external_search_1"].UrlTemplate);
                Assert.AreEqual("Search Goodreads", providersByKey["external_search_2"].Label,
                    "A FEFF-only library value is absent under the pinned JavaScript trim semantics and inherits the system value.");
                Assert.AreEqual("https://www.goodreads.com/search?q={{title}}", providersByKey["external_search_2"].UrlTemplate);
                Assert.AreEqual("Search WorldCat", providersByKey["external_search_3"].Label);
                Assert.AreEqual(nelOnly, providersByKey["external_search_3"].UrlTemplate,
                    "NEL is not removed by the pinned JavaScript trim and remains a raw library override.");
                Assert.AreEqual(4, providersByKey.Count);

                using (var reconcileError = new StringWriter())
                {
                    Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
                }

                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection,
                    target,
                    package,
                    report,
                    connectionEnvironmentName,
                    "external-search system provider label",
                    "UPDATE [asap].[ExternalSearchProvider] SET [Label] = N'Search Amazon drift' WHERE [ProviderKey] = N'external_search_1';",
                    "SELECT COUNT(*) FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = N'external_search_1' AND [Label] = N'Search Amazon drift';",
                    1,
                    "UPDATE [asap].[ExternalSearchProvider] SET [Label] = N'Search Amazon' WHERE [ProviderKey] = N'external_search_1';",
                    "SELECT COUNT(*) FROM [asap].[ExternalSearchProvider] WHERE [ProviderKey] = N'external_search_1' AND [Label] = N'Search Amazon';",
                    1);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            await DropDatabaseAsync(master, databaseName);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
