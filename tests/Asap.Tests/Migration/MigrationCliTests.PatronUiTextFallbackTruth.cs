using Asap.Migration;
using Asap.Tests;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportAndReconcilePreservePinnedPatronUiTextFallbackSemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-patron-ui-text-fallback-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPatronUiTextFallback_{Guid.NewGuid():N}";
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
        const string systemPageTitle = "System custom patron page title";
        const string systemEbookMessage = "System eBook message sentinel";
        const string systemEaudiobookMessage = "System eAudiobook message sentinel";
        const string libraryEbookMessage = "Library-specific eBook message";
        const string libraryEaudiobookMessage = "Library-specific eAudiobook message";
        const string modernEbookMessage = "\uFEFF";
        const string modernEaudiobookMessage = "  Modern eAudiobook override remains raw  ";
        var unrepresentableDatabaseName = $"AsapMigrationPatronUiTextUnrepresentable_{Guid.NewGuid():N}";
        var unrepresentableTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = unrepresentableDatabaseName }.ConnectionString;
        var unrepresentableConnectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousUnrepresentableConnectionEnvironment = Environment.GetEnvironmentVariable(unrepresentableConnectionEnvironmentName);
        var postmarkTokenEnvironmentName = $"ASAP_MIGRATION_TEST_POSTMARK_{Guid.NewGuid():N}";
        var previousPostmarkTokenEnvironment = Environment.GetEnvironmentVariable(postmarkTokenEnvironmentName);
        var keyPath = Path.Combine(root, "keys");
        var targetDeployed = false;
        var unrepresentableTargetDeployed = false;
        Directory.CreateDirectory(root);

        int RunImportRequiringProtection(string sourcePackage, string reportPath, StringWriter error) => MigrationCli.Run(
            [
                "import", "--package", sourcePackage,
                "--connection-string-env", unrepresentableConnectionEnvironmentName,
                "--allowed-tenant-ids", tenantId.ToString(),
                "--report", reportPath,
                "--external-config", ExternalConfigurationPath(sourcePackage),
                "--postmark-token-env", postmarkTokenEnvironmentName
            ],
            TextWriter.Null,
            error);

        try
        {
            var package = CreateMinimalPackage(
                root,
                $$"""
                INSERT INTO [polaris_organizations] VALUES
                    ('pb-org-3', '3', 'Second Test Library', 'TEST2', 1, 2, 1);
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [pageTitle] TEXT,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('ui-system', 'system', NULL, '{{systemPageTitle}}', '{{systemEbookMessage}}', '{{systemEaudiobookMessage}}'),
                    ('ui-library-2', 'library', 'pb-org-2', '', '{{libraryEbookMessage}}', '{{libraryEaudiobookMessage}}'),
                    ('ui-library-3', 'library', 'pb-org-3', '', 'Library three eBook fallback', 'Library three eAudiobook fallback');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('modern-override-2', '2', '', NULL),
                    ('modern-override-3', '3', '{{modernEbookMessage}}', '{{modernEaudiobookMessage}}');
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
            targetDeployed = true;
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, report, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT [OrganizationId], [PageTitle], [EbookMessage], [EaudiobookMessage] FROM [asap].[PatronSettings] WHERE [OrganizationId] IN (1, 2, 3) ORDER BY [OrganizationId];";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync(), "The system patron text row must be imported.");
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.AreEqual(systemPageTitle, reader.GetString(1));
                Assert.AreEqual(systemEbookMessage, reader.GetString(2));
                Assert.AreEqual(systemEaudiobookMessage, reader.GetString(3));

                Assert.IsTrue(await reader.ReadAsync(), "The library patron text row must be imported.");
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsTrue(reader.IsDBNull(1), "The blank library page title remains absent so the configured system title is inherited.");
                Assert.IsFalse(reader.IsDBNull(2), "An empty modern eBook override must not erase the meaningful library UI value.");
                Assert.IsFalse(reader.IsDBNull(3), "A null modern eAudiobook override must not erase the meaningful library UI value.");
                Assert.AreEqual(libraryEbookMessage, reader.GetString(2), "An empty modern eBook override falls back to the meaningful library UI value.");
                Assert.AreEqual(libraryEaudiobookMessage, reader.GetString(3), "A null modern eAudiobook override falls back to the meaningful library UI value.");

                Assert.IsTrue(await reader.ReadAsync(), "The second library patron text row must be imported.");
                Assert.AreEqual(3, reader.GetInt32(0));
                Assert.AreEqual(modernEbookMessage, reader.GetString(2), "A truthy FEFF-only modern eBook override remains raw because it is representable by the target resolver.");
                Assert.AreEqual(modernEaudiobookMessage, reader.GetString(3), "A truthy padded modern eAudiobook override remains raw.");
                Assert.IsFalse(await reader.ReadAsync(), "System and library patron text rows must remain unique.");
            }

            using (var services = new ServiceCollection()
                       .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target))
                       .BuildServiceProvider())
            {
                var configuration = new PatronConfigurationService(
                    services.GetRequiredService<IDbContextFactory<AsapDbContext>>());
                var effective = await configuration.GetAsync(2, CancellationToken.None);
                Assert.IsNotNull(effective);
                Assert.AreEqual(systemPageTitle, effective.PageTitle,
                    "A blank partial library page title inherits the configured system value.");
                Assert.AreEqual(libraryEbookMessage, effective.EbookMessage);
                Assert.AreEqual(libraryEaudiobookMessage, effective.EaudiobookMessage);

                var secondLibrary = await configuration.GetAsync(3, CancellationToken.None);
                Assert.IsNotNull(secondLibrary);
                Assert.AreEqual(modernEbookMessage, secondLibrary.EbookMessage);
                Assert.AreEqual(modernEaudiobookMessage, secondLibrary.EaudiobookMessage);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(
                Path.Combine(keyPath, "sentinel.txt"),
                "Unrepresentable patron messages must not write protected keys.");
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            Environment.SetEnvironmentVariable(postmarkTokenEnvironmentName, "fixture-postmark-token");
            DeployDacpac(master, unrepresentableDatabaseName);
            unrepresentableTargetDeployed = true;
            Environment.SetEnvironmentVariable(unrepresentableConnectionEnvironmentName, unrepresentableTarget);
            var sqlFingerprintBeforeRefusal = ComputeTargetFingerprintForTest(unrepresentableTarget);

            var invalidMessages = new[]
            {
                (Name: "modern-ebook-space", Field: "ebookMessage", SqlValue: "' '", InvalidConnectionFirst: true),
                (Name: "modern-eaudiobook-nel", Field: "eaudiobookMessage", SqlValue: "char(133)", InvalidConnectionFirst: false)
            };
            foreach (var invalidMessage in invalidMessages)
            {
                var ebookSql = invalidMessage.Field == "ebookMessage" ? invalidMessage.SqlValue : "NULL";
                var eaudiobookSql = invalidMessage.Field == "eaudiobookMessage" ? invalidMessage.SqlValue : "NULL";
                var unrepresentablePackage = CreateMinimalPackage(
                    Path.Combine(root, invalidMessage.Name),
                    $$"""
                    CREATE TABLE [ui_settings]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [scope] TEXT NOT NULL,
                        [libraryOrganization] TEXT,
                        [pageTitle] TEXT,
                        [ebookMessage] TEXT,
                        [eaudiobookMessage] TEXT
                    );
                    INSERT INTO [ui_settings] VALUES
                        ('ui-system', 'system', NULL, 'Configured system page title', 'System eBook', 'System eAudiobook'),
                        ('ui-library-2', 'library', 'pb-org-2', '', 'Meaningful library eBook', 'Meaningful library eAudiobook');
                    CREATE TABLE [patron_settings_overrides]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [orgId] TEXT NOT NULL,
                        [ebookMessage] TEXT,
                        [eaudiobookMessage] TEXT
                    );
                    INSERT INTO [patron_settings_overrides] VALUES
                        ('modern-override-2', '2', {{ebookSql}}, {{eaudiobookSql}});
                    """);
                var externalConfigurationPath = ExternalConfigurationPath(unrepresentablePackage);
                var configuration = TestConfigurationFactory.Create();
                configuration.Application.DataProtectionKeysPath = keyPath;
                await File.WriteAllTextAsync(
                    externalConfigurationPath,
                    JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

                using (var validationError = new StringWriter())
                {
                    Assert.AreEqual(
                        0,
                        MigrationCli.Run(
                            ["validate", "--package", unrepresentablePackage, "--external-config", externalConfigurationPath],
                            TextWriter.Null,
                            validationError),
                        validationError.ToString());
                }

                var refusalReport = Path.Combine(root, $"{invalidMessage.Name}-report.json");
                if (invalidMessage.InvalidConnectionFirst)
                {
                    Environment.SetEnvironmentVariable(unrepresentableConnectionEnvironmentName, "not a SQL Server connection string");
                    using var invalidConnectionError = new StringWriter();
                    Assert.AreEqual(
                        1,
                        RunImportRequiringProtection(unrepresentablePackage, refusalReport, invalidConnectionError),
                        invalidConnectionError.ToString());
                    StringAssert.Contains(invalidConnectionError.ToString(), "patron_message_unrepresentable");
                    StringAssert.Contains(invalidConnectionError.ToString(), "eBook");
                    Assert.IsFalse(File.Exists(refusalReport));
                    Assert.IsFalse(File.Exists(refusalReport + ".pending"));
                    Assert.AreEqual(sqlFingerprintBeforeRefusal, ComputeTargetFingerprintForTest(unrepresentableTarget));
                    Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
                    Environment.SetEnvironmentVariable(unrepresentableConnectionEnvironmentName, unrepresentableTarget);
                }

                using (var importError = new StringWriter())
                {
                    Assert.AreEqual(
                        1,
                        RunImportRequiringProtection(unrepresentablePackage, refusalReport, importError),
                        importError.ToString());
                    StringAssert.Contains(importError.ToString(), "patron_message_unrepresentable");
                    StringAssert.Contains(importError.ToString(), invalidMessage.Field == "ebookMessage" ? "eBook" : "eAudiobook");
                }
                Assert.IsFalse(File.Exists(refusalReport), "An unrepresentable modern patron message must not publish an import report.");
                Assert.IsFalse(File.Exists(refusalReport + ".pending"), "An unrepresentable modern patron message must not prepare a report.");
                Assert.AreEqual(sqlFingerprintBeforeRefusal, ComputeTargetFingerprintForTest(unrepresentableTarget),
                    "Unrepresentable modern patron messages must be refused before target SQL changes.");
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    "Unrepresentable modern patron messages must not write Data Protection keys.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            Environment.SetEnvironmentVariable(
                unrepresentableConnectionEnvironmentName,
                previousUnrepresentableConnectionEnvironment);
            Environment.SetEnvironmentVariable(postmarkTokenEnvironmentName, previousPostmarkTokenEnvironment);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            if (unrepresentableTargetDeployed)
            {
                await DropDatabaseAsync(master, unrepresentableDatabaseName);
            }

            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Patron UI text fallback cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
