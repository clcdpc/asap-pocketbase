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
    public async Task ImportAndReconcilePreservePinnedScopedCurrentTextSemantics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-scoped-current-text-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationScopedCurrentText_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var postmarkTokenEnvironmentName = $"ASAP_MIGRATION_TEST_POSTMARK_{Guid.NewGuid():N}";
        var previousPostmarkTokenEnvironment = Environment.GetEnvironmentVariable(postmarkTokenEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string systemCreatorsLabel = "System creators label sentinel";
        const string systemCreatorsHelp = "System creators help sentinel";
        const string systemCreatorsMessage = "System creators message sentinel";
        const string systemEligibilityMessage = "System eligibility message sentinel";
        const string systemPageTitle = "System page title sentinel";
        const string weeklyFallback = "Weekly suggestion limit reached. You can try again after {{next_available_date}}.";
        const string librarySuggestionLimitMessage = "  Library weekly limit message  ";
        const string libraryCommonAuthorsHelp = "  Library creators help  ";
        const string libraryCommonAuthorsMessage = "  Library creators message  ";
        const string libraryPatronCodeMessage = "  Library eligibility message  ";
        const string feffOnly = "\uFEFF";
        var workflowKeyPath = Path.Combine(root, "workflow-preflight-keys");
        var targetDeployed = false;
        Directory.CreateDirectory(root);

        static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
        static string? NullableString(SqlDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        int RunImportWithToken(string sourcePackage, string reportPath, StringWriter error) => MigrationCli.Run(
            [
                "import", "--package", sourcePackage,
                "--connection-string-env", connectionEnvironmentName,
                "--allowed-tenant-ids", tenantId.ToString(),
                "--report", reportPath,
                "--external-config", ExternalConfigurationPath(sourcePackage),
                "--postmark-token-env", postmarkTokenEnvironmentName
            ],
            TextWriter.Null,
            error);

        var workflowTextFields = new[]
        {
            "suggestionLimitMessage",
            "commonAuthorsLabel",
            "commonAuthorsHelp",
            "commonAuthorsMessage",
            "patronCodeEligibilityMessage"
        };
        var systemWorkflowValues = new[]
        {
            "",
            systemCreatorsLabel,
            systemCreatorsHelp,
            systemCreatorsMessage,
            systemEligibilityMessage
        };
        var libraryWorkflowValues = new[]
        {
            librarySuggestionLimitMessage,
            feffOnly,
            libraryCommonAuthorsHelp,
            libraryCommonAuthorsMessage,
            libraryPatronCodeMessage
        };

        var patronTextFields = new[]
        {
            "pageTitle",
            "barcodeLabel",
            "pinLabel",
            "loginPrompt",
            "loginNote",
            "suggestionFormNote",
            "noEmailMessage",
            "successTitle",
            "successMessage",
            "alreadySubmittedMessage",
            "ebookMessage",
            "eaudiobookMessage"
        };
        var targetPatronColumns = new[]
        {
            "PageTitle",
            "BarcodeLabel",
            "PinLabel",
            "LoginPrompt",
            "LoginNote",
            "SuggestionFormNote",
            "NoEmailMessage",
            "SuccessTitle",
            "SuccessMessage",
            "AlreadySubmittedMessage",
            "EbookMessage",
            "EaudiobookMessage"
        };
        var systemPatronValues = new[]
        {
            systemPageTitle,
            "System barcode sentinel",
            "System pin sentinel",
            "System login prompt sentinel",
            "System login note sentinel",
            "System form note sentinel",
            "System no-email sentinel",
            "System success title sentinel",
            "System success message sentinel",
            "System duplicate message sentinel",
            "System eBook message sentinel",
            "System eAudiobook message sentinel"
        };
        var libraryPatronValues = new[]
        {
            "",
            "  Library barcode label  ",
            "  Library pin label  ",
            "  Library login prompt  ",
            "  Library login note  ",
            "  Library form note  ",
            "  Library no-email message  ",
            "  Library success title  ",
            "  Library success message  ",
            "  Library duplicate message  ",
            "  Library eBook message  ",
            "  Library eAudiobook message  "
        };
        var workflowColumns = string.Join(", ", workflowTextFields.Select(name => $"[{name}]"));
        var patronColumns = string.Join(", ", patronTextFields.Select(name => $"[{name}]"));
        var workflowSystemSqlValues = string.Join(", ", systemWorkflowValues.Select(SqlLiteral));
        var workflowLibrarySqlValues = string.Join(", ", libraryWorkflowValues.Select(value =>
            string.Equals(value, feffOnly, StringComparison.Ordinal) ? "char(65279)" : SqlLiteral(value)));
        var systemPatronSqlValues = string.Join(", ", systemPatronValues.Select(SqlLiteral));
        var libraryPatronSqlValues = string.Join(", ", libraryPatronValues.Select(SqlLiteral));

        try
        {
            var package = CreateMinimalPackage(
                root,
                $$"""
                CREATE TABLE [workflow_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    {{workflowColumns}}
                );
                INSERT INTO [workflow_settings] ([id], [scope], [libraryOrganization], {{workflowColumns}}) VALUES
                    ('workflow-system', 'system', NULL, {{workflowSystemSqlValues}}),
                    ('workflow-library', 'library', 'pb-org-2', {{workflowLibrarySqlValues}});
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    {{patronColumns}}
                );
                INSERT INTO [ui_settings] ([id], [scope], [libraryOrganization], {{patronColumns}}) VALUES
                    ('ui-system', 'system', NULL, {{systemPatronSqlValues}}),
                    ('ui-library', 'library', 'pb-org-2', {{libraryPatronSqlValues}});
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
                command.CommandText = "SELECT [OrganizationId], [SuggestionLimitMessage], [CommonAuthorsLabel], [CommonAuthorsHelp], [CommonAuthorsMessage], [PatronCodeEligibilityMessage] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] IN (1, 2) ORDER BY [OrganizationId];";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.AreEqual(string.Empty, reader.GetString(1), "The source system value remains raw in storage while its effective API value uses the pinned fallback.");
                Assert.AreEqual(systemCreatorsLabel, reader.GetString(2));
                Assert.AreEqual(systemCreatorsHelp, reader.GetString(3));
                Assert.AreEqual(systemCreatorsMessage, reader.GetString(4));
                Assert.AreEqual(systemEligibilityMessage, reader.GetString(5));

                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.AreEqual(librarySuggestionLimitMessage, reader.GetString(1));
                Assert.IsTrue(reader.IsDBNull(2), "A FEFF-only workflow value is absent under the pinned JavaScript-trim presence check.");
                Assert.AreEqual(libraryCommonAuthorsHelp, reader.GetString(3));
                Assert.AreEqual(libraryCommonAuthorsMessage, reader.GetString(4));
                Assert.AreEqual(libraryPatronCodeMessage, reader.GetString(5));
                Assert.IsFalse(await reader.ReadAsync());
            }

            var systemPatronSqlValuesExpected = new string?[systemPatronValues.Length];
            var libraryPatronSqlValuesExpected = new string?[libraryPatronValues.Length];
            for (var index = 0; index < systemPatronValues.Length; index++)
            {
                systemPatronSqlValuesExpected[index] = systemPatronValues[index];
                libraryPatronSqlValuesExpected[index] = index == 0 ? null : libraryPatronValues[index];
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT [OrganizationId], {string.Join(", ", targetPatronColumns.Select(name => $"[{name}]"))} FROM [asap].[PatronSettings] WHERE [OrganizationId] IN (1, 2) ORDER BY [OrganizationId];";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(1, reader.GetInt32(0));
                for (var index = 0; index < systemPatronSqlValuesExpected.Length; index++)
                {
                    Assert.AreEqual(systemPatronSqlValuesExpected[index], NullableString(reader, index + 1),
                        $"System patron field {patronTextFields[index]} must retain its raw configured value.");
                }

                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(2, reader.GetInt32(0));
                for (var index = 0; index < libraryPatronSqlValuesExpected.Length; index++)
                {
                    Assert.AreEqual(libraryPatronSqlValuesExpected[index], NullableString(reader, index + 1),
                        $"Library patron field {patronTextFields[index]} must retain its raw meaningful value or remain absent.");
                }
                Assert.IsFalse(await reader.ReadAsync());
            }

            using (var services = new ServiceCollection()
                       .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target))
                       .BuildServiceProvider())
            {
                var configuration = new PatronConfigurationService(
                    services.GetRequiredService<IDbContextFactory<AsapDbContext>>());
                var system = await configuration.GetAsync(1, CancellationToken.None);
                Assert.IsNotNull(system);
                Assert.AreEqual(weeklyFallback, system.SuggestionLimitMessage,
                    "A blank system workflow value resolves through the pinned fixed fallback.");
                Assert.AreEqual(systemPageTitle, system.PageTitle);

                var library = await configuration.GetAsync(2, CancellationToken.None);
                Assert.IsNotNull(library);
                Assert.AreEqual(librarySuggestionLimitMessage, library.SuggestionLimitMessage);
                Assert.AreEqual(systemCreatorsLabel, library.CommonCreatorsLabel,
                    "A FEFF-only library workflow value is absent and inherits the customized system value.");
                Assert.AreEqual(libraryCommonAuthorsHelp, library.CommonCreatorsHelp);
                Assert.AreEqual(libraryCommonAuthorsMessage, library.CommonCreatorsMessage);
                Assert.AreEqual(libraryPatronCodeMessage, library.PatronCodeEligibilityMessage);
                Assert.AreEqual(systemPageTitle, library.PageTitle,
                    "Blank library page title is the documented field-by-field inheritance correction.");
                Assert.AreEqual(libraryPatronValues[1], library.BarcodeLabel);
                Assert.AreEqual(libraryPatronValues[2], library.PinLabel);
                Assert.AreEqual(libraryPatronValues[3], library.LoginPrompt);
                Assert.AreEqual(libraryPatronValues[4], library.LoginNote);
                Assert.AreEqual(libraryPatronValues[5], library.SuggestionFormNote);
                Assert.AreEqual(libraryPatronValues[6], library.NoEmailMessage);
                Assert.AreEqual(libraryPatronValues[7], library.SuccessTitle);
                Assert.AreEqual(libraryPatronValues[8], library.SuccessMessage);
                Assert.AreEqual(libraryPatronValues[9], library.AlreadySubmittedMessage);
                Assert.AreEqual(libraryPatronValues[10], library.EbookMessage);
                Assert.AreEqual(libraryPatronValues[11], library.EaudiobookMessage);
            }

            var unrepresentablePackage = CreateMinimalPackage(
                Path.Combine(root, "workflow-nel-only"),
                """
                CREATE TABLE [workflow_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [commonAuthorsLabel] TEXT
                );
                INSERT INTO [workflow_settings] VALUES
                    ('workflow-system', 'system', NULL, 'System creators label sentinel'),
                    ('workflow-library', 'library', 'pb-org-2', char(133));
                """);
            var unrepresentableConfiguration = TestConfigurationFactory.Create();
            unrepresentableConfiguration.Application.DataProtectionKeysPath = workflowKeyPath;
            await File.WriteAllTextAsync(
                ExternalConfigurationPath(unrepresentablePackage),
                JsonSerializer.Serialize(unrepresentableConfiguration, new JsonSerializerOptions { WriteIndented = true }));
            var unrepresentableReport = Path.Combine(root, "workflow-nel-only-report.json");
            Environment.SetEnvironmentVariable(postmarkTokenEnvironmentName, "fixture-postmark-token");

            Environment.SetEnvironmentVariable(connectionEnvironmentName, "not a SQL Server connection string");
            using (var invalidConnectionError = new StringWriter())
            {
                Assert.AreEqual(1, RunImportWithToken(unrepresentablePackage, unrepresentableReport, invalidConnectionError));
                StringAssert.Contains(invalidConnectionError.ToString(), "workflow_text_unrepresentable");
                StringAssert.Contains(invalidConnectionError.ToString(), "commonAuthorsLabel");
            }
            Assert.IsFalse(File.Exists(unrepresentableReport));
            Assert.IsFalse(File.Exists(unrepresentableReport + ".pending"));
            Assert.IsFalse(Directory.Exists(workflowKeyPath),
                "A source-selected NEL-only workflow message must be refused before loading the credential protector.");

            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var targetFingerprintBeforeRefusal = ComputeTargetFingerprintForTest(target);
            using (var reachableSqlError = new StringWriter())
            {
                Assert.AreEqual(1, RunImportWithToken(unrepresentablePackage, unrepresentableReport, reachableSqlError));
                StringAssert.Contains(reachableSqlError.ToString(), "workflow_text_unrepresentable");
                StringAssert.Contains(reachableSqlError.ToString(), "commonAuthorsLabel");
            }
            Assert.AreEqual(targetFingerprintBeforeRefusal, ComputeTargetFingerprintForTest(target),
                "A workflow value the target resolver cannot represent must be refused before SQL writes.");
            Assert.IsFalse(File.Exists(unrepresentableReport));
            Assert.IsFalse(File.Exists(unrepresentableReport + ".pending"));
            Assert.IsFalse(Directory.Exists(workflowKeyPath));

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(targetFingerprintBeforeRefusal, ComputeTargetFingerprintForTest(target),
                "After rejecting the NEL-only source package, the original source package still reconciles exactly to the unchanged target.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            Environment.SetEnvironmentVariable(postmarkTokenEnvironmentName, previousPostmarkTokenEnvironment);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Scoped-current-text cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

}
