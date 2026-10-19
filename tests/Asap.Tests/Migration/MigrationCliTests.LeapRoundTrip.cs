using Asap.Migration;
using Microsoft.Data.SqlClient;

namespace Asap.Tests.Migration;

public sealed partial class MigrationCliTests
{
    [TestMethod]
    [DataRow("https://leap.example.org/#patron/{{patron-id}}/{{patron-id}}", "https://leap.example.org/#bib/{{bibid}}")]
    [DataRow("https://leap.example.org/#patron/{{patronId}}", "https://leap.example.org/#bib/{{bibId}}/{{bibId}}")]
    [DataRow("javascript:alert({{patronId}})", "file:///legacy/{{bibid}}")]
    [DoNotParallelize]
    public async Task LeapSourcePatternsRoundTripWithAliasesAndInertRawValues(string patronPattern, string bibPattern)
    {
        var root = Path.Combine(Path.GetTempPath(), $"asap-migration-leap-roundtrip-{Guid.NewGuid():N}");
        var database = $"AsapLeapRoundTrip_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True") { InitialCatalog = "master" }.ConnectionString;
        var target = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
        var environmentName = $"ASAP_MIGRATION_TEST_{Guid.NewGuid():N}";
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        Directory.CreateDirectory(root);
        try
        {
            var package = CreateMinimalPackage(root, $$"""
                CREATE TABLE [system_settings] ([id] TEXT NOT NULL PRIMARY KEY,[leapBibUrlPattern] TEXT,[leapPatronUrlPattern] TEXT);
                INSERT INTO [system_settings] VALUES ('settings-1',{{Literal(bibPattern)}},{{Literal(patronPattern)}});
                """);
            var report = Path.Combine(root, "report.json");
            DeployDacpac(master, database);
            Environment.SetEnvironmentVariable(environmentName, target);
            using (var validation = new StringWriter())
            {
                Assert.AreEqual(0, MigrationCli.Run(["validate", "--package", package], TextWriter.Null, validation), validation.ToString());
            }
            using (var import = new StringWriter())
            {
                Assert.AreEqual(0, RunImport(package, report, environmentName,
                    Guid.Parse("00000000-0000-0000-0000-000000000002"), import), import.ToString());
            }
            await using var connection = new SqlConnection(target);
            await connection.OpenAsync();
            await using (var read = new SqlCommand("SELECT [LeapBibUrlPattern],[LeapPatronUrlPattern] FROM [asap].[SystemSettings] WHERE [OrganizationId]=1;", connection))
            await using (var values = await read.ExecuteReaderAsync())
            {
                Assert.IsTrue(await values.ReadAsync());
                Assert.AreEqual(bibPattern, values.GetString(0));
                Assert.AreEqual(patronPattern, values.GetString(1));
                Assert.IsFalse(await values.ReadAsync());
            }
            using (var reconcile = new StringWriter())
            {
                Assert.AreEqual(0, RunReconcile(package, report, environmentName, reconcile), reconcile.ToString());
            }
            // Recomputing a report fingerprint cannot bless a changed source-owned pattern.
            await AssertFingerprintRefreshedSourceOwnedDriftRejectedAsync(connection, target, package, report, environmentName,
                "LEAP source patron pattern", "UPDATE [asap].[SystemSettings] SET [LeapPatronUrlPattern]=N'Drifted source' WHERE [OrganizationId]=1;",
                "SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [LeapPatronUrlPattern]=N'Drifted source';", 1,
                $"UPDATE [asap].[SystemSettings] SET [LeapPatronUrlPattern]=N{Literal(patronPattern)} WHERE [OrganizationId]=1;",
                $"SELECT COUNT(*) FROM [asap].[SystemSettings] WHERE [LeapPatronUrlPattern]=N{Literal(patronPattern)};", 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
            await DropDatabaseAsync(master, database);
            Directory.Delete(root, recursive: true);
        }
    }
}
