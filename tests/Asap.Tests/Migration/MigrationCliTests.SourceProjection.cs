using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text.Json;
using Asap.Migration;
using Microsoft.Data.SqlClient;
using Asap.Web.Features.Staff;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ValidateAndImportRejectMalformedPolarisSourceIdentityBeforeWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-source-identity-{Guid.NewGuid():N}");
        var validDatabaseName = $"AsapMigrationIdentityValid_{Guid.NewGuid():N}";
        var invalidDatabaseName = $"AsapMigrationIdentityInvalid_{Guid.NewGuid():N}";
        var invalidReferenceDatabaseName = $"AsapMigrationReferenceInvalid_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var validTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = validDatabaseName }.ConnectionString;
        var invalidTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = invalidDatabaseName }.ConnectionString;
        var invalidReferenceTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = invalidReferenceDatabaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        const string sourceApiKey = "migration-source-identity-api-key";
        Directory.CreateDirectory(root);
        X509Certificate2? persistedCertificate = null;

        static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        string CreatePackage(string caseName, string sourceId, string formatReference)
        {
            var caseRoot = Path.Combine(root, caseName);
            var sql = $$"""
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
                    ('fmt-book', 'system', NULL, '0', 'Printed Book', 1, 10, 'none',
                     'required', 'Title', 'required', 'Author', 'optional', 'Identifier',
                     'optional', 'Publication', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('pb-request-identity', '2', {{SqlLiteral(formatReference)}}, 'A20000000000001',
                     'Identity reference control', 0, 'suggestion',
                     '2029-03-01T12:00:00Z', '2029-03-02T12:00:00Z');
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT, [apiKey] TEXT,
                    [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT, [workstationId] TEXT,
                    [userId] TEXT, [langId] TEXT, [appId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ({{SqlLiteral(sourceId)}}, 'https://polaris.example.org', 'access', {{SqlLiteral(sourceApiKey)}},
                     'EXAMPLE', 'service-user', NULL, '99', '42', NULL, NULL, NULL, NULL);
                """;
            return CreateMinimalPackage(caseRoot, sql);
        }

        void WriteConfiguration(string package, string keyPath)
        {
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = persistedCertificate!.Thumbprint;
            File.WriteAllText(
                ExternalConfigurationPath(package),
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
        }

        try
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=ASAP Migration Source Identity Test",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            persistedCertificate = X509CertificateLoader.LoadPkcs12(
                certificate.Export(X509ContentType.Pfx),
                password: null,
                X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(persistedCertificate);
            }

            var validPackage = CreatePackage("exact-source-id", "polaris-1", "fmt-book");
            var malformedPackage = CreatePackage("padded-source-id", " polaris-1 ", "fmt-book");
            var malformedReferencePackage = CreatePackage("padded-source-reference", "polaris-1", " fmt-book ");
            var validKeyPath = Path.Combine(root, "valid-keys");
            var invalidKeyPath = Path.Combine(root, "invalid-keys");
            var invalidReferenceKeyPath = Path.Combine(root, "invalid-reference-keys");
            WriteConfiguration(validPackage, validKeyPath);
            WriteConfiguration(malformedPackage, invalidKeyPath);
            WriteConfiguration(malformedReferencePackage, invalidReferenceKeyPath);

            DeployDacpac(master, validDatabaseName);
            DeployDacpac(master, invalidDatabaseName);
            DeployDacpac(master, invalidReferenceDatabaseName);
            var validReport = Path.Combine(root, "valid-report.json");
            var malformedReport = Path.Combine(root, "malformed-report.json");
            var malformedReferenceReport = Path.Combine(root, "malformed-reference-report.json");

            using (var validate = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", validPackage], TextWriter.Null, validate),
                    validate.ToString());
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, validTarget);
            using (var import = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(validPackage, validReport, connectionEnvironmentName, tenantId, import), import.ToString());
            }
            using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(validReport)))
            {
                var polarisTransformation = report.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "polaris_settings");
                Assert.AreEqual("polaris-1", polarisTransformation.GetProperty("sourceId").GetString());
            }
            await using (var connection = new SqlConnection(validTarget))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(
                    connection,
                    "SELECT COUNT(*) FROM [asap].[TitleRequest] request JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'title_request' AND mapping.[PocketBaseId] = N'pb-request-identity' AND mapping.[NewId] = request.[Id] JOIN [asap].[MaterialFormat] format ON format.[Id] = request.[MaterialFormatId] WHERE request.[Title] = N'Identity reference control' AND format.[OwnerOrganizationId] = 1 AND format.[Code] = N'book';"),
                    "The exact relation-ID control must resolve to the canonical target format.");
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT [ProtectedApiKey] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;";
                var protectedApiKey = (string?)await command.ExecuteScalarAsync();
                Assert.IsFalse(string.IsNullOrWhiteSpace(protectedApiKey));
                Assert.AreNotEqual(sourceApiKey, protectedApiKey,
                    "The exact-ID control confirms the source credential was protected rather than stored as plaintext.");
            }
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(validPackage, validReport, connectionEnvironmentName, reconcile), reconcile.ToString());
            }

            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidTarget);
            var targetFingerprintBefore = ComputeTargetFingerprintForTest(invalidTarget);
            using var validationError = new StringWriter();
            var validationExitCode = MigrationCli.Run(
                ["validate", "--package", malformedPackage],
                TextWriter.Null,
                validationError);
            using var importError = new StringWriter();
            var importExitCode = RunImport(malformedPackage, malformedReport, connectionEnvironmentName, tenantId, importError);
            var targetFingerprintAfter = ComputeTargetFingerprintForTest(invalidTarget);
            var failures = new List<string>();
            if (validationExitCode != 1)
            {
                failures.Add("Public package validation accepted a padded generated Polaris source ID.");
            }
            if (importExitCode != 1)
            {
                failures.Add("Public import did not refuse the malformed Polaris source identity.");
            }
            if (!string.Equals(targetFingerprintBefore, targetFingerprintAfter, StringComparison.Ordinal))
            {
                failures.Add("Rejected source identity changed target SQL.");
            }
            if (File.Exists(malformedReport) || File.Exists(malformedReport + ".pending"))
            {
                failures.Add("Rejected source identity wrote or prepared a migration report.");
            }
            if (Directory.Exists(invalidKeyPath))
            {
                failures.Add("Rejected source identity wrote a Data Protection key ring.");
            }
            if (validationError.ToString().Contains(sourceApiKey, StringComparison.Ordinal) ||
                importError.ToString().Contains(sourceApiKey, StringComparison.Ordinal))
            {
                failures.Add("A validation or import diagnostic disclosed source credential material.");
            }

            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidReferenceTarget);
            var referenceFingerprintBefore = ComputeTargetFingerprintForTest(invalidReferenceTarget);
            using var referenceValidationError = new StringWriter();
            var referenceValidationExitCode = MigrationCli.Run(
                ["validate", "--package", malformedReferencePackage],
                TextWriter.Null,
                referenceValidationError);
            using var referenceImportError = new StringWriter();
            var referenceImportExitCode = RunImport(
                malformedReferencePackage,
                malformedReferenceReport,
                connectionEnvironmentName,
                tenantId,
                referenceImportError);
            var referenceFingerprintAfter = ComputeTargetFingerprintForTest(invalidReferenceTarget);
            if (referenceValidationExitCode != 1 || referenceImportExitCode != 1)
            {
                failures.Add("Public validation or import accepted a padded generated format reference.");
            }
            if (!string.Equals(referenceFingerprintBefore, referenceFingerprintAfter, StringComparison.Ordinal))
            {
                failures.Add("Rejected source relationship changed target SQL.");
            }
            if (File.Exists(malformedReferenceReport) || File.Exists(malformedReferenceReport + ".pending"))
            {
                failures.Add("Rejected source relationship wrote or prepared a migration report.");
            }
            if (Directory.Exists(invalidReferenceKeyPath))
            {
                failures.Add("Rejected source relationship wrote a Data Protection key ring.");
            }
            if (referenceValidationError.ToString().Contains(sourceApiKey, StringComparison.Ordinal) ||
                referenceImportError.ToString().Contains(sourceApiKey, StringComparison.Ordinal))
            {
                failures.Add("A source-relationship diagnostic disclosed source credential material.");
            }

            Assert.AreEqual(
                string.Empty,
                string.Join(Environment.NewLine, failures),
                "Only an exact generated source ID is a valid source identity; refusal must precede SQL, report, and key-ring writes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            if (persistedCertificate is not null)
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Remove(persistedCertificate);
                persistedCertificate.Dispose();
            }
            await DropDatabaseAsync(master, validDatabaseName);
            await DropDatabaseAsync(master, invalidDatabaseName);
            await DropDatabaseAsync(master, invalidReferenceDatabaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportPreservesInertNelJavascriptPatronPatternWithoutActivatingLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-inert-patron-pattern-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationInertPatronPattern_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        try
        {
            var patronPattern = "\u0085javascript:alert({{patron-id}})";
            var package = CreateMinimalPackage(
                root,
                $$"""
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [leapBibUrlPattern] TEXT,
                    [leapPatronUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES ('settings-1', NULL, {{SqlLiteral(patronPattern)}});
                """);
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            using var validationError = new StringWriter();
            var validationExitCode = MigrationCli.Run(
                ["validate", "--package", package],
                TextWriter.Null,
                validationError);
            using var importError = new StringWriter();
            var importExitCode = RunImport(package, report, connectionEnvironmentName, tenantId, importError);
            var failures = new List<string>();
            if (validationExitCode != 0)
            {
                failures.Add("Public validation rejected a source pattern that cannot activate the target patron lookup.");
            }
            if (importExitCode != 0)
            {
                failures.Add("Public import rejected an inert NEL-prefixed non-HTTP patron pattern.");
            }

            if (importExitCode == 0)
            {
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT [LeapPatronUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;";
                var importedPattern = (string?)await command.ExecuteScalarAsync();
                if (!string.Equals(importedPattern, patronPattern, StringComparison.Ordinal))
                {
                    failures.Add("The inert source patron pattern was not preserved exactly in SQL.");
                }
                using var reconcile = new StringWriter();
                if (RunReconcile(package, report, connectionEnvironmentName, reconcile) != 0)
                {
                    failures.Add("Independent reconciliation rejected the preserved inert patron pattern.");
                }
            }

            var backendPredicate = typeof(TitleRequestEndpoints).GetMethod(
                "HasUsablePatronResearchUrl",
                BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new InvalidOperationException("The target patron research predicate is missing.");
            if ((bool)backendPredicate.Invoke(null, [patronPattern])!)
            {
                failures.Add("The target backend considers a NEL-prefixed JavaScript URL usable for patron lookup.");
            }

            Assert.AreEqual(
                string.Empty,
                string.Join(Environment.NewLine, failures),
                "The raw source value is inert in both the pinned browser URL builder and the target HTTP(S)-only lookup predicate.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }
}
