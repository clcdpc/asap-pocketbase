using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Asap.Web.Infrastructure.Health;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Sql;

[TestClass]
public sealed class DatabaseBaselineTests
{
    private static string _databaseName = null!;
    private static string _masterConnectionString = null!;
    private static string _databaseConnectionString = null!;
    private static string _dacpacPath = null!;
    private static bool _databaseWasMissingBeforeDeployment;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _masterConnectionString =
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True";

        var masterBuilder = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = "master"
        };
        _masterConnectionString = masterBuilder.ConnectionString;
        _databaseName = $"AsapSlice0Tests_{Guid.NewGuid():N}";
        _databaseConnectionString = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = _databaseName
        }.ConnectionString;
        _dacpacPath = FindDacpac();

        using (var connection = new SqlConnection(_masterConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_ID(@databaseName);";
            command.Parameters.AddWithValue("@databaseName", _databaseName);
            _databaseWasMissingBeforeDeployment = command.ExecuteScalar() is DBNull;
        }

        context.WriteLine($"Deploying {_dacpacPath} to {_databaseName}");
        new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath);
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (string.IsNullOrEmpty(_databaseName) ||
            !_databaseName.StartsWith("AsapSlice0Tests_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database outside the Slice 0 test namespace.");
        }

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        var quoted = new SqlCommandBuilder().QuoteIdentifier(_databaseName);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};";
        await command.ExecuteNonQueryAsync();
    }

    [TestMethod]
    public async Task DacpacDeploysSql2022CompatibilityAndSingletonRows()
    {
        Assert.IsTrue(_databaseWasMissingBeforeDeployment);

        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();

        Assert.AreEqual(16, Convert.ToInt32(await Scalar(connection, "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);")));
        Assert.AreEqual(160, Convert.ToInt32(await Scalar(connection, "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();")));
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, "SELECT COUNT(*) FROM [asap].[DeploymentState] WHERE [Id] = 1;")));
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_dacpacPath))).ToLowerInvariant();
        Assert.AreEqual(
            expectedHash,
            Convert.ToString(await Scalar(connection, "SELECT [LastDacpacSha256] FROM [asap].[DeploymentState] WHERE [Id] = 1;")));
    }

    [TestMethod]
    public async Task RepeatedServiceDeploymentsRetainExistingDataAndUnownedSchema()
    {
        const string marker = "slice-00-repeat-deploy-marker";
        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(
                connection,
                """
                CREATE TABLE [asap].[Slice0ServicePathProbe]
                (
                    [Id] int NOT NULL PRIMARY KEY,
                    [Value] nvarchar(50) NOT NULL
                );
                INSERT INTO [asap].[Slice0ServicePathProbe] VALUES (1, N'retain me');
                """);
            await using var markerCommand = connection.CreateCommand();
            markerCommand.CommandText = """
                UPDATE [asap].[DeploymentState]
                SET [LastReleaseVersion] = @marker
                WHERE [Id] = 1;
                """;
            markerCommand.Parameters.AddWithValue("@marker", marker);
            await markerCommand.ExecuteNonQueryAsync();
        }

        try
        {
            var deployment = new DacpacDeploymentService();
            deployment.Deploy(_databaseConnectionString, _dacpacPath);
            deployment.Deploy(_databaseConnectionString, _dacpacPath);

            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            Assert.AreEqual(
                marker,
                Convert.ToString(await Scalar(
                    connection,
                    "SELECT [LastReleaseVersion] FROM [asap].[DeploymentState] WHERE [Id] = 1;")));
            Assert.AreEqual(
                "retain me",
                Convert.ToString(await Scalar(
                    connection,
                    "SELECT [Value] FROM [asap].[Slice0ServicePathProbe] WHERE [Id] = 1;")));
        }
        finally
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await NonQuery(
                connection,
                """
                DROP TABLE IF EXISTS [asap].[Slice0ServicePathProbe];
                UPDATE [asap].[DeploymentState]
                SET [LastReleaseVersion] = NULL
                WHERE [Id] = 1;
                """);
        }
    }

    [TestMethod]
    public async Task SingletonConstraintsRejectSecondRows()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();

        await Assert.ThrowsAsync<SqlException>(async () =>
            await NonQuery(
                connection,
                "INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc]) VALUES (2, 1, SYSUTCDATETIME());"));
        await Assert.ThrowsAsync<SqlException>(async () =>
            await NonQuery(connection, "INSERT INTO [asap].[DeploymentState] ([Id]) VALUES (2);"));

        Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, "SELECT COUNT(*) FROM [asap].[SchemaVersion];")));
        Assert.AreEqual(1, Convert.ToInt32(await Scalar(connection, "SELECT COUNT(*) FROM [asap].[DeploymentState];")));
    }

    [TestMethod]
    public async Task RuntimeRoleHasDmlButNoSchemaControlGrant()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT
                SUM(CASE WHEN permission_name IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE') AND state IN ('G', 'W') THEN 1 ELSE 0 END),
                SUM(CASE WHEN permission_name IN ('ALTER', 'CONTROL') AND state IN ('G', 'W') THEN 1 ELSE 0 END)
            FROM sys.database_permissions
            WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID('asap_runtime')
              AND class_desc = 'SCHEMA';
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(4, reader.GetInt32(0));
        Assert.AreEqual(0, reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }

    [TestMethod]
    public async Task ReadinessRequiresExactSchemaVersion()
    {
        var services = new ServiceCollection()
            .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(_databaseConnectionString))
            .BuildServiceProvider();
        var readiness = new ReadinessService(
            new ConfigurationLoadResult(TestConfigurationFactory.Create(), "test.json", []),
            new RuntimeInitializationState(),
            services,
            NullLogger<ReadinessService>.Instance);

        Assert.IsTrue((await readiness.CheckAsync(CancellationToken.None)).IsReady);

        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 2 WHERE [Id] = 1;");
        }

        try
        {
            var result = await readiness.CheckAsync(CancellationToken.None);
            Assert.IsFalse(result.IsReady);
            Assert.AreEqual("schema_version_mismatch", result.ErrorCode);
        }
        finally
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 1 WHERE [Id] = 1;");
        }
    }

    [TestMethod]
    public async Task DataLossDeploymentIsBlockedAndRetainsData()
    {
        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(
                connection,
                "CREATE TABLE [asap].[Slice0DataLossProbe] ([Id] int NOT NULL PRIMARY KEY, [Value] nvarchar(50) NOT NULL); INSERT INTO [asap].[Slice0DataLossProbe] VALUES (1, N'retain me');");
        }

        try
        {
            using var package = DacPackage.Load(_dacpacPath);
            var services = new DacServices(_masterConnectionString);
            var options = new DacDeployOptions
            {
                BlockOnPossibleDataLoss = true,
                CreateNewDatabase = false,
                DropObjectsNotInSource = true
            };

            Assert.Throws<DacServicesException>(() =>
                services.Deploy(package, _databaseName, upgradeExisting: true, options));

            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            Assert.AreEqual(
                "retain me",
                Convert.ToString(await Scalar(connection, "SELECT [Value] FROM [asap].[Slice0DataLossProbe] WHERE [Id] = 1;")));
        }
        finally
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await NonQuery(connection, "DROP TABLE IF EXISTS [asap].[Slice0DataLossProbe];");
        }
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task NonQuery(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindDacpac()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asap.sln")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new FileNotFoundException("Could not locate Asap.sln from the test output directory.");
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = Path.Combine(
            root.FullName,
            "database",
            "Asap.Database",
            "bin",
            configuration,
            "Asap.Database.dacpac");

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Build the DACPAC before running SQL tests.", path);
    }
}
