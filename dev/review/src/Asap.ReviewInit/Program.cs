using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;

return await ReviewInitializer.RunAsync();

internal static class ReviewInitializer
{
    private const string RuntimeLoginName = "asap_review_runtime";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync()
    {
        try
        {
            var settings = ReviewEnvironment.Load();
            var input = await LoadInputAsync(settings.InputConfigPath);
            ValidateInput(input);

            Directory.CreateDirectory(Path.GetDirectoryName(settings.OutputConfigPath)!);
            Directory.CreateDirectory(settings.DataProtectionKeysPath);
            MakeReviewDirectoryWritable(settings.DataProtectionKeysPath);

            Console.WriteLine($"Waiting for SQL Server at {settings.SqlServer}...");
            var masterConnectionString = BuildConnectionString(
                settings.SqlServer,
                "master",
                "sa",
                settings.SaPassword);
            await WaitForSqlAsync(masterConnectionString, TimeSpan.FromMinutes(2));

            Console.WriteLine($"Creating review database {settings.DatabaseName} if needed...");
            await EnsureDatabaseAsync(masterConnectionString, settings.DatabaseName);

            var adminDatabaseConnectionString = BuildConnectionString(
                settings.SqlServer,
                settings.DatabaseName,
                "sa",
                settings.SaPassword);

            Console.WriteLine("Deploying ASAP DACPAC...");
            DeployDacpac(masterConnectionString, settings.DatabaseName, settings.DacpacPath);

            Console.WriteLine("Installing or validating Hangfire schema...");
            await InstallHangfireAsync(adminDatabaseConnectionString, settings.HangfireScriptPath);

            var runtimePassword = CreatePassword();
            Console.WriteLine("Creating least-privilege review runtime SQL login...");
            await ConfigureRuntimeLoginAsync(
                masterConnectionString,
                adminDatabaseConnectionString,
                runtimePassword);

            if (string.Equals(input.Seed, "Review", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Seeding deterministic review records...");
                await SeedReviewDataAsync(adminDatabaseConnectionString);
            }
            else if (!string.Equals(input.Seed, "None", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Seed must be either 'Review' or 'None'.");
            }

            using var certificate = await LoadOrCreateDataProtectionCertificateAsync(settings);

            var runtimeConnectionString = BuildConnectionString(
                settings.SqlServer,
                settings.DatabaseName,
                RuntimeLoginName,
                runtimePassword);

            var applicationConfiguration = CreateApplicationConfiguration(
                input,
                runtimeConnectionString,
                certificate.Thumbprint,
                settings.DataProtectionKeysPath);

            await File.WriteAllTextAsync(
                settings.OutputConfigPath,
                JsonSerializer.Serialize(applicationConfiguration, JsonOptions));

            MakeReviewFileReadable(settings.OutputConfigPath);
            MakeReviewFileReadable(settings.PfxPath);
            MakeReviewFileReadable(settings.PfxPasswordPath);

            Console.WriteLine("ASAP review database initialization completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ASAP review initialization failed: {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<ReviewInput> LoadInputAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Review configuration was not found.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ReviewInput>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Review configuration is empty or invalid JSON.");
    }

    private static void ValidateInput(ReviewInput input)
    {
        if (!Guid.TryParse(input.Entra.ClientId, out var clientId) || clientId == Guid.Empty)
        {
            throw new InvalidOperationException("Entra.ClientId must be a non-empty GUID.");
        }

        if (input.Entra.AllowedTenantIds.Count == 0 ||
            input.Entra.AllowedTenantIds.Any(value => !Guid.TryParse(value, out var tenantId) || tenantId == Guid.Empty))
        {
            throw new InvalidOperationException("Entra.AllowedTenantIds must contain at least one non-empty GUID.");
        }

        if (input.Entra.AllowedTenantIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            input.Entra.AllowedTenantIds.Count)
        {
            throw new InvalidOperationException("Entra.AllowedTenantIds cannot contain duplicates.");
        }

        try
        {
            var address = new MailAddress(input.Entra.InitialSuperAdminEmail);
            if (!address.Address.Equals(input.Entra.InitialSuperAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Entra.InitialSuperAdminEmail must be a valid email address.");
        }
    }

    private static async Task WaitForSqlAsync(string connectionString, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                return;
            }
            catch (SqlException exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new TimeoutException("SQL Server did not become ready before the review initialization timeout.", lastError);
    }

    private static async Task EnsureDatabaseAsync(string masterConnectionString, string databaseName)
    {
        ValidateDatabaseName(databaseName);
        var escaped = databaseName.Replace("]", "]]", StringComparison.Ordinal);

        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = $"""
            IF DB_ID(@databaseName) IS NULL
            BEGIN
                EXEC(N'CREATE DATABASE [{escaped}]');
            END;
            """;
        command.Parameters.AddWithValue("@databaseName", databaseName);
        await command.ExecuteNonQueryAsync();
    }

    private static void DeployDacpac(string masterConnectionString, string databaseName, string dacpacPath)
    {
        if (!File.Exists(dacpacPath))
        {
            throw new FileNotFoundException("ASAP DACPAC was not found in the review image.", dacpacPath);
        }

        ValidateDatabaseName(databaseName);
        using var package = DacPackage.Load(dacpacPath);
        var services = new DacServices(masterConnectionString);
        services.Deploy(
            package,
            databaseName,
            upgradeExisting: true,
            new DacDeployOptions
            {
                BlockOnPossibleDataLoss = true,
                CreateNewDatabase = false,
                DropObjectsNotInSource = false
            });
    }

    private static async Task InstallHangfireAsync(string connectionString, string scriptPath)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("Packaged Hangfire installation script was not found.", scriptPath);
        }

        var script = await File.ReadAllTextAsync(scriptPath);
        if (Regex.IsMatch(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException("The packaged Hangfire script unexpectedly contains GO batch separators.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT MAX([Version]) FROM [HangFire].[Schema];";
        var result = await versionCommand.ExecuteScalarAsync();
        if (result is null or DBNull || Convert.ToInt32(result) != 9)
        {
            throw new InvalidOperationException("Hangfire schema did not initialize to expected version 9.");
        }
    }

    private static async Task ConfigureRuntimeLoginAsync(
        string masterConnectionString,
        string databaseConnectionString,
        string runtimePassword)
    {
        var escapedPassword = runtimePassword.Replace("'", "''", StringComparison.Ordinal);

        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandText = $"""
                IF SUSER_ID(N'{RuntimeLoginName}') IS NULL
                    CREATE LOGIN [asap_review_runtime]
                    WITH PASSWORD = N'{escapedPassword}', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
                ELSE
                    ALTER LOGIN [asap_review_runtime] WITH PASSWORD = N'{escapedPassword}';
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var database = new SqlConnection(databaseConnectionString))
        {
            await database.OpenAsync();
            await using var command = database.CreateCommand();
            command.CommandText = """
                IF USER_ID(N'asap_review_runtime') IS NULL
                    CREATE USER [asap_review_runtime] FOR LOGIN [asap_review_runtime];

                IF NOT EXISTS
                (
                    SELECT 1
                    FROM sys.database_role_members AS membership
                    INNER JOIN sys.database_principals AS role_principal
                        ON role_principal.principal_id = membership.role_principal_id
                    INNER JOIN sys.database_principals AS member_principal
                        ON member_principal.principal_id = membership.member_principal_id
                    WHERE role_principal.[name] = N'asap_runtime'
                      AND member_principal.[name] = N'asap_review_runtime'
                )
                    ALTER ROLE [asap_runtime] ADD MEMBER [asap_review_runtime];

                GRANT CONNECT TO [asap_review_runtime];
                GRANT VIEW DEFINITION TO [asap_review_runtime];
                GRANT EXECUTE ON SCHEMA::[asap] TO [asap_review_runtime];
                GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[HangFire] TO [asap_review_runtime];
                GRANT EXECUTE ON SCHEMA::[HangFire] TO [asap_review_runtime];
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedReviewDataAsync(string connectionString)
    {
        const string sql = """
            SET NOCOUNT ON;
            SET XACT_ABORT ON;

            IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 2)
            BEGIN
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [IsActive], [LastSyncedUtc])
                VALUES
                    (2, N'Review Library', N'REVIEW', 1, SYSUTCDATETIME());
            END;

            DECLARE @book bigint =
                (SELECT TOP (1) [Id]
                 FROM [asap].[MaterialFormat]
                 WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book');

            IF @book IS NULL
                THROW 51000, 'Review seed could not find the system book material format.', 1;

            IF NOT EXISTS (SELECT 1 FROM [asap].[TitleRequest] WHERE [LegacyId] = N'review-suggestion')
            BEGIN
                INSERT INTO [asap].[TitleRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Email],
                    [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                    [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                    [Title], [Author], [Identifier], [Publication], [AutoHold], [MaterialFormatId],
                    [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-suggestion', 2, 2, N'REVIEW1001', N'patron@example.org',
                    N'Review', N'Patron', N'1', N'Adult', 101, N'Main Library', N'Review Library',
                    N'The Review Suggestion', N'A. Reviewer', N'9780000000001', N'Coming soon', 1, @book,
                    N'suggestion', NULL, NULL, DATEADD(day, -2, SYSUTCDATETIME()), DATEADD(day, -2, SYSUTCDATETIME())
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [asap].[TitleRequest] WHERE [LegacyId] = N'review-purchase')
            BEGIN
                INSERT INTO [asap].[TitleRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Email],
                    [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                    [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                    [Title], [Author], [Identifier], [Publication], [AutoHold], [MaterialFormatId],
                    [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-purchase', 2, 2, N'REVIEW1002', N'patron@example.org',
                    N'Second', N'Patron', N'1', N'Adult', 101, N'Main Library', N'Review Library',
                    N'Awaiting Purchase', N'B. Reviewer', N'9780000000002', N'Already published', 1, @book,
                    N'outstanding_purchase', NULL, NULL, DATEADD(day, -6, SYSUTCDATETIME()), DATEADD(day, -3, SYSUTCDATETIME())
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [asap].[TitleRequest] WHERE [LegacyId] = N'review-pending-hold')
            BEGIN
                INSERT INTO [asap].[TitleRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Email],
                    [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                    [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                    [Title], [Author], [Identifier], [Publication], [AutoHold], [MaterialFormatId],
                    [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-pending-hold', 2, 2, N'REVIEW1003', N'patron@example.org',
                    N'Third', N'Patron', N'1', N'Adult', 102, N'North Branch', N'Review Library',
                    N'Pending Hold Example', N'C. Reviewer', N'9780000000003', N'Already published', 1, @book,
                    N'pending_hold', NULL, N'9003', DATEADD(day, -10, SYSUTCDATETIME()), DATEADD(day, -2, SYSUTCDATETIME())
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [asap].[TitleRequest] WHERE [LegacyId] = N'review-hold-placed')
            BEGIN
                INSERT INTO [asap].[TitleRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Email],
                    [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                    [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                    [Title], [Author], [Identifier], [Publication], [AutoHold], [MaterialFormatId],
                    [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-hold-placed', 2, 2, N'REVIEW1004', N'patron@example.org',
                    N'Fourth', N'Patron', N'1', N'Adult', 101, N'Main Library', N'Review Library',
                    N'Placed Hold Example', N'D. Reviewer', N'9780000000004', N'Already published', 1, @book,
                    N'hold_placed', NULL, N'9004', DATEADD(day, -15, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME())
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [asap].[TitleRequest] WHERE [LegacyId] = N'review-closed')
            BEGIN
                INSERT INTO [asap].[TitleRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Email],
                    [NameFirst], [NameLast], [PatronCodeId], [PatronCodeDescription],
                    [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                    [Title], [Author], [Identifier], [Publication], [AutoHold], [MaterialFormatId],
                    [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-closed', 2, 2, N'REVIEW1005', N'patron@example.org',
                    N'Fifth', N'Patron', N'1', N'Adult', 101, N'Main Library', N'Review Library',
                    N'Completed Review Example', N'E. Reviewer', N'9780000000005', N'Already published', 1, @book,
                    N'closed', N'hold_completed', N'9005', DATEADD(day, -30, SYSUTCDATETIME()), DATEADD(day, -5, SYSUTCDATETIME())
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM [asap].[AdditionalCopyRequest] WHERE [LegacyId] = N'review-additional-copy')
            BEGIN
                INSERT INTO [asap].[AdditionalCopyRequest]
                (
                    [LegacyId], [LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [Author],
                    [Identifier], [Publication], [MaterialFormatId], [FormatSnapshot], [Status],
                    [CreatedByDisplayName], [CreatedUtc], [UpdatedUtc]
                )
                VALUES
                (
                    N'review-additional-copy', 2, N'Review Library', N'9010', N'Additional Copy Example',
                    N'F. Reviewer', N'9780000000010', N'Already published', @book, N'Book', N'open',
                    N'Review seed', DATEADD(day, -8, SYSUTCDATETIME()), DATEADD(day, -2, SYSUTCDATETIME())
                );
            END;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<X509Certificate2> LoadOrCreateDataProtectionCertificateAsync(
        ReviewEnvironment settings)
    {
        if (File.Exists(settings.PfxPath) && File.Exists(settings.PfxPasswordPath))
        {
            Console.WriteLine("Reusing existing disposable Data Protection certificate...");
            var existingPassword = (await File.ReadAllTextAsync(settings.PfxPasswordPath)).Trim();
            return X509CertificateLoader.LoadPkcs12FromFile(
                settings.PfxPath,
                existingPassword,
                X509KeyStorageFlags.Exportable);
        }

        if (File.Exists(settings.PfxPath) || File.Exists(settings.PfxPasswordPath))
        {
            throw new InvalidOperationException(
                "Review Data Protection certificate state is incomplete. Clean the review environment and start again.");
        }

        Console.WriteLine("Generating disposable Data Protection certificate...");
        var certificate = CreateDataProtectionCertificate();
        var pfxPassword = CreatePassword();
        await File.WriteAllBytesAsync(
            settings.PfxPath,
            certificate.Export(X509ContentType.Pfx, pfxPassword));
        await File.WriteAllTextAsync(settings.PfxPasswordPath, pfxPassword);
        return certificate;
    }

    private static X509Certificate2 CreateDataProtectionCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ASAP Review Data Protection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static object CreateApplicationConfiguration(
        ReviewInput input,
        string runtimeConnectionString,
        string certificateThumbprint,
        string dataProtectionKeysPath)
    {
        var emailDomain = input.Entra.InitialSuperAdminEmail.Split('@', 2)[1];
        var limit = new { PageSize = 50, MaxPerRun = 200 };

        return new
        {
            Environment = new
            {
                Name = "Review",
                IsNonProduction = true,
                UiBannerText = "LOCAL REVIEW ENVIRONMENT"
            },
            ConnectionStrings = new
            {
                AsapDatabase = runtimeConnectionString,
                HangfireDatabase = runtimeConnectionString
            },
            Authentication = new
            {
                Entra = new
                {
                    input.Entra.ClientId,
                    input.Entra.AllowedTenantIds,
                    InitialSuperAdmin = new
                    {
                        UserPrincipalName = input.Entra.InitialSuperAdminEmail,
                        DisplayName = input.Entra.InitialSuperAdminDisplayName,
                        NotificationEmail = input.Entra.InitialSuperAdminEmail
                    }
                }
            },
            Application = new
            {
                BusinessTimeZone = "America/New_York",
                DataProtectionKeysPath = dataProtectionKeysPath,
                DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint,
                LogPath = "/review/logs"
            },
            EmailSafety = new
            {
                AllowedRecipientDomains = new[] { emailDomain, "example.org" }
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            },
            PatronLoginRateLimit = new
            {
                PermitLimit = 100,
                WindowSeconds = 300
            },
            Hangfire = new
            {
                Schedules = new Dictionary<string, string>
                {
                    ["WorkflowProcessing"] = "*/5 * * * *",
                    ["IdentifierProcessing"] = "*/5 * * * *",
                    ["OrganizationRefresh"] = "0 */6 * * *",
                    ["WeeklyStaffSummary"] = "0 8 * * 1",
                    ["EmailOutboxSweep"] = "*/5 * * * *",
                    ["PatronSessionCleanup"] = "15 * * * *",
                    ["EmailPayloadCleanup"] = "30 2 * * *"
                },
                ProcessingLimits = new
                {
                    Default = limit,
                    Timeouts = limit,
                    Queues = new Dictionary<string, object>
                    {
                        ["IdentifierProcessing"] = limit,
                        ["PurchasePromotion"] = limit,
                        ["HoldPlacement"] = limit,
                        ["FulfillmentTracking"] = limit,
                        ["OutstandingTimeout"] = limit,
                        ["PendingHoldTimeout"] = limit,
                        ["HoldPickupTimeout"] = limit,
                        ["AdditionalCopyTimeout"] = limit
                    }
                }
            }
        };
    }

    private static string BuildConnectionString(
        string server,
        string database,
        string userName,
        string password) =>
        new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            UserID = userName,
            Password = password,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = true
        }.ConnectionString;

    private static string CreatePassword() =>
        $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(24))}aA1!";

    private static void ValidateDatabaseName(string databaseName)
    {
        if (!Regex.IsMatch(databaseName, "^Asap[A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Review database name must start with Asap and contain only letters, digits, or underscores.");
        }
    }

    private static void MakeReviewDirectoryWritable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    private static void MakeReviewFileReadable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);
        }
    }
}

internal sealed class ReviewEnvironment
{
    public required string SqlServer { get; init; }
    public required string SaPassword { get; init; }
    public required string DatabaseName { get; init; }
    public required string InputConfigPath { get; init; }
    public required string OutputConfigPath { get; init; }
    public required string PfxPath { get; init; }
    public required string PfxPasswordPath { get; init; }
    public required string DacpacPath { get; init; }
    public required string HangfireScriptPath { get; init; }
    public required string DataProtectionKeysPath { get; init; }

    public static ReviewEnvironment Load() => new()
    {
        SqlServer = Required("REVIEW_SQL_SERVER"),
        SaPassword = Required("REVIEW_SA_PASSWORD"),
        DatabaseName = Environment.GetEnvironmentVariable("REVIEW_DATABASE_NAME") ?? "AsapReview",
        InputConfigPath = Environment.GetEnvironmentVariable("REVIEW_INPUT_CONFIG") ?? "/review/input/review.json",
        OutputConfigPath = Environment.GetEnvironmentVariable("REVIEW_OUTPUT_CONFIG") ?? "/review/config/application.json",
        PfxPath = Environment.GetEnvironmentVariable("REVIEW_PFX_PATH") ?? "/review/config/dataprotection.pfx",
        PfxPasswordPath = Environment.GetEnvironmentVariable("REVIEW_PFX_PASSWORD_PATH") ?? "/review/config/dataprotection-password.txt",
        DacpacPath = Environment.GetEnvironmentVariable("REVIEW_DACPAC_PATH") ?? "/review/assets/Asap.Database.dacpac",
        HangfireScriptPath = Environment.GetEnvironmentVariable("REVIEW_HANGFIRE_SCRIPT") ?? "/review/assets/Hangfire/1.8.25/install.sql",
        DataProtectionKeysPath = Environment.GetEnvironmentVariable("REVIEW_DATA_PROTECTION_PATH") ?? "/review/dpkeys"
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment value {name} is missing.");
}

internal sealed class ReviewInput
{
    public ReviewEntraInput Entra { get; set; } = new();
    public string Seed { get; set; } = "Review";
}

internal sealed class ReviewEntraInput
{
    public string ClientId { get; set; } = string.Empty;
    public List<string> AllowedTenantIds { get; set; } = [];
    public string InitialSuperAdminEmail { get; set; } = string.Empty;
    public string InitialSuperAdminDisplayName { get; set; } = "ASAP Reviewer";
}
