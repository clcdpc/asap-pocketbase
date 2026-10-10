using Asap.Migration;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asap.Security;

namespace Asap.Tests.Migration;

[TestClass]
public sealed partial class MigrationCliTests
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
        StringAssert.Contains(output.ToString(), "recover-report --package");
        StringAssert.Contains(output.ToString(), "a committed import with a report-promotion failure can be recovered without retrying import");
        Assert.IsFalse(output.ToString().Contains("staff-identity-map", StringComparison.Ordinal));
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
        Assert.AreEqual(
            "structured-policy-v4",
            contract.RootElement.GetProperty("contractVersion").GetString());
        Assert.AreEqual(12, contract.RootElement.GetProperty("expectedSchemaVersion").GetInt32());
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
    public void PackageValidationRejectsDuplicateJsonPropertiesButPreservesJsonLookingText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-duplicate-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var nestedPackage = CreateMinimalPackage(Path.Combine(root, "nested"));
            var manifestPath = Path.Combine(nestedPackage, "manifest.json");
            var manifest = File.ReadAllText(manifestPath);
            File.WriteAllText(
                manifestPath,
                manifest.Replace("\"sourceDatabase\": {", "\"sourceDatabase\": {\"duplicate\": {\"name\": 1, \"name\": 2},", StringComparison.Ordinal),
                new UTF8Encoding(false));
            AssertPackageValidationCode(nestedPackage, "package_manifest_invalid");

            var directPackage = CreateMinimalPackage(Path.Combine(root, "direct"));
            var directOrganizationsPath = Path.Combine(directPackage, "organizations.json");
            var directOrganizations = File.ReadAllText(directOrganizationsPath);
            const string directProperty = "\"organizationId\": \"1\",";
            Assert.IsTrue(directOrganizations.Contains(directProperty, StringComparison.Ordinal));
            File.WriteAllText(
                directOrganizationsPath,
                directOrganizations.Replace(directProperty, directProperty + " \"organizationId\": \"1\",", StringComparison.Ordinal),
                new UTF8Encoding(false));
            UpdateManifestEntry(directPackage, "organizations.json");
            AssertPackageValidationCode(directPackage, "package_domain_invalid");

            var ordinaryTextPackage = CreateMinimalPackage(Path.Combine(root, "ordinary-text"));
            var organizationsPath = Path.Combine(ordinaryTextPackage, "organizations.json");
            var organizations = JsonNode.Parse(File.ReadAllText(organizationsPath))!.AsObject();
            organizations["collections"]!["polaris_organizations"]![0]!["displayName"] = "{\"retired\":{\"key\":1,\"key\":2}}";
            File.WriteAllText(
                organizationsPath,
                organizations.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            UpdateManifestEntry(ordinaryTextPackage, "organizations.json");
            Assert.AreEqual(0, MigrationCli.Run(
                ["validate", "--package", ordinaryTextPackage],
                TextWriter.Null,
                TextWriter.Null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void ExportedOperationalLimitsClampSignedOverflowLikeLegacyParseInt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-overflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASAP_JOB_PAGE_SIZE"] = "+2147483648trailing",
            ["ASAP_JOB_MAX_PER_RUN"] = "-2147483649trailing",
            ["ASAP_TIMEOUT_PAGE_SIZE"] = "\uFEFF-2147483649tail",
            ["ASAP_TIMEOUT_MAX_PER_RUN"] = " 2147483648tail",
            ["ASAP_PENDING_SUGGESTION_ISBN_CHECKS_PAGE_SIZE"] = "-2147483649signed-overflow",
            ["ASAP_PENDING_SUGGESTION_ISBN_CHECKS_MAX_PER_RUN"] = "+2147483648signed-overflow",
            ["ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE"] = "-2147483649signed-overflow",
            ["ASAP_OUTSTANDING_PURCHASES_MAX_PER_RUN"] = "+2147483648signed-overflow",
            ["ASAP_PENDING_HOLDS_PAGE_SIZE"] = "2147483648suffix",
            ["ASAP_PENDING_HOLDS_MAX_PER_RUN"] = "-2147483649suffix",
            ["ASAP_CHECKED_OUT_PAGE_SIZE"] = "-2147483649signed-overflow",
            ["ASAP_CHECKED_OUT_MAX_PER_RUN"] = "+2147483648signed-overflow",
            ["ASAP_OUTSTANDING_TIMEOUT_PAGE_SIZE"] = "+2147483648signed-overflow",
            ["ASAP_OUTSTANDING_TIMEOUT_MAX_PER_RUN"] = "-2147483649signed-overflow",
            ["ASAP_PENDING_HOLD_TIMEOUT_PAGE_SIZE"] = "-2147483649signed-overflow",
            ["ASAP_PENDING_HOLD_TIMEOUT_MAX_PER_RUN"] = "+2147483648signed-overflow",
            ["ASAP_HOLD_PICKUP_TIMEOUT_PAGE_SIZE"] = "+2147483648signed-overflow",
            ["ASAP_HOLD_PICKUP_TIMEOUT_MAX_PER_RUN"] = "-2147483649signed-overflow",
            ["ASAP_ADDITIONAL_COPY_TIMEOUT_PAGE_SIZE"] = "-2147483649signed-overflow",
            ["ASAP_ADDITIONAL_COPY_TIMEOUT_MAX_PER_RUN"] = "+2147483648signed-overflow",
            ["ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE"] = "\u0085",
            ["ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"] = "-2147483649"
        };
        var previous = overrides.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var value in overrides)
            {
                Environment.SetEnvironmentVariable(value.Key, value.Value);
            }

            var package = CreateMinimalPackage(root);
            using var operational = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(package, "effective-legacy-operational-config.json")));
            var limits = operational.RootElement.GetProperty("processingLimits");
            Assert.AreEqual(500, limits.GetProperty("global").GetProperty("pageSize").GetProperty("value").GetInt32());
            Assert.AreEqual(1, limits.GetProperty("global").GetProperty("maxPerRun").GetProperty("value").GetInt32());
            Assert.AreEqual(1, limits.GetProperty("timeouts").GetProperty("pageSize").GetProperty("value").GetInt32());
            Assert.AreEqual(5000, limits.GetProperty("timeouts").GetProperty("maxPerRun").GetProperty("value").GetInt32());
            var holdQueue = limits.GetProperty("effectiveQueues").GetProperty("pending_holds");
            Assert.AreEqual(500, holdQueue.GetProperty("pageSize").GetProperty("value").GetInt32());
            Assert.AreEqual(1, holdQueue.GetProperty("maxPerRun").GetProperty("value").GetInt32());
            var obsolete = limits.GetProperty("obsoletePathOverrides").GetProperty("pending_isbn_checks");
            Assert.AreEqual(1, obsolete.GetProperty("pageSize").GetProperty("value").GetInt32());
            Assert.AreEqual(1, obsolete.GetProperty("maxPerRun").GetProperty("value").GetInt32());
            var configuredObsolete = limits.GetProperty("configuredQueueOverrides").GetProperty("pending_isbn_checks");
            Assert.AreEqual(1, configuredObsolete.GetProperty("pageSize").GetInt32());
            Assert.AreEqual(1, configuredObsolete.GetProperty("maxPerRun").GetInt32());
            var queueExpectations = new Dictionary<string, (int PageSize, int MaxPerRun)>(StringComparer.Ordinal)
            {
                ["pending_suggestion_isbn_checks"] = (1, 5000),
                ["outstanding_purchases"] = (1, 5000),
                ["pending_holds"] = (500, 1),
                ["checked_out"] = (1, 5000),
                ["outstanding_timeout"] = (500, 1),
                ["pending_hold_timeout"] = (1, 5000),
                ["hold_pickup_timeout"] = (500, 1),
                ["additional_copy_timeout"] = (1, 5000)
            };
            var effectiveQueues = limits.GetProperty("effectiveQueues");
            foreach (var (queueName, expected) in queueExpectations)
            {
                var queue = effectiveQueues.GetProperty(queueName);
                Assert.AreEqual(expected.PageSize, queue.GetProperty("pageSize").GetProperty("value").GetInt32(), queueName);
                Assert.AreEqual(expected.MaxPerRun, queue.GetProperty("maxPerRun").GetProperty("value").GetInt32(), queueName);
            }

            Environment.SetEnvironmentVariable("ASAP_JOB_PAGE_SIZE", "\u0085");
            var pureNelPackage = CreateMinimalPackage(Path.Combine(root, "pure-nel-required"));
            using var pureNelOperational = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(pureNelPackage, "effective-legacy-operational-config.json")));
            var pureNelRequired = pureNelOperational.RootElement.GetProperty("processingLimits")
                .GetProperty("global").GetProperty("pageSize");
            Assert.AreEqual(50, pureNelRequired.GetProperty("value").GetInt32());
            Assert.AreEqual("environment_override", pureNelRequired.GetProperty("provenance").GetString());
        }
        finally
        {
            foreach (var name in overrides.Keys)
            {
                Environment.SetEnvironmentVariable(name, previous[name]);
            }
            Directory.Delete(root, recursive: true);
        }
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
                        [lastSynced] TEXT,
                        [organizationCodeId] INTEGER,
                        [parentOrganizationId] INTEGER
                    );
                    INSERT INTO [polaris_organizations]
                        ([id], [organizationId], [displayName], [abbreviation], [enabledForPatrons], [lastSynced], [organizationCodeId], [parentOrganizationId])
                    VALUES
                        ('pb-org-2', '2', 'Test Library', 'TEST', 1, '2030-01-02 03:04:05.000Z', 2, 1);
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
            Assert.AreEqual(JsonValueKind.Null, manifest.RootElement.GetProperty("sourceDatabase").GetProperty("wal").ValueKind);
            CollectionAssert.AreEqual(
                new[]
                {
                    "legacy_smtp_transport_excluded",
                    "pocketbase_auth_session_scheduler_state_excluded"
                },
                manifest.RootElement.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()).ToArray());

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

            var compatibleSha = "abcdef0123456789abcdef0123456789abcdef01";
            var compatiblePackage = Path.Combine(root, "compatible-package");
            using var compatibleError = new StringWriter();
            Assert.AreEqual(0, MigrationCli.Run(
                [
                    "export", "--source", source, "--storage", storage, "--output", compatiblePackage,
                    "--source-git-sha", compatibleSha, "--exported-at-utc", "2030-01-02T03:04:05Z",
                    "--confirm-source-stopped"
                ],
                TextWriter.Null,
                compatibleError), compatibleError.ToString());
            using var compatibleManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(compatiblePackage, "manifest.json")));
            Assert.AreEqual(compatibleSha, compatibleManifest.RootElement.GetProperty("pocketBaseSourceGitSha").GetString());
            Assert.AreEqual(0, MigrationCli.Run(["validate", "--package", compatiblePackage], TextWriter.Null, TextWriter.Null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ExportBindsWalBackedSourceIdentityAndPreservesSourceBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-wal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            CreateMinimalPackage(Path.Combine(root, "seed"));
            var seedSource = Path.Combine(root, "seed", "data.db");
            var firstSource = Path.Combine(root, "first", "data.db");
            var secondSource = Path.Combine(root, "second", "data.db");
            var firstStorage = Path.Combine(root, "first-storage");
            var secondStorage = Path.Combine(root, "second-storage");
            var firstPackage = Path.Combine(root, "first-package");
            var secondPackage = Path.Combine(root, "second-package");
            Directory.CreateDirectory(Path.GetDirectoryName(firstSource)!);
            Directory.CreateDirectory(Path.GetDirectoryName(secondSource)!);
            Directory.CreateDirectory(firstStorage);
            Directory.CreateDirectory(secondStorage);
            File.Copy(seedSource, firstSource);
            File.Copy(seedSource, secondSource);

            CommitWalUpdate(firstSource, "WAL snapshot one");
            CommitWalUpdate(secondSource, "WAL snapshot two");

            var firstMainBytes = File.ReadAllBytes(firstSource);
            var secondMainBytes = File.ReadAllBytes(secondSource);
            var firstWalPath = firstSource + "-wal";
            var secondWalPath = secondSource + "-wal";
            Assert.IsTrue(File.Exists(firstWalPath));
            Assert.IsTrue(File.Exists(secondWalPath));
            var firstWalBytes = File.ReadAllBytes(firstWalPath);
            var secondWalBytes = File.ReadAllBytes(secondWalPath);
            CollectionAssert.AreEqual(firstMainBytes, secondMainBytes);

            Export(firstSource, firstStorage, firstPackage);
            Export(secondSource, secondStorage, secondPackage);

            using var firstManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(firstPackage, "manifest.json")));
            using var secondManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(secondPackage, "manifest.json")));
            var firstSourceMetadata = firstManifest.RootElement.GetProperty("sourceDatabase");
            var secondSourceMetadata = secondManifest.RootElement.GetProperty("sourceDatabase");
            var firstWalMetadata = firstSourceMetadata.GetProperty("wal");
            var secondWalMetadata = secondSourceMetadata.GetProperty("wal");
            Assert.AreEqual("data.db-wal", firstWalMetadata.GetProperty("fileName").GetString());
            Assert.AreEqual("data.db-wal", secondWalMetadata.GetProperty("fileName").GetString());
            Assert.AreEqual(firstWalBytes.LongLength, firstWalMetadata.GetProperty("length").GetInt64());
            Assert.AreEqual(secondWalBytes.LongLength, secondWalMetadata.GetProperty("length").GetInt64());
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(firstWalBytes)),
                firstWalMetadata.GetProperty("sha256").GetString());
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(secondWalBytes)),
                secondWalMetadata.GetProperty("sha256").GetString());
            Assert.AreNotEqual(
                firstWalMetadata.GetProperty("sha256").GetString(),
                secondWalMetadata.GetProperty("sha256").GetString());
            Assert.AreNotEqual(
                File.ReadAllText(Path.Combine(firstPackage, "manifest.json")),
                File.ReadAllText(Path.Combine(secondPackage, "manifest.json")));

            using var firstOrganizations = JsonDocument.Parse(File.ReadAllText(Path.Combine(firstPackage, "organizations.json")));
            using var secondOrganizations = JsonDocument.Parse(File.ReadAllText(Path.Combine(secondPackage, "organizations.json")));
            var firstLibrary = firstOrganizations.RootElement.GetProperty("collections").GetProperty("polaris_organizations")
                .EnumerateArray().Single(row => row.GetProperty("organizationId").GetString() == "2");
            var secondLibrary = secondOrganizations.RootElement.GetProperty("collections").GetProperty("polaris_organizations")
                .EnumerateArray().Single(row => row.GetProperty("organizationId").GetString() == "2");
            Assert.AreEqual("WAL snapshot one", firstLibrary.GetProperty("displayName").GetString());
            Assert.AreEqual("WAL snapshot two", secondLibrary.GetProperty("displayName").GetString());
            Assert.AreEqual(
                0,
                MigrationCli.Run(["validate", "--package", firstPackage], TextWriter.Null, TextWriter.Null));
            Assert.AreEqual(
                0,
                MigrationCli.Run(["validate", "--package", secondPackage], TextWriter.Null, TextWriter.Null));
            CollectionAssert.AreEqual(firstMainBytes, File.ReadAllBytes(firstSource));
            CollectionAssert.AreEqual(secondMainBytes, File.ReadAllBytes(secondSource));
            CollectionAssert.AreEqual(firstWalBytes, File.ReadAllBytes(firstWalPath));
            CollectionAssert.AreEqual(secondWalBytes, File.ReadAllBytes(secondWalPath));
            Assert.IsFalse(File.Exists(Path.Combine(firstPackage, "data.db-wal")));
            Assert.IsFalse(File.Exists(Path.Combine(secondPackage, "data.db-wal")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void CommitWalUpdate(string source, string displayName)
        {
            SqliteConnection? reader = null;
            SqliteTransaction? readerTransaction = null;
            try
            {
                using (var writer = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = source,
                        Pooling = false
                    }.ConnectionString))
                {
                    writer.Open();
                    using (var journal = writer.CreateCommand())
                    {
                        journal.CommandText = "PRAGMA journal_mode=WAL;";
                        Assert.AreEqual("wal", Convert.ToString(journal.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
                    }
                    using (var checkpoint = writer.CreateCommand())
                    {
                        checkpoint.CommandText = "PRAGMA wal_autocheckpoint=0;";
                        checkpoint.ExecuteNonQuery();
                    }
                    using (var update = writer.CreateCommand())
                    {
                        update.CommandText = "UPDATE [polaris_organizations] SET [displayName] = $displayName WHERE [id] = 'pb-org-2';";
                        update.Parameters.AddWithValue("$displayName", displayName);
                        update.ExecuteNonQuery();
                    }

                    reader = new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = source,
                            Mode = SqliteOpenMode.ReadOnly,
                            Pooling = false
                        }.ConnectionString);
                    reader.Open();
                    readerTransaction = reader.BeginTransaction();
                    using var read = reader.CreateCommand();
                    read.Transaction = readerTransaction;
                    read.CommandText = "SELECT [displayName] FROM [polaris_organizations] WHERE [id] = 'pb-org-2';";
                    Assert.AreEqual(displayName, Convert.ToString(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                readerTransaction?.Dispose();
                reader?.Dispose();
            }
        }

        static void Export(string source, string storage, string package)
        {
            using var error = new StringWriter();
            Assert.AreEqual(
                0,
                MigrationCli.Run(
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
                    error),
                error.ToString());
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
    public void ImportAndReconcileRequireExternalConfigurationAtEveryEntryPoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-required-config-{Guid.NewGuid():N}");
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(root);
            var report = Path.Combine(root, "report.json");

            using var importMissingError = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", report
                    ],
                    TextWriter.Null,
                    importMissingError));
            StringAssert.Contains(importMissingError.ToString(), "invalid_arguments");
            StringAssert.Contains(importMissingError.ToString(), "Missing required option: --external-config");

            using var reconcileMissingError = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(
                    [
                        "reconcile", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--report", report
                    ],
                    TextWriter.Null,
                    reconcileMissingError));
            StringAssert.Contains(reconcileMissingError.ToString(), "invalid_arguments");
            StringAssert.Contains(reconcileMissingError.ToString(), "Missing required option: --external-config");

            var importException = Assert.Throws<MigrationOperationException>(() =>
                MigrationImporter.Import(new MigrationImportOptions(
                    package,
                    string.Empty,
                    new HashSet<Guid> { tenantId },
                    report,
                    null)));
            Assert.AreEqual("external_configuration_missing", importException.Code);

            var reconcileException = Assert.Throws<MigrationOperationException>(() =>
                MigrationReconciler.Reconcile(new MigrationReconcileOptions(
                    package,
                    string.Empty,
                    report,
                    null)));
            Assert.AreEqual("external_configuration_missing", reconcileException.Code);

            var configurationPath = ExternalConfigurationPath(package);
            using var validConfigurationError = new StringWriter();
            Assert.AreEqual(
                0,
                MigrationCli.Run(
                    ["validate", "--package", package, "--external-config", configurationPath],
                    TextWriter.Null,
                    validConfigurationError),
                validConfigurationError.ToString());

            var configuration = JsonNode.Parse(File.ReadAllText(configurationPath))!.AsObject();
            configuration["Hangfire"]!["Schedules"]!["WorkflowProcessing"] = "17 * * * *";
            File.WriteAllText(configurationPath, configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            using var importMismatchError = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", report,
                        "--external-config", configurationPath
                    ],
                    TextWriter.Null,
                    importMismatchError));
            StringAssert.Contains(importMismatchError.ToString(), "operational_configuration_mismatch");

            using var reconcileMismatchError = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(
                    [
                        "reconcile", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--report", report,
                        "--external-config", configurationPath
                    ],
                    TextWriter.Null,
                    reconcileMismatchError));
            StringAssert.Contains(reconcileMismatchError.ToString(), "operational_configuration_mismatch");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
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

            using var validationError = new StringWriter();
            Assert.AreEqual(
                0,
                MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validationError),
                validationError.ToString());

            var unreferencedAssetPath = "assets/branding/unreferenced/logo.png";
            var unreferencedAssetFullPath = Path.Combine(package, unreferencedAssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(unreferencedAssetFullPath)!);
            var brandingBytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "src", "Asap.Web", "Frontend", "jpl.png"));
            File.WriteAllBytes(unreferencedAssetFullPath, brandingBytes);
            AddManifestFile(package, unreferencedAssetPath, brandingBytes);
            AssertPackageValidationCode(package, "package_unlisted_file");
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
    public void ValidationRejectsRehashedOpaqueFilesAndCredentialShapedManifestPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-package-membership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var opaquePackage = CreateMinimalPackage(Path.Combine(root, "opaque"));
            var opaquePath = Path.Combine(opaquePackage, "opaque.bin");
            var opaqueData = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(opaquePath, opaqueData);
            AddManifestFile(opaquePackage, "opaque.bin", opaqueData);
            AssertPackageValidationCode(opaquePackage, "package_unlisted_file");

            var credentialPathPackage = CreateMinimalPackage(Path.Combine(root, "credential-path"));
            const string credentialShapedPath = "postmarkToken=fixture-only-value";
            UpdateManifestPath(credentialPathPackage, "organizations.json", credentialShapedPath);
            using var error = new StringWriter();
            Assert.AreEqual(
                1,
                MigrationCli.Run(["validate", "--package", credentialPathPackage], TextWriter.Null, error));
            StringAssert.Contains(error.ToString(), "package_secret_forbidden");
            Assert.IsFalse(error.ToString().Contains("fixture-only-value", StringComparison.Ordinal));
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

            var manifestScalarSecretPackage = CreateMinimalPackage(Path.Combine(root, "manifest-scalar-secret"));
            SetManifestWarning(manifestScalarSecretPackage, "postmarkToken=fixture-only-value");
            AssertPackageValidationCode(manifestScalarSecretPackage, "package_secret_forbidden");

            var manifestWarningPackage = CreateMinimalPackage(Path.Combine(root, "manifest-warning"));
            SetManifestWarning(manifestWarningPackage, "fixture-only-warning");
            AssertPackageValidationCode(manifestWarningPackage, "package_manifest_invalid");

            var manifestUnknownPackage = CreateMinimalPackage(Path.Combine(root, "manifest-unknown"));
            AddManifestProperty(manifestUnknownPackage, "unexpectedMember", "fixture-only-value");
            AssertPackageValidationCode(manifestUnknownPackage, "package_manifest_invalid");

            var invalidWalMetadataPackage = CreateMinimalPackage(Path.Combine(root, "invalid-wal-metadata"));
            var invalidWalManifestPath = Path.Combine(invalidWalMetadataPackage, "manifest.json");
            var invalidWalManifest = JsonNode.Parse(File.ReadAllText(invalidWalManifestPath))!.AsObject();
            invalidWalManifest["sourceDatabase"]!.AsObject()["wal"] = new JsonObject
            {
                ["fileName"] = "data.db-shm",
                ["length"] = 1,
                ["sha256"] = new string('a', 64)
            };
            File.WriteAllText(
                invalidWalManifestPath,
                invalidWalManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            AssertPackageValidationCode(invalidWalMetadataPackage, "package_manifest_invalid");

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
    public async Task ImportUsesSourceStaffEmailAndReconcilesFreshSqlTargetWithoutIdentityMap()
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
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(root, "dirty-target-report.json"),
                        "--external-config", ExternalConfigurationPath(package)
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
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [DisplayName] = N'Test Library' AND [IsActive] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [UserPrincipalName] = N'source-admin@example.org' AND [NormalizedUserPrincipalName] = N'SOURCE-ADMIN@EXAMPLE.ORG' AND [EntraTenantId] IS NULL AND [EntraObjectId] IS NULL AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
                Assert.AreEqual(4, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] IN (N'organization', N'staff_user');"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 1 AND [IsActive] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 20 AND [OrganizationCodeId] = 3 AND [ParentOrganizationId] = 2 AND [IsActive] = 0;"));
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
            Assert.AreEqual("source-admin@example.org", staffRecipient.GetProperty("targetAssignmentRecipient").GetString());
            Assert.AreEqual("source-admin@example.org", staffRecipient.GetProperty("targetPurchaseReminderRecipient").GetString());
            Assert.AreEqual("source-admin@example.org", staffRecipient.GetProperty("targetAdditionalCopyReminderRecipient").GetString());
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
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
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
                        "--report", report,
                        "--external-config", ExternalConfigurationPath(package)
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
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", secondReport,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                TextWriter.Null,
                secondError), secondError.ToString());
            AssertEquivalentReportsExceptTargetBinding(
                report,
                target,
                secondReport,
                secondTarget,
                "Equivalent fresh targets must produce the same restricted reconciliation report apart from target-specific hashes.");

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
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(differentPackage)
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
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
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
        var secondDatabaseName = $"AsapMigrationRequest_{Guid.NewGuid():N}";
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
                    ('fmt-book', 'system', NULL, '0', 'Printed Book', 1, 7, 'none',
                     'required', 'Work title', 'required', 'Creator', 'optional', 'ISBN',
                     'required', 'Publication timing', '2029-01-01T00:00:00.7891234Z', '2029-02-01T00:00:00.1234567Z'),
                    ('fmt-a-book-other-override', 'library', 'pb-org-3', '0', 'Other Library Book', 1, 12, 'none',
                     'required', 'Title', 'required', 'Author', 'optional', 'Identifier number',
                     'required', 'Publication Timing', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z'),
                    ('fmt-b-book-library-override', 'library', '+2', '0', 'Local Book', 1, 11, 'none',
                     'required', 'Title', 'required', 'Author', 'optional', 'Identifier number',
                     'required', 'Publication Timing', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z'),
                    ('fmt-other-library', 'library', 'pb-org-3', 'other-library', 'Other Library Format', 1, 20, 'none',
                     'required', 'Title', 'required', 'Author', 'optional', 'Identifier',
                     'optional', 'Publication', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
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
                    ('pb-rule-1', '2', '0', 'pb-staff-2', 1,
                     '2029-02-01T00:00:00.7654321Z', '2029-02-02T00:00:00.1234567Z');
                CREATE TABLE [workflow_tags]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [code] TEXT NOT NULL,
                    [label] TEXT NOT NULL,
                    [description] TEXT
                );
                INSERT INTO [workflow_tags] VALUES
                    ('tag-found', 'Identifier found', 'Identifier found', 'Found in Polaris'),
                    ('tag-case', 'Custom_Case_Tag', 'Custom case tag', 'Preserve exact source case');
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
                    ('pb-request-1', '2', 'fmt-b-book-library-override', 'A20000000000001', 'patron@example.org',
                     'Ada', 'Reader', 'The Found Book', 'A. Writer', '9780000000001',
                    'Coming soon', 1, 'suggestion', '09001', 'found', 'one match', 2,
                     '2029-03-02T12:00:00.1234567Z', 'pb-staff-2', 'Library Selector',
                     '2029-03-01T12:01:00.9876543Z', 'automatic_format_rule', 'pb-rule-1',
                     '2029-03-01T12:00:00.7891234Z', '2029-03-02T12:00:00.1234567Z');
                ALTER TABLE [title_requests] ADD COLUMN [notes] TEXT;
                UPDATE [title_requests] SET [notes] = 'Source request note.' WHERE [id] = 'pb-request-1';
                ALTER TABLE [title_requests] ADD COLUMN [customFields] TEXT;
                UPDATE [title_requests] SET [customFields] = '{" İ Audience Name ":{"label":"Historical key","type":"text","value":"keep"}}' WHERE [id] = 'pb-request-1';
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
                     '{"bibId":"9001"}', '2029-03-01T13:00:00.2345678Z');
                CREATE TABLE [email_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [templateKey] TEXT NOT NULL,
                    [name] TEXT,
                    [subject] TEXT,
                    [body] TEXT,
                    [enabled] INTEGER NOT NULL,
                    [sourceTemplateId] TEXT
                );
                INSERT INTO [email_templates] VALUES
                    ('template-submitted', 'system', NULL, 'suggestion_submitted', 'Submission receipt',
                     'Received: {{title}}', '<p>Hello {{name}}</p><p>{{title}} by {{author}}</p>', 1, NULL),
                    ('z-system-case', 'System', NULL, 'Custom_Case_Override', 'System case template',
                     'System subject', '<p>System body</p>', 1, NULL),
                    ('a-library-case', 'library', 'pb-org-2', 'Custom_Case_Override', 'Library case template',
                     'Library subject', '<p>Library body</p>', 1, 'z-system-case');
                CREATE TABLE [rejection_templates]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [name] TEXT,
                    [subject] TEXT,
                    [body] TEXT,
                    [enabled] INTEGER NOT NULL,
                    [sortOrder] INTEGER,
                    [sourceTemplateId] TEXT
                );
                INSERT INTO [rejection_templates] VALUES
                    ('z-system-reject', 'System', NULL, 'System rejection', 'System declined', '<p>System declined.</p>', 1, 10, NULL),
                    ('a-library-reject', 'library', 'pb-org-2', 'Library rejection', NULL, NULL, 1, 20, 'z-system-reject');
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
                     'HTTPS://*.Library.Example.Invalid:443, http://LOCALHOST:1234/, https://Exact.Example.Invalid/, HTTPS://Exact-Port.Example.Invalid:443, HTTP://[::1]:1234/');
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
                     1, char(65279) || 'Butler, Octavia' || char(65279) || char(10) || 'Le Guin, Ursula', 'Collected creators', 'Check first.', 'Already collected.',
                     1, 0, 1, char(65279) || '7' || char(65279) || ', ' || char(65279) || '9' || char(65279), 'Card not eligible.',
                     1, 'Search Catalog', 'https://catalog.example/search?q={{title}}',
                     0, 'Search Two', 'https://two.example/{{title}}',
                     1, 'Search Three', 'https://three.example/{{title}}',
                     0, '', '', '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z'),
                    ('workflow-library', 'library', '+2', 8, 'Local weekly limit.',
                     0, NULL, 0, 0, NULL, 0, NULL, 0, NULL, 1,
                     0, '', NULL, NULL, NULL, 0, 1, 0, '', NULL,
                     0, 'Local Catalog', 'https://local.example/{{title}}',
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
                     '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                ALTER TABLE [workflow_settings] ADD COLUMN [outstandingTimeoutRejectionTemplate] TEXT;
                UPDATE [workflow_settings] SET [outstandingTimeoutRejectionTemplate] = 'z-system-reject' WHERE [id] = 'workflow-system';
                UPDATE [workflow_settings] SET [outstandingTimeoutRejectionTemplate] = 'a-library-reject' WHERE [id] = 'workflow-library';
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
                    ('ui-system', 'System', NULL, 'Suggest an Item', 'Card number', 'PIN',
                     '<p>Sign in to suggest.</p>', '<p>Have your card ready.</p>', '<p>One title per form.</p>',
                     '<p>Add an email for updates.</p>', 'Thank you', '<p>Received.</p>',
                     '<p>Already received {{duplicate_date}}.</p>', '<p>Use Libby.</p>', '<p>Use Libby audio.</p>',
                     'Received locally',
                    '[]',
                     '   ', 'migration_logo.png', 'Consortium logo',
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
                    ('patron-override-2', '2', '{"suggestion":"  Local received  ","Silently Closed":"  Local silent label  "}',
                    char(65279) || '[{"label":"","name":"\uFEFFİ Name\uFEFF","enabled":null,"sortOrder":0},{"id":"\uFEFFlocal_preorder\uFEFF","label":"\uFEFFLocal preorder\uFEFF","enabled":true,"sortOrder":null},{"id":"third_option","label":"Third\u0085 option","enabled":true,"sortOrder":10}]' || char(65279),
                     '{"0":{"messageBehavior":"\uFEFFmessage\uFEFF","message":"\uFEFFFormat message\uFEFF","fields":{"author":{"mode":"\u0085hidden\u0085","label":"\u0085Creator\u0085"},"identifier":{"mode":"\uFEFFhidden\uFEFF","label":"\uFEFFISBN\uFEFF"},"title":null},"customFields":{"audience":{"mode":"\uFEFFrequired\uFEFF","label":"\uFEFFWho is it for?\uFEFF"},"audience-empty":{"mode":"required"},"audience_empty":{"mode":"\uFEFFrequired\uFEFF","label":"\uFEFF"},"audience-disabled":{"mode":"required"},"i_audience_name":{"mode":"required","label":"Who is it for?"},"İ Audience Name":{"mode":"hidden","label":"Wrong raw-key match"}}},"book":{"fields":{"author":{"label":"Book fallback"}},"customFields":{"audience":{"mode":"hidden"}}},"audiobook_cd":{"fields":null,"customFields":{"audience-empty":{"mode":"required"},"audience_empty":{"mode":"\u0085required\u0085","label":"\u0085Not trimmed\u0085"}}},"dvd":null,"music_cd":{"customFields":null},"ebook":{"customFields":{"i_audience_name":null}}}',
                     '[{"key":"\uFEFF audience \uFEFF","label":"\uFEFFAudience\uFEFF","type":" \uFEFFselect\uFEFF ","helpText":"\uFEFFChoose\u0085 the intended audience.\uFEFF","enabled":true,"sortOrder":10,"options":[{"id":"\uFEFFadult\uFEFF","label":"\uFEFFAdult\uFEFF","enabled":true,"sortOrder":10},{"id":"teen","label":"Teen\u0085 Option","enabled":true}]},{"key":"\uFEFFaudience-empty\uFEFF","label":"\u0085Audience without enabled options\u0085","type":"\uFEFFselect\uFEFF","enabled":true,"sortOrder":null,"options":[{"id":"retired","label":"Retired","enabled":false,"sortOrder":null}]},{"key":"audience-disabled","label":"Disabled audience","type":"select","enabled":false},{"label":"İ Audience Name","type":"select","options":[{"name":"İ Adult Option","enabled":true},{"value":"Teen Option","enabled":true}]},{"key":"text-ignores-options","label":"Text ignores options","type":" text ","options":[{"id":"ignored-option","label":"Ignored option"}]}]',
                     NULL, NULL, '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
                INSERT INTO [patron_settings_overrides] VALUES
                    ('numeric-fallback-override-3', '3', NULL,
                     char(65279) || '1' || char(65279) || char(10) || char(65279) || '2' || char(65279) || char(10) || char(65279) || '3' || char(65279) || char(10) || char(65279) || '4' || char(65279),
                     NULL, NULL, NULL, NULL, '2029-01-01T00:00:00Z', '2029-02-01T00:00:00Z');
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
                },
                "2030-01-02T03:04:05.1234567Z");
            var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var report = Path.Combine(root, "import-report.json");
            var secondReport = Path.Combine(root, "second-import-report.json");
            DeployDacpac(master, databaseName);
            DeployDacpac(master, secondDatabaseName);

            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            Environment.SetEnvironmentVariable(secondConnectionEnvironmentName, secondTarget);
            using var error = new StringWriter();
            var exitCode = RunImport(package, report, connectionEnvironmentName, tenantId, error);
            Assert.AreEqual(0, exitCode, error.ToString());

            using var secondError = new StringWriter();
            Assert.AreEqual(0, RunImport(package, secondReport, secondConnectionEnvironmentName, tenantId, secondError), secondError.ToString());
            AssertEquivalentReportsExceptTargetBinding(
                report,
                target,
                secondReport,
                secondTarget,
                "Equivalent fresh targets must produce the same BIB-authority classification apart from target-specific hashes.");

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book' AND [Label] = N'Printed Book' AND [SortOrder] = 7;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[LegacyId] IS NULL AND r.[LibraryOrganizationId] = 2 AND r.[Status] = N'suggestion' AND r.[IsbnCheckStatus] = N'found' AND r.[BibId] = 9001 AND r.[Title] = N'The Found Book';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r " +
                "JOIN [asap].[LegacyPocketBaseMapping] rm ON rm.[EntityType] = N'title_request' AND rm.[PocketBaseId] = N'pb-request-1' AND rm.[NewId] = r.[Id] " +
                "JOIN [asap].[LegacyPocketBaseMapping] fm ON fm.[EntityType] = N'material_format_override' AND fm.[PocketBaseId] = N'fmt-b-book-library-override' " +
                "JOIN [asap].[MaterialFormatOverride] o ON o.[Id] = fm.[NewId] " +
                "WHERE r.[MaterialFormatId] = o.[MaterialFormatId] AND o.[MaterialFormatId] = (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book') AND o.[Id] <> o.[MaterialFormatId];"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [BibId] = 9001 AND [BibIdStaffVerified] = 0;"));
            Assert.AreEqual(1, await ScalarAsync(connection,
                """
                SELECT COUNT(*) FROM [asap].[TitleRequestEvent] event
                CROSS APPLY OPENJSON(event.[MetadataJson], '$.bibSources')
                    WITH ([bibId] int '$.bibId', [sourceValue] nvarchar(100) '$.sourceValue') source
                WHERE event.[EventType] = N'legacy' AND source.[bibId] = 9001 AND source.[sourceValue] = N'09001';
                """));
            using (var authorityReport = JsonDocument.Parse(await File.ReadAllTextAsync(report)))
            {
                var authority = authorityReport.RootElement.GetProperty("bibAuthorityReconciliation");
                Assert.AreEqual(1, authority.GetProperty("sourceRequestsWithBibId").GetInt32());
                Assert.AreEqual(1, authority.GetProperty("automationDerivedBibs").GetInt32());
                Assert.AreEqual(0, authority.GetProperty("staffAuthoritativeBibs").GetInt32());
                Assert.AreEqual(0, authority.GetProperty("ambiguousBibsImportedWithoutStaffAuthority").GetInt32());
                var placementTransform = authorityReport.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "placed_bib_protection");
                Assert.AreEqual("inserted", placementTransform.GetProperty("action").GetString());
                Assert.AreEqual(JsonValueKind.Array, placementTransform.GetProperty("hints").ValueKind);
                Assert.AreEqual(JsonValueKind.Array, placementTransform.GetProperty("bibSources").ValueKind);
                var authorityTransform = authorityReport.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "title_request_bib_authority");
                Assert.AreEqual("automation_derived", authorityTransform.GetProperty("classification").GetString());
                Assert.IsFalse(authorityTransform.GetProperty("bibIdStaffVerified").GetBoolean());
                var polarisTransform = authorityReport.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "polaris_settings");
                Assert.AreEqual("7", polarisTransform.GetProperty("retiredRequestingOrganizationSource").GetString());
                Assert.AreEqual("3", polarisTransform.GetProperty("retiredPickupOrganizationSource").GetString());
                var customFieldTransform = authorityReport.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "patron_custom_fields" && item.GetProperty("organizationId").GetInt32() == 2);
                Assert.AreEqual(5, customFieldTransform.GetProperty("fields").GetInt32());
                Assert.AreEqual(30, customFieldTransform.GetProperty("formatRules").GetInt32());
                Assert.AreEqual(1, customFieldTransform.GetProperty("downgradedRequiredSelectRules").GetInt32());
            }
            Assert.AreEqual(0, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'asap.PolarisSettings') AND name IN (N'OrganizationIdForRequests', N'PickupOrganizationId');"));
            Assert.AreEqual(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM [asap].[PolarisSettings] WHERE [WorkstationId] = 99 AND [SystemPolarisUserId] = 42;"));
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
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'status_changed' AND [CreatedUtc] = '2029-03-01T13:00:00.2345678';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book' AND [CreatedUtc] = '2029-01-01T00:00:00.7891234' AND [UpdatedUtc] = '2029-02-01T00:00:00.1234567';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'format_auto_claim_rule' AND m.[PocketBaseId] = N'pb-rule-1' AND m.[NewId] = c.[Id] WHERE c.[CreatedUtc] = '2029-02-01T00:00:00.7654321' AND c.[DeactivatedUtc] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[CreatedUtc] = '2029-03-01T12:00:00.7891234' AND r.[UpdatedUtc] = '2029-03-02T12:00:00.1234567' AND r.[ClaimedAtUtc] = '2029-03-01T12:01:00.9876543';"),
                "Request history timestamps retain their seven-digit source precision.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [UpdatedUtc] = '2029-02-01T00:00:00';"),
                "System settings preserve the UI source timestamp instead of the manifest fallback.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND [CreatedUtc] = '2030-01-02T03:04:05.1234567' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true' AND JSON_VALUE([MetadataJson], '$.bibId') = N'9001' AND JSON_VALUE([MetadataJson], '$.transform') = N'placed_bib_protection_v1';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted' AND [SubjectTemplate] = N'Received: {{title}}' AND [BodyTemplate] LIKE N'%{{name}}%';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] childMap " +
                "JOIN [asap].[EmailTemplate] child ON child.[Id] = childMap.[NewId] " +
                "JOIN [asap].[LegacyPocketBaseMapping] sourceMap ON sourceMap.[EntityType] = N'email_template' AND sourceMap.[PocketBaseId] = N'z-system-case' " +
                "WHERE childMap.[EntityType] = N'email_template' AND childMap.[PocketBaseId] = N'a-library-case' " +
                "AND child.[OrganizationId] = 2 AND child.[TemplateKey] COLLATE Latin1_General_100_BIN2 = N'Custom_Case_Override' COLLATE Latin1_General_100_BIN2 AND child.[SourceTemplateId] = sourceMap.[NewId] AND child.[IsCustom] = 0;"));
            Assert.AreEqual(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM [asap].[WorkflowTag] WHERE [Code] COLLATE Latin1_General_100_BIN2 = N'Custom_Case_Tag' COLLATE Latin1_General_100_BIN2 AND [Label] = N'Custom case tag';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] childMap " +
                "JOIN [asap].[EmailTemplate] child ON child.[Id] = childMap.[NewId] " +
                "JOIN [asap].[LegacyPocketBaseMapping] sourceMap ON sourceMap.[EntityType] = N'email_template' AND sourceMap.[PocketBaseId] = N'z-system-reject' " +
                "JOIN [asap].[EmailTemplate] sourceTemplate ON sourceTemplate.[Id] = sourceMap.[NewId] " +
                "WHERE childMap.[EntityType] = N'email_template' AND childMap.[PocketBaseId] = N'a-library-reject' " +
                "AND child.[OrganizationId] = 2 AND child.[TemplateKey] = N'rejection:z-system-reject' AND child.[SourceTemplateId] = sourceTemplate.[Id] AND child.[IsCustom] = 0 AND child.[SubjectTemplate] IS NULL AND child.[BodyTemplate] IS NULL " +
                "AND sourceTemplate.[OrganizationId] = 1 AND sourceTemplate.[TemplateKey] = N'rejection:z-system-reject' AND sourceTemplate.[SubjectTemplate] = N'System declined' AND sourceTemplate.[BodyTemplate] = N'<p>System declined.</p>';"),
                "The library rejection row preserves null source content and links to the matching system template that supplies effective content.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[WorkflowSettings] w JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_template' AND m.[PocketBaseId] = N'a-library-reject' AND m.[NewId] = w.[OutstandingTimeoutRejectionTemplateId] WHERE w.[OrganizationId] = 2;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[WorkflowSettings] w JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_template' AND m.[PocketBaseId] = N'z-system-reject' AND m.[NewId] = w.[OutstandingTimeoutRejectionTemplateId] WHERE w.[OrganizationId] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IS NULL AND [EventType] = N'sent' AND JSON_VALUE([MetadataJson], '$.sourceRecordId') = N'mail-history-1' AND JSON_VALUE([MetadataJson], '$.recipient') = N'patron@example.org';"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailOutbox];"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [StaffApplicationUrl] = N'https://staff.example.org/staff/' AND [SystemNotEnabledMessage] = N'   ';"));
            Assert.AreEqual(5, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin] WHERE [NormalizedOrigin] IN (N'https://*.library.example.invalid:443', N'http://localhost:1234', N'https://exact.example.invalid', N'https://exact-port.example.invalid:443', N'http://[::1]:1234');"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2 AND [SuggestionLimit] = 8 AND [AutoPromote] = 1 AND [AllowPatronAutoholdOptOut] = 0;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2 AND [SilentStatusLabel] = N'  Local silent label  ' AND DATALENGTH([SilentStatusLabel]) = 44;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2 AND [SuggestionStatusLabel] = N'  Local received  ' AND DATALENGTH([SuggestionStatusLabel]) = 36;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NULL AND [FromAddress] = N'notices@example.org' AND [FromName] = N'ASAP Notices';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder' AND [Label] = N'Local preorder';"));
            Assert.AreEqual(3, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 AND (([OptionKey] = N'i-name' AND [Label] = N'İ Name' AND [IsEnabled] = 1 AND [SortOrder] = 10) OR ([OptionKey] = N'local_preorder' AND [Label] = N'Local preorder' AND [IsEnabled] = 1 AND [SortOrder] = 20) OR ([OptionKey] = N'third_option' AND [Label] = N'Third' + NCHAR(133) + N' option' AND [IsEnabled] = 1 AND [SortOrder] = 10));"));
            var publicationOptionOrder = new List<string>();
            await using (var orderCommand = connection.CreateCommand())
            {
                orderCommand.CommandText = "SELECT [OptionKey] FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 ORDER BY [SortOrder], [Id];";
                await using var orderReader = await orderCommand.ExecuteReaderAsync();
                while (await orderReader.ReadAsync())
                {
                    publicationOptionOrder.Add(orderReader.GetString(0));
                }
            }
            CollectionAssert.AreEqual(new[] { "i-name", "third_option", "local_preorder" }, publicationOptionOrder,
                "Null and zero publication sort orders use their source ordinal, with stable source order for ties; null enabled defaults to true.");
            Assert.AreEqual(3, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 1 AND (([OptionKey] = N'already_published' AND [Label] = N'Already published' AND [IsEnabled] = 1 AND [SortOrder] = 10) OR ([OptionKey] = N'coming_soon' AND [Label] = N'Coming soon' AND [IsEnabled] = 1 AND [SortOrder] = 20) OR ([OptionKey] = N'published_a_while_back' AND [Label] = N'Published a while back' AND [IsEnabled] = 1 AND [SortOrder] = 30));"));
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 3;"),
                "A source numeric-only library list must inherit the current system publication set.");
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 3;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1' AND o.[IsEnabled] = 0 AND o.[Label] = N'Local Catalog';"));
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1;"));
            var creatorValues = new List<string>();
            await using (var creatorCommand = connection.CreateCommand())
            {
                creatorCommand.CommandText = "SELECT [Value] FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1 ORDER BY [SortOrder], [Id];";
                await using var creatorReader = await creatorCommand.ExecuteReaderAsync();
                while (await creatorReader.ReadAsync())
                {
                    creatorValues.Add(creatorReader.GetString(0));
                }
            }
            CollectionAssert.AreEqual(new[] { "Butler, Octavia", "Le Guin, Ursula" }, creatorValues,
                "Legacy creator configuration splits on LF only and preserves commas inside names.");
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 1;"));
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 1 AND [PatronCodeId] IN (7, 9);"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND r.[Mode] = N'required' AND r.[LabelOverride] = N'Who is it for?';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book' AND o.[MessageBehavior] = N'message' AND o.[Message] = N'Format message' AND o.[AuthorMode] = N'optional' AND o.[AuthorLabel] = NCHAR(133) + N'Creator' + NCHAR(133) AND o.[TitleLabel] = N'Title' AND o.[IdentifierMode] = N'hidden' AND o.[IdentifierLabel] IS NULL AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'ISBN';"),
                "The raw source format identity keeps only values differing from its mapped base, while current effective labels remain intact.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience_empty' AND m.[Code] = N'book' AND r.[Mode] = N'optional' AND r.[LabelOverride] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'book' AND r.[Mode] = N'required' AND r.[LabelOverride] = N'Who is it for?';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience_empty' AND m.[Code] = N'audiobook_cd' AND r.[Mode] = N'hidden' AND r.[LabelOverride] = NCHAR(133) + N'Not trimmed' + NCHAR(133);"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [HelpText] = N'Choose' + NCHAR(133) + N' the intended audience.';"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook' AND r.[Mode] = N'hidden' AND r.[LabelOverride] IS NULL;"));
            Assert.AreEqual(5, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND (([FieldKey] = N'audience' AND [SortOrder] = 10 AND [Label] = N'Audience') OR ([FieldKey] = N'audience_empty' AND [SortOrder] = 0 AND [Label] = NCHAR(133) + N'Audience without enabled options' + NCHAR(133)) OR ([FieldKey] = N'audience_disabled' AND [SortOrder] = 30 AND [Label] = N'Disabled audience') OR ([FieldKey] = N'i_audience_name' AND [SortOrder] = 40 AND [Label] = N'İ Audience Name') OR ([FieldKey] = N'text_ignores_options' AND [FieldType] = N'text' AND [SortOrder] = 50 AND [Label] = N'Text ignores options'));"));
            Assert.AreEqual(5, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2;"));
            Assert.AreEqual(5, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND ((f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[SortOrder] = 10) OR (f.[FieldKey] = N'audience' AND o.[OptionKey] = N'teen' AND o.[Label] = N'Teen' + NCHAR(133) + N' Option' AND o.[SortOrder] = 20) OR (f.[FieldKey] = N'audience_empty' AND o.[OptionKey] = N'retired' AND o.[Label] = N'Retired' AND o.[SortOrder] = 0) OR (f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option' AND o.[Label] = N'İ Adult Option' AND o.[SortOrder] = 10) OR (f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'teen_option' AND o.[SortOrder] = 20));"));
            Assert.AreEqual(5, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2;"));
            Assert.AreEqual(30, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = 2;"));
            Assert.AreEqual(0, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'text_ignores_options';"));
            Assert.AreEqual(6, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'text_ignores_options' AND r.[Mode] = N'hidden' AND r.[LabelOverride] IS NULL;"));
            Assert.AreEqual(2, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND m.[OwnerOrganizationId] = 1 AND m.[Code] IN (N'dvd', N'music_cd') AND r.[Mode] = N'hidden' AND r.[LabelOverride] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook' AND r.[Mode] = N'hidden' AND r.[LabelOverride] IS NULL;"));
            var disabledAudienceRules = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var disabledAudienceCommand = connection.CreateCommand())
            {
                disabledAudienceCommand.CommandText =
                    "SELECT f.[Code], r.[Mode] FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] c ON c.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND c.[FieldKey] = N'audience_disabled' ORDER BY f.[Code];";
                await using var disabledAudienceReader = await disabledAudienceCommand.ExecuteReaderAsync();
                while (await disabledAudienceReader.ReadAsync())
                {
                    disabledAudienceRules.Add(disabledAudienceReader.GetString(0), disabledAudienceReader.GetString(1));
                }
            }
            CollectionAssert.AreEqual(
                new[] { "audiobook_cd", "book", "dvd", "eaudiobook", "ebook", "music_cd" },
                disabledAudienceRules.Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray());
            Assert.IsTrue(disabledAudienceRules.Values.All(mode => mode == "hidden"));
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[LegacyId] IS NULL AND r.[CustomFieldsJson] = N'{\" İ Audience Name \":{\"label\":\"Historical key\",\"type\":\"text\",\"value\":\"keep\"}}';"),
                "The mapping locates the request while its historical field snapshot retains the original identity and values.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 1 AND [LogoContentType] = N'image/png' AND [LogoFileName] = N'migration_logo.png' AND DATALENGTH([LogoData]) > 1000 AND [LogoAltText] = N'Consortium logo' AND [UpdatedUtc] = '2029-02-01T00:00:00.0000000';"));

            var customFieldDriftCases = new (string Name, string DriftSql, string DriftAssertionSql, string RestoreSql, string RestoredAssertionSql, int RestoredCount)[]
            {
                ("custom field label", "UPDATE [asap].[PatronCustomField] SET [Label] = N'Changed audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [Label] = N'Changed audience';", "UPDATE [asap].[PatronCustomField] SET [Label] = N'Audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [Label] = N'Audience';", 1),
                ("custom field help text", "UPDATE [asap].[PatronCustomField] SET [HelpText] = N'Changed help.' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [HelpText] = N'Changed help.';", "UPDATE [asap].[PatronCustomField] SET [HelpText] = N'Choose' + NCHAR(133) + N' the intended audience.' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [HelpText] = N'Choose' + NCHAR(133) + N' the intended audience.';", 1),
                ("custom field type", "UPDATE [asap].[PatronCustomField] SET [FieldType] = N'text' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [FieldType] = N'text';", "UPDATE [asap].[PatronCustomField] SET [FieldType] = N'select' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [FieldType] = N'select';", 1),
                ("custom field enabled state", "UPDATE [asap].[PatronCustomField] SET [IsEnabled] = 0 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [IsEnabled] = 0;", "UPDATE [asap].[PatronCustomField] SET [IsEnabled] = 1 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [IsEnabled] = 1;", 1),
                ("custom field order", "UPDATE [asap].[PatronCustomField] SET [SortOrder] = 31 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience_disabled';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience_disabled' AND [SortOrder] = 31;", "UPDATE [asap].[PatronCustomField] SET [SortOrder] = 30 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience_disabled';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience_disabled' AND [SortOrder] = 30;", 1),
                ("custom field explicit order", "UPDATE [asap].[PatronCustomField] SET [SortOrder] = 11 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [SortOrder] = 11;", "UPDATE [asap].[PatronCustomField] SET [SortOrder] = 10 WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [SortOrder] = 10;", 1),
                ("custom field key case only", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'Audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] COLLATE Latin1_General_100_BIN2 = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] COLLATE Latin1_General_100_BIN2 = N'Audience';", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] COLLATE Latin1_General_100_BIN2 = N'Audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] COLLATE Latin1_General_100_BIN2 = N'audience';", 1),
                ("custom field key", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'audience-drift' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience-drift';", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience-drift';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", 1),
                ("custom-field option identity", "UPDATE o SET [OptionKey] = N'adult-drift' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult-drift';", "UPDATE o SET [OptionKey] = N'adult' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult-drift';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", 1),
                ("custom-field option label", "UPDATE o SET [Label] = N'Changed adult' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[Label] = N'Changed adult';", "UPDATE o SET [Label] = N'Adult' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[Label] = N'Adult';", 1),
                ("custom-field option enabled state", "UPDATE o SET [IsEnabled] = 0 FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[IsEnabled] = 0;", "UPDATE o SET [IsEnabled] = 1 FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[IsEnabled] = 1;", 1),
                ("custom-field option order", "UPDATE o SET [SortOrder] = 11 FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[SortOrder] = 11;", "UPDATE o SET [SortOrder] = 10 FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'adult' AND o.[SortOrder] = 10;", 1),
                ("extra custom-field option", "INSERT INTO [asap].[PatronCustomFieldOption] ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder]) SELECT [Id], N'unmapped-option', N'Unmapped option', 1, 99 FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'unmapped-option';", "DELETE o FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'unmapped-option';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND o.[OptionKey] = N'unmapped-option';", 0),
                ("extra custom-field definition", "INSERT INTO [asap].[PatronCustomField] ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder]) VALUES (2, N'unmapped-field', N'text', N'Unmapped field', 1, 99);", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'unmapped-field';", "DELETE FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'unmapped-field';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'unmapped-field';", 0),
                ("extra custom-field format rule", "INSERT INTO [asap].[MaterialFormatCustomFieldRule] ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode]) SELECT 3, m.[Id], f.[Id], N'optional' FROM [asap].[MaterialFormat] m CROSS JOIN [asap].[PatronCustomField] f WHERE m.[OwnerOrganizationId] = 1 AND m.[Code] = N'book' AND f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 3 AND m.[OwnerOrganizationId] = 1 AND m.[Code] = N'book' AND f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience';", "DELETE r FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 3 AND m.[OwnerOrganizationId] = 1 AND m.[Code] = N'book' AND f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] WHERE r.[LibraryOrganizationId] = 3 AND m.[OwnerOrganizationId] = 1 AND m.[Code] = N'book' AND f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience';", 0)
            };
            foreach (var drift in customFieldDriftCases)
            {
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection, target, package, report, connectionEnvironmentName,
                    drift.Name, drift.DriftSql, drift.DriftAssertionSql, 1,
                    drift.RestoreSql, drift.RestoredAssertionSql, drift.RestoredCount);
            }

            var normalizedCustomFieldDriftCases = new (string Name, string DriftSql, string DriftAssertionSql, string RestoreSql, string RestoredAssertionSql)[]
            {
                ("normalized custom-field key", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'i_audience_name_drift' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'i_audience_name';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'i_audience_name_drift';", "UPDATE [asap].[PatronCustomField] SET [FieldKey] = N'i_audience_name' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'i_audience_name_drift';", "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'i_audience_name';"),
                ("normalized custom-field option ID", "UPDATE o SET [OptionKey] = N'i_adult_option_drift' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option_drift';", "UPDATE o SET [OptionKey] = N'i_adult_option' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option_drift';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option';"),
                ("name-alias option label", "UPDATE o SET [Label] = N'Drifted Unicode option' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option' AND o.[Label] = N'Drifted Unicode option';", "UPDATE o SET [Label] = N'İ Adult Option' FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option';", "SELECT COUNT(*) FROM [asap].[PatronCustomFieldOption] o JOIN [asap].[PatronCustomField] f ON f.[Id] = o.[PatronCustomFieldId] WHERE f.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND o.[OptionKey] = N'i_adult_option' AND o.[Label] = N'İ Adult Option';"),
                ("sparse-null custom-field rule", "UPDATE r SET [Mode] = N'optional' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook' AND r.[Mode] = N'optional';", "UPDATE r SET [Mode] = N'hidden' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'i_audience_name' AND m.[Code] = N'ebook' AND r.[Mode] = N'hidden';"),
                ("sparse-null format rule", "UPDATE r SET [Mode] = N'optional' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND m.[Code] = N'dvd';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND m.[Code] = N'dvd' AND r.[Mode] = N'optional';", "UPDATE r SET [Mode] = N'hidden' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND m.[Code] = N'dvd';", "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience' AND m.[Code] = N'dvd' AND r.[Mode] = N'hidden';")
            };
            foreach (var drift in normalizedCustomFieldDriftCases)
            {
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection, target, package, report, connectionEnvironmentName,
                    drift.Name, drift.DriftSql, drift.DriftAssertionSql, 1,
                    drift.RestoreSql, drift.RestoredAssertionSql, 1);
            }
            await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                connection, target, package, report, connectionEnvironmentName,
                "sparse-null fields object default",
                "UPDATE [asap].[MaterialFormat] SET [AuthorLabel] = N'Drifted sparse default' WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd' AND [AuthorLabel] = N'Drifted sparse default';",
                1,
                "UPDATE [asap].[MaterialFormat] SET [AuthorLabel] = N'Author' WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd' AND [AuthorLabel] = N'Author';",
                1);
            await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                connection, target, package, report, connectionEnvironmentName,
                "sparse-null individual field default",
                "UPDATE o SET [TitleLabel] = N'Drifted individual null' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book' AND o.[TitleLabel] = N'Drifted individual null';",
                1,
                "UPDATE o SET [TitleLabel] = N'Title' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book' AND o.[TitleLabel] = N'Title';",
                1);

            var configurationAndHistoryDriftCases = new (string Name, string DriftSql, string DriftAssertionSql, string RestoreSql, string RestoredAssertionSql)[]
            {
                ("system settings update timestamp", "UPDATE [asap].[SystemSettings] SET [UpdatedUtc] = '2029-02-02T00:00:00' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [UpdatedUtc] = '2029-02-02T00:00:00';", "UPDATE [asap].[SystemSettings] SET [UpdatedUtc] = '2029-02-01T00:00:00' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [UpdatedUtc] = '2029-02-01T00:00:00';"),
                ("system not-enabled message", "UPDATE [asap].[SystemSettings] SET [SystemNotEnabledMessage] = N'Drifted availability message.' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [SystemNotEnabledMessage] = N'Drifted availability message.';", "UPDATE [asap].[SystemSettings] SET [SystemNotEnabledMessage] = N'   ' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [SystemNotEnabledMessage] = N'   ' ;"),
                ("seeded misconfigured message", "UPDATE [asap].[SystemSettings] SET [MisconfiguredMessage] = N'Drifted misconfigured message.' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [MisconfiguredMessage] = N'Drifted misconfigured message.';", "UPDATE [asap].[SystemSettings] SET [MisconfiguredMessage] = N'The {{library}} suggestion system is currently misconfigured. Please contact staff.' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [MisconfiguredMessage] = N'The {{library}} suggestion system is currently misconfigured. Please contact staff.';"),
                ("target-only material seed label", "UPDATE [asap].[MaterialFormat] SET [AuthorLabel] = N'Drifted DVD creator' WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd' AND [AuthorLabel] = N'Drifted DVD creator';", "UPDATE [asap].[MaterialFormat] SET [AuthorLabel] = N'Director/Actors/Producer' WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd' AND [AuthorLabel] = N'Director/Actors/Producer';"),
                ("source-absent material seed key case only", "UPDATE [asap].[MaterialFormat] SET [Code] = N'DVD' WHERE [OwnerOrganizationId] = 1 AND [Code] COLLATE Latin1_General_100_BIN2 = N'dvd';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] COLLATE Latin1_General_100_BIN2 = N'DVD';", "UPDATE [asap].[MaterialFormat] SET [Code] = N'dvd' WHERE [OwnerOrganizationId] = 1 AND [Code] COLLATE Latin1_General_100_BIN2 = N'DVD';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] COLLATE Latin1_General_100_BIN2 = N'dvd';"),
                ("material format message field", "UPDATE [asap].[MaterialFormat] SET [Message] = N'Drifted format message' WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook' AND [Message] = N'Drifted format message';", "UPDATE [asap].[MaterialFormat] SET [Message] = NULL WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook';", "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook' AND [Message] IS NULL;"),
                ("organization display name", "UPDATE [asap].[Organization] SET [DisplayName] = N'Drifted Library' WHERE [Id] = 2;", "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [DisplayName] = N'Drifted Library';", "UPDATE [asap].[Organization] SET [DisplayName] = N'Test Library' WHERE [Id] = 2;", "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [DisplayName] = N'Test Library';"),
                ("organization mapping source key case only", "UPDATE [asap].[LegacyPocketBaseMapping] SET [PocketBaseId] = N'PB-ORG-2' WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'organization' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-org-2';", "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'organization' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'PB-ORG-2';", "UPDATE [asap].[LegacyPocketBaseMapping] SET [PocketBaseId] = N'pb-org-2' WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'organization' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'PB-ORG-2';", "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'organization' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-org-2';"),
                ("staff mapping entity type case only", "UPDATE [asap].[LegacyPocketBaseMapping] SET [EntityType] = N'STAFF_USER' WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'staff_user' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-staff-2';", "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'STAFF_USER' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-staff-2';", "UPDATE [asap].[LegacyPocketBaseMapping] SET [EntityType] = N'staff_user' WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'STAFF_USER' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-staff-2';", "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] COLLATE Latin1_General_100_BIN2 = N'staff_user' AND [PocketBaseId] COLLATE Latin1_General_100_BIN2 = N'pb-staff-2';"),
                ("system URL configuration", "UPDATE [asap].[SystemSettings] SET [LeapBibUrlPattern] = N'https://drift.example/bib/{{bibId}}' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [LeapBibUrlPattern] = N'https://drift.example/bib/{{bibId}}';", "UPDATE [asap].[SystemSettings] SET [LeapBibUrlPattern] = N'https://leap.example/bib/{{bibId}}' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1 AND [LeapBibUrlPattern] = N'https://leap.example/bib/{{bibId}}';"),
                ("workflow configuration", "UPDATE [asap].[WorkflowSettings] SET [SuggestionLimit] = 9 WHERE [OrganizationId] = 2;", "SELECT COUNT(*) FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2 AND [SuggestionLimit] = 9;", "UPDATE [asap].[WorkflowSettings] SET [SuggestionLimit] = 8 WHERE [OrganizationId] = 2;", "SELECT COUNT(*) FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2 AND [SuggestionLimit] = 8;"),
                ("common creator source order", "UPDATE [asap].[CommonCreatorTerm] SET [SortOrder] = 11 WHERE [OrganizationId] = 1 AND [Value] = N'Butler, Octavia';", "SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1 AND [Value] = N'Butler, Octavia' AND [SortOrder] = 11;", "UPDATE [asap].[CommonCreatorTerm] SET [SortOrder] = 10 WHERE [OrganizationId] = 1 AND [Value] = N'Butler, Octavia';", "SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 1 AND [Value] = N'Butler, Octavia' AND [SortOrder] = 10;"),
                ("patron configuration", "UPDATE [asap].[PatronSettings] SET [PageTitle] = N'Drifted title' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1 AND [PageTitle] = N'Drifted title';", "UPDATE [asap].[PatronSettings] SET [PageTitle] = N'Suggest an Item' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 1 AND [PageTitle] = N'Suggest an Item';"),
                ("email template configuration", "UPDATE [asap].[EmailTemplate] SET [SubjectTemplate] = N'Drifted: {{title}}' WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';", "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted' AND [SubjectTemplate] = N'Drifted: {{title}}';", "UPDATE [asap].[EmailTemplate] SET [SubjectTemplate] = N'Received: {{title}}' WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';", "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted' AND [SubjectTemplate] = N'Received: {{title}}';"),
                ("branding configuration", "UPDATE [asap].[Branding] SET [LogoAltText] = N'Drifted branding' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 1 AND [LogoAltText] = N'Drifted branding';", "UPDATE [asap].[Branding] SET [LogoAltText] = N'Consortium logo' WHERE [OrganizationId] = 1;", "SELECT COUNT(*) FROM [asap].[Branding] WHERE [OrganizationId] = 1 AND [LogoAltText] = N'Consortium logo';"),
                ("external-search provider override", "UPDATE o SET [Label] = N'Drifted catalog' FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1';", "SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1' AND o.[Label] = N'Drifted catalog';", "UPDATE o SET [Label] = N'Local Catalog' FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1';", "SELECT COUNT(*) FROM [asap].[ExternalSearchProviderOverride] o JOIN [asap].[ExternalSearchProvider] p ON p.[Id] = o.[ExternalSearchProviderId] WHERE o.[LibraryOrganizationId] = 2 AND p.[ProviderKey] = N'external_search_1' AND o.[Label] = N'Local Catalog';"),
                ("publication option configuration", "UPDATE [asap].[PublicationOption] SET [Label] = N'Drifted preorder' WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder';", "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder' AND [Label] = N'Drifted preorder';", "UPDATE [asap].[PublicationOption] SET [Label] = N'Local preorder' WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder';", "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2 AND [OptionKey] = N'local_preorder' AND [Label] = N'Local preorder';"),
                ("title request history", "UPDATE r SET [Notes] = N'Drifted request note.' FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1';", "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1' AND r.[Notes] = N'Drifted request note.';", "UPDATE r SET [Notes] = N'Source request note.' FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1';", "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1' AND r.[Notes] = N'Source request note.';"),
                ("title request claim history", "UPDATE r SET [ClaimedByDisplayName] = N'Drifted claimant' FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1';", "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1' AND r.[ClaimedByDisplayName] = N'Drifted claimant';", "UPDATE r SET [ClaimedByDisplayName] = N'Library Selector' FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1';", "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[NewId] = r.[Id] WHERE m.[PocketBaseId] = N'pb-request-1' AND r.[ClaimedByDisplayName] = N'Library Selector';"),
                ("title-request event history", "UPDATE e SET [Message] = N'Drifted event history.' FROM [asap].[TitleRequestEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'event-placed';", "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'event-placed' AND e.[Message] = N'Drifted event history.';", "UPDATE e SET [Message] = N'Existing hold adopted.' FROM [asap].[TitleRequestEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'event-placed';", "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'event-placed' AND e.[Message] = N'Existing hold adopted.'"),
                ("email delivery history", "UPDATE e SET [MetadataJson] = JSON_MODIFY(e.[MetadataJson], '$.recipient', N'drift@example.org') FROM [asap].[EmailDeliveryEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_delivery_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'mail-history-1';", "SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_delivery_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'mail-history-1' AND JSON_VALUE(e.[MetadataJson], '$.recipient') = N'drift@example.org';", "UPDATE e SET [MetadataJson] = JSON_MODIFY(e.[MetadataJson], '$.recipient', N'patron@example.org') FROM [asap].[EmailDeliveryEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_delivery_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'mail-history-1';", "SELECT COUNT(*) FROM [asap].[EmailDeliveryEvent] e JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'email_delivery_event' AND m.[NewId] = e.[Id] WHERE m.[PocketBaseId] = N'mail-history-1' AND JSON_VALUE(e.[MetadataJson], '$.recipient') = N'patron@example.org';")
            };
            foreach (var drift in configurationAndHistoryDriftCases)
            {
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection, target, package, report, connectionEnvironmentName,
                    drift.Name, drift.DriftSql, drift.DriftAssertionSql, 1,
                    drift.RestoreSql, drift.RestoredAssertionSql, 1);
            }

            await using (var requiredSelectDrift = connection.CreateCommand())
            {
                requiredSelectDrift.CommandText =
                    "UPDATE r SET [Mode] = N'required' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience_empty' AND m.[Code] = N'book';";
                Assert.AreEqual(1, await requiredSelectDrift.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var requiredSelectDriftFingerprint = ComputeTargetFingerprintForTest(target);
            var requiredSelectDriftReportBytes = await File.ReadAllBytesAsync(report);
            using (var normalizationDriftError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, report, connectionEnvironmentName, normalizationDriftError));
                StringAssert.Contains(normalizationDriftError.ToString(), "Imported custom field format mode and label override differs from the immutable source package.");
            }
            Assert.AreEqual(requiredSelectDriftFingerprint, ComputeTargetFingerprintForTest(target),
                "Reconciliation must not repair source-inconsistent custom-field rules.");
            CollectionAssert.AreEqual(requiredSelectDriftReportBytes, await File.ReadAllBytesAsync(report),
                "Failed reconciliation must not rewrite the refreshed report.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience_empty' AND m.[Code] = N'book' AND r.[Mode] = N'required';"),
                "The rejected required-select drift remains present until explicitly restored.");
            await using (var restoreRule = connection.CreateCommand())
            {
                restoreRule.CommandText =
                    "UPDATE r SET [Mode] = N'optional' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND f.[FieldKey] = N'audience_empty' AND m.[Code] = N'book';";
                Assert.AreEqual(1, await restoreRule.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            using (var normalizationRestored = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, normalizationRestored), normalizationRestored.ToString());
            }

            await using (var wrongAuthority = connection.CreateCommand())
            {
                wrongAuthority.CommandText =
                    "UPDATE r SET [BibIdStaffVerified] = 1 FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[BibId] = 9001;";
                Assert.AreEqual(1, await wrongAuthority.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var wrongAuthorityFingerprint = ComputeTargetFingerprintForTest(target);
            var wrongAuthorityReportBytes = await File.ReadAllBytesAsync(report);
            using var authorityDriftError = new StringWriter();
            Assert.AreEqual(1, MigrationCli.Run(
                [
                    "reconcile", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                TextWriter.Null,
                authorityDriftError));
            StringAssert.Contains(authorityDriftError.ToString(), "BIB authority fields differ from the immutable source classification inputs.");
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[BibId] = 9001 AND r.[BibIdStaffVerified] = 1;"),
                "Rejected BIB authority drift remains present until explicitly restored.");
            Assert.AreEqual(wrongAuthorityFingerprint, ComputeTargetFingerprintForTest(target),
                "Reconciliation must not repair source-inconsistent BIB authority.");
            CollectionAssert.AreEqual(wrongAuthorityReportBytes, await File.ReadAllBytesAsync(report),
                "Failed BIB-authority reconciliation must not rewrite the refreshed report.");
            await using (var restoreAuthority = connection.CreateCommand())
            {
                restoreAuthority.CommandText =
                    "UPDATE r SET [BibIdStaffVerified] = 0 FROM [asap].[TitleRequest] r JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'title_request' AND m.[PocketBaseId] = N'pb-request-1' AND m.[NewId] = r.[Id] WHERE r.[BibId] = 9001;";
                Assert.AreEqual(1, await restoreAuthority.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            using var authorityRestored = new StringWriter();
            Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, authorityRestored), authorityRestored.ToString());
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
    public async Task ImportRejectsForeignRelationshipsAndIndependentlyDetectsTargetDrift()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-owner-oracle-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationOwnerOracle_{Guid.NewGuid():N}";
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var foreignFormatRoot = Path.Combine(root, "foreign-format");
            var foreignFormatPackage = CreateMinimalPackage(
                foreignFormatRoot,
                """
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                    [code] TEXT, [label] TEXT, [enabled] INTEGER, [sortOrder] INTEGER
                );
                INSERT INTO [material_formats] VALUES
                    ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('fmt-foreign', 'library', '3', 'foreign', 'Foreign Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                    [title] TEXT, [autohold] INTEGER, [status] TEXT, [created] TEXT, [updated] TEXT
                );
                INSERT INTO [title_requests] VALUES
                    ('request-foreign-format', '2', 'fmt-foreign', 'A20000000000021', 'Foreign format',
                     0, 'suggestion', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            var foreignFormatReport = Path.Combine(foreignFormatRoot, "report.json");
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunImport(foreignFormatPackage, foreignFormatReport, connectionEnvironmentName, tenantId, error));
                StringAssert.Contains(error.ToString(), "request_format_owner_invalid");
            }
            Assert.IsFalse(File.Exists(foreignFormatReport + ".pending"));
            await AssertFreshImportTargetAsync(target);

            var foreignCopyRoot = Path.Combine(root, "foreign-copy");
            var foreignCopyPackage = CreateMinimalPackage(
                foreignCopyRoot,
                """
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                    [code] TEXT, [label] TEXT, [enabled] INTEGER, [sortOrder] INTEGER
                );
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                    [title] TEXT, [autohold] INTEGER, [status] TEXT, [created] TEXT, [updated] TEXT
                );
                INSERT INTO [title_requests] VALUES
                    ('request-owning-library', '2', 'fmt-book', 'A20000000000022', 'Source request',
                     0, 'suggestion', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [additional_copy_requests]
                (
                    [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [sourceTitleRequest] TEXT,
                    [bibid] TEXT, [title] TEXT, [status] TEXT, [created] TEXT, [updated] TEXT
                );
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-foreign-library', '3', 'request-owning-library', '9001', 'Copy', 'open',
                     '2029-01-03T00:00:00Z', '2029-01-04T00:00:00Z');
                """);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunImport(foreignCopyPackage, Path.Combine(foreignCopyRoot, "report.json"), connectionEnvironmentName, tenantId, error));
                StringAssert.Contains(error.ToString(), "additional_copy_request_library_mismatch");
            }
            await AssertFreshImportTargetAsync(target);

            foreach (var (name, snapshot) in new[]
            {
                ("array-snapshot", "[]"),
                ("unsupported-snapshot", "{\"retired\":{\"label\":\"Old\",\"type\":\"text\",\"value\":\"keep\",\"future\":true}}"),
                ("duplicate-snapshot", "{\"retired\":{\"label\":\"Old\",\"type\":\"text\",\"value\":\"keep\",\"label\":\"duplicate\"}}")
            })
            {
                var caseRoot = Path.Combine(root, name);
                var package = CreateMinimalPackage(
                    caseRoot,
                    $$"""
                    CREATE TABLE [material_formats]
                    (
                        [id] TEXT PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                        [code] TEXT, [label] TEXT, [enabled] INTEGER, [sortOrder] INTEGER
                    );
                    INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                    CREATE TABLE [title_requests]
                    (
                        [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                        [title] TEXT, [autohold] INTEGER, [status] TEXT, [customFields] TEXT,
                        [created] TEXT, [updated] TEXT
                    );
                    INSERT INTO [title_requests] VALUES
                        ('request-invalid-snapshot', '2', 'fmt-book', 'A20000000000023', 'Invalid snapshot',
                         0, 'suggestion', '{{snapshot.Replace("'", "''", StringComparison.Ordinal)}}',
                         '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                    """);
                using var error = new StringWriter();
                Assert.AreEqual(1, RunImport(package, Path.Combine(caseRoot, "report.json"), connectionEnvironmentName, tenantId, error), name);
                StringAssert.Contains(error.ToString(), "custom_fields_snapshot_invalid", name);
                await AssertFreshImportTargetAsync(target);
            }

            var validRoot = Path.Combine(root, "valid");
            var validPackage = CreateMinimalPackage(
                validRoot,
                """
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                    [code] TEXT, [label] TEXT, [enabled] INTEGER, [sortOrder] INTEGER
                );
                INSERT INTO [material_formats] VALUES
                    ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('fmt-foreign', 'library', '3', 'foreign', 'Foreign Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                    [title] TEXT, [autohold] INTEGER, [status] TEXT, [customFields] TEXT,
                    [created] TEXT, [updated] TEXT
                );
                INSERT INTO [title_requests] VALUES
                    ('request-1', '2', 'fmt-book', 'A20000000000024', '{"a":1,"a":2}',
                     0, 'suggestion', '{"retired":{"label":"Old","type":"text","value":"Preserve me"}}',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            const string alternateSourceSha = "abcdef0123456789abcdef0123456789abcdef01";
            SetPackageSourceGitSha(validPackage, alternateSourceSha);
            var reportPath = Path.Combine(validRoot, "report.json");
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(validPackage, reportPath, connectionEnvironmentName, tenantId, error), error.ToString());
            }

            using (var importedReport = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                var placementTransform = importedReport.RootElement.GetProperty("transformations").EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "placed_bib_protection");
                Assert.AreEqual("no_placement_evidence", placementTransform.GetProperty("action").GetString());
                Assert.AreEqual(JsonValueKind.Array, placementTransform.GetProperty("hints").ValueKind);
                Assert.AreEqual(JsonValueKind.Array, placementTransform.GetProperty("bibSources").ValueKind);
            }

            long requestId;
            long systemFormatId;
            long foreignFormatId;
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                requestId = await ReadLongAsync(connection,
                    "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'title_request' AND [PocketBaseId] = N'request-1';");
                systemFormatId = await ReadLongAsync(connection,
                    "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format' AND [PocketBaseId] = N'fmt-book';");
                foreignFormatId = await ReadLongAsync(connection,
                    "SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 3 AND [Code] = N'foreign';");
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Id] = " + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " AND [Title] = N'{\"a\":1,\"a\":2}' AND [CustomFieldsJson] = N'{\"retired\":{\"label\":\"Old\",\"type\":\"text\",\"value\":\"Preserve me\"}}' AND [PatronIdSnapshot] IS NULL;"));
                await using var mutate = connection.CreateCommand();
                mutate.CommandText = "UPDATE [asap].[TitleRequest] SET [MaterialFormatId] = @formatId WHERE [Id] = @requestId;";
                mutate.Parameters.AddWithValue("@formatId", foreignFormatId);
                mutate.Parameters.AddWithValue("@requestId", requestId);
                await mutate.ExecuteNonQueryAsync();
            }
            RefreshReportFingerprint(reportPath, target);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(validPackage, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "target title request lost its source library or format relationship");
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var mutate = connection.CreateCommand();
                mutate.CommandText = "UPDATE [asap].[TitleRequest] SET [MaterialFormatId] = @formatId WHERE [Id] = @requestId;";
                mutate.Parameters.AddWithValue("@formatId", systemFormatId);
                mutate.Parameters.AddWithValue("@requestId", requestId);
                await mutate.ExecuteNonQueryAsync();
            }
            RefreshReportFingerprint(reportPath, target);
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var fabricateSnapshot = connection.CreateCommand();
                fabricateSnapshot.CommandText = "UPDATE [asap].[TitleRequest] SET [PatronIdSnapshot] = 12345 WHERE [Id] = @requestId;";
                fabricateSnapshot.Parameters.AddWithValue("@requestId", requestId);
                Assert.AreEqual(1, await fabricateSnapshot.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            var patronSnapshotFingerprint = ComputeTargetFingerprintForTest(target);
            using (var patronIdDriftError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(validPackage, reportPath, connectionEnvironmentName, patronIdDriftError));
                StringAssert.Contains(patronIdDriftError.ToString(), "legacy title request has a fabricated native patron ID snapshot");
            }
            Assert.AreEqual(patronSnapshotFingerprint, ComputeTargetFingerprintForTest(target), "Reconciliation must not clear an invented patron identity.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restoreSnapshot = connection.CreateCommand();
                restoreSnapshot.CommandText = "UPDATE [asap].[TitleRequest] SET [PatronIdSnapshot] = NULL WHERE [Id] = @requestId;";
                restoreSnapshot.Parameters.AddWithValue("@requestId", requestId);
                Assert.AreEqual(1, await restoreSnapshot.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            using (var legacyIdentityRestored = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(validPackage, reportPath, connectionEnvironmentName, legacyIdentityRestored), legacyIdentityRestored.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                try
                {
                    await using (var disableConstraint = connection.CreateCommand())
                    {
                        disableConstraint.CommandText = "ALTER TABLE [asap].[Organization] NOCHECK CONSTRAINT [CK_Organization_SystemActive];";
                        await disableConstraint.ExecuteNonQueryAsync();
                    }
                    await using (var deactivateSystem = connection.CreateCommand())
                    {
                        deactivateSystem.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 1;";
                        await deactivateSystem.ExecuteNonQueryAsync();
                    }
                    RefreshReportFingerprint(reportPath, target);
                    var inactiveFingerprint = ComputeTargetFingerprintForTest(target);
                    using var inactiveError = new StringWriter();
                    Assert.AreEqual(1, RunReconcile(validPackage, reportPath, connectionEnvironmentName, inactiveError));
                    StringAssert.Contains(inactiveError.ToString(), "Target organization activity differs from the pinned source transformation.");
                    Assert.AreEqual(inactiveFingerprint, ComputeTargetFingerprintForTest(target), "Reconciliation must not reactivate the system organization.");
                }
                finally
                {
                    await using (var reactivateSystem = connection.CreateCommand())
                    {
                        reactivateSystem.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 1;";
                        await reactivateSystem.ExecuteNonQueryAsync();
                    }
                    await using (var reenableConstraint = connection.CreateCommand())
                    {
                        reenableConstraint.CommandText = "ALTER TABLE [asap].[Organization] WITH CHECK CHECK CONSTRAINT [CK_Organization_SystemActive];";
                        await reenableConstraint.ExecuteNonQueryAsync();
                    }
                }
            }
            RefreshReportFingerprint(reportPath, target);

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var corruptSnapshot = connection.CreateCommand();
                corruptSnapshot.CommandText = "UPDATE [asap].[TitleRequest] SET [CustomFieldsJson] = N'[]' WHERE [Id] = @requestId;";
                corruptSnapshot.Parameters.AddWithValue("@requestId", requestId);
                await corruptSnapshot.ExecuteNonQueryAsync();
            }
            RefreshReportFingerprint(reportPath, target);
            using (var snapshotDriftError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(validPackage, reportPath, connectionEnvironmentName, snapshotDriftError));
                StringAssert.Contains(snapshotDriftError.ToString(), "custom_fields_snapshot_invalid");
            }
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restoreSnapshot = connection.CreateCommand();
                restoreSnapshot.CommandText = "UPDATE [asap].[TitleRequest] SET [CustomFieldsJson] = N'{\"retired\":{\"label\":\"Old\",\"type\":\"text\",\"value\":\"Preserve me\"}}' WHERE [Id] = @requestId;";
                restoreSnapshot.Parameters.AddWithValue("@requestId", requestId);
                await restoreSnapshot.ExecuteNonQueryAsync();
            }
            RefreshReportFingerprint(reportPath, target);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(validPackage, reportPath, connectionEnvironmentName, error), error.ToString());
            }

            using (var committedReport = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                Assert.AreEqual(alternateSourceSha, committedReport.RootElement.GetProperty("sourceGitSha").GetString());
            }

            var report = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
            report["sourceGitSha"] = "ffffffffffffffffffffffffffffffffffffffff";
            File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(validPackage, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_report_mismatch");
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
    [DoNotParallelize]
    public async Task ImportMaterialFormatsProcessesSystemBeforeSameCodeLibraryOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-order-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationFormatOrder_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                INSERT INTO [polaris_organizations] VALUES
                    ('pb-org-3', '3', 'Partial Rules Library', 'PART', 1, 2, 1),
                    ('pb-org-4', '4', 'Empty Rules Library', 'EMPTY', 1, 2, 1),
                    ('pb-org-5', '5', 'Book Fallback Library', 'BOOK', 1, 2, 1),
                    ('pb-org-6', '6', 'Uppercase Rule Key Library', 'UPPER', 1, 2, 1),
                    ('pb-org-7', '7', 'Uppercase Rule Member Library', 'MEMBER', 1, 2, 1),
                    ('pb-org-8', '8', 'Uppercase Rule Field Library', 'FIELD', 1, 2, 1);
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL,
                    [messageBehavior] TEXT, [titleMode] TEXT, [titleLabel] TEXT,
                    [authorMode] TEXT, [authorLabel] TEXT, [identifierMode] TEXT, [identifierLabel] TEXT,
                    [publicationMode] TEXT, [publicationLabel] TEXT
                );
                INSERT INTO [material_formats] VALUES
                    ('a-local', 'library', 'pb-org-2', 'same-new-code', '  Library Format  ', 1, 20,
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    ('z-system', 'system', NULL, 'same-new-code', 'System Format', 1, 10,
                     'none', 'required', 'Title', 'hidden', 'System Custom Author', 'optional', 'System Identifier', 'optional', 'System Publication'),
                    ('system-book', 'system', NULL, 'book', 'Book', 1, 10,
                     'none', 'required', 'Title', 'hidden', 'System Book Author', 'optional', 'System Identifier', 'optional', 'System Timing'),
                    ('system-audiobook', 'system', NULL, 'audiobook_cd', 'Audiobook CD', 1, 20,
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    ('system-dvd', 'system', NULL, 'dvd', 'DVD', 1, 30,
                     'none', 'optional', 'Title', 'hidden', 'System Director', 'optional', 'System UPC', 'optional', 'System Timing'),
                    ('system-music', 'system', NULL, 'music_cd', 'Music CD', 1, 35,
                     'none', 'hidden', 'Title', 'optional', 'System Artist', 'optional', 'System UPC', 'optional', 'System Timing'),
                    ('system-ebook', 'system', NULL, 'ebook', 'eBook', 1, 38,
                     'ebookMessage', 'required', 'Title', 'required', 'Author', 'optional', 'Identifier number', 'required', 'Publication Timing'),
                    ('library-ebook', 'library', 'pb-org-2', 'ebook', '', 1, 40,
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
                    ('owned-library-format', 'library', 'pb-org-2', 'local-special', 'Local Special', 1, 50,
                     NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [orgId] TEXT NOT NULL,
                    [patronFormatRules] TEXT, [additionalFieldDefinitions] TEXT,
                    [created] TEXT, [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('partial-rules-3', '3', '{"dvd":{"messageBehavior":"message","message":"   ","fields":{"title":{"mode":"hidden"}},"customFields":{"audience":{"mode":"REQUIRED","labelOverride":"Wrong Label","label":"  DVD Audience  "}}},"book":{"messageBehavior":"RETIRED_BEHAVIOR","fields":{"author":{"mode":"HIDDEN","label":"  Book Author  "}}}}', '[{"key":"audience","type":"text","label":"Audience"}]', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('empty-rules-4', '4', '{}', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('book-fallback-5', '5', '{"book":{"messageBehavior":"message","message":"  Explicit Book Rule Message  ","fields":{"title":{"mode":"optional"},"author":{"mode":"hidden","label":"  Explicit Book Author  "}}}}', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('uppercase-key-6', '6', '{"BOOK":{"fields":{"author":{"mode":"hidden"}}}}', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('uppercase-member-7', '7', '{"book":{"Fields":{"author":{"mode":"hidden"}}}}', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('uppercase-field-8', '8', '{"book":{"MESSAGEBEHAVIOR":"message","fields":{"author":{"MODE":"hidden"}}}}', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", environmentName,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", reportPath,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                TextWriter.Null,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var format = connection.CreateCommand();
            format.CommandText = "SELECT [Id], [Label] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'same-new-code';";
            long formatId;
            await using (var reader = await format.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync());
                formatId = reader.GetInt64(0);
                Assert.AreEqual("System Format", reader.GetString(1));
                Assert.IsFalse(await reader.ReadAsync());
            }
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'same-new-code';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = 2 AND [MaterialFormatId] = " + formatId + " AND [Label] = N'  Library Format  ' AND [SortOrder] = 20 AND [IsEnabled] = 1 AND [MessageBehavior] = N'none' AND [AuthorMode] = N'required' AND [AuthorLabel] = N'Author' AND [PublicationMode] = N'required' AND [PublicationLabel] = N'Publication Timing';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [Label] = N'Local Special' AND [SortOrder] = 50 AND [IsEnabled] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] f JOIN [asap].[LegacyPocketBaseMapping] m ON m.[NewId] = f.[Id] AND m.[EntityType] = N'material_format' WHERE f.[OwnerOrganizationId] = 2 AND f.[Code] = N'local-special' AND m.[PocketBaseId] = N'owned-library-format';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format' AND [PocketBaseId] = N'z-system' AND [NewId] = " + formatId + ";"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format_override' AND [PocketBaseId] = N'a-local';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book' AND [AuthorMode] = N'hidden' AND [AuthorLabel] = N'System Book Author';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'audiobook_cd' AND [MessageBehavior] = N'none' AND [TitleMode] = N'required' AND [TitleLabel] = N'Title' AND [AuthorMode] = N'required' AND [AuthorLabel] = N'Author' AND [IdentifierMode] = N'optional' AND [IdentifierLabel] = N'Identifier number' AND [PublicationMode] = N'required' AND [PublicationLabel] = N'Publication Timing';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'dvd' AND [TitleMode] = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'music_cd' AND [TitleMode] = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'ebook' AND [MessageBehavior] = N'ebookMessage' AND [Message] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook' AND [MessageBehavior] = N'eaudiobookMessage' AND [Message] IS NULL;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'same-new-code' AND [IsEnabled] = 1;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook' AND [IsEnabled] = 0;"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook' AND o.[Label] IS NULL;"));
            using (var importedReport = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath)))
            {
                var unavailableSeeds = importedReport.RootElement.GetProperty("transformations")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("entity").GetString() == "system_material_format_availability")
                    .GetProperty("unavailableSeedCodes")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToArray();
                CollectionAssert.AreEqual(new[] { "eaudiobook" }, unavailableSeeds);
            }
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'none' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required' AND COALESCE(o.[PublicationLabel], f.[PublicationLabel]) = N'Publication Timing';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'book' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'none' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Book Author' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required' AND COALESCE(o.[PublicationLabel], f.[PublicationLabel]) = N'Publication Timing';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'dvd' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'message' AND COALESCE(o.[Message], f.[Message], N'') = N'' AND COALESCE(o.[TitleMode], f.[TitleMode]) = N'required' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Director/Actors/Producer' AND COALESCE(o.[IdentifierMode], f.[IdentifierMode]) = N'hidden' AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'UPC' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'music_cd' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Artist' AND COALESCE(o.[IdentifierMode], f.[IdentifierMode]) = N'hidden' AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'UPC' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'ebook' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'message' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'eaudiobook' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'message' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'same-new-code' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Book Author';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 4 AND f.[Code] = N'book' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 4 AND f.[Code] = N'same-new-code' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'none' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author' AND COALESCE(o.[IdentifierMode], f.[IdentifierMode]) = N'optional' AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'Identifier number' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required' AND COALESCE(o.[PublicationLabel], f.[PublicationLabel]) = N'Publication Timing';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 5 AND f.[Code] = N'same-new-code' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'message' AND COALESCE(o.[Message], f.[Message]) = N'Explicit Book Rule Message' AND COALESCE(o.[TitleMode], f.[TitleMode]) = N'required' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'hidden' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Explicit Book Author';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 6 AND f.[Code] = N'book' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 7 AND f.[Code] = N'book' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required';"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 8 AND f.[Code] = N'book' AND COALESCE(o.[MessageBehavior], f.[MessageBehavior]) = N'none' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required';"));
            await using (var customFieldRule = connection.CreateCommand())
            {
                customFieldRule.CommandText = "SELECT r.[Mode], r.[LabelOverride] FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] c ON c.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 3 AND c.[FieldKey] = N'audience' AND f.[Code] = N'dvd';";
                await using var customFieldReader = await customFieldRule.ExecuteReaderAsync();
                Assert.IsTrue(await customFieldReader.ReadAsync());
                Assert.AreEqual("hidden", customFieldReader.GetString(0));
                Assert.AreEqual("DVD Audience", customFieldReader.GetString(1));
                Assert.IsFalse(await customFieldReader.ReadAsync());
            }
            await using (var ebookMessage = connection.CreateCommand())
            {
                ebookMessage.CommandText = "SELECT COALESCE(o.[Message], f.[Message]) FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = 3 WHERE f.[OwnerOrganizationId] = 1 AND f.[Code] = N'ebook';";
                Assert.AreEqual(
                    "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
                    await ebookMessage.ExecuteScalarAsync());
            }
            await using (var eaudiobookMessage = connection.CreateCommand())
            {
                eaudiobookMessage.CommandText = "SELECT COALESCE(o.[Message], f.[Message]) FROM [asap].[MaterialFormat] f LEFT JOIN [asap].[MaterialFormatOverride] o ON o.[MaterialFormatId] = f.[Id] AND o.[LibraryOrganizationId] = 3 WHERE f.[OwnerOrganizationId] = 1 AND f.[Code] = N'eaudiobook';";
                Assert.AreEqual(
                    "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
                    await eaudiobookMessage.ExecuteScalarAsync());
            }

            await using (var corruptSparseDefault = connection.CreateCommand())
            {
                corruptSparseDefault.CommandText = "UPDATE o SET [AuthorMode] = N'optional' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook';";
                Assert.AreEqual(1, await corruptSparseDefault.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            using (var sparseReconcileError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, reportPath, environmentName, sparseReconcileError));
                StringAssert.Contains(sparseReconcileError.ToString(), "reconciliation_failed");
            }
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook' AND o.[AuthorMode] = N'optional';"));
            await using (var restoreSparseDefault = connection.CreateCommand())
            {
                restoreSparseDefault.CommandText = "UPDATE o SET [AuthorMode] = N'required' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook';";
                Assert.AreEqual(1, await restoreSparseDefault.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            using (var validReconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, validReconcileError), validReconcileError.ToString());
            }

            await AssertFingerprintRefreshedFormatDriftRejectedAsync(
                connection,
                target,
                package,
                reportPath,
                environmentName,
                "UPDATE [asap].[MaterialFormat] SET [Label] = N'Changed Label' WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [Label] = N'Changed Label';",
                "UPDATE [asap].[MaterialFormat] SET [Label] = N'Local Special' WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [Label] = N'Local Special';",
                "source record");
            await AssertFingerprintRefreshedFormatDriftRejectedAsync(
                connection,
                target,
                package,
                reportPath,
                environmentName,
                "UPDATE [asap].[MaterialFormat] SET [SortOrder] = 51 WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [SortOrder] = 51;",
                "UPDATE [asap].[MaterialFormat] SET [SortOrder] = 50 WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [SortOrder] = 50;",
                "source record");
            await AssertFingerprintRefreshedFormatDriftRejectedAsync(
                connection,
                target,
                package,
                reportPath,
                environmentName,
                "UPDATE [asap].[MaterialFormat] SET [IsEnabled] = 0 WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [IsEnabled] = 0;",
                "UPDATE [asap].[MaterialFormat] SET [IsEnabled] = 1 WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 2 AND [Code] = N'local-special' AND [IsEnabled] = 1;",
                "source record");
            await AssertFingerprintRefreshedFormatDriftRejectedAsync(
                connection,
                target,
                package,
                reportPath,
                environmentName,
                "UPDATE o SET [Label] = N'eBook' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook' AND o.[Label] = N'eBook';",
                "UPDATE o SET [Label] = NULL FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'ebook' AND o.[Label] IS NULL;",
                "source record");
            await AssertFingerprintRefreshedFormatDriftRejectedAsync(
                connection,
                target,
                package,
                reportPath,
                environmentName,
                "UPDATE [asap].[MaterialFormat] SET [IsEnabled] = 1 WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook' AND [IsEnabled] = 1;",
                "UPDATE [asap].[MaterialFormat] SET [IsEnabled] = 0 WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook';",
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'eaudiobook' AND [IsEnabled] = 0;",
                "Target system material format availability or empty message differs from the immutable source package and pinned target seed.");

            await using (var corrupt = connection.CreateCommand())
            {
                corrupt.CommandText = "UPDATE o SET [AuthorMode] = N'optional' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'book';";
                Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(Path.Combine(root, "report.json"), target);
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, Path.Combine(root, "report.json"), environmentName, reconcileError));
                StringAssert.Contains(reconcileError.ToString(), "reconciliation_failed");
            }
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'book' AND o.[AuthorMode] = N'optional';"));

            await AssertFormatMappingKindDriftRejectedAsync(root, master, tenantId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportFormatRuleDefaultsUseTheRawLegacyCodeBeforeTargetNormalization()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-raw-code-{Guid.NewGuid():N}");
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            foreach (var sourceCode in new[] { "0", "Book" })
            {
                var caseName = sourceCode == "0" ? "zero" : "capital-book";
                var caseRoot = Path.Combine(root, caseName);
                var databaseName = $"AsapMigrationRawFormat_{Guid.NewGuid():N}";
                var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
                Directory.CreateDirectory(caseRoot);
                try
                {
                    var directRules = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        [sourceCode] = new Dictionary<string, object?>
                        {
                            ["customFields"] = new Dictionary<string, object?>
                            {
                                ["audience"] = new Dictionary<string, object?>
                                {
                                    ["mode"] = "required",
                                    ["label"] = $"Direct {sourceCode}"
                                }
                            }
                        }
                    });
                    var invalidDirectRules = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        [sourceCode] = new Dictionary<string, object?>
                        {
                            ["customFields"] = new Dictionary<string, object?>
                            {
                                ["audience"] = new Dictionary<string, object?>
                                {
                                    ["mode"] = 1
                                }
                            }
                        }
                    });
                    const string bookFallbackRules = """{"book":{"customFields":{"audience":{"mode":"optional","label":"Book fallback"}}}}""";
                    const string definitions = """[{"key":"audience","label":"Audience","type":"text","enabled":true,"sortOrder":10}]""";
                    string CreateAdditionalSql(string directRuleJson) => $$"""
                        INSERT INTO [polaris_organizations] VALUES
                            ('pb-org-3', '3', 'Book Fallback Library', 'FALLBACK', 1, 2, 1);
                        CREATE TABLE [material_formats]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                            [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL,
                            [messageBehavior] TEXT, [titleMode] TEXT, [titleLabel] TEXT,
                            [authorMode] TEXT, [authorLabel] TEXT, [identifierMode] TEXT, [identifierLabel] TEXT,
                            [publicationMode] TEXT, [publicationLabel] TEXT
                        );
                        INSERT INTO [material_formats] VALUES
                            ('format-alias', 'system', NULL, '{{sourceCode}}', 'Legacy Alias', 1, 10,
                             'none', NULL, NULL, 'hidden', 'Legacy Author', NULL, NULL, NULL, NULL);
                        CREATE TABLE [patron_settings_overrides]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [orgId] TEXT NOT NULL,
                            [patronFormatRules] TEXT, [additionalFieldDefinitions] TEXT,
                            [created] TEXT, [updated] TEXT
                        );
                        INSERT INTO [patron_settings_overrides] VALUES
                            ('direct-rule', '2', '{{directRuleJson}}', '{{definitions}}', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                            ('book-fallback', '3', '{{bookFallbackRules}}', '{{definitions}}', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                        """;
                    var invalidRoot = Path.Combine(caseRoot, "invalid");
                    Directory.CreateDirectory(invalidRoot);
                    var invalidPackage = CreateMinimalPackage(invalidRoot, CreateAdditionalSql(invalidDirectRules));
                    var package = CreateMinimalPackage(caseRoot, CreateAdditionalSql(directRules));
                    DeployDacpac(master, databaseName);
                    Environment.SetEnvironmentVariable(environmentName, target);
                    var invalidReportPath = Path.Combine(invalidRoot, "report.json");
                    using (var invalidImportError = new StringWriter())
                    {
                        Assert.AreEqual(1, RunImport(invalidPackage, invalidReportPath, environmentName, tenantId, invalidImportError));
                        StringAssert.Contains(invalidImportError.ToString(), "custom_field_rule_invalid");
                    }
                    Assert.IsFalse(File.Exists(invalidReportPath));
                    Assert.IsFalse(File.Exists(invalidReportPath + ".pending"));
                    await AssertFreshImportTargetAsync(target);

                    var reportPath = Path.Combine(caseRoot, "report.json");
                    using (var importError = new StringWriter())
                    {
                        Assert.AreEqual(0, RunImport(package, reportPath, environmentName, tenantId, importError), importError.ToString());
                    }

                    await using var connection = new SqlConnection(target);
                    await connection.OpenAsync();
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        $"SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND m.[Code] = N'book' AND f.[FieldKey] = N'audience' AND r.[Mode] = N'required' AND r.[LabelOverride] = N'Direct {sourceCode}';"));
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] f ON f.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] m ON m.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 3 AND m.[Code] = N'book' AND f.[FieldKey] = N'audience' AND r.[Mode] = N'optional' AND r.[LabelOverride] = N'Book fallback';"));
                    using (var initialReconcileError = new StringWriter())
                    {
                        Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, initialReconcileError), initialReconcileError.ToString());
                    }
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'optional' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author' AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'Identifier' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'optional' AND COALESCE(o.[PublicationLabel], f.[PublicationLabel]) = N'Publication';"));
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 3 AND f.[Code] = N'book' AND COALESCE(o.[AuthorMode], f.[AuthorMode]) = N'required' AND COALESCE(o.[AuthorLabel], f.[AuthorLabel]) = N'Author' AND COALESCE(o.[IdentifierLabel], f.[IdentifierLabel]) = N'Identifier number' AND COALESCE(o.[PublicationMode], f.[PublicationMode]) = N'required' AND COALESCE(o.[PublicationLabel], f.[PublicationLabel]) = N'Publication Timing';"));

                    await using (var corruptCustomFieldRule = connection.CreateCommand())
                    {
                        corruptCustomFieldRule.CommandText = "UPDATE r SET [Mode] = N'optional' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] c ON c.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND c.[FieldKey] = N'audience' AND f.[Code] = N'book';";
                        Assert.AreEqual(1, await corruptCustomFieldRule.ExecuteNonQueryAsync());
                    }
                    RefreshReportFingerprint(reportPath, target);
                    using (var customFieldRuleReconcileError = new StringWriter())
                    {
                        Assert.AreEqual(1, RunReconcile(package, reportPath, environmentName, customFieldRuleReconcileError));
                        StringAssert.Contains(
                            customFieldRuleReconcileError.ToString(),
                            "Imported custom field format mode and label override differs from the immutable source package.");
                    }
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        $"SELECT COUNT(*) FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] c ON c.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND c.[FieldKey] = N'audience' AND f.[Code] = N'book' AND r.[Mode] = N'optional' AND r.[LabelOverride] = N'Direct {sourceCode}';"));
                    await using (var restoreCustomFieldRule = connection.CreateCommand())
                    {
                        restoreCustomFieldRule.CommandText = "UPDATE r SET [Mode] = N'required' FROM [asap].[MaterialFormatCustomFieldRule] r JOIN [asap].[PatronCustomField] c ON c.[Id] = r.[PatronCustomFieldId] JOIN [asap].[MaterialFormat] f ON f.[Id] = r.[MaterialFormatId] WHERE r.[LibraryOrganizationId] = 2 AND c.[FieldKey] = N'audience' AND f.[Code] = N'book';";
                        Assert.AreEqual(1, await restoreCustomFieldRule.ExecuteNonQueryAsync());
                    }
                    RefreshReportFingerprint(reportPath, target);

                    await using (var corruptRule = connection.CreateCommand())
                    {
                        corruptRule.CommandText = "UPDATE o SET [AuthorMode] = N'required' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book';";
                        Assert.AreEqual(1, await corruptRule.ExecuteNonQueryAsync());
                    }
                    RefreshReportFingerprint(reportPath, target);
                    using (var reconcileError = new StringWriter())
                    {
                        Assert.AreEqual(1, RunReconcile(package, reportPath, environmentName, reconcileError));
                        StringAssert.Contains(reconcileError.ToString(), "reconciliation_failed");
                    }
                    Assert.AreEqual(1, await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book' AND o.[AuthorMode] = N'required';"));

                    await using (var restoreRule = connection.CreateCommand())
                    {
                        restoreRule.CommandText = "UPDATE o SET [AuthorMode] = N'optional' FROM [asap].[MaterialFormatOverride] o JOIN [asap].[MaterialFormat] f ON f.[Id] = o.[MaterialFormatId] WHERE o.[LibraryOrganizationId] = 2 AND f.[Code] = N'book';";
                        Assert.AreEqual(1, await restoreRule.ExecuteNonQueryAsync());
                    }
                    RefreshReportFingerprint(reportPath, target);
                    using var validReconcileError = new StringWriter();
                    Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, validReconcileError), validReconcileError.ToString());
                }
                finally
                {
                    Environment.SetEnvironmentVariable(environmentName, null);
                    await DropDatabaseAsync(master, databaseName);
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportRejectsLibraryOnlyFormatsThatCanonicalizeToReservedSystemSeeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-library-seed-format-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationLibrarySeedFormat_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            foreach (var (caseName, sourceCode) in new[]
                     {
                         ("canonical-book", "book"),
                         ("numeric-book", "0"),
                         ("case-alias-book", "Book")
                     })
            {
                var caseRoot = Path.Combine(root, caseName);
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
                    INSERT INTO [material_formats] VALUES
                        ('library-format', 'library', 'pb-org-2', '{{sourceCode}}', 'Library-owned format', 1, 20);
                    """);
                var reportPath = Path.Combine(caseRoot, "report.json");
                using var error = new StringWriter();
                Assert.AreEqual(1, RunImport(package, reportPath, environmentName, tenantId, error));
                StringAssert.Contains(error.ToString(), "format_library_seed_unsupported");
                Assert.IsFalse(File.Exists(reportPath));
                Assert.IsFalse(File.Exists(reportPath + ".pending"));
                await AssertFreshImportTargetAsync(target);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportRejectsCrossScopeMaterialFormatAliasesThatChangeLegacyIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-cross-scope-alias-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationFormatCrossScopeAlias_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL,
                    UNIQUE ([scope], [libraryOrganization], [code])
                );
                INSERT INTO [material_formats] VALUES
                    ('system-book', 'system', NULL, 'book', 'System Book', 1, 10),
                    ('library-zero', 'library', 'pb-org-2', '0', 'Library Numeric Alias', 1, 20);
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using var error = new StringWriter();
            Assert.AreEqual(1, RunImport(package, reportPath, environmentName, tenantId, error));
            StringAssert.Contains(error.ToString(), "format_code_conflict");
            Assert.IsFalse(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(reportPath + ".pending"));
            await AssertFreshImportTargetAsync(target);

            var reverseRoot = Path.Combine(root, "reverse");
            var reversePackage = CreateMinimalPackage(
                reverseRoot,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL,
                    UNIQUE ([scope], [libraryOrganization], [code])
                );
                INSERT INTO [material_formats] VALUES
                    ('system-zero', 'system', NULL, '0', 'System Numeric Alias', 1, 10),
                    ('library-book', 'library', 'pb-org-2', 'book', 'Library Book', 1, 20);
                """);
            AssertPackageValidationCode(reversePackage, "format_code_conflict");
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportRejectsMaterialFormatAliasesThatCollapseWithinOneSourceOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-format-alias-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationFormatAlias_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL,
                    UNIQUE ([scope], [libraryOrganization], [code])
                );
                INSERT INTO [material_formats] VALUES
                    ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('fmt-zero', 'system', NULL, '0', 'Numeric Book Alias', 1, 20);
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using var error = new StringWriter();
            Assert.AreEqual(1, RunImport(package, reportPath, environmentName, tenantId, error));
            StringAssert.Contains(error.ToString(), "format_code_conflict");
            Assert.IsFalse(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(reportPath + ".pending"));
            await AssertFreshImportTargetAsync(target);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CommittedImportWithFailedReportPromotionRecoversReadOnlyAndIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-report-recovery-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationReportRecovery_{Guid.NewGuid():N}";
        var rolledBackDatabaseName = $"AsapMigrationReportRolledBack_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var rolledBackEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var rolledBackTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = rolledBackDatabaseName }.ConnectionString;
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var package = CreateMinimalPackage(
                Path.Combine(root, "package-source"),
                """
                INSERT INTO [polaris_organizations] VALUES
                    ('pb-org-3', '3', 'Audit Format Library', 'AUDIT', 1, 2, 1);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [format] TEXT, [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [claimedByStaffUserId] TEXT, [claimedByDisplayName] TEXT,
                    [claimedAt] TEXT, [claimType] TEXT, [claimRuleId] TEXT, [bibid] INTEGER,
                    [isbnCheckStatus] TEXT, [identifier] TEXT, [lastChecked] TEXT,
                    [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-recovery-claim', '2', NULL, 'book', 'A20000000000999', 'Recovery claim', 0,
                     'suggestion', 'pb-staff-1', 'Source Administrator', '2029-03-01T10:00:00Z', 'manual', NULL,
                     9001, 'found', '9780000000001', '2029-03-02T00:00:00Z',
                     '2029-03-01T00:00:00Z', '2029-03-02T00:00:00Z');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [orgId] TEXT NOT NULL,
                    [patronFormatRules] TEXT, [additionalFieldDefinitions] TEXT,
                    [created] TEXT, [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('fields-override', '2', '{"book":{"customFields":{"audience":{"mode":"optional","label":"Audience rule"}}}}',
                     '[{"key":"audience","type":"text","label":"Audience","enabled":true,"sortOrder":10}]',
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('format-override', '3', '{"book":{"fields":{"author":{"mode":"hidden"}}}}', NULL,
                     '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            var precommitReportPath = Path.Combine(root, "precommit-report.json");
            Directory.CreateDirectory(precommitReportPath + ".pending");
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunImport(package, precommitReportPath, connectionEnvironmentName, tenantId, error));
                StringAssert.Contains(error.ToString(), "import_report_prepare_failed");
            }
            Assert.IsFalse(File.Exists(precommitReportPath));
            Assert.IsFalse(File.Exists(precommitReportPath + ".pending.tmp"));
            await AssertFreshImportTargetAsync(target);
            Directory.Delete(precommitReportPath + ".pending");

            var reportPath = Path.Combine(root, "blocked-report-path");
            Directory.CreateDirectory(reportPath);

            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunImport(package, reportPath, connectionEnvironmentName, tenantId, error));
                StringAssert.Contains(error.ToString(), "import_committed_report_failed");
                StringAssert.Contains(error.ToString(), "SQL import committed");
            }

            var pendingPath = reportPath + ".pending";
            Assert.IsTrue(File.Exists(pendingPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(3, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [OrganizationCodeId] IS NOT NULL;"));
                Assert.AreEqual(4, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'organization';"));
            }

            var validPending = File.ReadAllText(pendingPath);
            var tamperedPending = JsonNode.Parse(validPending)!.AsObject();
            tamperedPending["packageIdentitySha256"] = new string('0', 64);
            File.WriteAllText(pendingPath, tamperedPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_report_mismatch");
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var targetIdentityTamper = JsonNode.Parse(validPending)!.AsObject();
            targetIdentityTamper["targetIdentitySha256"] = new string('0', 64);
            File.WriteAllText(pendingPath, targetIdentityTamper.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_report_target_mismatch");
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var missingVersion = JsonNode.Parse(validPending)!.AsObject();
            missingVersion.Remove("reportVersion");
            var wrongCountShape = JsonNode.Parse(validPending)!.AsObject();
            wrongCountShape["targetCounts"] = "not-an-object";
            foreach (var malformedReport in new[] { "{", missingVersion.ToJsonString(), wrongCountShape.ToJsonString() })
            {
                File.WriteAllText(pendingPath, malformedReport, new UTF8Encoding(false));
                var unchangedSqlFingerprint = ComputeTargetFingerprintForTest(target);
                using var error = new StringWriter();
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_report_invalid");
                Assert.AreEqual(unchangedSqlFingerprint, ComputeTargetFingerprintForTest(target), "Malformed reports must be rejected before target verification or mutation.");
                Assert.IsTrue(File.Exists(pendingPath));
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var requiredAuditSections = new[]
            {
                "claimReconciliation", "placementReconciliation", "bibAuthorityReconciliation", "sourceToTargetReconciliation"
            };
            foreach (var section in requiredAuditSections)
            {
                var missingSection = JsonNode.Parse(validPending)!.AsObject();
                missingSection.Remove(section);
                var wrongKindSection = JsonNode.Parse(validPending)!.AsObject();
                wrongKindSection[section] = "missing-audit-object";
                foreach (var altered in new[] { missingSection, wrongKindSection })
                {
                    File.WriteAllText(pendingPath, altered.ToJsonString(), new UTF8Encoding(false));
                    var unchangedSqlFingerprint = ComputeTargetFingerprintForTest(target);
                    using var error = new StringWriter();
                    Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                    StringAssert.Contains(error.ToString(), "reconciliation_report_invalid");
                    Assert.AreEqual(unchangedSqlFingerprint, ComputeTargetFingerprintForTest(target));
                    Assert.IsTrue(File.Exists(pendingPath));
                    Assert.IsFalse(File.Exists(reportPath));
                }
            }

            var falseSourceAudit = JsonNode.Parse(validPending)!.AsObject();
            falseSourceAudit["sourceToTargetReconciliation"]!["passed"] = false;
            var changedConfigurationFieldCount = JsonNode.Parse(validPending)!.AsObject();
            changedConfigurationFieldCount["sourceToTargetReconciliation"]!["configurationFieldsChecked"] = 999999;
            var changedConfigurationRelationshipCount = JsonNode.Parse(validPending)!.AsObject();
            changedConfigurationRelationshipCount["sourceToTargetReconciliation"]!["configurationRelationshipsChecked"] = 999999;
            var contradictorySourceCount = JsonNode.Parse(validPending)!.AsObject();
            contradictorySourceCount["sourceToTargetReconciliation"]!["titleRequests"] = 999;
            var contradictoryPlacementCount = JsonNode.Parse(validPending)!.AsObject();
            contradictoryPlacementCount["placementReconciliation"]!["protectedRequests"] = 999;
            var clearedTransformations = JsonNode.Parse(validPending)!.AsObject();
            clearedTransformations["transformations"] = new JsonArray();
            var changedImportedSourceCount = JsonNode.Parse(validPending)!.AsObject();
            changedImportedSourceCount["importedCounts"]!["staff_users"] = 0;
            var missingDerivedImportedCount = JsonNode.Parse(validPending)!.AsObject();
            missingDerivedImportedCount["importedCounts"]!.AsObject().Remove("title_request_bib_authority_automation_derived");
            var missingSourceAuditCount = JsonNode.Parse(validPending)!.AsObject();
            missingSourceAuditCount["sourceToTargetReconciliation"]!.AsObject().Remove("titleRequests");
            foreach (var altered in new[]
                     {
                         falseSourceAudit, changedConfigurationFieldCount, changedConfigurationRelationshipCount,
                         contradictorySourceCount, contradictoryPlacementCount,
                         clearedTransformations, changedImportedSourceCount, missingDerivedImportedCount, missingSourceAuditCount
                     })
            {
                File.WriteAllText(pendingPath, altered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                var unchangedSqlFingerprint = ComputeTargetFingerprintForTest(target);
                using var error = new StringWriter();
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_report_");
                Assert.AreEqual(unchangedSqlFingerprint, ComputeTargetFingerprintForTest(target));
                Assert.IsTrue(File.Exists(pendingPath));
                Assert.IsFalse(File.Exists(reportPath));
            }

            var malformedTransformationCases = new[]
            {
                (Entity: "staff_user", Property: "targetWeeklyEligible", WrongKind: "string"),
                (Entity: "title_request_bib_authority", Property: "classification", WrongKind: "number"),
                (Entity: "patron_custom_fields", Property: "downgradedRequiredSelectRules", WrongKind: "string"),
                (Entity: "patron_format_rules", Property: "formats", WrongKind: "string"),
                (Entity: "title_request_isbn_status", Property: "targetStatus", WrongKind: "number"),
                (Entity: "placed_bib_protection", Property: "hints", WrongKind: "string"),
                (Entity: "placed_bib_protection", Property: "bibSources", WrongKind: "string")
            };
            foreach (var (entity, property, wrongKind) in malformedTransformationCases)
            {
                foreach (var removeProperty in new[] { true, false })
                {
                    var altered = JsonNode.Parse(validPending)!.AsObject();
                    var transformation = altered["transformations"]!.AsArray()
                        .Single(item => item!["entity"]!.GetValue<string>() == entity)!.AsObject();
                    if (removeProperty)
                    {
                        transformation.Remove(property);
                    }
                    else if (wrongKind == "string")
                    {
                        transformation[property] = "wrong-kind";
                    }
                    else if (wrongKind == "number")
                    {
                        transformation[property] = JsonValue.Create(17);
                    }
                    else
                    {
                        Assert.Fail($"Unsupported wrong-kind fixture for {entity}.{property}.");
                    }

                    File.WriteAllText(pendingPath, altered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                    var unchangedSqlFingerprint = ComputeTargetFingerprintForTest(target);
                    using var error = new StringWriter();
                    Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                    StringAssert.Contains(error.ToString(), "reconciliation_report_invalid");
                    Assert.AreEqual(unchangedSqlFingerprint, ComputeTargetFingerprintForTest(target));
                    Assert.IsTrue(File.Exists(pendingPath));
                    Assert.IsFalse(File.Exists(reportPath));
                }
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var wrongClaimLibrary = JsonNode.Parse(validPending)!.AsObject();
            wrongClaimLibrary["claimReconciliation"]!["titleRequests"]![0]!["libraryOrganizationId"] = 20;
            var wrongClaimStatus = JsonNode.Parse(validPending)!.AsObject();
            wrongClaimStatus["claimReconciliation"]!["titleRequests"]![0]!["status"] = "outstanding_purchase";
            foreach (var altered in new[] { wrongClaimLibrary, wrongClaimStatus })
            {
                File.WriteAllText(pendingPath, altered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                var unchangedSqlFingerprint = ComputeTargetFingerprintForTest(target);
                using var error = new StringWriter();
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Claim-reconciliation groups differ from source library, status, and attribution inputs.");
                Assert.AreEqual(unchangedSqlFingerprint, ComputeTargetFingerprintForTest(target));
                Assert.IsTrue(File.Exists(pendingPath));
                Assert.IsFalse(File.Exists(reportPath));
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            const string originalCiphertext = "migration-fingerprint-ciphertext-original";
            const string changedCiphertext = "migration-fingerprint-ciphertext-changed";
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var set = connection.CreateCommand();
                set.CommandText = "UPDATE [asap].[PolarisSettings] SET [ProtectedApiKey] = @ciphertext WHERE [OrganizationId] = 1;";
                set.Parameters.AddWithValue("@ciphertext", originalCiphertext);
                Assert.AreEqual(1, await set.ExecuteNonQueryAsync());
            }
            var ciphertextFingerprint = ComputeTargetFingerprintForTest(target);
            var ciphertextPending = JsonNode.Parse(validPending)!.AsObject();
            ciphertextPending["targetFingerprintSha256"] = ciphertextFingerprint;
            File.WriteAllText(pendingPath, ciphertextPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var mutate = connection.CreateCommand();
                mutate.CommandText = "UPDATE [asap].[PolarisSettings] SET [ProtectedApiKey] = @ciphertext WHERE [OrganizationId] = 1;";
                mutate.Parameters.AddWithValue("@ciphertext", changedCiphertext);
                Assert.AreEqual(1, await mutate.ExecuteNonQueryAsync());
            }
            var changedCiphertextFingerprint = ComputeTargetFingerprintForTest(target);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Imported Polaris source fields, protected-secret presence and imported timestamp differs from the immutable source package.");
            }
            Assert.AreEqual(changedCiphertextFingerprint, ComputeTargetFingerprintForTest(target), "Report recovery must not rewrite protected values.");
            Assert.IsFalse(File.ReadAllText(pendingPath).Contains(originalCiphertext, StringComparison.Ordinal));
            Assert.IsFalse(File.ReadAllText(pendingPath).Contains(changedCiphertext, StringComparison.Ordinal));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restore = connection.CreateCommand();
                restore.CommandText = "UPDATE [asap].[PolarisSettings] SET [ProtectedApiKey] = NULL WHERE [OrganizationId] = 1;";
                Assert.AreEqual(1, await restore.ExecuteNonQueryAsync());
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO [asap].[QueueProgress] ([QueueName], [ScopeOrganizationId]) VALUES (N'IdentifierProcessing', 1);";
                await insert.ExecuteNonQueryAsync();
            }
            var queuedFingerprint = ComputeTargetFingerprintForTest(target);
            var queuedPending = JsonNode.Parse(validPending)!.AsObject();
            queuedPending["targetCounts"]!["queue_progress"] = 1;
            queuedPending["targetFingerprintSha256"] = queuedFingerprint;
            File.WriteAllText(pendingPath, queuedPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "target count queue_progress differs from source-derived expectations");
            }
            Assert.AreEqual(queuedFingerprint, ComputeTargetFingerprintForTest(target), "Recovery must reject importer-excluded queue state without mutating it.");
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.IsFalse(File.Exists(reportPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM [asap].[QueueProgress] WHERE [QueueName] = N'IdentifierProcessing' AND [ScopeOrganizationId] = 1;";
                Assert.AreEqual(1, await remove.ExecuteNonQueryAsync());
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            long extraRequestId;
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');
                    DECLARE @now datetime2(7) = SYSUTCDATETIME();
                    INSERT INTO [asap].[TitleRequest]
                        ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
                    OUTPUT inserted.[Id]
                    VALUES (2, N'recovery-extra-request', N'Unmapped recovery row', 0, @formatId, N'suggestion', @now, @now);
                    """;
                extraRequestId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }
            var extraRequestFingerprint = ComputeTargetFingerprintForTest(target);
            var extraRequestPending = JsonNode.Parse(validPending)!.AsObject();
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                extraRequestPending["targetCounts"]!["title_requests"] = await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest];");
            }
            extraRequestPending["targetFingerprintSha256"] = extraRequestFingerprint;
            File.WriteAllText(pendingPath, extraRequestPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Target TitleRequest population contains rows outside the immutable source package.");
            }
            Assert.AreEqual(extraRequestFingerprint, ComputeTargetFingerprintForTest(target), "Recovery must not delete an unmapped ordinary request.");
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.IsFalse(File.Exists(reportPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM [asap].[TitleRequest] WHERE [Id] = @id;";
                remove.Parameters.AddWithValue("@id", extraRequestId);
                Assert.AreEqual(1, await remove.ExecuteNonQueryAsync());
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            long extraStaffUserId;
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO [asap].[StaffUser] ([UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive]) OUTPUT inserted.[Id] VALUES (N'extra-recovery@example.org', N'EXTRA-RECOVERY@EXAMPLE.ORG', N'staff', 2, 0);";
                extraStaffUserId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }
            var extraStaffFingerprint = ComputeTargetFingerprintForTest(target);
            var extraStaffPending = JsonNode.Parse(validPending)!.AsObject();
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                extraStaffPending["targetCounts"]!["staff_users"] = await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];");
            }
            extraStaffPending["targetFingerprintSha256"] = extraStaffFingerprint;
            File.WriteAllText(pendingPath, extraStaffPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Imported exact staff population including only the authorized bootstrap row differs from the immutable source package.");
            }
            Assert.AreEqual(extraStaffFingerprint, ComputeTargetFingerprintForTest(target), "Recovery must not remove an unmapped staff row.");
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.IsFalse(File.Exists(reportPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM [asap].[StaffUser] WHERE [Id] = @id;";
                remove.Parameters.AddWithValue("@id", extraStaffUserId);
                Assert.AreEqual(1, await remove.ExecuteNonQueryAsync());
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var inactivate = connection.CreateCommand();
                inactivate.CommandText = "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [NormalizedUserPrincipalName] = N'SOURCE-ADMIN@EXAMPLE.ORG';";
                Assert.AreEqual(1, await inactivate.ExecuteNonQueryAsync());
            }
            var inactiveStaffFingerprint = ComputeTargetFingerprintForTest(target);
            var inactiveStaffPending = JsonNode.Parse(validPending)!.AsObject();
            inactiveStaffPending["targetFingerprintSha256"] = inactiveStaffFingerprint;
            File.WriteAllText(pendingPath, inactiveStaffPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Imported staff source fields and pinned authentication-email transform differs from the immutable source package.");
            }
            Assert.AreEqual(inactiveStaffFingerprint, ComputeTargetFingerprintForTest(target), "Recovery must not reactivate a source staff row.");
            Assert.IsTrue(File.Exists(pendingPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var reactivate = connection.CreateCommand();
                reactivate.CommandText = "UPDATE [asap].[StaffUser] SET [IsActive] = 1 WHERE [NormalizedUserPrincipalName] = N'SOURCE-ADMIN@EXAMPLE.ORG';";
                Assert.AreEqual(1, await reactivate.ExecuteNonQueryAsync());
            }
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            DeployDacpac(master, rolledBackDatabaseName);
            Environment.SetEnvironmentVariable(rolledBackEnvironmentName, rolledBackTarget);
            var rolledBackFingerprint = ComputeTargetFingerprintForTest(rolledBackTarget);
            var rollbackPending = JsonNode.Parse(validPending)!.AsObject();
            rollbackPending["targetFingerprintSha256"] = rolledBackFingerprint;
            rollbackPending["targetIdentitySha256"] = ComputeTargetIdentityForTest(rolledBackTarget);
            File.WriteAllText(pendingPath, rollbackPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, rolledBackEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Target organization name, abbreviation, native type, parent, or synchronization timestamp differs from the source snapshot.");
            }
            Assert.AreEqual(rolledBackFingerprint, ComputeTargetFingerprintForTest(rolledBackTarget), "Recovery must not populate a rolled-back target.");
            await AssertFreshImportTargetAsync(rolledBackTarget);
            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 2;";
                await corrupt.ExecuteNonQueryAsync();
            }
            var corruptFingerprint = ComputeTargetFingerprintForTest(target);
            var corruptPending = JsonNode.Parse(validPending)!.AsObject();
            corruptPending["targetFingerprintSha256"] = corruptFingerprint;
            File.WriteAllText(pendingPath, corruptPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Target organization activity differs");
            }
            Assert.AreEqual(corruptFingerprint, ComputeTargetFingerprintForTest(target), "Failed recovery must not change the corrupt target.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restore = connection.CreateCommand();
                restore.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;";
                await restore.ExecuteNonQueryAsync();
            }

            long branchMappingId;
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                branchMappingId = await ReadLongAsync(connection,
                    "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'organization' AND [PocketBaseId] = N'pb-org-20';");
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'organization' AND [PocketBaseId] = N'pb-org-20';";
                await remove.ExecuteNonQueryAsync();
            }
            var partialFingerprint = ComputeTargetFingerprintForTest(target);
            var partialPending = JsonNode.Parse(validPending)!.AsObject();
            partialPending["targetFingerprintSha256"] = partialFingerprint;
            File.WriteAllText(pendingPath, partialPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Organization source mapping keys or native identities differ from the immutable source package.");
            }
            Assert.AreEqual(partialFingerprint, ComputeTargetFingerprintForTest(target), "Failed recovery must not repair a partial target.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restore = connection.CreateCommand();
                restore.CommandText = "INSERT INTO [asap].[LegacyPocketBaseMapping] ([EntityType], [PocketBaseId], [NewId]) VALUES (N'organization', N'pb-org-20', @id);";
                restore.Parameters.AddWithValue("@id", branchMappingId);
                await restore.ExecuteNonQueryAsync();
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO [asap].[Organization] ([Id], [DisplayName], [OrganizationCodeId], [ParentOrganizationId], [IsActive]) VALUES (21, N'Unmapped branch', 3, 2, 0);";
                await insert.ExecuteNonQueryAsync();
            }
            var extraOrganizationFingerprint = ComputeTargetFingerprintForTest(target);
            var extraOrganizationPending = JsonNode.Parse(validPending)!.AsObject();
            extraOrganizationPending["targetFingerprintSha256"] = extraOrganizationFingerprint;
            File.WriteAllText(pendingPath, extraOrganizationPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "Target organization population includes rows outside the system seed and source native identities.");
            }
            Assert.AreEqual(extraOrganizationFingerprint, ComputeTargetFingerprintForTest(target), "Recovery must not remove an unmapped organization row.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var remove = connection.CreateCommand();
                remove.CommandText = "DELETE FROM [asap].[Organization] WHERE [Id] = 21;";
                await remove.ExecuteNonQueryAsync();
            }

            Directory.Delete(reportPath);
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var drift = connection.CreateCommand();
                drift.CommandText = "UPDATE [asap].[PatronCustomField] SET [Label] = N'Drifted recovery label' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';";
                Assert.AreEqual(1, await drift.ExecuteNonQueryAsync());
            }
            var customFieldDriftFingerprint = ComputeTargetFingerprintForTest(target);
            var customFieldDriftPending = JsonNode.Parse(validPending)!.AsObject();
            customFieldDriftPending["targetFingerprintSha256"] = customFieldDriftFingerprint;
            File.WriteAllText(pendingPath, customFieldDriftPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            var customFieldDriftPendingContents = await File.ReadAllTextAsync(pendingPath);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error));
                StringAssert.Contains(error.ToString(), "reconciliation_failed", "Recovery must reject source-configuration drift even when the pending report fingerprint is refreshed.");
            }
            Assert.AreEqual(customFieldDriftFingerprint, ComputeTargetFingerprintForTest(target), "Failed report recovery must not repair custom-field drift.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PatronCustomField] WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience' AND [Label] = N'Drifted recovery label';"));
            }
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.IsFalse(File.Exists(reportPath));
            Assert.AreEqual(customFieldDriftPendingContents, await File.ReadAllTextAsync(pendingPath), "Failed recovery must not rewrite or promote the pending report.");
            using (var pendingState = JsonDocument.Parse(await File.ReadAllTextAsync(pendingPath)))
            using (var originalPendingState = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual(
                    originalPendingState.RootElement.GetProperty("reportState").GetString(),
                    pendingState.RootElement.GetProperty("reportState").GetString());
            }
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restore = connection.CreateCommand();
                restore.CommandText = "UPDATE [asap].[PatronCustomField] SET [Label] = N'Audience' WHERE [LibraryOrganizationId] = 2 AND [FieldKey] = N'audience';";
                Assert.AreEqual(1, await restore.ExecuteNonQueryAsync());
            }

            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var noTokenFingerprint = ComputeTargetFingerprintForTest(target);
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var addTokenPresence = connection.CreateCommand();
                addTokenPresence.CommandText = "UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = N'migration-test-ciphertext' WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NULL;";
                Assert.AreEqual(1, await addTokenPresence.ExecuteNonQueryAsync(), "The zero-SMTP fixture must begin with no operator-provisioned token.");
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] = N'migration-test-ciphertext';"));
            }
            var tokenPresenceDriftFingerprint = ComputeTargetFingerprintForTest(target);
            var tokenPresenceDriftPending = JsonNode.Parse(validPending)!.AsObject();
            tokenPresenceDriftPending["targetFingerprintSha256"] = tokenPresenceDriftFingerprint;
            File.WriteAllText(pendingPath, tokenPresenceDriftPending.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            var tokenPresenceDriftPendingContents = await File.ReadAllTextAsync(pendingPath);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, reportPath, connectionEnvironmentName, error), "Recovery must reject a target-only token when the report records no token and the source has no SMTP row.");
                StringAssert.Contains(error.ToString(), "reconciliation_failed");
            }
            Assert.AreEqual(tokenPresenceDriftFingerprint, ComputeTargetFingerprintForTest(target), "Failed recovery must not repair protected-token presence drift.");
            Assert.IsTrue(File.Exists(pendingPath));
            Assert.IsFalse(File.Exists(reportPath));
            Assert.AreEqual(tokenPresenceDriftPendingContents, await File.ReadAllTextAsync(pendingPath), "Failed recovery must preserve the pending report bytes.");
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var restoreTokenPresence = connection.CreateCommand();
                restoreTokenPresence.CommandText = "UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = NULL WHERE [OrganizationId] = 1 AND [ProtectedServerToken] = N'migration-test-ciphertext';";
                Assert.AreEqual(1, await restoreTokenPresence.ExecuteNonQueryAsync());
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NOT NULL;"));
            }
            Assert.AreEqual(noTokenFingerprint, ComputeTargetFingerprintForTest(target), "Restoring the source-absent token state must restore the original SQL fingerprint.");

            File.WriteAllText(pendingPath, validPending, new UTF8Encoding(false));

            var fingerprintBefore = ComputeTargetFingerprintForTest(target);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, error), error.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            using (var report = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                Assert.AreEqual("recovered", report.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(report.RootElement.GetProperty("reconciliationPassed").GetBoolean());
                Assert.AreEqual(1, report.RootElement.GetProperty("importedCounts").GetProperty("staff_users").GetInt32());
                var staffUserTransformations = report.RootElement.GetProperty("transformations").EnumerateArray()
                    .Where(item => item.GetProperty("entity").GetString() == "staff_user")
                    .ToArray();
                Assert.AreEqual(1, staffUserTransformations.Length);
                Assert.AreEqual("pb-staff-1", staffUserTransformations[0].GetProperty("sourceId").GetString());
            }
            Assert.AreEqual(fingerprintBefore, ComputeTargetFingerprintForTest(target), "Recovery must not mutate SQL state.");

            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, error), error.ToString());
            }
            Assert.AreEqual(fingerprintBefore, ComputeTargetFingerprintForTest(target), "Repeated recovery must remain read-only.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            Environment.SetEnvironmentVariable(rolledBackEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            await DropDatabaseAsync(master, rolledBackDatabaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportReportsCommittedWhenPostCommitSqlVerificationFailsAndRecoversReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-postcommit-sql-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPostCommitSql_{Guid.NewGuid():N}";
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
        var databaseOffline = false;
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var package = CreateMinimalPackage(Path.Combine(root, "package-source"));
            var reportPath = Path.Combine(root, "report.json");
            var options = new MigrationImportOptions(
                package,
                target,
                new HashSet<Guid> { tenantId },
                reportPath,
                ExternalConfigurationPath(package));
            var importWithCommitHook = typeof(MigrationImporter).GetMethod(
                "Import",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(MigrationImportOptions), typeof(Action)],
                modifiers: null) ?? throw new InvalidOperationException("The post-commit regression hook is missing.");
            TargetInvocationException? invocationError = null;
            try
            {
                importWithCommitHook.Invoke(null,
                [
                    options,
                    (Action)(() =>
                    {
                        SetDatabaseOnline(master, databaseName, online: false);
                        databaseOffline = true;
                    })
                ]);
            }
            catch (TargetInvocationException exception)
            {
                invocationError = exception;
            }
            finally
            {
                if (databaseOffline)
                {
                    SetDatabaseOnline(master, databaseName, online: true);
                    databaseOffline = false;
                }
            }

            Assert.IsNotNull(invocationError, "The post-commit verifier should fail while the target database is offline.");
            var committedError = invocationError?.InnerException as MigrationOperationException;
            Assert.IsNotNull(committedError);
            Assert.AreEqual("import_committed_report_failed", committedError!.Code);
            Assert.IsFalse(File.Exists(reportPath));
            var pendingPath = reportPath + ".pending";
            Assert.IsTrue(File.Exists(pendingPath));
            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(2, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [OrganizationCodeId] IS NOT NULL;"));
                Assert.AreEqual(3, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'organization';"));
            }

            var fingerprintBeforeRecovery = ComputeTargetFingerprintForTest(target);
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, error), error.ToString());
            }
            Assert.AreEqual(fingerprintBeforeRecovery, ComputeTargetFingerprintForTest(target), "Report recovery must not mutate committed SQL state.");
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.AreEqual("recovered", report.RootElement.GetProperty("reportState").GetString());
            Assert.IsTrue(report.RootElement.GetProperty("reconciliationPassed").GetBoolean());
        }
        finally
        {
            if (databaseOffline)
            {
                SetDatabaseOnline(master, databaseName, online: true);
            }
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);

            var invalidCases = new[]
            {
                (Name: "found_without_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found", Identifier: "9780000000001", Bib: "NULL", Error: "identifier_found_without_bib"),
                (Name: "alias_without_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found_in_polaris", Identifier: "9780000000002", Bib: "NULL", Error: "identifier_found_without_bib"),
                (Name: "ambiguous_error", RequestStatus: "suggestion", CloseReason: "NULL", Status: "error", Identifier: "9780000000003", Bib: "NULL", Error: "identifier_error_ambiguous"),
                (Name: "ambiguous_pending_bib_authority", RequestStatus: "suggestion", CloseReason: "NULL", Status: "pending", Identifier: "9780000000004", Bib: "'9004'", Error: "bib_authority_ambiguous"),
                (Name: "ambiguous_retryable_bib_authority", RequestStatus: "suggestion", CloseReason: "NULL", Status: "error_max_retries", Identifier: "9780000000005", Bib: "'9005'", Error: "bib_authority_ambiguous"),
                (Name: "ambiguous_closed_reopen_bib_authority", RequestStatus: "closed", CloseReason: "'rejected'", Status: "pending", Identifier: "9780000000006", Bib: "'9006'", Error: "bib_authority_ambiguous"),
                (Name: "invalid_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found", Identifier: "9780000000007", Bib: "'not-a-bib'", Error: "source_bib_invalid"),
                (Name: "zero_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found", Identifier: "9780000000007", Bib: "'0'", Error: "source_bib_invalid"),
                (Name: "negative_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found", Identifier: "9780000000007", Bib: "'-1'", Error: "source_bib_invalid"),
                (Name: "overflow_bib", RequestStatus: "suggestion", CloseReason: "NULL", Status: "found", Identifier: "9780000000007", Bib: "'2147483648'", Error: "source_bib_invalid"),
                (Name: "unknown", RequestStatus: "suggestion", CloseReason: "NULL", Status: "mystery", Identifier: "NULL", Bib: "NULL", Error: "identifier_status_invalid")
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
                        [autohold] INTEGER NOT NULL, [status] TEXT NOT NULL, [closeReason] TEXT, [identifier] TEXT,
                        [bibid] TEXT, [isbnCheckStatus] TEXT, [isbnCheckRetryCount] INTEGER,
                        [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                    );
                    INSERT INTO [title_requests] VALUES
                        ('request-1', '2', 'fmt-book', 'A20000000000001', 'Invalid identifier state',
                         0, '{{item.RequestStatus}}', {{item.CloseReason}}, {{identifier}}, {{item.Bib}}, '{{item.Status}}', 3,
                         '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                    """);
                using var error = new StringWriter();

                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(caseRoot, "report.json"),
                        "--external-config", ExternalConfigurationPath(package)
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
                    ('request-4', '2', 'fmt-book', 'A20000000000004', 'Found', 0, 'suggestion', '9780000000004', '4', 'found', 3, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-5', '2', 'fmt-book', 'A20000000000005', 'Not found', 0, 'suggestion', '9780000000005', NULL, 'not_found', 4, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-6', '2', 'fmt-book', 'A20000000000006', 'Skipped', 0, 'suggestion', NULL, NULL, 'skipped_no_isbn', 9, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-7', '2', 'fmt-book', 'A20000000000007', 'Exhausted', 0, 'suggestion', '9780000000007', NULL, 'error_max_retries', 5, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-8', '2', 'fmt-book', 'A20000000000008', 'Missing identifier error', 0, 'suggestion', NULL, NULL, 'error', 7, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-9', '2', 'fmt-book', 'A20000000000009', 'Historical alias', 0, 'suggestion', '9780000000009', '9', 'found_in_polaris', 1, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            using var validError = new StringWriter();
            var validExitCode = MigrationCli.Run(
                [
                    "import", "--package", validPackage,
                    "--connection-string-env", connectionEnvironmentName,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", Path.Combine(validRoot, "report.json"),
                    "--external-config", ExternalConfigurationPath(validPackage)
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
    public async Task ImportRejectsInvalidPolarisIntegrationIdentityWithoutPartialSqlState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-polaris-identity-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPolarisIdentity_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
            { InitialCatalog = "master" }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var index = 0;
            foreach (var field in new[] { "workstationId", "userId" })
            {
                foreach (var value in new[] { "0", "-1", "2147483648", "bad" })
                {
                    var caseRoot = Path.Combine(root, $"case-{index++}");
                    Directory.CreateDirectory(caseRoot);
                    var workstation = field == "workstationId" ? value : "99";
                    var user = field == "userId" ? value : "42";
                    var package = CreateMinimalPackage(caseRoot, $"""
                        CREATE TABLE [polaris_settings] ([id] TEXT PRIMARY KEY,
                            [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT);
                        INSERT INTO [polaris_settings] VALUES ('native-identity', '{workstation}', '{user}', '7', '3');
                        """);
                    using var error = new StringWriter();
                    Assert.AreEqual(1, MigrationCli.Run([
                        "import", "--package", package, "--connection-string-env", environmentName,
                        "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                        "--report", Path.Combine(caseRoot, "report.json"),
                        "--external-config", ExternalConfigurationPath(package)
                    ], TextWriter.Null, error), $"{field}: {value}");
                    StringAssert.Contains(error.ToString(), "source_polaris_identity_invalid");
                    await using var connection = new SqlConnection(target);
                    await connection.OpenAsync();
                    Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];"));
                    Assert.AreEqual(1, await ScalarAsync(connection,
                        "SELECT COUNT(*) FROM [asap].[PolarisSettings] WHERE [WorkstationId] IS NULL AND [SystemPolarisUserId] IS NULL;"));
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ImportRejectsMalformedBibIdentityAtEverySourceBoundaryWithoutPartialSqlState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-native-bib-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationNativeBib_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var cases = new List<(string Boundary, string Bib, string Error)>();
            foreach (var boundary in new[] { "request", "copy", "deleted", "event" })
            {
                foreach (var bib in new[] { "not-a-bib", "0", "-1", "2147483648" })
                {
                    cases.Add((boundary, bib, "source_bib_invalid"));
                }
            }
            cases.Add(("event", """{"bibId":"9001","bibId":"9002"}""", "source_json_invalid"));
            cases.Add(("event", """{"bibId":"9001","BibID":"9002"}""", "source_json_invalid"));
            foreach (var (boundary, bib, expectedError) in cases)
            {
                var caseRoot = Path.Combine(root, $"case-{cases.IndexOf((boundary, bib, expectedError))}");
                Directory.CreateDirectory(caseRoot);
                var source = boundary switch
                {
                    "request" => $"""
                        CREATE TABLE [title_requests] (
                            [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                            [title] TEXT, [autohold] INTEGER, [status] TEXT, [bibid] TEXT,
                            [isbnCheckStatus] TEXT, [created] TEXT, [updated] TEXT);
                        INSERT INTO [title_requests] VALUES (
                            'native-request', '2', 'fmt-book', 'A20000000000001', 'Native BIB', 0,
                            'suggestion', '{bib}', 'found', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                        """,
                    "copy" => $"""
                        CREATE TABLE [additional_copy_requests] (
                            [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [bibid] TEXT, [title] TEXT,
                            [status] TEXT, [created] TEXT, [updated] TEXT);
                        INSERT INTO [additional_copy_requests] VALUES (
                            'native-copy', '2', '{bib}', 'Native copy', 'open',
                            '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                        """,
                    "deleted" => $"""
                        CREATE TABLE [deleted_request_audit] (
                            [id] TEXT PRIMARY KEY, [titleRequestId] TEXT, [libraryOrgId] TEXT,
                            [bibid] TEXT, [status] TEXT, [deletedAt] TEXT);
                        INSERT INTO [deleted_request_audit] VALUES (
                            'native-deleted', 'original-request', '2', '{bib}', 'closed', '2029-01-02T00:00:00Z');
                        """,
                    _ => $$"""
                        CREATE TABLE [title_requests] (
                            [id] TEXT PRIMARY KEY, [libraryOrgId] TEXT, [formatRef] TEXT, [barcode] TEXT,
                            [title] TEXT, [autohold] INTEGER, [status] TEXT, [created] TEXT, [updated] TEXT);
                        INSERT INTO [title_requests] VALUES (
                            'native-request', '2', 'fmt-book', 'A20000000000001', 'Placed BIB', 0,
                            'hold_placed', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                        CREATE TABLE [title_request_events] (
                            [id] TEXT PRIMARY KEY, [titleRequest] TEXT, [eventType] TEXT,
                            [actorType] TEXT, [metadata] TEXT, [created] TEXT);
                        INSERT INTO [title_request_events] VALUES (
                            'native-event', 'native-request', 'hold_placed', 'system',
                            '{{(bib.StartsWith('{') ? bib : JsonSerializer.Serialize(new { bibId = bib }))}}',
                            '2029-01-02T00:00:00Z');
                        """
                };
                var package = CreateMinimalPackage(caseRoot, """
                    CREATE TABLE [material_formats] (
                        [id] TEXT PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                        [code] TEXT, [label] TEXT, [enabled] INTEGER, [sortOrder] INTEGER);
                    INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                    """ + source);
                using var error = new StringWriter();
                var exitCode = MigrationCli.Run([
                    "import", "--package", package, "--connection-string-env", environmentName,
                    "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                    "--report", Path.Combine(caseRoot, "report.json"),
                    "--external-config", ExternalConfigurationPath(package)
                ], TextWriter.Null, error);
                Assert.AreEqual(1, exitCode, $"{boundary}: {bib}");
                StringAssert.Contains(error.ToString(), expectedError, $"{boundary}: {bib}");
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                foreach (var table in new[] { "StaffUser", "TitleRequest", "AdditionalCopyRequest", "DeletedRequestAudit" })
                {
                    Assert.AreEqual(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM [asap].[{table}];"),
                        $"{boundary}: {bib} must roll back {table}.");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
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
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
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
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var report = Path.Combine(root, "report.json");
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
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
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(caseRoot, "report.json"),
                        "--external-config", ExternalConfigurationPath(package)
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
                INSERT INTO [title_requests] VALUES ('request-1', '2', 'fmt-book', 'A20000000000001', 'BIB conflict', 0, 'hold_placed', '9001', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events] ([id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL, [eventType] TEXT NOT NULL, [actorType] TEXT NOT NULL, [metadata] TEXT, [created] TEXT NOT NULL);
                INSERT INTO [title_request_events] VALUES ('event-1', 'request-1', 'hold_placed', 'system', '{"bibId":"9002"}', '2029-01-03T00:00:00Z');
                """);
            using (var bibError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", bibConflictPackage,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", Path.Combine(bibConflictRoot, "report.json"),
                        "--external-config", ExternalConfigurationPath(bibConflictPackage)
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

            var ambiguousRoot = Path.Combine(root, "ambiguous");
            Directory.CreateDirectory(ambiguousRoot);
            var ambiguousPackage = CreateMinimalPackage(
                ambiguousRoot,
                """
                CREATE TABLE [material_formats] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL, [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL);
                INSERT INTO [material_formats] VALUES ('fmt-book', 'system', NULL, 'book', 'Book', 1, 10);
                CREATE TABLE [title_requests]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [libraryOrgId] TEXT NOT NULL, [formatRef] TEXT,
                    [barcode] TEXT NOT NULL, [title] TEXT NOT NULL, [autohold] INTEGER NOT NULL,
                    [status] TEXT NOT NULL, [closeReason] TEXT, [bibid] TEXT,
                    [created] TEXT NOT NULL, [updated] TEXT NOT NULL
                );
                INSERT INTO [title_requests] VALUES
                    ('request-ambiguous', '2', 'fmt-book', 'A20000000000012', 'Hint only', 0,
                     'closed', 'manual', '9003', '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                """);
            var ambiguousReport = Path.Combine(ambiguousRoot, "report.json");
            using (var ambiguousError = new StringWriter())
            {
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", ambiguousPackage,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", ambiguousReport,
                        "--external-config", ExternalConfigurationPath(ambiguousPackage)
                    ],
                    TextWriter.Null,
                    ambiguousError);
                Assert.AreEqual(1, exitCode);
                StringAssert.Contains(ambiguousError.ToString(), "placement_history_ambiguous");
                Assert.IsFalse(File.Exists(ambiguousReport));
            }

            await using (var ambiguousRollbackConnection = new SqlConnection(target))
            {
                await ambiguousRollbackConnection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(ambiguousRollbackConnection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"));
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
                    ('request-current', '2', 'fmt-book', 'A20000000000001', 'Current placed', 0, 'hold_placed', 'status-placed', NULL, NULL, '1', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-completed', '2', 'fmt-book', 'A20000000000002', 'Completed', 0, 'closed', 'status-closed', 'hold_completed', 'reason-completed', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-not-picked', '2', 'fmt-book', 'A20000000000003', 'Not picked up', 0, 'closed', 'status-closed', 'hold_not_picked_up', 'reason-not-picked', '3', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-unclaimed', '2', 'fmt-book', 'A20000000000004', 'Unclaimed', 0, 'closed', 'status-closed', 'hold_unclaimed', 'reason-unclaimed', '4', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-cancelled', '2', 'fmt-book', 'A20000000000005', 'Cancelled', 0, 'closed', 'status-closed', 'hold_cancelled', 'reason-cancelled', '5', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-expired', '2', 'fmt-book', 'A20000000000006', 'Expired', 0, 'closed', 'status-closed', 'hold_expired', 'reason-expired', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-dedicated', '2', 'fmt-book', 'A20000000000007', 'Dedicated event', 0, 'closed', 'status-closed', 'manual', 'reason-manual', NULL, NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-to', '2', 'fmt-book', 'A20000000000008', 'Transition to', 0, 'closed', 'status-closed', 'manual', 'reason-manual', '8', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-from', '2', 'fmt-book', 'A20000000000009', 'Transition from', 0, 'closed', 'status-closed', 'manual', 'reason-manual', '9', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-event-terminal', '2', 'fmt-book', 'A20000000000010', 'Event terminal', 0, 'closed', 'status-closed', 'manual', 'reason-manual', '10', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z'),
                    ('request-none', '2', 'fmt-book', 'A20000000000011', 'No placement evidence', 0, 'closed', 'status-closed', 'rejected', 'reason-rejected', '11', NULL, '2029-01-01T00:00:00Z', '2029-01-02T00:00:00Z');
                CREATE TABLE [title_request_events]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [titleRequest] TEXT NOT NULL,
                    [eventType] TEXT NOT NULL, [fromStatus] TEXT, [toStatus] TEXT,
                    [closeReason] TEXT, [actorType] TEXT NOT NULL, [metadata] TEXT,
                    [created] TEXT NOT NULL
                );
                INSERT INTO [title_request_events] VALUES
                    ('event-dedicated', 'request-dedicated', 'hold_placed', NULL, NULL, NULL, 'system', '{"bibId":"7"}', '2029-02-01T00:00:00Z'),
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
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(validPackage)
                ],
                TextWriter.Null,
                validError);
            Assert.AreEqual(0, validExitCode, validError.ToString());

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                Assert.AreEqual(10, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [LegacyHoldProtected] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Title] = N'No placement evidence' AND [LegacyHoldProtected] = 0;"));
                Assert.AreEqual(10, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [EventType] = N'legacy' AND JSON_VALUE([MetadataJson], '$.legacyBibProtection') = N'true';"));
                Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[TitleRequest] r ON r.[Id] = e.[TitleRequestId] WHERE r.[Title] = N'Dedicated event' AND JSON_VALUE(e.[MetadataJson], '$.bibId') = N'7';"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e JOIN [asap].[TitleRequest] r ON r.[Id] = e.[TitleRequestId] WHERE r.[Title] = N'No placement evidence' AND JSON_VALUE(e.[MetadataJson], '$.legacyBibProtection') = N'true';"));
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var placement = document.RootElement.GetProperty("placementReconciliation");
            Assert.AreEqual(11, placement.GetProperty("sourceRequestsEvaluated").GetInt32());
            Assert.AreEqual(10, placement.GetProperty("protectedRequests").GetInt32());
            Assert.AreEqual(8, placement.GetProperty("knownBibMarkers").GetInt32());
            Assert.AreEqual(2, placement.GetProperty("explicitNullBibMarkers").GetInt32());
            Assert.AreEqual(1, placement.GetProperty("noPlacementEvidence").GetInt32());
            Assert.AreEqual(0, placement.GetProperty("placementHistoryAmbiguous").GetInt32());
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
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", report,
                        "--external-config", ExternalConfigurationPath(package)
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
            var initialTargetFingerprint = ComputeTargetFingerprintForTest(target);

            foreach (var origin in new[]
                     {
                         "http://remote.example.org",
                         "https://user@example.org",
                         "https://example.org/path",
                         "https://example.org?query=1",
                         "http://*.example.org",
                         "https://example.org:abc",
                         "https://example.org:99999",
                         "https://example.org:00099999",
                         "https://example.org:999999999999999999999",
                         "https://*.example.org:99999",
                         "https://example.org:",
                         "https://:443",
                         "https://example.org\\path",
                         "https://example.org[broken",
                         "https://[2001:db8::zzz]"
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
                var reportPath = Path.Combine(packageRoot, "report.json");
                using var error = new StringWriter();

                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", reportPath,
                        "--external-config", ExternalConfigurationPath(package)
                    ],
                    TextWriter.Null,
                    error);

                Assert.AreEqual(1, exitCode, origin);
                StringAssert.Contains(error.ToString(), "patron_embed_origin_invalid", origin);
                Assert.IsFalse(File.Exists(reportPath), $"An invalid origin must not publish a report: {origin}");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"An invalid origin must not leave a pending report: {origin}");
                await using var connection = new SqlConnection(target);
                await connection.OpenAsync();
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2;"), origin);
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PatronEmbedAllowedOrigin];"), origin);
                Assert.AreEqual(initialTargetFingerprint, ComputeTargetFingerprintForTest(target), $"An invalid origin must leave SQL unchanged: {origin}");
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
    public async Task ImportRejectsCrossScopeTemplateReferencesBeforeTargetWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-template-scope-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationTemplateScope_{Guid.NewGuid():N}";
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var initialFingerprint = ComputeTargetFingerprintForTest(target);
            var invalidPackages = new[]
            {
                (
                    Name: "library-template-lineage",
                    ErrorCode: "email_template_source_scope_invalid",
                    Sql: """
                        INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-template', 'system', NULL, 'shared-template', 'System', 'System subject', 'System body', 1, NULL),
                            ('foreign-template', 'library', 'pb-org-3', 'shared-template', 'Foreign', 'Foreign subject', 'Foreign body', 1, NULL),
                            ('library-child', 'library', 'pb-org-2', 'shared-template', 'Child', 'Child subject', 'Child body', 1, 'foreign-template');
                        """
                ),
                (
                    Name: "ordinary-template-parent-key-mismatch",
                    ErrorCode: "email_template_source_key_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-parent', 'system', NULL, 'system-key', 'Parent', 'Parent subject', 'Parent body', 1, NULL),
                            ('library-child', 'library', 'pb-org-2', 'different-key', 'Child', 'Child subject', 'Child body', 1, 'system-parent');
                        """
                ),
                (
                    Name: "rejection-template-parent-kind-mismatch",
                    ErrorCode: "email_template_source_kind_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-ordinary', 'system', NULL, 'system-ordinary-key', 'Ordinary', 'Subject', 'Body', 1, NULL);
                        CREATE TABLE [rejection_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [name] TEXT, [subject] TEXT, [body] TEXT, [enabled] INTEGER NOT NULL,
                            [sortOrder] INTEGER, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [rejection_templates] VALUES
                            ('library-rejection', 'library', 'pb-org-2', 'Local rejection', 'Local subject', 'Local body', 1, 10, 'system-ordinary');
                        """
                ),
                (
                    Name: "system-ordinary-template-source-edge",
                    ErrorCode: "email_template_source_scope_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-child', 'system', NULL, 'same-key', 'System child', 'Subject', 'Body', 1, 'other-system-template');
                        """
                ),
                (
                    Name: "system-rejection-template-source-edge",
                    ErrorCode: "email_template_source_scope_invalid",
                    Sql: """
                        CREATE TABLE [rejection_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [name] TEXT, [subject] TEXT, [body] TEXT, [enabled] INTEGER NOT NULL,
                            [sortOrder] INTEGER, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [rejection_templates] VALUES
                            ('system-rejection', 'system', NULL, 'Rejection', 'Subject', 'Body', 1, 10, 'other-template');
                        """
                ),
                (
                    Name: "workflow-selects-ordinary-email-template",
                    ErrorCode: "workflow_template_kind_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('ordinary-rejection-shaped', 'system', NULL, 'ordinary-template', 'Ordinary', 'Subject', 'Body', 1, NULL);
                        CREATE TABLE [workflow_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT, [outstandingTimeoutRejectionTemplate] TEXT
                        );
                        INSERT INTO [workflow_settings] VALUES
                            ('system-workflow', 'system', NULL, 'ordinary-rejection-shaped');
                        """
                ),
                (
                    Name: "ordinary-template-reserved-rejection-prefix-without-collision",
                    ErrorCode: "email_template_target_kind_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('ordinary-reserved-kind', 'system', NULL, 'rejection:ordinary-only', 'Ordinary', 'Subject', 'Body', 1, NULL);
                        """
                ),
                (
                    Name: "foreign-workflow-rejection-template",
                    ErrorCode: "workflow_template_scope_invalid",
                    Sql: """
                        INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
                        CREATE TABLE [rejection_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [name] TEXT, [subject] TEXT, [body] TEXT, [enabled] INTEGER NOT NULL,
                            [sortOrder] INTEGER, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [rejection_templates] VALUES
                            ('foreign-rejection', 'library', 'pb-org-3', 'Foreign rejection', 'Rejected', '<p>Rejected</p>', 1, 10, NULL);
                        CREATE TABLE [workflow_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT, [outstandingTimeoutRejectionTemplate] TEXT
                        );
                        INSERT INTO [workflow_settings] VALUES
                            ('library-workflow', 'library', 'pb-org-2', 'foreign-rejection');
                        """
                ),
                (
                    Name: "system-email-template-seed-case-collision",
                    ErrorCode: "email_template_seed_identity_collision",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-seed-case', 'system', NULL, 'Suggestion_Submitted', 'Seed casing', 'Subject', 'Body', 1, NULL);
                        """
                ),
                (
                    Name: "library-email-template-seed-case-collision",
                    ErrorCode: "email_template_seed_identity_collision",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('library-seed-case', 'library', 'pb-org-2', 'Suggestion_Submitted', 'Seed casing', 'Subject', 'Body', 1, NULL);
                        """
                ),
                (
                    Name: "implicit-library-template-case-collision",
                    ErrorCode: "email_template_sql_identity_collision",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-case', 'system', NULL, 'Shared_Case_Key', 'System', 'Subject', 'Body', 1, NULL),
                            ('library-case', 'library', 'pb-org-2', 'shared_case_key', 'Library', 'Subject', 'Body', 1, NULL);
                        """
                ),
                (
                    Name: "ordinary-rejection-target-key-collision",
                    ErrorCode: "email_template_target_kind_invalid",
                    Sql: """
                        CREATE TABLE [email_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [templateKey] TEXT NOT NULL, [name] TEXT, [subject] TEXT, [body] TEXT,
                            [enabled] INTEGER NOT NULL, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [email_templates] VALUES
                            ('system-ordinary', 'system', NULL, 'rejection:system-rejection', 'Ordinary', 'Subject', 'Body', 1, NULL);
                        CREATE TABLE [rejection_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [name] TEXT, [subject] TEXT, [body] TEXT, [enabled] INTEGER NOT NULL,
                            [sortOrder] INTEGER, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [rejection_templates] VALUES
                            ('system-rejection', 'system', NULL, 'Rejection', 'Rejected', 'Body', 1, 10, NULL);
                        """
                ),
                (
                    Name: "rejection-target-key-case-collision",
                    ErrorCode: "email_template_sql_identity_collision",
                    Sql: """
                        CREATE TABLE [rejection_templates]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL, [libraryOrganization] TEXT,
                            [name] TEXT, [subject] TEXT, [body] TEXT, [enabled] INTEGER NOT NULL,
                            [sortOrder] INTEGER, [sourceTemplateId] TEXT
                        );
                        INSERT INTO [rejection_templates] VALUES
                            ('System-Rejection', 'system', NULL, 'System one', 'One', 'Body', 1, 10, NULL),
                            ('system-rejection', 'system', NULL, 'System two', 'Two', 'Body', 1, 20, NULL),
                            ('library-rejection-one', 'library', 'pb-org-2', 'Library one', NULL, NULL, 1, 30, 'System-Rejection'),
                            ('library-rejection-two', 'library', 'pb-org-2', 'Library two', NULL, NULL, 1, 40, 'system-rejection');
                        """
                )
            };

            foreach (var testCase in invalidPackages)
            {
                var packageRoot = Path.Combine(root, testCase.Name);
                var package = CreateMinimalPackage(packageRoot, testCase.Sql);
                var reportPath = Path.Combine(packageRoot, "report.json");
                using var error = new StringWriter();

                Assert.AreEqual(1, RunImport(package, reportPath, connectionEnvironmentName, tenantId, error), testCase.Name);
                StringAssert.Contains(error.ToString(), testCase.ErrorCode, testCase.Name);
                Assert.IsFalse(File.Exists(reportPath), $"An invalid source relationship must not publish a report: {testCase.Name}");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"An invalid source relationship must not leave a pending report: {testCase.Name}");
                Assert.AreEqual(initialFingerprint, ComputeTargetFingerprintForTest(target), $"An invalid source relationship must leave SQL unchanged: {testCase.Name}");
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
    [DoNotParallelize]
    public async Task ImportRejectsWorkflowTagIdentitiesLostByTargetSqlCollationBeforeWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-workflow-tag-collation-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationTagCollation_{Guid.NewGuid():N}";
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var initialFingerprint = ComputeTargetFingerprintForTest(target);
            var invalidSources = new[]
            {
                (
                    Name: "seed-case-collision",
                    ErrorCode: "workflow_tag_seed_identity_collision",
                    Sql: """
                        CREATE TABLE [workflow_tags]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL,
                            [label] TEXT NOT NULL, [description] TEXT
                        );
                        INSERT INTO [workflow_tags] VALUES
                            ('tag-seed-case', 'DUPLICATE_SUGGESTION', 'Duplicate suggestion', 'Case-only seed collision');
                        """
                ),
                (
                    Name: "source-case-collision",
                    ErrorCode: "workflow_tag_sql_identity_collision",
                    Sql: """
                        CREATE TABLE [workflow_tags]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY, [code] TEXT NOT NULL,
                            [label] TEXT NOT NULL, [description] TEXT
                        );
                        INSERT INTO [workflow_tags] VALUES
                            ('tag-mixed-case', 'Custom_Tag', 'Custom tag', NULL),
                            ('tag-lower-case', 'custom_tag', 'Other custom tag', NULL);
                        """
                )
            };

            foreach (var testCase in invalidSources)
            {
                var packageRoot = Path.Combine(root, testCase.Name);
                var package = CreateMinimalPackage(packageRoot, testCase.Sql);
                var reportPath = Path.Combine(packageRoot, "report.json");
                using var error = new StringWriter();

                Assert.AreEqual(1, RunImport(package, reportPath, connectionEnvironmentName, tenantId, error), testCase.Name);
                StringAssert.Contains(error.ToString(), testCase.ErrorCode, testCase.Name);
                Assert.IsFalse(File.Exists(reportPath), $"Unsupported source tag identity must not publish a report: {testCase.Name}");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"Unsupported source tag identity must not leave a pending report: {testCase.Name}");
                Assert.AreEqual(initialFingerprint, ComputeTargetFingerprintForTest(target), $"Unsupported source tag identity must leave SQL unchanged: {testCase.Name}");
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
    [DoNotParallelize]
    public async Task ImportUsesPinnedPublicationFallbacksAndSystemScopeAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-publication-fallbacks-{Guid.NewGuid():N}");
        var newlineDatabaseName = $"AsapMigrationPublicationNewline_{Guid.NewGuid():N}";
        var customDatabaseName = $"AsapMigrationPublicationCustom_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var newlineTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = newlineDatabaseName }.ConnectionString;
        var customTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = customDatabaseName }.ConnectionString;
        var newlineEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var customEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var newlinePackage = CreateMinimalPackage(
                Path.Combine(root, "numeric-newline"),
                """
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [publicationOptions] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('z-system', 'system', NULL, '1' || char(10) || '2' || char(10) || '3' || char(10) || '4');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [publicationOptions] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('a-library', '2', '["1","2","3","4"]');
                """);
            var customPackage = CreateMinimalPackage(
                Path.Combine(root, "capitalized-system"),
                """
                CREATE TABLE [ui_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [publicationOptions] TEXT
                );
                INSERT INTO [ui_settings] VALUES
                    ('z-system', 'System', NULL,
                     char(65279) || '[{"label":"","name":"\uFEFFSystem One\uFEFF","enabled":true,"sortOrder":20},{"label":"System Two","enabled":true,"sortOrder":10}]' || char(65279));
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [publicationOptions] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('a-library', '2', char(65279) || '1' || char(65279) || char(10) || char(65279) || '2' || char(65279) || char(10) || char(65279) || '3' || char(65279) || char(10) || char(65279) || '4' || char(65279));
                """);

            DeployDacpac(master, newlineDatabaseName);
            DeployDacpac(master, customDatabaseName);
            Environment.SetEnvironmentVariable(newlineEnvironmentName, newlineTarget);
            Environment.SetEnvironmentVariable(customEnvironmentName, customTarget);
            var newlineReport = Path.Combine(root, "numeric-newline-report.json");
            var customReport = Path.Combine(root, "capitalized-system-report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(newlinePackage, newlineReport, newlineEnvironmentName, tenantId, importError), importError.ToString());
            }
            using (var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(newlineReport)))
            {
                Assert.AreEqual(0, reportDocument.RootElement.GetProperty("importedCounts").GetProperty("smtp_settings").GetInt32());
                var tokenState = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "email_provider_token");
                Assert.AreEqual(1, tokenState.GetProperty("organizationId").GetInt32());
                Assert.IsFalse(tokenState.GetProperty("postmarkTokenProvisioned").GetBoolean());
            }
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(customPackage, customReport, customEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using (var connection = new SqlConnection(newlineTarget))
            {
                await connection.OpenAsync();
                Assert.AreEqual(3, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 1 AND (([OptionKey] = N'already_published' AND [Label] = N'Already published' AND [SortOrder] = 10) OR ([OptionKey] = N'coming_soon' AND [Label] = N'Coming soon' AND [SortOrder] = 20) OR ([OptionKey] = N'published_a_while_back' AND [Label] = N'Published a while back' AND [SortOrder] = 30));"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 2;"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2;"));
            }
            await using (var connection = new SqlConnection(customTarget))
            {
                await connection.OpenAsync();
                Assert.AreEqual(2, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 1 AND (([OptionKey] COLLATE Latin1_General_100_BIN2 = N'system-one' COLLATE Latin1_General_100_BIN2 AND [Label] = N'System One' AND [SortOrder] = 20) OR ([OptionKey] COLLATE Latin1_General_100_BIN2 = N'system-two' COLLATE Latin1_General_100_BIN2 AND [Label] = N'System Two' AND [SortOrder] = 10));"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = 2;"));
                Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2;"));
                foreach (var (package, report, environmentName) in new[]
                         {
                             (newlinePackage, newlineReport, newlineEnvironmentName),
                             (customPackage, customReport, customEnvironmentName)
                         })
                {
                    using var reconcileError = new StringWriter();
                    Assert.AreEqual(0, RunReconcile(package, report, environmentName, reconcileError), reconcileError.ToString());
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(newlineEnvironmentName, null);
            Environment.SetEnvironmentVariable(customEnvironmentName, null);
            await DropDatabaseAsync(master, newlineDatabaseName);
            await DropDatabaseAsync(master, customDatabaseName);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportRejectsUnrepresentableSourceConfigurationValuesBeforeWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-publication-alias-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPublicationAlias_{Guid.NewGuid():N}";
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var initialFingerprint = ComputeTargetFingerprintForTest(target);
            var invalidSources = new[]
            {
                (
                    Name: "system-selected-whitespace-label",
                    ErrorCode: "publication_options_invalid",
                    Sql: """
                        CREATE TABLE [ui_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT,
                            [publicationOptions] TEXT
                        );
                        INSERT INTO [ui_settings] VALUES
                            ('ui-system', 'system', NULL, '[{"label":"   ","name":"Good"}]');
                        """
                ),
                (
                    Name: "library-selected-whitespace-name",
                    ErrorCode: "publication_options_invalid",
                    Sql: """
                        CREATE TABLE [patron_settings_overrides]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [orgId] TEXT NOT NULL,
                            [publicationOptions] TEXT
                        );
                        INSERT INTO [patron_settings_overrides] VALUES
                            ('library-options', '2', '[{"name":"   ","value":"Good"}]');
                        """
                ),
                (
                    Name: "patron-code-leading-zero-alias",
                    ErrorCode: "source_patron_code_invalid",
                    Sql: """
                        CREATE TABLE [workflow_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT,
                            [allowedPatronCodeIds] TEXT
                        );
                        INSERT INTO [workflow_settings] VALUES ('workflow-system', 'system', NULL, '02');
                        """
                ),
                (
                    Name: "patron-code-newline-is-not-a-separator",
                    ErrorCode: "source_patron_code_invalid",
                    Sql: """
                        CREATE TABLE [workflow_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT,
                            [allowedPatronCodeIds] TEXT
                        );
                        INSERT INTO [workflow_settings] VALUES ('workflow-system', 'system', NULL, '2' || char(10) || '3');
                        """
                ),
                (
                    Name: "patron-code-nel-remains-part-of-identity",
                    ErrorCode: "source_patron_code_invalid",
                    Sql: """
                        CREATE TABLE [workflow_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [scope] TEXT NOT NULL,
                            [libraryOrganization] TEXT,
                            [allowedPatronCodeIds] TEXT
                        );
                        INSERT INTO [workflow_settings] VALUES ('workflow-system', 'system', NULL, char(133) || '2');
                        """
                ),
                (
                    Name: "modern-active-duplicate-label-with-blank-sibling",
                    ErrorCode: "duplicate_labels_invalid",
                    Sql: """
                        CREATE TABLE [patron_settings_overrides]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [orgId] TEXT NOT NULL,
                            [duplicateStatusLabels] TEXT
                        );
                        INSERT INTO [patron_settings_overrides] VALUES ('modern-labels', '2', '{"suggestion":"Valid","closed":"   "}');
                        """
                ),
                (
                    Name: "modern-nel-only-duplicate-label",
                    ErrorCode: "duplicate_labels_invalid",
                    Sql: """
                        CREATE TABLE [patron_settings_overrides]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [orgId] TEXT NOT NULL,
                            [duplicateStatusLabels] TEXT
                        );
                        INSERT INTO [patron_settings_overrides] VALUES ('modern-labels', '2', '{"suggestion":"\u0085"}');
                        """
                ),
                (
                    Name: "legacy-blank-duplicate-label",
                    ErrorCode: "duplicate_labels_invalid",
                    Sql: """
                        CREATE TABLE [patron_library_settings]
                        (
                            [id] TEXT NOT NULL PRIMARY KEY,
                            [libraryOrganization] TEXT NOT NULL,
                            [duplicateRequestStatusLabels] TEXT
                        );
                        INSERT INTO [patron_library_settings] VALUES ('legacy-labels', 'pb-org-2', '{"suggestion":"   "}');
                        """
                )
            };

            foreach (var testCase in invalidSources)
            {
                var packageRoot = Path.Combine(root, testCase.Name);
                var package = CreateMinimalPackage(packageRoot, testCase.Sql);
                var reportPath = Path.Combine(packageRoot, "report.json");
                using var importError = new StringWriter();
                Assert.AreEqual(1, RunImport(package, reportPath, connectionEnvironmentName, tenantId, importError), testCase.Name);
                StringAssert.Contains(importError.ToString(), testCase.ErrorCode, testCase.Name);
                Assert.IsFalse(File.Exists(reportPath), $"An unrepresentable source configuration value must not publish a report: {testCase.Name}");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"An unrepresentable source configuration value must not leave a pending report: {testCase.Name}");
                Assert.AreEqual(initialFingerprint, ComputeTargetFingerprintForTest(target), $"An unrepresentable source configuration value must leave SQL unchanged: {testCase.Name}");
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
    [DoNotParallelize]
    public async Task ImportRejectsCustomFieldIdentitiesLostByLegacyNormalizationBeforeWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-custom-field-normalization-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationCustomFieldNormalization_{Guid.NewGuid():N}";
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
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var initialFingerprint = ComputeTargetFingerprintForTest(target);
            var invalidDefinitions = new[]
            {
                (Name: "normalized-definition-key-collision", ErrorCode: "custom_fields_identity_invalid", Json: "[{\"key\":\"Audience-Name\",\"label\":\"First\",\"type\":\"text\"},{\"key\":\"audience name\",\"label\":\"Second\",\"type\":\"text\"}]"),
                (Name: "normalized-option-id-collision", ErrorCode: "custom_fields_identity_invalid", Json: "[{\"key\":\"choice\",\"label\":\"Choice\",\"type\":\"select\",\"options\":[{\"id\":\"Option-One\",\"label\":\"One\"},{\"id\":\"option one\",\"label\":\"Another\"}]}]"),
                (Name: "normalized-definition-key-empty", ErrorCode: "custom_fields_identity_invalid", Json: "[{\"key\":\"!!!\",\"label\":\"No normalized key\",\"type\":\"text\"}]"),
                (Name: "legacy-nel-type-does-not-normalize-to-select", ErrorCode: "custom_fields_invalid", Json: "[{\"label\":\"Unsupported trim type\",\"type\":\"\\u0085select\\u0085\"}]")
            };

            foreach (var testCase in invalidDefinitions)
            {
                var packageRoot = Path.Combine(root, testCase.Name);
                var package = CreateMinimalPackage(
                    packageRoot,
                    $$"""
                    CREATE TABLE [patron_settings_overrides]
                    (
                        [id] TEXT NOT NULL PRIMARY KEY,
                        [orgId] TEXT NOT NULL,
                        [additionalFieldDefinitions] TEXT
                    );
                    INSERT INTO [patron_settings_overrides] VALUES
                        ('invalid-fields', '2', '{{testCase.Json.Replace("'", "''", StringComparison.Ordinal)}}');
                    """);
                var reportPath = Path.Combine(packageRoot, "report.json");
                using var error = new StringWriter();

                Assert.AreEqual(1, RunImport(package, reportPath, connectionEnvironmentName, tenantId, error), testCase.Name);
                StringAssert.Contains(error.ToString(), testCase.ErrorCode, testCase.Name);
                Assert.IsFalse(File.Exists(reportPath), $"An unrepresentable custom-field source shape must not publish a report: {testCase.Name}");
                Assert.IsFalse(File.Exists(reportPath + ".pending"), $"An unrepresentable custom-field source shape must not leave a pending report: {testCase.Name}");
                Assert.AreEqual(initialFingerprint, ComputeTargetFingerprintForTest(target), $"An unrepresentable custom-field source shape must leave SQL unchanged: {testCase.Name}");
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
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Modern Blank Library', 'MBL', 1, 2, 1);
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
                    ('legacy-2', 'pb-org-2', '{"suggestion":"  Live legacy label  "}', '2029-02-01T00:00:00Z'),
                    ('legacy-3', 'pb-org-3', '{"suggestion":"Ignored legacy label"}', '2029-02-01T00:00:00Z');
                CREATE TABLE [patron_settings_overrides]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [orgId] TEXT NOT NULL,
                    [duplicateStatusLabels] TEXT,
                    [updated] TEXT
                );
                INSERT INTO [patron_settings_overrides] VALUES
                    ('modern-3', '3', '{"suggestion":"   "}', '2029-03-01T00:00:00Z');
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
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
                    "--allowed-tenant-ids", tenantId.ToString(),
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                TextWriter.Null,
                error);

            Assert.AreEqual(0, exitCode, error.ToString());
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2 AND [SuggestionStatusLabel] = N'  Live legacy label  ' AND DATALENGTH([SuggestionStatusLabel]) = 42;"));
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
        var bibId = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var publication = new string('P', 128);
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(
                root,
                $$"""
                INSERT INTO [polaris_organizations] VALUES ('pb-org-3', '3', 'Other Library', 'OTHER', 1, 2, 1);
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
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT,
                    [code] TEXT NOT NULL,
                    [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL,
                    [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES
                    ('fmt-local', 'library', 'pb-org-2', 'local', 'Local format', 1, 20),
                    ('fmt-foreign', 'library', 'pb-org-3', 'foreign', 'Foreign format', 1, 30);
                INSERT INTO [additional_copy_requests] VALUES
                    ('copy-boundary', '', '2', 'Frozen library', '{{bibId}}', 'Frozen title', 'Frozen author',
                     'book', 'COPY-BOUNDARY', '{{publication}}', 'closed', '<p>Frozen notes</p>',
                     'pb-staff-1', 'Historical creator', '', '', '2030-01-03T06:07:08Z',
                     '2030-01-02T03:04:05Z', NULL, '', '', ''),
                    ('copy-no-format', NULL, '2', 'Frozen library', '7001', 'No format', NULL,
                     NULL, NULL, NULL, 'open', NULL, NULL, NULL, NULL, NULL, NULL,
                     '2030-01-05T00:00:00Z', '2030-01-05T00:00:00Z', NULL, NULL, NULL),
                    ('copy-library-format', NULL, '2', 'Frozen library', '7002', 'Library format', NULL,
                     'local', NULL, NULL, 'open', NULL, NULL, NULL, NULL, NULL, NULL,
                     '2030-01-06T00:00:00Z', '2030-01-06T00:00:00Z', NULL, NULL, NULL);
                """);
            DeployDacpac(master, databaseName);
            var report = Path.Combine(root, "report.json");
            Environment.SetEnvironmentVariable(environmentName, target);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", environmentName,
                    "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                    "--report", report,
                    "--external-config", ExternalConfigurationPath(package)
                ],
                output,
                error);
            Assert.AreEqual(0, exitCode, error.ToString());

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT copy.[BibId], copy.[Publication], copy.[CreatedUtc], copy.[UpdatedUtc], copy.[ClosedUtc],
                       copy.[ClosedByStaffUserId], copy.[ClosedByDisplayName], copy.[ClaimType], copy.[ClaimRuleId]
                FROM [asap].[LegacyPocketBaseMapping] mapping
                JOIN [asap].[AdditionalCopyRequest] copy ON copy.[Id] = mapping.[NewId]
                WHERE mapping.[EntityType] = N'additional_copy' AND mapping.[PocketBaseId] = N'copy-boundary';
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(int.MaxValue, reader.GetInt32(0));
            Assert.AreEqual(publication, reader.GetString(1));
            Assert.AreEqual(reader.GetDateTime(2), reader.GetDateTime(3));
            Assert.AreEqual(new DateTime(2030, 1, 3, 6, 7, 8), reader.GetDateTime(4));
            Assert.IsTrue(reader.IsDBNull(5));
            Assert.IsTrue(reader.IsDBNull(6));
            Assert.IsTrue(reader.IsDBNull(7));
            Assert.IsTrue(reader.IsDBNull(8));
            await reader.DisposeAsync();

            var additionalCopyDriftCases = new (string Name, string DriftSql, string DriftAssertionSql, string RestoreSql, string RestoredAssertionSql)[]
            {
                ("additional-copy title history", "UPDATE c SET [Title] = N'Drifted copy title' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[Title] = N'Drifted copy title';", "UPDATE c SET [Title] = N'Frozen title' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[Title] = N'Frozen title';"),
                ("additional-copy notes history", "UPDATE c SET [Notes] = N'<p>Drifted copy notes</p>' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[Notes] = N'<p>Drifted copy notes</p>';", "UPDATE c SET [Notes] = N'<p>Frozen notes</p>' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[Notes] = N'<p>Frozen notes</p>';"),
                ("additional-copy creator history", "UPDATE c SET [CreatedByDisplayName] = N'Drifted creator' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[CreatedByDisplayName] = N'Drifted creator';", "UPDATE c SET [CreatedByDisplayName] = N'Historical creator' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[CreatedByDisplayName] = N'Historical creator';"),
                ("additional-copy close history", "UPDATE c SET [ClosedUtc] = '2030-01-04T06:07:08' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[ClosedUtc] = '2030-01-04T06:07:08';", "UPDATE c SET [ClosedUtc] = '2030-01-03T06:07:08' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[ClosedUtc] = '2030-01-03T06:07:08';"),
                ("additional-copy created-time fallback", "UPDATE c SET [UpdatedUtc] = '2030-01-02T03:05:05' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[UpdatedUtc] = '2030-01-02T03:05:05';", "UPDATE c SET [UpdatedUtc] = '2030-01-02T03:04:05' FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary';", "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] c JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id] WHERE m.[PocketBaseId] = N'copy-boundary' AND c.[UpdatedUtc] = '2030-01-02T03:04:05';")
            };
            foreach (var drift in additionalCopyDriftCases)
            {
                await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                    connection, target, package, report, environmentName,
                    drift.Name, drift.DriftSql, drift.DriftAssertionSql, 1,
                    drift.RestoreSql, drift.RestoredAssertionSql, 1);
            }

            var copyFormats = new Dictionary<string, (long? FormatId, int? OwnerOrganizationId, string? Code)>(StringComparer.Ordinal);
            await using (var formatQuery = connection.CreateCommand())
            {
                formatQuery.CommandText =
                    "SELECT m.[PocketBaseId], c.[MaterialFormatId], f.[OwnerOrganizationId], f.[Code] " +
                    "FROM [asap].[LegacyPocketBaseMapping] m JOIN [asap].[AdditionalCopyRequest] c ON c.[Id] = m.[NewId] " +
                    "LEFT JOIN [asap].[MaterialFormat] f ON f.[Id] = c.[MaterialFormatId] " +
                    "WHERE m.[EntityType] = N'additional_copy';";
                await using var formatReader = await formatQuery.ExecuteReaderAsync();
                while (await formatReader.ReadAsync())
                {
                    copyFormats.Add(
                        formatReader.GetString(0),
                        (formatReader.IsDBNull(1) ? null : formatReader.GetInt64(1),
                         formatReader.IsDBNull(2) ? null : formatReader.GetInt32(2),
                         formatReader.IsDBNull(3) ? null : formatReader.GetString(3)));
                }
            }
            Assert.AreEqual(3, copyFormats.Count);
            var systemFormat = copyFormats["copy-boundary"];
            Assert.AreEqual(1, systemFormat.OwnerOrganizationId);
            Assert.AreEqual("book", systemFormat.Code);
            Assert.IsNull(copyFormats["copy-no-format"].FormatId);
            Assert.IsNull(copyFormats["copy-no-format"].OwnerOrganizationId);
            Assert.AreEqual(2, copyFormats["copy-library-format"].OwnerOrganizationId);
            Assert.AreEqual("local", copyFormats["copy-library-format"].Code);
            Assert.AreEqual(1, await ScalarAsync(connection, """
                SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] m
                JOIN [asap].[MaterialFormat] f ON f.[Id] = m.[NewId]
                WHERE m.[EntityType] = N'material_format' AND m.[PocketBaseId] = N'fmt-local'
                  AND f.[OwnerOrganizationId] = 2 AND f.[Code] = N'local';
                """));

            var foreignFormatId = await ReadLongAsync(connection,
                "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format' AND [PocketBaseId] = N'fmt-foreign';");
            Assert.IsTrue(foreignFormatId > 0);
            Assert.AreEqual(1, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [Id] = " + foreignFormatId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " AND [OwnerOrganizationId] = 3 AND [Code] = N'foreign';"));
            await using (var attachForeignFormat = connection.CreateCommand())
            {
                attachForeignFormat.CommandText = """
                    UPDATE c SET [MaterialFormatId] = @formatId
                    FROM [asap].[AdditionalCopyRequest] c
                    JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id]
                    WHERE m.[PocketBaseId] = N'copy-boundary';
                    """;
                attachForeignFormat.Parameters.AddWithValue("@formatId", foreignFormatId);
                Assert.AreEqual(1, await attachForeignFormat.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var foreignFormatFingerprint = ComputeTargetFingerprintForTest(target);
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, report, environmentName, reconcileError));
                StringAssert.Contains(reconcileError.ToString(), "target additional-copy request does not preserve its source relation, format identity, and library ownership");
            }
            Assert.AreEqual(foreignFormatFingerprint, ComputeTargetFingerprintForTest(target), "Reconciliation must not repair a foreign format relationship.");
            await using (var restoreFormat = connection.CreateCommand())
            {
                restoreFormat.CommandText = """
                    UPDATE c SET [MaterialFormatId] = @expectedFormatId
                    FROM [asap].[AdditionalCopyRequest] c
                    JOIN [asap].[LegacyPocketBaseMapping] m ON m.[EntityType] = N'additional_copy' AND m.[NewId] = c.[Id]
                    WHERE m.[PocketBaseId] = N'copy-boundary';
                    """;
                restoreFormat.Parameters.AddWithValue("@expectedFormatId", systemFormat.FormatId!.Value);
                await restoreFormat.ExecuteNonQueryAsync();
            }
            RefreshReportFingerprint(report, target);
            using (var reconcileRestored = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, environmentName, reconcileRestored), reconcileRestored.ToString());
            }

            using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.AreEqual(6, reportDocument.RootElement.GetProperty("reportVersion").GetInt32());
            Assert.AreEqual(3, reportDocument.RootElement.GetProperty("importedCounts")
                .GetProperty("additional_copy_requests").GetInt32());
            Assert.AreEqual(ComputeTargetIdentityForTest(target), reportDocument.RootElement.GetProperty("targetIdentitySha256").GetString());
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
            Environment.SetEnvironmentVariable(environmentName, "SQL must not be reached");
            using var error = new StringWriter();

            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", environmentName,
                    "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                    "--report", Path.Combine(root, "report.json"),
                    "--external-config", ExternalConfigurationPath(package)
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
    public void ImportBlocksActiveInvalidAndDuplicateStaffAuthenticationEmailsBeforeSqlMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-staff-email-{Guid.NewGuid():N}");
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(environmentName, "SQL must not be reached");
        try
        {
            var cases = new[]
            {
                ("missing", "UPDATE [staff_users] SET [email] = NULL WHERE [id] = 'pb-staff-1';", "active_staff_email_invalid"),
                ("placeholder", "UPDATE [staff_users] SET [email] = 'legacy@staff.asap.local' WHERE [id] = 'pb-staff-1';", "active_staff_email_invalid"),
                ("duplicate",
                    """
                    INSERT INTO [staff_users] VALUES
                        ('pb-staff-2', 'SOURCE-ADMIN@example.org', 'duplicate-admin', 'Duplicate Administrator',
                         'staff', 1, '2', 0, NULL, 0, 0, 0);
                    """,
                    "duplicate_staff_email")
            };

            foreach (var testCase in cases)
            {
                var caseRoot = Path.Combine(root, testCase.Item1);
                Directory.CreateDirectory(caseRoot);
                var package = CreateMinimalPackage(caseRoot, testCase.Item2);
                using var error = new StringWriter();
                var exitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", environmentName,
                        "--allowed-tenant-ids", "00000000-0000-0000-0000-000000000002",
                        "--report", Path.Combine(caseRoot, "report.json"),
                        "--external-config", ExternalConfigurationPath(package)
                    ],
                    TextWriter.Null,
                    error);

                Assert.AreEqual(1, exitCode, testCase.Item1);
                StringAssert.Contains(error.ToString(), testCase.Item3, testCase.Item1);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ImportProjectsPolarisProviderDefaultsAndPreservesRepresentableConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-polaris-runtime-{Guid.NewGuid():N}");
        var defaultDatabaseName = $"AsapMigrationPolarisDefault_{Guid.NewGuid():N}";
        var accessDatabaseName = $"AsapMigrationPolarisAccess_{Guid.NewGuid():N}";
        var invalidLeapDatabaseName = $"AsapMigrationPolarisLeap_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var defaultTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = defaultDatabaseName }.ConnectionString;
        var accessTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = accessDatabaseName }.ConnectionString;
        var invalidLeapTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = invalidLeapDatabaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);

        static string SqlLiteral(string? value) =>
            value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        string CreatePackage(
            string caseName,
            string? host,
            string? accessId,
            string? staffDomain,
            string? adminUser,
            string? leapBibPattern,
            string? leapPatronPattern,
            string? workstationId = "99",
            string? userId = "42",
            string? langId = null,
            string? appId = null)
        {
            var caseRoot = Path.Combine(root, caseName);
            Directory.CreateDirectory(caseRoot);
            var sql = $$"""
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [leapBibUrlPattern] TEXT, [leapPatronUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings-1', {{SqlLiteral(leapBibPattern)}}, {{SqlLiteral(leapPatronPattern)}});
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT, [apiKey] TEXT,
                    [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT, [workstationId] TEXT,
                    [userId] TEXT, [langId] TEXT, [appId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris-1', {{SqlLiteral(host)}}, {{SqlLiteral(accessId)}}, NULL,
                     {{SqlLiteral(staffDomain)}}, {{SqlLiteral(adminUser)}}, NULL,
                     {{SqlLiteral(workstationId)}}, {{SqlLiteral(userId)}}, {{SqlLiteral(langId)}}, {{SqlLiteral(appId)}}, NULL, NULL);
                """;
            return CreateMinimalPackage(caseRoot, sql);
        }

        try
        {
            var bibPattern = "\u0085https://catalog.example/title/{bib}";
            var feffPatronPattern = "\uFEFFhttps://catalog.example/patron/{{patron-id}}";
            var defaultPackage = CreatePackage(
                "polaris-defaults",
                "polaris.example.org/",
                string.Empty,
                "  EXAMPLE-DOMAIN  ",
                "  service-user  ",
                bibPattern,
                feffPatronPattern,
                langId: string.Empty);
            DeployDacpac(master, defaultDatabaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, defaultTarget);
            var defaultReport = Path.Combine(root, "polaris-default-report.json");
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(defaultPackage, defaultReport, connectionEnvironmentName, tenantId, error), error.ToString());
            }

            await using (var connection = new SqlConnection(defaultTarget))
            {
                await connection.OpenAsync();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT [Host], [AccessId], [StaffDomain], [AdminUser] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;";
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual("https://polaris.example.org", reader.GetString(0),
                        "A schemeless legacy PAPI host must resolve to the pinned HTTPS host.");
                    Assert.AreEqual("SuggestAPI", reader.GetString(1),
                        "A missing or empty legacy AccessId must use the pinned provider default.");
                    Assert.AreEqual("  EXAMPLE-DOMAIN  ", reader.GetString(2),
                        "A representable legacy staff domain must retain its exact raw value.");
                    Assert.AreEqual("  service-user  ", reader.GetString(3),
                        "A representable legacy admin user must retain its exact raw value.");
                }
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT [LeapBibUrlPattern], [LeapPatronUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;";
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual(bibPattern, reader.GetString(0),
                        "NEL is not ECMAScript trim whitespace, so the source-invalid research pattern must remain inactive.");
                    Assert.AreEqual("https://catalog.example/patron/{{patron-id}}", reader.GetString(1),
                        "A valid FEFF-padded patron pattern must materialize its pinned ECMAScript-trimmed effective value.");
                }
                using var reconcile = new StringWriter();
                Assert.AreEqual(0, RunReconcile(defaultPackage, defaultReport, connectionEnvironmentName, reconcile), reconcile.ToString());
            }

            var accessPackage = CreatePackage(
                "polaris-raw-access",
                "https://polaris.example.org",
                "  ACCESS-ID  ",
                "DOMAIN",
                "service-user",
                null,
                null,
                workstationId: null,
                userId: string.Empty,
                appId: string.Empty);
            DeployDacpac(master, accessDatabaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, accessTarget);
            var accessReport = Path.Combine(root, "polaris-access-report.json");
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(accessPackage, accessReport, connectionEnvironmentName, tenantId, error), error.ToString());
            }
            await using (var connection = new SqlConnection(accessTarget))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT [AccessId], [WorkstationId], [SystemPolarisUserId] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual("  ACCESS-ID  ", reader.GetString(0),
                    "A nonempty legacy AccessId is sent verbatim by the pinned provider.");
                Assert.IsFalse(reader.IsDBNull(1), "A missing legacy workstation uses the pinned provider default.");
                Assert.IsFalse(reader.IsDBNull(2), "An empty legacy system-user identity uses the pinned provider default.");
                Assert.AreEqual(1, reader.GetInt32(1));
                Assert.AreEqual(1, reader.GetInt32(2));
                using var reconcile = new StringWriter();
                Assert.AreEqual(0, RunReconcile(accessPackage, accessReport, connectionEnvironmentName, reconcile), reconcile.ToString());
            }

            var initialTargetFingerprint = string.Empty;
            DeployDacpac(master, invalidLeapDatabaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidLeapTarget);
            initialTargetFingerprint = ComputeTargetFingerprintForTest(invalidLeapTarget);

            void AssertRefusedBeforeSql(string package, string report, string errorCode, string reason)
            {
                using (var error = new StringWriter())
                {
                    Assert.AreEqual(1, RunImport(package, report, connectionEnvironmentName, tenantId, error), reason);
                    StringAssert.Contains(error.ToString(), errorCode);
                }
                Assert.AreEqual(initialTargetFingerprint, ComputeTargetFingerprintForTest(invalidLeapTarget),
                    "An unrepresentable provider configuration must leave target SQL unchanged.");
                Assert.IsFalse(File.Exists(report));
                Assert.IsFalse(File.Exists(report + ".pending"));
            }

            var uppercaseHostPackage = CreatePackage(
                "polaris-uppercase-host",
                "HTTPS://polaris.example.org/",
                null,
                null,
                null,
                null,
                null);
            AssertRefusedBeforeSql(
                uppercaseHostPackage,
                Path.Combine(root, "polaris-uppercase-report.json"),
                "polaris_settings_unrepresentable",
                "The pinned case-sensitive scheme check must not turn an uppercase source prefix into a working endpoint.");

            var nondefaultLanguagePackage = CreatePackage(
                "polaris-nondefault-language",
                "https://polaris.example.org",
                null,
                null,
                null,
                null,
                null,
                langId: "1036");
            AssertRefusedBeforeSql(
                nondefaultLanguagePackage,
                Path.Combine(root, "polaris-language-report.json"),
                "polaris_settings_unrepresentable",
                "A nondefault source Polaris language cannot be represented by the target provider.");

            var nondefaultApplicationPackage = CreatePackage(
                "polaris-nondefault-application",
                "https://polaris.example.org",
                null,
                null,
                null,
                null,
                null,
                appId: "101");
            AssertRefusedBeforeSql(
                nondefaultApplicationPackage,
                Path.Combine(root, "polaris-application-report.json"),
                "polaris_settings_unrepresentable",
                "A nondefault source Polaris application cannot be represented by the target provider.");

            var invalidPattern = "\u0085https://catalog.example/patron/{{patron-id}}";
            var invalidLeapPackage = CreatePackage(
                "leap-patron-nel",
                null,
                null,
                null,
                null,
                null,
                invalidPattern);
            var invalidLeapReport = Path.Combine(root, "leap-patron-report.json");
            AssertRefusedBeforeSql(
                invalidLeapPackage,
                invalidLeapReport,
                "leap_patron_url_pattern_unrepresentable",
                "A NEL-prefixed patron pattern is unrepresentable while the target resolver uses .NET Trim.");
            await AssertFreshImportTargetAsync(invalidLeapTarget);
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, defaultDatabaseName);
            await DropDatabaseAsync(master, accessDatabaseName);
            await DropDatabaseAsync(master, invalidLeapDatabaseName);
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
            const string sourceApiKey = " \uFEFFsource-api-secret\u0085 ";
            const string sourceAdminPassword = "   ";
            var package = CreateMinimalPackage(
                root,
                $"""
                UPDATE [staff_users] SET [role] = 'admin', [libraryOrgId] = '2';
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                    [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                    [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris-1', 'https://polaris.example.org', 'access', '{sourceApiKey}',
                     'EXAMPLE', 'service-user', '{sourceAdminPassword}', '99', '42', '7', '3');
                """);
            using (var runtime = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(package, "effective-legacy-runtime-config.json"))))
            {
                var settings = runtime.RootElement.GetProperty("settings");
                Assert.IsTrue(settings.GetProperty("PolarisApiKey").GetProperty("hasValue").GetBoolean(),
                    "The frozen runtime must preserve nonempty Polaris credential presence without trimming.");
                Assert.IsTrue(settings.GetProperty("PolarisAdminPassword").GetProperty("hasValue").GetBoolean(),
                    "A whitespace-only nonempty Polaris credential must remain present in frozen runtime metadata.");
            }
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
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "source-admin@example.org";
            configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "Bootstrap Administrator";
            configuration.Authentication.Entra.InitialSuperAdmin.NotificationEmail = "bootstrap-notify@example.org";
            var configurationPath = Path.Combine(root, "asap.settings.json");
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
            var report = Path.Combine(root, "report.json");
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            Environment.SetEnvironmentVariable(tokenEnvironmentName, "target-postmark-secret");

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = MigrationCli.Run(
                [
                    "import", "--package", package,
                    "--connection-string-env", connectionEnvironmentName,
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
                "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'SOURCE-ADMIN@EXAMPLE.ORG' AND [EntraTenantId] IS NULL AND [EntraObjectId] IS NULL AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
            string apiKeyCiphertext;
            string adminPasswordCiphertext;
            string postmarkTokenCiphertext;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT [ProtectedApiKey], [ProtectedAdminPassword]
                    FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync(), "The imported system Polaris settings row must exist.");
                Assert.IsFalse(reader.IsDBNull(0), "The source API key must remain protected.");
                Assert.IsFalse(reader.IsDBNull(1), "The source admin password must remain protected.");
                apiKeyCiphertext = reader.GetString(0);
                adminPasswordCiphertext = reader.GetString(1);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT [ProtectedServerToken] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;";
                postmarkTokenCiphertext = (string)(await command.ExecuteScalarAsync() ??
                    throw new InvalidOperationException("The operator-provisioned token ciphertext is missing."));
            }
            Assert.IsFalse(string.Equals(apiKeyCiphertext, sourceApiKey, StringComparison.Ordinal),
                "The protected API key must not be stored as plaintext.");
            Assert.IsFalse(string.Equals(adminPasswordCiphertext, sourceAdminPassword, StringComparison.Ordinal),
                "The protected admin password must not be stored as plaintext.");
            Assert.IsFalse(string.Equals(postmarkTokenCiphertext, "target-postmark-secret", StringComparison.Ordinal),
                "The protected operator token must not be stored as plaintext.");
            var protector = DataProtectionProvider.Create(
                    new DirectoryInfo(keyPath),
                    builder => builder
                        .SetApplicationName(SecurityContract.DataProtectionApplicationName)
                        .ProtectKeysWithCertificate(persistedCertificate))
                .CreateProtector(SecurityContract.IntegrationCredentialPurpose);
            Assert.IsTrue(StringComparer.Ordinal.Equals(sourceApiKey, protector.Unprotect(apiKeyCiphertext)),
                "The protected API key must round-trip the exact source value without Unicode trimming.");
            Assert.IsTrue(StringComparer.Ordinal.Equals(sourceAdminPassword, protector.Unprotect(adminPasswordCiphertext)),
                "A whitespace-only nonempty protected admin password must round-trip exactly.");
            Assert.IsTrue(StringComparer.Ordinal.Equals("target-postmark-secret", protector.Unprotect(postmarkTokenCiphertext)),
                "The target operator token must retain its exact configured value.");
            var reportText = await File.ReadAllTextAsync(report);
            using (var reportDocument = JsonDocument.Parse(reportText))
            {
                Assert.AreEqual(0, reportDocument.RootElement.GetProperty("importedCounts").GetProperty("smtp_settings").GetInt32());
                var polarisState = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "polaris_settings");
                Assert.IsTrue(polarisState.GetProperty("apiKeyProtected").GetBoolean());
                Assert.IsTrue(polarisState.GetProperty("adminPasswordProtected").GetBoolean());
                var tokenState = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "email_provider_token");
                Assert.AreEqual(1, tokenState.GetProperty("organizationId").GetInt32());
                Assert.IsTrue(tokenState.GetProperty("postmarkTokenProvisioned").GetBoolean());
            }
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NOT NULL;"));

            string KeyRingSnapshot() => string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(keyPath, "*", SearchOption.AllDirectories)
                    .OrderBy(path => Path.GetRelativePath(keyPath, path), StringComparer.Ordinal)
                    .Select(path =>
                    {
                        var information = new FileInfo(path);
                        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                        return $"{Path.GetRelativePath(keyPath, path)}|{information.Length}|{information.LastWriteTimeUtc.Ticks}|{digest}";
                    }));

            var keyRingBeforeFirstReconcile = KeyRingSnapshot();
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcile), reconcile.ToString());
            }
            Assert.AreEqual(keyRingBeforeFirstReconcile, KeyRingSnapshot(),
                "Successful read-only credential reconciliation must not create or rotate Data Protection keys.");

            var differentApiKeyCiphertext = protector.Protect(Guid.NewGuid().ToString("N"));
            var keyRingBeforeCredentialChecks = KeyRingSnapshot();
            await using (var changeCredential = connection.CreateCommand())
            {
                changeCredential.CommandText = "UPDATE [asap].[PolarisSettings] SET [ProtectedApiKey] = @value WHERE [OrganizationId] = 1;";
                changeCredential.Parameters.AddWithValue("@value", differentApiKeyCiphertext);
                Assert.AreEqual(1, await changeCredential.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var differentCredentialSqlFingerprint = ComputeTargetFingerprintForTest(target);
            var differentCredentialReport = await File.ReadAllTextAsync(report);
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, report, connectionEnvironmentName, reconcile),
                    "Reconciliation must decrypt and compare the protected credential to immutable source bytes.");
                StringAssert.Contains(reconcile.ToString(), "reconciliation_failed");
                Assert.IsFalse(reconcile.ToString().Contains(sourceApiKey, StringComparison.Ordinal));
            }
            Assert.AreEqual(differentCredentialSqlFingerprint, ComputeTargetFingerprintForTest(target),
                "Failed credential reconciliation must not repair SQL.");
            Assert.AreEqual(differentCredentialReport, await File.ReadAllTextAsync(report),
                "Failed credential reconciliation must not rewrite the report.");
            Assert.AreEqual(keyRingBeforeCredentialChecks, KeyRingSnapshot(),
                "Read-only credential reconciliation must not create or rotate Data Protection keys.");
            await using (var readChangedCredential = connection.CreateCommand())
            {
                readChangedCredential.CommandText = "SELECT [ProtectedApiKey] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;";
                Assert.AreEqual(differentApiKeyCiphertext, (string?)await readChangedCredential.ExecuteScalarAsync(),
                    "The independently rejected ciphertext must remain unchanged.");
            }

            var pendingPath = report + ".pending";
            var pendingReport = JsonNode.Parse(differentCredentialReport)!.AsObject();
            pendingReport["reportState"] = "commit_pending";
            pendingReport["reconciliationPassed"] = false;
            var pendingReportText = pendingReport.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(pendingPath, pendingReportText, new UTF8Encoding(false));
            File.Delete(report);
            using (var recover = new StringWriter())
            {
                Assert.AreEqual(1, RunRecoverReport(package, report, connectionEnvironmentName, recover),
                    "Report recovery must decrypt and compare the protected credential to immutable source bytes.");
                StringAssert.Contains(recover.ToString(), "reconciliation_failed");
                Assert.IsFalse(recover.ToString().Contains(sourceApiKey, StringComparison.Ordinal));
            }
            Assert.AreEqual(differentCredentialSqlFingerprint, ComputeTargetFingerprintForTest(target),
                "Failed credential recovery must not repair SQL.");
            Assert.AreEqual(pendingReportText, await File.ReadAllTextAsync(pendingPath),
                "Failed credential recovery must not rewrite or promote the prepared report.");
            Assert.IsFalse(File.Exists(report), "Failed credential recovery must leave the report unpromoted.");
            Assert.AreEqual(keyRingBeforeCredentialChecks, KeyRingSnapshot(),
                "Read-only credential recovery must not create or rotate Data Protection keys.");

            await using (var restoreCredential = connection.CreateCommand())
            {
                restoreCredential.CommandText = "UPDATE [asap].[PolarisSettings] SET [ProtectedApiKey] = @value WHERE [OrganizationId] = 1;";
                restoreCredential.Parameters.AddWithValue("@value", apiKeyCiphertext);
                Assert.AreEqual(1, await restoreCredential.ExecuteNonQueryAsync());
            }
            var restoredCredentialFingerprint = ComputeTargetFingerprintForTest(target);
            pendingReport = JsonNode.Parse(await File.ReadAllTextAsync(pendingPath))!.AsObject();
            pendingReport["targetFingerprintSha256"] = restoredCredentialFingerprint;
            await File.WriteAllTextAsync(
                pendingPath,
                pendingReport.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            using (var recover = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, report, connectionEnvironmentName, recover), recover.ToString());
            }
            Assert.IsTrue(File.Exists(report));
            Assert.IsFalse(File.Exists(pendingPath));
            Assert.AreEqual(keyRingBeforeCredentialChecks, KeyRingSnapshot(),
                "Successful read-only credential recovery must not create or rotate Data Protection keys.");
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcile), reconcile.ToString());
            }

            await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
                connection, target, package, report, connectionEnvironmentName,
                "source-absent seeded email-template key case only",
                "UPDATE [asap].[EmailTemplate] SET [TemplateKey] = N'Suggestion_Submitted' WHERE [OrganizationId] = 1 AND [TemplateKey] COLLATE Latin1_General_100_BIN2 = N'suggestion_submitted';",
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] COLLATE Latin1_General_100_BIN2 = N'Suggestion_Submitted';",
                1,
                "UPDATE [asap].[EmailTemplate] SET [TemplateKey] = N'suggestion_submitted' WHERE [OrganizationId] = 1 AND [TemplateKey] COLLATE Latin1_General_100_BIN2 = N'Suggestion_Submitted';",
                "SELECT COUNT(*) FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] COLLATE Latin1_General_100_BIN2 = N'suggestion_submitted';",
                1);

            string tokenCiphertext;
            await using (var readToken = connection.CreateCommand())
            {
                readToken.CommandText = "SELECT [ProtectedServerToken] FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1;";
                tokenCiphertext = (string)(await readToken.ExecuteScalarAsync() ?? throw new InvalidOperationException("The operator-provisioned token ciphertext is missing."));
            }
            await using (var removeToken = connection.CreateCommand())
            {
                removeToken.CommandText = "UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = NULL WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NOT NULL;";
                Assert.AreEqual(1, await removeToken.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            var tokenRemovalFingerprint = ComputeTargetFingerprintForTest(target);
            var tokenRemovalReport = await File.ReadAllTextAsync(report);
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, report, connectionEnvironmentName, reconcile), "Reconciliation must reject removal of a target-only provisioned token when the source has no SMTP row.");
                StringAssert.Contains(reconcile.ToString(), "reconciliation_failed");
            }
            Assert.AreEqual(tokenRemovalFingerprint, ComputeTargetFingerprintForTest(target), "Reconciliation must not repair protected-token presence drift.");
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[EmailSettings] WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NOT NULL;"));
            Assert.AreEqual(tokenRemovalReport, await File.ReadAllTextAsync(report), "Failed reconciliation must not promote or rewrite the report.");
            await using (var restoreToken = connection.CreateCommand())
            {
                restoreToken.CommandText = "UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = @token WHERE [OrganizationId] = 1 AND [ProtectedServerToken] IS NULL;";
                restoreToken.Parameters.AddWithValue("@token", tokenCiphertext);
                Assert.AreEqual(1, await restoreToken.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(report, target);
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcile), reconcile.ToString());
            }

            Assert.IsFalse(reportText.Contains(sourceApiKey, StringComparison.Ordinal), "The report must not contain plaintext integration credentials.");
            Assert.IsFalse(reportText.Contains("target-postmark-secret", StringComparison.Ordinal));
            StringAssert.Contains(reportText, "promoted_existing");
            using (var reportDocument = JsonDocument.Parse(reportText))
            {
                var promotedRecipient = reportDocument.RootElement.GetProperty("transformations").EnumerateArray().Single(item =>
                    item.GetProperty("entity").GetString() == "staff_user" &&
                    item.GetProperty("sourceId").GetString() == "pb-staff-1");
                Assert.AreEqual("source-admin@example.org", promotedRecipient.GetProperty("targetAssignmentRecipient").GetString());
                Assert.IsTrue(promotedRecipient.GetProperty("targetWeeklyEligible").GetBoolean());
                Assert.AreEqual("staff_email", promotedRecipient.GetProperty("notificationEmailSource").GetString());
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
                "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'INSERTED-BOOTSTRAP@EXAMPLE.ORG' AND [EntraTenantId] IS NULL AND [EntraObjectId] IS NULL AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
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

    private static int RunImport(
        string package,
        string report,
        string connectionEnvironmentName,
        Guid tenantId,
        StringWriter error)
    {
        var exitCode = MigrationCli.Run(
            [
                "import", "--package", package,
                "--connection-string-env", connectionEnvironmentName,
                "--allowed-tenant-ids", tenantId.ToString(),
                "--report", report,
                "--external-config", ExternalConfigurationPath(package)
            ],
            TextWriter.Null,
            error);
        if (exitCode != 0 &&
            error.ToString().Contains("import_committed_report_failed", StringComparison.Ordinal) &&
            File.Exists(report + ".pending"))
        {
            using var recoveryError = new StringWriter();
            var recoveryExitCode = RunRecoverReport(package, report, connectionEnvironmentName, recoveryError);
            error.WriteLine($"Pending-report recovery diagnostic (exit {recoveryExitCode}): {recoveryError.ToString().Trim()}");
        }

        return exitCode;
    }

    private static int RunReconcile(
        string package,
        string report,
        string connectionEnvironmentName,
        StringWriter error) =>
        MigrationCli.Run(
            [
                "reconcile", "--package", package,
                "--connection-string-env", connectionEnvironmentName,
                "--report", report,
                "--external-config", ExternalConfigurationPath(package)
            ],
            TextWriter.Null,
            error);

    private static int RunRecoverReport(
        string package,
        string report,
        string connectionEnvironmentName,
        StringWriter error) =>
        MigrationCli.Run(
            [
                "recover-report", "--package", package,
                "--connection-string-env", connectionEnvironmentName,
                "--report", report,
                "--external-config", ExternalConfigurationPath(package)
            ],
            TextWriter.Null,
            error);

    private static async Task AssertFreshImportTargetAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 1;"));
        Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] > 1;"));
        Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[StaffUser];"));
        Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping];"));
        Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[TitleRequest];"));
        Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest];"));
    }

    private static async Task<long> ReadLongAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void RefreshReportFingerprint(string reportPath, string connectionString)
    {
        var fingerprint = ComputeTargetFingerprintForTest(connectionString);
        var report = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
        report["targetFingerprintSha256"] = fingerprint;
        File.WriteAllText(
            reportPath,
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static async Task AssertFormatMappingKindDriftRejectedAsync(
        string root,
        string master,
        Guid tenantId)
    {
        var scenarioRoot = Path.Combine(root, "format-mapping-kind");
        Directory.CreateDirectory(scenarioRoot);
        var databaseName = $"AsapMigrationFormatMappingKind_{Guid.NewGuid():N}";
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        try
        {
            var package = CreateMinimalPackage(
                scenarioRoot,
                """
                CREATE TABLE [material_formats]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT NOT NULL,
                    [libraryOrganization] TEXT, [code] TEXT NOT NULL, [label] TEXT NOT NULL,
                    [enabled] INTEGER NOT NULL, [sortOrder] INTEGER NOT NULL
                );
                INSERT INTO [material_formats] VALUES
                    ('library-owned-format', 'library', 'pb-org-2', 'mapping-proof', 'Mapping Proof', 1, 20);
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(scenarioRoot, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, environmentName, tenantId, importError), importError.ToString());
            }

            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            var formatId = await ReadLongAsync(
                connection,
                "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format' AND [PocketBaseId] = N'library-owned-format';");
            Assert.AreEqual(0, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormatOverride] WHERE [Id] = " + formatId + ";"));
            await using (var changeMappingKind = connection.CreateCommand())
            {
                changeMappingKind.CommandText = "UPDATE [asap].[LegacyPocketBaseMapping] SET [EntityType] = N'material_format_override' WHERE [EntityType] = N'material_format' AND [PocketBaseId] = N'library-owned-format';";
                Assert.AreEqual(1, await changeMappingKind.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            var refreshedReport = await File.ReadAllTextAsync(reportPath);
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(1, RunReconcile(package, reportPath, environmentName, reconcileError));
                StringAssert.Contains(reconcileError.ToString(), "reconciliation_failed");
            }
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'material_format_override' AND [PocketBaseId] = N'library-owned-format' AND [NewId] = " + formatId + ";"));
            Assert.AreEqual(1, await ScalarAsync(connection, "SELECT COUNT(*) FROM [asap].[MaterialFormat] WHERE [Id] = " + formatId + " AND [OwnerOrganizationId] = 2 AND [Code] = N'mapping-proof' AND [Label] = N'Mapping Proof';"));
            Assert.AreEqual(refreshedReport, await File.ReadAllTextAsync(reportPath));

            await using (var restoreMappingKind = connection.CreateCommand())
            {
                restoreMappingKind.CommandText = "UPDATE [asap].[LegacyPocketBaseMapping] SET [EntityType] = N'material_format' WHERE [EntityType] = N'material_format_override' AND [PocketBaseId] = N'library-owned-format';";
                Assert.AreEqual(1, await restoreMappingKind.ExecuteNonQueryAsync());
            }
            RefreshReportFingerprint(reportPath, target);
            using (var validReconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, validReconcileError), validReconcileError.ToString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            if (Directory.Exists(scenarioRoot))
            {
                Directory.Delete(scenarioRoot, recursive: true);
            }
        }
    }

    private static async Task AssertFingerprintRefreshedFormatDriftRejectedAsync(
        SqlConnection connection,
        string targetConnectionString,
        string packagePath,
        string reportPath,
        string connectionEnvironmentName,
        string driftSql,
        string driftAssertionSql,
        string restoreSql,
        string restoredAssertionSql,
        string expectedDiagnostic)
    {
        await using (var drift = connection.CreateCommand())
        {
            drift.CommandText = driftSql;
            Assert.AreEqual(1, await drift.ExecuteNonQueryAsync());
        }
        RefreshReportFingerprint(reportPath, targetConnectionString);
        var reportWithRefreshedFingerprint = await File.ReadAllTextAsync(reportPath);
        using (var error = new StringWriter())
        {
            Assert.AreEqual(1, RunReconcile(packagePath, reportPath, connectionEnvironmentName, error));
            StringAssert.Contains(error.ToString(), expectedDiagnostic);
        }
        Assert.AreEqual(1, await ScalarAsync(connection, driftAssertionSql));
        Assert.AreEqual(reportWithRefreshedFingerprint, await File.ReadAllTextAsync(reportPath));

        await using (var restore = connection.CreateCommand())
        {
            restore.CommandText = restoreSql;
            Assert.AreEqual(1, await restore.ExecuteNonQueryAsync());
        }
        RefreshReportFingerprint(reportPath, targetConnectionString);
        using (var valid = new StringWriter())
        {
            Assert.AreEqual(0, RunReconcile(packagePath, reportPath, connectionEnvironmentName, valid), valid.ToString());
        }
        Assert.AreEqual(1, await ScalarAsync(connection, restoredAssertionSql));
    }

    private static async Task AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(
        SqlConnection connection,
        string targetConnectionString,
        string packagePath,
        string reportPath,
        string connectionEnvironmentName,
        string subject,
        string driftSql,
        string driftAssertionSql,
        int expectedDriftStateCount,
        string restoreSql,
        string restoredAssertionSql,
        int expectedRestoredStateCount)
    {
        await using (var drift = connection.CreateCommand())
        {
            drift.CommandText = driftSql;
            Assert.AreEqual(1, await drift.ExecuteNonQueryAsync(), $"The {subject} SQL mutation must change one target row.");
        }

        Assert.AreEqual(expectedDriftStateCount, await ScalarAsync(connection, driftAssertionSql), $"The {subject} drift must be present before reconciliation.");
        RefreshReportFingerprint(reportPath, targetConnectionString);
        var fingerprintWithDrift = ComputeTargetFingerprintForTest(targetConnectionString);
        var reportWithRefreshedFingerprint = await File.ReadAllTextAsync(reportPath);
        using (var error = new StringWriter())
        {
            Assert.AreEqual(1, RunReconcile(packagePath, reportPath, connectionEnvironmentName, error), $"Reconciliation accepted refreshed-fingerprint {subject} drift.");
            StringAssert.Contains(error.ToString(), "reconciliation_failed", $"The {subject} drift must be rejected by the independent source oracle.");
        }

        Assert.AreEqual(fingerprintWithDrift, ComputeTargetFingerprintForTest(targetConnectionString), $"Reconciliation must not repair {subject} drift.");
        Assert.AreEqual(expectedDriftStateCount, await ScalarAsync(connection, driftAssertionSql), $"The {subject} drift must remain present after failed verification.");
        Assert.AreEqual(reportWithRefreshedFingerprint, await File.ReadAllTextAsync(reportPath), $"Failed verification must not change or promote the report for {subject} drift.");

        await using (var restore = connection.CreateCommand())
        {
            restore.CommandText = restoreSql;
            Assert.AreEqual(1, await restore.ExecuteNonQueryAsync(), $"The {subject} mutation must be restorable as one SQL row change.");
        }

        Assert.AreEqual(expectedRestoredStateCount, await ScalarAsync(connection, restoredAssertionSql), $"The {subject} source state must be restored before positive reconciliation.");
        RefreshReportFingerprint(reportPath, targetConnectionString);
        using (var restored = new StringWriter())
        {
            Assert.AreEqual(0, RunReconcile(packagePath, reportPath, connectionEnvironmentName, restored), $"Restored {subject} source state should reconcile: {restored}");
        }
    }

    private static void AssertEquivalentReportsExceptTargetBinding(
        string firstReportPath,
        string firstConnectionString,
        string secondReportPath,
        string secondConnectionString,
        string message)
    {
        var first = JsonNode.Parse(File.ReadAllText(firstReportPath))!.AsObject();
        var second = JsonNode.Parse(File.ReadAllText(secondReportPath))!.AsObject();
        var firstFingerprint = first["targetFingerprintSha256"]!.GetValue<string>();
        var secondFingerprint = second["targetFingerprintSha256"]!.GetValue<string>();
        var firstIdentity = first["targetIdentitySha256"]!.GetValue<string>();
        var secondIdentity = second["targetIdentitySha256"]!.GetValue<string>();
        Assert.IsTrue(IsSha256(firstFingerprint));
        Assert.IsTrue(IsSha256(secondFingerprint));
        Assert.IsTrue(IsSha256(firstIdentity));
        Assert.IsTrue(IsSha256(secondIdentity));
        Assert.AreEqual(ComputeTargetFingerprintForTest(firstConnectionString), firstFingerprint);
        Assert.AreEqual(ComputeTargetFingerprintForTest(secondConnectionString), secondFingerprint);
        Assert.AreEqual(ComputeTargetIdentityForTest(firstConnectionString), firstIdentity);
        Assert.AreEqual(ComputeTargetIdentityForTest(secondConnectionString), secondIdentity);

        first["targetFingerprintSha256"] = "target-fingerprint";
        first["targetIdentitySha256"] = "target-identity";
        second["targetFingerprintSha256"] = "target-fingerprint";
        second["targetIdentitySha256"] = "target-identity";
        Assert.AreEqual(first.ToJsonString(), second.ToJsonString(), message);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ComputeTargetFingerprintForTest(string connectionString)
    {
        var method = typeof(MigrationReconciler)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(item => item.Name == "ComputeTargetFingerprint" &&
                            item.GetParameters().Length == 1 &&
                            item.GetParameters()[0].ParameterType == typeof(string));
        return (string)method.Invoke(null, [connectionString])!;
    }

    private static string ComputeTargetIdentityForTest(string connectionString)
    {
        var method = typeof(MigrationReconciler).GetMethod(
            "ComputeTargetIdentitySha256",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException("The target identity helper is missing.");
        return (string)method.Invoke(null, [connectionString])!;
    }

    internal static string CreateMinimalPackage(
        string root,
        string additionalSql = "",
        Action<string>? prepareStorage = null,
        string exportedAtUtc = "2030-01-02T03:04:05Z")
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
                    [enabledForPatrons] INTEGER NOT NULL,
                    [organizationCodeId] INTEGER,
                    [parentOrganizationId] INTEGER
                );
                INSERT INTO [polaris_organizations] VALUES
                    ('pb-org-1', '1', 'ASAP System', 'ASAP', 0, NULL, NULL),
                    ('pb-org-2', '2', 'Test Library', 'TEST', 1, 2, 1),
                    ('pb-org-20', '20', 'Test Branch', 'BR', 1, 3, 2);
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
                "--exported-at-utc", exportedAtUtc,
                "--confirm-source-stopped"
            ],
            TextWriter.Null,
            exportError);
        Assert.AreEqual(0, exitCode, exportError.ToString());
        File.WriteAllText(
            Path.Combine(root, "asap.settings.json"),
            JsonSerializer.Serialize(TestConfigurationFactory.Create(), new JsonSerializerOptions { WriteIndented = true }));
        return package;
    }

    private static string ExternalConfigurationPath(string package) =>
        Path.Combine(Directory.GetParent(package)!.FullName, "asap.settings.json");

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

    private static void SetManifestWarning(string package, string value)
    {
        var path = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest["warnings"]!.AsArray()[0] = value;
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

    private static void SetPackageSourceGitSha(string package, string sourceGitSha)
    {
        var manifestPath = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["pocketBaseSourceGitSha"] = sourceGitSha;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        foreach (var relativePath in new[]
        {
            "effective-legacy-runtime-config.json",
            "effective-legacy-operational-config.json"
        })
        {
            var path = Path.Combine(package, relativePath);
            var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            metadata["capturedFromPocketBaseSha"] = sourceGitSha;
            File.WriteAllText(path, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            UpdateManifestEntry(package, relativePath);
        }
    }

    private static void SetDatabaseOnline(string masterConnectionString, string databaseName, bool online)
    {
        using var connection = new SqlConnection(masterConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        var quotedName = new SqlCommandBuilder().QuoteIdentifier(databaseName);
        command.CommandText = online
            ? $"ALTER DATABASE {quotedName} SET ONLINE;"
            : $"ALTER DATABASE {quotedName} SET OFFLINE WITH ROLLBACK IMMEDIATE;";
        command.ExecuteNonQuery();
        SqlConnection.ClearAllPools();
    }

    private static void AddManifestFile(string package, string relativePath, byte[] data)
    {
        var manifestPath = Path.Combine(package, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["files"]!.AsArray().Add(
            new JsonObject
            {
                ["path"] = relativePath,
                ["length"] = data.LongLength,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data))
            });
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
