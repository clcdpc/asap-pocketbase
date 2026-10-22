using Asap.Migration;
using Asap.Tests;
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
    public async Task SystemWorkflowAndPageTitlePreservePinnedEcmaScriptTruthiness()
    {
        const string nel = "\u0085";
        const string systemAuthorsLabel = "System authors-label sentinel";
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-system-whitespace-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationSystemWhitespace_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnection = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var targetDeployed = false;

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
                    [suggestionLimitMessage] TEXT,
                    [commonAuthorsHelp] TEXT,
                    [commonAuthorsLabel] TEXT
                );
                INSERT INTO [workflow_settings] VALUES
                    ('workflow-system', 'system', NULL, char(133), ' ', 'System authors-label sentinel'),
                    ('workflow-library', 'library', 'pb-org-2', NULL, NULL, ' ');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [pageTitle] TEXT
                );
                INSERT INTO [ui_settings] VALUES ('ui-system', 'system', NULL, char(133));
                """);
            var report = Path.Combine(root, "report.json");
            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validationError),
                    validationError.ToString());
            }

            targetDeployed = true;
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    RunImport(package, report, connectionEnvironmentName, tenantId, importError),
                    importError.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT [OrganizationId], [SuggestionLimitMessage], [CommonAuthorsHelp], [CommonAuthorsLabel] " +
                        "FROM [asap].[WorkflowSettings] WHERE [OrganizationId] IN (1, 2) ORDER BY [OrganizationId];";
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual(1, reader.GetInt32(0));
                    Assert.AreEqual(nel, reader.GetString(1), "System NEL text must remain raw in SQL.");
                    Assert.AreEqual(" ", reader.GetString(2), "System ASCII-space text must remain raw in SQL.");
                    Assert.AreEqual(systemAuthorsLabel, reader.GetString(3));

                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual(2, reader.GetInt32(0));
                    Assert.IsTrue(reader.IsDBNull(1));
                    Assert.IsTrue(reader.IsDBNull(2));
                    Assert.IsTrue(reader.IsDBNull(3), "A Library ASCII-space workflow value is absent under pinned JavaScript trim.");
                    Assert.IsFalse(await reader.ReadAsync());
                }

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT [PageTitle] FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1;";
                    Assert.AreEqual(nel, (string?)await command.ExecuteScalarAsync(), "System pageTitle NEL text must remain raw in SQL.");
                }
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            using var services = new ServiceCollection()
                .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target))
                .BuildServiceProvider();
            var configuration = new PatronConfigurationService(
                services.GetRequiredService<IDbContextFactory<AsapDbContext>>());
            var system = await configuration.GetAsync(1, CancellationToken.None);
            Assert.IsNotNull(system);
            var library = await configuration.GetAsync(2, CancellationToken.None);
            Assert.IsNotNull(library);
            CollectionAssert.AreEqual(
                new[] { nel, " ", nel, nel, " ", systemAuthorsLabel, nel },
                new[]
                {
                    system.SuggestionLimitMessage,
                    system.CommonCreatorsHelp,
                    system.PageTitle,
                    library.SuggestionLimitMessage,
                    library.CommonCreatorsHelp,
                    library.CommonCreatorsLabel,
                    library.PageTitle
                },
                "The effective patron API must retain pinned JavaScript truthiness and Library-scope fallback semantics.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnection);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("System-whitespace cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
