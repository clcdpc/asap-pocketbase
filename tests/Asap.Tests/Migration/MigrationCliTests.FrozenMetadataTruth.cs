using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task FrozenMetadataMustMatchSourceRowsAndPinnedOperationalRulesBeforeImport()
    {
        const string runtimeFile = "effective-legacy-runtime-config.json";
        const string operationalFile = "effective-legacy-operational-config.json";
        const string defaultCron = "0 * * * *";
        const string paddedCron = " 0 * * * * ";
        const string sourceIcon = "https://icons.example.org/{id}.gif";
        const string defaultIcon = "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";
        const string expectedStaffUrl = "https://source-staff.example.org/staff/";
        const string expectedPublicStaffUrl = "https://captured-public.example.org/staff/";
        const string defaultStaffUrl = "http://localhost:8090/staff/";
        const string invalidConnectionString = "DefinitelyNotASqlClientKeyword=1";
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-frozen-metadata-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationFrozenMetadata_{Guid.NewGuid():N}";
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
        var queueNames = new[]
        {
            "pending_suggestion_isbn_checks", "outstanding_purchases", "pending_holds", "checked_out",
            "outstanding_timeout", "pending_hold_timeout", "hold_pickup_timeout", "additional_copy_timeout"
        };
        var environmentNames = new[]
            {
                "ASAP_STAFF_URL", "ASAP_PUBLIC_URL", "ASAP_BASE_URL",
                "ASAP_CRON_SCHEDULE", "ASAP_ORG_SYNC_CRON_SCHEDULE",
                "ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", "ASAP_ISBN_CHECK_CRON_SCHEDULE",
                "ASAP_JOB_PAGE_SIZE", "ASAP_JOB_MAX_PER_RUN",
                "ASAP_TIMEOUT_PAGE_SIZE", "ASAP_TIMEOUT_MAX_PER_RUN",
                "ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE", "ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN"
            }
            .Concat(queueNames.SelectMany(name => new[]
            {
                $"ASAP_{name.ToUpperInvariant()}_PAGE_SIZE",
                $"ASAP_{name.ToUpperInvariant()}_MAX_PER_RUN"
            }))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var previousEnvironment = environmentNames.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        var keyPath = Path.Combine(root, "keys");
        var targetDeployed = false;
        Directory.CreateDirectory(root);

        void ClearSourceEnvironment()
        {
            foreach (var name in environmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        string WriteExternalConfiguration(string package)
        {
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            var path = ExternalConfigurationPath(package);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            return path;
        }

        JsonObject RuntimeSetting(JsonObject metadata, string name) =>
            metadata["settings"]!.AsObject()[name]!.AsObject();

        JsonObject ProcessingLimit(JsonObject metadata, string section, string dimension) =>
            metadata["processingLimits"]!.AsObject()[section]!.AsObject()[dimension]!.AsObject();

        JsonObject QueueLimit(JsonObject metadata, string queue, string dimension) =>
            metadata["processingLimits"]!.AsObject()["effectiveQueues"]!.AsObject()[queue]!
                .AsObject()[dimension]!.AsObject();

        JsonObject ConfiguredOverrides(JsonObject metadata) =>
            metadata["processingLimits"]!.AsObject()["configuredQueueOverrides"]!.AsObject();

        JsonObject RetiredOverride(JsonObject metadata) =>
            metadata["processingLimits"]!.AsObject()["obsoletePathOverrides"]!.AsObject()
                ["pending_isbn_checks"]!.AsObject();

        void SetTargetSchedule(JsonObject external, string key, string value)
        {
            external["Hangfire"]!.AsObject()["Schedules"]!.AsObject()[key] = value;
        }

        void SetTargetLimit(JsonObject external, string section, string? queue, string dimension, int value)
        {
            var limits = external["Hangfire"]!.AsObject()["ProcessingLimits"]!.AsObject();
            var targetGroup = section switch
            {
                "Default" => limits["Default"]!.AsObject(),
                "Timeouts" => limits["Timeouts"]!.AsObject(),
                "Queues" => limits["Queues"]!.AsObject()[queue!]!.AsObject(),
                _ => throw new InvalidOperationException("Unknown target processing-limit section.")
            };
            targetGroup[dimension] = value;
        }

        int ValidatePackage(string package, StringWriter error) => MigrationCli.Run(
            ["validate", "--package", package],
            TextWriter.Null,
            error);

        void AssertMetadataConflict(StringWriter error, string caseName)
        {
            var diagnostic = error.ToString();
            var lineBreak = diagnostic.IndexOfAny(['\r', '\n']);
            var code = lineBreak < 0 ? diagnostic.Trim() : diagnostic[..lineBreak].Trim();
            Assert.AreEqual("package_metadata_conflict", code, $"{caseName} must fail with package_metadata_conflict.");
        }

        void AssertPackageSecretForbidden(StringWriter error, string caseName)
        {
            var diagnostic = error.ToString();
            var lineBreak = diagnostic.IndexOfAny(['\r', '\n']);
            var code = lineBreak < 0 ? diagnostic.Trim() : diagnostic[..lineBreak].Trim();
            Assert.AreEqual("package_secret_forbidden", code, $"{caseName} must fail with package_secret_forbidden.");
            Assert.IsFalse(diagnostic.Contains("literal-canary", StringComparison.Ordinal));
            Assert.IsFalse(diagnostic.Contains("fingerprint-canary", StringComparison.Ordinal));
            Assert.IsFalse(diagnostic.Contains("extension-canary", StringComparison.Ordinal));
        }

        void AssertMutationDiagnostic(StringWriter error, string expectedCode, string caseName)
        {
            if (expectedCode == "package_metadata_conflict")
            {
                AssertMetadataConflict(error, caseName);
            }
            else
            {
                Assert.AreEqual("package_secret_forbidden", expectedCode, $"Unexpected diagnostic expectation for {caseName}.");
                AssertPackageSecretForbidden(error, caseName);
            }
        }

        try
        {
            ClearSourceEnvironment();
            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidConnectionString);
            Directory.CreateDirectory(keyPath);
            File.WriteAllText(Path.Combine(keyPath, "sentinel.txt"), "Frozen metadata rejection must preserve this key ring.");
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);

            Environment.SetEnvironmentVariable("ASAP_PENDING_HOLDS_PAGE_SIZE", "not-a-number");
            Environment.SetEnvironmentVariable("ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE", "not-a-number");
            Environment.SetEnvironmentVariable("ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN", "not-a-number");
            var persistedPackage = CreateMinimalPackage(
                Path.Combine(root, "persisted-source"),
                """
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings0000001', '  https://source-staff.example.org  ', '  https://icons.example.org/{id}.gif  ');
                CREATE TABLE [polaris_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                    [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                    [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
                );
                INSERT INTO [polaris_settings] VALUES
                    ('polaris-1', 'https://polaris.example.org', 'access', '   ',
                     'EXAMPLE', 'service-user', '  ', '99', '42', '7', '3');
                """);
            WriteExternalConfiguration(persistedPackage);

            var persistedRuntime = JsonNode.Parse(File.ReadAllText(Path.Combine(persistedPackage, runtimeFile)))!.AsObject();
            Assert.AreEqual(expectedStaffUrl, RuntimeSetting(persistedRuntime, "StaffApplicationUrl")["value"]!.GetValue<string>());
            Assert.AreEqual("persisted_database", RuntimeSetting(persistedRuntime, "StaffApplicationUrl")["provenance"]!.GetValue<string>());
            Assert.AreEqual("system_settings.staffUrl", RuntimeSetting(persistedRuntime, "StaffApplicationUrl")["source"]!.GetValue<string>());
            Assert.AreEqual(sourceIcon, RuntimeSetting(persistedRuntime, "MaterialTypeIconUrlPattern")["value"]!.GetValue<string>());
            Assert.IsTrue(RuntimeSetting(persistedRuntime, "PolarisApiKey")["hasValue"]!.GetValue<bool>());
            Assert.IsTrue(RuntimeSetting(persistedRuntime, "PolarisAdminPassword")["hasValue"]!.GetValue<bool>());
            Assert.AreEqual("persisted_database", RuntimeSetting(persistedRuntime, "PolarisApiKey")["provenance"]!.GetValue<string>());
            Assert.AreEqual("persisted_database", RuntimeSetting(persistedRuntime, "PolarisAdminPassword")["provenance"]!.GetValue<string>());
            Assert.AreEqual("polaris_settings.apiKey", RuntimeSetting(persistedRuntime, "PolarisApiKey")["source"]!.GetValue<string>());
            Assert.AreEqual("polaris_settings.adminPassword", RuntimeSetting(persistedRuntime, "PolarisAdminPassword")["source"]!.GetValue<string>());

            var persistedOperational = JsonNode.Parse(File.ReadAllText(Path.Combine(persistedPackage, operationalFile)))!.AsObject();
            Assert.AreEqual(defaultCron, persistedOperational["schedules"]!["asap-hold-check"]!["value"]!.GetValue<string>());
            Assert.AreEqual("code_default", persistedOperational["schedules"]!["asap-hold-check"]!["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_CRON_SCHEDULE", persistedOperational["schedules"]!["asap-hold-check"]!["source"]!.GetValue<string>());
            Assert.AreEqual("*/5 * * * *", persistedOperational["schedules"]!["asap-isbn-check"]!["value"]!.GetValue<string>());
            Assert.AreEqual("code_default", persistedOperational["schedules"]!["asap-isbn-check"]!["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_ISBN_CHECK_CRON_SCHEDULE", persistedOperational["schedules"]!["asap-isbn-check"]!["source"]!.GetValue<string>());
            Assert.AreEqual("0 2 * * *", persistedOperational["schedules"]!["asap-organization-sync"]!["value"]!.GetValue<string>());
            Assert.AreEqual("code_default", persistedOperational["schedules"]!["asap-organization-sync"]!["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_ORG_SYNC_CRON_SCHEDULE", persistedOperational["schedules"]!["asap-organization-sync"]!["source"]!.GetValue<string>());
            Assert.AreEqual("0 20 * * 0", persistedOperational["schedules"]!["asap-weekly-staff-action-summary"]!["value"]!.GetValue<string>());
            Assert.AreEqual("code_default", persistedOperational["schedules"]!["asap-weekly-staff-action-summary"]!["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE", persistedOperational["schedules"]!["asap-weekly-staff-action-summary"]!["source"]!.GetValue<string>());
            Assert.AreEqual(50, ProcessingLimit(persistedOperational, "global", "pageSize")["value"]!.GetValue<int>());
            Assert.AreEqual("code_default", ProcessingLimit(persistedOperational, "global", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_JOB_PAGE_SIZE", ProcessingLimit(persistedOperational, "global", "pageSize")["source"]!.GetValue<string>());
            Assert.AreEqual(500, ProcessingLimit(persistedOperational, "global", "maxPerRun")["value"]!.GetValue<int>());
            Assert.AreEqual("code_default", ProcessingLimit(persistedOperational, "global", "maxPerRun")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_JOB_MAX_PER_RUN", ProcessingLimit(persistedOperational, "global", "maxPerRun")["source"]!.GetValue<string>());
            Assert.AreEqual("inherited_global", ProcessingLimit(persistedOperational, "timeouts", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual(50, ProcessingLimit(persistedOperational, "timeouts", "pageSize")["value"]!.GetValue<int>());
            Assert.AreEqual("ASAP_TIMEOUT_PAGE_SIZE", ProcessingLimit(persistedOperational, "timeouts", "pageSize")["source"]!.GetValue<string>());
            Assert.AreEqual("inherited_global", ProcessingLimit(persistedOperational, "timeouts", "maxPerRun")["provenance"]!.GetValue<string>());
            Assert.AreEqual(500, ProcessingLimit(persistedOperational, "timeouts", "maxPerRun")["value"]!.GetValue<int>());
            Assert.AreEqual("ASAP_TIMEOUT_MAX_PER_RUN", ProcessingLimit(persistedOperational, "timeouts", "maxPerRun")["source"]!.GetValue<string>());
            Assert.AreEqual("inherited_global", QueueLimit(persistedOperational, "outstanding_purchases", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE", QueueLimit(persistedOperational, "outstanding_purchases", "pageSize")["queueOverrideSource"]!.GetValue<string>());
            Assert.AreEqual("inherited_timeout", QueueLimit(persistedOperational, "hold_pickup_timeout", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("environment_override", QueueLimit(persistedOperational, "pending_holds", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_PENDING_HOLDS_PAGE_SIZE", QueueLimit(persistedOperational, "pending_holds", "pageSize")["queueOverrideSource"]!.GetValue<string>());
            Assert.AreEqual(50, ConfiguredOverrides(persistedOperational)["pending_holds"]!["pageSize"]!.GetValue<int>());
            Assert.IsTrue(ConfiguredOverrides(persistedOperational)["pending_holds"]!.AsObject().ContainsKey("maxPerRun"));
            Assert.IsNull(ConfiguredOverrides(persistedOperational)["pending_holds"]!["maxPerRun"]);
            Assert.AreEqual("ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN", RetiredOverride(persistedOperational)["maxPerRunEnvKey"]!.GetValue<string>());
            Assert.AreEqual("retired_with_hourly_identifier_path", RetiredOverride(persistedOperational)["resolution"]!.GetValue<string>());
            Assert.AreEqual(1, RetiredOverride(persistedOperational)["pageSize"]!["value"]!.GetValue<int>());
            Assert.AreEqual("ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE", RetiredOverride(persistedOperational)["pageSizeEnvKey"]!.GetValue<string>());
            Assert.AreEqual(1, RetiredOverride(persistedOperational)["maxPerRun"]!["value"]!.GetValue<int>());
            Assert.AreEqual("ASAP_PENDING_ISBN_CHECKS_MAX_PER_RUN", RetiredOverride(persistedOperational)["maxPerRunEnvKey"]!.GetValue<string>());
            Assert.AreEqual(1, ConfiguredOverrides(persistedOperational)["pending_isbn_checks"]!["pageSize"]!.GetValue<int>());
            Assert.AreEqual(1, ConfiguredOverrides(persistedOperational)["pending_isbn_checks"]!["maxPerRun"]!.GetValue<int>());
            Assert.AreEqual(2, ConfiguredOverrides(persistedOperational).Count);

            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, ValidatePackage(persistedPackage, error), error.ToString());
            }

            ClearSourceEnvironment();
            Environment.SetEnvironmentVariable("ASAP_STAFF_URL", null);
            Environment.SetEnvironmentVariable("ASAP_BASE_URL", "#fragment");
            Environment.SetEnvironmentVariable("ASAP_PUBLIC_URL", "https://captured-public.example.org");
            Environment.SetEnvironmentVariable("ASAP_CRON_SCHEDULE", paddedCron);
            Environment.SetEnvironmentVariable("ASAP_JOB_PAGE_SIZE", "invalid-but-nonempty");
            Environment.SetEnvironmentVariable("ASAP_JOB_MAX_PER_RUN", "invalid-but-nonempty");
            Environment.SetEnvironmentVariable("ASAP_TIMEOUT_PAGE_SIZE", "invalid-but-nonempty");
            Environment.SetEnvironmentVariable("ASAP_TIMEOUT_MAX_PER_RUN", "invalid-but-nonempty");
            Environment.SetEnvironmentVariable("ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE", "invalid-but-nonempty");
            Environment.SetEnvironmentVariable("ASAP_OUTSTANDING_PURCHASES_MAX_PER_RUN", "invalid-but-nonempty");
            var publicPackage = CreateMinimalPackage(Path.Combine(root, "public-source"));
            WriteExternalConfiguration(publicPackage);
            var publicExternal = JsonNode.Parse(File.ReadAllText(ExternalConfigurationPath(publicPackage)))!.AsObject();
            SetTargetSchedule(publicExternal, "WorkflowProcessing", paddedCron);
            File.WriteAllText(
                ExternalConfigurationPath(publicPackage),
                publicExternal.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));

            var publicRuntime = JsonNode.Parse(File.ReadAllText(Path.Combine(publicPackage, runtimeFile)))!.AsObject();
            Assert.AreEqual(expectedPublicStaffUrl, RuntimeSetting(publicRuntime, "StaffApplicationUrl")["value"]!.GetValue<string>());
            Assert.AreEqual("environment_fallback", RuntimeSetting(publicRuntime, "StaffApplicationUrl")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_PUBLIC_URL", RuntimeSetting(publicRuntime, "StaffApplicationUrl")["source"]!.GetValue<string>());
            Assert.AreEqual(defaultIcon, RuntimeSetting(publicRuntime, "MaterialTypeIconUrlPattern")["value"]!.GetValue<string>());
            Assert.AreEqual("code_default", RuntimeSetting(publicRuntime, "MaterialTypeIconUrlPattern")["provenance"]!.GetValue<string>());
            Assert.AreEqual("normalization.defaultFormatIconUrlPattern", RuntimeSetting(publicRuntime, "MaterialTypeIconUrlPattern")["source"]!.GetValue<string>());
            Assert.IsFalse(RuntimeSetting(publicRuntime, "PolarisApiKey")["hasValue"]!.GetValue<bool>());
            Assert.IsFalse(RuntimeSetting(publicRuntime, "PolarisAdminPassword")["hasValue"]!.GetValue<bool>());
            Assert.AreEqual("absent_database_record", RuntimeSetting(publicRuntime, "PolarisApiKey")["provenance"]!.GetValue<string>());
            Assert.AreEqual("absent_database_record", RuntimeSetting(publicRuntime, "PolarisAdminPassword")["provenance"]!.GetValue<string>());
            Assert.AreEqual("polaris_settings.apiKey", RuntimeSetting(publicRuntime, "PolarisApiKey")["source"]!.GetValue<string>());
            Assert.AreEqual("polaris_settings.adminPassword", RuntimeSetting(publicRuntime, "PolarisAdminPassword")["source"]!.GetValue<string>());

            var publicOperational = JsonNode.Parse(File.ReadAllText(Path.Combine(publicPackage, operationalFile)))!.AsObject();
            Assert.AreEqual(paddedCron, publicOperational["schedules"]!["asap-hold-check"]!["value"]!.GetValue<string>());
            Assert.AreEqual("environment_override", publicOperational["schedules"]!["asap-hold-check"]!["provenance"]!.GetValue<string>());
            Assert.AreEqual(50, ProcessingLimit(publicOperational, "global", "pageSize")["value"]!.GetValue<int>());
            Assert.AreEqual("environment_override", ProcessingLimit(publicOperational, "global", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual(500, ProcessingLimit(publicOperational, "global", "maxPerRun")["value"]!.GetValue<int>());
            Assert.AreEqual("environment_override", ProcessingLimit(publicOperational, "global", "maxPerRun")["provenance"]!.GetValue<string>());
            Assert.AreEqual("environment_override", ProcessingLimit(publicOperational, "timeouts", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("environment_override", ProcessingLimit(publicOperational, "timeouts", "maxPerRun")["provenance"]!.GetValue<string>());
            Assert.AreEqual("environment_override", QueueLimit(publicOperational, "outstanding_purchases", "pageSize")["provenance"]!.GetValue<string>());
            Assert.AreEqual("environment_override", QueueLimit(publicOperational, "outstanding_purchases", "maxPerRun")["provenance"]!.GetValue<string>());
            Assert.AreEqual("ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE", QueueLimit(publicOperational, "outstanding_purchases", "pageSize")["queueOverrideSource"]!.GetValue<string>());
            Assert.AreEqual("ASAP_OUTSTANDING_PURCHASES_MAX_PER_RUN", QueueLimit(publicOperational, "outstanding_purchases", "maxPerRun")["queueOverrideSource"]!.GetValue<string>());
            Assert.AreEqual(50, ConfiguredOverrides(publicOperational)["outstanding_purchases"]!["pageSize"]!.GetValue<int>());
            Assert.AreEqual(500, ConfiguredOverrides(publicOperational)["outstanding_purchases"]!["maxPerRun"]!.GetValue<int>());
            Assert.AreEqual(1, ConfiguredOverrides(publicOperational).Count);
            Assert.IsFalse(ConfiguredOverrides(publicOperational).ContainsKey("pending_isbn_checks"));

            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, ValidatePackage(publicPackage, error), error.ToString());
            }

            ClearSourceEnvironment();
            var localhostPackage = CreateMinimalPackage(Path.Combine(root, "localhost-source"));
            using (var localhostRuntime = JsonDocument.Parse(File.ReadAllText(Path.Combine(localhostPackage, runtimeFile))))
            {
                var staff = localhostRuntime.RootElement.GetProperty("settings").GetProperty("StaffApplicationUrl");
                Assert.AreEqual(defaultStaffUrl, staff.GetProperty("value").GetString());
                Assert.AreEqual("code_default", staff.GetProperty("provenance").GetString());
                Assert.AreEqual("settings.staffUrl.localhost", staff.GetProperty("source").GetString());
            }
            using (var error = new StringWriter())
            {
                Assert.AreEqual(0, ValidatePackage(localhostPackage, error), error.ToString());
            }

            var mutations = new (string Name, string Package, string File, Action<JsonObject> Metadata, Action<JsonObject>? Target)[]
            {
                ("persisted staff value", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "StaffApplicationUrl")["value"] = "https://false.example.org/staff/", null),
                ("persisted staff provenance", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "StaffApplicationUrl")["provenance"] = "code_default", null),
                ("persisted staff source", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "StaffApplicationUrl")["source"] = "ASAP_STAFF_URL", null),
                ("safe icon value", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "MaterialTypeIconUrlPattern")["value"] = "https://forged.example.org/{SearchCode}.png", null),
                ("custom icon provenance", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "MaterialTypeIconUrlPattern")["provenance"] = "code_default", null),
                ("custom icon source", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "MaterialTypeIconUrlPattern")["source"] = "normalization.defaultFormatIconUrlPattern", null),
                ("absent icon value must be code default", publicPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "MaterialTypeIconUrlPattern")["value"] = "https://forged.example.org/{id}.gif", null),
                ("absent icon cannot claim a persisted row", publicPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "MaterialTypeIconUrlPattern")["provenance"] = "persisted_database", null),
                ("whitespace Polaris API key presence", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisApiKey")["hasValue"] = false, null),
                ("whitespace Polaris admin password presence", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisAdminPassword")["hasValue"] = false, null),
                ("persisted Polaris API key source", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisApiKey")["source"] = "polaris_settings.otherSecret", null),
                ("persisted Polaris admin password provenance", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisAdminPassword")["provenance"] = "absent_database_record", null),
                ("absent Polaris API key falsely present", publicPackage, runtimeFile,
                    metadata =>
                    {
                        RuntimeSetting(metadata, "PolarisApiKey")["hasValue"] = true;
                        RuntimeSetting(metadata, "PolarisApiKey")["provenance"] = "persisted_database";
                    }, null),
                ("absent Polaris admin password falsely present", publicPackage, runtimeFile,
                    metadata =>
                    {
                        RuntimeSetting(metadata, "PolarisAdminPassword")["hasValue"] = true;
                        RuntimeSetting(metadata, "PolarisAdminPassword")["provenance"] = "persisted_database";
                    }, null),
                ("absent Polaris admin password source", publicPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisAdminPassword")["source"] = "polaris_settings.otherSecret", null),
                ("absent staff URL falsely persisted", publicPackage, runtimeFile,
                    metadata =>
                    {
                        RuntimeSetting(metadata, "StaffApplicationUrl")["provenance"] = "persisted_database";
                        RuntimeSetting(metadata, "StaffApplicationUrl")["source"] = "system_settings.staffUrl";
                    }, null),
                ("no-row staff URL cannot come from environment helper", publicPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "StaffApplicationUrl")["value"] = "https://forged.example.org/staff", null),
                ("no-row code default must be exact", publicPackage, runtimeFile,
                    metadata =>
                    {
                        RuntimeSetting(metadata, "StaffApplicationUrl")["provenance"] = "code_default";
                        RuntimeSetting(metadata, "StaffApplicationUrl")["source"] = "settings.staffUrl.localhost";
                    }, null),
                ("cron default value is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-hold-check"]!["value"] = "15 * * * *",
                    external => SetTargetSchedule(external, "WorkflowProcessing", "15 * * * *")),
                ("ISBN cron code default is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-isbn-check"]!["value"] = "15 * * * *",
                    external => SetTargetSchedule(external, "IdentifierProcessing", "15 * * * *")),
                ("organization-sync cron code default is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-organization-sync"]!["value"] = "15 * * * *",
                    external => SetTargetSchedule(external, "OrganizationRefresh", "15 * * * *")),
                ("weekly-summary cron code default is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-weekly-staff-action-summary"]!["value"] = "15 * * * *",
                    external => SetTargetSchedule(external, "WeeklyStaffSummary", "15 * * * *")),
                ("cron source key is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-hold-check"]!["source"] = "ASAP_OTHER_CRON_SCHEDULE", null),
                ("cron provenance is pinned", persistedPackage, operationalFile,
                    metadata => metadata["schedules"]!["asap-hold-check"]!["provenance"] = "persisted_database", null),
                ("global code default value is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "pageSize")["value"] = 51,
                    external => SetTargetLimit(external, "Default", null, "PageSize", 51)),
                ("global environment page-size override respects its bound", publicPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "pageSize")["value"] = 501,
                    external => SetTargetLimit(external, "Default", null, "PageSize", 501)),
                ("global environment max-per-run override respects its bound", publicPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "maxPerRun")["value"] = 5001,
                    external => SetTargetLimit(external, "Default", null, "MaxPerRun", 5001)),
                ("global source key is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "pageSize")["source"] = "ASAP_WRONG_JOB_PAGE_SIZE", null),
                ("global max-per-run source key is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "maxPerRun")["source"] = "ASAP_WRONG_JOB_MAX_PER_RUN", null),
                ("global provenance cannot inherit", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "global", "pageSize")["provenance"] = "inherited_global", null),
                ("timeout inheritance equals global", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "pageSize")["value"] = 49,
                    external => SetTargetLimit(external, "Timeouts", null, "PageSize", 49)),
                ("timeout inherited provenance is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "pageSize")["provenance"] = "code_default", null),
                ("timeout environment page-size override respects its bound", publicPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "pageSize")["value"] = 501,
                    external => SetTargetLimit(external, "Timeouts", null, "PageSize", 501)),
                ("timeout environment max-per-run override respects its bound", publicPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "maxPerRun")["value"] = 5001,
                    external => SetTargetLimit(external, "Timeouts", null, "MaxPerRun", 5001)),
                ("timeout source key is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "pageSize")["source"] = "ASAP_WRONG_TIMEOUT_PAGE_SIZE", null),
                ("timeout max-per-run source key is pinned", persistedPackage, operationalFile,
                    metadata => ProcessingLimit(metadata, "timeouts", "maxPerRun")["source"] = "ASAP_WRONG_TIMEOUT_MAX_PER_RUN", null),
                ("queue global inheritance equals parent", persistedPackage, operationalFile,
                    metadata => QueueLimit(metadata, "outstanding_purchases", "pageSize")["value"] = 49,
                    external => SetTargetLimit(external, "Queues", "PurchasePromotion", "PageSize", 49)),
                ("queue timeout inheritance equals parent", persistedPackage, operationalFile,
                    metadata => QueueLimit(metadata, "hold_pickup_timeout", "pageSize")["value"] = 49,
                    external => SetTargetLimit(external, "Queues", "HoldPickupTimeout", "PageSize", 49)),
                ("queue environment page-size override respects its bound", publicPackage, operationalFile,
                    metadata =>
                    {
                        QueueLimit(metadata, "outstanding_purchases", "pageSize")["value"] = 501;
                        ConfiguredOverrides(metadata)["outstanding_purchases"]!["pageSize"] = 501;
                    },
                    external => SetTargetLimit(external, "Queues", "PurchasePromotion", "PageSize", 501)),
                ("queue environment max-per-run override respects its bound", publicPackage, operationalFile,
                    metadata =>
                    {
                        QueueLimit(metadata, "outstanding_purchases", "maxPerRun")["value"] = 5001;
                        ConfiguredOverrides(metadata)["outstanding_purchases"]!["maxPerRun"] = 5001;
                    },
                    external => SetTargetLimit(external, "Queues", "PurchasePromotion", "MaxPerRun", 5001)),
                ("queue environment key is pinned", persistedPackage, operationalFile,
                    metadata => QueueLimit(metadata, "pending_holds", "pageSize")["queueOverrideSource"] = "ASAP_PENDING_HOLDS_MAX_PER_RUN", null),
                ("queue max-per-run environment key is pinned", publicPackage, operationalFile,
                    metadata => QueueLimit(metadata, "outstanding_purchases", "maxPerRun")["queueOverrideSource"] = "ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE", null),
                ("queue provenance cannot be code default", persistedPackage, operationalFile,
                    metadata => QueueLimit(metadata, "pending_holds", "pageSize")["provenance"] = "code_default", null),
                ("configured queue override effective value matches", persistedPackage, operationalFile,
                    metadata => QueueLimit(metadata, "pending_holds", "pageSize")["value"] = 51,
                    external => SetTargetLimit(external, "Queues", "HoldPlacement", "PageSize", 51)),
                ("configured queue override preserves inherited null", persistedPackage, operationalFile,
                    metadata => ConfiguredOverrides(metadata)["pending_holds"]!["maxPerRun"] = 500, null),
                ("configured queue override cannot be omitted", persistedPackage, operationalFile,
                    metadata =>
                    {
                        _ = ConfiguredOverrides(metadata).Remove("pending_holds");
                    }, null),
                ("configured queue override cannot be invented", persistedPackage, operationalFile,
                    metadata => ConfiguredOverrides(metadata)["ghost_queue"] = new JsonObject { ["pageSize"] = 1, ["maxPerRun"] = null }, null),
                ("retired override source cannot be omitted", persistedPackage, operationalFile,
                    metadata =>
                    {
                        _ = ConfiguredOverrides(metadata).Remove("pending_isbn_checks");
                    }, null),
                ("retired override cannot be invented without capture", publicPackage, operationalFile,
                    metadata => ConfiguredOverrides(metadata)["pending_isbn_checks"] = new JsonObject { ["pageSize"] = null, ["maxPerRun"] = null }, null),
                ("retired override resolution is pinned", persistedPackage, operationalFile,
                    metadata => RetiredOverride(metadata)["resolution"] = "retired_without_resolution", null),
                ("retired page-size environment key is pinned", persistedPackage, operationalFile,
                    metadata => RetiredOverride(metadata)["pageSizeEnvKey"] = "ASAP_WRONG_PENDING_ISBN_CHECKS_PAGE_SIZE", null),
                ("retired max-per-run environment key is pinned", persistedPackage, operationalFile,
                    metadata => RetiredOverride(metadata)["maxPerRunEnvKey"] = "ASAP_WRONG_PENDING_ISBN_CHECKS_MAX_PER_RUN", null),
                ("retired override respects page-size bound", persistedPackage, operationalFile,
                    metadata =>
                    {
                        RetiredOverride(metadata)["pageSize"]!["value"] = 501;
                        ConfiguredOverrides(metadata)["pending_isbn_checks"]!["pageSize"] = 501;
                    }, null),
                ("retired override respects max-per-run bound", persistedPackage, operationalFile,
                    metadata =>
                    {
                        RetiredOverride(metadata)["maxPerRun"]!["value"] = 5001;
                        ConfiguredOverrides(metadata)["pending_isbn_checks"]!["maxPerRun"] = 5001;
                    }, null)
            };

            var secretChildMutations = new (string Name, string Package, string File, Action<JsonObject> Metadata, Action<JsonObject>? Target)[]
            {
                ("Polaris API key metadata rejects extra secret children", persistedPackage, runtimeFile,
                    metadata =>
                    {
                        var claim = RuntimeSetting(metadata, "PolarisApiKey");
                        claim["value"] = "literal-canary";
                        claim["fingerprint"] = "fingerprint-canary";
                        claim["extension"] = "extension-canary";
                    }, null),
                ("Polaris admin password metadata rejects extra secret children", persistedPackage, runtimeFile,
                    metadata =>
                    {
                        var claim = RuntimeSetting(metadata, "PolarisAdminPassword");
                        claim["value"] = "literal-canary";
                        claim["fingerprint"] = "fingerprint-canary";
                        claim["extension"] = "extension-canary";
                    }, null),
                ("Polaris API key metadata rejects a value child", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisApiKey")["value"] = "literal-canary", null),
                ("Polaris API key metadata rejects a fingerprint child", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisApiKey")["fingerprint"] = "fingerprint-canary", null),
                ("Polaris admin password metadata rejects a value child", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisAdminPassword")["value"] = "literal-canary", null),
                ("Polaris admin password metadata rejects a fingerprint child", persistedPackage, runtimeFile,
                    metadata => RuntimeSetting(metadata, "PolarisAdminPassword")["fingerprint"] = "fingerprint-canary", null)
            };

            var allMutations = mutations
                .Select(mutation => (
                    mutation.Name,
                    mutation.Package,
                    mutation.File,
                    mutation.Metadata,
                    mutation.Target,
                    ErrorCode: "package_metadata_conflict"))
                .Concat(secretChildMutations.Select(mutation => (
                    mutation.Name,
                    mutation.Package,
                    mutation.File,
                    mutation.Metadata,
                    mutation.Target,
                    ErrorCode: "package_secret_forbidden")))
                .ToArray();

            DeployDacpac(master, databaseName);
            targetDeployed = true;
            var targetFingerprintBefore = ComputeTargetFingerprintForTest(target);

            for (var index = 0; index < allMutations.Length; index++)
            {
                var mutation = allMutations[index];
                var metadataPath = Path.Combine(mutation.Package, mutation.File);
                var manifestPath = Path.Combine(mutation.Package, "manifest.json");
                var externalPath = ExternalConfigurationPath(mutation.Package);
                var originalMetadata = File.ReadAllBytes(metadataPath);
                var originalManifest = File.ReadAllBytes(manifestPath);
                var originalExternal = File.ReadAllBytes(externalPath);
                var reportPath = Path.Combine(root, $"rejected-{index}.json");
                try
                {
                    var metadata = JsonNode.Parse(originalMetadata)!.AsObject();
                    mutation.Metadata(metadata);
                    File.WriteAllText(
                        metadataPath,
                        metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                        new UTF8Encoding(false));
                    UpdateManifestEntry(mutation.Package, mutation.File);
                    if (mutation.Target is not null)
                    {
                        var external = JsonNode.Parse(originalExternal)!.AsObject();
                        mutation.Target(external);
                        File.WriteAllText(
                            externalPath,
                            external.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                            new UTF8Encoding(false));
                    }

                    using (var validateError = new StringWriter())
                    {
                        Assert.AreEqual(1, ValidatePackage(mutation.Package, validateError), mutation.Name);
                        AssertMutationDiagnostic(validateError, mutation.ErrorCode, mutation.Name);
                    }

                    using (var importError = new StringWriter())
                    {
                        Assert.AreEqual(
                            1,
                            RunImport(mutation.Package, reportPath, connectionEnvironmentName, tenantId, importError),
                            mutation.Name);
                        AssertMutationDiagnostic(importError, mutation.ErrorCode, mutation.Name);
                    }

                    Assert.IsFalse(File.Exists(reportPath), $"{mutation.Name} must not create a report.");
                    Assert.IsFalse(File.Exists(reportPath + ".pending"), $"{mutation.Name} must not prepare a report.");
                    Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath), $"{mutation.Name} must not write data-protection keys.");
                    Assert.AreEqual(
                        targetFingerprintBefore,
                        ComputeTargetFingerprintForTest(target),
                        $"{mutation.Name} must not change SQL when the connection setting is invalid.");

                    Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
                    using (var reachableImportError = new StringWriter())
                    {
                        Assert.AreEqual(
                            1,
                            RunImport(mutation.Package, reportPath, connectionEnvironmentName, tenantId, reachableImportError),
                            $"{mutation.Name} with a reachable target");
                        AssertMutationDiagnostic(reachableImportError, mutation.ErrorCode, mutation.Name);
                    }
                    Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidConnectionString);

                    Assert.IsFalse(File.Exists(reportPath), $"{mutation.Name} must not create a report with a reachable target.");
                    Assert.IsFalse(File.Exists(reportPath + ".pending"), $"{mutation.Name} must not prepare a report with a reachable target.");
                    Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath), $"{mutation.Name} must not write data-protection keys with a reachable target.");
                    Assert.AreEqual(
                        targetFingerprintBefore,
                        ComputeTargetFingerprintForTest(target),
                        $"{mutation.Name} must not change the reachable SQL target.");
                }
                finally
                {
                    File.WriteAllBytes(metadataPath, originalMetadata);
                    File.WriteAllBytes(manifestPath, originalManifest);
                    File.WriteAllBytes(externalPath, originalExternal);
                }

                CollectionAssert.AreEqual(originalMetadata, File.ReadAllBytes(metadataPath), $"{mutation.Name} must restore metadata bytes exactly.");
                CollectionAssert.AreEqual(originalManifest, File.ReadAllBytes(manifestPath), $"{mutation.Name} must restore manifest bytes exactly.");
                CollectionAssert.AreEqual(originalExternal, File.ReadAllBytes(externalPath), $"{mutation.Name} must restore target config bytes exactly.");
                using var restoredError = new StringWriter();
                Assert.AreEqual(0, ValidatePackage(mutation.Package, restoredError), $"Restored {mutation.Name} package must validate: {restoredError}");
            }

            ClearSourceEnvironment();
            Environment.SetEnvironmentVariable("ASAP_STAFF_URL", "https://import-time-staff.example.org");
            Environment.SetEnvironmentVariable("ASAP_BASE_URL", "https://import-time-base.example.org");
            Environment.SetEnvironmentVariable("ASAP_PUBLIC_URL", "https://import-time-public.example.org");
            Environment.SetEnvironmentVariable("ASAP_CRON_SCHEDULE", "15 * * * *");
            Environment.SetEnvironmentVariable("ASAP_JOB_PAGE_SIZE", "400");
            Environment.SetEnvironmentVariable("ASAP_TIMEOUT_PAGE_SIZE", "400");
            Environment.SetEnvironmentVariable("ASAP_OUTSTANDING_PURCHASES_PAGE_SIZE", "1");

            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var report = Path.Combine(root, "public-import-report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(publicPackage, report, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT [StaffApplicationUrl], [MaterialTypeIconUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(expectedPublicStaffUrl, reader.GetString(0));
                Assert.AreEqual(
                    defaultIcon,
                    reader.GetString(1));
                Assert.IsFalse(await reader.ReadAsync());
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(publicPackage, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }

            Assert.IsTrue(File.Exists(report));
            Assert.IsFalse(File.Exists(report + ".pending"));
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
        }
        finally
        {
            foreach (var item in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnectionEnvironment);
            if (targetDeployed)
            {
                await DropDatabaseAsync(master, databaseName);
            }
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Frozen metadata test cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
