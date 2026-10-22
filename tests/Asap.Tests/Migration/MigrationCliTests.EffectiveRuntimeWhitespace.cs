using Asap.Migration;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ExportImportAndReconcilePreservePinnedRuntimeWhitespaceAndRawCron()
    {
        const string rawCron = " 0 * * * * ";
        const string expectedStaffUrl = "https://runtime.example.org/staff/";
        const string expectedIconPattern = "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-runtime-whitespace-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationRuntimeWhitespace_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
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
        Directory.CreateDirectory(root);

        try
        {
            foreach (var name in environmentNames)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            var feeff = "\uFEFF";
            var unsafeIconPattern = feeff + "javascript:alert({id})" + feeff;
            Environment.SetEnvironmentVariable("ASAP_JOB_PAGE_SIZE", feeff);
            Environment.SetEnvironmentVariable("ASAP_PENDING_ISBN_CHECKS_PAGE_SIZE", "   ");
            Environment.SetEnvironmentVariable("ASAP_TIMEOUT_PAGE_SIZE", "\u0085");
            Environment.SetEnvironmentVariable("ASAP_JOB_MAX_PER_RUN", "not-a-number");
            Environment.SetEnvironmentVariable(
                "ASAP_STAFF_URL",
                $"{feeff}https://runtime.example.org{feeff}#ignored{feeff}");
            Environment.SetEnvironmentVariable("ASAP_CRON_SCHEDULE", rawCron);
            var package = CreateMinimalPackage(
                Path.Combine(root, "source"),
                $"""
                CREATE TABLE [system_settings]
                (
                    [id] TEXT NOT NULL PRIMARY KEY,
                    [staffUrl] TEXT,
                    [formatIconUrlPattern] TEXT
                );
                INSERT INTO [system_settings] VALUES
                    ('settings0000001', '', '{unsafeIconPattern}');
                """);

            using (var runtime = JsonDocument.Parse(File.ReadAllText(
                       Path.Combine(package, "effective-legacy-runtime-config.json"))))
            {
                var settings = runtime.RootElement.GetProperty("settings");
                var staff = settings.GetProperty("StaffApplicationUrl");
                Assert.AreEqual(expectedStaffUrl, staff.GetProperty("value").GetString());
                Assert.AreEqual("environment_fallback", staff.GetProperty("provenance").GetString());
                Assert.AreEqual("ASAP_STAFF_URL", staff.GetProperty("source").GetString());
                var icon = settings.GetProperty("MaterialTypeIconUrlPattern");
                Assert.AreEqual(expectedIconPattern, icon.GetProperty("value").GetString());
                Assert.AreEqual("code_default", icon.GetProperty("provenance").GetString());
                Assert.AreEqual("normalization.defaultFormatIconUrlPattern", icon.GetProperty("source").GetString());
            }

            using (var operational = JsonDocument.Parse(File.ReadAllText(
                       Path.Combine(package, "effective-legacy-operational-config.json"))))
            {
                var schedule = operational.RootElement.GetProperty("schedules").GetProperty("asap-hold-check");
                Assert.AreEqual(rawCron, schedule.GetProperty("value").GetString());
                Assert.AreEqual("environment_override", schedule.GetProperty("provenance").GetString());
                Assert.AreEqual("ASAP_CRON_SCHEDULE", schedule.GetProperty("source").GetString());

                var limits = operational.RootElement.GetProperty("processingLimits");
                var globalPageSize = limits.GetProperty("global").GetProperty("pageSize");
                Assert.AreEqual(50, globalPageSize.GetProperty("value").GetInt32());
                Assert.AreEqual("code_default", globalPageSize.GetProperty("provenance").GetString());
                Assert.AreEqual("ASAP_JOB_PAGE_SIZE", globalPageSize.GetProperty("source").GetString());

                var timeoutPageSize = limits.GetProperty("timeouts").GetProperty("pageSize");
                Assert.AreEqual(50, timeoutPageSize.GetProperty("value").GetInt32());
                Assert.AreEqual("environment_override", timeoutPageSize.GetProperty("provenance").GetString());
                Assert.AreEqual("ASAP_TIMEOUT_PAGE_SIZE", timeoutPageSize.GetProperty("source").GetString());

                var globalMaxPerRun = limits.GetProperty("global").GetProperty("maxPerRun");
                Assert.AreEqual(500, globalMaxPerRun.GetProperty("value").GetInt32());
                Assert.AreEqual("environment_override", globalMaxPerRun.GetProperty("provenance").GetString());
                Assert.AreEqual("ASAP_JOB_MAX_PER_RUN", globalMaxPerRun.GetProperty("source").GetString());

                var obsoletePageSize = limits.GetProperty("obsoletePathOverrides")
                    .GetProperty("pending_isbn_checks")
                    .GetProperty("pageSize");
                Assert.AreEqual(JsonValueKind.Null, obsoletePageSize.ValueKind);
                Assert.IsFalse(limits.GetProperty("configuredQueueOverrides")
                    .TryGetProperty("pending_isbn_checks", out _));
            }

            var externalConfigPath = ExternalConfigurationPath(package);
            var externalConfig = JsonNode.Parse(File.ReadAllText(externalConfigPath))!.AsObject();
            externalConfig["Hangfire"]!["Schedules"]!["WorkflowProcessing"] = rawCron;
            File.WriteAllText(
                externalConfigPath,
                externalConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));

            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(0, MigrationCli.Run(
                    ["validate", "--package", package, "--external-config", externalConfigPath],
                    TextWriter.Null,
                    validationError), validationError.ToString());
            }

            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable("ASAP_STAFF_URL", "https://import-time.example.org");
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var report = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, report, connectionEnvironmentName, tenantId, importError), importError.ToString());
            }

            await using (var connection = new SqlConnection(target))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT [StaffApplicationUrl], [MaterialTypeIconUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(expectedStaffUrl, reader.GetString(0));
                Assert.AreEqual(expectedIconPattern, reader.GetString(1));
                Assert.IsFalse(await reader.ReadAsync());
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
        }
        finally
        {
            foreach (var item in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(item.Key, item.Value);
            }
            Environment.SetEnvironmentVariable(connectionEnvironmentName, null);
            await DropDatabaseAsync(master, databaseName);
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Effective runtime whitespace cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }
}
