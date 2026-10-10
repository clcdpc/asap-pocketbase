using System.Text.Json;
using Asap.Migration;
using Asap.Tests;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task LibraryScopedMaterialFormatRejectsBranchOwnerAndPreservesBranchRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-branch-library-{Guid.NewGuid():N}");
        var validDatabase = $"AsapMigrationLibraryOwner_{Guid.NewGuid():N}";
        var invalidDatabase = $"AsapMigrationBranchOwner_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True")
        {
            InitialCatalog = "master"
        }.ConnectionString;
        var validTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = validDatabase }.ConnectionString;
        var invalidTarget = new SqlConnectionStringBuilder(master) { InitialCatalog = invalidDatabase }.ConnectionString;
        var connectionEnvironmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        var previousConnection = Environment.GetEnvironmentVariable(connectionEnvironmentName);
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Directory.CreateDirectory(root);
        try
        {
            var validRoot = Path.Combine(root, "valid-library-owner");
            Directory.CreateDirectory(validRoot);
            var validPackage = CreateMinimalPackage(
                validRoot,
                """
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
                    ('system-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('library-owned-format', 'library', 'pb-org-2', 'ownership-proof', 'Ownership Proof', 1, 20);
                """);
            AssertBranchSourceIdentity(validPackage);

            using (var validationError = new StringWriter())
            {
                Assert.AreEqual(0, MigrationCli.Run(
                    ["validate", "--package", validPackage, "--external-config", ExternalConfigurationPath(validPackage)],
                    TextWriter.Null,
                    validationError), validationError.ToString());
            }

            DeployDacpac(master, validDatabase);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, validTarget);
            var validReport = Path.Combine(validRoot, "report.json");
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(validPackage, validReport, connectionEnvironmentName, tenantId, importError),
                    importError.ToString());
            }

            await using (var connection = new SqlConnection(validTarget))
            {
                await connection.OpenAsync();
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 2 AND [OrganizationCodeId] = 2 AND [IsActive] = 1;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[Organization] WHERE [Id] = 20 AND [OrganizationCodeId] = 3 AND [ParentOrganizationId] = 2 AND [IsActive] = 0;"));
                Assert.AreEqual(1, await ScalarAsync(connection,
                    "SELECT COUNT(*) FROM [asap].[MaterialFormat] f JOIN [asap].[LegacyPocketBaseMapping] mapping ON mapping.[EntityType] = N'material_format' AND mapping.[PocketBaseId] = N'library-owned-format' AND mapping.[NewId] = f.[Id] WHERE f.[OwnerOrganizationId] = 2 AND f.[Code] = N'ownership-proof';"));
            }
            using (var reconcileError = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(validPackage, validReport, connectionEnvironmentName, reconcileError),
                    reconcileError.ToString());
            }

            var invalidRoot = Path.Combine(root, "invalid-branch-owner");
            Directory.CreateDirectory(invalidRoot);
            var invalidPackage = CreateMinimalPackage(
                invalidRoot,
                """
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
                    ('system-book', 'system', NULL, 'book', 'Book', 1, 10),
                    ('branch-owned-format', 'library', 'pb-org-20', 'branch-proof', 'Branch Proof', 1, 20);
                """);
            AssertBranchSourceIdentity(invalidPackage);

            var keyPath = Path.Combine(invalidRoot, "keys");
            Directory.CreateDirectory(keyPath);
            await File.WriteAllTextAsync(Path.Combine(keyPath, "sentinel.txt"), "preserve the existing key ring");
            var externalConfiguration = TestConfigurationFactory.Create();
            externalConfiguration.Application.DataProtectionKeysPath = keyPath;
            await File.WriteAllTextAsync(
                ExternalConfigurationPath(invalidPackage),
                JsonSerializer.Serialize(externalConfiguration, new JsonSerializerOptions { WriteIndented = true }));
            var keyRingBefore = SnapshotReportTruthKeyRing(keyPath);
            var invalidReport = Path.Combine(invalidRoot, "report.json");

            DeployDacpac(master, invalidDatabase);
            Environment.SetEnvironmentVariable(connectionEnvironmentName, invalidTarget);
            var fingerprintBefore = ComputeTargetFingerprintForTest(invalidTarget);
            using (var importError = new StringWriter())
            {
                Assert.AreEqual(1, RunImport(invalidPackage, invalidReport, connectionEnvironmentName, tenantId, importError));
                StringAssert.Contains(importError.ToString(), "organization_reference_invalid");
            }
            Assert.AreEqual(fingerprintBefore, ComputeTargetFingerprintForTest(invalidTarget));
            Assert.AreEqual(keyRingBefore, SnapshotReportTruthKeyRing(keyPath));
            Assert.IsFalse(File.Exists(invalidReport));
            Assert.IsFalse(File.Exists(invalidReport + ".pending"));
            await AssertFreshImportTargetAsync(invalidTarget);
        }
        finally
        {
            Environment.SetEnvironmentVariable(connectionEnvironmentName, previousConnection);
            await DropDatabaseAsync(master, validDatabase);
            await DropDatabaseAsync(master, invalidDatabase);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void AssertBranchSourceIdentity(string package)
    {
        using var organizations = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "organizations.json")));
        var branch = organizations.RootElement
            .GetProperty("collections")
            .GetProperty("polaris_organizations")
            .EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == "pb-org-20");
        Assert.AreEqual("20", branch.GetProperty("organizationId").GetString());
        Assert.AreEqual(3, branch.GetProperty("organizationCodeId").GetInt32());
        Assert.AreEqual(2, branch.GetProperty("parentOrganizationId").GetInt32());
    }
}
