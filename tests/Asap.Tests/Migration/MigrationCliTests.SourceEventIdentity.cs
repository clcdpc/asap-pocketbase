using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Asap.Migration;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ValidateAndImportRejectPaddedTitleRequestEventRelationsBeforeWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-event-identity-{Guid.NewGuid():N}");
        var cases = new[]
        {
            (Name: "exact", FromStatus: "status-suggestion", ToStatus: "status-closed", CloseReason: "reason-manual", IsValid: true),
            (Name: "padded-from-status", FromStatus: " status-suggestion ", ToStatus: "status-closed", CloseReason: "reason-manual", IsValid: false),
            (Name: "padded-to-status", FromStatus: "status-suggestion", ToStatus: " status-closed ", CloseReason: "reason-manual", IsValid: false),
            (Name: "padded-close-reason", FromStatus: "status-suggestion", ToStatus: "status-closed", CloseReason: " reason-manual ", IsValid: false)
        };
        var databaseNames = cases.ToDictionary(
            item => item.Name,
            _ => $"AsapMigrationEventIdentity_{Guid.NewGuid():N}",
            StringComparer.Ordinal);
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var targets = databaseNames.ToDictionary(
            item => item.Key,
            item => new SqlConnectionStringBuilder(master) { InitialCatalog = item.Value }.ConnectionString,
            StringComparer.Ordinal);
        const string sourceApiKey = "event-relation-source-key";
        Directory.CreateDirectory(root);
        X509Certificate2? persistedCertificate = null;

        static string SqlLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        string CreatePackage((string Name, string FromStatus, string ToStatus, string CloseReason, bool IsValid) item)
        {
            var caseRoot = Path.Combine(root, item.Name);
            var package = CreateMinimalPackage(
                caseRoot,
                $$"""
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT, [apiKey] TEXT,
                    [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT, [workstationId] TEXT,
                    [userId] TEXT, [langId] TEXT, [appId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris-1', 'https://polaris.example.org', 'access', '{{sourceApiKey}}',
                     'EXAMPLE', 'service-user', NULL, '99', '42', NULL, NULL, NULL, NULL);
                CREATE TABLE [request_statuses] ([id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL);
                INSERT INTO [request_statuses] VALUES ('status-suggestion', 'suggestion'), ('status-closed', 'closed');
                CREATE TABLE [request_close_reasons] ([id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL);
                INSERT INTO [request_close_reasons] VALUES ('reason-manual', 'manual');
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT, [statusRef] TEXT, [closeReason] TEXT, [closeReasonRef] TEXT,
                    [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-1', '2', 'fmt-book', 'A20000000000001', 'Event identity control', 0,
                     'closed', 'status-closed', 'manual', 'reason-manual',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL, [eventType] TEXT NOT NULL,
                    [fromStatus] TEXT, [toStatus] TEXT, [closeReason] TEXT, [actorType] TEXT NOT NULL,
                    [metadata] TEXT, [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('event-1', 'request-1', 'status_changed', {{SqlLiteral(item.FromStatus)}},
                     {{SqlLiteral(item.ToStatus)}}, {{SqlLiteral(item.CloseReason)}}, 'system', NULL,
                     '2029-01-03T00:00:00Z');
                """);

            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = Path.Combine(root, $"{item.Name}-keys");
            configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = persistedCertificate!.Thumbprint;
            File.WriteAllText(
                ExternalConfigurationPath(package),
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            return package;
        }

        try
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=ASAP Migration Event Identity Test",
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

            var packages = cases.ToDictionary(item => item.Name, CreatePackage, StringComparer.Ordinal);
            foreach (var databaseName in databaseNames.Values)
            {
                DeployDacpac(master, databaseName);
            }

            var validPackage = packages["exact"];
            var validReport = Path.Combine(root, "exact-report.json");
            Environment.SetEnvironmentVariable(connectionEnvironmentName, targets["exact"]);
            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(
                    0,
                    MigrationCli.Run(["validate", "--package", validPackage], TextWriter.Null, validationError),
                    validationError.ToString());
            }
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(validPackage, validReport, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using (var connection = new SqlConnection(targets["exact"]))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT event.[Status], event.[CloseReason] FROM [asap].[LegacyPocketBaseMapping] mapping JOIN [asap].[TitleRequestEvent] event ON event.[Id] = mapping.[NewId] WHERE mapping.[EntityType] = N'title_request_event' AND mapping.[PocketBaseId] = N'event-1';";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual("closed", reader.GetString(0));
                Assert.AreEqual("manual", reader.GetString(1));
                Assert.IsFalse(await reader.ReadAsync());
            }
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(validPackage, validReport, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "exact-keys")),
                "The exact control must exercise the credential-protection key ring before negative no-write assertions are meaningful.");

            var failures = new List<string>();
            foreach (var item in cases.Where(item => !item.IsValid))
            {
                var package = packages[item.Name];
                var report = Path.Combine(root, $"{item.Name}-report.json");
                var keyPath = Path.Combine(root, $"{item.Name}-keys");
                Environment.SetEnvironmentVariable(connectionEnvironmentName, targets[item.Name]);
                var fingerprintBefore = ComputeTargetFingerprintForTest(targets[item.Name]);
                using var validationError = new StringWriter();
                var validationExitCode = MigrationCli.Run(
                    ["validate", "--package", package],
                    TextWriter.Null,
                    validationError);
                using var importError = new StringWriter();
                var importExitCode = RunImport(package, report, connectionEnvironmentName, tenantId, importError);
                var fingerprintAfter = ComputeTargetFingerprintForTest(targets[item.Name]);

                if (validationExitCode != 1)
                {
                    failures.Add($"Public validation accepted the padded {item.Name} event relation.");
                }
                if (importExitCode != 1)
                {
                    failures.Add($"Public import did not refuse the padded {item.Name} event relation.");
                }
                if (!string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal))
                {
                    failures.Add($"Rejected {item.Name} event relation changed target SQL.");
                }
                if (File.Exists(report) || File.Exists(report + ".pending"))
                {
                    failures.Add($"Rejected {item.Name} event relation wrote or prepared a migration report.");
                }
                if (Directory.Exists(keyPath))
                {
                    failures.Add($"Rejected {item.Name} event relation wrote a Data Protection key ring.");
                }
                if (validationError.ToString().Contains(sourceApiKey, StringComparison.Ordinal) ||
                    importError.ToString().Contains(sourceApiKey, StringComparison.Ordinal))
                {
                    failures.Add($"The {item.Name} event relation diagnostic disclosed source credential material.");
                }
            }

            Assert.AreEqual(
                string.Empty,
                string.Join(Environment.NewLine, failures),
                "Event status and close-reason references must retain exact source identities; refusal must precede SQL, report, and key-ring writes.");
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
            foreach (var databaseName in databaseNames.Values)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
