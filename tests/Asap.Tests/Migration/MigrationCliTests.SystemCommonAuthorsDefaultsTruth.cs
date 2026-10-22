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
    public async Task PresentSystemCommonAuthorsNullAndEmptyUsePinnedDefaults()
    {
        const string commonAuthorsHelpDefault = "See if this is a creator we already collect.";
        const string commonAuthorsMessageDefault =
            "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.";
        var cases = new[]
        {
            (Name: "help-null-message-empty", HelpIsNull: true, MessageIsNull: false),
            (Name: "help-empty-message-null", HelpIsNull: false, MessageIsNull: true)
        };
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-common-authors-defaults-{Guid.NewGuid():N}");
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnection = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var deployedDatabaseNames = new List<string>();
        var effectiveValueFailures = new List<string>();

        try
        {
            foreach (var item in cases)
            {
                var caseRoot = Path.Combine(root, item.Name);
                var databaseName = $"AsapMigrationAuthorsDefault_{Guid.NewGuid():N}";
                var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
                var helpSql = item.HelpIsNull ? "NULL" : "''";
                var messageSql = item.MessageIsNull ? "NULL" : "''";
                var package = CreateMinimalPackage(
                    caseRoot,
                    $$"""
                    CREATE TABLE [workflow_settings]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [scope] TEXT NOT NULL,
                        [libraryOrganization] TEXT,
                        [commonAuthorsHelp] TEXT,
                        [commonAuthorsMessage] TEXT
                    );
                    INSERT INTO [workflow_settings] VALUES
                        ('workflow-system', 'system', NULL, {{helpSql}}, {{messageSql}});
                    """);
                var report = Path.Combine(caseRoot, "report.json");

                using (var validationError = new StringWriter())
                {
                    Assert.AreEqual(
                        0,
                        MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validationError),
                        validationError.ToString());
                }

                deployedDatabaseNames.Add(databaseName);
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
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT [CommonAuthorsHelp], [CommonAuthorsMessage] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 1;";
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual(item.HelpIsNull, reader.IsDBNull(0), "The imported SQL value must preserve source NULL versus empty.");
                    if (!item.HelpIsNull)
                    {
                        Assert.AreEqual(string.Empty, reader.GetString(0));
                    }
                    Assert.AreEqual(item.MessageIsNull, reader.IsDBNull(1), "The imported SQL value must preserve source NULL versus empty.");
                    if (!item.MessageIsNull)
                    {
                        Assert.AreEqual(string.Empty, reader.GetString(1));
                    }
                    Assert.IsFalse(await reader.ReadAsync());
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
                var effective = await configuration.GetAsync(1, CancellationToken.None);
                Assert.IsNotNull(effective);
                if (!string.Equals(commonAuthorsHelpDefault, effective.CommonCreatorsHelp, StringComparison.Ordinal))
                {
                    effectiveValueFailures.Add(
                        $"{item.Name}: effective CommonAuthorsHelp was {effective.CommonCreatorsHelp.Length} chars, expected the pinned default.");
                }
                if (!string.Equals(commonAuthorsMessageDefault, effective.CommonCreatorsMessage, StringComparison.Ordinal))
                {
                    effectiveValueFailures.Add(
                        $"{item.Name}: effective CommonAuthorsMessage was {effective.CommonCreatorsMessage.Length} chars, expected the pinned default.");
                }
            }

            Assert.AreEqual(0, effectiveValueFailures.Count, string.Join(Environment.NewLine, effectiveValueFailures));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnection);
            foreach (var databaseName in deployedDatabaseNames)
            {
                await DropDatabaseAsync(master, databaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Common-authors cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
