using Asap.Migration;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task ReconcileAndRecoverRejectForgedBootstrapWhenSourceAlreadyHasUsableAdministrator()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-bootstrap-necessity-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationBootstrapNecessity_{Guid.NewGuid():N}";
        var master = MigrationAuditMasterConnection();
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(Path.Combine(root, "source"),
                """
                INSERT INTO [staff_users] VALUES
                    ('ordinary-staff', 'ordinary@example.org', 'ordinary', 'Ordinary Staff',
                     'staff', 1, '2', 0, NULL, 0, 0, 0);
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());
            }
            var authenticReport = File.ReadAllText(reportPath);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            var staffId = await ReadLongAsync(connection,
                "SELECT [NewId] FROM [asap].[LegacyPocketBaseMapping] WHERE [EntityType] = N'staff_user' AND [PocketBaseId] = N'ordinary-staff';");
            await ExecuteAsync(connection,
                "UPDATE [asap].[StaffUser] SET [Role] = N'super_admin', [OrganizationId] = 1 WHERE [Id] = " + staffId + ";");
            var report = JsonNode.Parse(authenticReport)!.AsObject();
            report["transformations"]!.AsArray().Add(new JsonObject
            {
                ["entity"] = "migration_bootstrap_super_admin",
                ["action"] = "promoted_existing",
                ["targetStaffUserId"] = staffId,
                ["authenticationEmail"] = "ordinary@example.org",
                ["appliedAtUtc"] = report["exportedAtUtc"]!.DeepClone()
            });
            File.WriteAllText(reportPath, report.ToJsonString());
            RefreshReportFingerprint(reportPath, target);
            await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                "bootstrap", recoverPending: false);
            await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                "bootstrap", recoverPending: true);
            await ExecuteAsync(connection,
                "UPDATE [asap].[StaffUser] SET [Role] = N'staff', [OrganizationId] = 2 WHERE [Id] = " + staffId + ";");
            File.WriteAllText(reportPath, authenticReport);
            using var restored = new StringWriter();
            Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, restored), restored.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            DeleteMigrationAuditRoot(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task LegitimateBootstrapRequiresConfiguredIdentityAndPreservesUnaffectedSourceFields(bool inserted)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-bootstrap-config-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationBootstrapConfig_{Guid.NewGuid():N}";
        var master = MigrationAuditMasterConnection();
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(Path.Combine(root, "source"),
                "UPDATE [staff_users] SET [role] = 'admin', [libraryOrgId] = '2', [active] = 0;");
            var configuration = TestConfigurationFactory.Create();
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName =
                inserted ? "inserted@example.org" : "SOURCE-ADMIN@example.org";
            configuration.Authentication.Entra.InitialSuperAdmin.DisplayName = "Configured Display";
            configuration.Authentication.Entra.InitialSuperAdmin.NotificationEmail = "configured-notify@example.org";
            File.WriteAllText(ExternalConfigurationPath(package), JsonSerializer.Serialize(configuration));
            var validConfiguration = File.ReadAllText(ExternalConfigurationPath(package));
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());
            }
            using (var valid = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, valid), valid.ToString());
            }
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            Assert.AreEqual(1, await ScalarAsync(connection, inserted
                ? "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [UserPrincipalName] = N'inserted@example.org' AND [DisplayName] = N'Configured Display' AND [NotificationEmail] = N'configured-notify@example.org' AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"
                : "SELECT COUNT(*) FROM [asap].[StaffUser] WHERE [UserPrincipalName] = N'SOURCE-ADMIN@example.org' AND [DisplayName] = N'Source Administrator' AND [NotificationEmail] = N'source-admin@example.org' AND [WeeklyActionSummaryEmail] = N'weekly@example.org' AND [Role] = N'super_admin' AND [OrganizationId] = 1 AND [IsActive] = 1;"));
            configuration.Authentication.Entra.InitialSuperAdmin.UserPrincipalName = "unauthorized@example.org";
            File.WriteAllText(ExternalConfigurationPath(package), JsonSerializer.Serialize(configuration));
            await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                "bootstrap", recoverPending: false);
            File.WriteAllText(ExternalConfigurationPath(package), validConfiguration);
            var originalReport = File.ReadAllText(reportPath);
            await ExecuteAsync(connection,
                "UPDATE [asap].[StaffUser] SET [DisplayName] = N'Forged display' WHERE [Role] = N'super_admin';");
            RefreshReportFingerprint(reportPath, target);
            await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                "staff", recoverPending: false);
            File.WriteAllText(reportPath, originalReport);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            DeleteMigrationAuditRoot(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task ReconcileAndImmediateReportRecoveryRejectExcludedPickupAndAdministrativeAuditPopulations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-excluded-populations-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationExcludedPopulations_{Guid.NewGuid():N}";
        var master = MigrationAuditMasterConnection();
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(Path.Combine(root, "source"));
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());
            }
            var originalReport = File.ReadAllText(reportPath);
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            var vectors = new[]
            {
                (Table: "PickupPreferenceOperation", Insert:
                    """
                    INSERT INTO [asap].[PickupPreferenceOperation]
                        ([Id], [Barcode], [PatronId], [LibraryOrganizationId], [Origin],
                         [ToPickupBranchId], [ToPickupBranchName], [State], [DispatchStartedUtc], [ConfirmedByRead])
                    VALUES ('11111111-2222-3333-4444-555555555555', N'owned-barcode', 123, 2,
                            N'patron_suggestion', 20, N'Test Branch', 1, '2030-01-02T04:00:00', 0);
                    """),
                (Table: "AdministrativeAudit", Insert:
                    """
                    INSERT INTO [asap].[AdministrativeAudit] ([OrganizationId], [Action], [DetailsJson], [CreatedUtc])
                    VALUES (2, N'invented_import_history', N'{"invented":true}', '2030-01-02T04:00:00');
                    """)
            };
            foreach (var vector in vectors)
            {
                await ExecuteAsync(connection, vector.Insert);
                RefreshReportFingerprint(reportPath, target);
                await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                    vector.Table, recoverPending: false);
                await AssertMigrationAuditRejectedReadOnlyAsync(package, reportPath, environmentName, target,
                    vector.Table, recoverPending: true);
                await ExecuteAsync(connection, $"DELETE FROM [asap].[{vector.Table}];");
                File.WriteAllText(reportPath, originalReport);
            }
            using var restored = new StringWriter();
            Assert.AreEqual(0, RunRecoverReport(package, reportPath, environmentName, restored), restored.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            DeleteMigrationAuditRoot(root);
        }
    }

    [TestMethod]
    [DataRow("0", "0", "5,30,14,14,14", "5,30,14,14,14")]
    [DataRow("9", "0", "9,9,9,9,9", "5,30,14,14,14")]
    [DataRow("9", "NULL", "9,9,9,9,9", "null,null,null,null,null")]
    [DataRow("NULL", "NULL", "5,30,14,14,14", "null,null,null,null,null")]
    [DataRow("9", "7", "9,9,9,9,9", "7,7,7,7,7")]
    [DataRow("missing", "missing", "5,30,14,14,14", "null,null,null,null,null")]
    [DoNotParallelize]
    public async Task WorkflowZeroValuesPreservePinnedDefaultsAndExplicitLibraryOverrides(
        string systemValue, string libraryValue, string expectedSystem, string expectedLibrary)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-workflow-zero-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationWorkflowZero_{Guid.NewGuid():N}";
        var master = MigrationAuditMasterConnection();
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var additionalSql = systemValue == "missing"
                ? """
                  CREATE TABLE [workflow_settings] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT);
                  INSERT INTO [workflow_settings] VALUES ('workflow-system', 'system', NULL), ('workflow-library', 'library', 'pb-org-2');
                  """
                : $$"""
                  CREATE TABLE [workflow_settings]
                      ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT, [libraryOrganization] TEXT,
                       [suggestionLimit] INTEGER, [outstandingTimeoutDays] INTEGER, [holdPickupTimeoutDays] INTEGER,
                       [pendingHoldTimeoutDays] INTEGER, [additionalCopyTimeoutDays] INTEGER);
                  INSERT INTO [workflow_settings] VALUES
                      ('workflow-system', 'system', NULL, {{systemValue}}, {{systemValue}}, {{systemValue}}, {{systemValue}}, {{systemValue}}),
                      ('workflow-library', 'library', 'pb-org-2', {{libraryValue}}, {{libraryValue}}, {{libraryValue}}, {{libraryValue}}, {{libraryValue}});
                  """;
            var package = CreateMinimalPackage(Path.Combine(root, "source"), additionalSql);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var reportPath = Path.Combine(root, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, reportPath, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), importError), importError.ToString());
            }
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            foreach (var expected in new[] { (Organization: 1, Values: expectedSystem), (Organization: 2, Values: expectedLibrary) })
            {
                await using var command = new SqlCommand(
                    "SELECT [SuggestionLimit], [OutstandingTimeoutDays], [HoldPickupTimeoutDays], [PendingHoldTimeoutDays], [AdditionalCopyTimeoutDays] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @id;", connection);
                command.Parameters.AddWithValue("@id", expected.Organization);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(expected.Values, string.Join(',', Enumerable.Range(0, 5)
                    .Select(index => reader.IsDBNull(index) ? "null" : reader.GetInt32(index).ToString())),
                    "Expected values are literal pinned workflows.js defaults, independently stated by the fixture.");
            }
            using (var services = new ServiceCollection()
                       .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(target)).BuildServiceProvider())
            {
                var configuration = new PatronConfigurationService(services.GetRequiredService<IDbContextFactory<AsapDbContext>>());
                var library = await configuration.GetAsync(2, CancellationToken.None);
                Assert.IsNotNull(library);
                var expectedLimit = expectedLibrary.Split(',')[0];
                Assert.AreEqual(int.Parse(expectedLimit == "null" ? expectedSystem.Split(',')[0] : expectedLimit), library.SuggestionLimit);
            }
            using var reconcile = new StringWriter();
            Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, reconcile), reconcile.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            DeleteMigrationAuditRoot(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task EncodedPublicationOptionsRejectRecursiveDuplicatePropertiesBeforeSqlOrReportWrites(bool library)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-publication-duplicates-{Guid.NewGuid():N}");
        var databaseName = $"AsapMigrationPublicationDuplicates_{Guid.NewGuid():N}";
        var master = MigrationAuditMasterConnection();
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(Path.Combine(root, "source"),
                library ? """
                CREATE TABLE [patron_settings_overrides] ([id] TEXT NOT NULL PRIMARY KEY, [orgId] TEXT, [publicationOptions] TEXT);
                INSERT INTO [patron_settings_overrides] VALUES ('patron-library', '2', '["Published"]');
                """ : """
                CREATE TABLE [ui_settings] ([id] TEXT NOT NULL PRIMARY KEY, [scope] TEXT, [publicationOptions] TEXT);
                INSERT INTO [ui_settings] VALUES ('ui-system', 'system', '["Published"]');
                """);
            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(environmentName, target);
            var fingerprint = ComputeTargetFingerprintForTest(target);
            var reportPath = Path.Combine(root, "report.json");
            var vectors = new[]
            {
                """[{"id":"year","label":"Before","label":"After"}]""",
                """[{"id":"year","label":"Before","\u006cabel":"After"}]""",
                """[{"id":"year","label":"Before","Label":"After"}]""",
                """[{"id":"before","id":"after","label":"Published"}]""",
                """[{"id":"year","label":"Published","enabled":false,"enabled":true}]""",
                """[{"id":"year","label":"Published","sortOrder":20,"sortOrder":10}]""",
                """[{"id":"year","label":"Published","metadata":{"name":"Before","NAME":"After"}}]"""
            };
            foreach (var vector in vectors)
            {
                SetAuditPublicationOptions(package, vector, library);
                using (var validation = new StringWriter())
                {
                    Assert.AreEqual(1, MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validation), vector);
                    StringAssert.Contains(validation.ToString(), "Duplicate JSON property");
                }
                using (var import = new StringWriter())
                {
                    Assert.AreEqual(1, RunImport(package, reportPath, environmentName,
                        Guid.Parse("00000000-0000-0000-0000-000000000002"), import), vector);
                    StringAssert.Contains(import.ToString(), "Duplicate JSON property");
                }
                Assert.AreEqual(fingerprint, ComputeTargetFingerprintForTest(target));
                Assert.IsFalse(File.Exists(reportPath));
                Assert.IsFalse(File.Exists(reportPath + ".pending"));
                foreach (var parserOwner in new[]
                {
                    (Type: "MigrationConfigurationImporter", Method: "ParsePublicationOptions"),
                    (Type: "MigrationIndependentConfigurationVerifier", Method: "ParsePublicationOptions"),
                    (Type: "MigrationReconciler", Method: "AuditPublicationOptionCount")
                })
                {
                    var parser = typeof(MigrationCli).Assembly.GetType("Asap.Migration." + parserOwner.Type)!
                        .GetMethod(parserOwner.Method, BindingFlags.NonPublic | BindingFlags.Static)!;
                    var thrown = Assert.Throws<TargetInvocationException>(() => parser.Invoke(null, [vector, true]));
                    Assert.IsInstanceOfType<MigrationOperationException>(thrown.InnerException);
                }
            }
            foreach (var control in new[] { "[\"Published\",\"Coming soon\"]", "Published\nComing soon", "{\"label\":\"Before\",\"label\":\"After\"}" })
            {
                SetAuditPublicationOptions(package, control, library);
                using var validation = new StringWriter();
                Assert.AreEqual(0, MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validation), validation.ToString());
            }
            SetAuditPublicationOptions(package, "[\"Published\",\"Coming soon\"]", library);
            using var validImport = new StringWriter();
            Assert.AreEqual(0, RunImport(package, reportPath, environmentName,
                Guid.Parse("00000000-0000-0000-0000-000000000002"), validImport), validImport.ToString());
            using var reconcile = new StringWriter();
            Assert.AreEqual(0, RunReconcile(package, reportPath, environmentName, reconcile), reconcile.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, databaseName);
            DeleteMigrationAuditRoot(root);
        }
    }

    private static string MigrationAuditMasterConnection() => new SqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
        "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True") { InitialCatalog = "master" }.ConnectionString;

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void DeleteMigrationAuditRoot(string root)
    {
        var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Migration audit cleanup escaped its generated temporary directory.");
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task AssertMigrationAuditRejectedReadOnlyAsync(
        string package, string reportPath, string environmentName, string target, string subject, bool recoverPending)
    {
        var original = File.ReadAllText(reportPath);
        var path = reportPath;
        if (recoverPending)
        {
            var pending = JsonNode.Parse(original)!.AsObject();
            pending["reportState"] = "commit_pending";
            pending["reconciliationPassed"] = false;
            path += ".pending";
            File.WriteAllText(path, pending.ToJsonString());
            File.Delete(reportPath);
        }
        var fingerprint = ComputeTargetFingerprintForTest(target);
        var reportBefore = File.ReadAllText(path);
        using var error = new StringWriter();
        Assert.AreEqual(1, recoverPending
            ? RunRecoverReport(package, reportPath, environmentName, error)
            : RunReconcile(package, reportPath, environmentName, error), subject + ": " + error);
        StringAssert.Contains(error.ToString().ToLowerInvariant(), subject.ToLowerInvariant());
        Assert.AreEqual(fingerprint, ComputeTargetFingerprintForTest(target), "Rejection must not repair SQL state.");
        Assert.AreEqual(reportBefore, File.ReadAllText(path), "Rejection must not change or promote a report.");
        if (recoverPending)
        {
            Assert.IsFalse(File.Exists(reportPath));
            File.Delete(path);
            File.WriteAllText(reportPath, original);
        }
        return Task.CompletedTask;
    }

    private static void SetAuditPublicationOptions(string package, string raw, bool library)
    {
        var path = Path.Combine(package, "patron-settings.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["collections"]![library ? "patron_settings_overrides" : "ui_settings"]!.AsArray()[0]!["publicationOptions"] = raw;
        File.WriteAllText(path, document.ToJsonString());
        UpdateManifestEntry(package, "patron-settings.json");
    }
}
