using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Development;
using Asap.Web.Infrastructure.Health;
using Asap.Web.Features.Staff;
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
        _databaseName = $"AsapSlice1Tests_{Guid.NewGuid():N}";
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
            !_databaseName.StartsWith("AsapSlice1Tests_", StringComparison.Ordinal))
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
        Assert.AreEqual(6, Convert.ToInt32(await Scalar(connection, "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
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
        const string marker = "slice-01-repeat-deploy-marker";
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
                "INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc]) VALUES (2, 3, SYSUTCDATETIME());"));
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
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 4 WHERE [Id] = 1;");
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
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 6 WHERE [Id] = 1;");
        }
    }

    [TestMethod]
    public async Task SchemaSixRejectsInPlaceDeploymentOverAnOlderApplicationDatabase()
    {
        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 5 WHERE [Id] = 1;");
        }

        try
        {
            Assert.Throws<DacServicesException>(() =>
                new DacpacDeploymentService().Deploy(_databaseConnectionString, _dacpacPath));

            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            Assert.AreEqual(
                5,
                Convert.ToInt32(await Scalar(
                    connection,
                    "SELECT [Version] FROM [asap].[SchemaVersion] WHERE [Id] = 1;")));
        }
        finally
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await NonQuery(connection, "UPDATE [asap].[SchemaVersion] SET [Version] = 6 WHERE [Id] = 1;");
        }
    }

    [TestMethod]
    public async Task AuthorizationSensitiveOutboxRequiresExplicitAddressKind()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await NonQuery(
            connection,
            "INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (2, N'Library', 1);");
        await NonQuery(
            connection,
            """
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [Role], [OrganizationId], [IsActive])
            VALUES
                ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                 N'staff@example.test', N'STAFF@EXAMPLE.TEST', N'staff', 2, 1);
            """);

        await Assert.ThrowsAsync<SqlException>(async () =>
            await NonQuery(
                connection,
                """
                INSERT INTO [asap].[EmailOutbox]
                    ([OrganizationId], [DeliveryClass], [RecipientStaffUserId], [RecipientAuthenticationEmail],
                     [AuthorizationOrganizationId], [RecipientAddressKind],
                     [ToAddress], [FromAddress], [Subject], [BodyHtml], [Status], [CreatedUtc])
                VALUES
                    (2, N'staff_authorization_sensitive',
                     (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'STAFF@EXAMPLE.TEST'),
                     N'STAFF@EXAMPLE.TEST',
                     2, NULL, N'staff@example.test', N'asap@example.test', N'Subject', N'<p>Body</p>',
                     N'pending', SYSUTCDATETIME());
                """));
    }

    [TestMethod]
    public async Task ActiveStaffRequireUniqueNormalizedEmailButNotEntraMetadata()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await NonQuery(
            connection,
            "INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (70001, N'Email identity library', 1);");
        await NonQuery(
            connection,
            """
            INSERT INTO [asap].[StaffUser]
                ([UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive])
            VALUES (N'email-only@example.org', N'EMAIL-ONLY@EXAMPLE.ORG', N'staff', 70001, 1);

            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [Role], [OrganizationId], [IsActive])
            VALUES
                ('71111111-1111-1111-1111-111111111111', '72222222-2222-2222-2222-222222222222',
                 N'first@example.org', N'FIRST@EXAMPLE.ORG', N'staff', 70001, 1),
                ('71111111-1111-1111-1111-111111111111', '72222222-2222-2222-2222-222222222222',
                 N'second@example.org', N'SECOND@EXAMPLE.ORG', N'staff', 70001, 1);
            """);

        await Assert.ThrowsAsync<SqlException>(() => NonQuery(
            connection,
            """
            INSERT INTO [asap].[StaffUser]
                ([UserPrincipalName], [NormalizedUserPrincipalName], [Role], [OrganizationId], [IsActive])
            VALUES (N'EMAIL-ONLY@example.org', N'EMAIL-ONLY@EXAMPLE.ORG', N'staff', 70001, 1);
            """));
        await Assert.ThrowsAsync<SqlException>(() => NonQuery(
            connection,
            """
            INSERT INTO [asap].[StaffUser] ([Role], [OrganizationId], [IsActive])
            VALUES (N'staff', 70001, 1);
            """));
    }

    [TestMethod]
    public async Task StaffEligibilityUsesEmailCurrentTenantAndParticipation()
    {
        var tenantId = Guid.Parse("31111111-1111-1111-1111-111111111111");
        var objectId = Guid.Parse("32222222-2222-2222-2222-222222222222");
        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(
                connection,
                $"""
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (6, N'Eligibility Library', 1);
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [Role], [OrganizationId], [IsActive])
                VALUES ('{tenantId}', '{objectId}', N'staff@example.org', N'STAFF@EXAMPLE.ORG', N'staff', 6, 1);
                """);
        }

        var configuration = TestConfigurationFactory.Create();
        configuration.Authentication.Entra.AllowedTenantIds = [tenantId.ToString()];
        var services = new ServiceCollection()
            .AddDbContextFactory<AsapDbContext>(options => options.UseSqlServer(_databaseConnectionString))
            .BuildServiceProvider();
        var service = new StaffEligibilityService(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            configuration);
        var staffId = await GetStaffIdAsync(objectId);

        Assert.AreEqual(
            StaffEligibilityOutcome.Allowed,
            (await service.EvaluateAsync(
                new StaffIdentityEvidence(staffId, "STAFF@EXAMPLE.ORG", tenantId),
                6,
                StaffRoleRequirement.Any,
                requireParticipation: true,
                CancellationToken.None)).Outcome);
        Assert.AreEqual(
            StaffEligibilityOutcome.InvalidIdentity,
            (await service.EvaluateAsync(
                new StaffIdentityEvidence(staffId, "OTHER@EXAMPLE.ORG", tenantId),
                6,
                StaffRoleRequirement.Any,
                requireParticipation: true,
                CancellationToken.None)).Outcome);

        await using (var connection = new SqlConnection(_databaseConnectionString))
        {
            await connection.OpenAsync();
            await NonQuery(connection, "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 6;");
        }

        Assert.AreEqual(
            StaffEligibilityOutcome.Forbidden,
            (await service.EvaluateAsync(
                new StaffIdentityEvidence(staffId, "STAFF@EXAMPLE.ORG", tenantId),
                6,
                StaffRoleRequirement.Any,
                requireParticipation: true,
                CancellationToken.None)).Outcome);

        async Task<long> GetStaffIdAsync(Guid id)
        {
            await using var connection = new SqlConnection(_databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [EntraObjectId] = @id;",
                connection);
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }

    [TestMethod]
    public async Task SentOutboxAllowsSubjectAndBodyPurgeButRetainsEnvelope()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();

        await NonQuery(
            connection,
            """
            INSERT INTO [asap].[EmailOutbox]
                ([OrganizationId], [DeliveryClass], [ToAddress], [FromAddress], [Subject], [BodyHtml],
                 [Status], [ProviderMessageId], [CreatedUtc], [SentUtc])
            VALUES
                (1, N'business_event', N'patron@example.test', N'asap@example.test', NULL, NULL,
                 N'sent', N'provider-message', DATEADD(day, -91, SYSUTCDATETIME()), DATEADD(day, -91, SYSUTCDATETIME()));
            """);

        Assert.AreEqual(
            1,
            Convert.ToInt32(await Scalar(
                connection,
                "SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [ProviderMessageId] = N'provider-message' AND [Subject] IS NULL AND [BodyHtml] IS NULL;")));

        await Assert.ThrowsAsync<SqlException>(async () =>
            await NonQuery(
                connection,
                """
                INSERT INTO [asap].[EmailOutbox]
                    ([OrganizationId], [DeliveryClass], [Subject], [BodyHtml], [Status], [CreatedUtc], [SentUtc])
                VALUES
                    (1, N'business_event', NULL, NULL, N'sent', SYSUTCDATETIME(), SYSUTCDATETIME());
                """));
    }

    [TestMethod]
    public async Task ClaimSnapshotCannotOmitClaimType()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await NonQuery(
            connection,
            """
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (4, N'Claim Test Library', 1);
            INSERT INTO [asap].[MaterialFormat]
                ([OwnerOrganizationId], [Code], [Label], [SortOrder], [IsEnabled], [CreatedUtc], [UpdatedUtc])
            VALUES
                (1, N'claim-test-format', N'Claim Test Format', 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        await Assert.ThrowsAsync<SqlException>(async () =>
            await NonQuery(
                connection,
                """
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                     [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES
                    (4, N'12345', N'Claim snapshot test', 0,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'claim-test-format'),
                     N'suggestion', N'Historical Staff', SYSUTCDATETIME(), NULL,
                     SYSUTCDATETIME(), SYSUTCDATETIME());
                """));
    }

    [TestMethod]
    public async Task OrderedConfigurationAllowsSourceSortTies()
    {
        await using var connection = new SqlConnection(_databaseConnectionString);
        await connection.OpenAsync();

        await NonQuery(
            connection,
            """
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [IsActive]) VALUES (5, N'Ordering Test Library', 1);
            INSERT INTO [asap].[CommonCreatorSet] ([OrganizationId]) VALUES (5);
            INSERT INTO [asap].[CommonCreatorTerm] ([OrganizationId], [Value], [SortOrder])
            VALUES (5, N'First tied term', 10), (5, N'Second tied term', 10);
            """);

        Assert.AreEqual(
            2,
            Convert.ToInt32(await Scalar(
                connection,
                "SELECT COUNT(*) FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = 5 AND [SortOrder] = 10;")));
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
