using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ImportUsesPinnedJavascriptWhitespaceForMaterialFormatIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-js-trim-{Guid.NewGuid():N}");
        var validDatabaseName = $"AsapMigrationFormatJsTrim_{Guid.NewGuid():N}";
        var invalidDatabaseName = $"AsapMigrationFormatJsTrimNel_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var validTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = validDatabaseName }.ConnectionString;
        var invalidTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = invalidDatabaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnectionEnvironment = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var queueNames = new[]
        {
            "pending_suggestion_isbn_checks", "outstanding_purchases", "pending_holds", "checked_out",
            "outstanding_timeout", "pending_hold_timeout", "hold_pickup_timeout", "additional_copy_timeout"
        };
        var environmentNames = new[]
            {
                "ASAP_STAFF_URL", "ASAP_PUBLIC_URL", "ASAP_BASE_URL",
                "ASAP_CRON_SCHEDULE", "ASAP_ISBN_CHECK_CRON_SCHEDULE", "ASAP_ORG_SYNC_CRON_SCHEDULE",
                "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", "ASAP_JOB_PAGE_SIZE", "ASAP_JOB_MAX_PER_RUN",
                "ASAP_TIMEOUT_PAGE_SIZE", "ASAP_TIMEOUT_MAX_PER_RUN", "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE",
                "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"
            }
            .Concat(queueNames.SelectMany(name => new[]
            {
                $"ASAP_{name.ToUpperInvariant()}_PAGE_SIZE",
                $"ASAP_{name.ToUpperInvariant()}_MAX_PER_RUN"
            }))
            .ToArray();
        var previousEnvironment = environmentNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var validTargetDeployed = false;
        var invalidTargetDeployed = false;
        Directory.CreateDirectory(root);

        static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        string CreatePackage(string caseName, string sourceCode, string scope, string? libraryOrganization)
        {
            var caseRoot = Path.Combine(root, caseName);
            var organizationValue = libraryOrganization is null ? "NULL" : SqlLiteral(libraryOrganization);
            var package = CreateMinimalPackage(
                caseRoot,
                $$"""
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL,
                    [sortOrder] INTEGER NOT NULL, [messageBehavior] TEXT, [titleMode] TEXT,
                    [titleLabel] TEXT, [authorMode] TEXT, [authorLabel] TEXT, [identifierMode] TEXT,
                    [identifierLabel] TEXT, [publicationMode] TEXT, [publicationLabel] TEXT,
                    [created] TEXT, [updated] TEXT
                );
                INSERT INTO [material_formats] VALUES
                    ('format-source', {{SqlLiteral(scope)}}, {{organizationValue}}, {{SqlLiteral(sourceCode)}}, 'Printed Book', 1, 10,
                     'none', 'required', 'Title', 'required', 'Author', 'optional', 'Identifier',
                     'required', 'Publication Timing', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-format-reference', '2', 'format-source', 'A20000000000001',
                     'Pinned format whitespace', 0, 'suggestion',
                     '2029-03-01T12:00:00Z', '2029-03-02T12:00:00Z');
                """);
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = Path.Combine(caseRoot, "keys");
            File.WriteAllText(
                ExternalConfigurationPath(package),
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            return package;
        }

        try
        {
            foreach (var name in environmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            var feffPackage = CreatePackage("feff", "\uFEFFBook", "system", null);
            var nelPackage = CreatePackage("nel", "\u0085book", "library", "pb-org-2");
            var validReport = Path.Combine(root, "feff-report.json");
            var invalidReport = Path.Combine(root, "nel-report.json");
            var invalidKeyPath = Path.Combine(root, "nel", "keys");

            DeployDacpac(master, validDatabaseName);
            validTargetDeployed = true;
            DeployDacpac(master, invalidDatabaseName);
            invalidTargetDeployed = true;

            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", feffPackage], TextWriter.Null, validationError),
                    validationError.ToString());
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, validTarget);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(feffPackage, validReport, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using (var connection = new SqlConnection(validTarget))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM [asap].[MaterialFormat] format JOIN [asap].[LegacyPocketBaseMapping] formatMapping ON formatMapping.[EntityType] = N'material_format' AND formatMapping.[PocketBaseId] = N'format-source' AND formatMapping.[NewId] = format.[Id] JOIN [asap].[TitleRequest] request ON request.[MaterialFormatId] = format.[Id] JOIN [asap].[LegacyPocketBaseMapping] requestMapping ON requestMapping.[EntityType] = N'title_request' AND requestMapping.[PocketBaseId] = N'request-format-reference' AND requestMapping.[NewId] = request.[Id] WHERE format.[OwnerOrganizationId] = 1 AND format.[Code] = N'book' AND request.[Title] = N'Pinned format whitespace';"),
                    "Pinned JavaScript trim should map the FEFF-padded source format and its exact request reference to the canonical target book format.");
            }
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(feffPackage, validReport, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidTarget);
            var invalidFingerprintBefore = ComputeTargetFingerprintForTest(invalidTarget);
            using var nelValidationError = new StringWriter();
            var nelValidationExitCode = MigrationCli.Run(
                ["validate", "--package", nelPackage],
                TextWriter.Null,
                nelValidationError);
            using var nelImportError = new StringWriter();
            var nelImportExitCode = RunImport(nelPackage, invalidReport, connectionEnvironmentName, tenantId, nelImportError);
            var invalidFingerprintAfter = ComputeTargetFingerprintForTest(invalidTarget);

            Assert.AreEqual(1, nelValidationExitCode,
                "A NEL-prefixed source identity cannot be silently canonicalized to book when the target editor trims it differently from the pinned source.");
            Assert.AreEqual(1, nelImportExitCode, nelImportError.ToString());
            StringAssert.Contains(nelValidationError.ToString(), "format_library_seed_unsupported");
            Assert.AreEqual(invalidFingerprintBefore, invalidFingerprintAfter, "The unrepresentable format identity must be refused before SQL writes.");
            Assert.IsFalse(File.Exists(invalidReport));
            Assert.IsFalse(File.Exists(invalidReport + ".pending"));
            Assert.IsFalse(Directory.Exists(invalidKeyPath), "Refusing the source format identity must not create a Data Protection key ring.");
        }
        finally
        {
            foreach (var item in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            if (validTargetDeployed)
            {
                await DropDatabaseAsync(master, validDatabaseName);
            }
            if (invalidTargetDeployed)
            {
                await DropDatabaseAsync(master, invalidDatabaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Material-format whitespace cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
