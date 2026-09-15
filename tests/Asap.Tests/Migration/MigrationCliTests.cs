using Asap.Migration;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Security;

namespace Asap.Tests.Migration;

[TestClass]
public sealed class MigrationCliTests
{
    [TestMethod]
    public void HelpReportsImplementedExportImportAndReconciliation()
    {
        using var output = new StringWriter();

        var exitCode = MigrationCli.Run(["--help"], output, TextWriter.Null);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output.ToString(), "export --source");
        StringAssert.Contains(output.ToString(), "import --package");
        StringAssert.Contains(output.ToString(), "reconcile --package");
        StringAssert.Contains(output.ToString(), "Import validates a fresh target and writes a deterministic reconciliation report.");
    }

    [TestMethod]
    public void ContractPinsSourceSchemaAndImplementedCapabilities()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, MigrationCli.Run(["describe-contract"], output, TextWriter.Null));
        using var contract = JsonDocument.Parse(output.ToString());

        Assert.AreEqual(
            "150b30b776565194260cc327eeeffdfb46475e81",
            contract.RootElement.GetProperty("pocketBaseBaselineSha").GetString());
        Assert.AreEqual(5, contract.RootElement.GetProperty("expectedSchemaVersion").GetInt32());
        Assert.AreEqual(
            "CLC.ASAP",
            contract.RootElement.GetProperty("dataProtectionApplicationName").GetString());
        Assert.AreEqual(
            "CLC.ASAP.IntegrationCredential.v1",
            contract.RootElement.GetProperty("integrationCredentialPurpose").GetString());
        CollectionAssert.Contains(
            contract.RootElement.GetProperty("implementedCapabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList(),
            "stopped_sqlite_export");
        CollectionAssert.Contains(
            contract.RootElement.GetProperty("implementedCapabilities")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToList(),
            "fresh_sql_import_and_reconciliation");
    }

    [TestMethod]
    public void UnknownCommandFails()
    {
        using var error = new StringWriter();

        var exitCode = MigrationCli.Run(["not-a-command"], TextWriter.Null, error);

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(error.ToString(), "Unknown argument");
    }

    [TestMethod]
    public void ExportReadsStoppedSqliteAndWritesDeterministicHashedPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-export-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "data.db");
        var storage = Path.Combine(root, "storage");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(storage);
        try
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = source,
                    Pooling = false
                }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE [_migrations] ([file] TEXT NOT NULL PRIMARY KEY, [applied] INTEGER NOT NULL);
                    INSERT INTO [_migrations] VALUES ('202607270001_patron_code_eligibility.js', 1);
                    CREATE TABLE [polaris_organizations]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [organizationId] TEXT NOT NULL,
                        [displayName] TEXT,
                        [abbreviation] TEXT,
                        [enabledForPatrons] INTEGER NOT NULL,
                        [lastSynced] TEXT
                    );
                    INSERT INTO [polaris_organizations]
                        ([id], [organizationId], [displayName], [abbreviation], [enabledForPatrons], [lastSynced])
                    VALUES
                        ('pb-org-2', '2', 'Test Library', 'TEST', 1, '2030-01-02 03:04:05.000Z');
                    """;
                command.ExecuteNonQuery();
                CreatePinnedSourceSchemaFixtureTables(connection);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "export",
                    "--source", source,
                    "--storage", storage,
                    "--output", package,
                    "--source-git-sha", MigrationContract.PocketBaseBaselineSha,
                    "--exported-at-utc", "2030-01-02T03:04:05Z",
                    "--confirm-source-stopped"
                ],
                output,
                error);

            Assert.AreEqual(0, exitCode, error.ToString());
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
            Assert.AreEqual(
                MigrationContract.PocketBaseBaselineSha,
                manifest.RootElement.GetProperty("pocketBaseSourceGitSha").GetString());
            Assert.AreEqual(
                "202607270001_patron_code_eligibility.js",
                manifest.RootElement.GetProperty("pocketBaseSourceSchemaVersion").GetString());
            Assert.AreEqual(1, manifest.RootElement.GetProperty("entityCounts").GetProperty("polaris_organizations").GetInt32());
            Assert.IsTrue(manifest.RootElement.GetProperty("sourceDatabase").GetProperty("sha256").GetString()!.Length == 64);

            using var organizations = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(package, "organizations.json")));
            var row = organizations.RootElement
                .GetProperty("collections")
                .GetProperty("polaris_organizations")[0];
            Assert.AreEqual("2", row.GetProperty("organizationId").GetString());
            Assert.AreEqual("Test Library", row.GetProperty("displayName").GetString());

            var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.IsTrue(files.Any(file => file.GetProperty("path").GetString() == "organizations.json"));
            Assert.IsTrue(files.All(file => file.GetProperty("sha256").GetString()!.Length == 64));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExportRejectsMissingPinnedSourceCollectionBeforeWritingPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-missing-collection-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "data.db");
        var storage = Path.Combine(root, "storage");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(storage);
        try
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = source,
                    Pooling = false
                }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE [_migrations] ([file] TEXT NOT NULL PRIMARY KEY, [applied] INTEGER NOT NULL);
                    INSERT INTO [_migrations] VALUES ('202607270001_patron_code_eligibility.js', 1);
                    CREATE TABLE [polaris_organizations] ([id] TEXT NOT NULL PRIMARY KEY);
                    """;
                command.ExecuteNonQuery();
                CreatePinnedSourceSchemaFixtureTables(connection);
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TABLE [staff_users];";
                drop.ExecuteNonQuery();
            }

            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "export",
                    "--source", source,
                    "--storage", storage,
                    "--output", package,
                    "--source-git-sha", MigrationContract.PocketBaseBaselineSha,
                    "--exported-at-utc", "2030-01-02T03:04:05Z",
                    "--confirm-source-stopped"
                ],
                TextWriter.Null,
                error);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(error.ToString(), "source_collection_missing");
            StringAssert.Contains(error.ToString(), "staff_users");
            Assert.IsFalse(File.Exists(Path.Combine(package, "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ExportFreezesPinnedRuntimeAndOperationalEnvironmentResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-effective-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var environment = new Dictionary<string, string?>
        {
            ["ASAP_STAFF_URL"] = "https://legacy.example.org/staff/",
            ["ASAP_PUBLIC_URL"] = "https://ignored.example.org/",
            ["ASAP_JOB_PAGE_SIZE"] = "23",
            ["ASAP_TIMEOUT_MAX_PER_RUN"] = "301",
            ["ASAP_PENDING_SUGGESTION_ISBN_CHECKS_MAX_PER_RUN"] = "77",
            ["ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE"] = "19"
        };
        var previous = environment.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var item in environment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }

            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES ('settings0000001', '', '');
                """);

            using var runtime = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-runtime-config.json")));
            var staff = runtime.RootElement.GetProperty("settings").GetProperty("StaffApplicationUrl");
            Assert.AreEqual("https://legacy.example.org/staff/staff/", staff.GetProperty("value").GetString());
            Assert.AreEqual("environment_fallback", staff.GetProperty("provenance").GetString());
            Assert.AreEqual("ASAP_STAFF_URL", staff.GetProperty("source").GetString());

            using var operational = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-operational-config.json")));
            var limits = operational.RootElement.GetProperty("processingLimits");
            Assert.AreEqual(23, limits.GetProperty("global").GetProperty("pageSize").GetProperty("value").GetInt32());
            Assert.AreEqual(
                77,
                limits.GetProperty("effectiveQueues")
                    .GetProperty("pending_suggestion_isbn_checks")
                    .GetProperty("maxPerRun")
                    .GetProperty("value")
                    .GetInt32());
            Assert.AreEqual(
                19,
                limits.GetProperty("obsoletePathOverrides")
                    .GetProperty("pending_isbn_checks")
                    .GetProperty("pageSize")
                    .GetProperty("value")
                    .GetInt32());
            Assert.AreEqual(0, MigrationCli.Run(
                ["validate", "--package", package],
                TextWriter.Null,
                TextWriter.Null));
        }
        finally
        {
            foreach (var item in previous)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ValidationReconcilesFrozenOperationalConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-operational-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(root);
            var configuration = TestConfigurationFactory.Create();
            var configurationPath = Path.Combine(root, "asap.settings.json");
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

            using var matchedError = new StringWriter();
            Assert.AreEqual(0, MigrationCli.Run(
                ["validate", "--package", package, "--external-config", configurationPath],
                TextWriter.Null,
                matchedError), matchedError.ToString());

            configuration.Hangfire.Schedules!["WorkflowProcessing"] = "17 * * * *";
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            using var mismatchError = new StringWriter();
            Assert.AreEqual(1, MigrationCli.Run(
                ["validate", "--package", package, "--external-config", configurationPath],
                TextWriter.Null,
                mismatchError));
            StringAssert.Contains(mismatchError.ToString(), "operational_configuration_mismatch");
            StringAssert.Contains(mismatchError.ToString(), "WorkflowProcessing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExportCopiesAndHashesPocketBaseBrandingBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-branding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string collectionId = "pbc_branding_fixture";
            const string recordId = "ui-settings-record";
            const string fileName = "logo_fixture.png";
            var package = CreateMinimalPackage(
                root,
                $$"""
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES ('{{collectionId}}', 'ui_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('{{recordId}}', 'system', NULL, '{{fileName}}', 'Fixture logo');
                """,
                storage =>
                {
                    var directory = Path.Combine(storage, collectionId, recordId);
                    Directory.CreateDirectory(directory);
                    File.Copy(
                        Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"),
                        Path.Combine(directory, fileName));
                });

            using var branding = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "branding.json")));
            var row = branding.RootElement.GetProperty("collections").GetProperty("branding")[0];
            Assert.AreEqual("image/png", row.GetProperty("contentType").GetString());
            Assert.AreEqual(fileName, row.GetProperty("fileName").GetString());
            var assetPath = row.GetProperty("assetPath").GetString()!;
            CollectionAssert.AreEqual(
                File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png")),
                File.ReadAllBytes(Path.Combine(package, assetPath.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ValidateAcceptsCompletePackageAndRejectsTamperedDomainFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(root);
            using var validError = new StringWriter();
            Assert.AreEqual(
                0,
                MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validError),
                validError.ToString());

            File.AppendAllText(Path.Combine(package, "organizations.json"), " ");
            using var tamperedError = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(["validate", "--package", package], TextWriter.Null, tamperedError));
            StringAssert.Contains(tamperedError.ToString(), "package_hash_mismatch");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ExportUsesPinnedInitializationPrecedenceWhenSystemSettingsRecordIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-initialization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var environment = new Dictionary<string, string?>
        {
            ["ASAP_STAFF_URL"] = null,
            ["ASAP_BASE_URL"] = "https://base.example.org/catalog#ignored",
            ["ASAP_PUBLIC_URL"] = "https://public.example.org/"
        };
        var previous = environment.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var item in environment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }

            var package = CreateMinimalPackage(root);
            using var runtime = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-runtime-config.json")));
            var staff = runtime.RootElement.GetProperty("settings").GetProperty("StaffApplicationUrl");
            Assert.AreEqual("https://base.example.org/catalog/staff/", staff.GetProperty("value").GetString());
            Assert.AreEqual("environment_fallback", staff.GetProperty("provenance").GetString());
            Assert.AreEqual("ASAP_BASE_URL", staff.GetProperty("source").GetString());
        }
        finally
        {
            foreach (var item in previous)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ValidationRejectsRehashedMalformedConflictingAndUnsafePackageInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-package-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var traversalPackage = CreateMinimalPackage(Path.Combine(root, "traversal"));
            UpdateManifestPath(traversalPackage, "organizations.json", "../outside.json");
            AssertPackageValidationCode(traversalPackage, "package_path_invalid");

            var malformedPackage = CreateMinimalPackage(Path.Combine(root, "malformed"));
            File.WriteAllText(
                Path.Combine(malformedPackage, "organizations.json"),
                "{\"formatVersion\":1,\"domain\":\"organizations\",\"collections\":{\"polaris_organizations\":[null]}}",
                new UTF8Encoding(false));
            UpdateManifestEntry(malformedPackage, "organizations.json");
            AssertPackageValidationCode(malformedPackage, "package_domain_invalid");

            var invalidUtf8Package = CreateMinimalPackage(Path.Combine(root, "utf8"));
            File.WriteAllBytes(Path.Combine(invalidUtf8Package, "organizations.json"), [0x7b, 0xff, 0x7d]);
            UpdateManifestEntry(invalidUtf8Package, "organizations.json");
            AssertPackageValidationCode(invalidUtf8Package, "package_domain_invalid");

            var secretPackage = CreateMinimalPackage(Path.Combine(root, "secret"));
            AddRuntimeProperty(secretPackage, "secretFingerprint", "forbidden");
            UpdateManifestEntry(secretPackage, "effective-legacy-runtime-config.json");
            AssertPackageValidationCode(secretPackage, "package_secret_forbidden");

            var manifestSecretPackage = CreateMinimalPackage(Path.Combine(root, "manifest-secret"));
            AddManifestProperty(manifestSecretPackage, "postmarkToken", "fixture-only-value");
            AssertPackageValidationCode(manifestSecretPackage, "package_secret_forbidden");

            var manifestUnknownPackage = CreateMinimalPackage(Path.Combine(root, "manifest-unknown"));
            AddManifestProperty(manifestUnknownPackage, "unexpectedMember", "fixture-only-value");
            AssertPackageValidationCode(manifestUnknownPackage, "package_manifest_invalid");

            var conflictingPackage = CreateMinimalPackage(Path.Combine(root, "conflict"));
            var operationalPath = Path.Combine(conflictingPackage, "effective-legacy-operational-config.json");
            var operational = JsonNode.Parse(File.ReadAllText(operationalPath))!.AsObject();
            operational["processingLimits"]!["effectiveQueues"]!["pending_holds"]!["targetQueue"] = "IdentifierProcessing";
            File.WriteAllText(operationalPath, operational.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            UpdateManifestEntry(conflictingPackage, "effective-legacy-operational-config.json");
            AssertPackageValidationCode(conflictingPackage, "package_metadata_conflict");

            const string collectionId = "pbc_branding_validation";
            const string recordId = "ui-settings-validation";
            const string fileName = "logo_validation.png";
            var brandingPackage = CreateMinimalPackage(
                Path.Combine(root, "branding"),
                $$"""
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES ('{{collectionId}}', 'ui_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('{{recordId}}', 'system', NULL, '{{fileName}}', 'Validation logo');
                """,
                storage =>
                {
                    var directory = Path.Combine(storage, collectionId, recordId);
                    Directory.CreateDirectory(directory);
                    File.Copy(
                        Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"),
                        Path.Combine(directory, fileName));
                });
            using (var branding = JsonDocument.Parse(File.ReadAllText(Path.Combine(brandingPackage, "branding.json"))))
            {
                var assetPath = branding.RootElement.GetProperty("collections").GetProperty("branding")[0]
                    .GetProperty("assetPath").GetString()!;
                File.WriteAllBytes(Path.Combine(brandingPackage, assetPath.Replace('/', Path.DirectorySeparatorChar)), [1, 2, 3]);
                UpdateManifestEntry(brandingPackage, assetPath);
            }
            AssertPackageValidationCode(brandingPackage, "branding_asset_invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportRequiresExplicitIdentityMapAndReconcilesFreshSqlTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-import-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationImport_{Guid.NewGuid():N}";
        var secondDatabaseName = $"AsapMigrationImport_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var secondTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = secondDatabaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var secondConnectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(root);
            using var runtimeSnapshot = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-runtime-config.json")));
            var frozenStaffUrl = runtimeSnapshot.RootElement
                .GetProperty("settings")
                .GetProperty("StaffApplicationUrl")
                .GetProperty("value")
                .GetString();
            var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var objectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"{{objectId}}","userPrincipalName":"admin@example.org","displayName":"Mapped Administrator","notificationEmail":"target-notify@example.org"}]}
                """);
            var report = Path.Combine(root, "import-report.json");

            using (var dacpac = DacPackage.Load(FindDacpac()))
            {
                new DacServices(master).Deploy(
                    dacpac,
                    databaseName,
                    upgradeExisting: true,
                    new DacDeployOptions
                    {
                        BlockOnPossibleDataLoss = true,
                        CreateNewDatabase = true,
                        DropObjectsNotInSource = true
                    });
            }

            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            await using (var dirtyConnection = new SqlConnection(target))
            {
                await dirtyConnection.OpenAsync();
                await using var dirtyInsert = dirtyConnection.CreateCommand();
                dirtyInsert.CommandText =
                    """
                    INSERT INTO [asap].[QueueProgress]
                        ([QueueName], [ScopeOrganizationId], [CycleMaxId], [LastCreatedUtc], [LastItemId],
                         [LastOutcomeItemId], [LastOutcomeCode], [LastOutcomeUtc], [UpdatedUtc])
                    VALUES (N'IdentifierProcessing', 1, 42, '2030-01-02T03:04:00Z', 42,
                            42, N'processed', '2030-01-02T03:04:01Z', '2030-01-02T03:04:02Z');
                    """;
                await dirtyInsert.ExecuteNonQueryAsync();
            }
            using (var dirtyError = new StringWriter())
            {
                var dirtyExitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(root, "dirty-target-report.json")
                    ],
                    TextWriter.Null,
                    dirtyError);
                Assert.AreEqual(1, dirtyExitCode);
                StringAssert.Contains(dirtyError.ToString(), "target_not_fresh");
            }
            await using (var cleanConnection = new SqlConnection(target))
            {
                await cleanConnection.OpenAsync();
                await using var cleanDelete = cleanConnection.CreateCommand();
                cleanDelete.CommandText = "DELETE FROM [asap].[QueueProgress];";
                await cleanDelete.ExecuteNonQueryAsync();
            }
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import",
                    "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [DisplayName] = N'Test Library' AND [IsActive] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [EntraTenantId] = '{tenantId}' AND [EntraObjectId] = '{objectId}' AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
            Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] IN (N'organization', N'staff_user');"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PatronSession];"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[QueueProgress];"));
            await using (var staffUrl = connection.CreateCommand())
            {
                staffUrl.CommandText =
                    "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [StaffApplicationUrl] = @value;";
                staffUrl.Parameters.AddWithValue("@value", frozenStaffUrl!);
                Assert.AreEqual(1, Convert.ToInt32(await staffUrl.ExecuteScalarAsync()));
            }

            using var reportDocument = JsonDocument.Parse(File.ReadAllText(report));
            Assert.IsTrue(reportDocument.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            Assert.AreEqual(1, reportDocument.RootElement.GetProperty("importedCounts").GetProperty("staff_users").GetInt32());
            Assert.AreEqual(64, reportDocument.RootElement.GetProperty("targetFingerprintSha256").GetString()!.Length);
            Assert.AreEqual(64, reportDocument.RootElement.GetProperty("packageIdentitySha256").GetString()!.Length);
            Assert.AreEqual(0, reportDocument.RootElement.GetProperty("targetCounts").GetProperty("queue_progress").GetInt32());
            var staffRecipient = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                item.GetProperty("entity").GetString() == "staff_user");
            Assert.AreEqual("weekly@example.org", staffRecipient.GetProperty("sourceAssignmentRecipient").GetString());
            Assert.AreEqual("weekly@example.org", staffRecipient.GetProperty("sourcePurchaseReminderRecipient").GetString());
            Assert.AreEqual("weekly@example.org", staffRecipient.GetProperty("sourceAdditionalCopyReminderRecipient").GetString());
            Assert.AreEqual("weekly@example.org", staffRecipient.GetProperty("sourceWeeklyRecipient").GetString());
            Assert.AreEqual("target-notify@example.org", staffRecipient.GetProperty("targetAssignmentRecipient").GetString());
            Assert.AreEqual("target-notify@example.org", staffRecipient.GetProperty("targetPurchaseReminderRecipient").GetString());
            Assert.AreEqual("target-notify@example.org", staffRecipient.GetProperty("targetAdditionalCopyReminderRecipient").GetString());
            Assert.AreEqual("weekly@example.org", staffRecipient.GetProperty("targetWeeklyRecipient").GetString());
            Assert.IsTrue(staffRecipient.GetProperty("assignmentRecipientChanged").GetBoolean());
            Assert.IsTrue(staffRecipient.GetProperty("purchaseReminderRecipientChanged").GetBoolean());
            Assert.IsTrue(staffRecipient.GetProperty("additionalCopyReminderRecipientChanged").GetBoolean());
            Assert.IsFalse(staffRecipient.GetProperty("weeklyRecipientChanged").GetBoolean());
            Assert.IsFalse(staffRecipient.GetProperty("sourceWeeklyEligible").GetBoolean());
            Assert.IsTrue(staffRecipient.GetProperty("targetWeeklyEligible").GetBoolean());
            Assert.IsTrue(staffRecipient.GetProperty("newlyWeeklyEligible").GetBoolean());

            using var reconcileOutput = new StringWriter();
            using var reconcileError = new StringWriter();
            Assert.AreEqual(0, MigrationCli.Run(
                [
                    "reconcile", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--report", report
                ],
                reconcileOutput,
                reconcileError), reconcileError.ToString());
            StringAssert.Contains(reconcileOutput.ToString(), "Reconciliation succeeded");

            await using (var firstCycle = connection.CreateCommand())
            {
                firstCycle.CommandText =
                    """
                    INSERT INTO [asap].[QueueProgress]
                        ([QueueName], [ScopeOrganizationId], [CycleMaxId], [UpdatedUtc])
                    VALUES (N'IdentifierProcessing', 1, 0, '2030-01-02T03:04:05Z');
                    SELECT [CycleMaxId], [LastCreatedUtc], [LastItemId]
                    FROM [asap].[QueueProgress]
                    WHERE [QueueName] = N'IdentifierProcessing' AND [ScopeOrganizationId] = 1;
                    """;
                await using var cycleReader = await firstCycle.ExecuteReaderAsync();
                Assert.IsTrue(await cycleReader.ReadAsync());
                Assert.AreEqual(0L, cycleReader.GetInt64(0), "QueueProgress schema preserves a zero watermark for an empty cycle.");
                Assert.IsTrue(await cycleReader.IsDBNullAsync(1));
                Assert.IsTrue(await cycleReader.IsDBNullAsync(2));
            }
            using (var queueDriftError = new StringWriter())
            {
                Assert.AreEqual(1, MigrationCli.Run(
                    [
                        "reconcile", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--report", report
                    ],
                    TextWriter.Null,
                    queueDriftError));
                StringAssert.Contains(queueDriftError.ToString(), "QueueProgress runtime count changed");
            }
            await using (var clearCycle = connection.CreateCommand())
            {
                clearCycle.CommandText = "DELETE FROM [asap].[QueueProgress];";
                await clearCycle.ExecuteNonQueryAsync();
            }

            DeployDacpac(master, secondDatabaseName);
            Environment.SetEnvironmentVariable(secondConnectionEnvironmentName, secondTarget);
            var secondReport = Path.Combine(root, "second-import-report.json");
            using var secondError = new StringWriter();
            Assert.AreEqual(0, MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", secondConnectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", secondReport
                ],
                TextWriter.Null,
                secondError), secondError.ToString());
            Assert.AreEqual(
                await File.ReadAllTextAsync(report),
                await File.ReadAllTextAsync(secondReport),
                "Equivalent fresh targets must produce the same restricted reconciliation report.");

            var differentPackageRoot = Path.Combine(root, "different-package");
            Directory.CreateDirectory(differentPackageRoot);
            var differentPackage = CreateMinimalPackage(
                differentPackageRoot,
                "UPDATE [polaris_organizations] SET [displayName] = 'Different source value' WHERE [id] = 'pb-org-2';");
            using var differentPackageError = new StringWriter();
            Assert.AreEqual(1, MigrationCli.Run(
                [
                    "reconcile", "--package", differentPackage,
                    "--connection-string-env", connectionEnvironmentName,
                    "--report", report
                ],
                TextWriter.Null,
                differentPackageError));
            StringAssert.Contains(differentPackageError.ToString(), "reconciliation_report_mismatch");

            await using (var mutate = connection.CreateCommand())
            {
                mutate.CommandText = "UPDATE [asap].[Organization] SET [DisplayName] = N'Changed after import' WHERE [Id] = 2;";
                await mutate.ExecuteNonQueryAsync();
            }
            using var driftError = new StringWriter();
            Assert.AreEqual(1, MigrationCli.Run(
                [
                    "reconcile", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--report", report
                ],
                TextWriter.Null,
                driftError));
            StringAssert.Contains(driftError.ToString(), "reconciliation_failed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            Environment.SetEnvironmentVariable(secondConnectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            await DropDatabaseAsync(master, secondDatabaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportMapsFormatAndFoundRequestWithCanonicalTag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-request-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationRequest_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL,
                    [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL,
                    [sortOrder] INTEGER NOT NULL,
                    [messageBehavior] TEXT,
                    [titleMode] TEXT,
                    [titleLabel] TEXT,
                    [authorMode] TEXT,
                    [authorLabel] TEXT,
                    [identifierMode] TEXT,
                    [identifierLabel] TEXT,
                    [publicationMode] TEXT,
                    [publicationLabel] TEXT,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [material_formats] VALUES
                    ('fmt-book', 'system', NULL, 'book', 'Printed Book', 1, 7, 'none',
                     'required', 'Work title', 'required', 'Creator', 'optional', 'ISBN',
                     'required', 'Publication timing', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                INSERT INTO [staff_users] VALUES
                    ('pb-staff-2', 'selector@example.org', 'selector', 'Library Selector',
                     'staff', 1, '2', 0, NULL, 0, 1, 0);
                CREATE TABLE [format_claim_rules]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrgId] TEXT NOT NULL,
                    [format] TEXT NOT NULL,
                    [staffUserId] TEXT,
                    [active] INTEGER NOT NULL,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [format_claim_rules] VALUES
                    ('pb-rule-1', '2', 'book', 'pb-staff-2', 1,
                     '2029-02-01T00:00:00Z', '2029-02-02T00:00:00Z');
                CREATE TABLE [workflow_tags]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [code] TEXT NOT NULL,
                    [label] TEXT NOT NULL,
                    [description] TEXT
                );
                INSERT INTO [workflow_tags] VALUES
                    ('tag-found', 'Identifier found', 'Identifier found', 'Found in Polaris');
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrgId] TEXT NOT NULL,
                    [formatRef] TEXT,
                    [barcode] TEXT NOT NULL,
                    [email] TEXT,
                    [nameFirst] TEXT,
                    [nameLast] TEXT,
                    [title] TEXT NOT NULL,
                    [author] TEXT,
                    [identifier] TEXT,
                    [publication] TEXT,
                    [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL,
                    [bibid] TEXT,
                    [isbnCheckStatus] TEXT,
                    [isbnCheckResult] TEXT,
                    [isbnCheckRetryCount] INTEGER,
                    [lastChecked] TEXT,
                    [claimedByStaffUserId] TEXT,
                    [claimedByDisplayName] TEXT,
                    [claimedAt] TEXT,
                    [claimType] TEXT,
                    [claimRuleId] TEXT,
                    [created] TEXT NOT NULL,
                    [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('pb-request-1', '2', 'fmt-book', 'A20000000000001', 'patron@example.org',
                     'Ada', 'Reader', 'The Found Book', 'A. Writer', '9780000000001',
                     'Coming soon', 1, 'suggestion', 'BIB-9001', 'found', 'one match', 2,
                     '2029-03-02T12:00:00Z', 'pb-staff-2', 'Library Selector',
                     '2029-03-01T12:01:00Z', 'automatic_format_rule', 'pb-rule-1',
                     '2029-03-01T12:00:00Z', '2029-03-02T12:00:00Z');
                CREATE TABLE [title_request_tags]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [titleRequest] TEXT NOT NULL,
                    [tag] TEXT NOT NULL
                );
                INSERT INTO [title_request_tags] VALUES ('join-found', 'pb-request-1', 'tag-found');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [titleRequest] TEXT NOT NULL,
                    [eventType] TEXT NOT NULL,
                    [fromStatus] TEXT,
                    [toStatus] TEXT,
                    [closeReason] TEXT,
                    [actorType] TEXT,
                    [actorName] TEXT,
                    [message] TEXT,
                    [metadata] TEXT,
                    [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('event-placed', 'pb-request-1', 'status_changed', 'pending_hold', 'hold_placed',
                     NULL, 'staff', 'Library Selector', 'Existing hold adopted.',
                     '{"bibId":"BIB-9001"}', '2029-03-01T13:00:00Z');
                CREATE TABLE [email_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [templateKey] TEXT NOT NULL,
                    [name] TEXT,
                    [subject] TEXT,
                    [body] TEXT,
                    [enabled] INTEGER NOT NULL
                );
                INSERT INTO [email_templates] VALUES
                    ('template-submitted', 'system', NULL, 'suggestion_submitted', 'Submission receipt',
                     'Received: {{title}}', '<p>Hello {{name}}</p><p>{{title}} by {{author}}</p>', 1);
                CREATE TABLE [email_delivery_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [titleRequest] TEXT,
                    [emailTemplate] TEXT,
                    [templateKey] TEXT,
                    [recipient] TEXT,
                    [subject] TEXT,
                    [status] TEXT,
                    [error] TEXT,
                    [metadata] TEXT,
                    [created] TEXT NOT NULL
                );
                INSERT INTO [email_delivery_events] VALUES
                    ('mail-history-1', 'pb-request-1', 'template-submitted', 'suggestion_submitted',
                     'patron@example.org', 'Received: The Found Book', 'sent', NULL,
                     '{"legacyProvider":"smtp"}', '2029-03-01T12:05:00Z');
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [leapBibUrlPattern] TEXT,
                    [leapPatronUrlPattern] TEXT,
                    [formatIconUrlPattern] TEXT,
                    [patronEmbedAllowedOrigins] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings0000001', 'https://staff.example.org/staff/', 'https://leap.example/bib/{{bibId}}',
                     'https://leap.example/patron/{{barcode}}', 'https://icons.example/{{code}}.png',
                     'HTTPS://*.Library.Example.Invalid:443, http://LOCALHOST:1234/, https://Exact.Example.Invalid/');
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [host] TEXT,
                    [accessId] TEXT,
                    [apiKey] TEXT,
                    [staffDomain] TEXT,
                    [adminUser] TEXT,
                    [adminPassword] TEXT,
                    [workstationId] TEXT,
                    [userId] TEXT,
                    [requestingOrgId] TEXT,
                    [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris00000010', 'https://polaris.example.org', 'SuggestAPI', '', 'EXAMPLE',
                     'svc-asap', '', '99', '42', '7', '3');
                CREATE TABLE [workflow_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [suggestionLimit] INTEGER,
                    [suggestionLimitMessage] TEXT,
                    [outstandingTimeoutEnabled] INTEGER,
                    [outstandingTimeoutDays] INTEGER,
                    [outstandingTimeoutSendEmail] INTEGER,
                    [holdPickupTimeoutEnabled] INTEGER,
                    [holdPickupTimeoutDays] INTEGER,
                    [pendingHoldTimeoutEnabled] INTEGER,
                    [pendingHoldTimeoutDays] INTEGER,
                    [additionalCopyTimeoutEnabled] INTEGER,
                    [additionalCopyTimeoutDays] INTEGER,
                    [autoPromote] INTEGER,
                    [commonAuthorsEnabled] INTEGER,
                    [commonAuthorsList] TEXT,
                    [commonAuthorsLabel] TEXT,
                    [commonAuthorsHelp] TEXT,
                    [commonAuthorsMessage] TEXT,
                    [allowPatronAutoholdOptOut] INTEGER,
                    [allowAnyRegisteredCardLogin] INTEGER,
                    [patronCodeEligibilityEnabled] INTEGER,
                    [allowedPatronCodeIds] TEXT,
                    [patronCodeEligibilityMessage] TEXT,
                    [externalSearch1Enabled] INTEGER,
                    [externalSearch1Label] TEXT,
                    [externalSearch1UrlTemplate] TEXT,
                    [externalSearch2Enabled] INTEGER,
                    [externalSearch2Label] TEXT,
                    [externalSearch2UrlTemplate] TEXT,
                    [externalSearch3Enabled] INTEGER,
                    [externalSearch3Label] TEXT,
                    [externalSearch3UrlTemplate] TEXT,
                    [externalSearch4Enabled] INTEGER,
                    [externalSearch4Label] TEXT,
                    [externalSearch4UrlTemplate] TEXT,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [workflow_settings] VALUES
                    ('workflow-system', 'system', NULL, 6, 'Try after {{next_available_date}}.',
                     1, 31, 0, 1, 15, 1, 16, 1, 17, 0,
                     1, 'Octavia Butler,Ursula Le Guin', 'Collected creators', 'Check first.', 'Already collected.',
                     1, 0, 1, '7, 9', 'Card not eligible.',
                     1, 'Search Catalog', 'https://catalog.example/search?q={{title}}',
                     0, 'Search Two', 'https://two.example/{{title}}',
                     1, 'Search Three', 'https://three.example/{{title}}',
                     0, '', '', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z'),
                    ('workflow-library', 'library', 'pb-org-2', 8, 'Local weekly limit.',
                     0, NULL, 0, 0, NULL, 0, NULL, 0, NULL, 1,
                     0, '', NULL, NULL, NULL, 0, 1, 0, '', NULL,
                     0, 'Local Catalog', 'https://local.example/{{title}}',
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                     '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES ('pbc_ui_settings', 'ui_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [pageTitle] TEXT,
                    [barcodeLabel] TEXT,
                    [pinLabel] TEXT,
                    [loginPrompt] TEXT,
                    [loginNote] TEXT,
                    [suggestionFormNote] TEXT,
                    [noEmailMessage] TEXT,
                    [successTitle] TEXT,
                    [successMessage] TEXT,
                    [alreadySubmittedMessage] TEXT,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT,
                    [duplicateLabelSuggestion] TEXT,
                    [publicationOptions] TEXT,
                    [systemNotEnabledMessage] TEXT,
                    [logo] TEXT,
                    [logoAlt] TEXT,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('ui-system', 'system', NULL, 'Suggest an Item', 'Card number', 'PIN',
                     '<p>Sign in to suggest.</p>', '<p>Have your card ready.</p>', '<p>One title per form.</p>',
                     '<p>Add an email for updates.</p>', 'Thank you', '<p>Received.</p>',
                     '<p>Already received {{duplicate_date}}.</p>', '<p>Use Libby.</p>', '<p>Use Libby audio.</p>',
                     'Received locally',
                     'Already published
                     Coming soon
                     Published a while back',
                     '{{library}} is paused.', 'migration_logo.png', 'Consortium logo',
                     '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [duplicateStatusLabels] TEXT,
                    [publicationOptions] TEXT,
                    [patronFormatRules] TEXT,
                    [additionalFieldDefinitions] TEXT,
                    [ebookMessage] TEXT,
                    [eaudiobookMessage] TEXT,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('patron-override-2', '2', '{"suggestion":"Local received"}',
                     '[{"id":"local_preorder","label":"Local preorder","enabled":true,"sortOrder":10}]',
                     '{"book":{"customFields":{"audience":{"mode":"required","labelOverride":"Who is it for?"}}}}',
                     '[{"key":"audience","label":"Audience","type":"select","enabled":true,"sortOrder":10,"options":[{"id":"adult","label":"Adult","enabled":true,"sortOrder":10}]}]',
                     NULL, NULL, '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                CREATE TABLE [smtp_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [fromAddress] TEXT,
                    [fromName] TEXT,
                    [created] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [smtp_settings] VALUES
                    ('smtp-system', 'notices@example.org', 'ASAP Notices',
                     '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                """,
                storage =>
                {
                    var directory = Path.Combine(storage, "pbc_ui_settings", "ui-system");
                    Directory.CreateDirectory(directory);
                    File.Copy(
                        Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"),
                        Path.Combine(directory, "migration_logo.png"));
                });
            var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[
                  {"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"},
                  {"pocketBaseStaffUserId":"pb-staff-2","tenantId":"{{tenantId}}","objectId":"22222222-2222-2222-2222-222222222222","userPrincipalName":"selector@example.org"}
                ]}
                """);
            var report = Path.Combine(root, "import-report.json");
            DeployDacpac(master, databaseName);

            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report
                ],
                TextWriter.Null,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book' AND [Label] = N'Printed Book' AND [SortOrder] = 7;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[LegacyId] IS NULL AND r.[LibraryOrganizationId] = 2 AND r.[Status] = N'suggestion' AND r.[IsbnCheckStatus] = N'found' AND r.[BibId] = N'BIB-9001' AND r.[Title] = N'The Found Book';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag] j JOIN [asap].[WorkflowTag] t ON t.[Id] = j.[WorkflowTagId] WHERE t.[Code] = N'polaris_bib_found';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[FormatAutoClaimRule] c ON c.[Id] = r.[ClaimRuleId] AND c.[StaffUserId] = r.[ClaimedByStaffUserId] WHERE r.[ClaimType] = N'automatic_format_rule' AND c.[IsActive] = 1 AND c.[LibraryOrganizationId] = 2;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'status_changed' AND [Status] = N'hold_placed' AND [ActorType] = N'staff';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'status_changed' AND [CreatedUtc] = '2029-03-01T13:00:00';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND [CreatedUtc] = '2030-01-02T03:04:05' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true' AND JSON_VALUE([MetadataJson], '$.bibId') = N'BIB-9001' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted' AND [SubjectTemplate] = N'Received: {{title}}' AND [BodyTemplate] LIKE N'%{{name}}%';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IS NULL AND [EventType] = N'sent' AND JSON_VALUE([MetadataJson], '$.sourceRecordId') = N'mail-history-1' AND JSON_VALUE([MetadataJson], '$.recipient') = N'patron@example.org';"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [StaffApplicationUrl] = N'https://staff.example.org/staff/' AND [SystemNotEnabledMessage] = N'{{library}} is paused.';"));
            Assert.AreEqual(3, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin] WHERE [NormalizedOrigin] IN (N'https://*.library.example.invalid:443', N'http://localhost:1234', N'https://exact.example.invalid');"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2 AND [SuggestionLimit] = 8 AND [AutoPromote] = 1 AND [AllowPatronAutoholdOptOut] = 0;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NULL AND [FromAddress] = N'notices@example.org' AND [FromName] = N'ASAP Notices';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder' AND [Label] = N'Local preorder';"));
            Assert.AreEqual(3, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 1 AND [OptionKey] IN (N'already-published', N'coming-soon', N'published-a-while-back');"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1' AND o.[IsEnabled] = 0 AND o.[Label] = N'Local Catalog';"));
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND r.[Mode] = N'required' AND r.[LabelOverride] = N'Who is it for?';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 1 AND [LogoContentType] = N'image/png' AND [LogoFileName] = N'migration_logo.png' AND DATALENGTH([LogoData]) > 1000 AND [LogoAltText] = N'Consortium logo';"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportNormalizesLegacyIdentifierStatesAndRejectsAmbiguousStatesAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-isbn-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationIsbn_{Guid.NewGuid():N}";
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
        try
        {
            DeployDacpac(master, databaseName);
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var invalidCases = new[]
            {
                (Name: "found_without_bib", Status: "found", Identifier: "9780000000001", Bib: "NULL", Error: "identifier_found_without_bib"),
                (Name: "alias_without_bib", Status: "found_in_polaris", Identifier: "9780000000002", Bib: "NULL", Error: "identifier_found_without_bib"),
                (Name: "ambiguous_error", Status: "error", Identifier: "9780000000003", Bib: "NULL", Error: "identifier_error_ambiguous"),
                (Name: "unknown", Status: "mystery", Identifier: "NULL", Bib: "NULL", Error: "identifier_status_invalid")
            };
            foreach (var item in invalidCases)
            {
                var caseRoot = Path.Combine(root, item.Name);
                Directory.CreateDirectory(caseRoot);
                var identifier = item.Identifier == "NULL" ? "NULL" : $"'{item.Identifier}'";
                var package = CreateMinimalPackage(
                    caseRoot,
                    $$"""
                    CREATE TABLE [material_formats]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                        [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                        [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                    );
                    INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                    CREATE TABLE [title_requests]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                        [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL,
                        [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [identifier] TEXT,
                        [bibid] TEXT, [isbnCheckStatus] TEXT, [isbnCheckRetryCount] INTEGER,
                        [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                    );
                    INSERT INTO [title_requests] VALUES
                        ('request-1', '2', 'fmt-book', 'A20000000000001', 'Invalid identifier state',
                         0, 'suggestion', {{identifier}}, {{item.Bib}}, '{{item.Status}}', 3,
                         '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                    """);
                using var error = new StringWriter();

                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(caseRoot, "report.json")
                    ],
                    TextWriter.Null,
                    error);

                Assert.AreEqual(1, exitCode, item.Name);
                StringAssert.Contains(error.ToString(), item.Error, item.Name);
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];"), item.Name);
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"), item.Name);
            }

            var validRoot = Path.Combine(root, "valid");
            Directory.CreateDirectory(validRoot);
            var validPackage = CreateMinimalPackage(
                validRoot,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                    [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL,
                    [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [identifier] TEXT,
                    [bibid] TEXT, [isbnCheckStatus] TEXT, [isbnCheckRetryCount] INTEGER,
                    [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-1', '2', 'fmt-book', 'A20000000000001', 'Absent null', 0, 'suggestion', NULL, NULL, NULL, 0, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-2', '2', 'fmt-book', 'A20000000000002', 'Absent blank', 0, 'suggestion', NULL, NULL, '', 0, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-3', '2', 'fmt-book', 'A20000000000003', 'Pending', 0, 'suggestion', '9780000000003', NULL, 'pending', 2, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-4', '2', 'fmt-book', 'A20000000000004', 'Found', 0, 'suggestion', '9780000000004', 'BIB-4', 'found', 3, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-5', '2', 'fmt-book', 'A20000000000005', 'Not found', 0, 'suggestion', '9780000000005', NULL, 'not_found', 4, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-6', '2', 'fmt-book', 'A20000000000006', 'Skipped', 0, 'suggestion', NULL, NULL, 'skipped_no_isbn', 9, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-7', '2', 'fmt-book', 'A20000000000007', 'Exhausted', 0, 'suggestion', '9780000000007', NULL, 'error_max_retries', 5, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-8', '2', 'fmt-book', 'A20000000000008', 'Missing identifier error', 0, 'suggestion', NULL, NULL, 'error', 7, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-9', '2', 'fmt-book', 'A20000000000009', 'Historical alias', 0, 'suggestion', '9780000000009', 'BIB-9', 'found_in_polaris', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            using var validError = new StringWriter();
            var validExitCode = MigrationCli.Run(
                [
                    "import", "--package", validPackage,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", Path.Combine(validRoot, "report.json")
                ],
                TextWriter.Null,
                validError);
            Assert.AreEqual(0, validExitCode, validError.ToString());

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] IS NULL;"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'Pending' AND [IsbnCheckStatus] = N'pending' AND [IsbnCheckRetryCount] = 2;"));
                Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'found';"));
                Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[TitleRequestWorkflowTag] j ON j.[TitleRequestId] = r.[Id] JOIN [asap].[WorkflowTag] t ON t.[Id] = j.[WorkflowTagId] WHERE r.[IsbnCheckStatus] = N'found' AND t.[Code] = N'polaris_bib_found';"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'Not found' AND [IsbnCheckStatus] = N'not_found' AND [IsbnCheckRetryCount] = 4;"));
                Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'skipped_no_isbn' AND [IsbnCheckRetryCount] = 0;"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [IsbnCheckStatus] = N'error_max_retries' AND [IsbnCheckRetryCount] = 5 AND [IsbnCheckLastErrorCode] = N'legacy_retry_exhausted';"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportNormalizesOperationalClaimsAndRulesWhilePreservingDormantLibraryHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-claims-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationClaims_{Guid.NewGuid():N}";
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
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                UPDATE [polaris_organizations] SET [enabledForPatrons] = 0 WHERE [id] = 'pb-org-2';
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1);
                INSERT INTO [staff_users] VALUES
                    ('pb-staff-same', 'same@example.org', 'same', 'Same Library', 'staff', 1, '2', 0, NULL, 0, 0, 0),
                    ('pb-staff-inactive', 'foreign@staff.asap.local', 'inactive', 'Inactive Selector', 'staff', 0, '2', 0, NULL, 0, 0, 0),
                    ('pb-staff-other', 'other@example.org', 'other', 'Other Library', 'admin', 1, '3', 0, 'not-an-email', 0, 0, 0);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [format_claim_rules]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                    [format] TEXT NOT NULL, [staffUserId] TEXT, [active] INTEGER NOT NULL,
                    [created] TEXT, [updated] TEXT
                );
                INSERT INTO [format_claim_rules] VALUES
                    ('rule-same', '2', 'book', 'pb-staff-same', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-same-inactive', '2', 'book', 'pb-staff-same', 0, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-other', '2', 'book', 'pb-staff-other', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('rule-unmapped', '2', 'book', 'missing-staff', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                    [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL,
                    [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [closeReason] TEXT,
                    [isbnCheckStatus] TEXT, [claimedByStaffUserId] TEXT,
                    [claimedByDisplayName] TEXT, [claimedAt] TEXT, [claimType] TEXT,
                    [claimRuleId] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-same', '2', 'fmt-book', 'A20000000000001', 'Same library', 0, 'suggestion', NULL, NULL, 'pb-staff-same', 'Same Library', '2029-02-01T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-inactive', '2', 'fmt-book', 'A20000000000002', 'Inactive claimant', 0, 'outstanding_purchase', NULL, NULL, 'pb-staff-inactive', 'Inactive Selector', '2029-02-02T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-outscope', '2', 'fmt-book', 'A20000000000003', 'Out of scope', 0, 'pending_hold', NULL, NULL, 'pb-staff-other', 'Other Library', '2029-02-03T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-super', '2', 'fmt-book', 'A20000000000004', 'Cross library administrator', 0, 'hold_placed', NULL, NULL, 'pb-staff-1', 'Source Administrator', '2029-02-04T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-auto', '2', 'fmt-book', 'A20000000000005', 'Automatic rule', 0, 'suggestion', NULL, NULL, 'pb-staff-same', 'Same Library', '2029-02-05T10:00:00Z', 'automatic_format_rule', 'rule-same', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-unmapped', '2', 'fmt-book', 'A20000000000006', 'Missing claimant', 0, 'hold_placed', NULL, NULL, 'missing-staff', 'Former Selector', '2029-02-06T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-closed', '2', 'fmt-book', 'A20000000000007', 'Closed history', 0, 'closed', 'rejected', NULL, 'pb-staff-inactive', 'Inactive Selector', '2029-02-07T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-auto-inactive-rule', '2', 'fmt-book', 'A20000000000008', 'Inactive historical rule', 0, 'suggestion', NULL, NULL, 'pb-staff-same', 'Same Library', '2029-02-08T10:00:00Z', 'automatic_format_rule', 'rule-same-inactive', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-metadata-open', '2', 'fmt-book', 'A20000000000009', 'Open metadata only', 0, 'suggestion', NULL, NULL, NULL, 'Former Selector', '2029-02-09T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-metadata-closed', '2', 'fmt-book', 'A20000000000010', 'Closed metadata only', 0, 'closed', 'manual', NULL, NULL, 'Former Selector', '2029-02-10T10:00:00Z', 'manual', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[
                  {"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"},
                  {"pocketBaseStaffUserId":"pb-staff-same","tenantId":"{{tenantId}}","objectId":"22222222-2222-2222-2222-222222222222","userPrincipalName":"same@example.org"},
                  {"pocketBaseStaffUserId":"pb-staff-other","tenantId":"{{tenantId}}","objectId":"33333333-3333-3333-3333-333333333333","userPrincipalName":"other@example.org"}
                ]}
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var report = Path.Combine(root, "report.json");
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report
                ],
                TextWriter.Null,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(4, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Status] <> N'closed' AND [ClaimedByStaffUserId] IS NOT NULL;"));
            Assert.AreEqual(4, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Status] <> N'closed' AND [ClaimedByStaffUserId] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'Closed history' AND [Status] = N'closed' AND [ClaimedByStaffUserId] IS NOT NULL AND [ClaimedByDisplayName] = N'Inactive Selector' AND [ClaimedAtUtc] = '2029-02-07T10:00:00Z';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'Inactive historical rule' AND [ClaimedByStaffUserId] IS NOT NULL AND [ClaimType] = N'automatic_format_rule' AND [ClaimRuleId] IS NOT NULL;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'Closed metadata only' AND [ClaimedByStaffUserId] IS NULL AND [ClaimedByDisplayName] = N'Former Selector' AND [ClaimedAtUtc] = '2029-02-10T10:00:00Z' AND [ClaimType] = N'manual';"));
            Assert.AreEqual(4, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.transform') = N'claim_attribution_normalization_v1';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[TitleRequest] r ON r.[Id] = e.[TitleRequestId] WHERE r.[Title] = N'Open metadata only' AND JSON_VALUE(e.[MetadataJson], '$.sourceDisplayName') = N'Former Selector';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [IsActive] = 1 AND [StaffUserId] IS NOT NULL;"));
            Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [IsActive] = 0 AND [StaffUserId] IS NOT NULL;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [IsActive] = 0 AND [StaffUserId] IS NULL;"));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.AreEqual(0, document.RootElement.GetProperty("targetCounts").GetProperty("invalid_open_title_request_claims").GetInt32());
            var inactiveTransform = document.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                item.GetProperty("entity").GetString() == "title_request_claim" &&
                item.GetProperty("sourceId").GetString() == "request-inactive");
            Assert.AreEqual("Inactive Selector", inactiveTransform.GetProperty("sourceDisplayName").GetString());
            Assert.AreEqual("2029-02-02T10:00:00Z", inactiveTransform.GetProperty("sourceClaimedAtUtc").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
            Assert.AreEqual("manual", inactiveTransform.GetProperty("sourceClaimType").GetString());
            Assert.AreEqual("claimant_inactive", inactiveTransform.GetProperty("reason").GetString());
            var placeholderRecipient = document.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                item.GetProperty("entity").GetString() == "staff_user" &&
                item.GetProperty("sourceId").GetString() == "pb-staff-inactive");
            Assert.AreEqual("foreign@staff.asap.local", placeholderRecipient.GetProperty("sourceAssignmentRecipient").GetString());
            Assert.AreEqual(JsonValueKind.Null, placeholderRecipient.GetProperty("targetAssignmentRecipient").ValueKind);
            Assert.IsTrue(placeholderRecipient.GetProperty("assignmentRecipientChanged").GetBoolean());
            var invalidWeeklyRecipient = document.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                item.GetProperty("entity").GetString() == "staff_user" &&
                item.GetProperty("sourceId").GetString() == "pb-staff-other");
            Assert.AreEqual("not-an-email", invalidWeeklyRecipient.GetProperty("sourceAssignmentRecipient").GetString());
            Assert.AreEqual("not-an-email", invalidWeeklyRecipient.GetProperty("sourcePurchaseReminderRecipient").GetString());
            Assert.AreEqual("other@example.org", invalidWeeklyRecipient.GetProperty("targetAssignmentRecipient").GetString());
            var metadataTransform = document.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                item.GetProperty("entity").GetString() == "title_request_claim" &&
                item.GetProperty("sourceId").GetString() == "request-metadata-open");
            Assert.AreEqual(JsonValueKind.Null, metadataTransform.GetProperty("sourceClaimantId").ValueKind);
            Assert.AreEqual("Former Selector", metadataTransform.GetProperty("sourceDisplayName").GetString());
            Assert.AreEqual("claimant_unmapped", metadataTransform.GetProperty("reason").GetString());
            Assert.IsTrue(metadataTransform.GetProperty("migrationAnnotationInserted").GetBoolean());
            var summary = document.RootElement.GetProperty("claimReconciliation").GetProperty("titleRequests").EnumerateArray().ToArray();
            Assert.IsTrue(summary.Any(item => item.GetProperty("outcome").GetString() == "cleared" && item.GetProperty("reason").GetString() == "claimant_inactive" && item.GetProperty("count").GetInt32() == 1));
            Assert.IsTrue(summary.Any(item => item.GetProperty("outcome").GetString() == "closed_history" && item.GetProperty("reason").GetString() == "closed_history_preserved" && item.GetProperty("count").GetInt32() == 1));
            Assert.IsTrue(summary.Any(item => item.GetProperty("outcome").GetString() == "closed_history" && item.GetProperty("reason").GetString() == "closed_claimant_unmapped" && item.GetProperty("count").GetInt32() == 1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportReconcilesTheCompletePlacedHistoryEvidenceUnionAndBlocksConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-placement-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPlacement_{Guid.NewGuid():N}";
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
        try
        {
            DeployDacpac(master, databaseName);
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var conflictCases = new[]
            {
                (
                    Name: "status",
                    Error: "request_status_conflict",
                    Sql:
                    """
                    CREATE TABLE [request_statuses] ([id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL);
                    INSERT INTO [request_statuses] VALUES ('status-suggestion', 'suggestion');
                    CREATE TABLE [material_formats] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL);
                    INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                    CREATE TABLE [title_requests] ([id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL, [status] TEXT, [statusRef] TEXT, [isbnCheckStatus] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL);
                    INSERT INTO [title_requests] VALUES ('request-1', '2', 'fmt-book', 'A20000000000001', 'Conflict', 0, 'hold_placed', 'status-suggestion', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                    """)
            };

            foreach (var item in conflictCases)
            {
                var caseRoot = Path.Combine(root, item.Name);
                Directory.CreateDirectory(caseRoot);
                var package = CreateMinimalPackage(caseRoot, item.Sql);
                using var error = new StringWriter();
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(caseRoot, "report.json")
                    ],
                    TextWriter.Null,
                    error);
                Assert.AreEqual(1, exitCode, item.Name);
                StringAssert.Contains(error.ToString(), item.Error, item.Name);
            }

            var bibConflictRoot = Path.Combine(root, "bib");
            Directory.CreateDirectory(bibConflictRoot);
            var bibConflictPackage = CreateMinimalPackage(
                bibConflictRoot,
                """
                CREATE TABLE [material_formats] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL);
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests] ([id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [bibid] TEXT, [isbnCheckStatus] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL);
                INSERT INTO [title_requests] VALUES ('request-1', '2', 'fmt-book', 'A20000000000001', 'BIB conflict', 0, 'hold_placed', 'BIB-A', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events] ([id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL, [eventType] TEXT NOT NULL, [actorType] TEXT NOT NULL, [metadata] TEXT, [created] TEXT NOT NULL);
                INSERT INTO [title_request_events] VALUES ('event-1', 'request-1', 'hold_placed', 'system', '{"bibId":"BIB-B"}', '2029-01-03T00:00:00Z');
                """);
            using (var bibError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", bibConflictPackage,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(bibConflictRoot, "report.json")
                    ],
                    TextWriter.Null,
                    bibError);
                Assert.AreEqual(1, exitCode);
                StringAssert.Contains(bibError.ToString(), "placed_bib_conflict");
            }

            await using (var rollbackConnection = new SqlConnection(target))
            {
                await rollbackConnection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(rollbackConnection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"));
                Assert.AreEqual(0, await ScalarAsync(rollbackConnection, "SELECT COUNT(*) FROM [asap].[StaffUser];"));
            }

            var validRoot = Path.Combine(root, "valid");
            Directory.CreateDirectory(validRoot);
            var validPackage = CreateMinimalPackage(
                validRoot,
                """
                CREATE TABLE [request_statuses] ([id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL);
                INSERT INTO [request_statuses] VALUES ('status-closed', 'closed'), ('status-placed', 'hold_placed');
                CREATE TABLE [request_close_reasons] ([id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL);
                INSERT INTO [request_close_reasons] VALUES
                    ('reason-completed', 'hold_completed'), ('reason-not-picked', 'hold_not_picked_up'),
                    ('reason-unclaimed', 'hold_unclaimed'), ('reason-cancelled', 'hold_cancelled'),
                    ('reason-expired', 'hold_expired'), ('reason-manual', 'manual'), ('reason-rejected', 'rejected');
                CREATE TABLE [material_formats] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL);
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT, [statusRef] TEXT, [closeReason] TEXT, [closeReasonRef] TEXT,
                    [bibid] TEXT, [isbnCheckStatus] TEXT, [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-current', '2', 'fmt-book', 'A20000000000001', 'Current placed', 0, 'hold_placed', 'status-placed', NULL, NULL, 'BIB-1', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-completed', '2', 'fmt-book', 'A20000000000002', 'Completed', 0, 'closed', 'status-closed', 'hold_completed', 'reason-completed', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-not-picked', '2', 'fmt-book', 'A20000000000003', 'Not picked up', 0, 'closed', 'status-closed', 'hold_not_picked_up', 'reason-not-picked', 'BIB-3', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-unclaimed', '2', 'fmt-book', 'A20000000000004', 'Unclaimed', 0, 'closed', 'status-closed', 'hold_unclaimed', 'reason-unclaimed', 'BIB-4', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-cancelled', '2', 'fmt-book', 'A20000000000005', 'Cancelled', 0, 'closed', 'status-closed', 'hold_cancelled', 'reason-cancelled', 'BIB-5', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-expired', '2', 'fmt-book', 'A20000000000006', 'Expired', 0, 'closed', 'status-closed', 'hold_expired', 'reason-expired', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-dedicated', '2', 'fmt-book', 'A20000000000007', 'Dedicated event', 0, 'closed', 'status-closed', 'manual', 'reason-manual', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-to', '2', 'fmt-book', 'A20000000000008', 'Transition to', 0, 'closed', 'status-closed', 'manual', 'reason-manual', 'BIB-8', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-from', '2', 'fmt-book', 'A20000000000009', 'Transition from', 0, 'closed', 'status-closed', 'manual', 'reason-manual', 'BIB-9', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-event-terminal', '2', 'fmt-book', 'A20000000000010', 'Event terminal', 0, 'closed', 'status-closed', 'manual', 'reason-manual', 'BIB-10', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-none', '2', 'fmt-book', 'A20000000000011', 'No placement evidence', 0, 'closed', 'status-closed', 'rejected', 'reason-rejected', 'BIB-11', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL,
                    [eventType] TEXT NOT NULL, [fromStatus] TEXT, [toStatus] TEXT,
                    [closeReason] TEXT, [actorType] TEXT NOT NULL, [metadata] TEXT,
                    [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('event-dedicated', 'request-dedicated', 'hold_placed', NULL, NULL, NULL, 'system', '{"bibId":"BIB-7"}', '2029-02-01T00:00:00Z'),
                    ('event-to', 'request-to', 'status_changed', 'pending_hold', 'status-placed', NULL, 'staff', NULL, '2029-02-02T00:00:00Z'),
                    ('event-from', 'request-from', 'legacy_departure', 'status-placed', 'status-closed', NULL, 'system', NULL, '2029-02-03T00:00:00Z'),
                    ('event-terminal', 'request-event-terminal', 'legacy_terminal', NULL, NULL, 'reason-completed', 'system', NULL, '2029-02-04T00:00:00Z');
                """);
            var report = Path.Combine(validRoot, "report.json");
            using var validError = new StringWriter();
            var validExitCode = MigrationCli.Run(
                [
                    "import", "--package", validPackage,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report
                ],
                TextWriter.Null,
                validError);
            Assert.AreEqual(0, validExitCode, validError.ToString());

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(10, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true';"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[TitleRequest] r ON r.[Id] = e.[TitleRequestId] WHERE r.[Title] = N'Dedicated event' AND JSON_VALUE(e.[MetadataJson], '$.bibId') = N'BIB-7';"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[TitleRequest] r ON r.[Id] = e.[TitleRequestId] WHERE r.[Title] = N'No placement evidence' AND JSON_VALUE(e.[MetadataJson], '$.legacyBibProtection') = N'true';"));
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var placement = document.RootElement.GetProperty("placementReconciliation");
            Assert.AreEqual(11, placement.GetProperty("sourceRequestsEvaluated").GetInt32());
            Assert.AreEqual(10, placement.GetProperty("protectedRequests").GetInt32());
            Assert.AreEqual(8, placement.GetProperty("knownBibMarkers").GetInt32());
            Assert.AreEqual(2, placement.GetProperty("explicitNullBibMarkers").GetInt32());
            Assert.AreEqual(1, placement.GetProperty("noPlacementEvidence").GetInt32());
            Assert.AreEqual(0, placement.GetProperty("fabricatedHoldPlacementOperations").GetInt32());
            var terminalReasons = placement.GetProperty("terminalReasons").EnumerateArray().ToDictionary(
                item => item.GetProperty("reason").GetString()!,
                item => item.GetProperty("count").GetInt32());
            CollectionAssert.IsSubsetOf(
                new[] { "hold_completed", "hold_not_picked_up", "hold_unclaimed", "hold_cancelled", "hold_expired" },
                terminalReasons.Keys.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportBlocksMissingOrInvalidHistoricalEventTimesAndRollsBack()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-event-time-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEventTime_{Guid.NewGuid():N}";
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
        try
        {
            DeployDacpac(master, databaseName);
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var cases = new[]
            {
                (Name: "missing", Column: "", Value: "", Error: "request_event_created_missing"),
                (Name: "null", Column: ", [created] TEXT", Value: ", NULL", Error: "request_event_created_missing"),
                (Name: "blank", Column: ", [created] TEXT", Value: ", '   '", Error: "request_event_created_missing"),
                (Name: "malformed", Column: ", [created] TEXT", Value: ", 'not-a-time'", Error: "source_value_invalid")
            };

            foreach (var item in cases)
            {
                var caseRoot = Path.Combine(root, item.Name);
                Directory.CreateDirectory(caseRoot);
                var package = CreateMinimalPackage(
                    caseRoot,
                    $$"""
                    CREATE TABLE [material_formats]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                        [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                        [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                    );
                    INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                    CREATE TABLE [title_requests]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL,
                        [formatRef] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL,
                        [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [isbnCheckStatus] TEXT,
                        [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                    );
                    INSERT INTO [title_requests] VALUES
                        ('request-1', '2', 'fmt-book', 'A20000000000001', 'Historical title',
                         0, 'suggestion', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                    CREATE TABLE [title_request_events]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL,
                        [eventType] TEXT NOT NULL, [actorType] TEXT{{item.Column}}
                    );
                    INSERT INTO [title_request_events] VALUES
                        ('event-1', 'request-1', 'created', 'system'{{item.Value}});
                    """);
                var report = Path.Combine(caseRoot, "report.json");
                using var error = new StringWriter();

                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", report
                    ],
                    TextWriter.Null,
                    error);

                Assert.AreEqual(1, exitCode, item.Name);
                StringAssert.Contains(error.ToString(), item.Error, item.Name);
                Assert.IsFalse(File.Exists(report), item.Name);
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];"), item.Name);
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"), item.Name);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportRejectsUnsafeEmbedOriginsAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-embed-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationEmbed_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);

            foreach (var origin in new[]
                     {
                         "http://remote.example.org",
                         "https://user@example.org",
                         "https://example.org/path",
                         "https://example.org?query=1",
                         "http://*.example.org"
                     })
            {
                var packageRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(packageRoot);
                var package = CreateMinimalPackage(
                    packageRoot,
                    $$"""
                    CREATE TABLE [system_settings]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [patronEmbedAllowedOrigins] TEXT
                    );
                    INSERT INTO [system_settings] VALUES ('settings0000001', '{{origin}}');
                    """);
                using var error = new StringWriter();

                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--staff-identity-map", identityMap,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(packageRoot, "report.json")
                    ],
                    TextWriter.Null,
                    error);

                Assert.AreEqual(1, exitCode, origin);
                StringAssert.Contains(error.ToString(), "patron_embed_origin_invalid", origin);
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2;"), origin);
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin];"), origin);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportUsesLegacyDuplicateLabelsOnlyWhenModernOverrideIsAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-legacy-settings-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationLegacySettings_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Modern Blank Library', 'MBL', 1);
                CREATE TABLE [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
                INSERT INTO [_collections] VALUES
                    ('pbc-ui-settings', 'ui_settings'),
                    ('pbc-library-settings', 'library_settings');
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [duplicateLabelSuggestion] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('ui-library-2', 'library', 'pb-org-2', 'Dormant library ui label', 'Current UI logo');
                CREATE TABLE [patron_library_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrganization] TEXT NOT NULL,
                    [duplicateRequestStatusLabels] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_library_settings] VALUES
                    ('legacy-2', 'pb-org-2', '{"suggestion":"Live legacy label"}', '2029-02-01T00:00:00Z'),
                    ('legacy-3', 'pb-org-3', '{"suggestion":"Ignored legacy label"}', '2029-02-01T00:00:00Z');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [duplicateStatusLabels] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('modern-3', '3', '{}', '2029-03-01T00:00:00Z');
                CREATE TABLE [library_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [libraryOrganization] TEXT NOT NULL,
                    [logo] TEXT,
                    [logoAlt] TEXT
                );
                INSERT INTO [library_settings] VALUES
                    ('legacy-branding-2', 'pb-org-2', NULL, 'Dormant legacy logo');
                """);
            var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report
                ],
                TextWriter.Null,
                error);

            Assert.AreEqual(0, exitCode, error.ToString());
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2 AND [SuggestionStatusLabel] = N'Live legacy label';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 3 AND [SuggestionStatusLabel] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 2 AND [LogoAltText] = N'Current UI logo' AND [LogoData] IS NULL;"));

            using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var transformations = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().ToArray();
            Assert.IsTrue(transformations.Any(item =>
                item.GetProperty("entity").GetString() == "patron_duplicate_labels" &&
                item.GetProperty("sourceId").GetString() == "legacy-2" &&
                item.GetProperty("disposition").GetString() == "applied_legacy_fallback"));
            Assert.IsTrue(transformations.Any(item =>
                item.GetProperty("entity").GetString() == "patron_duplicate_labels" &&
                item.GetProperty("sourceId").GetString() == "legacy-3" &&
                item.GetProperty("disposition").GetString() == "ignored_modern_override_present"));
            Assert.IsTrue(transformations.Any(item =>
                item.GetProperty("entity").GetString() == "legacy_library_branding" &&
                item.GetProperty("sourceId").GetString() == "legacy-branding-2" &&
                item.GetProperty("disposition").GetString() == "intentionally_dropped_not_effective_at_pinned_source"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportPreservesAdditionalCopyHistoryAndUsesCreatedWhenUpdatedIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-additional-copy-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationAdditionalCopy_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var bibId = new string('9', 128);
        var publication = new string('P', 128);
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                $$"""
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [sourceTitleRequest] TEXT,
                    [libraryOrgId] TEXT NOT NULL,
                    [libraryOrgName] TEXT,
                    [bibid] TEXT NOT NULL,
                    [title] TEXT,
                    [author] TEXT,
                    [format] TEXT,
                    [identifier] TEXT,
                    [publication] TEXT,
                    [status] TEXT NOT NULL,
                    [notes] TEXT,
                    [createdByStaff] TEXT,
                    [createdByUsername] TEXT,
                    [closedByStaff] TEXT,
                    [closedByUsername] TEXT,
                    [closedAt] TEXT,
                    [created] TEXT,
                    [updated] TEXT,
                    [claimedByStaffUserId] TEXT,
                    [claimedByDisplayName] TEXT,
                    [claimedAt] TEXT
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-boundary', '', '2', 'Frozen library', '{{bibId}}', 'Frozen title', 'Frozen author',
                     'book', 'COPY-BOUNDARY', '{{publication}}', 'closed', '<p>Frozen notes</p>',
                     'pb-staff-1', 'Historical creator', '', '', '2030-01-03T06:07:08Z',
                     '2030-01-02T03:04:05Z', NULL, '', '', '');
                """);
            DeployDacpac(master, databaseName);
            var identityMap = Path.Combine(root, "staff-map.json");
            await File.WriteAllTextAsync(
                identityMap,
                """
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"00000000-0000-0000-0000-000000000002","objectId":"00000000-0000-0000-0000-000000000011","userPrincipalName":"admin@example.org"}]}
                """);
            var report = Path.Combine(root, "report.json");
            Environment.SetEnvironmentVariable(environmentName, target);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", environmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                    "--report", report
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT [BibId], [Publication], [CreatedUtc], [UpdatedUtc], [ClosedUtc],
                       [ClosedByStaffUserId], [ClosedByDisplayName], [ClaimType], [ClaimRuleId]
                FROM [asap].[AdditionalCopyRequest];
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(bibId, reader.GetString(0));
            Assert.AreEqual(publication, reader.GetString(1));
            Assert.AreEqual(reader.GetDateTime(2), reader.GetDateTime(3));
            Assert.AreEqual(new DateTime(2030, 1, 3, 6, 7, 8), reader.GetDateTime(4));
            Assert.IsTrue(reader.IsDBNull(5));
            Assert.IsTrue(reader.IsDBNull(6));
            Assert.IsTrue(reader.IsDBNull(7));
            Assert.IsTrue(reader.IsDBNull(8));
            await reader.DisposeAsync();

            using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.AreEqual(4, reportDocument.RootElement.GetProperty("reportVersion").GetInt32());
            Assert.AreEqual(1, reportDocument.RootElement.GetProperty("importedCounts")
                .GetProperty("additional_copy_requests").GetInt32());
            Assert.IsTrue(reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Any(item =>
                item.GetProperty("entity").GetString() == "additional_copy_updated_timestamp" &&
                item.GetProperty("reason").GetString() == "missing_updated_uses_created"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ImportBlocksUnaccountedPopulatedConfigurationFieldsBeforeSqlMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-setting-field-{Guid.NewGuid():N}");
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [settingsKey] TEXT,
                    [staffUrl] TEXT,
                    [futureBehaviorSwitch] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings-1', 'system', 'https://staff.example.org/staff/', 'must-not-be-lost');
                """);
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                """
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"00000000-0000-0000-0000-000000000002","objectId":"11111111-1111-1111-1111-111111111111","userPrincipalName":"admin@example.org"}]}
                """);
            Environment.SetEnvironmentVariable(environmentName, "SQL must not be reached");
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", environmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                    "--report", Path.Combine(root, "report.json")
                ],
                TextWriter.Null,
                error);

            Assert.AreEqual(1, exitCode);
            StringAssert.Contains(error.ToString(), "source_field_unaccounted");
            StringAssert.Contains(error.ToString(), "futureBehaviorSwitch");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportPromotesConfiguredBootstrapIdentityAndProtectsIntegrationCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-bootstrap-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationBootstrap_{Guid.NewGuid():N}";
        var insertedDatabaseName = $"AsapMigrationBootstrapInserted_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var insertedTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = insertedDatabaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tokenEnvironmentName = $"ASAP_MIGRATION_TOKEN_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var objectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Directory.CreateDirectory(root);
        X509Certificate2? persistedCertificate = null;
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                UPDATE [staff_users] SET [role] = 'admin', [libraryOrgId] = '2';
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                    [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                    [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris-1', 'https://polaris.example.org', 'access', 'source-api-secret',
                     'EXAMPLE', 'service-user', 'source-admin-secret', '99', '42', '7', '3');
                CREATE TABLE [smtp_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [fromAddress] TEXT, [fromName] TEXT
                );
                INSERT INTO [smtp_settings] VALUES ('smtp-1', 'notices@example.org', 'ASAP');
                """);
            DeployDacpac(master, databaseName);

            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=ASAP Migration Credential Test",
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

            var keyPath = Path.Combine(root, "keys");
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = persistedCertificate.Thumbprint;
            configuration.Authentication.Entra.InitialSuperAdmin.TenantId = tenantId.ToString();
            configuration.Authentication.Entra.InitialSuperAdmin.ObjectId = objectId.ToString();
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "bootstrap@example.org";
            configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "Bootstrap Administrator";
            configuration.Authentication.Entra.InitialSuperAdmin.NotificationEmail = "bootstrap-notify@example.org";
            var configurationPath = Path.Combine(root, "asap.settings.json");
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            var identityMap = Path.Combine(root, "staff-entra-identity-map.json");
            File.WriteAllText(
                identityMap,
                $$"""
                {"users":[{"pocketBaseStaffUserId":"pb-staff-1","tenantId":"{{tenantId}}","objectId":"{{objectId}}","userPrincipalName":"mapped@example.org"}]}
                """);
            var report = Path.Combine(root, "report.json");
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            Environment.SetEnvironmentVariable(tokenEnvironmentName, "target-postmark-secret");

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report,
                    "--external-config", configurationPath,
                    "--postmark-token-env", tokenEnvironmentName
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());
            Assert.IsFalse((output + error.ToString()).Contains("secret", StringComparison.Ordinal));

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [EntraTenantId] = '{tenantId}' AND [EntraObjectId] = '{objectId}' AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
            var ciphertexts = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT [ProtectedApiKey] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1
                    UNION ALL SELECT [ProtectedAdminPassword] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1
                    UNION ALL SELECT [ProtectedServerToken] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) ciphertexts.Add(reader.GetString(0));
            }
            Assert.HasCount(3, ciphertexts);
            Assert.IsFalse(ciphertexts.Contains("source-api-secret"));
            Assert.IsFalse(ciphertexts.Contains("source-admin-secret"));
            Assert.IsFalse(ciphertexts.Contains("target-postmark-secret"));
            var protector = DataProtectionProvider.Create(
                    new DirectoryInfo(keyPath),
                    builder => builder
                        .SetApplicationName(SecurityContract.DataProtectionApplicationName)
                        .ProtectKeysWithCertificate(persistedCertificate))
                .CreateProtector(SecurityContract.IntegrationCredentialPurpose);
            CollectionAssert.AreEquivalent(
                new[] { "source-api-secret", "source-admin-secret", "target-postmark-secret" },
                ciphertexts.Select(protector.Unprotect).ToArray());
            var reportText = await File.ReadAllTextAsync(report);
            Assert.IsFalse(reportText.Contains("source-api-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("source-admin-secret", StringComparison.Ordinal));
            Assert.IsFalse(reportText.Contains("target-postmark-secret", StringComparison.Ordinal));
            StringAssert.Contains(reportText, "promoted_existing");
            using (var reportDocument = JsonDocument.Parse(reportText))
            {
                var promotedRecipient = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "staff_user" &&
                    item.GetProperty("sourceId").GetString() == "pb-staff-1");
                Assert.AreEqual("bootstrap-notify@example.org", promotedRecipient.GetProperty("targetAssignmentRecipient").GetString());
                Assert.IsTrue(promotedRecipient.GetProperty("targetWeeklyEligible").GetBoolean());
                Assert.AreEqual("migration_bootstrap", promotedRecipient.GetProperty("notificationEmailSource").GetString());
            }

            DeployDacpac(master, insertedDatabaseName);
            var insertedObjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            configuration.Authentication.Entra.InitialSuperAdmin.ObjectId = insertedObjectId.ToString();
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "inserted-bootstrap@example.org";
            configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "Inserted Bootstrap Administrator";
            configuration.Authentication.Entra.InitialSuperAdmin.NotificationEmail = "inserted-bootstrap-notify@example.org";
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            var insertedReport = Path.Combine(root, "inserted-report.json");
            Environment.SetEnvironmentVariable(connectionEnvironmentName, insertedTarget);
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--staff-identity-map", identityMap,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", insertedReport,
                    "--external-config", configurationPath,
                    "--postmark-token-env", tokenEnvironmentName
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var insertedConnection = new SqlConnection(insertedTarget);
            await insertedConnection.OpenAsync();
            Assert.AreEqual(2, await ScalarAsync(insertedConnection, "SELECT COUNT(*) FROM [asap].[StaffUser];"));
            Assert.AreEqual(1, await ScalarAsync(
                insertedConnection,
                $"SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [EntraTenantId] = '{tenantId}' AND [EntraObjectId] = '{insertedObjectId}' AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
            var insertedReportText = await File.ReadAllTextAsync(insertedReport);
            StringAssert.Contains(insertedReportText, "\"migration_bootstrap_staff_users\": 1");
            StringAssert.Contains(insertedReportText, "\"action\": \"inserted\"");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            Environment.SetEnvironmentVariable(tokenEnvironmentName, null);
            if (persistedCertificate is not null)
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                store.Remove(persistedCertificate);
                persistedCertificate.Dispose();
            }
            await DropDatabaseAsync(master, databaseName);
            await DropDatabaseAsync(master, insertedDatabaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateMinimalPackage(
        string root,
        string additionalSql = "",
        Action<string>? prepareStorage = null)
    {
        var source = Path.Combine(root, "data.db");
        var storage = Path.Combine(root, "storage");
        var package = Path.Combine(root, "package");
        Directory.CreateDirectory(storage);
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Pooling = false
            }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE [_migrations] ([file] TEXT NOT NULL PRIMARY KEY, [applied] INTEGER NOT NULL);
                INSERT INTO [_migrations] VALUES ('202607270001_patron_code_eligibility.js', 1);
                CREATE TABLE [polaris_organizations]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [organizationId] TEXT NOT NULL,
                    [displayName] TEXT,
                    [abbreviation] TEXT,
                    [enabledForPatrons] INTEGER NOT NULL
                );
                INSERT INTO [polaris_organizations] VALUES ('pb-org-2', '2', 'Test Library', 'TEST', 1);
                CREATE TABLE [staff_users]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [email] TEXT,
                    [username] TEXT,
                    [displayName] TEXT,
                    [role] TEXT NOT NULL,
                    [active] INTEGER NOT NULL,
                    [libraryOrgId] TEXT,
                    [weekly_action_summary_enabled] INTEGER NOT NULL,
                    [weekly_action_summary_email] TEXT,
                    [purchase_reminder_default] INTEGER NOT NULL,
                    [additional_copy_reminder_default] INTEGER NOT NULL,
                    [default_mine_unclaimed_filter] INTEGER NOT NULL
                );
                INSERT INTO [staff_users] VALUES
                    ('pb-staff-1', 'source-admin@example.org', 'source-admin', 'Source Administrator',
                     'super_admin', 1, '1', 1, 'weekly@example.org', 1, 0, 1);
                """ + additionalSql;
            command.ExecuteNonQuery();
            CreatePinnedSourceSchemaFixtureTables(connection);
        }
        prepareStorage?.Invoke(storage);

        using var exportError = new StringWriter();
        var exitCode = MigrationCli.Run(
            [
                "export",
                "--source", source,
                "--storage", storage,
                "--output", package,
                "--source-git-sha", MigrationContract.PocketBaseBaselineSha,
                "--exported-at-utc", "2030-01-02T03:04:05Z",
                "--confirm-source-stopped"
            ],
            TextWriter.Null,
            exportError);
        Assert.AreEqual(0, exitCode, exportError.ToString());
        return package;
    }

    private static void CreatePinnedSourceSchemaFixtureTables(SqliteConnection connection)
    {
        // These empty tables model the collections present at the pinned source schema; production export only reads them.
        var collectionNames = new[]
        {
            "polaris_organizations",
            "staff_users",
            "system_settings",
            "polaris_settings",
            "workflow_settings",
            "ui_settings",
            "patron_settings_overrides",
            "patron_library_settings",
            "library_settings",
            "smtp_settings",
            "material_formats",
            "format_claim_rules",
            "workflow_tags",
            "title_requests",
            "title_request_tags",
            "request_statuses",
            "request_close_reasons",
            "title_request_events",
            "email_templates",
            "rejection_templates",
            "email_delivery_events",
            "deleted_request_audit",
            "additional_copy_requests"
        };

        foreach (var collectionName in collectionNames)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE IF NOT EXISTS [{collectionName}] ([id] TEXT NOT NULL PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        using var metadata = connection.CreateCommand();
        metadata.CommandText =
            """
            CREATE TABLE IF NOT EXISTS [_collections] ([id] TEXT NOT NULL PRIMARY KEY, [name] TEXT NOT NULL);
            INSERT OR IGNORE INTO [_collections] ([id], [name]) VALUES ('pbc_fixture_ui_settings', 'ui_settings');
            """;
        metadata.ExecuteNonQuery();
    }

    private static void AssertPackageValidationCode(string package, string expectedCode)
    {
        using var error = new StringWriter();
        Assert.AreEqual(1, MigrationCli.Run(["validate", "--package", package], TextWriter.Null, error));
        StringAssert.Contains(error.ToString(), expectedCode);
    }

    private static void UpdateManifestPath(string package, string oldPath, string newPath)
    {
        var manifestPath = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var entry = manifest["files"]!.AsArray().Single(item =>
            string.Equals(item!["path"]!.GetValue<string>(), oldPath, StringComparison.OrdinalIgnoreCase))!.AsObject();
        entry["path"] = newPath;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static void AddRuntimeProperty(string package, string name, string value)
    {
        var path = Path.Combine(package, "effective-legacy-runtime-config.json");
        var runtime = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        runtime[name] = value;
        File.WriteAllText(path, runtime.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static void AddManifestProperty(string package, string name, string value)
    {
        var path = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest[name] = value;
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static void UpdateManifestEntry(string package, string relativePath)
    {
        var fullPath = Path.Combine(package, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var data = File.ReadAllBytes(fullPath);
        var manifestPath = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var entry = manifest["files"]!.AsArray().Single(item =>
            string.Equals(item!["path"]!.GetValue<string>(), relativePath, StringComparison.OrdinalIgnoreCase))!.AsObject();
        entry["length"] = data.LongLength;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data));
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static void DeployDacpac(string masterConnectionString, string databaseName)
    {
        using var dacpac = DacPackage.Load(FindDacpac());
        new DacServices(masterConnectionString).Deploy(
            dacpac,
            databaseName,
            upgradeExisting: true,
            new DacDeployOptions
            {
                BlockOnPossibleDataLoss = true,
                CreateNewDatabase = true,
                DropObjectsNotInSource = true
            });
    }

    private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        await using var cleanup = new SqlConnection(masterConnectionString);
        await cleanup.OpenAsync();
        var quoted = new SqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var command = cleanup.CreateCommand();
        command.CommandText = $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}; END";
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindDacpac()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asap.sln")))
        {
            root = root.Parent;
        }
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        return Path.Combine(root!.FullName, "database", "Asap.Database", "bin", configuration, "Asap.Database.dacpac");
    }

    private static string FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asap.sln")))
        {
            root = root.Parent;
        }
        return root!.FullName;
    }
}
