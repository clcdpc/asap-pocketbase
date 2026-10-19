using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task TransformationReportPolarisProtectionBitMustMatchSourceAndTargetForReconcileAndRecovery()
    {
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        await VerifyPolarisTransformationReportTruthCaseAsync(
            master,
            tenantId,
            "no-credentials",
            "polaris-report-no-credentials",
            """
            CREATE TABLE [polaris_settings]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
            );
            INSERT INTO [polaris_settings] VALUES
                ('polaris-report-no-credentials', 'https://polaris.example.org', 'access', NULL,
                 'EXAMPLE', 'service-user', NULL, '99', '42', NULL, '');
            """,
            apiKeyProtected: false,
            adminPasswordProtected: false,
            requestingOrganizationSource: null,
            pickupOrganizationSource: string.Empty,
            pendingMutations:
            [
                new("apiKeyProtected", JsonValue.Create(true)),
                new("retiredRequestingOrganizationSource", JsonValue.Create(string.Empty)),
                new("operationContextSource", JsonValue.Create("global_service_library"))
            ],
            recoveredMutations:
            [
                new("adminPasswordProtected", JsonValue.Create(true)),
                new("retiredPickupOrganizationSource", null),
                new("sourceId", JsonValue.Create("polaris-report-other-source"))
            ],
            sourceApiKey: null,
            sourceAdminPassword: null);

        await VerifyPolarisTransformationReportTruthCaseAsync(
            master,
            tenantId,
            "credentials-present",
            "polaris-report-credentials-present",
            """
            CREATE TABLE [polaris_settings]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
            );
            INSERT INTO [polaris_settings] VALUES
                ('polaris-report-credentials-present', 'https://polaris.example.org', 'access', ' source-api-key ',
                 'EXAMPLE', 'service-user', ' source-admin-password ', '99', '42', ' 7 ', '3');
            """,
            apiKeyProtected: true,
            adminPasswordProtected: true,
            requestingOrganizationSource: " 7 ",
            pickupOrganizationSource: "3",
            pendingMutations:
            [
                new("apiKeyProtected", JsonValue.Create(false)),
                new("retiredRequestingOrganizationSource", JsonValue.Create("7"))
            ],
            recoveredMutations:
            [
                new("adminPasswordProtected", JsonValue.Create(false)),
                new("retiredPickupOrganizationSource", JsonValue.Create(" 3 "))
            ],
            sourceApiKey: " source-api-key ",
            sourceAdminPassword: " source-admin-password ");

        await VerifyPolarisTransformationReportTruthCaseAsync(
            master,
            tenantId,
            "api-key-only",
            "polaris-report-api-key-only",
            """
            CREATE TABLE [polaris_settings]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
            );
            INSERT INTO [polaris_settings] VALUES
                ('polaris-report-api-key-only', 'https://polaris.example.org', 'access', ' api-only-secret ',
                 'EXAMPLE', 'service-user', NULL, '99', '42', NULL, NULL);
            """,
            apiKeyProtected: true,
            adminPasswordProtected: false,
            requestingOrganizationSource: null,
            pickupOrganizationSource: null,
            pendingMutations: [new("adminPasswordProtected", JsonValue.Create(true))],
            recoveredMutations: [new("apiKeyProtected", JsonValue.Create(false))],
            sourceApiKey: " api-only-secret ",
            sourceAdminPassword: null);

        await VerifyPolarisTransformationReportTruthCaseAsync(
            master,
            tenantId,
            "admin-password-only",
            "polaris-report-admin-password-only",
            """
            CREATE TABLE [polaris_settings]
            (
                [id] TEXT NOT NULL PRIMARY KEY, [host] TEXT, [accessId] TEXT,
                [apiKey] TEXT, [staffDomain] TEXT, [adminUser] TEXT, [adminPassword] TEXT,
                [workstationId] TEXT, [userId] TEXT, [requestingOrgId] TEXT, [pickupOrgId] TEXT
            );
            INSERT INTO [polaris_settings] VALUES
                ('polaris-report-admin-password-only', 'https://polaris.example.org', 'access', NULL,
                 'EXAMPLE', 'service-user', ' admin-only-secret ', '99', '42', NULL, NULL);
            """,
            apiKeyProtected: false,
            adminPasswordProtected: true,
            requestingOrganizationSource: null,
            pickupOrganizationSource: null,
            pendingMutations: [new("apiKeyProtected", JsonValue.Create(true))],
            recoveredMutations: [new("adminPasswordProtected", JsonValue.Create(false))],
            sourceApiKey: null,
            sourceAdminPassword: " admin-only-secret ");
    }

    private static async Task VerifyPolarisTransformationReportTruthCaseAsync(
        string master,
        Guid tenantId,
        string caseName,
        string sourceId,
        string sourceSql,
        bool apiKeyProtected,
        bool adminPasswordProtected,
        string? requestingOrganizationSource,
        string? pickupOrganizationSource,
        IReadOnlyList<PolarisReportTruthMutation> pendingMutations,
        IReadOnlyList<PolarisReportTruthMutation> recoveredMutations,
        string? sourceApiKey,
        string? sourceAdminPassword)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-report-truth-{caseName}-{Guid.NewGuid():N}");
        var packageRoot = Path.Combine(root, "package-source");
        var databaseName = $"AsapMigrationReportTruth_{Guid.NewGuid():N}";
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = databaseName }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        X509Certificate2? persistedCertificate = null;
        Directory.CreateDirectory(root);

        async Task AssertProtectedPresenceAsync()
        {
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [ProtectedApiKey], [ProtectedAdminPassword] FROM [asap].[PolarisSettings] WHERE [OrganizationId] = 1;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync(), "The imported System1 Polaris settings row must remain present.");
            Assert.AreEqual(apiKeyProtected, !reader.IsDBNull(0));
            Assert.AreEqual(adminPasswordProtected, !reader.IsDBNull(1));
            Assert.IsFalse(await reader.ReadAsync(), "System1 Polaris settings must remain unique.");
        }

        try
        {
            var package = CreateMinimalPackage(packageRoot, sourceSql);
            var keyPath = Path.Combine(root, "keys");
            Directory.CreateDirectory(keyPath);
            var keyRingSentinel = Path.Combine(keyPath, "sentinel.txt");
            await File.WriteAllTextAsync(keyRingSentinel, "report validation must preserve this key-ring directory");
            var configuration = TestConfigurationFactory.Create();
            configuration.Application.DataProtectionKeysPath = keyPath;
            if (sourceApiKey is not null || sourceAdminPassword is not null)
            {
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
                configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = persistedCertificate.Thumbprint;
            }
            var externalConfigurationPath = ExternalConfigurationPath(package);
            await File.WriteAllTextAsync(
                externalConfigurationPath,
                JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));

            DeployDacpac(master, databaseName);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, target);
            var reportPath = Path.Combine(root, "blocked-report-path");
            Directory.CreateDirectory(reportPath);
            using (var importError = new StringWriter())
            {
                var importExitCode = MigrationCli.Run(
                    [
                        "import", "--package", package,
                        "--connection-string-env", connectionEnvironmentName,
                        "--allowed-tenant-ids", tenantId.ToString(),
                        "--report", reportPath,
                        "--external-config", externalConfigurationPath
                    ],
                    TextWriter.Null,
                    importError);
                Assert.AreEqual(1, importExitCode, importError.ToString());
                StringAssert.Contains(importError.ToString(), "import_committed_report_failed");
            }

            var pendingPath = reportPath + ".pending";
            Assert.IsTrue(File.Exists(pendingPath), "The committed import must leave its authentic prepared report for recovery.");
            Assert.IsTrue(Directory.Exists(reportPath), "The final report path must still be blocked for the prepared-report exercise.");
            var validPending = await File.ReadAllTextAsync(pendingPath);
            AssertPolarisReportBaseline(
                validPending,
                sourceId,
                apiKeyProtected,
                adminPasswordProtected,
                requestingOrganizationSource,
                pickupOrganizationSource);
            using (var pending = JsonDocument.Parse(validPending))
            {
                Assert.AreEqual("committed", pending.RootElement.GetProperty("reportState").GetString());
                Assert.IsTrue(pending.RootElement.GetProperty("reconciliationPassed").GetBoolean());
            }
            if (sourceApiKey is not null)
            {
                Assert.IsFalse(validPending.Contains(sourceApiKey, StringComparison.Ordinal),
                    "The import report must not contain the source API key.");
                var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceApiKey)));
                Assert.IsFalse(validPending.Contains(secretHash, StringComparison.OrdinalIgnoreCase),
                    "The import report must not contain the source API key hash.");
            }
            if (sourceAdminPassword is not null)
            {
                Assert.IsFalse(validPending.Contains(sourceAdminPassword, StringComparison.Ordinal),
                    "The import report must not contain the source admin password.");
                var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceAdminPassword)));
                Assert.IsFalse(validPending.Contains(secretHash, StringComparison.OrdinalIgnoreCase),
                    "The import report must not contain the source admin password hash.");
            }

            var sqlFingerprint = ComputeTargetFingerprintForTest(target);
            await AssertProtectedPresenceAsync();
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);

            async Task RejectPendingMutationAsync(PolarisReportTruthMutation mutation)
            {
                var tamperedPending = ApplyPolarisReportMutation(validPending, mutation);
                await File.WriteAllTextAsync(pendingPath, tamperedPending, new UTF8Encoding(false));
                using (var recoverError = new StringWriter())
                {
                    Assert.AreEqual(
                        1,
                        RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError),
                        $"Prepared recovery accepted a false {mutation.Property} claim: {recoverError}");
                    StringAssert.Contains(recoverError.ToString(), "reconciliation_report_mismatch");
                }
                Assert.AreEqual(tamperedPending, await File.ReadAllTextAsync(pendingPath),
                    $"Rejected recovery must preserve the tampered {mutation.Property} bytes.");
                Assert.IsFalse(File.Exists(reportPath), "Rejected recovery must not promote a false transformation report.");
                Assert.IsTrue(Directory.Exists(reportPath), "Rejected recovery must not replace the blocked final path.");
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected recovery must not change SQL for {mutation.Property}.");
                await AssertProtectedPresenceAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected recovery must not change the key ring for {mutation.Property}.");

                await File.WriteAllTextAsync(pendingPath, validPending, new UTF8Encoding(false));
                Assert.AreEqual(validPending, await File.ReadAllTextAsync(pendingPath),
                    $"Restoring {mutation.Property} must restore the exact original pending bytes.");
            }

            foreach (var mutation in pendingMutations)
            {
                await RejectPendingMutationAsync(mutation);
            }

            Directory.Delete(reportPath);
            using (var recoverError = new StringWriter())
            {
                Assert.AreEqual(0, RunRecoverReport(package, reportPath, connectionEnvironmentName, recoverError), recoverError.ToString());
            }
            Assert.IsTrue(File.Exists(reportPath));
            Assert.IsFalse(File.Exists(pendingPath));
            var validRecoveredReport = await File.ReadAllTextAsync(reportPath);
            AssertPolarisReportBaseline(
                validRecoveredReport,
                sourceId,
                apiKeyProtected,
                adminPasswordProtected,
                requestingOrganizationSource,
                pickupOrganizationSource);
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertProtectedPresenceAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));

            async Task RejectRecoveredMutationAsync(PolarisReportTruthMutation mutation)
            {
                var tamperedReport = ApplyPolarisReportMutation(validRecoveredReport, mutation);
                await File.WriteAllTextAsync(reportPath, tamperedReport, new UTF8Encoding(false));
                using (var reconcileError = new StringWriter())
                {
                    Assert.AreEqual(
                        1,
                        RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError),
                        $"Reconcile accepted a false {mutation.Property} claim: {reconcileError}");
                    StringAssert.Contains(reconcileError.ToString(), "reconciliation_report_mismatch");
                }
                Assert.AreEqual(tamperedReport, await File.ReadAllTextAsync(reportPath),
                    $"Rejected reconciliation must preserve the tampered {mutation.Property} bytes.");
                Assert.IsFalse(File.Exists(pendingPath), "Rejected reconciliation must not create or consume a pending report.");
                Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target),
                    $"Rejected reconciliation must not change SQL for {mutation.Property}.");
                await AssertProtectedPresenceAsync();
                Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath),
                    $"Rejected reconciliation must not change the key ring for {mutation.Property}.");

                await File.WriteAllTextAsync(reportPath, validRecoveredReport, new UTF8Encoding(false));
                Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath),
                    $"Restoring {mutation.Property} must restore the exact original report bytes.");
            }

            foreach (var mutation in recoveredMutations)
            {
                await RejectRecoveredMutationAsync(mutation);
            }

            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, reportPath, connectionEnvironmentName, reconcileError), reconcileError.ToString());
            }
            Assert.AreEqual(validRecoveredReport, await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(sqlFingerprint, ComputeTargetFingerprintForTest(target));
            await AssertProtectedPresenceAsync();
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
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
            await DropDatabaseAsync(master, databaseName);
            var tempRootPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var cleanupRoot = Path.GetFullPath(root);
            if (!cleanupRoot.StartsWith(tempRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Report-truth test cleanup escaped its generated temporary directory.");
            }
            if (Directory.Exists(cleanupRoot))
            {
                Directory.Delete(cleanupRoot, recursive: true);
            }
        }
    }

    private static void AssertPolarisReportBaseline(
        string reportJson,
        string sourceId,
        bool apiKeyProtected,
        bool adminPasswordProtected,
        string? requestingOrganizationSource,
        string? pickupOrganizationSource)
    {
        using var report = JsonDocument.Parse(reportJson);
        var polaris = report.RootElement.GetProperty("transformations").EnumerateArray()
            .Single(item => item.GetProperty("entity").GetString() == "polaris_settings");
        Assert.AreEqual(sourceId, polaris.GetProperty("sourceId").GetString());
        Assert.AreEqual(apiKeyProtected, polaris.GetProperty("apiKeyProtected").GetBoolean());
        Assert.AreEqual(adminPasswordProtected, polaris.GetProperty("adminPasswordProtected").GetBoolean());
        Assert.AreEqual(requestingOrganizationSource, ReadNullableReportString(polaris, "retiredRequestingOrganizationSource"));
        Assert.AreEqual(pickupOrganizationSource, ReadNullableReportString(polaris, "retiredPickupOrganizationSource"));
        Assert.AreEqual(
            "owning_request_or_effective_servicing_library",
            polaris.GetProperty("operationContextSource").GetString());
    }

    private static string? ReadNullableReportString(JsonElement item, string propertyName)
    {
        var value = item.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static string ApplyPolarisReportMutation(string reportJson, PolarisReportTruthMutation mutation)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var polaris = report["transformations"]!.AsArray()
            .Single(item => item!["entity"]!.GetValue<string>() == "polaris_settings")!.AsObject();
        polaris[mutation.Property] = mutation.Replacement?.DeepClone();
        return report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record PolarisReportTruthMutation(string Property, JsonNode? Replacement);

    private static string SnapshotReportTruthKeyRing(string keyPath) => string.Join(
        Environment.NewLine,
        Directory.EnumerateFiles(keyPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(keyPath, path), StringComparer.Ordinal)
            .Select(path =>
            {
                var information = new FileInfo(path);
                var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return $"{Path.GetRelativePath(keyPath, path)}|{information.Length}|{information.LastWriteTimeUtc.Ticks}|{digest}";
            }));
}
