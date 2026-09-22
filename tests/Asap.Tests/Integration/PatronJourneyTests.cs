using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Tests.Sql;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Infrastructure.Testing;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed partial class PatronJourneyTests
{
    private static string databaseName = null!;
    private static string masterConnectionString = null!;
    private static string databaseConnectionString = null!;
    private static string temporaryDirectory = null!;
    private static string configurationPath = null!;
    private static string certificateThumbprint = null!;
    private WebApplicationFactory<Program>? factory;
    private RecordingOutboxDispatcher? dispatcher;
    private MutableTimeProvider? timeProvider;

    [ClassInitialize]
    public static async Task Initialize(TestContext context)
    {
        masterConnectionString =
            Environment.GetEnvironmentVariable("ASAP_TEST_SQL_CONNECTION_STRING") ??
            "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True";
        var masterBuilder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = "master"
        };
        masterConnectionString = masterBuilder.ConnectionString;
        databaseName = $"AsapPatronJourney_{Guid.NewGuid():N}";
        databaseConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        var dacpacPath = TestArtifactPaths.FindDacpac();
        context.WriteLine($"Deploying {dacpacPath} to {databaseName}");
        using (var package = DacPackage.Load(dacpacPath))
        {
            new DacServices(masterConnectionString).Deploy(
                package,
                databaseName,
                upgradeExisting: true,
                new DacDeployOptions
                {
                    BlockOnPossibleDataLoss = true,
                    CreateNewDatabase = true,
                    DropObjectsNotInSource = true
                });
        }

        await InstallHangfireAsync();
        await SeedLibraryAsync();

        temporaryDirectory = Path.Combine(Path.GetTempPath(), $"asap-patron-journey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "keys"));
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, "logs"));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ASAP Patron Journey Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(2));
        using var persistedCertificate = X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        certificateThumbprint = persistedCertificate.Thumbprint;
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            store.Add(persistedCertificate);
        }

        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configurationPath = Path.Combine(temporaryDirectory, "asap.settings.json");
        await File.WriteAllTextAsync(
            configurationPath,
            JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (!string.IsNullOrWhiteSpace(certificateThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Find(
                         X509FindType.FindByThumbprint,
                         certificateThumbprint,
                         validOnly: false))
            {
                store.Remove(certificate);
                certificate.Dispose();
            }
        }

        if (!string.IsNullOrWhiteSpace(databaseName) &&
            databaseName.StartsWith("AsapPatronJourney_", StringComparison.Ordinal))
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync();
            var quoted = new SqlCommandBuilder().QuoteIdentifier(databaseName);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};";
            await command.ExecuteNonQueryAsync();
        }

        if (!string.IsNullOrWhiteSpace(temporaryDirectory) && Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [TestInitialize]
    public void StartApplication()
    {
        dispatcher = new RecordingOutboxDispatcher();
        timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        factory = CreateApplicationFactory(configurationPath);
    }

    private WebApplicationFactory<Program> CreateApplicationFactory(
        string settingsPath,
        IPolarisReferenceProvider? referenceProvider = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Asap:ConfigFile", settingsPath);
            builder.UseSetting("Testing:UseDeterministicPatronProvider", "true");
            builder.ConfigureServices(services =>
            {
                var worker = services.Single(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService) &&
                    descriptor.ImplementationType == typeof(HangfireWorkerHostedService));
                services.Remove(worker);
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(dispatcher!);
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, RecordingEmailSender>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider!);
                if (referenceProvider is not null)
                {
                    services.RemoveAll<IPolarisReferenceProvider>();
                    services.AddSingleton(referenceProvider);
                }
            });
        });

    [TestCleanup]
    public async Task StopApplication()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }
        await CleanupSharedSlice2TestConfigurationAsync();
    }

    [TestMethod]
    public async Task PatronCanLoginSubmitAndLogoutThroughHttpWithCommittedSqlState()
    {
        using var client = factory!.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000001", pin = "1234", libraryOrgId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginDocument.RootElement.GetProperty("token").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        Assert.AreEqual("20000000000001", loginDocument.RootElement.GetProperty("barcode").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var restored = await client.GetAsync("/api/asap/patron/session");
        Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode, await restored.Content.ReadAsStringAsync());

        var submission = await client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            new
            {
                format = "book",
                title = "A Tracer Through the Stack",
                author = "Ada Example",
                isbn = "9780000000001",
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            });
        Assert.AreEqual(HttpStatusCode.Created, submission.StatusCode, await submission.Content.ReadAsStringAsync());

        using var submissionDocument = JsonDocument.Parse(await submission.Content.ReadAsStringAsync());
        var requestId = submissionDocument.RootElement.GetProperty("id").GetInt64();
        Assert.HasCount(1, dispatcher!.EnqueuedIds);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    r.[Barcode], r.[Title], r.[Status], r.[IsbnCheckStatus], r.[BibId],
                    (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id]),
                    o.[Status], o.[BusinessKey]
                FROM [asap].[TitleRequest] r
                JOIN [asap].[EmailOutbox] o ON o.[Id] = @outboxId
                WHERE r.[Id] = @requestId;
                """;
            command.Parameters.AddWithValue("@requestId", requestId);
            command.Parameters.AddWithValue("@outboxId", dispatcher.EnqueuedIds.Single());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("20000000000001", reader.GetString(0));
            Assert.AreEqual("A Tracer Through The Stack", reader.GetString(1));
            Assert.AreEqual("suggestion", reader.GetString(2));
            Assert.AreEqual("found", reader.GetString(3));
            Assert.AreEqual("9001", reader.GetString(4));
            Assert.IsGreaterThanOrEqualTo(1, reader.GetInt32(5));
            Assert.AreEqual("pending", reader.GetString(6));
            Assert.AreEqual($"patron-submission:{requestId}", reader.GetString(7));
        }

        var logout = await client.PostAsync("/api/asap/patron/logout", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/asap/patron/session")).StatusCode);
    }

    [TestMethod]
    public async Task PatronLoginRateLimitUsesRestartLoadedExternalPolicyAndRetryAfter()
    {
        var settingsPath = Path.Combine(temporaryDirectory, $"rate-limit-{Guid.NewGuid():N}.json");
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.PatronLoginRateLimit.PermitLimit = 2;
        configuration.PatronLoginRateLimit.WindowSeconds = 5;
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(configuration));

        await using var limitedFactory = CreateApplicationFactory(settingsPath);
        using var client = limitedFactory.CreateClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var denied = await client.PostAsJsonAsync(
                "/api/asap/patron/login",
                new { barcode = "20000000000001", pin = "wrong", libraryOrgId = 2 });
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000001", pin = "wrong", libraryOrgId = 2 });

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.IsTrue(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.IsTrue(int.TryParse(values.Single(), out var retryAfter));
        Assert.IsGreaterThanOrEqualTo(1, retryAfter);
        Assert.IsLessThanOrEqualTo(5, retryAfter);
    }

    [TestMethod]
    public async Task PatronBrowserJourneyRunsOnKestrelWithRealSqlAndRecordingEmailTransport()
    {
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var baseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifactDirectory = Path.Combine(
            repositoryRoot,
            ".artifacts",
            "browser",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["TZ"] = "America/New_York";
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "patron.cjs"));
        startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(artifactDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the patron browser runner.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.AreEqual(0, process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");

        using (var report = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "browser-results.json"))))
        {
            Assert.HasCount(10, report.RootElement.GetProperty("majorStates").EnumerateArray().ToArray());
            Assert.HasCount(3, report.RootElement.GetProperty("authRaces").EnumerateArray().ToArray());
        }

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        Assert.AreEqual(2, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Barcode] IN (N'20000000000801', N'20000000000802');"));
        Assert.HasCount(2, dispatcher!.EnqueuedIds);
        await using var outbox = connection.CreateCommand();
        outbox.CommandText =
            "SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [Id] IN (@first, @second) AND [Status] = N'pending';";
        outbox.Parameters.AddWithValue("@first", dispatcher.EnqueuedIds[0]);
        outbox.Parameters.AddWithValue("@second", dispatcher.EnqueuedIds[1]);
        Assert.AreEqual(2, Convert.ToInt32(await outbox.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers()
    {
        factory!.UseKestrel(0);
        using var client = factory.CreateClient();
        var baseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var staffObjectId = Guid.NewGuid();
        var seeded = await SeedStaffBrowserStateAsync(
            Guid.Parse(identity.TenantId!),
            Guid.Parse(identity.ObjectId!),
            staffObjectId);

        var repositoryRoot = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifactDirectory = Path.Combine(
            repositoryRoot,
            ".artifacts",
            "browser",
            $"staff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "tests", "browser", "staff.cjs"));
        startInfo.ArgumentList.Add(baseAddress.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(artifactDirectory);
        startInfo.ArgumentList.Add(seeded.SuperId.ToString());
        startInfo.ArgumentList.Add(identity.TenantId!);
        startInfo.ArgumentList.Add(identity.UserPrincipalName!);
        startInfo.ArgumentList.Add(seeded.StaffId.ToString());
        startInfo.ArgumentList.Add("browser.staff@example.org");
        startInfo.ArgumentList.Add(seeded.LegacyRequestId);
        startInfo.ArgumentList.Add(seeded.PrimaryRequestId.ToString());
        startInfo.ArgumentList.Add(seeded.BlockedRequestId.ToString());
        startInfo.ArgumentList.Add(seeded.ResolutionRequestId.ToString());
        startInfo.ArgumentList.Add(seeded.OtherRequestId.ToString());
        startInfo.ArgumentList.Add(seeded.CopySourceRequestId.ToString());
        startInfo.ArgumentList.Add(seeded.InvalidClosedCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.InvalidClaimantId.ToString());
        startInfo.ArgumentList.Add(seeded.LegacyRuleId.ToString());
        startInfo.ArgumentList.Add(seeded.MobileCopyId.ToString());
        startInfo.ArgumentList.Add(seeded.ForeignStaffId.ToString());
        startInfo.ArgumentList.Add(seeded.InvalidTenantStaffId.ToString());
        startInfo.ArgumentList.Add(seeded.UnboundStaffId.ToString());
        startInfo.ArgumentList.Add(seeded.StaleTitleAId.ToString());
        startInfo.ArgumentList.Add(seeded.StaleTitleBId.ToString());
        startInfo.ArgumentList.Add(seeded.StaleCopyAId.ToString());
        startInfo.ArgumentList.Add(seeded.StaleCopyBId.ToString());
        startInfo.ArgumentList.Add(seeded.StaleCreateSourceId.ToString());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the staff browser runner.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.AreEqual(0, process.ExitCode, $"{stdout}{Environment.NewLine}{stderr}");

        using (var report = JsonDocument.Parse(
                   await File.ReadAllTextAsync(Path.Combine(artifactDirectory, "staff-browser-results.json"))))
        {
            Assert.HasCount(22, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
            var analytics = report.RootElement.GetProperty("analytics");
            Assert.AreEqual("all", analytics.GetProperty("desktopSuperAdminScope").GetString());
            Assert.AreEqual("last90", analytics.GetProperty("desktopRange").GetString());
            Assert.IsTrue(analytics.GetProperty("invalidScopeRecovery").GetBoolean());
            Assert.AreEqual(1, analytics.GetProperty("allScopeRecoveryRequests").GetInt32());
            Assert.IsTrue(analytics.GetProperty("mobileLibraryOnly").GetBoolean());
            Assert.AreEqual("legacy", report.RootElement.GetProperty("additionalCopy").GetProperty("inheritedClaimType").GetString());
            Assert.AreEqual(
                seeded.LegacyRuleId.ToString(),
                report.RootElement.GetProperty("additionalCopy").GetProperty("inheritedClaimRuleId").GetString());
            Assert.AreEqual(
                "claimant_inactive",
                report.RootElement.GetProperty("additionalCopy").GetProperty("clearedReason").GetString());
            Assert.AreEqual(
                seeded.StaffId.ToString(),
                report.RootElement.GetProperty("assignmentCandidates").GetProperty("ordinaryStaffAssigned").GetString());
            Assert.AreEqual(
                seeded.SuperId.ToString(),
                report.RootElement.GetProperty("assignmentCandidates").GetProperty("systemSuperAdminAssigned").GetString());
            Assert.AreEqual(
                seeded.ForeignStaffId.ToString(),
                report.RootElement.GetProperty("assignmentCandidates").GetProperty("foreignStaffExcluded").GetString());
            Assert.IsTrue(report.RootElement.GetProperty("staleAssignmentCandidates").GetProperty("titleRequestBOnly").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleAssignmentCandidates").GetProperty("additionalCopyBOnly").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("ordinaryTitleAssignment").GetProperty("desktop").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("ordinaryTitleAssignment").GetProperty("mobile").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("additionalCopyNonDelete").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("additionalCopyConflict").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("additionalCopyDelete").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("titleRequestAssign").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("additionalCopyCreate").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("sameIdRerender").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("signedOutContext").GetBoolean());
            Assert.IsTrue(report.RootElement.GetProperty("staleMutationCompletions").GetProperty("holdOperationError").GetBoolean());
        }

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            """
            SELECT r.[Title], r.[Status], r.[ClaimedByStaffUserId], s.[WeeklyActionSummaryEnabled],
                   s.[WeeklyActionSummaryEmail], s.[DefaultMineUnclaimedFilter],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] IN (N'claim_manual_cleared', N'claim_manual_assigned')),
                   r.[Publication],
                   JSON_VALUE(r.[CustomFieldsJson], '$.audience_note.value'),
                   JSON_VALUE(r.[CustomFieldsJson], '$.binding.value'),
                   JSON_VALUE(r.[CustomFieldsJson], '$.retired.value')
            FROM [asap].[TitleRequest] r
            JOIN [asap].[StaffUser] s ON s.[Id] = @superId
            WHERE r.[Id] = @requestId;
            """;
        command.Parameters.AddWithValue("@requestId", seeded.PrimaryRequestId);
        command.Parameters.AddWithValue("@superId", seeded.SuperId);
        await using var verified = await command.ExecuteReaderAsync();
        Assert.IsTrue(await verified.ReadAsync());
        Assert.AreEqual("Browser staff title edited", verified.GetString(0));
        Assert.AreEqual("outstanding_purchase", verified.GetString(1));
        Assert.AreEqual(seeded.SuperId, verified.GetInt64(2));
        Assert.IsTrue(verified.GetBoolean(3));
        Assert.AreEqual("browser-weekly@example.org", verified.GetString(4));
        Assert.IsTrue(verified.GetBoolean(5));
        Assert.AreEqual(4, verified.GetInt32(6));
        Assert.AreEqual("Library backlist", verified.GetString(7));
        Assert.AreEqual("Edited audience", verified.GetString(8));
        Assert.AreEqual("hardback", verified.GetString(9));
        Assert.AreEqual("Keep me", verified.GetString(10));
        await verified.CloseAsync();

        await using var resolution = verify.CreateCommand();
        resolution.CommandText =
            """
            SELECT r.[Status], o.[State], o.[Phase], o.[ExecutionEpoch], o.[PolarisHoldId], o.[OutcomeEvidenceKind],
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.evidenceReference'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.proofSource'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.causalConnection'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.provenFinalHoldId'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.operationSpecificProofAttested'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.executorExclusionReference'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.executorExclusionExplanation'),
                   JSON_VALUE(o.[DetailJson], '$.operatorResolution.executorExclusionAttested'),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = r.[Id] AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]
                    WHERE [TargetType] = N'hold_placement_operation'
                      AND [TargetId] = CONVERT(nvarchar(30), o.[Id])
                      AND [Action] = N'hold_operation_resolved_succeeded'),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                    WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), r.[Id]) + N':%')
            FROM [asap].[TitleRequest] r
            JOIN [asap].[HoldPlacementOperation] o ON o.[TitleRequestId] = r.[Id]
            WHERE r.[Id] = @resolutionRequestId;
            """;
        resolution.Parameters.AddWithValue("@resolutionRequestId", seeded.ResolutionRequestId);
        await using var resolved = await resolution.ExecuteReaderAsync();
        Assert.IsTrue(await resolved.ReadAsync());
        Assert.AreEqual("hold_placed", resolved.GetString(0));
        Assert.AreEqual("succeeded", resolved.GetString(1));
        Assert.AreEqual("result_recorded", resolved.GetString(2));
        Assert.AreEqual(4L, resolved.GetInt64(3));
        Assert.AreEqual("8456", resolved.GetString(4));
        Assert.AreEqual("operator_verified:authoritative_correlated_hold", resolved.GetString(5));
        Assert.AreEqual("Support report SR-BROWSER-2904", resolved.GetString(6));
        Assert.AreEqual("Polaris support final transaction report", resolved.GetString(7));
        Assert.AreEqual(
            "Report identifies operation attempt 2, frozen patron ending 2904, and BIB 92904 as the completed transaction.",
            resolved.GetString(8));
        Assert.AreEqual("8456", resolved.GetString(9));
        Assert.AreEqual("true", resolved.GetString(10));
        Assert.AreEqual("Operations record OPS-BROWSER-2904", resolved.GetString(11));
        Assert.AreEqual(
            "All responsible and superseded workers and interactive hosts ended at 2026-09-13T13:00:00Z; the cited final report accounts for already-sent provider work.",
            resolved.GetString(12));
        Assert.AreEqual("true", resolved.GetString(13));
        Assert.AreEqual(1, resolved.GetInt32(14));
        Assert.AreEqual(1, resolved.GetInt32(15));
        Assert.AreEqual(1, resolved.GetInt32(16));
        await resolved.CloseAsync();

        await using var additionalCopy = verify.CreateCommand();
        additionalCopy.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM [asap].[TitleRequest]
                 WHERE [Id]=@copySourceId
                   AND [Status]=N'hold_placed'
                   AND [Notes] LIKE N'%Additional copy request created for BIB 92905.%'),
                (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                 WHERE [Subject]=N'ASAP additional-copy reminder'
                   AND [BodyText] LIKE N'%Browser additional copy source (BIB 92905)%'
                   AND [DeliveryClass]=N'staff_authorization_sensitive'),
                (SELECT COUNT(*) FROM [asap].[DeletedRequestAudit]
                 WHERE [RequestType]=N'additional_copy'
                   AND [Title]=N'Browser additional copy source'
                   AND [BibId]=N'92905'
                   AND [MaskedBarcode] IS NULL),
                (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest]
                 WHERE [Id]=@invalidClosedCopyId
                   AND [Status]=N'open'
                   AND [ClaimedByStaffUserId] IS NULL
                   AND [ClaimedByDisplayName] IS NULL
                   AND [ClaimedAtUtc] IS NULL
                   AND [ClaimType] IS NULL
                   AND [ClaimRuleId] IS NULL
                   AND [Notes] LIKE N'%System cleared retained claim while reopening (claimant_inactive)%'),
                (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest]
                 WHERE [Id]=@mobileCopyId AND [Status]=N'open' AND [ClaimedByStaffUserId] IS NULL),
                (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest]
                 WHERE [Title]=N'Browser additional copy source');
            """;
        additionalCopy.Parameters.AddWithValue("@copySourceId", seeded.CopySourceRequestId);
        additionalCopy.Parameters.AddWithValue("@invalidClosedCopyId", seeded.InvalidClosedCopyId);
        additionalCopy.Parameters.AddWithValue("@mobileCopyId", seeded.MobileCopyId);
        await using var copyState = await additionalCopy.ExecuteReaderAsync();
        Assert.IsTrue(await copyState.ReadAsync());
        Assert.AreEqual(1, copyState.GetInt32(0));
        Assert.AreEqual(1, copyState.GetInt32(1));
        Assert.AreEqual(1, copyState.GetInt32(2));
        Assert.AreEqual(1, copyState.GetInt32(3));
        Assert.AreEqual(1, copyState.GetInt32(4));
        Assert.AreEqual(0, copyState.GetInt32(5));
    }

    [TestMethod]
    public async Task PatronEntryPathsServeIndexWithoutRedirectingAwayLibraryContext()
    {
        using var client = factory!.CreateClient();

        foreach (var path in new[] { "/patron?libraryOrgId=2", "/patron/?libraryOrgId=2" })
        {
            var response = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            Assert.AreEqual("/patron/", response.RequestMessage!.RequestUri!.AbsolutePath, path);
            Assert.AreEqual("?libraryOrgId=2", response.RequestMessage.RequestUri.Query, path);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "<title>Material Suggestion</title>", path);
        }
    }

    [TestMethod]
    public async Task PatronResponseCspUsesCanonicalExactAndWildcardEmbedOrigins()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES
                    (1, N'https://*.embed.example.org', N'https://*.embed.example.org', SYSUTCDATETIME()),
                    (1, N'http://localhost:4321', N'http://localhost:4321', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            var response = await client.GetAsync("/patron/");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var policy = response.Headers.GetValues("Content-Security-Policy").Single();
            StringAssert.Contains(
                policy,
                "frame-ancestors 'self' http://localhost:4321 https://*.embed.example.org;");
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PatronResponseCspEchoesConcreteOriginAllowedByWildcard()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES
                    (1, N'https://sites.google.com', N'https://sites.google.com', SYSUTCDATETIME()),
                    (1, N'https://*.googleusercontent.com', N'https://*.googleusercontent.com', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/patron/");
            request.Headers.TryAddWithoutValidation(
                "Referer",
                "https://123-embed.googleusercontent.com/library/form");
            var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var policy = response.Headers.GetValues("Content-Security-Policy").Single();
            StringAssert.Contains(
                policy,
                "frame-ancestors 'self' https://*.googleusercontent.com https://sites.google.com https://123-embed.googleusercontent.com;");

            using var nestedRequest = new HttpRequestMessage(HttpMethod.Get, "/patron/");
            Assert.IsTrue(nestedRequest.Headers.TryAddWithoutValidation(
                "Referer",
                "https://deep.123-embed.googleusercontent.com/path"));
            using var nestedResponse = await client.SendAsync(nestedRequest);
            Assert.AreEqual(
                "'self' https://*.googleusercontent.com https://sites.google.com https://deep.123-embed.googleusercontent.com",
                FrameAncestorsDirective(nestedResponse));
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[PatronEmbedAllowedOrigin] WHERE [OrganizationId] = 1;";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PatronResponseCspDoesNotEchoUntrustedOrigins()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                DELETE FROM [asap].[PatronEmbedAllowedOrigin];
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES
                    (1, N'https://*.embed.example.org', N'https://*.embed.example.org', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            var untrustedReferers = new[]
            {
                "https://embed.example.org/",
                "https://child.embed.example.org.evil.invalid/",
                "https://unmatched.invalid/",
                "not a URL",
                "https://child.embed.example.org;frame-src *",
                "https://library-only.example.org/"
            };

            foreach (var referer in untrustedReferers)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/patron/");
                Assert.IsTrue(request.Headers.TryAddWithoutValidation("Referer", referer));
                using var response = await client.SendAsync(request);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, referer);
                var policy = response.Headers.GetValues("Content-Security-Policy").Single();
                var directive = Regex.Match(policy, @"frame-ancestors ([^;]+);").Groups[1].Value;
                Assert.AreEqual("'self' https://*.embed.example.org", directive, referer);
            }
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[PatronEmbedAllowedOrigin];";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PatronResponseCspUsesRefererBeforeOriginAndDeduplicatesExactOrigin()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                DELETE FROM [asap].[PatronEmbedAllowedOrigin];
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES
                    (1, N'https://*.embed.example.org', N'https://*.embed.example.org', SYSUTCDATETIME()),
                    (1, N'https://exact.example.org', N'https://exact.example.org', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            const string storedDirective =
                "'self' https://*.embed.example.org https://exact.example.org";

            using (var exactRequest = new HttpRequestMessage(HttpMethod.Get, "/patron/"))
            {
                Assert.IsTrue(exactRequest.Headers.TryAddWithoutValidation(
                    "Referer",
                    "https://exact.example.org/path"));
                using var exactResponse = await client.SendAsync(exactRequest);
                Assert.AreEqual(
                    storedDirective,
                    FrameAncestorsDirective(exactResponse),
                    "An exact request origin must not be duplicated.");
            }

            using (var originRequest = new HttpRequestMessage(HttpMethod.Get, "/patron/"))
            {
                Assert.IsTrue(originRequest.Headers.TryAddWithoutValidation(
                    "Origin",
                    "https://origin-only.embed.example.org"));
                using var originResponse = await client.SendAsync(originRequest);
                Assert.AreEqual(
                    $"{storedDirective} https://origin-only.embed.example.org",
                    FrameAncestorsDirective(originResponse),
                    "Origin must be used when Referer is absent.");
            }

            using (var precedenceRequest = new HttpRequestMessage(HttpMethod.Get, "/patron/"))
            {
                Assert.IsTrue(precedenceRequest.Headers.TryAddWithoutValidation(
                    "Referer",
                    "https://unmatched.invalid/path"));
                Assert.IsTrue(precedenceRequest.Headers.TryAddWithoutValidation(
                    "Origin",
                    "https://would-match.embed.example.org"));
                using var precedenceResponse = await client.SendAsync(precedenceRequest);
                Assert.AreEqual(
                    storedDirective,
                    FrameAncestorsDirective(precedenceResponse),
                    "A present Referer must not fall through to Origin when it is unmatched.");
            }

            using (var malformedRefererRequest = new HttpRequestMessage(HttpMethod.Get, "/patron/"))
            {
                Assert.IsTrue(malformedRefererRequest.Headers.TryAddWithoutValidation(
                    "Referer",
                    "not a URL"));
                Assert.IsTrue(malformedRefererRequest.Headers.TryAddWithoutValidation(
                    "Origin",
                    "https://would-also-match.embed.example.org"));
                using var malformedRefererResponse = await client.SendAsync(malformedRefererRequest);
                Assert.AreEqual(
                    storedDirective,
                    FrameAncestorsDirective(malformedRefererResponse),
                    "A malformed Referer must not fall through to Origin.");
            }
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[PatronEmbedAllowedOrigin];";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PatronResponseCspWildcardMatchRequiresSameExplicitPortText()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                """
                DELETE FROM [asap].[PatronEmbedAllowedOrigin];
                INSERT INTO [asap].[PatronEmbedAllowedOrigin]
                    ([OrganizationId], [Origin], [NormalizedOrigin], [CreatedUtc])
                VALUES
                    (1, N'https://*.plain.example.org', N'https://*.plain.example.org', SYSUTCDATETIME()),
                    (1, N'https://*.ported.example.org:8443', N'https://*.ported.example.org:8443', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            const string storedDirective =
                "'self' https://*.plain.example.org https://*.ported.example.org:8443";
            var cases = new[]
            {
                new
                {
                    Referer = "https://child.plain.example.org/path",
                    Expected = $"{storedDirective} https://child.plain.example.org"
                },
                new
                {
                    Referer = "https://child.plain.example.org:444/path",
                    Expected = storedDirective
                },
                new
                {
                    Referer = "https://child.ported.example.org:8443/path",
                    Expected = $"{storedDirective} https://child.ported.example.org:8443"
                },
                new
                {
                    Referer = "https://child.ported.example.org/path",
                    Expected = storedDirective
                }
            };

            foreach (var item in cases)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/patron/");
                Assert.IsTrue(request.Headers.TryAddWithoutValidation("Referer", item.Referer));
                using var response = await client.SendAsync(request);
                Assert.AreEqual(item.Expected, FrameAncestorsDirective(response), item.Referer);
            }
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM [asap].[PatronEmbedAllowedOrigin];";
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PublicConfigurationAppliesFieldInheritanceAndExternalSearchOverrides()
    {
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE [asap].[SystemSettings]
                SET [SystemNotEnabledMessage] = N'{{library}} is offline.', [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;

                IF NOT EXISTS (SELECT 1 FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2)
                    INSERT INTO [asap].[PatronSettings]
                        ([OrganizationId], [SuggestionStatusLabel], [UpdatedUtc])
                    VALUES (2, N'Locally received', SYSUTCDATETIME());
                ELSE
                    UPDATE [asap].[PatronSettings]
                    SET [SuggestionStatusLabel] = N'Locally received', [UpdatedUtc] = SYSUTCDATETIME()
                    WHERE [OrganizationId] = 2;

                INSERT INTO [asap].[ExternalSearchProviderOverride]
                    ([LibraryOrganizationId], [ExternalSearchProviderId], [IsEnabled])
                SELECT 2, [Id], 0
                FROM [asap].[ExternalSearchProvider]
                WHERE [ProviderKey] = N'external_search_1'
                  AND NOT EXISTS
                  (
                      SELECT 1 FROM [asap].[ExternalSearchProviderOverride]
                      WHERE [LibraryOrganizationId] = 2
                        AND [ExternalSearchProviderId] = [ExternalSearchProvider].[Id]
                  );
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var client = factory!.CreateClient();
        var response = await client.GetAsync("/api/asap/config?libraryOrgId=2");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.AreEqual("Test Library is offline.", root.GetProperty("systemNotEnabledMessage").GetString());
        Assert.IsFalse(root.GetProperty("systemNotEnabledMessage").GetString()!.Contains("{{library}}", StringComparison.Ordinal));
        Assert.AreEqual(
            "Locally received",
            root.GetProperty("duplicateStatusLabels").GetProperty("suggestion").GetString());
        Assert.IsFalse(root.GetProperty("externalSearch1Enabled").GetBoolean());
        Assert.AreEqual("Search Amazon", root.GetProperty("externalSearch1Label").GetString());
        Assert.IsTrue(root.GetProperty("externalSearch2Enabled").GetBoolean());
        Assert.AreEqual("Search Goodreads", root.GetProperty("externalSearch2Label").GetString());
        Assert.AreEqual("Search WorldCat", root.GetProperty("externalSearch3Label").GetString());
        Assert.IsFalse(root.GetProperty("externalSearch4Enabled").GetBoolean());
    }

    [TestMethod]
    public async Task PublicConfigurationUsesPinnedDefaultsWhenSystemAndLibraryTextAreBlank()
    {
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE [asap].[PatronSettings]
                SET [LoginPrompt] = NULL, [LoginNote] = NULL, [SuggestionFormNote] = NULL,
                    [NoEmailMessage] = NULL, [SuccessMessage] = NULL, [AlreadySubmittedMessage] = NULL,
                    [EbookMessage] = NULL, [EaudiobookMessage] = NULL, [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;

                IF NOT EXISTS (SELECT 1 FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2)
                    INSERT INTO [asap].[PatronSettings]
                        ([OrganizationId], [LoginPrompt], [LoginNote], [SuggestionFormNote], [NoEmailMessage],
                         [SuccessMessage], [AlreadySubmittedMessage], [EbookMessage], [EaudiobookMessage], [UpdatedUtc])
                    VALUES (2, N'', N'', N'', N'', N'', N'', N'', N'', SYSUTCDATETIME());
                ELSE
                    UPDATE [asap].[PatronSettings]
                    SET [LoginPrompt] = N'', [LoginNote] = N'', [SuggestionFormNote] = N'',
                        [NoEmailMessage] = N'', [SuccessMessage] = N'', [AlreadySubmittedMessage] = N'',
                        [EbookMessage] = N'', [EaudiobookMessage] = N'', [UpdatedUtc] = SYSUTCDATETIME()
                    WHERE [OrganizationId] = 2;

                DELETE FROM [asap].[Branding] WHERE [OrganizationId] IN (1, 2);
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var client = factory!.CreateClient();
        var response = await client.GetAsync("/api/asap/config?libraryOrgId=2");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.AreEqual(
            "Please enter your information below to start the suggestion process.",
            root.GetProperty("loginPrompt").GetString());
        Assert.AreEqual(
            "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.",
            root.GetProperty("loginNote").GetString());
        Assert.AreEqual(
            "If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update.",
            root.GetProperty("suggestionFormNote").GetString());
        Assert.AreEqual(
            "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
            root.GetProperty("noEmailMessage").GetString());
        Assert.AreEqual(
            "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
            root.GetProperty("successMessage").GetString());
        Assert.AreEqual(
            "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>",
            root.GetProperty("alreadySubmittedMessage").GetString());
        Assert.AreEqual("/jpl.png", root.GetProperty("logoUrl").GetString());
        Assert.AreEqual("Library Logo", root.GetProperty("logoAlt").GetString());
        StringAssert.Contains(root.GetProperty("ebookMessage").GetString(), "help.libbyapp.com/en-us/6260.htm");
        StringAssert.Contains(root.GetProperty("eaudiobookMessage").GetString(), "This is an eAudiobook suggestion");
    }

    [TestMethod]
    public async Task DacpacSeedsPinnedSubmissionEmailTemplate()
    {
        const string expectedBody =
            "Hello {{name}},\n\n" +
            "Thank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\n" +
            "If we add this item, we will place a hold for you automatically and send another update.\n\n" +
            "Thank you for helping us shape the library collection.";
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT [SubjectTemplate], [BodyTemplate]
            FROM [asap].[EmailTemplate]
            WHERE [OrganizationId] = 1 AND [TemplateKey] = N'suggestion_submitted';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("Suggestion received: {{title}}", reader.GetString(0));
        Assert.AreEqual(expectedBody, reader.GetString(1));
    }

    [TestMethod]
    public async Task AdministrationSettingsSaveReadAndClearUsesLivePatronCodesAndSparseOverrides()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var initialResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
        Assert.AreEqual(HttpStatusCode.OK, initialResponse.StatusCode, await initialResponse.Content.ReadAsStringAsync());
        using var initialDocument = JsonDocument.Parse(await initialResponse.Content.ReadAsStringAsync());
        var initial = initialDocument.RootElement;
        var initialLibrary = initial.GetProperty("stored").GetProperty("libraryOverride");
        var originalWorkflowMessage = OptionalString(initialLibrary.GetProperty("workflow"), "suggestionLimitMessage");
        var originalLoginNote = OptionalString(initialLibrary.GetProperty("patron"), "loginNote");
        var originalCodes = initialLibrary.GetProperty("allowedPatronCodeIds");
        var originalCodesExist = originalCodes.GetProperty("exists").GetBoolean();
        var originalCodeValues = originalCodes.GetProperty("values")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        var systemEffectiveCodes = initial.GetProperty("effective").GetProperty("allowedPatronCodeIds")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        try
        {
            using var choicesResponse = await client.GetAsync("/api/asap/staff/polaris/patron-codes?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, choicesResponse.StatusCode, await choicesResponse.Content.ReadAsStringAsync());
            using var choicesDocument = JsonDocument.Parse(await choicesResponse.Content.ReadAsStringAsync());
            var choices = choicesDocument.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.IsTrue(choices.Any(item => item.GetProperty("id").GetString() == "1"));
            Assert.IsTrue(choices.All(item => item.GetProperty("id").ValueKind == JsonValueKind.String));

            using var saveResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = initial.GetProperty("version").GetString(),
                    workflow = new
                    {
                        suggestionLimitMessage = "Library-specific limit message",
                        allowedPatronCodeIds = new[] { "1" }
                    },
                    patron = new { loginNote = "Library-specific login note" }
                });
            Assert.AreEqual(HttpStatusCode.OK, saveResponse.StatusCode, await saveResponse.Content.ReadAsStringAsync());

            using var savedResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, savedResponse.StatusCode, await savedResponse.Content.ReadAsStringAsync());
            using var savedDocument = JsonDocument.Parse(await savedResponse.Content.ReadAsStringAsync());
            var saved = savedDocument.RootElement;
            Assert.AreEqual(
                "Library-specific limit message",
                saved.GetProperty("stored").GetProperty("libraryOverride").GetProperty("workflow")
                    .GetProperty("suggestionLimitMessage").GetString());
            Assert.AreEqual(
                "Library-specific login note",
                saved.GetProperty("stored").GetProperty("libraryOverride").GetProperty("patron")
                    .GetProperty("loginNote").GetString());
            CollectionAssert.AreEqual(
                new[] { "1" },
                saved.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                    .GetProperty("values").EnumerateArray().Select(item => item.GetString()).ToArray());

            using var invalidResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = saved.GetProperty("version").GetString(),
                    workflow = new { allowedPatronCodeIds = new[] { "999" } }
                });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode, await invalidResponse.Content.ReadAsStringAsync());
            using var invalidDocument = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("patron_code_unknown", invalidDocument.RootElement.GetProperty("code").GetString());

            using var afterInvalidResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, afterInvalidResponse.StatusCode, await afterInvalidResponse.Content.ReadAsStringAsync());
            using var afterInvalidDocument = JsonDocument.Parse(await afterInvalidResponse.Content.ReadAsStringAsync());
            var afterInvalid = afterInvalidDocument.RootElement;
            CollectionAssert.AreEqual(
                new[] { "1" },
                afterInvalid.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                    .GetProperty("values").EnumerateArray().Select(item => item.GetString()).ToArray());

            using var clearScalarResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = afterInvalid.GetProperty("version").GetString(),
                    workflow = new { suggestionLimitMessage = (string?)null }
                });
            Assert.AreEqual(HttpStatusCode.OK, clearScalarResponse.StatusCode, await clearScalarResponse.Content.ReadAsStringAsync());

            using var afterScalarClearResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, afterScalarClearResponse.StatusCode, await afterScalarClearResponse.Content.ReadAsStringAsync());
            using var afterScalarClearDocument = JsonDocument.Parse(await afterScalarClearResponse.Content.ReadAsStringAsync());
            var afterScalarClear = afterScalarClearDocument.RootElement;
            Assert.AreEqual(
                JsonValueKind.Null,
                afterScalarClear.GetProperty("stored").GetProperty("libraryOverride").GetProperty("workflow").ValueKind);
            Assert.AreEqual(
                "Library-specific login note",
                afterScalarClear.GetProperty("stored").GetProperty("libraryOverride").GetProperty("patron")
                    .GetProperty("loginNote").GetString());

            using var clearSetResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = afterScalarClear.GetProperty("version").GetString(),
                    workflow = new { allowedPatronCodeIds = Array.Empty<string>() }
                });
            Assert.AreEqual(HttpStatusCode.OK, clearSetResponse.StatusCode, await clearSetResponse.Content.ReadAsStringAsync());

            using var finalResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, finalResponse.StatusCode, await finalResponse.Content.ReadAsStringAsync());
            using var finalDocument = JsonDocument.Parse(await finalResponse.Content.ReadAsStringAsync());
            var final = finalDocument.RootElement;
            Assert.IsFalse(final.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                .GetProperty("exists").GetBoolean());
            CollectionAssert.AreEqual(
                systemEffectiveCodes,
                final.GetProperty("effective").GetProperty("allowedPatronCodeIds")
                    .EnumerateArray().Select(item => item.GetString()).ToArray());

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText =
                "SELECT (SELECT COUNT(*) FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = 2), " +
                "(SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 2);";
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(0, reader.GetInt32(0));
            Assert.AreEqual(0, reader.GetInt32(1));
        }
        finally
        {
            using var latestResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            if (latestResponse.IsSuccessStatusCode)
            {
                using var latestDocument = JsonDocument.Parse(await latestResponse.Content.ReadAsStringAsync());
                using var restoreResponse = await client.PostAsJsonAsync(
                    "/api/asap/staff/settings",
                    new
                    {
                        orgId = "2",
                        version = latestDocument.RootElement.GetProperty("version").GetString(),
                        workflow = new
                        {
                            suggestionLimitMessage = originalWorkflowMessage,
                            allowedPatronCodeIds = originalCodesExist ? originalCodeValues : Array.Empty<string>()
                        },
                        patron = new { loginNote = originalLoginNote }
                    });
                Assert.IsTrue(restoreResponse.IsSuccessStatusCode, await restoreResponse.Content.ReadAsStringAsync());
            }
        }

        static string? OptionalString(JsonElement parent, string propertyName) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Null
                ? value.GetString()
                : null;
    }

    [TestMethod]
    public async Task AdministrationInheritableScalarsSaveResolveAndResetPerField()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var libraryId = 91404;
        var templateKeyA = $"rejection:slice4_scalar_a_{Guid.NewGuid():N}";
        var templateKeyB = $"rejection:slice4_scalar_b_{Guid.NewGuid():N}";
        await UpsertTestOrganizationAsync(libraryId, "Slice 4 Scalar Library", "S4S");

        using var initialSystem = await ReadSettingsDocumentAsync(client, "system");
        var initialPayload = CaptureScalarRestorePayload(initialSystem.RootElement);

        try
        {
            using (var templateSave = await SaveSettingsDocumentAsync(
                       client,
                       initialSystem.RootElement,
                       "system",
                       new Dictionary<string, object?>
                       {
                           ["templates"] = new object[]
                           {
                               new Dictionary<string, object?>
                               {
                                   ["templateKey"] = templateKeyA,
                                   ["displayName"] = "Slice 4 scalar rejection A",
                                   ["subject"] = "Slice 4 scalar rejection A",
                                   ["body"] = "Slice 4 scalar rejection A body",
                                   ["enabled"] = true
                               },
                               new Dictionary<string, object?>
                               {
                                   ["templateKey"] = templateKeyB,
                                   ["displayName"] = "Slice 4 scalar rejection B",
                                   ["subject"] = "Slice 4 scalar rejection B",
                                   ["body"] = "Slice 4 scalar rejection B body",
                                   ["enabled"] = true
                               }
                           }
                       }))
            {
                Assert.AreEqual("saved", templateSave.RootElement.GetProperty("code").GetString());
            }

            using var withTemplates = await ReadSettingsDocumentAsync(client, "system");
            var templateIdA = FindTemplateId(withTemplates.RootElement, templateKeyA);
            var templateIdB = FindTemplateId(withTemplates.RootElement, templateKeyB);
            var fields = BuildScalarSettingCases(templateIdA, templateIdB);

            using (var saveSystem = await SaveSettingsDocumentAsync(
                       client,
                       withTemplates.RootElement,
                       "system",
                       ScalarPayload(fields, value => value.SystemValue)))
            {
                Assert.AreEqual("saved", saveSystem.RootElement.GetProperty("code").GetString());
            }

            using var savedSystem = await ReadSettingsDocumentAsync(client, "system");
            foreach (var field in fields)
            {
                var systemSection = savedSystem.RootElement.GetProperty("stored")
                    .GetProperty("configuredSystem")
                    .GetProperty(field.Section);
                AssertScalarEquals(field.SystemValue, systemSection, field.Name, $"system {field.Section}.{field.Name}");
            }

            using var initialLibraryForSave = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using (var saveLibrary = await SaveSettingsDocumentAsync(
                       client,
                       initialLibraryForSave.RootElement,
                       libraryId.ToString(),
                       ScalarPayload(fields, value => value.LibraryValue)))
            {
                Assert.AreEqual("saved", saveLibrary.RootElement.GetProperty("code").GetString());
            }

            using var savedLibrary = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            foreach (var field in fields)
            {
                var libraryOverride = savedLibrary.RootElement.GetProperty("stored")
                    .GetProperty("libraryOverride")
                    .GetProperty(field.Section);
                AssertScalarEquals(field.LibraryValue, libraryOverride, field.Name, $"library override {field.Section}.{field.Name}");
                AssertResolvedScalarEquals(field.LibraryValue, savedLibrary.RootElement, field, $"effective library {field.Section}.{field.Name}");
            }

            var latest = savedLibrary.RootElement.Clone();
            foreach (var field in fields)
            {
                var sentinel = fields.First(item => item.Section == field.Section && item.Name != field.Name);
                using var reset = await SaveSettingsDocumentAsync(
                    client,
                    latest,
                    libraryId.ToString(),
                    new Dictionary<string, object?>
                    {
                        [field.Section] = new Dictionary<string, object?>
                        {
                            [field.Name] = null,
                            [sentinel.Name] = sentinel.LibraryValue
                        }
                    });
                Assert.AreEqual("saved", reset.RootElement.GetProperty("code").GetString(), $"{field.Section}.{field.Name}");

                using var afterReset = await ReadSettingsDocumentAsync(client, libraryId.ToString());
                latest = afterReset.RootElement.Clone();
                var librarySection = afterReset.RootElement.GetProperty("stored")
                    .GetProperty("libraryOverride")
                    .GetProperty(field.Section);
                AssertScalarEquals(null, librarySection, field.Name, $"cleared override {field.Section}.{field.Name}");
                AssertResolvedScalarEquals(field.SystemValue, afterReset.RootElement, field, $"fallback {field.Section}.{field.Name}");
                AssertScalarEquals(sentinel.LibraryValue, librarySection, sentinel.Name, $"preserved peer override {field.Section}.{sentinel.Name}");
            }

            using var secretBase = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using (var saveLibrarySecret = await SaveSettingsDocumentAsync(
                       client,
                       secretBase.RootElement,
                       libraryId.ToString(),
                       new Dictionary<string, object?>
                       {
                           ["email"] = new Dictionary<string, object?>
                           {
                               ["fromAddress"] = "slice4-secret-sentinel@example.org",
                               ["postmarkToken"] = "slice4-library-secret-token"
                           }
                       }))
            {
                Assert.AreEqual("saved", saveLibrarySecret.RootElement.GetProperty("code").GetString());
            }

            using var withSecret = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            AssertScalarEquals(true, withSecret.RootElement.GetProperty("stored")
                .GetProperty("libraryOverride").GetProperty("email"), "hasPostmarkToken", "library secret saved");
            using var clearSecret = await SaveSettingsDocumentAsync(
                client,
                withSecret.RootElement,
                libraryId.ToString(),
                new Dictionary<string, object?>
                {
                    ["email"] = new Dictionary<string, object?>
                    {
                        ["clearPostmarkToken"] = true,
                        ["fromAddress"] = "slice4-secret-sentinel@example.org"
                    }
                });
            Assert.AreEqual("saved", clearSecret.RootElement.GetProperty("code").GetString());

            using var afterSecretClear = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var emailOverride = afterSecretClear.RootElement.GetProperty("stored")
                .GetProperty("libraryOverride")
                .GetProperty("email");
            AssertScalarEquals(false, emailOverride, "hasPostmarkToken", "library secret cleared");
            AssertScalarEquals("slice4-secret-sentinel@example.org", emailOverride, "fromAddress", "sender override preserved beside secret clear");
        }
        finally
        {
            using var latestSystem = await ReadSettingsDocumentAsync(client, "system");
            using (var restore = await SaveSettingsDocumentAsync(
                       client,
                       latestSystem.RootElement,
                       "system",
                       initialPayload))
            {
                Assert.AreEqual("saved", restore.RootElement.GetProperty("code").GetString());
            }

            await CleanupScalarSettingsTestDataAsync(libraryId, templateKeyA, templateKeyB);
        }
    }

    [TestMethod]
    public async Task AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole()
    {
        using var client = factory!.CreateClient();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var tenantId = Guid.Parse(identity.TenantId!);
        var objectId = Guid.NewGuid();
        long adminId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                VALUES (@tenantId, @objectId, N'scope.admin@example.org', N'SCOPE.ADMIN@EXAMPLE.ORG',
                        N'Scope Administrator', N'scope.admin@example.org', N'admin', 2, 1);
                SELECT CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@tenantId", tenantId);
            seed.Parameters.AddWithValue("@objectId", objectId);
            adminId = Convert.ToInt64(await seed.ExecuteScalarAsync());
        }

        try
        {
            AddTestingStaffHeaders(client, adminId, tenantId, "SCOPE.ADMIN@EXAMPLE.ORG");
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using (var ownSettings = await client.GetAsync("/api/asap/staff/settings?orgId=2"))
            {
                Assert.AreEqual(HttpStatusCode.OK, ownSettings.StatusCode, await ownSettings.Content.ReadAsStringAsync());
            }

            using (var systemSettings = await client.GetAsync("/api/asap/staff/settings?orgId=system"))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, systemSettings.StatusCode, await systemSettings.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await systemSettings.Content.ReadAsStringAsync());
                Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
            }

            using (var systemMutation = await client.PostAsJsonAsync(
                       "/api/asap/staff/settings",
                       new
                       {
                           orgId = "system",
                           version = "admin-cannot-use-this",
                           systemSettings = new { staffUrl = "https://forbidden.example.org/staff" }
                       }))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, systemMutation.StatusCode, await systemMutation.Content.ReadAsStringAsync());
            }

            using (var ownAudit = await client.GetAsync("/api/asap/staff/audit?organizationId=2"))
            {
                Assert.AreEqual(HttpStatusCode.OK, ownAudit.StatusCode, await ownAudit.Content.ReadAsStringAsync());
            }

            using (var otherAudit = await client.GetAsync("/api/asap/staff/audit?organizationId=3"))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, otherAudit.StatusCode, await otherAudit.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await otherAudit.Content.ReadAsStringAsync());
                Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
            }
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand(
                "DELETE FROM [asap].[StaffUser] WHERE [Id] = @id;",
                connection);
            cleanup.Parameters.AddWithValue("@id", adminId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task AdministrationPatronCodeProviderFailureDoesNotPartiallyWrite()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        await using var failingFactory = CreateApplicationFactory(
            configurationPath,
            new FailingPatronCodeReferenceProvider());
        using var client = failingFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var settingsResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
        Assert.AreEqual(HttpStatusCode.OK, settingsResponse.StatusCode, await settingsResponse.Content.ReadAsStringAsync());
        using var settingsDocument = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync());
        var version = settingsDocument.RootElement.GetProperty("version").GetString();
        var before = await ReadPatronCodeRowsAsync(2);

        using var saveResponse = await client.PostAsJsonAsync(
            "/api/asap/staff/settings",
            new
            {
                orgId = "2",
                version,
                workflow = new { allowedPatronCodeIds = new[] { "1" } }
            });
        Assert.AreEqual(HttpStatusCode.BadGateway, saveResponse.StatusCode, await saveResponse.Content.ReadAsStringAsync());
        using var errorDocument = JsonDocument.Parse(await saveResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("patron_codes_unavailable", errorDocument.RootElement.GetProperty("code").GetString());
        var after = await ReadPatronCodeRowsAsync(2);
        Assert.AreEqual(before.SetCount, after.SetCount);
        CollectionAssert.AreEqual(before.Values, after.Values);
    }

    [TestMethod]
    public async Task StaffSessionUsesCurrentEmailIdentityAndTestingAuthOnlyInTesting()
    {
        using var anonymousClient = factory!.CreateClient();
        using var anonymousResponse = await anonymousClient.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, anonymousResponse.StatusCode);
        using (var body = JsonDocument.Parse(await anonymousResponse.Content.ReadAsStringAsync()))
        {
            Assert.IsFalse(body.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.IsFalse(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("antiforgeryToken").GetString()));
        }

        var tenantId = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!;
        long staffUserId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @email;",
                connection);
            command.Parameters.AddWithValue("@email", "ADMIN@EXAMPLE.ORG");
            staffUserId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        using var staffClient = factory.CreateClient();
        staffClient.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", staffUserId.ToString());
        staffClient.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", tenantId);
        staffClient.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", "admin@example.org");
        using var staffResponse = await staffClient.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, staffResponse.StatusCode);
        using (var body = JsonDocument.Parse(await staffResponse.Content.ReadAsStringAsync()))
        {
            Assert.IsTrue(body.RootElement.GetProperty("authenticated").GetBoolean());
            Assert.AreEqual("super_admin", body.RootElement.GetProperty("staff").GetProperty("role").GetString());
            Assert.AreEqual(staffUserId.ToString(), body.RootElement.GetProperty("staff").GetProperty("id").GetString());
        }

        staffClient.DefaultRequestHeaders.Remove("X-ASAP-Test-Staff-Email");
        staffClient.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", "wrong@example.org");
        using var reboundResponse = await staffClient.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.Unauthorized, reboundResponse.StatusCode);
        using var reboundBody = JsonDocument.Parse(await reboundResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("staff_session_invalid", reboundBody.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaffCookieSurvivesRestartAndOidChangeButNotEmailChangeOrTenantRemoval()
    {
        await factory!.DisposeAsync();
        factory = null;
        var configuredIdentity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var baseTenantId = Guid.Parse(configuredIdentity.TenantId!);
        var staffTenantId = Guid.NewGuid();
        var originalObjectId = Guid.NewGuid();
        var reboundObjectId = Guid.NewGuid();
        var initialPath = await WriteStaffConfigurationAsync(
            $"cookie-initial-{Guid.NewGuid():N}.json",
            [baseTenantId, staffTenantId]);

        long staffId;
        string originalCookie;
        await using (var firstFactory = CreateApplicationFactory(initialPath))
        {
            using var startup = firstFactory.CreateClient();
            Assert.AreEqual(HttpStatusCode.OK, (await startup.GetAsync("/api/asap/staff/session")).StatusCode);
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText =
                    """
                    INSERT INTO [asap].[StaffUser]
                        ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                         [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                    VALUES
                        (@tenantId, @objectId, N'restart.staff@example.org', N'RESTART.STAFF@EXAMPLE.ORG',
                         N'Restart Staff', N'restart.notify@example.org', N'staff', 2, 1);
                    SELECT CONVERT(bigint, SCOPE_IDENTITY());
                    """;
                seed.Parameters.AddWithValue("@tenantId", staffTenantId);
                seed.Parameters.AddWithValue("@objectId", originalObjectId);
                staffId = Convert.ToInt64(await seed.ExecuteScalarAsync());
            }

            originalCookie = ProtectStaffCookie(firstFactory, staffId, staffTenantId, "RESTART.STAFF@EXAMPLE.ORG");
            using var firstClient = CookieClient(firstFactory, originalCookie);
            using var firstSession = await firstClient.GetAsync("/api/asap/staff/session");
            Assert.AreEqual(HttpStatusCode.OK, firstSession.StatusCode, await firstSession.Content.ReadAsStringAsync());
            using var firstBody = JsonDocument.Parse(await firstSession.Content.ReadAsStringAsync());
            Assert.IsTrue(firstBody.RootElement.GetProperty("authenticated").GetBoolean());
        }

        string renamedCookie;
        await using (var restartedFactory = CreateApplicationFactory(initialPath))
        {
            using (var restartedClient = CookieClient(restartedFactory, originalCookie))
            using (var restartedSession = await restartedClient.GetAsync("/api/asap/staff/session"))
            {
                Assert.AreEqual(HttpStatusCode.OK, restartedSession.StatusCode, await restartedSession.Content.ReadAsStringAsync());
                using var restartedBody = JsonDocument.Parse(await restartedSession.Content.ReadAsStringAsync());
                Assert.IsTrue(restartedBody.RootElement.GetProperty("authenticated").GetBoolean());
            }

            await restartedFactory.Services.GetRequiredService<StaffSignInService>()
                .RecordSuccessfulSignInAsync(
                    staffId,
                    "RESTART.STAFF@EXAMPLE.ORG",
                    staffTenantId,
                    reboundObjectId,
                    " Refreshed Staff ",
                    CancellationToken.None);
            using (var refreshedClient = CookieClient(restartedFactory, originalCookie))
            using (var refreshedSession = await refreshedClient.GetAsync("/api/asap/staff/session"))
            {
                Assert.AreEqual(HttpStatusCode.OK, refreshedSession.StatusCode, await refreshedSession.Content.ReadAsStringAsync());
                using var refreshedBody = JsonDocument.Parse(await refreshedSession.Content.ReadAsStringAsync());
                var refreshed = refreshedBody.RootElement.GetProperty("staff");
                Assert.AreEqual("restart.staff@example.org", refreshed.GetProperty("userPrincipalName").GetString());
                Assert.AreEqual("Refreshed Staff", refreshed.GetProperty("displayName").GetString());
                Assert.AreEqual("restart.notify@example.org", refreshed.GetProperty("notificationEmail").GetString());
            }

            using (var oidChangedClient = CookieClient(restartedFactory, originalCookie))
            using (var oidChangedSession = await oidChangedClient.GetAsync("/api/asap/staff/session"))
            {
                Assert.AreEqual(HttpStatusCode.OK, oidChangedSession.StatusCode, await oidChangedSession.Content.ReadAsStringAsync());
            }

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var rename = new SqlCommand(
                    "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = N'renamed.staff@example.org', [NormalizedUserPrincipalName] = N'RENAMED.STAFF@EXAMPLE.ORG' WHERE [Id] = @staffId;",
                    connection);
                rename.Parameters.AddWithValue("@staffId", staffId);
                Assert.AreEqual(1, await rename.ExecuteNonQueryAsync());
            }

            using (var oldEmailClient = CookieClient(restartedFactory, originalCookie))
            using (var oldEmailSession = await oldEmailClient.GetAsync("/api/asap/staff/session"))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, oldEmailSession.StatusCode);
                using var body = JsonDocument.Parse(await oldEmailSession.Content.ReadAsStringAsync());
                Assert.AreEqual("staff_session_invalid", body.RootElement.GetProperty("code").GetString());
                Assert.IsTrue(oldEmailSession.Headers.TryGetValues("Set-Cookie", out var clearedCookies));
                Assert.IsTrue(clearedCookies.Any(value => value.StartsWith("__Host-ASAP.Staff=", StringComparison.Ordinal)));
            }

            renamedCookie = ProtectStaffCookie(restartedFactory, staffId, staffTenantId, "RENAMED.STAFF@EXAMPLE.ORG");
            using var renamedClient = CookieClient(restartedFactory, renamedCookie);
            using var renamedSession = await renamedClient.GetAsync("/api/asap/staff/session");
            Assert.AreEqual(HttpStatusCode.OK, renamedSession.StatusCode, await renamedSession.Content.ReadAsStringAsync());
        }

        var removedTenantPath = await WriteStaffConfigurationAsync(
            $"cookie-removed-{Guid.NewGuid():N}.json",
            [baseTenantId]);
        await using var removedTenantFactory = CreateApplicationFactory(removedTenantPath);
        using var removedTenantClient = CookieClient(removedTenantFactory, renamedCookie);
        using var removedTenantSession = await removedTenantClient.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.Unauthorized, removedTenantSession.StatusCode);
        using var removedBody = JsonDocument.Parse(await removedTenantSession.Content.ReadAsStringAsync());
        Assert.AreEqual("staff_session_invalid", removedBody.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaffStartupUsesEmailIdentityWithoutRepairingObservedEntraMetadataForNewTenantPolicy()
    {
        using (var baselineClient = factory!.CreateClient())
        using (var baselineSession = await baselineClient.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, baselineSession.StatusCode);
        }

        var isolatedTenantId = Guid.NewGuid();
        var isolatedObjectId = Guid.NewGuid();
        var settingsPath = await WriteStaffConfigurationAsync(
            $"staff-policy-fail-closed-{Guid.NewGuid():N}.json",
            [isolatedTenantId],
            isolatedTenantId,
            isolatedObjectId);

        int countBefore;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var count = new SqlCommand("SELECT COUNT(*) FROM [asap].[StaffUser];", connection);
            countBefore = Convert.ToInt32(await count.ExecuteScalarAsync());
            Assert.IsGreaterThan(0, countBefore);
        }

        await using var isolatedFactory = CreateApplicationFactory(settingsPath);
        using var client = isolatedFactory.CreateClient();
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var business = await client.GetAsync("/api/asap/config");
        using var staffShell = await client.GetAsync("/staff/");

        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, business.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, staffShell.StatusCode);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var verify = new SqlCommand(
                "SELECT COUNT(*), COUNT(CASE WHEN [EntraObjectId] = @objectId THEN 1 END) FROM [asap].[StaffUser];",
                connection);
            verify.Parameters.AddWithValue("@objectId", isolatedObjectId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(countBefore, reader.GetInt32(0));
            Assert.AreEqual(0, reader.GetInt32(1));
        }
    }

    [TestMethod]
    public async Task StaffAuthenticationEmailChangeRejectsDuplicatesAndAuditsMetadataUpdate()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        long actorId;
        long targetId;
        string targetVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @actorEmail);
                INSERT INTO [asap].[StaffUser]
                    ([UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                VALUES (N'email.target@example.org', N'EMAIL.TARGET@EXAMPLE.ORG',
                        N'Email Target', N'email.target@example.org', N'staff', 2, 1);
                DECLARE @targetId bigint = SCOPE_IDENTITY();
                SELECT @actorId, @targetId, [RowVersion] FROM [asap].[StaffUser] WHERE [Id] = @targetId;
                """;
            seed.Parameters.AddWithValue("@actorEmail", identity.UserPrincipalName!.ToUpperInvariant());
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            targetId = reader.GetInt64(1);
            targetVersion = StaffVersion.Encode((byte[])reader[2]);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var duplicate = await client.PatchAsJsonAsync(
            $"/api/asap/staff/users/{targetId}",
            new
            {
                version = targetVersion,
                email = identity.UserPrincipalName,
                displayName = "Email Target",
                notificationEmail = "email.target@example.org"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());
        using (var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("identity_already_exists", duplicateBody.RootElement.GetProperty("code").GetString());
        }

        using var changed = await client.PatchAsJsonAsync(
            $"/api/asap/staff/users/{targetId}",
            new
            {
                version = targetVersion,
                email = "changed.email@example.org",
                displayName = "Changed Email Target",
                notificationEmail = "separate-notification@example.org"
            });
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        using var changedBody = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.AreEqual("changed.email@example.org", changedBody.RootElement.GetProperty("user").GetProperty("userPrincipalName").GetString());
        Assert.IsFalse(changedBody.RootElement.GetProperty("user").TryGetProperty("objectId", out _));

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT target.[UserPrincipalName], target.[NormalizedUserPrincipalName], target.[NotificationEmail],
                   target.[EntraTenantId], target.[EntraObjectId], audit.[ActorStaffUserId], audit.[Action]
            FROM [asap].[StaffUser] target
            JOIN [asap].[AdministrativeAudit] audit
              ON audit.[TargetType] = N'StaffUser' AND audit.[TargetId] = CONVERT(nvarchar(40), target.[Id])
            WHERE target.[Id] = @targetId AND audit.[Action] = N'staff_metadata_updated';
            """,
            verify);
        command.Parameters.AddWithValue("@targetId", targetId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("changed.email@example.org", result.GetString(0));
        Assert.AreEqual("CHANGED.EMAIL@EXAMPLE.ORG", result.GetString(1));
        Assert.AreEqual("separate-notification@example.org", result.GetString(2));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual(actorId, result.GetInt64(5));
        Assert.AreEqual("staff_metadata_updated", result.GetString(6));
    }

    [TestMethod]
    public async Task StaffProfilePersistsAllPreferencesAndEnforcesAntiforgeryAndVersion()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        long staffUserId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG';",
                connection);
            command.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            staffUserId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", staffUserId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);

        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        var token = sessionBody.RootElement.GetProperty("antiforgeryToken").GetString()!;
        var version = sessionBody.RootElement.GetProperty("staff").GetProperty("version").GetString()!;

        using var missingToken = await client.PostAsJsonAsync(
            "/api/asap/staff/profile",
            new
            {
                version,
                weeklyActionSummaryEnabled = true,
                weeklyActionSummaryEmail = "weekly@example.org",
                purchaseReminderDefault = true,
                additionalCopyReminderDefault = true,
                defaultMineUnclaimedFilter = true,
                notificationEmail = "must-not-be-accepted@example.org"
            });
        Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);

        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", token);
        using var update = await client.PostAsJsonAsync(
            "/api/asap/staff/profile",
            new
            {
                version,
                weeklyActionSummaryEnabled = true,
                weeklyActionSummaryEmail = " weekly@example.org ",
                purchaseReminderDefault = true,
                additionalCopyReminderDefault = false,
                defaultMineUnclaimedFilter = true
            });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var updateBody = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        var updated = updateBody.RootElement.GetProperty("staff");
        Assert.IsTrue(updated.GetProperty("weeklyActionSummaryEnabled").GetBoolean());
        Assert.AreEqual("weekly@example.org", updated.GetProperty("weeklyActionSummaryEmail").GetString());
        Assert.IsTrue(updated.GetProperty("purchaseReminderDefault").GetBoolean());
        Assert.IsFalse(updated.GetProperty("additionalCopyReminderDefault").GetBoolean());
        Assert.IsTrue(updated.GetProperty("defaultMineUnclaimedFilter").GetBoolean());

        using var clearWeekly = await client.PostAsJsonAsync(
            "/api/asap/staff/profile",
            new
            {
                version = updated.GetProperty("version").GetString(),
                weeklyActionSummaryEnabled = true,
                weeklyActionSummaryEmail = (string?)null,
                purchaseReminderDefault = true,
                additionalCopyReminderDefault = false,
                defaultMineUnclaimedFilter = true
            });
        Assert.AreEqual(HttpStatusCode.OK, clearWeekly.StatusCode, await clearWeekly.Content.ReadAsStringAsync());
        using var clearWeeklyBody = JsonDocument.Parse(await clearWeekly.Content.ReadAsStringAsync());
        var cleared = clearWeeklyBody.RootElement.GetProperty("staff");
        Assert.IsTrue(cleared.GetProperty("weeklyActionSummaryEnabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, cleared.GetProperty("weeklyActionSummaryEmail").ValueKind);

        using var stale = await client.PostAsJsonAsync(
            "/api/asap/staff/profile",
            new
            {
                version,
                weeklyActionSummaryEnabled = false,
                weeklyActionSummaryEmail = "stale@example.org",
                purchaseReminderDefault = false,
                additionalCopyReminderDefault = true,
                defaultMineUnclaimedFilter = false
            });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        using var placeholder = await client.PostAsJsonAsync(
            "/api/asap/staff/profile",
            new
            {
                version = cleared.GetProperty("version").GetString(),
                weeklyActionSummaryEnabled = true,
                weeklyActionSummaryEmail = "legacy@staff.asap.local",
                purchaseReminderDefault = false,
                additionalCopyReminderDefault = false,
                defaultMineUnclaimedFilter = false
            });
        Assert.AreEqual(HttpStatusCode.BadRequest, placeholder.StatusCode);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var verifyCommand = new SqlCommand(
            "SELECT [NotificationEmail], [WeeklyActionSummaryEmail], [PurchaseReminderDefault] FROM [asap].[StaffUser] WHERE [Id] = @id;",
            verify);
        verifyCommand.Parameters.AddWithValue("@id", staffUserId);
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("admin@example.org", reader.GetString(0));
        Assert.IsTrue(reader.IsDBNull(1));
        Assert.IsTrue(reader.GetBoolean(2));
    }

    [TestMethod]
    public async Task StaffLifecycleClearsOnlyOpenOutOfScopeRelationshipsAndRetainsHistory()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        long actorId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG';",
                connection);
            actorId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        var newTenantId = Guid.Parse(identity.TenantId!);
        var newObjectId = Guid.NewGuid();
        using var create = await client.PostAsJsonAsync(
            "/api/asap/staff/users",
            new
            {
                email = "workflow.staff@example.org",
                role = "staff",
                organizationId = 2
            });
        Assert.AreEqual(HttpStatusCode.Created, create.StatusCode, await create.Content.ReadAsStringAsync());
        using var createBody = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var created = createBody.RootElement.GetProperty("user");
        var staffId = long.Parse(created.GetProperty("id").GetString()!);
        var version = created.GetProperty("version").GetString();

        using var clearPrimary = await client.PatchAsJsonAsync(
            $"/api/asap/staff/users/{staffId}",
            new
            {
                version,
                email = "workflow.staff@example.org",
                displayName = "Workflow Staff",
                notificationEmail = (string?)null
            });
        Assert.AreEqual(HttpStatusCode.OK, clearPrimary.StatusCode, await clearPrimary.Content.ReadAsStringAsync());
        using var clearBody = JsonDocument.Parse(await clearPrimary.Content.ReadAsStringAsync());
        version = clearBody.RootElement.GetProperty("user").GetProperty("version").GetString();

        await factory.Services.GetRequiredService<StaffSignInService>()
            .RecordSuccessfulSignInAsync(
                staffId,
                "WORKFLOW.STAFF@EXAMPLE.ORG",
                newTenantId,
                newObjectId,
                " Refreshed Workflow Staff ",
                CancellationToken.None);
        using (var refreshedUsers = await client.GetAsync("/api/asap/staff/users?orgId=2"))
        {
            Assert.AreEqual(HttpStatusCode.OK, refreshedUsers.StatusCode, await refreshedUsers.Content.ReadAsStringAsync());
            using var refreshedUsersBody = JsonDocument.Parse(await refreshedUsers.Content.ReadAsStringAsync());
            var refreshed = refreshedUsersBody.RootElement.GetProperty("users").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == staffId.ToString());
            Assert.AreEqual("workflow.staff@example.org", refreshed.GetProperty("userPrincipalName").GetString());
            Assert.AreEqual("Refreshed Workflow Staff", refreshed.GetProperty("displayName").GetString());
            Assert.AreEqual(JsonValueKind.Null, refreshed.GetProperty("notificationEmail").ValueKind);
            version = refreshed.GetProperty("version").GetString();
        }

        long openRequestId;
        long closedRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[FormatAutoClaimRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
                VALUES (2, @formatId, @staffId, 1, SYSUTCDATETIME());

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002001', N'Open lifecycle request', 1, @formatId, N'suggestion',
                        @staffId, N'Workflow Staff', SYSUTCDATETIME(), N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """;
            command.Parameters.AddWithValue("@staffId", staffId);
            openRequestId = Convert.ToInt64(await command.ExecuteScalarAsync());

            command.CommandText =
                """
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002002', N'Closed lifecycle request', 1, @formatId, N'closed', N'manual',
                        @staffId, N'Workflow Staff', SYSUTCDATETIME(), N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """;
            closedRequestId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        using var deactivateRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{staffId}")
        {
            Content = JsonContent.Create(new { version })
        };
        using var deactivate = await client.SendAsync(deactivateRequest);
        Assert.AreEqual(HttpStatusCode.OK, deactivate.StatusCode, await deactivate.Content.ReadAsStringAsync());
        using var deactivateBody = JsonDocument.Parse(await deactivate.Content.ReadAsStringAsync());
        Assert.AreEqual(1, deactivateBody.RootElement.GetProperty("cleanup").GetProperty("rulesDeactivated").GetInt32());
        Assert.AreEqual(1, deactivateBody.RootElement.GetProperty("cleanup").GetProperty("openTitleClaimsCleared").GetInt32());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            """
            SELECT
                (SELECT [IsActive] FROM [asap].[StaffUser] WHERE [Id] = @staffId),
                (SELECT [NotificationEmail] FROM [asap].[StaffUser] WHERE [Id] = @staffId),
                (SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule] WHERE [StaffUserId] = @staffId AND [IsActive] = 1),
                (SELECT [ClaimedByStaffUserId] FROM [asap].[TitleRequest] WHERE [Id] = @openId),
                (SELECT [ClaimedByStaffUserId] FROM [asap].[TitleRequest] WHERE [Id] = @closedId),
                (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @openId AND [EventType] = N'claim_cleared'),
                (SELECT COUNT(*) FROM [asap].[AdministrativeAudit] WHERE [TargetId] = CONVERT(nvarchar(40), @staffId));
            """;
        verifyCommand.Parameters.AddWithValue("@staffId", staffId);
        verifyCommand.Parameters.AddWithValue("@openId", openRequestId);
        verifyCommand.Parameters.AddWithValue("@closedId", closedRequestId);
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsFalse(reader.GetBoolean(0));
        Assert.IsTrue(reader.IsDBNull(1));
        Assert.AreEqual(0, reader.GetInt32(2));
        Assert.IsTrue(reader.IsDBNull(3));
        Assert.AreEqual(staffId, reader.GetInt64(4));
        Assert.AreEqual(1, reader.GetInt32(5));
        Assert.IsGreaterThanOrEqualTo(3, reader.GetInt32(6));
    }

    [TestMethod]
    public async Task StaffRoleContractionRetainsInScopeWorkAndClearsOnlyOutOfScopeOperations()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        var targetObjectId = Guid.NewGuid();
        long actorId;
        long targetId;
        string targetVersion;
        long inScopeRequestId;
        long outOfScopeRequestId;
        long closedRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 91320)
                    INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                    VALUES (91320, N'Role contraction library', N'RCL', 1);
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                VALUES (@tenantId, @targetObjectId, N'contracting.admin@example.org', N'CONTRACTING.ADMIN@EXAMPLE.ORG',
                        N'Contracting Administrator', N'contracting.admin@example.org', N'super_admin', 1, 1);
                DECLARE @targetId bigint = SCOPE_IDENTITY();
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[FormatAutoClaimRule]
                    ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
                VALUES
                    (2, @formatId, @targetId, 1, SYSUTCDATETIME()),
                    (91320, @formatId, @targetId, 1, SYSUTCDATETIME());
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002320', N'In scope retained request', 0, @formatId, N'suggestion',
                        @targetId, N'Contracting Administrator', SYSUTCDATETIME(), N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @inScopeId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES (91320, N'20000000002321', N'Out of scope cleared request', 0, @formatId, N'suggestion',
                        @targetId, N'Contracting Administrator', SYSUTCDATETIME(), N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @outOfScopeId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason],
                     [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [CreatedUtc], [UpdatedUtc])
                VALUES (91320, N'20000000002322', N'Closed attribution retained request', 0, @formatId, N'closed', N'manual',
                        @targetId, N'Contracting Administrator', SYSUTCDATETIME(), N'manual', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @closedId bigint = SCOPE_IDENTITY();
                SELECT @actorId, @targetId, target.[RowVersion], @inScopeId, @outOfScopeId, @closedId
                FROM [asap].[StaffUser] target WHERE target.[Id] = @targetId;
                """;
            seed.Parameters.AddWithValue("@actorObjectId", Guid.Parse(identity.ObjectId!));
            seed.Parameters.AddWithValue("@tenantId", Guid.Parse(identity.TenantId!));
            seed.Parameters.AddWithValue("@targetObjectId", targetObjectId);
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            targetId = reader.GetInt64(1);
            targetVersion = StaffVersion.Encode((byte[])reader[2]);
            inScopeRequestId = reader.GetInt64(3);
            outOfScopeRequestId = reader.GetInt64(4);
            closedRequestId = reader.GetInt64(5);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var change = await client.PostAsJsonAsync(
            $"/api/asap/staff/users/{targetId}/role",
            new { version = targetVersion, role = "admin", organizationId = 2 });
        Assert.AreEqual(HttpStatusCode.OK, change.StatusCode, await change.Content.ReadAsStringAsync());
        using var changeBody = JsonDocument.Parse(await change.Content.ReadAsStringAsync());
        Assert.AreEqual(1, changeBody.RootElement.GetProperty("cleanup").GetProperty("rulesDeactivated").GetInt32());
        Assert.AreEqual(1, changeBody.RootElement.GetProperty("cleanup").GetProperty("openTitleClaimsCleared").GetInt32());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT target.[Role], target.[OrganizationId], target.[IsActive],
                   (SELECT [IsActive] FROM [asap].[FormatAutoClaimRule]
                    WHERE [StaffUserId] = @targetId AND [LibraryOrganizationId] = 2),
                   (SELECT [IsActive] FROM [asap].[FormatAutoClaimRule]
                    WHERE [StaffUserId] = @targetId AND [LibraryOrganizationId] = 91320),
                   (SELECT [ClaimedByStaffUserId] FROM [asap].[TitleRequest] WHERE [Id] = @inScopeId),
                   (SELECT [ClaimedByStaffUserId] FROM [asap].[TitleRequest] WHERE [Id] = @outOfScopeId),
                   (SELECT [ClaimedByDisplayName] FROM [asap].[TitleRequest] WHERE [Id] = @outOfScopeId),
                   (SELECT [ClaimType] FROM [asap].[TitleRequest] WHERE [Id] = @outOfScopeId),
                   (SELECT [ClaimedByStaffUserId] FROM [asap].[TitleRequest] WHERE [Id] = @closedId),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @outOfScopeId AND [EventType] = N'claim_cleared'),
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]
                    WHERE [TargetId] = CONVERT(nvarchar(40), @targetId) AND [Action] = N'staff_lifecycle_updated')
            FROM [asap].[StaffUser] target WHERE target.[Id] = @targetId;
            """,
            verify);
        command.Parameters.AddWithValue("@targetId", targetId);
        command.Parameters.AddWithValue("@inScopeId", inScopeRequestId);
        command.Parameters.AddWithValue("@outOfScopeId", outOfScopeRequestId);
        command.Parameters.AddWithValue("@closedId", closedRequestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("admin", result.GetString(0));
        Assert.AreEqual(2, result.GetInt32(1));
        Assert.IsTrue(result.GetBoolean(2));
        Assert.IsTrue(result.GetBoolean(3));
        Assert.IsFalse(result.GetBoolean(4));
        Assert.AreEqual(targetId, result.GetInt64(5));
        Assert.IsTrue(result.IsDBNull(6));
        Assert.IsTrue(result.IsDBNull(7));
        Assert.IsTrue(result.IsDBNull(8));
        Assert.AreEqual(targetId, result.GetInt64(9));
        Assert.AreEqual(1, result.GetInt32(10));
        Assert.AreEqual(1, result.GetInt32(11));
    }

    [TestMethod]
    public async Task StaffFinalTwoSuperAdminDeactivationsSerializeAndRejectTheLoser()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var configured = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var tenantId = Guid.Parse(configured.TenantId!);
        var secondObjectId = Guid.NewGuid();
        long firstId;
        long secondId;
        string firstVersion;
        string secondVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @firstId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = @firstEmail);
                UPDATE [asap].[StaffUser]
                SET [IsActive] = 1, [Role] = N'super_admin', [OrganizationId] = 1
                WHERE [Id] = @firstId;
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                VALUES (@tenantId, @secondObjectId, N'second.final.admin@example.org', N'SECOND.FINAL.ADMIN@EXAMPLE.ORG',
                        N'Second Final Admin', N'second.final.admin@example.org', N'super_admin', 1, 1);
                DECLARE @secondId bigint = SCOPE_IDENTITY();
                SELECT @firstId, @secondId, firstAdmin.[RowVersion], secondAdmin.[RowVersion]
                FROM [asap].[StaffUser] firstAdmin
                JOIN [asap].[StaffUser] secondAdmin ON secondAdmin.[Id] = @secondId
                WHERE firstAdmin.[Id] = @firstId;
                """;
            seed.Parameters.AddWithValue("@firstEmail", configured.UserPrincipalName!.ToUpperInvariant());
            seed.Parameters.AddWithValue("@tenantId", tenantId);
            seed.Parameters.AddWithValue("@secondObjectId", secondObjectId);
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            firstId = reader.GetInt64(0);
            secondId = reader.GetInt64(1);
            firstVersion = StaffVersion.Encode((byte[])reader[2]);
            secondVersion = StaffVersion.Encode((byte[])reader[3]);
        }

        try
        {
            using var firstClient = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            AddTestingStaffHeaders(firstClient, firstId, tenantId, configured.UserPrincipalName!);
            AddTestingStaffHeaders(secondClient, secondId, tenantId, "second.final.admin@example.org");
            firstClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(firstClient));
            secondClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(secondClient));
            using var firstRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{firstId}")
            {
                Content = JsonContent.Create(new { version = firstVersion })
            };
            using var secondRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/users/{secondId}")
            {
                Content = JsonContent.Create(new { version = secondVersion })
            };

            var responses = await Task.WhenAll(
                firstClient.SendAsync(firstRequest),
                secondClient.SendAsync(secondRequest));
            using var firstResponse = responses[0];
            using var secondResponse = responses[1];
            CollectionAssert.AreEquivalent(
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
                responses.Select(item => item.StatusCode).ToArray());
            var conflict = responses.Single(item => item.StatusCode == HttpStatusCode.Conflict);
            using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.AreEqual("active_super_admin_required", conflictBody.RootElement.GetProperty("code").GetString());

            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var count = new SqlCommand(
                """
                SELECT COUNT(*)
                FROM [asap].[StaffUser]
                WHERE [Id] IN (@firstId, @secondId) AND [IsActive] = 1
                  AND [Role] = N'super_admin' AND [OrganizationId] = 1;
                """,
                verify);
            count.Parameters.AddWithValue("@firstId", firstId);
            count.Parameters.AddWithValue("@secondId", secondId);
            Assert.AreEqual(1, Convert.ToInt32(await count.ExecuteScalarAsync()));
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var restore = connection.CreateCommand();
            restore.CommandText =
                """
                UPDATE [asap].[StaffUser]
                SET [IsActive] = 1, [Role] = N'super_admin', [OrganizationId] = 1
                WHERE [Id] = @firstId;
                UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @secondId;
                """;
            restore.Parameters.AddWithValue("@firstId", firstId);
            restore.Parameters.AddWithValue("@secondId", secondId);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task StaffMetadataAndActorLifecycleRaceRevalidatesInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var lifecycleService = factory.Services.GetRequiredService<StaffLifecycleService>();

        var metadataFirst = await SeedStaffMetadataRaceAsync("metadata-first");
        var metadataFirstActor = await ReadStaffMetadataActorAsync(metadataFirst);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockTarget = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockTarget.Parameters.AddWithValue("@id", metadataFirst.TargetId);
                Assert.AreEqual(metadataFirst.TargetId, Convert.ToInt64(await blockTarget.ExecuteScalarAsync()));
            }

            var update = lifecycleService.UpdateMetadataAsync(
                metadataFirstActor,
                metadataFirst.TargetId,
                new StaffMetadataInput(
                    StaffVersion.Encode(metadataFirst.TargetVersion),
                    "metadata-first.updated@example.org",
                    "Metadata First Updated",
                    "metadata-first.notice@example.org"),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(update.IsCompleted, "Metadata should be waiting on the target StaffUser serialization row.");
            var deactivate = lifecycleService.DeactivateAsync(
                superAdmin,
                metadataFirst.ActorId,
                new StaffDeactivateInput(StaffVersion.Encode(metadataFirst.ActorVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            var lifecycleWaitedForMetadata = !deactivate.IsCompleted;
            await blockerTransaction.CommitAsync();

            Assert.AreEqual("updated", (await update).Code);
            Assert.AreEqual("updated", (await deactivate).Code);
            Assert.IsTrue(lifecycleWaitedForMetadata, "Actor deactivation must serialize after an authorized metadata update already holding the lifecycle lock.");
        }
        await AssertStaffMetadataStateAsync(metadataFirst.TargetId, "Metadata First Updated", 1);

        var lifecycleFirst = await SeedStaffMetadataRaceAsync("lifecycle-first");
        var lifecycleFirstActor = await ReadStaffMetadataActorAsync(lifecycleFirst);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockActor = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockActor.Parameters.AddWithValue("@id", lifecycleFirst.ActorId);
                Assert.AreEqual(lifecycleFirst.ActorId, Convert.ToInt64(await blockActor.ExecuteScalarAsync()));
            }

            var demote = lifecycleService.ChangeRoleAsync(
                superAdmin,
                lifecycleFirst.ActorId,
                new StaffRoleInput(StaffVersion.Encode(lifecycleFirst.ActorVersion), "staff", 2),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(demote.IsCompleted, "Actor demotion should be waiting on the actor StaffUser serialization row.");
            var update = lifecycleService.UpdateMetadataAsync(
                lifecycleFirstActor,
                lifecycleFirst.TargetId,
                new StaffMetadataInput(
                    StaffVersion.Encode(lifecycleFirst.TargetVersion),
                    "lifecycle-first.updated@example.org",
                    "Lifecycle First Updated",
                    "lifecycle-first.notice@example.org"),
                CancellationToken.None);
            await Task.Delay(250);
            var metadataWaitedForLifecycle = !update.IsCompleted;
            await blockerTransaction.CommitAsync();

            Assert.AreEqual("updated", (await demote).Code);
            Assert.AreEqual("staff_scope_forbidden", (await update).Code);
            Assert.IsTrue(metadataWaitedForLifecycle, "Metadata must wait for and observe an actor lifecycle change that already owns the lifecycle lock.");
        }
        await AssertStaffMetadataStateAsync(
            lifecycleFirst.TargetId,
            lifecycleFirst.OriginalTargetDisplayName,
            0);
    }

    [TestMethod]
    public async Task StaffMetadataRejectsChangedActorEmailAndInactiveParticipationWithoutAudit()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var lifecycleService = factory.Services.GetRequiredService<StaffLifecycleService>();

        var emailChanged = await SeedStaffMetadataRaceAsync("email-changed");
        var emailChangedActor = await ReadStaffMetadataActorAsync(emailChanged);
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var changeEmail = new SqlCommand(
                "UPDATE [asap].[StaffUser] SET [UserPrincipalName] = @email, [NormalizedUserPrincipalName] = UPPER(@email) WHERE [Id] = @id;",
                connection);
            changeEmail.Parameters.AddWithValue("@email", "email-changed.actor@example.org");
            changeEmail.Parameters.AddWithValue("@id", emailChanged.ActorId);
            Assert.AreEqual(1, await changeEmail.ExecuteNonQueryAsync());
        }
        var emailChangedResult = await lifecycleService.UpdateMetadataAsync(
            emailChangedActor,
            emailChanged.TargetId,
            new StaffMetadataInput(
                StaffVersion.Encode(emailChanged.TargetVersion),
                "email-changed.updated@example.org",
                "Email Changed Updated",
                "email-changed.notice@example.org"),
            CancellationToken.None);
        Assert.AreEqual("staff_scope_forbidden", emailChangedResult.Code);
        await AssertStaffMetadataStateAsync(emailChanged.TargetId, emailChanged.OriginalTargetDisplayName, 0);

        var inactive = await SeedStaffMetadataRaceAsync("participation-inactive");
        var inactiveActor = await ReadStaffMetadataActorAsync(inactive);
        try
        {
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var deactivateOrganization = new SqlCommand(
                    "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 2;",
                    connection);
                Assert.AreEqual(1, await deactivateOrganization.ExecuteNonQueryAsync());
            }

            var inactiveResult = await lifecycleService.UpdateMetadataAsync(
                inactiveActor,
                inactive.TargetId,
                new StaffMetadataInput(
                    StaffVersion.Encode(inactive.TargetVersion),
                    "participation-inactive.updated@example.org",
                    "Participation Inactive Updated",
                    "participation-inactive.notice@example.org"),
                CancellationToken.None);
            Assert.AreEqual("staff_scope_forbidden", inactiveResult.Code);
            await AssertStaffMetadataStateAsync(inactive.TargetId, inactive.OriginalTargetDisplayName, 0);
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var reactivateOrganization = new SqlCommand(
                "UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;",
                connection);
            await reactivateOrganization.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task StaffLifecycleAndAssignmentRaceRevalidatesInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var assignmentService = factory.Services.GetRequiredService<TitleRequestMutationService>();
        var lifecycleService = factory.Services.GetRequiredService<StaffLifecycleService>();

        var assignmentFirst = await SeedLifecycleClaimRaceAsync("assignment-first", "20000000002031");
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockRequest = new SqlCommand(
                             "SELECT [Id] FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockRequest.Parameters.AddWithValue("@id", assignmentFirst.RequestId);
                Assert.AreEqual(assignmentFirst.RequestId, Convert.ToInt64(await blockRequest.ExecuteScalarAsync()));
            }

            var assign = assignmentService.AssignAsync(
                actor,
                assignmentFirst.RequestId,
                new AssignTitleRequestInput(StaffVersion.Encode(assignmentFirst.RequestVersion), assignmentFirst.StaffId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(assign.IsCompleted, "Assignment should be waiting on the request serialization row.");
            var deactivate = lifecycleService.DeactivateAsync(
                actor,
                assignmentFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(assignmentFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted, "Lifecycle should be waiting behind the assignment's organization lock.");
            await blockerTransaction.CommitAsync();

            Assert.AreEqual("updated", (await assign).Code);
            Assert.AreEqual("updated", (await deactivate).Code);
        }
        await AssertLifecycleRaceStateAsync(
            assignmentFirst,
            expectedAssignmentEvents: 1,
            expectedCleanupEvents: 1);

        var lifecycleFirst = await SeedLifecycleClaimRaceAsync("lifecycle-first", "20000000002032");
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", lifecycleFirst.StaffId);
                Assert.AreEqual(lifecycleFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }

            var deactivate = lifecycleService.DeactivateAsync(
                actor,
                lifecycleFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(lifecycleFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted, "Lifecycle should be waiting on the target StaffUser serialization row.");
            var assign = assignmentService.AssignAsync(
                actor,
                lifecycleFirst.RequestId,
                new AssignTitleRequestInput(StaffVersion.Encode(lifecycleFirst.RequestVersion), lifecycleFirst.StaffId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(assign.IsCompleted, "Assignment should be waiting behind the lifecycle organization lock.");
            await blockerTransaction.CommitAsync();

            Assert.AreEqual("updated", (await deactivate).Code);
            Assert.AreEqual("assignee_ineligible", (await assign).Code);
        }
        await AssertLifecycleRaceStateAsync(
            lifecycleFirst,
            expectedAssignmentEvents: 0,
            expectedCleanupEvents: 0);
    }

    [TestMethod]
    public async Task StaffLifecycleAndAutomaticRuleRaceRevalidatesInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var suggestionService = factory.Services.GetRequiredService<PatronSuggestionService>();
        var lifecycleService = factory.Services.GetRequiredService<StaffLifecycleService>();
        static PatronSuggestionInput Input(string title) => new(
            "book",
            title,
            "Rule Race Author",
            null,
            "Coming soon",
            101,
            true,
            new Dictionary<string, string?>());

        var submissionFirst = await SeedLifecycleAutoClaimRaceAsync("automatic-first");
        var firstSession = new PatronSessionContext(
            0,
            "20000000002041",
            2,
            2,
            2,
            DateTime.UtcNow.AddHours(1));
        PatronSuggestionResult firstResult;
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", submissionFirst.StaffId);
                Assert.AreEqual(submissionFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }

            var submit = suggestionService.CreateAsync(
                firstSession,
                Input("Automatic assignment first"),
                CancellationToken.None);
            await WaitForOrganizationUpdateLockAsync(2);
            Assert.IsFalse(submit.IsCompleted, "Submission should be waiting on the target StaffUser row after taking the organization lock.");
            var deactivate = lifecycleService.DeactivateAsync(
                actor,
                submissionFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(submissionFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted, "Lifecycle should be waiting behind submission's organization lock.");
            await blockerTransaction.CommitAsync();

            firstResult = await submit;
            Assert.AreEqual("updated", (await deactivate).Code);
        }
        await AssertAutomaticRuleRaceStateAsync(
            submissionFirst,
            firstResult.Id,
            expectedAssignedEvents: 1,
            expectedCleanupEvents: 1);

        var lifecycleFirst = await SeedLifecycleAutoClaimRaceAsync("lifecycle-first");
        var secondSession = new PatronSessionContext(
            0,
            "20000000002042",
            2,
            2,
            2,
            DateTime.UtcNow.AddHours(1));
        PatronSuggestionResult secondResult;
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", lifecycleFirst.StaffId);
                Assert.AreEqual(lifecycleFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }

            var deactivate = lifecycleService.DeactivateAsync(
                actor,
                lifecycleFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(lifecycleFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted, "Lifecycle should be waiting on the target StaffUser row.");
            var submit = suggestionService.CreateAsync(
                secondSession,
                Input("Lifecycle deactivation first"),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(submit.IsCompleted, "Submission should be waiting behind the lifecycle organization lock.");
            await blockerTransaction.CommitAsync();

            Assert.AreEqual("updated", (await deactivate).Code);
            secondResult = await submit;
        }
        await AssertAutomaticRuleRaceStateAsync(
            lifecycleFirst,
            secondResult.Id,
            expectedAssignedEvents: 0,
            expectedCleanupEvents: 0);
    }

    [TestMethod]
    public async Task StaffWorkflowTimestampsSerializeSqlUtcTicksWithZuluOffsets()
    {
        using var client = factory!.CreateClient();
        using (var session = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        AddTestingStaffHeaders(
            client,
            actor.Id,
            Guid.Parse(identity.TenantId!),
            actor.AuthenticationEmail);
        using (var authenticated = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
        }
        long titleRequestId;
        long copyRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code]=N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [LibraryNameSnapshot], [Barcode], [Title], [AutoHold], [MaterialFormatId],
                     [Status], [BibId], [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType],
                     [IsbnCheckStatus], [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                VALUES
                    (2, N'UTC Snapshot Library', N'20000000003101', N'UTC title DTO', 0, @formatId,
                     N'hold_placed', N'93101', @actorId, N'UTC Staff Snapshot', '2026-09-01T12:14:15', N'manual',
                     N'found', '2026-09-01T12:15:16', '2026-09-01T12:13:14', '2026-09-01T12:16:17');
                DECLARE @titleId bigint = SCOPE_IDENTITY();

                INSERT INTO [asap].[AdditionalCopyRequest]
                    ([SourceTitleRequestId], [LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title],
                     [MaterialFormatId], [FormatSnapshot], [Status], [CreatedByStaffUserId], [CreatedByDisplayName],
                     [CreatedUtc], [UpdatedUtc], [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc],
                     [ClaimType], [ClosedByStaffUserId], [ClosedByDisplayName], [ClosedUtc])
                VALUES
                    (@titleId, 2, N'UTC Snapshot Library', N'93101', N'UTC additional-copy DTO',
                     @formatId, N'book', N'closed', @actorId, N'UTC Staff Snapshot',
                     '2026-09-01T12:16:17', '2026-09-01T12:19:20', @actorId, N'UTC Staff Snapshot',
                     '2026-09-01T12:17:18', N'manual', @actorId, N'UTC Staff Snapshot', '2026-09-01T12:18:19');
                SELECT @titleId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@actorId", actor.Id);
            await using var seeded = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await seeded.ReadAsync());
            titleRequestId = seeded.GetInt64(0);
            copyRequestId = seeded.GetInt64(1);
        }

        using var titleResponse = await client.GetAsync($"/api/asap/staff/title-requests/{titleRequestId}");
        Assert.AreEqual(HttpStatusCode.OK, titleResponse.StatusCode);
        using var title = JsonDocument.Parse(await titleResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("2026-09-01T12:13:14Z", title.RootElement.GetProperty("created").GetString());
        Assert.AreEqual("2026-09-01T12:16:17Z", title.RootElement.GetProperty("updated").GetString());
        Assert.AreEqual("2026-09-01T12:14:15Z", title.RootElement.GetProperty("claimedAt").GetString());
        Assert.AreEqual("2026-09-01T12:15:16Z", title.RootElement.GetProperty("lastChecked").GetString());
        Assert.AreEqual("2026-09-01T12:13:14Z", title.RootElement.GetProperty("phaseEnteredAt").GetString());

        using var copyResponse = await client.GetAsync($"/api/asap/staff/additional-copies/{copyRequestId}");
        Assert.AreEqual(HttpStatusCode.OK, copyResponse.StatusCode);
        using var copy = JsonDocument.Parse(await copyResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("2026-09-01T12:16:17Z", copy.RootElement.GetProperty("created").GetString());
        Assert.AreEqual("2026-09-01T12:19:20Z", copy.RootElement.GetProperty("updated").GetString());
        Assert.AreEqual("2026-09-01T12:17:18Z", copy.RootElement.GetProperty("claimedAt").GetString());
        Assert.AreEqual("2026-09-01T12:18:19Z", copy.RootElement.GetProperty("closedAt").GetString());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText = "SELECT [CreatedUtc], [UpdatedUtc] FROM [asap].[AdditionalCopyRequest] WHERE [Id]=@id;";
        command.Parameters.AddWithValue("@id", copyRequestId);
        await using var stored = await command.ExecuteReaderAsync();
        Assert.IsTrue(await stored.ReadAsync());
        Assert.AreEqual(new DateTime(2026, 9, 1, 12, 16, 17, DateTimeKind.Unspecified), stored.GetDateTime(0));
        Assert.AreEqual(new DateTime(2026, 9, 1, 12, 19, 20, DateTimeKind.Unspecified), stored.GetDateTime(1));
    }

    [TestMethod]
    public async Task AssignmentCandidatesAllowOrdinaryStaffAndExposeOnlyCurrentEligibleRelationships()
    {
        var configured = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient();
        using (var startup = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, startup.StatusCode);
        }
        var tenantId = Guid.Parse(configured.TenantId!);
        var actorObjectId = Guid.NewGuid();
        var localObjectId = Guid.NewGuid();
        var foreignObjectId = Guid.NewGuid();
        var invalidTenantId = Guid.NewGuid();
        var invalidTenantObjectId = Guid.NewGuid();
        long superId;
        long actorId;
        long localId;
        long foreignId;
        long invalidTenantStaffId;
        long unboundStaffId;
        long taskId;
        string taskVersion;

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 82)
                BEGIN
                    INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                    VALUES (82, N'Candidate Foreign Library', N'CFL', 1);
                END;

                DECLARE @superId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [Role], [OrganizationId], [IsActive])
                VALUES
                    (@tenantId, @actorObjectId, N'candidate.actor@example.org', N'CANDIDATE.ACTOR@EXAMPLE.ORG',
                     N'Candidate Actor', N'staff', 2, 1);
                DECLARE @actorId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [Role], [OrganizationId], [IsActive])
                VALUES
                    (@tenantId, @localObjectId, N'candidate.local@example.org', N'CANDIDATE.LOCAL@EXAMPLE.ORG',
                     N'Candidate Local Admin', N'admin', 2, 1);
                DECLARE @localId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [Role], [OrganizationId], [IsActive])
                VALUES
                    (@tenantId, @foreignObjectId, N'candidate.foreign@example.org', N'CANDIDATE.FOREIGN@EXAMPLE.ORG',
                     N'Candidate Foreign Staff', N'staff', 82, 1);
                DECLARE @foreignId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [Role], [OrganizationId], [IsActive])
                VALUES
                    (@invalidTenantId, @invalidTenantObjectId, N'candidate.tenant@example.org',
                     N'CANDIDATE.TENANT@EXAMPLE.ORG', N'Candidate Historical Tenant', N'staff', 2, 1);
                DECLARE @invalidTenantStaffId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [Role], [OrganizationId], [IsActive])
                VALUES
                    (NULL, NULL, N'candidate.unbound@example.org', N'CANDIDATE.UNBOUND@EXAMPLE.ORG',
                     N'Candidate Never Signed In', N'staff', 2, 1);
                DECLARE @unboundStaffId bigint = SCOPE_IDENTITY();
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[AdditionalCopyRequest]
                    ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [MaterialFormatId],
                     [FormatSnapshot], [Status], [Notes], [CreatedByStaffUserId], [CreatedByDisplayName],
                     [CreatedUtc], [UpdatedUtc])
                VALUES
                    (2, N'Candidate Library Snapshot', N'candidate-api-bib', N'Candidate API task', @formatId,
                     N'book', N'open', N'Candidate API note', @actorId, N'Candidate Actor',
                     '2026-09-01T12:00:00', '2026-09-01T12:00:00');
                DECLARE @taskId bigint = SCOPE_IDENTITY();

                SELECT @superId, @actorId, @localId, @foreignId, @invalidTenantStaffId,
                       @unboundStaffId, @taskId, request.[RowVersion]
                FROM [asap].[AdditionalCopyRequest] request WHERE request.[Id] = @taskId;
                """;
            seed.Parameters.AddWithValue("@superObjectId", Guid.Parse(configured.ObjectId!));
            seed.Parameters.AddWithValue("@tenantId", tenantId);
            seed.Parameters.AddWithValue("@actorObjectId", actorObjectId);
            seed.Parameters.AddWithValue("@localObjectId", localObjectId);
            seed.Parameters.AddWithValue("@foreignObjectId", foreignObjectId);
            seed.Parameters.AddWithValue("@invalidTenantId", invalidTenantId);
            seed.Parameters.AddWithValue("@invalidTenantObjectId", invalidTenantObjectId);
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            superId = reader.GetInt64(0);
            actorId = reader.GetInt64(1);
            localId = reader.GetInt64(2);
            foreignId = reader.GetInt64(3);
            invalidTenantStaffId = reader.GetInt64(4);
            unboundStaffId = reader.GetInt64(5);
            taskId = reader.GetInt64(6);
            taskVersion = StaffVersion.Encode((byte[])reader[7]);
        }

        AddTestingStaffHeaders(client, actorId, tenantId, "candidate.actor@example.org");
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using (var management = await client.GetAsync("/api/asap/staff/users?orgId=2"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, management.StatusCode);
        }
        using (var forbiddenScope = await client.GetAsync("/api/asap/staff/assignment-candidates?libraryOrgId=82"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbiddenScope.StatusCode);
        }

        using var response = await client.GetAsync("/api/asap/staff/assignment-candidates?libraryOrgId=2");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidates = body.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        foreach (var candidate in candidates)
        {
            CollectionAssert.AreEquivalent(
                new[] { "displayName", "id" },
                candidate.EnumerateObject().Select(item => item.Name).ToArray());
        }
        var candidateIds = candidates.Select(item => item.GetProperty("id").GetString()).ToHashSet();
        Assert.IsTrue(candidateIds.Contains(actorId.ToString()));
        Assert.IsTrue(candidateIds.Contains(localId.ToString()));
        Assert.IsTrue(candidateIds.Contains(superId.ToString()));
        Assert.IsFalse(candidateIds.Contains(foreignId.ToString()));
        Assert.IsTrue(candidateIds.Contains(invalidTenantStaffId.ToString()));
        Assert.IsTrue(candidateIds.Contains(unboundStaffId.ToString()));

        using var assignedResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/additional-copies/{taskId}/assign",
            new { version = taskVersion, assigneeId = localId });
        Assert.AreEqual(HttpStatusCode.OK, assignedResponse.StatusCode);
        using var assigned = JsonDocument.Parse(await assignedResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(localId.ToString(), assigned.RootElement.GetProperty("claimedByStaffUserId").GetString());

        using var rejectedResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/additional-copies/{taskId}/assign",
            new
            {
                version = assigned.RootElement.GetProperty("version").GetString(),
                assigneeId = foreignId
            });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        using var rejected = JsonDocument.Parse(await rejectedResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("assignee_ineligible", rejected.RootElement.GetProperty("code").GetString());

        using var unchangedResponse = await client.GetAsync($"/api/asap/staff/additional-copies/{taskId}");
        using var unchanged = JsonDocument.Parse(await unchangedResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(localId.ToString(), unchanged.RootElement.GetProperty("claimedByStaffUserId").GetString());
    }

    [TestMethod]
    public async Task AdditionalCopyWorkflowPreservesLegacyRuleSourceIndependenceAndReducedAudit()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var initialActor = await ReadConfiguredSuperAdminAsync();
        var seeded = await SeedAdditionalCopyLegacySourceAsync(initialActor.Id, "workflow");
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<AdditionalCopyService>();

        var preview = await service.PreviewAsync(actor, seeded.SourceRequestId, CancellationToken.None);
        Assert.AreEqual("loaded", preview.Code);
        Assert.AreEqual(0, preview.Preview!.OpenCount);
        Assert.IsTrue(preview.Preview.EmailPurchaseReminderDefault);
        Assert.AreEqual("slice3-bib-workflow", preview.Preview.Bibid);

        var created = await service.CreateAsync(
            actor,
            seeded.SourceRequestId,
            new AdditionalCopyCreateInput(preview.Preview.Version, true),
            CancellationToken.None);
        Assert.AreEqual("created", created.Code);
        Assert.AreEqual(0, created.OpenCountBefore);
        Assert.AreEqual(1, created.OpenCountAfter);
        Assert.IsTrue(created.DispatchOutboxId.HasValue);
        CollectionAssert.Contains(dispatcher!.EnqueuedIds, created.DispatchOutboxId.Value);

        var task = await service.GetAsync(actor, created.RequestId!.Value.ToString(), null, CancellationToken.None);
        Assert.IsNotNull(task);
        Assert.AreEqual("Frozen source library workflow", task.LibraryOrgName);
        Assert.AreEqual("Legacy source workflow", task.Title);
        Assert.AreEqual(seeded.CurrentClaimantId.ToString(), task.ClaimedByStaffUserId);
        Assert.AreEqual("Current claimant snapshot workflow", task.ClaimedByDisplayName);
        Assert.AreEqual("legacy", task.ClaimType);
        Assert.AreEqual(seeded.HistoricalRuleId.ToString(), task.ClaimRuleId);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var mutateSource = new SqlCommand(
                "UPDATE [asap].[TitleRequest] SET [Title]=N'Changed source title', [LibraryNameSnapshot]=N'Changed source library', [UpdatedUtc]=SYSUTCDATETIME() WHERE [Id]=@id;",
                connection);
            mutateSource.Parameters.AddWithValue("@id", seeded.SourceRequestId);
            Assert.AreEqual(1, await mutateSource.ExecuteNonQueryAsync());
        }
        task = await service.GetAsync(actor, task.Id, null, CancellationToken.None);
        Assert.AreEqual("Frozen source library workflow", task!.LibraryOrgName);
        Assert.AreEqual("Legacy source workflow", task.Title);

        var closed = await service.SetClosedAsync(
            actor,
            created.RequestId.Value,
            new VersionInput(task.Version),
            reopen: false,
            CancellationToken.None);
        Assert.AreEqual("updated", closed.Code);
        task = await service.GetAsync(actor, task.Id, null, CancellationToken.None);
        var reopened = await service.SetClosedAsync(
            actor,
            created.RequestId.Value,
            new VersionInput(task!.Version),
            reopen: true,
            CancellationToken.None);
        Assert.AreEqual("updated", reopened.Code);
        Assert.IsNull(reopened.ClaimClearedReason);
        task = await service.GetAsync(actor, task.Id, null, CancellationToken.None);
        Assert.AreEqual("legacy", task!.ClaimType);
        Assert.AreEqual(seeded.HistoricalRuleId.ToString(), task.ClaimRuleId);
        Assert.AreEqual(seeded.CurrentClaimantId.ToString(), task.ClaimedByStaffUserId);

        byte[] sourceVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var closeSource = new SqlCommand(
                "UPDATE [asap].[TitleRequest] SET [Status]=N'closed', [CloseReason]=N'manual', [UpdatedUtc]=SYSUTCDATETIME() OUTPUT inserted.[RowVersion] WHERE [Id]=@id;",
                connection);
            closeSource.Parameters.AddWithValue("@id", seeded.SourceRequestId);
            sourceVersion = (byte[])(await closeSource.ExecuteScalarAsync())!;
        }
        var sourceDelete = await factory.Services.GetRequiredService<TitleRequestMutationService>().DeleteClosedAsync(
            actor,
            seeded.SourceRequestId,
            new VersionInput(StaffVersion.Encode(sourceVersion)),
            CancellationToken.None);
        Assert.AreEqual("deleted", sourceDelete.Code);
        task = await service.GetAsync(actor, task.Id, null, CancellationToken.None);
        Assert.IsNull(task!.SourceTitleRequest);
        Assert.AreEqual("Legacy source workflow", task.Title);

        closed = await service.SetClosedAsync(
            actor,
            created.RequestId.Value,
            new VersionInput(task.Version),
            reopen: false,
            CancellationToken.None);
        Assert.AreEqual("updated", closed.Code);
        task = await service.GetAsync(actor, task.Id, null, CancellationToken.None);
        var deleted = await service.DeleteClosedAsync(
            actor,
            created.RequestId.Value,
            new VersionInput(task!.Version),
            CancellationToken.None);
        Assert.AreEqual("deleted", deleted.Code);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] WHERE [Id]=@taskId),
                (SELECT COUNT(*) FROM [asap].[DeletedRequestAudit]
                 WHERE [RequestType]=N'additional_copy' AND [OriginalRequestKey]=CONVERT(nvarchar(64), @taskId)
                   AND [Title]=N'Legacy source workflow' AND [BibId]=N'slice3-bib-workflow'
                   AND [MaskedBarcode] IS NULL AND [CloseReason] IS NULL),
                (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                 WHERE [Id]=@outboxId AND [DeliveryClass]=N'staff_authorization_sensitive'
                   AND [RecipientStaffUserId]=@actorId AND [AuthorizationOrganizationId]=2
                   AND [RecipientAddressKind]=N'notification_email' AND [ToAddress]=N'slice3.actor@example.org'),
                (SELECT COUNT(*) FROM sys.columns
                 WHERE [object_id]=OBJECT_ID(N'[asap].[DeletedRequestAudit]') AND [name]=N'Notes');
            """;
        command.Parameters.AddWithValue("@taskId", created.RequestId.Value);
        command.Parameters.AddWithValue("@outboxId", created.DispatchOutboxId.Value);
        command.Parameters.AddWithValue("@actorId", actor.Id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(0, reader.GetInt32(0));
        Assert.AreEqual(1, reader.GetInt32(1));
        Assert.AreEqual(1, reader.GetInt32(2));
        Assert.AreEqual(0, reader.GetInt32(3));
    }

    [TestMethod]
    public async Task AdditionalCopyCreationRollsBackSourceTaskAndReminderAfterIntermediateFailure()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var initialActor = await ReadConfiguredSuperAdminAsync();
        var seeded = await SeedAdditionalCopyLegacySourceAsync(initialActor.Id, "rollback");
        var actor = await ReadConfiguredSuperAdminAsync();
        var service = factory.Services.GetRequiredService<AdditionalCopyService>();
        var preview = await service.PreviewAsync(actor, seeded.SourceRequestId, CancellationToken.None);
        Assert.AreEqual("loaded", preview.Code);

        int reminderCountBefore;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            reminderCountBefore = Convert.ToInt32(await new SqlCommand(
                "SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE N'additional-copy-reminder:%';",
                connection).ExecuteScalarAsync());
            await using var trigger = new SqlCommand(
                """
                CREATE TRIGGER [asap].[TR_Slice3RejectAdditionalCopyOutbox]
                ON [asap].[EmailOutbox]
                AFTER INSERT
                AS
                    THROW 51003, 'Slice 3 rollback probe', 1;
                """,
                connection);
            await trigger.ExecuteNonQueryAsync();
        }
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(
                actor,
                seeded.SourceRequestId,
                new AdditionalCopyCreateInput(preview.Preview!.Version, true),
                CancellationToken.None));

            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM [asap].[AdditionalCopyRequest] WHERE [SourceTitleRequestId]=@sourceId),
                    (SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE N'additional-copy-reminder:%'),
                    (SELECT [Notes] FROM [asap].[TitleRequest] WHERE [Id]=@sourceId),
                    (SELECT [RowVersion] FROM [asap].[TitleRequest] WHERE [Id]=@sourceId);
                """;
            command.Parameters.AddWithValue("@sourceId", seeded.SourceRequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(0, reader.GetInt32(0));
            Assert.AreEqual(reminderCountBefore, reader.GetInt32(1));
            Assert.AreEqual("Original source note rollback", reader.GetString(2));
            CollectionAssert.AreEqual(seeded.SourceVersion, (byte[])reader[3]);
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var drop = new SqlCommand(
                "DROP TRIGGER IF EXISTS [asap].[TR_Slice3RejectAdditionalCopyOutbox];",
                connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task AdditionalCopyAssignmentAndLifecycleRaceRevalidatesInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();

        var assignmentFirst = await SeedAdditionalCopyRaceAsync("assignment-first", closed: false, claimed: false);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockTask = new SqlCommand(
                             "SELECT [Id] FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockTask.Parameters.AddWithValue("@id", assignmentFirst.TaskId);
                Assert.AreEqual(assignmentFirst.TaskId, Convert.ToInt64(await blockTask.ExecuteScalarAsync()));
            }
            var assign = copies.AssignAsync(
                actor,
                assignmentFirst.TaskId,
                new AssignAdditionalCopyInput(StaffVersion.Encode(assignmentFirst.TaskVersion), assignmentFirst.StaffId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(assign.IsCompleted);
            var deactivate = lifecycle.DeactivateAsync(
                actor,
                assignmentFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(assignmentFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted);
            await blockerTransaction.CommitAsync();
            Assert.AreEqual("updated", (await assign).Code);
            var lifecycleResult = await deactivate;
            Assert.AreEqual("updated", lifecycleResult.Code);
            Assert.AreEqual(1, lifecycleResult.OpenAdditionalCopyClaimsCleared);
        }
        await AssertAdditionalCopyClaimStateAsync(
            assignmentFirst.TaskId, null, "staff_scope_contracted", 1, assignmentFirst.StaffId);

        var lifecycleFirst = await SeedAdditionalCopyRaceAsync("lifecycle-first", closed: false, claimed: false);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", lifecycleFirst.StaffId);
                Assert.AreEqual(lifecycleFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }
            var deactivate = lifecycle.DeactivateAsync(
                actor,
                lifecycleFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(lifecycleFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted);
            var assign = copies.AssignAsync(
                actor,
                lifecycleFirst.TaskId,
                new AssignAdditionalCopyInput(StaffVersion.Encode(lifecycleFirst.TaskVersion), lifecycleFirst.StaffId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(assign.IsCompleted);
            await blockerTransaction.CommitAsync();
            var lifecycleResult = await deactivate;
            Assert.AreEqual("updated", lifecycleResult.Code);
            Assert.AreEqual(0, lifecycleResult.OpenAdditionalCopyClaimsCleared);
            Assert.AreEqual("assignee_ineligible", (await assign).Code);
        }
        await AssertAdditionalCopyClaimStateAsync(
            lifecycleFirst.TaskId, null, null, 0, lifecycleFirst.StaffId);
    }

    [TestMethod]
    public async Task AdditionalCopyReopenAndLifecycleRaceRevalidatesInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();

        var reopenFirst = await SeedAdditionalCopyRaceAsync("reopen-first", closed: true, claimed: true);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockTask = new SqlCommand(
                             "SELECT [Id] FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockTask.Parameters.AddWithValue("@id", reopenFirst.TaskId);
                Assert.AreEqual(reopenFirst.TaskId, Convert.ToInt64(await blockTask.ExecuteScalarAsync()));
            }
            var reopen = copies.SetClosedAsync(
                actor,
                reopenFirst.TaskId,
                new VersionInput(StaffVersion.Encode(reopenFirst.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(reopen.IsCompleted);
            var deactivate = lifecycle.DeactivateAsync(
                actor,
                reopenFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(reopenFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted);
            await blockerTransaction.CommitAsync();
            var reopenResult = await reopen;
            Assert.AreEqual("updated", reopenResult.Code);
            Assert.IsNull(reopenResult.ClaimClearedReason);
            var lifecycleResult = await deactivate;
            Assert.AreEqual("updated", lifecycleResult.Code);
            Assert.AreEqual(1, lifecycleResult.OpenAdditionalCopyClaimsCleared);
        }
        await AssertAdditionalCopyClaimStateAsync(
            reopenFirst.TaskId, null, "staff_scope_contracted", 1, reopenFirst.StaffId, "open");

        var lifecycleFirst = await SeedAdditionalCopyRaceAsync("reopen-lifecycle-first", closed: true, claimed: true);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", lifecycleFirst.StaffId);
                Assert.AreEqual(lifecycleFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }
            var deactivate = lifecycle.DeactivateAsync(
                actor,
                lifecycleFirst.StaffId,
                new StaffDeactivateInput(StaffVersion.Encode(lifecycleFirst.StaffVersion)),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(deactivate.IsCompleted);
            var reopen = copies.SetClosedAsync(
                actor,
                lifecycleFirst.TaskId,
                new VersionInput(StaffVersion.Encode(lifecycleFirst.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(reopen.IsCompleted);
            await blockerTransaction.CommitAsync();
            var lifecycleResult = await deactivate;
            Assert.AreEqual("updated", lifecycleResult.Code);
            Assert.AreEqual(0, lifecycleResult.OpenAdditionalCopyClaimsCleared);
            var reopenResult = await reopen;
            Assert.AreEqual("updated", reopenResult.Code);
            Assert.AreEqual("claimant_inactive", reopenResult.ClaimClearedReason);
        }
        await AssertAdditionalCopyClaimStateAsync(
            lifecycleFirst.TaskId, null, "claimant_inactive", 0, lifecycleFirst.StaffId, "open");
    }

    [TestMethod]
    public async Task AdditionalCopyReopenRetainsEveryCurrentlyEligibleRoleAndLeavesNoClaimUnclaimed()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var copies = factory.Services.GetRequiredService<AdditionalCopyService>();

        foreach (var (suffix, role, organizationId) in new[]
                 {
                     ("eligible-staff", "staff", 2),
                     ("eligible-admin", "admin", 2),
                     ("eligible-super", "super_admin", 1)
                 })
        {
            var seeded = await SeedAdditionalCopyRaceAsync(
                suffix,
                closed: true,
                claimed: true,
                role,
                organizationId);
            var reopened = await copies.SetClosedAsync(
                actor,
                seeded.TaskId,
                new VersionInput(StaffVersion.Encode(seeded.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            Assert.AreEqual("updated", reopened.Code, suffix);
            Assert.IsNull(reopened.ClaimClearedReason, suffix);
            var current = await copies.GetAsync(actor, seeded.TaskId.ToString(), null, CancellationToken.None);
            Assert.AreEqual(seeded.StaffId.ToString(), current!.ClaimedByStaffUserId, suffix);
            Assert.AreEqual("manual", current.ClaimType, suffix);
            await AssertAdditionalCopyClaimStateAsync(
                seeded.TaskId,
                seeded.StaffId,
                null,
                0,
                null,
                "open",
                expectLifecycleAudit: false);
        }

        var unclaimed = await SeedAdditionalCopyRaceAsync("eligible-no-claim", closed: true, claimed: false);
        var noClaimReopen = await copies.SetClosedAsync(
            actor,
            unclaimed.TaskId,
            new VersionInput(StaffVersion.Encode(unclaimed.TaskVersion)),
            reopen: true,
            CancellationToken.None);
        Assert.AreEqual("updated", noClaimReopen.Code);
        Assert.IsNull(noClaimReopen.ClaimClearedReason);
        await AssertAdditionalCopyClaimStateAsync(
            unclaimed.TaskId,
            null,
            null,
            0,
            null,
            "open",
            expectLifecycleAudit: false);
    }

    [TestMethod]
    public async Task AdditionalCopyReopenAndScopeRoleChangesRevalidateInBothLockOrders()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
        var lifecycle = factory.Services.GetRequiredService<StaffLifecycleService>();

        await AssertAdditionalCopyReopenRoleChangeRaceAsync(
            actor,
            copies,
            lifecycle,
            "library-move",
            "staff",
            2,
            "staff",
            83);
        await AssertAdditionalCopyReopenRoleChangeRaceAsync(
            actor,
            copies,
            lifecycle,
            "super-demotion",
            "super_admin",
            1,
            "staff",
            83);
    }

    [TestMethod]
    public async Task AdditionalCopyReopenRejectsCandidateChangeAndInvalidClaimWithoutSubstitution()
    {
        using (var startup = factory!.CreateClient())
        using (var session = await startup.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        }
        var actor = await ReadConfiguredSuperAdminAsync();
        var copies = factory.Services.GetRequiredService<AdditionalCopyService>();
        var seeded = await SeedAdditionalCopyCandidateChangeAsync("candidate-change");

        AdditionalCopyMutationResult staleResult;
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockOrganization = new SqlCommand(
                             "SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=2;",
                             blockerConnection,
                             blockerTransaction))
            {
                Assert.AreEqual(2, Convert.ToInt32(await blockOrganization.ExecuteScalarAsync()));
            }
            var reopen = copies.SetClosedAsync(
                actor,
                seeded.TaskId,
                new VersionInput(StaffVersion.Encode(seeded.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(reopen.IsCompleted);
            await using (var change = new SqlConnection(databaseConnectionString))
            {
                await change.OpenAsync();
                await using var command = new SqlCommand(
                    "UPDATE [asap].[AdditionalCopyRequest] SET [ClaimedByStaffUserId]=@staffId, [ClaimedByDisplayName]=N'Changed candidate', [ClaimedAtUtc]=SYSUTCDATETIME(), [ClaimType]=N'manual', [ClaimRuleId]=NULL, [UpdatedUtc]=SYSUTCDATETIME() WHERE [Id]=@taskId;",
                    change);
                command.Parameters.AddWithValue("@staffId", seeded.SecondStaffId);
                command.Parameters.AddWithValue("@taskId", seeded.TaskId);
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
            }
            await blockerTransaction.CommitAsync();
            staleResult = await reopen;
        }
        Assert.AreEqual("stale_version", staleResult.Code);
        await AssertAdditionalCopyClaimStateAsync(
            seeded.TaskId,
            seeded.SecondStaffId,
            null,
            0,
            null,
            "closed",
            expectLifecycleAudit: false);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var deactivate = new SqlCommand(
                "UPDATE [asap].[StaffUser] SET [IsActive]=0 WHERE [Id]=@id;",
                connection);
            deactivate.Parameters.AddWithValue("@id", seeded.SecondStaffId);
            Assert.AreEqual(1, await deactivate.ExecuteNonQueryAsync());
        }
        var current = await copies.GetAsync(actor, seeded.TaskId.ToString(), null, CancellationToken.None);
        var reopened = await copies.SetClosedAsync(
            actor,
            seeded.TaskId,
            new VersionInput(current!.Version),
            reopen: true,
            CancellationToken.None);
        Assert.AreEqual("updated", reopened.Code);
        Assert.AreEqual("claimant_inactive", reopened.ClaimClearedReason);
        await AssertAdditionalCopyClaimStateAsync(
            seeded.TaskId,
            null,
            "claimant_inactive",
            0,
            null,
            "open",
            expectLifecycleAudit: false);

        current = await copies.GetAsync(actor, seeded.TaskId.ToString(), null, CancellationToken.None);
        var noOpVersion = current!.Version;
        var noOp = await copies.SetClosedAsync(
            actor,
            seeded.TaskId,
            new VersionInput(noOpVersion),
            reopen: true,
            CancellationToken.None);
        Assert.AreEqual("updated", noOp.Code);
        current = await copies.GetAsync(actor, seeded.TaskId.ToString(), null, CancellationToken.None);
        Assert.AreEqual(noOpVersion, current!.Version);
        Assert.IsNull(current.ClaimedByStaffUserId);
    }

    [TestMethod]
    public async Task StaffRequestWorkflowEnforcesVersionsIdentifierBarrierAndLegacyProtection()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        long actorId;
        long requestId;
        long protectedRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var actor = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG';",
                connection);
            actor.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            actorId = Convert.ToInt64(await actor.ExecuteScalarAsync());

            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Author], [Identifier], [Publication], [AutoHold],
                     [MaterialFormatId], [Status], [BibId], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount],
                     [IsbnCheckLastErrorCode], [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002101', N'Old workflow title', N'Old author', N'9780000000211', N'Old publication', 1,
                        @formatId, N'suggestion', N'9001', N'found', N'9001', 4, N'old_error',
                        SYSUTCDATETIME(), DATEADD(day, -1, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId])
                    SELECT @requestId, [Id] FROM [asap].[WorkflowTag]
                    WHERE [Code] IN (N'polaris_bib_found', N'polaris_bib_not_found', N'polaris_multiple_matches');

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                     [Status], [CloseReason], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002102', N'Protected workflow title', N'9780000000212', 1, @formatId,
                        N'closed', N'manual', NULL, N'not_found', DATEADD(day, -2, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @protectedId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [Message], [MetadataJson], [CreatedUtc])
                VALUES (@protectedId, N'legacy_status_imported', N'hold_placed', N'system', N'Imported placement evidence.',
                        N'{"legacyBibProtection":true,"legacyBibId":null}', DATEADD(day, -1, SYSUTCDATETIME()));
                SELECT @requestId, @protectedId;
                """;
            await using var seeded = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await seeded.ReadAsync());
            requestId = seeded.GetInt64(0);
            protectedRequestId = seeded.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var list = await client.GetAsync("/api/asap/staff/title-requests?scope=all");
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode, await list.Content.ReadAsStringAsync());
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var initial = listBody.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == requestId.ToString());
        var initialVersion = initial.GetProperty("version").GetString();
        Assert.IsTrue(initial.GetProperty("capabilities").GetProperty("canEditIdentifier").GetBoolean());

        using var bypass = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version = initialVersion, action = "edit", status = "hold_placed", title = "Bypassed title" });
        Assert.AreEqual(HttpStatusCode.BadRequest, bypass.StatusCode, await bypass.Content.ReadAsStringAsync());
        using (var bypassBody = JsonDocument.Parse(await bypass.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("invalid_transition", bypassBody.RootElement.GetProperty("code").GetString());
        }

        using var edit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new
            {
                version = initialVersion,
                action = "edit",
                status = "suggestion",
                title = "New workflow title",
                identifier = "9780000000219",
                bibid = (string?)null,
                autohold = true
            });
        Assert.AreEqual(HttpStatusCode.OK, edit.StatusCode, await edit.Content.ReadAsStringAsync());
        using var editBody = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        var editedVersion = editBody.RootElement.GetProperty("version").GetString();
        Assert.AreEqual("9780000000219", editBody.RootElement.GetProperty("identifier").GetString());
        Assert.AreEqual(JsonValueKind.Null, editBody.RootElement.GetProperty("bibid").ValueKind);
        Assert.AreEqual("pending", editBody.RootElement.GetProperty("isbnCheckStatus").GetString());
        Assert.HasCount(0, editBody.RootElement.GetProperty("workflowTags").EnumerateArray().ToArray());
        Assert.AreEqual(actorId.ToString(), editBody.RootElement.GetProperty("claimedByStaffUserId").GetString());

        using var stale = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version = initialVersion, action = "edit", status = "suggestion", title = "Stale title" });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var operation = connection.CreateCommand();
            operation.CommandText =
                """
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber], [State], [Phase],
                     [ExecutionEpoch], [RequestStartedUtc])
                VALUES (@requestId, N'20000000002101', N'9999', 1, N'ambiguous', N'create_started', 1, SYSUTCDATETIME());
                """;
            operation.Parameters.AddWithValue("@requestId", requestId);
            await operation.ExecuteNonQueryAsync();
        }

        using var descriptive = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version = editedVersion, action = "edit", status = "suggestion", title = "Allowed while blocked" });
        Assert.AreEqual(HttpStatusCode.OK, descriptive.StatusCode, await descriptive.Content.ReadAsStringAsync());
        using var descriptiveBody = JsonDocument.Parse(await descriptive.Content.ReadAsStringAsync());
        var blockedVersion = descriptiveBody.RootElement.GetProperty("version").GetString();
        Assert.AreEqual(
            "hold_operation_incomplete",
            descriptiveBody.RootElement.GetProperty("capabilities").GetProperty("blockingReason").GetString());

        using var blocked = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new
            {
                version = blockedVersion,
                action = "edit",
                status = "suggestion",
                identifier = "9780000000220"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_operation_incomplete", blockedBody.RootElement.GetProperty("code").GetString());

        string protectedVersion;
        using (var protectedGet = await client.GetAsync($"/api/asap/staff/title-requests/{protectedRequestId}"))
        {
            using var protectedBody = JsonDocument.Parse(await protectedGet.Content.ReadAsStringAsync());
            protectedVersion = protectedBody.RootElement.GetProperty("version").GetString()!;
        }
        using var reopen = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{protectedRequestId}/action",
            new { version = protectedVersion, action = "reopen", status = "suggestion" });
        Assert.AreEqual(HttpStatusCode.OK, reopen.StatusCode, await reopen.Content.ReadAsStringAsync());
        using var reopenBody = JsonDocument.Parse(await reopen.Content.ReadAsStringAsync());
        Assert.IsFalse(reopenBody.RootElement.GetProperty("capabilities").GetProperty("canEditIdentifier").GetBoolean());
        Assert.AreEqual(
            "identifier_locked_by_stage",
            reopenBody.RootElement.GetProperty("capabilities").GetProperty("blockingReason").GetString());

        using var protectedEdit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{protectedRequestId}/action",
            new
            {
                version = reopenBody.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "suggestion",
                identifier = "9780000000999"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, protectedEdit.StatusCode);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] = @requestId),
                (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId),
                (SELECT [Title] FROM [asap].[TitleRequest] WHERE [Id] = @requestId),
                (SELECT [Identifier] FROM [asap].[TitleRequest] WHERE [Id] = @protectedId);
            """;
        verifyCommand.Parameters.AddWithValue("@requestId", requestId);
        verifyCommand.Parameters.AddWithValue("@protectedId", protectedRequestId);
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(0, reader.GetInt32(0));
        Assert.IsGreaterThanOrEqualTo(2, reader.GetInt32(1));
        Assert.AreEqual("Allowed while blocked", reader.GetString(2));
        Assert.AreEqual("9780000000212", reader.GetString(3));
    }

    [TestMethod]
    public async Task StaffReopenedKnownLegacyAndSuccessfulHoldHistoryRemainIdentifierProtected()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        long actorId;
        long legacyRequestId;
        long successfulRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                     [Status], [CloseReason], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002140', N'Known legacy protected title', N'9780000002140', 1, @formatId,
                        N'closed', N'hold_cancelled', N'9040', N'found', DATEADD(day, -2, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @legacyId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [Message], [MetadataJson], [CreatedUtc])
                VALUES (@legacyId, N'legacy_status_imported', N'closed', N'system', N'Imported placement evidence.',
                        N'{"legacyBibProtection":true,"bibId":"9040"}', DATEADD(day, -1, SYSUTCDATETIME()));

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                     [Status], [CloseReason], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002141', N'Successful operation protected title', N'9780000002141', 1, @formatId,
                        N'closed', N'manual', N'9041', N'found', DATEADD(day, -2, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @successfulId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber], [State], [Phase],
                     [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc], [ResultCode], [PolarisHoldId], [OutcomeEvidenceKind])
                VALUES (@successfulId, N'20000000002141', N'9041', 1, N'succeeded', N'result_recorded', 1,
                        DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()), N'success', N'8141',
                        N'documented_create_success');
                SELECT @actorId, @legacyId, @successfulId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            legacyRequestId = reader.GetInt64(1);
            successfulRequestId = reader.GetInt64(2);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        async Task<JsonDocument> ReopenAsync(long requestId)
        {
            using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
            using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            using var reopen = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestId}/action",
                new { version = body.RootElement.GetProperty("version").GetString(), action = "reopen", status = "suggestion" });
            Assert.AreEqual(HttpStatusCode.OK, reopen.StatusCode, await reopen.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await reopen.Content.ReadAsStringAsync());
        }

        using var legacyReopened = await ReopenAsync(legacyRequestId);
        Assert.IsFalse(legacyReopened.RootElement.GetProperty("capabilities").GetProperty("canEditIdentifier").GetBoolean());
        using var legacyEdit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{legacyRequestId}/action",
            new
            {
                version = legacyReopened.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "suggestion",
                identifier = "9780000002199",
                bibid = "9999"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, legacyEdit.StatusCode, await legacyEdit.Content.ReadAsStringAsync());

        using var successfulReopened = await ReopenAsync(successfulRequestId);
        Assert.IsFalse(successfulReopened.RootElement.GetProperty("capabilities").GetProperty("canChangeBib").GetBoolean());
        using var successfulEdit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{successfulRequestId}/action",
            new
            {
                version = successfulReopened.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "suggestion",
                identifier = "9780000002141",
                bibid = (string?)null
            });
        Assert.AreEqual(HttpStatusCode.Conflict, successfulEdit.StatusCode, await successfulEdit.Content.ReadAsStringAsync());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT [Identifier] FROM [asap].[TitleRequest] WHERE [Id] = @legacyId),
                (SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @legacyId),
                (SELECT [Identifier] FROM [asap].[TitleRequest] WHERE [Id] = @successfulId),
                (SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @successfulId),
                (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                 WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), @legacyId) + N':%'
                    OR [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), @successfulId) + N':%');
            """,
            verify);
        command.Parameters.AddWithValue("@legacyId", legacyRequestId);
        command.Parameters.AddWithValue("@successfulId", successfulRequestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("9780000002140", result.GetString(0));
        Assert.AreEqual("9040", result.GetString(1));
        Assert.AreEqual("9780000002141", result.GetString(2));
        Assert.AreEqual("9041", result.GetString(3));
        Assert.AreEqual(0, result.GetInt32(4));
    }

    [TestMethod]
    public async Task StaffIdentifierMutationMatrixValidatesPrePlacementAndProtectedEndingStates()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        long actorId;
        long outstandingId;
        long pendingHoldId;
        long placedId;
        long closedId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                DECLARE @foundTagId bigint = (SELECT [Id] FROM [asap].[WorkflowTag] WHERE [Code] = N'polaris_bib_found');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId], [Status],
                     [BibId], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount], [IsbnCheckLastErrorCode],
                     [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002150', N'Outstanding identifier matrix', N'9780000002150', 1, @formatId,
                        N'outstanding_purchase', N'9050', N'found', N'Old result', 4, N'old_error', SYSUTCDATETIME(),
                        SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @outstandingId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId]) VALUES (@outstandingId, @foundTagId);

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId], [Status],
                     [BibId], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount], [IsbnCheckLastErrorCode],
                     [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002151', N'Pending hold identifier matrix', N'9780000002151', 1, @formatId,
                        N'pending_hold', N'9051', N'found', N'Old result', 3, N'old_error', SYSUTCDATETIME(),
                        SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @pendingHoldId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestWorkflowTag] ([TitleRequestId], [WorkflowTagId]) VALUES (@pendingHoldId, @foundTagId);

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId], [Status],
                     [BibId], [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002152', N'Placed identifier matrix', N'9780000002152', 1, @formatId,
                        N'hold_placed', N'9052', N'found', N'Placed result', 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @placedId bigint = SCOPE_IDENTITY();

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId], [Status],
                     [CloseReason], [BibId], [IsbnCheckStatus], [IsbnCheckResult], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002153', N'Closed identifier matrix', N'9780000002153', 1, @formatId,
                        N'closed', N'rejected', N'9053', N'found', N'Closed result', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @closedId bigint = SCOPE_IDENTITY();
                SELECT @actorId, @outstandingId, @pendingHoldId, @placedId, @closedId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            outstandingId = reader.GetInt64(1);
            pendingHoldId = reader.GetInt64(2);
            placedId = reader.GetInt64(3);
            closedId = reader.GetInt64(4);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        async Task<JsonDocument> GetAsync(long requestId)
        {
            using var response = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        using var outstanding = await GetAsync(outstandingId);
        using var outstandingEdit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{outstandingId}/action",
            new
            {
                version = outstanding.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "outstanding_purchase",
                identifier = "9780000002190"
            });
        Assert.AreEqual(HttpStatusCode.OK, outstandingEdit.StatusCode, await outstandingEdit.Content.ReadAsStringAsync());

        using var pending = await GetAsync(pendingHoldId);
        using var pendingClear = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{pendingHoldId}/action",
            new
            {
                version = pending.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "pending_hold",
                identifier = (string?)null
            });
        Assert.AreEqual(HttpStatusCode.OK, pendingClear.StatusCode, await pendingClear.Content.ReadAsStringAsync());

        using var placed = await GetAsync(placedId);
        using var placedUnchanged = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{placedId}/action",
            new
            {
                version = placed.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "hold_placed",
                title = "Placed descriptive edit",
                identifier = " 9780000002152 "
            });
        Assert.AreEqual(HttpStatusCode.OK, placedUnchanged.StatusCode, await placedUnchanged.Content.ReadAsStringAsync());
        using var placedUnchangedBody = JsonDocument.Parse(await placedUnchanged.Content.ReadAsStringAsync());
        using var placedChanged = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{placedId}/action",
            new
            {
                version = placedUnchangedBody.RootElement.GetProperty("version").GetString(),
                action = "edit",
                status = "hold_placed",
                identifier = "9780000002192"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, placedChanged.StatusCode, await placedChanged.Content.ReadAsStringAsync());

        using var closed = await GetAsync(closedId);
        using var combinedReopenEdit = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{closedId}/action",
            new
            {
                version = closed.RootElement.GetProperty("version").GetString(),
                action = "reopen",
                status = "suggestion",
                identifier = "9780000002193",
                bibid = "9093"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, combinedReopenEdit.StatusCode, await combinedReopenEdit.Content.ReadAsStringAsync());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Id], request.[Status], request.[Title], request.[Identifier], request.[BibId],
                   request.[IsbnCheckStatus], request.[IsbnCheckResult], request.[IsbnCheckRetryCount],
                   request.[IsbnCheckLastErrorCode], request.[LastCheckedUtc],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestWorkflowTag] tag WHERE tag.[TitleRequestId] = request.[Id])
            FROM [asap].[TitleRequest] request
            WHERE request.[Id] IN (@outstandingId, @pendingHoldId, @placedId, @closedId)
            ORDER BY request.[Id];
            """,
            verify);
        command.Parameters.AddWithValue("@outstandingId", outstandingId);
        command.Parameters.AddWithValue("@pendingHoldId", pendingHoldId);
        command.Parameters.AddWithValue("@placedId", placedId);
        command.Parameters.AddWithValue("@closedId", closedId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(outstandingId, result.GetInt64(0));
        Assert.AreEqual("outstanding_purchase", result.GetString(1));
        Assert.AreEqual("9780000002190", result.GetString(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual("pending", result.GetString(5));
        Assert.IsTrue(result.IsDBNull(6));
        Assert.AreEqual(0, result.GetInt32(7));
        Assert.IsTrue(result.IsDBNull(8));
        Assert.IsTrue(result.IsDBNull(9));
        Assert.AreEqual(0, result.GetInt32(10));

        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(pendingHoldId, result.GetInt64(0));
        Assert.AreEqual("pending_hold", result.GetString(1));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual("skipped_no_isbn", result.GetString(5));
        Assert.IsTrue(result.IsDBNull(6));
        Assert.AreEqual(0, result.GetInt32(7));
        Assert.IsTrue(result.IsDBNull(8));
        Assert.IsTrue(result.IsDBNull(9));
        Assert.AreEqual(0, result.GetInt32(10));

        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(placedId, result.GetInt64(0));
        Assert.AreEqual("hold_placed", result.GetString(1));
        Assert.AreEqual("Placed descriptive edit", result.GetString(2));
        Assert.AreEqual("9780000002152", result.GetString(3));
        Assert.AreEqual("9052", result.GetString(4));
        Assert.AreEqual("found", result.GetString(5));
        Assert.AreEqual("Placed result", result.GetString(6));

        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(closedId, result.GetInt64(0));
        Assert.AreEqual("closed", result.GetString(1));
        Assert.AreEqual("9780000002153", result.GetString(3));
        Assert.AreEqual("9053", result.GetString(4));
        Assert.AreEqual("found", result.GetString(5));
        Assert.AreEqual("Closed result", result.GetString(6));
    }

    [TestMethod]
    public async Task StaffClosedRequestDeletionWritesReducedAuditAndRetainsHoldHistory()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        long actorId;
        long deletableId;
        long retainedId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LegacyId], [LibraryOrganizationId], [Barcode], [Email], [Title], [Author], [Identifier], [Notes],
                     [AutoHold], [MaterialFormatId], [Status], [CloseReason], [BibId], [CreatedUtc], [UpdatedUtc])
                VALUES (N'legacy-delete-2142', 2, N'20000000002222', N'patron-private@example.org', N'Deleted audit title',
                        N'Deleted author', N'9780000002142', N'Private freeform note', 0, @formatId, N'closed', N'rejected',
                        N'9042', DATEADD(day, -4, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @deletableId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[TitleRequestEvent]
                    ([TitleRequestId], [EventType], [Status], [ActorType], [Message], [CreatedUtc])
                VALUES (@deletableId, N'created', N'closed', N'system', N'Historical event.', DATEADD(day, -3, SYSUTCDATETIME()));

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [CloseReason],
                     [BibId], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002143', N'Retained hold history title', 1, @formatId, N'closed', N'manual', N'9043',
                        DATEADD(day, -2, SYSUTCDATETIME()), SYSUTCDATETIME());
                DECLARE @retainedId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber], [State], [Phase],
                     [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc], [ResultCode], [PolarisHoldId])
                VALUES (@retainedId, N'20000000002143', N'9043', 1, N'succeeded', N'result_recorded', 1,
                        DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()), N'success', N'8143');
                SELECT @actorId, @deletableId, @retainedId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            deletableId = reader.GetInt64(1);
            retainedId = reader.GetInt64(2);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        async Task<string> VersionAsync(long requestId)
        {
            using var response = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("version").GetString()!;
        }

        using var retained = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{retainedId}")
        {
            Content = JsonContent.Create(new { version = await VersionAsync(retainedId) })
        };
        using var retainedResponse = await client.SendAsync(retained);
        Assert.AreEqual(HttpStatusCode.Conflict, retainedResponse.StatusCode, await retainedResponse.Content.ReadAsStringAsync());
        using (var retainedBody = JsonDocument.Parse(await retainedResponse.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("hold_history_retained", retainedBody.RootElement.GetProperty("code").GetString());
        }

        using var stale = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{deletableId}")
        {
            Content = JsonContent.Create(new { version = Convert.ToBase64String(new byte[8]) })
        };
        using var staleResponse = await client.SendAsync(stale);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode, await staleResponse.Content.ReadAsStringAsync());

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{deletableId}")
        {
            Content = JsonContent.Create(new { version = await VersionAsync(deletableId) })
        };
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT audit.[RequestType], audit.[OriginalRequestKey], audit.[LibraryOrganizationId], audit.[Title],
                   audit.[Author], audit.[Identifier], audit.[BibId], audit.[Status], audit.[CloseReason],
                   audit.[MaskedBarcode], audit.[DeletedByStaffUserId], audit.[DeletedByDisplayName],
                   (SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Id] = @deletableId),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @deletableId),
                   (SELECT COUNT(*) FROM [asap].[TitleRequest] WHERE [Id] = @retainedId)
            FROM [asap].[DeletedRequestAudit] audit
            WHERE audit.[OriginalRequestKey] = N'legacy-delete-2142';
            """,
            verify);
        command.Parameters.AddWithValue("@deletableId", deletableId);
        command.Parameters.AddWithValue("@retainedId", retainedId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("title_request", result.GetString(0));
        Assert.AreEqual("legacy-delete-2142", result.GetString(1));
        Assert.AreEqual(2, result.GetInt32(2));
        Assert.AreEqual("Deleted audit title", result.GetString(3));
        Assert.AreEqual("Deleted author", result.GetString(4));
        Assert.AreEqual("9780000002142", result.GetString(5));
        Assert.AreEqual("9042", result.GetString(6));
        Assert.AreEqual("closed", result.GetString(7));
        Assert.AreEqual("rejected", result.GetString(8));
        Assert.AreEqual("***2222", result.GetString(9));
        Assert.AreEqual(actorId, result.GetInt64(10));
        Assert.AreEqual("Test Administrator", result.GetString(11));
        Assert.AreEqual(0, result.GetInt32(12));
        Assert.AreEqual(0, result.GetInt32(13));
        Assert.AreEqual(1, result.GetInt32(14));
    }

    [TestMethod]
    public async Task StaffIdentifierRetryQueuesTheCanonicalProcessorAfterAtomicReset()
    {
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        long actorId;
        long requestId;
        int initialIdentifierJobCount;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId], [Status],
                     [IsbnCheckStatus], [IsbnCheckResult], [IsbnCheckRetryCount], [IsbnCheckLastErrorCode],
                     [LastCheckedUtc], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002110', N'Identifier retry title', N'9780000002110', 1, @formatId, N'suggestion',
                        N'error_max_retries', N'Upstream unavailable.', 5, N'provider_timeout',
                        SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
            await reader.CloseAsync();

            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM [HangFire].[Job] WHERE [InvocationData] LIKE N'%IdentifierLookupJobs%';",
                connection);
            initialIdentifierJobCount = Convert.ToInt32(await count.ExecuteScalarAsync());
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var retry = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/retry-identifier-check",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode, await retry.Content.ReadAsStringAsync());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT r.[IsbnCheckStatus], r.[IsbnCheckRetryCount], r.[IsbnCheckLastErrorCode], r.[IsbnCheckResult],
                   r.[LastCheckedUtc],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = r.[Id] AND [EventType] = N'identifier_retry_requested'),
                   (SELECT COUNT(*) FROM [HangFire].[Job]
                    WHERE [InvocationData] LIKE N'%IdentifierLookupJobs%')
            FROM [asap].[TitleRequest] r
            WHERE r.[Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("pending", result.GetString(0));
        Assert.AreEqual(0, result.GetInt32(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual(1, result.GetInt32(5));
        Assert.AreEqual(initialIdentifierJobCount + 1, result.GetInt32(6));
    }

    [TestMethod]
    public async Task StaffExplicitBibChangeRequiresProviderValidationBeforeLocalMutation()
    {
        var rejectingProvider = new RejectingBibStaffProvider();
        await using var rejectingFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(rejectingProvider);
            }));
        using var client = rejectingFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                     [Status], [BibId], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002103', N'BIB validation title', N'9780000000213', 1, @formatId,
                        N'suggestion', N'9001', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var version = getBody.RootElement.GetProperty("version").GetString();
        using var update = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "edit", status = "suggestion", bibid = "7777" });

        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var updateBody = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_not_found", updateBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, rejectingProvider.ValidationCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        Assert.AreEqual("9001", await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task StaffPickupUpdateChecksBarrierBeforeProviderAndCommitsOnlySuccessfulResult()
    {
        var pickupProvider = new ControllablePickupPatronProvider();
        await using var pickupFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider>(pickupProvider);
            }));
        using var client = pickupFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002104', N'Pickup workflow title', 1, @formatId, N'suggestion',
                        101, N'Main Library', N'skipped_no_isbn', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var options = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-options",
            new { forceRefresh = false });
        Assert.AreEqual(HttpStatusCode.OK, options.StatusCode, await options.Content.ReadAsStringAsync());
        using var optionsBody = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
        var version = optionsBody.RootElement.GetProperty("version").GetString();
        Assert.AreEqual(101, optionsBody.RootElement.GetProperty("currentPreferredPickupBranchId").GetInt32());
        Assert.HasCount(2, optionsBody.RootElement.GetProperty("pickupBranches").EnumerateArray().ToArray());

        await SetIncompleteOperationAsync(requestId, present: true);
        using var blocked = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-preference",
            new { version, preferredPickupBranchId = 102, currentPreferredPickupBranchIdAtLoad = 101 });
        Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode, await blocked.Content.ReadAsStringAsync());
        Assert.AreEqual(0, pickupProvider.UpdateCount);

        await SetIncompleteOperationAsync(requestId, present: false);
        pickupProvider.FailUpdate = true;
        using var failed = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-preference",
            new { version, preferredPickupBranchId = 102, currentPreferredPickupBranchIdAtLoad = 101 });
        Assert.AreEqual(HttpStatusCode.BadGateway, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        Assert.AreEqual(1, pickupProvider.UpdateCount);
        Assert.AreEqual(101, await ReadPickupSnapshotAsync(requestId));

        pickupProvider.FailUpdate = false;
        using var updated = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-preference",
            new { version, preferredPickupBranchId = 102, currentPreferredPickupBranchIdAtLoad = 101 });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        Assert.AreEqual(2, pickupProvider.UpdateCount);
        Assert.AreEqual(102, pickupProvider.CurrentPickupBranchId);
        Assert.AreEqual(102, await ReadPickupSnapshotAsync(requestId));

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var eventCount = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id AND [EventType] = N'pickup_preference_changed';",
            verify);
        eventCount.Parameters.AddWithValue("@id", requestId);
        Assert.AreEqual(1, Convert.ToInt32(await eventCount.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task StaffHoldPlacementPersistsCreateReplyBoundariesAndFinalIdentity()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002105', N'Hold placement title', 1, @formatId, N'pending_hold', N'9001',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var placed = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, placed.StatusCode, await placed.Content.ReadAsStringAsync());
        using var placedBody = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_placed", placedBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT o.[State], o.[Phase], o.[PolarisRequestGuid], o.[PolarisHoldId],
                   o.[ReplyAnswer], o.[ReplyState], o.[ProviderStatusType], o.[ProviderStatusValue],
                   o.[CreateStartedUtc], o.[CreateResponseObservedUtc], o.[ReplyStartedUtc],
                   o.[ReplyResponseObservedUtc], o.[CompletedUtc], r.[Status]
            FROM [asap].[HoldPlacementOperation] o
            JOIN [asap].[TitleRequest] r ON r.[Id] = o.[TitleRequestId]
            WHERE o.[TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("succeeded", result.GetString(0));
        Assert.AreEqual("result_recorded", result.GetString(1));
        Assert.AreEqual(holdProvider.RequestGuid.ToString(), result.GetString(2));
        Assert.AreEqual("8123", result.GetString(3));
        Assert.AreEqual("1", result.GetString(4));
        Assert.AreEqual("3", result.GetString(5));
        Assert.AreEqual("2", result.GetString(6));
        Assert.AreEqual("0", result.GetString(7));
        for (var index = 8; index <= 12; index++) Assert.IsFalse(result.IsDBNull(index));
        Assert.AreEqual("hold_placed", result.GetString(13));
    }

    [TestMethod]
    public async Task StaffHoldPlacementAdoptsOneLiveSameBibHoldWithoutMutationMarkers()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.Holds = [new PolarisHoldSnapshot(8451, 9002, 2, "Active", 101)];
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002107', N'Existing hold adoption title', 1, @formatId, N'pending_hold', N'9002',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var placed = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, placed.StatusCode, await placed.Content.ReadAsStringAsync());
        using var placedBody = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_placed", placedBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [State], [Phase], [PolarisHoldId], [OutcomeEvidenceKind], [CreateStartedUtc], [ReplyStartedUtc]
            FROM [asap].[HoldPlacementOperation]
            WHERE [TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("succeeded", result.GetString(0));
        Assert.AreEqual("result_recorded", result.GetString(1));
        Assert.AreEqual("8451", result.GetString(2));
        Assert.AreEqual("existing_hold_adoption", result.GetString(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
    }

    [TestMethod]
    public async Task StaffHoldPlacementPersistsAmbiguityWhenCreateThrowsAfterDispatchMarker()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.CreateException = new PolarisOperationalException(
            "testing_create_transport_error",
            "Testing create transport error.");
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002108', N'Throwing hold placement title', 1, @formatId, N'pending_hold', N'9003',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var placement = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, placement.StatusCode, await placement.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        using var blocked = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var operation = blockedBody.RootElement.GetProperty("holdOperation");
        Assert.AreEqual("operator_required", operation.GetProperty("state").GetString());
        Assert.AreEqual("create_started", operation.GetProperty("phase").GetString());
        Assert.AreEqual("hold_identity_ambiguous", operation.GetProperty("lastErrorCode").GetString());
    }

    [TestMethod]
    public async Task StaffAmbiguousCreateDoesNotInferSuccessFromUnrelatedNewSameBibHold()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.HoldAfterCreate = new PolarisHoldSnapshot(8452, 9004, 2, "Active", 101);
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002109', N'Correlated ambiguous hold title', 1, @formatId, N'pending_hold', N'9004',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var placed = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, placed.StatusCode, await placed.Content.ReadAsStringAsync());
        using var placedBody = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_operator_required", placedBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT o.[State], o.[Phase], o.[PolarisHoldId], o.[OutcomeEvidenceKind], o.[LastErrorCode],
                   o.[CreateStartedUtc], o.[CreateResponseObservedUtc], r.[Status]
            FROM [asap].[HoldPlacementOperation] o
            JOIN [asap].[TitleRequest] r ON r.[Id] = o.[TitleRequestId]
            WHERE o.[TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("operator_required", result.GetString(0));
        Assert.AreEqual("create_started", result.GetString(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.AreEqual("create_transport_ambiguous", result.GetString(3));
        Assert.AreEqual("hold_identity_ambiguous", result.GetString(4));
        Assert.IsFalse(result.IsDBNull(5));
        Assert.IsFalse(result.IsDBNull(6));
        Assert.AreEqual("pending_hold", result.GetString(7));
    }

    [TestMethod]
    public async Task StaffHoldReconcileTakesOverExpiredAcquiredWorkForInactiveLibrary()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                VALUES (91211, N'Inactive recovery library', N'IRL', 0);
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (91211, N'20000000002111', N'Expired acquired hold title', 1, @formatId, N'pending_hold', N'9005',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch], [LeaseExpiresUtc], [RequestStartedUtc])
                VALUES (@requestId, N'20000000002111', N'9005', 101, 1, N'in_progress', N'acquired',
                        NEWID(), 1, DATEADD(minute, -1, SYSUTCDATETIME()), SYSUTCDATETIME());
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var operation = getBody.RootElement.GetProperty("holdOperation");
        Assert.IsTrue(operation.GetProperty("canReconcile").GetBoolean());
        Assert.IsFalse(operation.GetProperty("canResolveSucceeded").GetBoolean());

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        using var reconcileBody = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual("updated", reconcileBody.RootElement.GetProperty("code").GetString());
        using var refreshed = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var refreshedBody = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_placed", refreshedBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [AttemptNumber], [ExecutionEpoch], [State], [CompletedUtc], [RecoveryAttemptCount], [LastRecoveryUtc]
            FROM [asap].[HoldPlacementOperation]
            WHERE [TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(1, result.GetInt32(0));
        Assert.AreEqual(2L, result.GetInt64(1));
        Assert.AreEqual("succeeded", result.GetString(2));
        Assert.IsFalse(result.IsDBNull(3));
        Assert.AreEqual(1, result.GetInt32(4));
        Assert.IsFalse(result.IsDBNull(5));
    }

    [TestMethod]
    public async Task StaffHoldReconcileResumesDurableReplyReadyWithoutRepeatingCreate()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002130', N'Reply ready recovery title', 1, @formatId, N'pending_hold', N'9030',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot],
                     [PickupBranchIdSnapshot], [RequestingOrganizationIdSnapshot], [WorkstationIdSnapshot],
                     [PolarisUserIdSnapshot], [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch],
                     [LeaseExpiresUtc], [RequestStartedUtc], [CreateStartedUtc], [CreateResponseObservedUtc],
                     [PolarisRequestGuid], [TxnGroupQualifier], [TxnQualifier], [ReplyAnswer], [ReplyState],
                     [ProviderStatusType], [ProviderStatusValue], [ResultCode], [OutcomeEvidenceKind])
                VALUES (@requestId, N'20000000002130', N'7130', N'9030', 101, 1, 1, N'1', 1,
                        N'in_progress', N'reply_ready', NEWID(), 1, DATEADD(minute, -1, SYSUTCDATETIME()),
                        DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(minute, -4, SYSUTCDATETIME()),
                        DATEADD(minute, -3, SYSUTCDATETIME()), @requestGuid, N'group-qualifier',
                        N'transaction-qualifier', N'1', N'3', N'3', N'5', N'reply_required',
                        N'create_status_5_reply_required');
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            seed.Parameters.AddWithValue("@requestGuid", holdProvider.RequestGuid.ToString());
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var operation = getBody.RootElement.GetProperty("holdOperation");

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Status], operation.[State], operation.[Phase], operation.[ExecutionEpoch],
                   operation.[ReplyStartedUtc], operation.[ReplyResponseObservedUtc], operation.[PolarisHoldId],
                   operation.[RecoveryAttemptCount],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'hold_placed')
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[TitleRequestId] = request.[Id]
            WHERE request.[Id] = @requestId;
            """,
            verify);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("hold_placed", result.GetString(0));
        Assert.AreEqual("succeeded", result.GetString(1));
        Assert.AreEqual("result_recorded", result.GetString(2));
        Assert.AreEqual(2L, result.GetInt64(3));
        Assert.IsFalse(result.IsDBNull(4));
        Assert.IsFalse(result.IsDBNull(5));
        Assert.AreEqual("8123", result.GetString(6));
        Assert.AreEqual(1, result.GetInt32(7));
        Assert.AreEqual(1, result.GetInt32(8));
    }

    [TestMethod]
    public async Task StaffHoldLeaseTakeoverFencesLateOriginalCreateResult()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.BlockCreate();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/asap/staff/session")).StatusCode);

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002131', N'Late create result title', 1, @formatId, N'pending_hold', N'9031',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        AddTestingStaffHeaders(client, actorId, Guid.Parse(identity.TenantId!), identity.UserPrincipalName!);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var request = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var requestBody = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        var original = client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = requestBody.RootElement.GetProperty("version").GetString() });
        await holdProvider.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var competing = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = requestBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, competing.StatusCode, await competing.Content.ReadAsStringAsync());
        using (var competingBody = JsonDocument.Parse(await competing.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("hold_operation_incomplete", competingBody.RootElement.GetProperty("code").GetString());
        }
        Assert.AreEqual(1, holdProvider.CreateCount);

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var expire = new SqlCommand(
                "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [TitleRequestId] = @requestId;",
                connection);
            expire.Parameters.AddWithValue("@requestId", requestId);
            Assert.AreEqual(1, await expire.ExecuteNonQueryAsync());
        }

        using var blocked = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var blockedOperation = blockedBody.RootElement.GetProperty("holdOperation");
        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{blockedOperation.GetProperty("id").GetString()}/reconcile",
            new { version = blockedOperation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());

        holdProvider.CompleteBlockedCreate(new HoldProviderResult(
            HoldProviderOutcome.FinalSuccess,
            null,
            "8131",
            null,
            null,
            2,
            0,
            "documented_create_success"));
        using var originalResponse = await original;
        Assert.AreEqual(HttpStatusCode.Conflict, originalResponse.StatusCode, await originalResponse.Content.ReadAsStringAsync());
        using var originalBody = JsonDocument.Parse(await originalResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("operation_ownership_lost", originalBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Status], operation.[State], operation.[Phase], operation.[ExecutionEpoch],
                   operation.[OwnerToken], operation.[LeaseExpiresUtc], operation.[CompletedUtc], operation.[PolarisHoldId],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                    WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), @requestId) + N':%')
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[TitleRequestId] = request.[Id]
            WHERE request.[Id] = @requestId;
            """,
            verify);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("pending_hold", result.GetString(0));
        Assert.AreEqual("operator_required", result.GetString(1));
        Assert.AreEqual("create_started", result.GetString(2));
        Assert.AreEqual(2L, result.GetInt64(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
        Assert.IsTrue(result.IsDBNull(6));
        Assert.IsTrue(result.IsDBNull(7));
        Assert.AreEqual(0, result.GetInt32(8));
        Assert.AreEqual(0, result.GetInt32(9));
    }

    [TestMethod]
    public async Task StaffHoldLeaseTakeoverFencesLateOriginalReplyResult()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        holdProvider.BlockReply();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var requestId = await SeedPendingHoldRequestAsync("Late reply result title", "20000000002132", "9032");
        using var request = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var requestBody = JsonDocument.Parse(await request.Content.ReadAsStringAsync());

        var original = client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = requestBody.RootElement.GetProperty("version").GetString() });
        await holdProvider.ReplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        await ExecuteNonQueryAsync(
            "UPDATE [asap].[HoldPlacementOperation] SET [LeaseExpiresUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [TitleRequestId] = @requestId;",
            ("@requestId", requestId));
        using var blocked = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var operation = blockedBody.RootElement.GetProperty("holdOperation");
        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());

        holdProvider.CompleteBlockedReply(new HoldProviderResult(
            HoldProviderOutcome.FinalSuccess,
            holdProvider.RequestGuid.ToString(),
            "8132",
            "group-qualifier",
            "transaction-qualifier",
            2,
            0,
            "documented_reply_success"));
        using var originalResponse = await original;
        Assert.AreEqual(HttpStatusCode.Conflict, originalResponse.StatusCode, await originalResponse.Content.ReadAsStringAsync());
        using var originalBody = JsonDocument.Parse(await originalResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("operation_ownership_lost", originalBody.RootElement.GetProperty("code").GetString());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Status], operation.[State], operation.[Phase], operation.[ExecutionEpoch],
                   operation.[OwnerToken], operation.[CompletedUtc], operation.[PolarisHoldId],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                    WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), @requestId) + N':%')
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[TitleRequestId] = request.[Id]
            WHERE request.[Id] = @requestId;
            """,
            verify);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("pending_hold", result.GetString(0));
        Assert.AreEqual("operator_required", result.GetString(1));
        Assert.AreEqual("reply_started", result.GetString(2));
        Assert.AreEqual(2L, result.GetInt64(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
        Assert.IsTrue(result.IsDBNull(6));
        Assert.AreEqual(0, result.GetInt32(7));
        Assert.AreEqual(0, result.GetInt32(8));
    }

    [TestMethod]
    public async Task StaffCompletedNullIdentityDoesNotInferCorrelationFromSoleSameBibHold()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.Holds = [new PolarisHoldSnapshot(8453, 9006, 2, "Active", 101)];
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002112', N'Identity enrichment title', 1, @formatId, N'hold_placed', N'9006',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc],
                     [ResultCode], [OutcomeEvidenceKind], [LastErrorCode])
                VALUES (@requestId, N'20000000002112', N'9006', 101, 1, N'succeeded', N'result_recorded', 1,
                        DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(minute, -4, SYSUTCDATETIME()),
                        N'success', N'provider_final_success_uncorrelated', N'hold_identity_unavailable');
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var operation = getBody.RootElement.GetProperty("holdOperation");
        Assert.AreEqual("succeeded", operation.GetProperty("state").GetString());
        Assert.AreEqual("hold_identity_unavailable", operation.GetProperty("lastErrorCode").GetString());
        Assert.IsFalse(operation.GetProperty("canReconcile").GetBoolean());

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        using var reconcileBody = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_operation_completed", reconcileBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);
        Assert.AreEqual(0, holdProvider.HoldReadCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT o.[State], o.[Phase], o.[PolarisHoldId], o.[OutcomeEvidenceKind], o.[LastErrorCode],
                   o.[CompletedUtc],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), @id) + N':%')
            FROM [asap].[HoldPlacementOperation] o
            WHERE o.[TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("succeeded", result.GetString(0));
        Assert.AreEqual("result_recorded", result.GetString(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.AreEqual("provider_final_success_uncorrelated", result.GetString(3));
        Assert.AreEqual("hold_identity_unavailable", result.GetString(4));
        Assert.IsFalse(result.IsDBNull(5));
        Assert.AreEqual(0, result.GetInt32(6));
        Assert.AreEqual(0, result.GetInt32(7));
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentWritesOnlyProvenIdentityEvidence()
    {
        long requestId;
        long operationId;
        byte[] requestVersion;
        byte[] operationVersion;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002120', N'Completed identity enrichment title', 1, @formatId, N'hold_placed', N'9020',
                        101, N'Main Library', N'found', DATEADD(minute, -8, SYSUTCDATETIME()), DATEADD(minute, -4, SYSUTCDATETIME()));
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CreateStartedUtc],
                     [CreateResponseObservedUtc], [CompletedUtc], [PolarisRequestGuid], [ProviderStatusType],
                     [ProviderStatusValue], [ResultCode], [OutcomeEvidenceKind], [RecoveryAttemptCount], [LastRecoveryUtc],
                     [LastErrorCode], [DetailJson])
                VALUES (@requestId, N'20000000002120', N'7120', N'9020', 101,
                        2, N'succeeded', N'result_recorded', 3, DATEADD(minute, -7, SYSUTCDATETIME()),
                        DATEADD(minute, -6, SYSUTCDATETIME()), DATEADD(minute, -5, SYSUTCDATETIME()),
                        DATEADD(minute, -4, SYSUTCDATETIME()), N'9b934869-b681-4523-b2fb-8604dd0d0832', N'2', N'0',
                        N'success', N'provider_final_success_uncorrelated', 1, DATEADD(minute, -3, SYSUTCDATETIME()),
                        N'hold_identity_unavailable', N'{"providerResult":"success"}');
                DECLARE @operationId bigint = SCOPE_IDENTITY();
                SELECT @requestId, @operationId, request.[RowVersion], operation.[RowVersion]
                FROM [asap].[TitleRequest] request
                JOIN [asap].[HoldPlacementOperation] operation ON operation.[Id] = @operationId
                WHERE request.[Id] = @requestId;
                """;
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            requestId = reader.GetInt64(0);
            operationId = reader.GetInt64(1);
            requestVersion = (byte[])reader[2];
            operationVersion = (byte[])reader[3];
        }

        var result = await factory!.Services.GetRequiredService<HoldPlacementService>()
            .RecordCompletedIdentityAsync(
                operationId,
                requestVersion,
                operationVersion,
                new ProvenHoldIdentityEvidence("8460", "provider-operation-proof-9020"),
                CancellationToken.None);
        Assert.AreEqual("updated", result.Code);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Status], request.[Barcode], request.[BibId], request.[UpdatedUtc],
                   operation.[State], operation.[Phase], operation.[CompletedUtc], operation.[BibIdSnapshot],
                   operation.[AttemptNumber], operation.[ExecutionEpoch], operation.[RecoveryAttemptCount],
                   operation.[LastRecoveryUtc], operation.[OwnerToken], operation.[LeaseExpiresUtc],
                   operation.[PolarisRequestGuid], operation.[ProviderStatusType], operation.[ProviderStatusValue],
                   operation.[ResultCode], operation.[PolarisHoldId], operation.[OutcomeEvidenceKind],
                   operation.[LastErrorCode], operation.[DetailJson],
                   CASE WHEN request.[RowVersion] = @requestVersion THEN 1 ELSE 0 END,
                   CASE WHEN operation.[RowVersion] = @operationVersion THEN 1 ELSE 0 END,
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = request.[Id]),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(30), request.[Id]) + N':%')
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[Id] = @operationId
            WHERE request.[Id] = @requestId;
            """,
            verify);
        command.Parameters.AddWithValue("@requestId", requestId);
        command.Parameters.AddWithValue("@operationId", operationId);
        command.Parameters.AddWithValue("@requestVersion", requestVersion);
        command.Parameters.AddWithValue("@operationVersion", operationVersion);
        await using var enriched = await command.ExecuteReaderAsync();
        Assert.IsTrue(await enriched.ReadAsync());
        Assert.AreEqual("hold_placed", enriched.GetString(0));
        Assert.AreEqual("20000000002120", enriched.GetString(1));
        Assert.AreEqual("9020", enriched.GetString(2));
        Assert.AreEqual("succeeded", enriched.GetString(4));
        Assert.AreEqual("result_recorded", enriched.GetString(5));
        Assert.IsFalse(enriched.IsDBNull(6));
        Assert.AreEqual("9020", enriched.GetString(7));
        Assert.AreEqual(2, enriched.GetInt32(8));
        Assert.AreEqual(3L, enriched.GetInt64(9));
        Assert.AreEqual(1, enriched.GetInt32(10));
        Assert.IsFalse(enriched.IsDBNull(11));
        Assert.IsTrue(enriched.IsDBNull(12));
        Assert.IsTrue(enriched.IsDBNull(13));
        Assert.AreEqual("9b934869-b681-4523-b2fb-8604dd0d0832", enriched.GetString(14));
        Assert.AreEqual("2", enriched.GetString(15));
        Assert.AreEqual("0", enriched.GetString(16));
        Assert.AreEqual("success", enriched.GetString(17));
        Assert.AreEqual("8460", enriched.GetString(18));
        Assert.AreEqual("authoritative_provider_operation_correlation", enriched.GetString(19));
        Assert.IsTrue(enriched.IsDBNull(20));
        using (var detail = JsonDocument.Parse(enriched.GetString(21)))
        {
            Assert.AreEqual("success", detail.RootElement.GetProperty("providerResult").GetString());
            Assert.AreEqual(
                "provider-operation-proof-9020",
                detail.RootElement.GetProperty("identityCorrelation").GetProperty("evidenceReference").GetString());
        }
        Assert.AreEqual(1, enriched.GetInt32(22), "The request rowversion must not change.");
        Assert.AreEqual(0, enriched.GetInt32(23), "The operation rowversion must fence the identity write.");
        Assert.AreEqual(0, enriched.GetInt32(24));
        Assert.AreEqual(0, enriched.GetInt32(25));
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentAllowsOnlyOneCompetingWriter()
    {
        var seeded = await SeedCompletedHoldIdentityAsync("competing", "20000000002121", "9021");
        var service = factory!.Services.GetRequiredService<HoldPlacementService>();
        var writes = await Task.WhenAll(
            service.RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence("8461", "provider-operation-proof-9021-a"),
                CancellationToken.None),
            service.RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence("8462", "provider-operation-proof-9021-b"),
                CancellationToken.None));
        CollectionAssert.AreEquivalent(
            new[] { "updated", "stale_version" },
            writes.Select(item => item.Code).ToArray());

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [PolarisHoldId], [DetailJson] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
            connection);
        command.Parameters.AddWithValue("@id", seeded.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        var storedId = reader.GetString(0);
        using var detail = JsonDocument.Parse(reader.GetString(1));
        var storedReference = detail.RootElement
            .GetProperty("identityCorrelation")
            .GetProperty("evidenceReference")
            .GetString();
        Assert.IsTrue(
            storedId == "8461" && storedReference == "provider-operation-proof-9021-a" ||
            storedId == "8462" && storedReference == "provider-operation-proof-9021-b");
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentNeverReplacesExistingIdentity()
    {
        var seeded = await SeedCompletedHoldIdentityAsync(
            "existing",
            "20000000002122",
            "9022",
            holdRequestId: "8463");
        var result = await factory!.Services.GetRequiredService<HoldPlacementService>()
            .RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence("9999", "provider-operation-proof-9022"),
                CancellationToken.None);
        Assert.AreEqual("hold_identity_already_recorded", result.Code);
        Assert.AreEqual("8463", await ReadHoldIdentityAsync(seeded.OperationId));
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentRequiresCurrentRequestAndOperationVersions()
    {
        var staleRequest = await SeedCompletedHoldIdentityAsync("stale-request", "20000000002123", "9023");
        var staleOperation = await SeedCompletedHoldIdentityAsync("stale-operation", "20000000002124", "9024");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE [asap].[TitleRequest] SET [Notes] = N'concurrent request edit' WHERE [Id] = @requestId;
                UPDATE [asap].[HoldPlacementOperation] SET [DetailJson] = N'{"concurrent":true}' WHERE [Id] = @operationId;
                """;
            update.Parameters.AddWithValue("@requestId", staleRequest.RequestId);
            update.Parameters.AddWithValue("@operationId", staleOperation.OperationId);
            Assert.AreEqual(2, await update.ExecuteNonQueryAsync());
        }

        var service = factory!.Services.GetRequiredService<HoldPlacementService>();
        var requestResult = await service.RecordCompletedIdentityAsync(
            staleRequest.OperationId,
            staleRequest.RequestVersion,
            staleRequest.OperationVersion,
            new ProvenHoldIdentityEvidence("8464", "provider-operation-proof-9023"),
            CancellationToken.None);
        var operationResult = await service.RecordCompletedIdentityAsync(
            staleOperation.OperationId,
            staleOperation.RequestVersion,
            staleOperation.OperationVersion,
            new ProvenHoldIdentityEvidence("8465", "provider-operation-proof-9024"),
            CancellationToken.None);
        Assert.AreEqual("stale_version", requestResult.Code);
        Assert.AreEqual("stale_version", operationResult.Code);
        Assert.IsNull(await ReadHoldIdentityAsync(staleRequest.OperationId));
        Assert.IsNull(await ReadHoldIdentityAsync(staleOperation.OperationId));
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentRequiresCurrentPatronBibAndLatestSuccess()
    {
        var changedPatron = await SeedCompletedHoldIdentityAsync(
            "changed-patron",
            "20000000002125",
            "9025",
            operationBarcode: "20000000009999");
        var changedBib = await SeedCompletedHoldIdentityAsync(
            "changed-bib",
            "20000000002126",
            "9026",
            operationBibId: "9999");
        var superseded = await SeedCompletedHoldIdentityAsync("superseded", "20000000002127", "9027");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var later = connection.CreateCommand();
            later.CommandText =
                """
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber], [State], [Phase],
                     [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc], [ResultCode], [OutcomeEvidenceKind], [PolarisHoldId])
                VALUES (@requestId, N'20000000002127', N'9027', 2, N'succeeded', N'result_recorded', 1,
                        DATEADD(minute, -2, SYSUTCDATETIME()), DATEADD(minute, -1, SYSUTCDATETIME()), N'success',
                        N'provider_final_success', N'8466');
                """;
            later.Parameters.AddWithValue("@requestId", superseded.RequestId);
            Assert.AreEqual(1, await later.ExecuteNonQueryAsync());
        }

        var service = factory!.Services.GetRequiredService<HoldPlacementService>();
        var patronResult = await service.RecordCompletedIdentityAsync(
            changedPatron.OperationId,
            changedPatron.RequestVersion,
            changedPatron.OperationVersion,
            new ProvenHoldIdentityEvidence("8467", "provider-operation-proof-9025"),
            CancellationToken.None);
        var bibResult = await service.RecordCompletedIdentityAsync(
            changedBib.OperationId,
            changedBib.RequestVersion,
            changedBib.OperationVersion,
            new ProvenHoldIdentityEvidence("8468", "provider-operation-proof-9026"),
            CancellationToken.None);
        var supersededResult = await service.RecordCompletedIdentityAsync(
            superseded.OperationId,
            superseded.RequestVersion,
            superseded.OperationVersion,
            new ProvenHoldIdentityEvidence("8469", "provider-operation-proof-9027"),
            CancellationToken.None);
        Assert.AreEqual("hold_identity_association_changed", patronResult.Code);
        Assert.AreEqual("hold_identity_association_changed", bibResult.Code);
        Assert.AreEqual("hold_identity_not_enrichable", supersededResult.Code);
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentRequiresActiveLibraryAndNoExecutorLease()
    {
        const int inactiveOrganizationId = 91220;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seedOrganization = connection.CreateCommand();
            seedOrganization.CommandText =
                """
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                VALUES (@id, N'Inactive identity library', N'IIL', 0);
                """;
            seedOrganization.Parameters.AddWithValue("@id", inactiveOrganizationId);
            Assert.AreEqual(1, await seedOrganization.ExecuteNonQueryAsync());
        }
        var inactive = await SeedCompletedHoldIdentityAsync(
            "inactive",
            "20000000002128",
            "9028",
            organizationId: inactiveOrganizationId);
        var leased = await SeedCompletedHoldIdentityAsync("leased", "20000000002129", "9029");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var lease = connection.CreateCommand();
            lease.CommandText =
                """
                UPDATE [asap].[HoldPlacementOperation]
                SET [OwnerToken] = NEWID(), [LeaseExpiresUtc] = DATEADD(minute, -1, SYSUTCDATETIME())
                WHERE [Id] = @id;
                SELECT [RowVersion] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;
                """;
            lease.Parameters.AddWithValue("@id", leased.OperationId);
            leased = leased with { OperationVersion = (byte[])(await lease.ExecuteScalarAsync())! };
        }

        var service = factory!.Services.GetRequiredService<HoldPlacementService>();
        var inactiveResult = await service.RecordCompletedIdentityAsync(
            inactive.OperationId,
            inactive.RequestVersion,
            inactive.OperationVersion,
            new ProvenHoldIdentityEvidence("8470", "provider-operation-proof-9028"),
            CancellationToken.None);
        var leasedResult = await service.RecordCompletedIdentityAsync(
            leased.OperationId,
            leased.RequestVersion,
            leased.OperationVersion,
            new ProvenHoldIdentityEvidence("8471", "provider-operation-proof-9029"),
            CancellationToken.None);
        Assert.AreEqual("organization_inactive", inactiveResult.Code);
        Assert.AreEqual("hold_identity_not_enrichable", leasedResult.Code);
    }

    [TestMethod]
    public async Task StaffHoldReplyExceptionPersistsMarkedAmbiguityAndNeverReplaysReply()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        holdProvider.ReplyException = new InvalidOperationException("synthetic reply disconnect");
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002114', N'Reply ambiguity title', 1, @formatId, N'pending_hold', N'9009',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        using var place = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, place.StatusCode, await place.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        using var blocked = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        var operation = blockedBody.RootElement.GetProperty("holdOperation");
        Assert.AreEqual("operator_required", operation.GetProperty("state").GetString());
        Assert.AreEqual("reply_started", operation.GetProperty("phase").GetString());
        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(1, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [State], [Phase], [ReplyStartedUtc], [ReplyResponseObservedUtc], [CompletedUtc], [OutcomeEvidenceKind]
            FROM [asap].[HoldPlacementOperation] WHERE [TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("operator_required", result.GetString(0));
        Assert.AreEqual("reply_started", result.GetString(1));
        Assert.IsFalse(result.IsDBNull(2));
        Assert.IsFalse(result.IsDBNull(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual("reply_provider_exception", result.GetString(5));
    }

    [TestMethod]
    public async Task StaffHoldReconcileCompletesDurableResultExactlyOnceWithoutAnotherMutation()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002115', N'Recorded result recovery title', 1, @formatId, N'pending_hold', N'9010',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch], [LeaseExpiresUtc],
                     [RequestStartedUtc], [ResultCode], [OutcomeEvidenceKind], [PolarisHoldId])
                VALUES (@requestId, N'20000000002115', N'9010', 101, 1, N'in_progress', N'result_recorded',
                        NEWID(), 1, DATEADD(minute, -1, SYSUTCDATETIME()), DATEADD(minute, -5, SYSUTCDATETIME()),
                        N'success', N'documented_reply_success', N'8455');
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var operation = getBody.RootElement.GetProperty("holdOperation");
        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT r.[Status], o.[State], o.[ExecutionEpoch], o.[RecoveryAttemptCount], o.[LastRecoveryUtc],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] = N'title-hold-placed:' + CONVERT(nvarchar(30), @id) + N':1')
            FROM [asap].[TitleRequest] r
            JOIN [asap].[HoldPlacementOperation] o ON o.[TitleRequestId] = r.[Id]
            WHERE r.[Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("hold_placed", result.GetString(0));
        Assert.AreEqual("succeeded", result.GetString(1));
        Assert.AreEqual(2L, result.GetInt64(2));
        Assert.AreEqual(1, result.GetInt32(3));
        Assert.IsFalse(result.IsDBNull(4));
        Assert.AreEqual(1, result.GetInt32(5));
        Assert.AreEqual(1, result.GetInt32(6));
    }

    [TestMethod]
    public async Task StaffHoldOperatorResolutionDoesNotTreatEvidenceReferenceAsHoldIdentity()
    {
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002116', N'Operator resolution title', 1, @formatId, N'pending_hold', N'9011',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [ResultCode], [LastErrorCode])
                VALUES (@requestId, N'20000000002116', N'9011', 101, 1, N'operator_required', N'create_started', 2,
                        DATEADD(minute, -5, SYSUTCDATETIME()), N'ambiguous', N'provider_timeout');
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var operation = getBody.RootElement.GetProperty("holdOperation");
        Assert.IsTrue(operation.GetProperty("canResolveSucceeded").GetBoolean());
        var requestVersion = getBody.RootElement.GetProperty("version").GetString();
        var operationId = operation.GetProperty("id").GetString();
        using var unsafeResolve = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/resolve",
            new
            {
                version = operation.GetProperty("version").GetString(),
                outcome = "succeeded",
                reason = "Matched the provider's completed response to this journal operation.",
                evidenceKind = "authoritative_correlated_hold",
                evidenceReference = "8456",
                originalExecutorExcluded = true
            });
        Assert.AreEqual(HttpStatusCode.Conflict, unsafeResolve.StatusCode, await unsafeResolve.Content.ReadAsStringAsync());
        using (var unsafeBody = JsonDocument.Parse(await unsafeResolve.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("invalid_resolution", unsafeBody.RootElement.GetProperty("code").GetString());
        }

        var resolution = new
        {
            version = operation.GetProperty("version").GetString(),
            requestVersion,
            outcome = "succeeded",
            reason = "Matched the provider's completed response to this exact journal operation and attempt.",
            evidenceKind = "provider_final_success",
            evidenceReference = "provider-response-8456",
            operationSpecificProofAttested = true,
            proofSource = "Polaris operations response retained in support case SUP-8456",
            causalConnection = "The response names this operation's attempt, frozen patron, BIB, and a definitive success outcome.",
            originalExecutorExcluded = true,
            executorExclusionAttested = true,
            executorExclusionReference = "incident-runbook-8456",
            executorExclusionExplanation = "The interactive host process was terminated at 2026-09-13T13:00:00Z and the provider response accounts for its in-flight request."
        };
        using var resolve = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/resolve",
            resolution);
        Assert.AreEqual(HttpStatusCode.OK, resolve.StatusCode, await resolve.Content.ReadAsStringAsync());
        using var duplicate = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/resolve",
            resolution);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode, await duplicate.Content.ReadAsStringAsync());

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT r.[Status], o.[State], o.[Phase], o.[PolarisHoldId], o.[OutcomeEvidenceKind],
                   o.[ExecutionEpoch], o.[DetailJson],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit] WHERE [TargetType] = N'hold_placement_operation' AND [TargetId] = CONVERT(nvarchar(30), o.[Id]) AND [Action] = N'hold_operation_resolved_succeeded'),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox] WHERE [BusinessKey] = N'title-hold-placed:' + CONVERT(nvarchar(30), @id) + N':1')
            FROM [asap].[TitleRequest] r
            JOIN [asap].[HoldPlacementOperation] o ON o.[TitleRequestId] = r.[Id]
            WHERE r.[Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("hold_placed", result.GetString(0));
        Assert.AreEqual("succeeded", result.GetString(1));
        Assert.AreEqual("result_recorded", result.GetString(2));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.AreEqual("operator_verified:provider_final_success", result.GetString(4));
        Assert.AreEqual(3L, result.GetInt64(5));
        using (var detail = JsonDocument.Parse(result.GetString(6)))
        {
            Assert.IsTrue(detail.RootElement.TryGetProperty("operatorResolution", out var recorded));
            Assert.AreEqual("provider-response-8456", recorded.GetProperty("evidenceReference").GetString());
            Assert.AreEqual(1, recorded.GetProperty("attemptNumber").GetInt32());
            Assert.AreEqual(2L, recorded.GetProperty("executionEpochBefore").GetInt64());
            Assert.AreEqual(3L, recorded.GetProperty("executionEpochAfter").GetInt64());
            Assert.IsTrue(recorded.GetProperty("operationSpecificProofAttested").GetBoolean());
            Assert.IsTrue(recorded.GetProperty("executorExclusionAttested").GetBoolean());
        }
        Assert.AreEqual(1, result.GetInt32(7));
        Assert.AreEqual(1, result.GetInt32(8));
        Assert.AreEqual(1, result.GetInt32(9));
    }

    [TestMethod]
    public async Task StaffAmbiguousMarkedCreateRequiresOperatorAndReconcileNeverReplaysMutation()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        await using var holdFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
            }));
        using var client = holdFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using (var start = await client.GetAsync("/api/asap/staff/session"))
        {
            Assert.AreEqual(HttpStatusCode.OK, start.StatusCode);
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        long actorId;
        long requestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                DECLARE @actorId bigint = (
                    SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
                DECLARE @formatId bigint = (
                    SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002106', N'Ambiguous hold title', 1, @formatId, N'pending_hold', N'9001',
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            await using var reader = await seed.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var getBody = JsonDocument.Parse(await get.Content.ReadAsStringAsync());

        using var placement = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, placement.StatusCode, await placement.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        using var blocked = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.AreEqual("pending_hold", blockedBody.RootElement.GetProperty("status").GetString());
        var operation = blockedBody.RootElement.GetProperty("holdOperation");
        Assert.AreEqual("operator_required", operation.GetProperty("state").GetString());
        Assert.AreEqual("create_started", operation.GetProperty("phase").GetString());

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount, "Reconcile must never replay a marked create.");

        using var afterReconcile = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var afterReconcileBody = JsonDocument.Parse(await afterReconcile.Content.ReadAsStringAsync());
        var reconciledOperation = afterReconcileBody.RootElement.GetProperty("holdOperation");
        var resolution = new
        {
            version = reconciledOperation.GetProperty("version").GetString(),
            requestVersion = afterReconcileBody.RootElement.GetProperty("version").GetString(),
            outcome = "not_performed",
            reason = "Provider operations report proves the request was not accepted.",
            evidenceKind = "provider_final_no_effect",
            evidenceReference = "operations-report-106",
            operationSpecificProofAttested = true,
            proofSource = "Polaris operations report retained with support case SUP-106",
            causalConnection = "The report identifies this exact attempt, frozen patron, BIB, and definitive no-effect result."
        };
        using var unsafeResolution = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{reconciledOperation.GetProperty("id").GetString()}/resolve",
            new
            {
                resolution.version,
                resolution.requestVersion,
                resolution.outcome,
                resolution.reason,
                resolution.evidenceKind,
                resolution.evidenceReference,
                resolution.operationSpecificProofAttested,
                resolution.proofSource,
                resolution.causalConnection,
                originalExecutorExcluded = true,
                executorExclusionAttested = false,
                executorExclusionReference = "incident-runbook-106",
                executorExclusionExplanation = "The prior host termination and in-flight request were accounted for."
            });
        Assert.AreEqual(HttpStatusCode.Conflict, unsafeResolution.StatusCode);

        using var resolved = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{reconciledOperation.GetProperty("id").GetString()}/resolve",
            new
            {
                resolution.version,
                resolution.requestVersion,
                resolution.outcome,
                resolution.reason,
                resolution.evidenceKind,
                resolution.evidenceReference,
                resolution.operationSpecificProofAttested,
                resolution.proofSource,
                resolution.causalConnection,
                originalExecutorExcluded = true,
                executorExclusionAttested = true,
                executorExclusionReference = "incident-runbook-106",
                executorExclusionExplanation = "The interactive host terminated at 2026-09-13T13:05:00Z and the report accounts for its in-flight provider request."
            });
        Assert.AreEqual(HttpStatusCode.OK, resolved.StatusCode, await resolved.Content.ReadAsStringAsync());
        Assert.AreEqual(1, holdProvider.CreateCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT o.[State], o.[RecoveryAttemptCount], o.[OwnerToken], o.[LeaseExpiresUtc], o.[CompletedUtc], r.[Status],
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit] WHERE [TargetType] = N'hold_placement_operation' AND [TargetId] = CONVERT(nvarchar(100), o.[Id])),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = r.[Id] AND [EventType] = N'hold_operation_resolved_not_performed')
            FROM [asap].[HoldPlacementOperation] o
            JOIN [asap].[TitleRequest] r ON r.[Id] = o.[TitleRequestId]
            WHERE o.[TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("no_hold", result.GetString(0));
        Assert.IsGreaterThanOrEqualTo(2, result.GetInt32(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.IsFalse(result.IsDBNull(4));
        Assert.AreEqual("pending_hold", result.GetString(5));
        Assert.AreEqual(1, result.GetInt32(6));
        Assert.AreEqual(1, result.GetInt32(7));
    }

    [TestMethod]
    public async Task StaffHoldOperatorResolutionRequiresDistinctProvenIdentityAndServerFencesNeverDispatched()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var correlated = await SeedOperatorResolutionAsync("correlated", "create_started");
        using var correlatedResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{correlated.OperationId}/resolve",
            OperatorResolutionPayload(
                correlated,
                "succeeded",
                "authoritative_correlated_hold",
                provenFinalHoldId: "8456",
                markedExecutionExcluded: true));
        Assert.AreEqual(HttpStatusCode.OK, correlatedResponse.StatusCode, await correlatedResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("8456", await ReadHoldIdentityAsync(correlated.OperationId));

        var missingIdentity = await SeedOperatorResolutionAsync("missing-correlated-id", "create_started");
        using var missingIdentityResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{missingIdentity.OperationId}/resolve",
            OperatorResolutionPayload(
                missingIdentity,
                "succeeded",
                "authoritative_correlated_hold",
                markedExecutionExcluded: true));
        Assert.AreEqual(HttpStatusCode.Conflict, missingIdentityResponse.StatusCode);

        var neverDispatched = await SeedOperatorResolutionAsync("never-dispatched", "acquired");
        using var neverDispatchedResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{neverDispatched.OperationId}/resolve",
            new
            {
                version = neverDispatched.OperationVersion,
                requestVersion = neverDispatched.RequestVersion,
                outcome = "not_performed",
                reason = "The server journal proves no create or reply marker was committed.",
                evidenceKind = "fenced_never_dispatched"
            });
        Assert.AreEqual(HttpStatusCode.OK, neverDispatchedResponse.StatusCode, await neverDispatchedResponse.Content.ReadAsStringAsync());

        var marked = await SeedOperatorResolutionAsync("marked-not-never", "create_started");
        using var markedResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{marked.OperationId}/resolve",
            new
            {
                version = marked.OperationVersion,
                requestVersion = marked.RequestVersion,
                outcome = "not_performed",
                reason = "A committed dispatch marker cannot be called never dispatched.",
                evidenceKind = "fenced_never_dispatched"
            });
        Assert.AreEqual(HttpStatusCode.Conflict, markedResponse.StatusCode);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [State], [ResultCode], [OutcomeEvidenceKind], [ExecutionEpoch], [OwnerToken], [LeaseExpiresUtc], [CompletedUtc]
            FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", neverDispatched.OperationId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("no_hold", result.GetString(0));
        Assert.AreEqual("definitive_no_effect", result.GetString(1));
        Assert.AreEqual("server_verified:fenced_never_dispatched", result.GetString(2));
        Assert.AreEqual(3L, result.GetInt64(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
        Assert.IsFalse(result.IsDBNull(6));
    }

    [TestMethod]
    public async Task StaffHoldOperatorResolutionRevalidatesRequestAssociationOwnershipAndActor()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var staleRequest = await SeedOperatorResolutionAsync("stale-request", "create_started");
        await ExecuteNonQueryAsync(
            "UPDATE [asap].[TitleRequest] SET [Title] = N'Concurrent title edit' WHERE [Id] = @id;",
            ("@id", staleRequest.RequestId));
        using var staleResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{staleRequest.OperationId}/resolve",
            OperatorResolutionPayload(staleRequest, "not_performed", "provider_final_no_effect", markedExecutionExcluded: true));
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode);
        await AssertResolutionRejectedWithoutHistoryAsync(staleRequest);

        var changedAssociation = await SeedOperatorResolutionAsync("changed-association", "create_started");
        await ExecuteNonQueryAsync(
            "UPDATE [asap].[TitleRequest] SET [Barcode] = N'20000000009999' WHERE [Id] = @id;",
            ("@id", changedAssociation.RequestId));
        using var currentRequest = await client.GetAsync($"/api/asap/staff/title-requests/{changedAssociation.RequestId}");
        using var currentRequestBody = JsonDocument.Parse(await currentRequest.Content.ReadAsStringAsync());
        var currentAssociation = changedAssociation with
        {
            RequestVersion = currentRequestBody.RootElement.GetProperty("version").GetString()!
        };
        using var associationResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{changedAssociation.OperationId}/resolve",
            OperatorResolutionPayload(currentAssociation, "not_performed", "provider_final_no_effect", markedExecutionExcluded: true));
        Assert.AreEqual(HttpStatusCode.Conflict, associationResponse.StatusCode);
        await AssertResolutionRejectedWithoutHistoryAsync(changedAssociation);

        var liveOwner = await SeedOperatorResolutionAsync("live-owner", "create_started", liveOwner: true);
        using var liveOwnerResponse = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{liveOwner.OperationId}/resolve",
            OperatorResolutionPayload(liveOwner, "not_performed", "provider_final_no_effect", markedExecutionExcluded: true));
        Assert.AreEqual(HttpStatusCode.Conflict, liveOwnerResponse.StatusCode);
        await AssertResolutionRejectedWithoutHistoryAsync(liveOwner);

        var revoked = await SeedOperatorResolutionAsync("revoked-actor", "create_started");
        await ExecuteNonQueryAsync(
            "UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @id;",
            ("@id", actor.Id));
        try
        {
            using var revokedResponse = await client.PostAsJsonAsync(
                $"/api/asap/staff/hold-operations/{revoked.OperationId}/resolve",
                OperatorResolutionPayload(revoked, "not_performed", "provider_final_no_effect", markedExecutionExcluded: true));
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
            await AssertResolutionRejectedWithoutHistoryAsync(revoked);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[StaffUser] SET [IsActive] = 1 WHERE [Id] = @id;",
                ("@id", actor.Id));
        }
    }

    [TestMethod]
    public async Task StaffHoldReconcileAndResolutionShareWorkflowProcessingGuard()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        var reconcileSeed = await SeedOperatorResolutionAsync("guard-reconcile", "create_started");
        var resolveSeed = await SeedOperatorResolutionAsync("guard-resolve", "create_started");

        var storage = factory.Services.GetRequiredService<JobStorage>();
        using var connection = storage.GetConnection();
        using var held = connection.AcquireDistributedLock("ASAP:WorkflowProcessing", TimeSpan.FromSeconds(5));
        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{reconcileSeed.OperationId}/reconcile",
            new { version = reconcileSeed.OperationVersion });
        using var reconcileBody = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode);
        Assert.AreEqual("workflow_processing_busy", reconcileBody.RootElement.GetProperty("code").GetString());

        using var resolve = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{resolveSeed.OperationId}/resolve",
            OperatorResolutionPayload(resolveSeed, "not_performed", "provider_final_no_effect", markedExecutionExcluded: true));
        using var resolveBody = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Conflict, resolve.StatusCode);
        Assert.AreEqual("workflow_processing_busy", resolveBody.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task StaffHoldDispatchRechecksOwnershipImmediatelyAfterCreateAndReplyMarkers()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        var createProvider = ScriptedHoldProvider.AmbiguousCreate();
        await using (var createFactory = factory.WithWebHostBuilder(builder =>
                     builder.ConfigureServices(services =>
                     {
                         services.RemoveAll<IPatronProvider>();
                         services.RemoveAll<IStaffPolarisProvider>();
                         services.AddSingleton<IPatronProvider>(createProvider);
                         services.AddSingleton<IStaffPolarisProvider>(createProvider);
                     })))
        using (var createClient = createFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }))
        {
            AddTestingStaffHeaders(createClient, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            createClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(createClient));
            var requestId = await SeedPendingHoldRequestAsync("Create marker fence", "20000000003201", "93201");
            await InstallMarkerFenceTriggerAsync("create_started");
            try
            {
                using var get = await createClient.GetAsync($"/api/asap/staff/title-requests/{requestId}");
                using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
                using var response = await createClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestId}/place-hold",
                    new { version = body.RootElement.GetProperty("version").GetString() });
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("operation_ownership_lost", responseBody.RootElement.GetProperty("code").GetString());
                Assert.AreEqual(0, createProvider.CreateCount, "An owner fenced after the marker must not invoke create.");
            }
            finally
            {
                await DropMarkerFenceTriggerAsync();
            }
        }

        var replyProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        await using (var replyFactory = factory.WithWebHostBuilder(builder =>
                     builder.ConfigureServices(services =>
                     {
                         services.RemoveAll<IPatronProvider>();
                         services.RemoveAll<IStaffPolarisProvider>();
                         services.AddSingleton<IPatronProvider>(replyProvider);
                         services.AddSingleton<IStaffPolarisProvider>(replyProvider);
                     })))
        using (var replyClient = replyFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }))
        {
            AddTestingStaffHeaders(replyClient, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            replyClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(replyClient));
            var requestId = await SeedPendingHoldRequestAsync("Reply marker fence", "20000000003202", "93202");
            await InstallMarkerFenceTriggerAsync("reply_started");
            try
            {
                using var get = await replyClient.GetAsync($"/api/asap/staff/title-requests/{requestId}");
                using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
                using var response = await replyClient.PostAsJsonAsync(
                    $"/api/asap/staff/title-requests/{requestId}/place-hold",
                    new { version = body.RootElement.GetProperty("version").GetString() });
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
                using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual("operation_ownership_lost", responseBody.RootElement.GetProperty("code").GetString());
                Assert.AreEqual(1, replyProvider.CreateCount);
                Assert.AreEqual(0, replyProvider.ReplyCount, "An owner fenced after the marker must not invoke reply.");
            }
            finally
            {
                await DropMarkerFenceTriggerAsync();
            }
        }
    }

    [TestMethod]
    [DataRow(503, IdentifierLookupOutcome.TransientFailure)]
    [DataRow(429, IdentifierLookupOutcome.TransientFailure)]
    [DataRow(401, IdentifierLookupOutcome.OperationalFailure)]
    public async Task PolarisIdentifierLookupClassifiesActualPackageHttpResponses(
        int statusCode,
        IdentifierLookupOutcome expected)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, "{}");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000099", CancellationToken.None);

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    public async Task PolarisIdentifierLookupRejectsSuccessfulMalformedProtocol(string content)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000098", CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.OperationalFailure, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public async Task PolarisIdentifierLookupAcceptsApiDefinedDefinitiveEmptyResponses(int papiErrorCode)
    {
        var content = $$"""
            {"PAPIErrorCode":{{papiErrorCode}},"TotalRecordsFound":0,"BibSearchRows":[]}
            """;
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000097", CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.NotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(429, "{}")]
    [DataRow(200, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{\"PAPIErrorCode\":0}")]
    public async Task PolarisAuthenticationTreatsTransportAndProtocolFailuresAsOperational(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.AuthenticateAsync("20000000000030", "1234", CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(401, "{}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"Invalid credentials\"}")]
    public async Task PolarisAuthenticationPreservesDefinitiveCredentialDenials(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PatronAuthenticationException>(async () =>
            await provider.AuthenticateAsync("20000000000031", "wrong", CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(429, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{}")]
    public async Task PolarisAuthenticationPreservesBasicDataOperationalFailures(
        int statusCode,
        string basicDataContent)
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             "{\"PAPIErrorCode\":0,\"AccessToken\":\"patron-token\",\"AccessSecret\":\"patron-secret\",\"PatronID\":123}"),
            ((HttpStatusCode)statusCode, basicDataContent));
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.AuthenticateAsync("20000000000033", "1234", CancellationToken.None));
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisPickupUpdateRequiresExplicitSuccessfulProtocolResponse()
    {
        var missingCode = new ProtectedUpdateResponseHandler("{}");
        var missingCodeProvider = await CreatePolarisProviderAsync(missingCode);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await missingCodeProvider.UpdatePreferredPickupBranchAsync(
                "20000000000032",
                101,
                CancellationToken.None));

        var explicitSuccess = new ProtectedUpdateResponseHandler("{\"PAPIErrorCode\":0}");
        var successProvider = await CreatePolarisProviderAsync(explicitSuccess);
        await successProvider.UpdatePreferredPickupBranchAsync(
            "20000000000032",
            101,
            CancellationToken.None);

        Assert.AreEqual(1, missingCode.UpdateRequestCount);
        Assert.AreEqual(1, explicitSuccess.UpdateRequestCount);
        StringAssert.Contains(
            explicitSuccess.UpdateRequestPath,
            "/public/v1/1033/100/7/patron/20000000000032");
        using var body = JsonDocument.Parse(explicitSuccess.UpdateRequestBody);
        Assert.AreEqual(7, body.RootElement.GetProperty("LogonBranchId").GetInt32());
        Assert.AreEqual(42, body.RootElement.GetProperty("LogonUserId").GetInt32());
        Assert.AreEqual(99, body.RootElement.GetProperty("LogonWorkstationId").GetInt32());
        Assert.AreEqual(101, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
    }

    [TestMethod]
    public async Task PolarisPickupBranchesNormalizeRawNestedAliasesAndSortLabels()
    {
        var handler = new StaticResponseHandler(
            HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"PickupBranchesRows":{"PickupBranchRow":[
              {"OrganizationID":3,"DisplayName":"Zulu Branch"},
              {"OrgID":4,"OrganizationName":"Alpha Branch"},
              {"PickupBranchID":3,"BranchName":"Duplicate Branch"}
            ]}}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var branches = await provider.GetPickupBranchesAsync(
            new PatronSnapshot(
                30,
                "20000000000033",
                "patron@example.org",
                "Test",
                "Patron",
                "1",
                null,
                77,
                88,
                "Home Library",
                4),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "4:Alpha Branch", "3:Zulu Branch" },
            branches.Select(item => $"{item.Id}:{item.Label}").ToArray());
        Assert.AreEqual(1, handler.RequestCount);
        StringAssert.Contains(handler.RequestPaths[0], "/public/v1/1033/100/77/pickupbranches");
    }

    [TestMethod]
    public async Task PolarisPreferredPickupUsesRequestFieldOrRegisteredBranchWithoutRawAliasFallback()
    {
        var cases = new[]
        {
            (RequestField: "\"RequestPickupBranchID\":200,", Expected: (int?)200),
            (RequestField: string.Empty, Expected: (int?)300),
            (RequestField: "\"RequestPickupBranchID\":0,", Expected: (int?)0)
        };

        foreach (var testCase in cases)
        {
            var handler = new SequenceResponseHandler(
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":0,\"AccessToken\":\"patron-token\",\"AccessSecret\":\"patron-secret\",\"PatronID\":123}"),
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":0,\"PatronBasicData\":{\"PatronID\":123,\"Barcode\":\"20000000000034\",\"PatronOrgID\":300," +
                 testCase.RequestField +
                 "\"CurrentPreferredPickupBranchID\":100,\"PreferredPickupBranchID\":400}}"),
                (HttpStatusCode.OK,
                 "{\"PAPIErrorCode\":1,\"OrganizationsGetRows\":[{\"OrganizationID\":300,\"OrganizationCodeID\":2,\"DisplayName\":\"Registered Branch\"}]}"));
            var provider = await CreatePolarisProviderAsync(handler);

            var patron = await provider.AuthenticateAsync(
                "20000000000034",
                "1234",
                CancellationToken.None);

            Assert.AreEqual(testCase.Expected, patron.PreferredPickupBranchId, testCase.RequestField);
            Assert.AreEqual(3, handler.RequestCount);
        }
    }

    [TestMethod]
    public async Task PatronLoginSelectsPreferredPickupOnlyWhenItIsAllowed()
    {
        await using var selectionFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPatronProvider>();
                services.AddSingleton<IPatronProvider, DisallowedPreferredPickupPatronProvider>();
            }));
        using var client = selectionFactory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000035", pin = "1234", libraryOrgId = 2 });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("selectedPickupBranchId").ValueKind);
        Assert.AreEqual(
            "Choose a preferred pickup location before submitting.",
            document.RootElement.GetProperty("pickupBranchWarning").GetString());
    }

    [TestMethod]
    public async Task SensitiveOutboxSuppressesWhenAuthorizationOrganizationIsInactive()
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var objectId = Guid.NewGuid();
        long outboxId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;
                DECLARE @staff TABLE ([Id] bigint);
                INSERT INTO [asap].[StaffUser]
                    ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                     [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
                OUTPUT inserted.[Id] INTO @staff
                VALUES
                    (@tenantId, @objectId, N'outbox-inactive@example.org', N'OUTBOX-INACTIVE@EXAMPLE.ORG',
                     N'Outbox Staff', N'outbox-inactive@example.org', N'staff', 2, 1);

                INSERT INTO [asap].[EmailOutbox]
                    ([OrganizationId], [BusinessKey], [DeliveryClass], [RecipientStaffUserId],
                     [RecipientAuthenticationEmail], [AuthorizationOrganizationId],
                     [RecipientAddressKind], [ToAddress], [FromAddress], [Subject], [BodyText],
                     [Status], [NextAttemptUtc], [CreatedUtc])
                OUTPUT inserted.[Id]
                SELECT 2, @businessKey, N'staff_authorization_sensitive', [Id],
                       N'OUTBOX-INACTIVE@EXAMPLE.ORG', 2, N'notification_email',
                       N'outbox-inactive@example.org', N'asap@example.org', N'Sensitive update', N'Body',
                       N'pending', SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM @staff;
                """;
            command.Parameters.AddWithValue("@tenantId", tenantId);
            command.Parameters.AddWithValue("@objectId", objectId);
            command.Parameters.AddWithValue("@businessKey", $"sensitive-inactive:{Guid.NewGuid():N}");
            outboxId = Convert.ToInt64(await command.ExecuteScalarAsync());
            await using var deactivate = connection.CreateCommand();
            deactivate.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = 2;";
            await deactivate.ExecuteNonQueryAsync();
        }

        try
        {
            var sender = (RecordingEmailSender)factory!.Services.GetRequiredService<IEmailSender>();
            var jobs = factory.Services.GetRequiredService<EmailOutboxJobs>();
            await jobs.DeliverAsync(outboxId, CancellationToken.None);

            Assert.AreEqual(0, sender.Envelopes.Count);
            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText =
                "SELECT [Status], [SuppressionReason] FROM [asap].[EmailOutbox] WHERE [Id] = @id;";
            command.Parameters.AddWithValue("@id", outboxId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("suppressed", reader.GetString(0));
            Assert.AreEqual("recipient_authorization_changed", reader.GetString(1));
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;";
            await command.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task SensitiveOutboxSuppressesAfterStaffDeactivationMoveDemotionOrEmailChange()
    {
        const int otherOrganizationId = 91230;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var organization = connection.CreateCommand();
            organization.CommandText =
                """
                IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = @id)
                    INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                    VALUES (@id, N'Other sensitive library', N'OSL', 1);
                """;
            organization.Parameters.AddWithValue("@id", otherOrganizationId);
            await organization.ExecuteNonQueryAsync();
        }

        var deactivated = await SeedSensitiveOutboxAsync("deactivated");
        var moved = await SeedSensitiveOutboxAsync("moved");
        var demoted = await SeedSensitiveOutboxAsync(
            "demoted",
            role: "super_admin",
            staffOrganizationId: 1);
        var emailChanged = await SeedSensitiveOutboxAsync("email-change");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var mutate = connection.CreateCommand();
            mutate.CommandText =
                """
                UPDATE [asap].[StaffUser] SET [IsActive] = 0 WHERE [Id] = @deactivatedId;
                UPDATE [asap].[StaffUser] SET [OrganizationId] = @otherOrganizationId WHERE [Id] = @movedId;
                UPDATE [asap].[StaffUser]
                SET [Role] = N'admin', [OrganizationId] = @otherOrganizationId
                WHERE [Id] = @demotedId;
                UPDATE [asap].[StaffUser]
                SET [UserPrincipalName] = N'changed-authentication@example.org',
                    [NormalizedUserPrincipalName] = N'CHANGED-AUTHENTICATION@EXAMPLE.ORG'
                WHERE [Id] = @emailChangedId;
                """;
            mutate.Parameters.AddWithValue("@deactivatedId", deactivated.StaffUserId);
            mutate.Parameters.AddWithValue("@movedId", moved.StaffUserId);
            mutate.Parameters.AddWithValue("@demotedId", demoted.StaffUserId);
            mutate.Parameters.AddWithValue("@emailChangedId", emailChanged.StaffUserId);
            mutate.Parameters.AddWithValue("@otherOrganizationId", otherOrganizationId);
            Assert.AreEqual(4, await mutate.ExecuteNonQueryAsync());
        }

        var sender = new RecordingEmailSender();
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        foreach (var outbox in new[] { deactivated, moved, demoted, emailChanged })
        {
            await jobs.DeliverAsync(outbox.OutboxId, CancellationToken.None);
            var state = await ReadOutboxStateAsync(outbox.OutboxId);
            Assert.AreEqual("suppressed", state.Status);
            Assert.AreEqual("recipient_authorization_changed", state.SuppressionReason);
        }
        Assert.AreEqual(0, sender.Envelopes.Count);
    }

    [TestMethod]
    public async Task SensitiveOutboxDeliveryDoesNotDependOnStoredOid()
    {
        var queued = await SeedSensitiveOutboxAsync("oid-metadata-change");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var mutate = new SqlCommand(
                "UPDATE [asap].[StaffUser] SET [EntraObjectId] = NEWID() WHERE [Id] = @id;",
                connection);
            mutate.Parameters.AddWithValue("@id", queued.StaffUserId);
            Assert.AreEqual(1, await mutate.ExecuteNonQueryAsync());
        }

        var sender = new RecordingEmailSender();
        await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
            .DeliverAsync(queued.OutboxId, CancellationToken.None);

        Assert.HasCount(1, sender.Envelopes);
        Assert.AreEqual("sent", (await ReadOutboxStateAsync(queued.OutboxId)).Status);
    }

    [TestMethod]
    public async Task SensitiveOutboxRevalidatesTheDeclaredRecipientAddressKind()
    {
        var weeklyUnaffectedByPrimary = await SeedSensitiveOutboxAsync(
            "weekly-primary-change",
            addressKind: "weekly_summary",
            weeklyEnabled: true,
            notificationEmail: "weekly-primary-old@example.org",
            weeklyEmail: "weekly-override@example.org",
            toAddress: "weekly-override@example.org");
        var weeklyChanged = await SeedSensitiveOutboxAsync(
            "weekly-change",
            addressKind: "weekly_summary",
            weeklyEnabled: true,
            notificationEmail: "weekly-change-primary@example.org",
            weeklyEmail: "weekly-change-old@example.org",
            toAddress: "weekly-change-old@example.org");
        var weeklyClearedToSameFallback = await SeedSensitiveOutboxAsync(
            "weekly-clear",
            addressKind: "weekly_summary",
            weeklyEnabled: true,
            notificationEmail: "weekly-same@example.org",
            weeklyEmail: "weekly-same@example.org",
            toAddress: "weekly-same@example.org");
        var ordinaryPrimaryChanged = await SeedSensitiveOutboxAsync(
            "ordinary-change",
            addressKind: "notification_email",
            weeklyEnabled: true,
            notificationEmail: "ordinary-old@example.org",
            weeklyEmail: "ordinary-old@example.org",
            toAddress: "ordinary-old@example.org");

        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var mutate = connection.CreateCommand();
            mutate.CommandText =
                """
                UPDATE [asap].[StaffUser]
                SET [NotificationEmail] = N'weekly-primary-new@example.org'
                WHERE [Id] = @weeklyPrimaryId;
                UPDATE [asap].[StaffUser]
                SET [WeeklyActionSummaryEmail] = N'weekly-change-new@example.org'
                WHERE [Id] = @weeklyChangedId;
                UPDATE [asap].[StaffUser]
                SET [WeeklyActionSummaryEmail] = NULL
                WHERE [Id] = @weeklyClearedId;
                UPDATE [asap].[StaffUser]
                SET [NotificationEmail] = N'ordinary-new@example.org'
                WHERE [Id] = @ordinaryId;
                """;
            mutate.Parameters.AddWithValue("@weeklyPrimaryId", weeklyUnaffectedByPrimary.StaffUserId);
            mutate.Parameters.AddWithValue("@weeklyChangedId", weeklyChanged.StaffUserId);
            mutate.Parameters.AddWithValue("@weeklyClearedId", weeklyClearedToSameFallback.StaffUserId);
            mutate.Parameters.AddWithValue("@ordinaryId", ordinaryPrimaryChanged.StaffUserId);
            Assert.AreEqual(4, await mutate.ExecuteNonQueryAsync());
        }

        var sender = new RecordingEmailSender();
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        await jobs.DeliverAsync(weeklyUnaffectedByPrimary.OutboxId, CancellationToken.None);
        await jobs.DeliverAsync(weeklyChanged.OutboxId, CancellationToken.None);
        await jobs.DeliverAsync(weeklyClearedToSameFallback.OutboxId, CancellationToken.None);
        await jobs.DeliverAsync(ordinaryPrimaryChanged.OutboxId, CancellationToken.None);

        Assert.AreEqual("sent", (await ReadOutboxStateAsync(weeklyUnaffectedByPrimary.OutboxId)).Status);
        Assert.AreEqual("suppressed", (await ReadOutboxStateAsync(weeklyChanged.OutboxId)).Status);
        Assert.AreEqual("sent", (await ReadOutboxStateAsync(weeklyClearedToSameFallback.OutboxId)).Status);
        Assert.AreEqual("suppressed", (await ReadOutboxStateAsync(ordinaryPrimaryChanged.OutboxId)).Status);
        Assert.AreEqual(2, sender.Envelopes.Count);
        CollectionAssert.AreEquivalent(
            new[] { "weekly-override@example.org", "weekly-same@example.org" },
            sender.Envelopes.Select(item => item.ToAddress).ToArray());
    }

    [TestMethod]
    public async Task OutboxRechecksProviderStartDeadlineAfterDelayedRecipientValidation()
    {
        var seeded = await SeedSensitiveOutboxAsync("deadline");
        var sender = new RecordingEmailSender();
        var jobs = CreateEmailOutboxJobs(
            sender,
            new EmailOutboxRuntimeOptions(
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMinutes(2),
                5));

        await using var blocker = new SqlConnection(databaseConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var lockStaff = blocker.CreateCommand())
        {
            lockStaff.Transaction = transaction;
            lockStaff.CommandText =
                "UPDATE [asap].[StaffUser] SET [DisplayName] = [DisplayName] WHERE [Id] = @id;";
            lockStaff.Parameters.AddWithValue("@id", seeded.StaffUserId);
            await lockStaff.ExecuteNonQueryAsync();
        }

        var delivery = jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        await WaitForOutboxStatusAsync(seeded.OutboxId, "sending", TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        await transaction.CommitAsync();
        await delivery;

        Assert.AreEqual(0, sender.Envelopes.Count);
        var state = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sending", state.Status);
        Assert.AreEqual("provider_start_deadline_exceeded", state.LastErrorCode);
    }

    [TestMethod]
    public async Task TimedOutOutboxDoesNotRetryBeforeLeaseBoundary()
    {
        var seeded = await SeedSensitiveOutboxAsync("timeout-boundary");
        var sender = new CancellationAwareTimeoutEmailSender();
        var jobs = CreateEmailOutboxJobs(
            sender,
            new EmailOutboxRuntimeOptions(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMilliseconds(75),
                TimeSpan.FromMinutes(2),
                5));

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        Assert.AreEqual(1, sender.CallCount);
        var timedOut = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sending", timedOut.Status);
        Assert.AreEqual("provider_timeout", timedOut.LastErrorCode);

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        Assert.AreEqual(1, sender.CallCount, "A sending lease must not be claimed for an early retry.");

        await SetLeaseExpiryAsync(seeded.OutboxId, expired: false);
        await jobs.SweepAsync(CancellationToken.None);
        CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);
        Assert.AreEqual("sending", (await ReadOutboxStateAsync(seeded.OutboxId)).Status);

        await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
        await jobs.SweepAsync(CancellationToken.None);
        CollectionAssert.Contains(dispatcher.EnqueuedIds, seeded.OutboxId);
        Assert.AreEqual("pending", (await ReadOutboxStateAsync(seeded.OutboxId)).Status);

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        Assert.AreEqual(2, sender.CallCount, "A new claim is allowed only after the ambiguity lease expires.");
    }

    [TestMethod]
    public async Task StaleProviderCompletionCannotOverwriteNewLeaseCompletion()
    {
        var seeded = await SeedSensitiveOutboxAsync("stale-completion");
        var sender = new FencedCompletionEmailSender();
        var jobs = CreateEmailOutboxJobs(
            sender,
            new EmailOutboxRuntimeOptions(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2),
                5));

        var firstDelivery = jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        await sender.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
        await jobs.SweepAsync(CancellationToken.None);
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);

        var completed = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sent", completed.Status);
        Assert.AreEqual("new-owner-message", completed.ProviderMessageId);

        sender.CompleteFirstCall();
        await firstDelivery;
        var afterStaleCompletion = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sent", afterStaleCompletion.Status);
        Assert.AreEqual("new-owner-message", afterStaleCompletion.ProviderMessageId);
    }

    [TestMethod]
    public async Task StaleNotConfiguredResultCannotOverwriteNewLeaseCompletion()
    {
        var seeded = await SeedSensitiveOutboxAsync("stale-not-configured");
        var sender = new FencedCompletionEmailSender();
        var jobs = CreateEmailOutboxJobs(
            sender,
            new EmailOutboxRuntimeOptions(
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2),
                5));

        var firstDelivery = jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        await sender.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
        await jobs.SweepAsync(CancellationToken.None);
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        sender.CompleteFirstCall(EmailSendResult.NotConfigured);
        await firstDelivery;

        var completed = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sent", completed.Status);
        Assert.AreEqual("new-owner-message", completed.ProviderMessageId);
        Assert.IsNull(completed.LastErrorCode);
    }

    [TestMethod]
    public async Task RecipientDomainPolicyIsAppliedAtIntentAndImmediatelyBeforeSend()
    {
        var blockedDispatcher = new RecordingOutboxDispatcher();
        var blockedSuggestionService = CreatePatronSuggestionService(
            allowedDomains: ["allowed.invalid"],
            blockedDispatcher);
        var blockedResult = await blockedSuggestionService.CreateAsync(
            new PatronSessionContext(
                9001,
                "20000000000040",
                2,
                2,
                2,
                DateTime.UtcNow.AddHours(1)),
            Suggestion("Initially Blocked Domain"),
            CancellationToken.None);
        var initiallyBlockedOutboxId = await FindSubmissionOutboxIdAsync(blockedResult.Id);
        var initiallyBlocked = await ReadOutboxStateAsync(initiallyBlockedOutboxId);
        Assert.AreEqual("suppressed", initiallyBlocked.Status);
        Assert.AreEqual("recipient_domain_not_allowed", initiallyBlocked.SuppressionReason);
        Assert.AreEqual(0, blockedDispatcher.EnqueuedIds.Count);

        var senderAfterRelaxation = new RecordingEmailSender();
        var allowedJobs = CreateEmailOutboxJobs(
            senderAfterRelaxation,
            EmailOutboxRuntimeOptions.Default,
            ["example.org"]);
        await allowedJobs.DeliverAsync(initiallyBlockedOutboxId, CancellationToken.None);
        Assert.AreEqual(0, senderAfterRelaxation.Envelopes.Count);
        Assert.AreEqual("suppressed", (await ReadOutboxStateAsync(initiallyBlockedOutboxId)).Status);

        var allowedDispatcher = new RecordingOutboxDispatcher();
        var allowedSuggestionService = CreatePatronSuggestionService(
            allowedDomains: ["example.org"],
            allowedDispatcher);
        var queuedResult = await allowedSuggestionService.CreateAsync(
            new PatronSessionContext(
                9002,
                "20000000000041",
                2,
                2,
                2,
                DateTime.UtcNow.AddHours(1)),
            Suggestion("Allowed Then Blocked Domain"),
            CancellationToken.None);
        var queuedOutboxId = await FindSubmissionOutboxIdAsync(queuedResult.Id);
        CollectionAssert.Contains(allowedDispatcher.EnqueuedIds, queuedOutboxId);
        Assert.AreEqual("pending", (await ReadOutboxStateAsync(queuedOutboxId)).Status);

        var senderAfterTightening = new RecordingEmailSender();
        var blockedJobs = CreateEmailOutboxJobs(
            senderAfterTightening,
            EmailOutboxRuntimeOptions.Default,
            ["allowed.invalid"]);
        await blockedJobs.DeliverAsync(queuedOutboxId, CancellationToken.None);
        var suppressedAtSend = await ReadOutboxStateAsync(queuedOutboxId);
        Assert.AreEqual(0, senderAfterTightening.Envelopes.Count);
        Assert.AreEqual("suppressed", suppressedAtSend.Status);
        Assert.AreEqual("recipient_domain_not_allowed", suppressedAtSend.SuppressionReason);
    }

    [TestMethod]
    public async Task UnconfiguredTransportAtIntentCommitsSuppressedOutboxWithoutDispatch()
    {
        var localDispatcher = new RecordingOutboxDispatcher();
        var sender = new MutableReadinessEmailSender(isConfigured: false);
        var service = CreatePatronSuggestionService(
            ["example.org"],
            localDispatcher,
            sender);

        var result = await service.CreateAsync(
            new PatronSessionContext(
                9010,
                "20000000000050",
                2,
                2,
                2,
                DateTime.UtcNow.AddHours(1)),
            Suggestion($"Unavailable Transport {Guid.NewGuid():N}"),
            CancellationToken.None);

        var outboxId = await FindSubmissionOutboxIdAsync(result.Id);
        var outbox = await ReadOutboxStateAsync(outboxId);
        Assert.AreEqual("suppressed", outbox.Status);
        Assert.AreEqual("mail_not_configured", outbox.SuppressionReason);
        Assert.AreEqual(0, localDispatcher.EnqueuedIds.Count);
    }

    [TestMethod]
    public async Task TransportReadinessLostBeforeDeliveryFailsWithoutProviderCall()
    {
        var localDispatcher = new RecordingOutboxDispatcher();
        var sender = new MutableReadinessEmailSender(isConfigured: true);
        var service = CreatePatronSuggestionService(["example.org"], localDispatcher, sender);
        var result = await service.CreateAsync(
            new PatronSessionContext(
                9011,
                "20000000000051",
                2,
                2,
                2,
                DateTime.UtcNow.AddHours(1)),
            Suggestion($"Readiness Lost {Guid.NewGuid():N}"),
            CancellationToken.None);
        var outboxId = await FindSubmissionOutboxIdAsync(result.Id);
        sender.IsConfigured = false;

        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        await jobs.DeliverAsync(outboxId, CancellationToken.None);

        var outbox = await ReadOutboxStateAsync(outboxId);
        Assert.AreEqual(0, sender.SendCount);
        Assert.AreEqual("failed", outbox.Status);
        Assert.AreEqual("mail_not_configured", outbox.LastErrorCode);
    }

    [TestMethod]
    public async Task TransportCanReportConfigurationLostAtSendBoundary()
    {
        var seeded = await SeedSensitiveOutboxAsync("send-not-configured");
        var sender = new MutableReadinessEmailSender(isConfigured: true)
        {
            ReturnNotConfiguredOnSend = true
        };
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);

        var outbox = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual(1, sender.SendCount);
        Assert.AreEqual("failed", outbox.Status);
        Assert.AreEqual("mail_not_configured", outbox.LastErrorCode);
    }

    [TestMethod]
    public async Task DuplicateIdentifierMatchWinsAndReturnsEscapedConfiguredContext()
    {
        long identifierRequestId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                IF NOT EXISTS (SELECT 1 FROM [asap].[PatronSettings] WHERE [OrganizationId] = 2)
                    INSERT INTO [asap].[PatronSettings]
                        ([OrganizationId], [AlreadySubmittedMessage], [RejectedStatusLabel], [UpdatedUtc])
                    VALUES
                        (2, N'Duplicate {{duplicate_title}} is {{duplicate_status}} via {{duplicate_match_type}}.',
                         N'Library declined', SYSUTCDATETIME());
                ELSE
                    UPDATE [asap].[PatronSettings]
                    SET [AlreadySubmittedMessage] = N'Duplicate {{duplicate_title}} is {{duplicate_status}} via {{duplicate_match_type}}.',
                        [RejectedStatusLabel] = N'Library declined', [UpdatedUtc] = SYSUTCDATETIME()
                    WHERE [OrganizationId] = 2;

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author], [Identifier],
                     [Publication], [AutoHold], [MaterialFormatId], [Status], [CloseReason], [CreatedUtc], [UpdatedUtc])
                OUTPUT inserted.[Id]
                VALUES
                    (2, 101, N'20000000000002', N'Identifier <Winner>', N'First Author', N'DUPLICATE-IDENTIFIER',
                     N'Coming soon', 1,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                     N'closed', N'rejected', DATEADD(day, -2, SYSUTCDATETIME()), DATEADD(day, -2, SYSUTCDATETIME()));

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author], [Identifier],
                     [Publication], [AutoHold], [MaterialFormatId], [Status], [CreatedUtc], [UpdatedUtc])
                VALUES
                    (2, 101, N'20000000000002', N'Newer Title', N'Second Author', N'OTHER-IDENTIFIER',
                     N'Coming soon', 1,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                     N'suggestion', DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()));
                """;
            identifierRequestId = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        using var client = factory!.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000002", pin = "1234", libraryOrgId = 2 });
        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginDocument.RootElement.GetProperty("token").GetString());

        var response = await client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            new
            {
                format = "book",
                title = "Newer Title",
                author = "Another Author",
                isbn = "DUPLICATE-IDENTIFIER",
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = responseDocument.RootElement;
        Assert.AreEqual(
            "This patron already has a suggestion for this identifier number.",
            root.GetProperty("message").GetString());
        Assert.AreEqual(
            "Duplicate Identifier &lt;Winner&gt; is Library declined via identifier number.",
            root.GetProperty("conflictMessage").GetString());
        var duplicate = root.GetProperty("duplicate");
        Assert.AreEqual(identifierRequestId, duplicate.GetProperty("id").GetInt64());
        Assert.AreEqual("Identifier <Winner>", duplicate.GetProperty("title").GetString());
        Assert.AreEqual("First Author", duplicate.GetProperty("author").GetString());
        Assert.AreEqual("book", duplicate.GetProperty("format").GetString());
        Assert.AreEqual("closed", duplicate.GetProperty("status").GetString());
        Assert.AreEqual("rejected", duplicate.GetProperty("closeReason").GetString());
        Assert.AreEqual("identifier", duplicate.GetProperty("matchType").GetString());
    }

    [TestMethod]
    public async Task CrossPatronIdentifierDuplicateCommitsTagAndExactSystemNote()
    {
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author], [Identifier],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                     [AutoHold], [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES
                    (2, 101, N'20000000000010', N'Existing Other Patron Request', N'Existing Author', N'CROSS-PATRON-ID',
                     101, N'Main Library', N'Test Library', 1,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                     N'suggestion', N'not_found', DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()));
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var client = factory!.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000011", pin = "1234", libraryOrgId = 2 });
        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginDocument.RootElement.GetProperty("token").GetString());

        var response = await client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            new
            {
                format = "book",
                title = "A New Cross Patron Request",
                author = "New Author",
                isbn = "CROSS-PATRON-ID",
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            """
            SELECT request.[Notes], COUNT(tag.[Id])
            FROM [asap].[TitleRequest] AS request
            LEFT JOIN [asap].[TitleRequestWorkflowTag] AS requestTag ON requestTag.[TitleRequestId] = request.[Id]
            LEFT JOIN [asap].[WorkflowTag] AS tag
              ON tag.[Id] = requestTag.[WorkflowTagId] AND tag.[Code] = N'duplicate_suggestion'
            WHERE request.[LibraryOrganizationId] = 2 AND request.[Barcode] = N'20000000000011'
            GROUP BY request.[Notes];
            """;
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        CollectionAssert.Contains(
            reader.GetString(0).Split(["\r\n"], StringSplitOptions.None),
            "Tagged as a duplicate suggestion because another patron has a suggestion with the same identifier number.",
            reader.GetString(0));
        Assert.AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task WeeklyLimitUsesSevenBusinessCalendarDaysAcrossSpringGap()
    {
        timeProvider!.SetUtcNow(new DateTimeOffset(2030, 3, 17, 6, 30, 0, TimeSpan.Zero));
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                IF EXISTS (SELECT 1 FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2)
                    UPDATE [asap].[WorkflowSettings]
                    SET [SuggestionLimit] = 1,
                        [SuggestionLimitMessage] = N'Available after {{next_available_date}}.',
                        [UpdatedUtc] = SYSUTCDATETIME()
                    WHERE [OrganizationId] = 2;
                ELSE
                    INSERT INTO [asap].[WorkflowSettings]
                        ([OrganizationId], [SuggestionLimit], [SuggestionLimitMessage], [UpdatedUtc])
                    VALUES
                        (2, 1, N'Available after {{next_available_date}}.', SYSUTCDATETIME());

                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [Author], [Identifier],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                     [AutoHold], [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES
                    (2, 101, N'20000000000020', N'Outside Calendar Window', N'Test Author', N'OLD-LIMIT-ID',
                     101, N'Main Library', N'Test Library', 1,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                     N'suggestion', N'not_found', '2030-03-10T07:00:00Z', '2030-03-10T07:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }

        using var client = factory!.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/asap/patron/login",
            new { barcode = "20000000000020", pin = "1234", libraryOrgId = 2 });
        using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            loginDocument.RootElement.GetProperty("token").GetString());

        var response = await client.PostAsJsonAsync(
            "/api/asap/patron/suggestions",
            new
            {
                format = "book",
                title = "Inside New Calendar Window",
                author = "Test Author",
                isbn = "NEW-LIMIT-ID",
                publication = "Coming soon",
                preferredPickupBranchId = 101,
                autohold = true,
                customFields = new Dictionary<string, string?>()
            });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> WriteStaffConfigurationAsync(
        string fileName,
        IReadOnlyList<Guid> allowedTenantIds,
        Guid? initialTenantId = null,
        Guid? initialObjectId = null)
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        configuration.Application.DataProtectionKeysPath = Path.Combine(temporaryDirectory, "keys");
        configuration.Application.LogPath = Path.Combine(temporaryDirectory, "logs");
        configuration.Application.DataProtectionKeyEncryptionCertificateThumbprint = certificateThumbprint;
        configuration.Authentication.Entra.AllowedTenantIds = allowedTenantIds
            .Select(value => value.ToString())
            .ToList();
        if (initialTenantId.HasValue)
        {
            configuration.Authentication.Entra.InitialSuperAdmin.TenantId = initialTenantId.Value.ToString();
        }
        if (initialObjectId.HasValue)
        {
            configuration.Authentication.Entra.InitialSuperAdmin.ObjectId = initialObjectId.Value.ToString();
        }
        var path = Path.Combine(temporaryDirectory, fileName);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string ProtectStaffCookie(
        WebApplicationFactory<Program> applicationFactory,
        long staffUserId,
        Guid tenantId,
        string authenticationEmail)
    {
        var cookieOptions = applicationFactory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(StaffAuthenticationRegistration.CookieScheme);
        var identity = new ClaimsIdentity(
            [
                new Claim(StaffClaims.StaffUserId, staffUserId.ToString()),
                new Claim(StaffClaims.TenantId, tenantId.ToString()),
                new Claim(StaffClaims.AuthenticationEmail, authenticationEmail)
            ],
            StaffAuthenticationRegistration.CookieScheme);
        var properties = new AuthenticationProperties
        {
            AllowRefresh = true,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        };
        return cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                properties,
                StaffAuthenticationRegistration.CookieScheme));
    }

    private static HttpClient CookieClient(
        WebApplicationFactory<Program> applicationFactory,
        string protectedCookie)
    {
        var client = applicationFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-ASAP.Staff={protectedCookie}");
        return client;
    }

    private static void AddTestingStaffHeaders(
        HttpClient client,
        long staffUserId,
        Guid tenantId,
        string authenticationEmail)
    {
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", staffUserId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", authenticationEmail);
    }

    private static async Task<string> ReadAntiforgeryTokenAsync(HttpClient client)
    {
        using var session = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode, await session.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("antiforgeryToken").GetString()!;
    }

    private static async Task<OperatorResolutionSeed> SeedOperatorResolutionAsync(
        string suffix,
        string phase,
        bool liveOwner = false)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (2, N'200000000031' + RIGHT(N'00' + CONVERT(nvarchar(2), ABS(CHECKSUM(NEWID())) % 100), 2),
                    N'Operator resolution ' + @suffix, 1, @formatId, N'pending_hold',
                    CONVERT(nvarchar(20), 93100 + ABS(CHECKSUM(NEWID())) % 800),
                    101, N'Main Library', N'found', DATEADD(minute, -8, SYSUTCDATETIME()), DATEADD(minute, -8, SYSUTCDATETIME()));
            DECLARE @requestId bigint = SCOPE_IDENTITY();
            DECLARE @barcode nvarchar(100) = (SELECT [Barcode] FROM [asap].[TitleRequest] WHERE [Id] = @requestId);
            DECLARE @bibId nvarchar(100) = (SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @requestId);
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                 [RequestingOrganizationIdSnapshot], [WorkstationIdSnapshot], [PolarisUserIdSnapshot],
                 [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch], [LeaseExpiresUtc], [RequestStartedUtc],
                 [CreateStartedUtc], [CreateResponseObservedUtc], [ReplyStartedUtc], [PolarisRequestGuid],
                 [TxnGroupQualifier], [TxnQualifier], [ReplyAnswer], [ReplyState], [ResultCode], [OutcomeEvidenceKind],
                 [RecoveryAttemptCount], [LastRecoveryUtc], [LastErrorCode], [DetailJson])
            VALUES
                (@requestId, @barcode, N'7105', @bibId, 101, 1, 1, N'1',
                 1, N'operator_required', @phase,
                 CASE WHEN @liveOwner = 1 THEN NEWID() ELSE NULL END, 2,
                 CASE WHEN @liveOwner = 1 THEN DATEADD(minute, 2, SYSUTCDATETIME()) ELSE NULL END,
                 DATEADD(minute, -7, SYSUTCDATETIME()),
                 CASE WHEN @phase IN (N'create_started', N'reply_started') THEN DATEADD(minute, -6, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN DATEADD(minute, -5, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN DATEADD(minute, -4, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN CONVERT(nvarchar(100), NEWID()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'group-qualifier' ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'transaction-qualifier' ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'Yes' ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'3' ELSE NULL END,
                 N'ambiguous', N'provider_transport_ambiguous', 1, DATEADD(minute, -3, SYSUTCDATETIME()),
                 N'provider_timeout', N'{"providerObservation":{"result":"ambiguous","source":"CLC"}}');
            DECLARE @operationId bigint = SCOPE_IDENTITY();
            SELECT @requestId, @operationId, request.[RowVersion], operation.[RowVersion]
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[Id] = @operationId
            WHERE request.[Id] = @requestId;
            """;
        seed.Parameters.AddWithValue("@suffix", suffix);
        seed.Parameters.AddWithValue("@phase", phase);
        seed.Parameters.AddWithValue("@liveOwner", liveOwner);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new OperatorResolutionSeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            StaffVersion.Encode((byte[])reader[2]),
            StaffVersion.Encode((byte[])reader[3]));
    }

    private static Dictionary<string, object?> OperatorResolutionPayload(
        OperatorResolutionSeed seed,
        string outcome,
        string evidenceKind,
        string? provenFinalHoldId = null,
        bool markedExecutionExcluded = false) => new()
    {
        ["version"] = seed.OperationVersion,
        ["requestVersion"] = seed.RequestVersion,
        ["outcome"] = outcome,
        ["reason"] = "The retained provider record establishes the definitive outcome for this exact journal attempt.",
        ["evidenceKind"] = evidenceKind,
        ["evidenceReference"] = $"support-record-{seed.OperationId}",
        ["operationSpecificProofAttested"] = true,
        ["proofSource"] = "Polaris operations report retained with the incident record",
        ["causalConnection"] = "The report identifies this operation, attempt, frozen patron, BIB, and definitive outcome.",
        ["provenFinalHoldId"] = provenFinalHoldId,
        ["originalExecutorExcluded"] = markedExecutionExcluded,
        ["executorExclusionAttested"] = markedExecutionExcluded,
        ["executorExclusionReference"] = markedExecutionExcluded ? $"executor-inventory-{seed.OperationId}" : null,
        ["executorExclusionExplanation"] = markedExecutionExcluded
            ? "All responsible and superseded Hangfire, interactive, and overlapping IIS executions ended by 2026-09-13T13:00:00Z; already-sent provider work is covered by the cited definitive result."
            : null
    };

    private static async Task AssertResolutionRejectedWithoutHistoryAsync(OperatorResolutionSeed seed)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT operation.[CompletedUtc], request.[Status],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = request.[Id] AND [EventType] IN (N'hold_placed', N'hold_operation_resolved_not_performed')),
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]
                    WHERE [TargetType] = N'hold_placement_operation' AND [TargetId] = CONVERT(nvarchar(30), operation.[Id]))
            FROM [asap].[HoldPlacementOperation] operation
            JOIN [asap].[TitleRequest] request ON request.[Id] = operation.[TitleRequestId]
            WHERE operation.[Id] = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@id", seed.OperationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.IsDBNull(0));
        Assert.AreEqual("pending_hold", reader.GetString(1));
        Assert.AreEqual(0, reader.GetInt32(2));
        Assert.AreEqual(0, reader.GetInt32(3));
    }

    private static async Task ExecuteNonQueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> SeedPendingHoldRequestAsync(string title, string barcode, string bibId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (2, @barcode, @title, 1, @formatId, N'pending_hold', @bibId,
                    101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT CONVERT(bigint, SCOPE_IDENTITY());
            """,
            connection);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@barcode", barcode);
        command.Parameters.AddWithValue("@bibId", bibId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task InstallMarkerFenceTriggerAsync(string marker)
    {
        Assert.IsTrue(marker is "create_started" or "reply_started");
        var condition = marker == "create_started"
            ? "deleted.[CreateStartedUtc] IS NULL AND inserted.[CreateStartedUtc] IS NOT NULL"
            : "deleted.[ReplyStartedUtc] IS NULL AND inserted.[ReplyStartedUtc] IS NOT NULL";
        await ExecuteNonQueryAsync(
            $"""
            CREATE OR ALTER TRIGGER [asap].[TR_Test_HoldMarkerFence]
            ON [asap].[HoldPlacementOperation]
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE operation
                SET [OwnerToken] = NULL,
                    [LeaseExpiresUtc] = NULL,
                    [ExecutionEpoch] = operation.[ExecutionEpoch] + 1,
                    [State] = N'operator_required',
                    [LastErrorCode] = N'test_marker_takeover'
                FROM [asap].[HoldPlacementOperation] operation
                JOIN inserted ON inserted.[Id] = operation.[Id]
                JOIN deleted ON deleted.[Id] = operation.[Id]
                WHERE {condition};
            END;
            """);
    }

    private static Task DropMarkerFenceTriggerAsync() =>
        ExecuteNonQueryAsync("DROP TRIGGER IF EXISTS [asap].[TR_Test_HoldMarkerFence];");

    private static async Task<CompletedHoldIdentitySeed> SeedCompletedHoldIdentityAsync(
        string titleSuffix,
        string requestBarcode,
        string requestBibId,
        string? operationBarcode = null,
        string? operationBibId = null,
        string? holdRequestId = null,
        int organizationId = 2)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (@organizationId, @requestBarcode, N'Identity fence ' + @titleSuffix, 1, @formatId, N'hold_placed', @requestBibId,
                    101, N'Main Library', N'found', DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(minute, -2, SYSUTCDATETIME()));
            DECLARE @requestId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                 [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CompletedUtc],
                 [ResultCode], [OutcomeEvidenceKind], [LastErrorCode], [DetailJson], [PolarisHoldId])
            VALUES (@requestId, @operationBarcode, @operationBibId, 101,
                    1, N'succeeded', N'result_recorded', 1, DATEADD(minute, -4, SYSUTCDATETIME()),
                    DATEADD(minute, -2, SYSUTCDATETIME()), N'success', N'provider_final_success_uncorrelated',
                    CASE WHEN @holdRequestId IS NULL THEN N'hold_identity_unavailable' ELSE NULL END,
                    N'{"providerResult":"success"}', @holdRequestId);
            DECLARE @operationId bigint = SCOPE_IDENTITY();
            SELECT @requestId, @operationId, request.[RowVersion], operation.[RowVersion]
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[Id] = @operationId
            WHERE request.[Id] = @requestId;
            """;
        seed.Parameters.AddWithValue("@organizationId", organizationId);
        seed.Parameters.AddWithValue("@requestBarcode", requestBarcode);
        seed.Parameters.AddWithValue("@requestBibId", requestBibId);
        seed.Parameters.AddWithValue("@operationBarcode", operationBarcode ?? requestBarcode);
        seed.Parameters.AddWithValue("@operationBibId", operationBibId ?? requestBibId);
        seed.Parameters.AddWithValue("@holdRequestId", (object?)holdRequestId ?? DBNull.Value);
        seed.Parameters.AddWithValue("@titleSuffix", titleSuffix);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new CompletedHoldIdentitySeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            (byte[])reader[2],
            (byte[])reader[3]);
    }

    private static async Task<string?> ReadHoldIdentityAsync(long operationId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [PolarisHoldId] FROM [asap].[HoldPlacementOperation] WHERE [Id] = @id;",
            connection);
        command.Parameters.AddWithValue("@id", operationId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private async Task<CurrentStaff> ReadConfiguredSuperAdminAsync()
    {
        var configured = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var result = await factory!.Services.GetRequiredService<StaffEligibilityService>()
            .FindByEmailAsync(
                configured.UserPrincipalName!.ToUpperInvariant(),
                Guid.Parse(configured.TenantId!),
                null,
                StaffRoleRequirement.SuperAdmin,
                requireParticipation: true,
                CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, result.Outcome);
        return result.Staff!;
    }

    private async Task<CurrentStaff> ReadStaffMetadataActorAsync(StaffMetadataRaceSeed seeded)
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var authenticationEmail = await context.StaffUsers
            .Where(item => item.Id == seeded.ActorId)
            .Select(item => item.NormalizedUserPrincipalName)
            .SingleAsync();
        var result = await factory!.Services.GetRequiredService<StaffEligibilityService>()
            .EvaluateAsync(
                new StaffIdentityEvidence(seeded.ActorId, authenticationEmail!, seeded.TenantId),
                2,
                StaffRoleRequirement.Admin,
                requireParticipation: true,
                CancellationToken.None);
        Assert.AreEqual(StaffEligibilityOutcome.Allowed, result.Outcome);
        return result.Staff!;
    }

    private static async Task<StaffMetadataRaceSeed> SeedStaffMetadataRaceAsync(string suffix)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var actorObjectId = Guid.NewGuid();
        var originalTargetDisplayName = $"Metadata target {suffix}";
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES (@tenantId, @actorObjectId, @suffix + N'.admin@example.org', UPPER(@suffix + N'.admin@example.org'),
                    N'Metadata admin ' + @suffix, @suffix + N'.admin@example.org', N'admin', 2, 1);
            DECLARE @actorId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES (@tenantId, NEWID(), @suffix + N'.target@example.org', UPPER(@suffix + N'.target@example.org'),
                    @targetDisplayName, @suffix + N'.target@example.org', N'staff', 2, 1);
            DECLARE @targetId bigint = SCOPE_IDENTITY();
            SELECT @actorId, @targetId, actorRow.[RowVersion], targetRow.[RowVersion]
            FROM [asap].[StaffUser] actorRow
            JOIN [asap].[StaffUser] targetRow ON targetRow.[Id] = @targetId
            WHERE actorRow.[Id] = @actorId;
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@actorObjectId", actorObjectId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        seed.Parameters.AddWithValue("@targetDisplayName", originalTargetDisplayName);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new StaffMetadataRaceSeed(
            reader.GetInt64(0),
            tenantId,
            actorObjectId,
            (byte[])reader[2],
            reader.GetInt64(1),
            (byte[])reader[3],
            originalTargetDisplayName);
    }

    private static async Task AssertStaffMetadataStateAsync(
        long targetId,
        string expectedDisplayName,
        int expectedAuditCount)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT [DisplayName],
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit]
                    WHERE [TargetType] = N'StaffUser'
                      AND [TargetId] = CONVERT(nvarchar(40), @targetId)
                      AND [Action] = N'staff_metadata_updated')
            FROM [asap].[StaffUser]
            WHERE [Id] = @targetId;
            """,
            connection);
        command.Parameters.AddWithValue("@targetId", targetId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(expectedDisplayName, reader.GetString(0));
        Assert.AreEqual(expectedAuditCount, reader.GetInt32(1));
    }

    private static async Task<LifecycleClaimRaceSeed> SeedLifecycleClaimRaceAsync(
        string suffix,
        string barcode)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var objectId = Guid.NewGuid();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES (@tenantId, @objectId, N'claim-' + @suffix + N'@example.org', UPPER(N'claim-' + @suffix + N'@example.org'),
                    @suffix, N'claim-' + @suffix + N'@example.org', N'staff', 2, 1);
            DECLARE @staffId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[FormatAutoClaimRule]
                ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
            VALUES (2, @formatId, @staffId, 1, SYSUTCDATETIME());
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                 [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (2, @barcode, N'Lifecycle race ' + @suffix, 1, @formatId, N'suggestion',
                    N'skipped_no_isbn', SYSUTCDATETIME(), SYSUTCDATETIME());
            DECLARE @requestId bigint = SCOPE_IDENTITY();
            SELECT @staffId, @requestId, staff.[RowVersion], request.[RowVersion]
            FROM [asap].[StaffUser] staff
            JOIN [asap].[TitleRequest] request ON request.[Id] = @requestId
            WHERE staff.[Id] = @staffId;
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@objectId", objectId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        seed.Parameters.AddWithValue("@barcode", barcode);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new LifecycleClaimRaceSeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            (byte[])reader[2],
            (byte[])reader[3]);
    }

    private static async Task AssertLifecycleRaceStateAsync(
        LifecycleClaimRaceSeed seeded,
        int expectedAssignmentEvents,
        int expectedCleanupEvents)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT staff.[IsActive], request.[ClaimedByStaffUserId],
                   (SELECT COUNT(*) FROM [asap].[FormatAutoClaimRule]
                    WHERE [StaffUserId] = @staffId AND [IsActive] = 1),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'claim_manual_assigned'),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'claim_cleared')
            FROM [asap].[StaffUser] staff
            JOIN [asap].[TitleRequest] request ON request.[Id] = @requestId
            WHERE staff.[Id] = @staffId;
            """,
            connection);
        command.Parameters.AddWithValue("@staffId", seeded.StaffId);
        command.Parameters.AddWithValue("@requestId", seeded.RequestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsFalse(reader.GetBoolean(0));
        Assert.IsTrue(reader.IsDBNull(1));
        Assert.AreEqual(0, reader.GetInt32(2));
        Assert.AreEqual(expectedAssignmentEvents, reader.GetInt32(3));
        Assert.AreEqual(expectedCleanupEvents, reader.GetInt32(4));
    }

    private static async Task<LifecycleAutoClaimRaceSeed> SeedLifecycleAutoClaimRaceAsync(string suffix)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        var objectId = Guid.NewGuid();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES (@tenantId, @objectId, N'auto-claim-' + @suffix + N'@example.org', UPPER(N'auto-claim-' + @suffix + N'@example.org'),
                    @suffix, N'auto-claim-' + @suffix + N'@example.org', N'staff', 2, 1);
            DECLARE @staffId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[FormatAutoClaimRule]
                ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc])
            VALUES (2, @formatId, @staffId, 1, SYSUTCDATETIME());
            DECLARE @ruleId bigint = SCOPE_IDENTITY();
            SELECT @staffId, @ruleId, [RowVersion]
            FROM [asap].[StaffUser]
            WHERE [Id] = @staffId;
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@objectId", objectId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new LifecycleAutoClaimRaceSeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            (byte[])reader[2]);
    }

    private static async Task WaitForOrganizationUpdateLockAsync(int organizationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "SET LOCK_TIMEOUT 100; SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @id;",
                connection);
            command.Parameters.AddWithValue("@id", organizationId);
            try
            {
                await command.ExecuteScalarAsync();
            }
            catch (SqlException exception) when (exception.Number == 1222)
            {
                return;
            }
            await Task.Delay(50);
        }

        Assert.Fail("Submission did not acquire the expected organization update lock.");
    }

    private static async Task AssertAutomaticRuleRaceStateAsync(
        LifecycleAutoClaimRaceSeed seeded,
        long requestId,
        int expectedAssignedEvents,
        int expectedCleanupEvents)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT staff.[IsActive], autoRule.[IsActive], request.[ClaimedByStaffUserId], request.[ClaimRuleId],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'claim_auto_assigned'),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'claim_cleared')
            FROM [asap].[StaffUser] staff
            JOIN [asap].[FormatAutoClaimRule] autoRule ON autoRule.[Id] = @ruleId
            JOIN [asap].[TitleRequest] request ON request.[Id] = @requestId
            WHERE staff.[Id] = @staffId;
            """,
            connection);
        command.Parameters.AddWithValue("@staffId", seeded.StaffId);
        command.Parameters.AddWithValue("@ruleId", seeded.RuleId);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsFalse(reader.GetBoolean(0));
        Assert.IsFalse(reader.GetBoolean(1));
        Assert.IsTrue(reader.IsDBNull(2));
        Assert.IsTrue(reader.IsDBNull(3));
        Assert.AreEqual(expectedAssignedEvents, reader.GetInt32(4));
        Assert.AreEqual(expectedCleanupEvents, reader.GetInt32(5));
    }

    private static async Task InstallHangfireAsync()
    {
        var sql = await File.ReadAllTextAsync(TestArtifactPaths.FindRepositoryFile(
            "scripts", "hangfire", "1.8.25", "install.sql"));
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<AdditionalCopyLegacySourceSeed> SeedAdditionalCopyLegacySourceAsync(
        long actorId,
        string suffix)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            UPDATE [asap].[StaffUser]
            SET [NotificationEmail]=N'slice3.actor@example.org', [AdditionalCopyReminderDefault]=1
            WHERE [Id]=@actorId;

            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES
                (@tenantId, NEWID(), @suffix + N'.current@example.org', UPPER(@suffix + N'.current@example.org'),
                 N'Current claimant ' + @suffix, @suffix + N'.current@example.org', N'staff', 2, 1);
            DECLARE @currentId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[StaffUser]
                ([UserPrincipalName], [NormalizedUserPrincipalName], [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (@suffix + N'.historical@example.org', UPPER(@suffix + N'.historical@example.org'),
                 N'Historical rule owner ' + @suffix, N'staff', 2, 0);
            DECLARE @historicalId bigint = SCOPE_IDENTITY();
            DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code]=N'book');

            INSERT INTO [asap].[FormatAutoClaimRule]
                ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc], [DeactivatedUtc])
            VALUES (2, @formatId, @historicalId, 0, DATEADD(day, -2, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()));
            DECLARE @ruleId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [Barcode], [Title], [Author], [Identifier],
                 [Publication], [AutoHold], [MaterialFormatId], [Status], [BibId], [Notes],
                 [ClaimedByStaffUserId], [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId],
                 [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Frozen source library ' + @suffix, N'20000000003' + RIGHT(N'000' + CONVERT(nvarchar(3), ABS(CHECKSUM(@suffix)) % 1000), 3),
                 N'Legacy source ' + @suffix, N'Slice 3 Author', N'9780000000300', N'Source publication', 1,
                 @formatId, N'pending_hold', N'slice3-bib-' + @suffix, N'Original source note ' + @suffix,
                 @currentId, N'Current claimant snapshot ' + @suffix, DATEADD(hour, -2, SYSUTCDATETIME()),
                 N'legacy', @ruleId, N'found', DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(hour, -1, SYSUTCDATETIME()));
            DECLARE @sourceId bigint = SCOPE_IDENTITY();
            SELECT @sourceId, @currentId, @ruleId, sourceRow.[RowVersion]
            FROM [asap].[TitleRequest] sourceRow WHERE sourceRow.[Id]=@sourceId;
            """;
        seed.Parameters.AddWithValue("@actorId", actorId);
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new AdditionalCopyLegacySourceSeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            (byte[])reader[3]);
    }

    private static async Task AssertAdditionalCopyReopenRoleChangeRaceAsync(
        CurrentStaff actor,
        AdditionalCopyService copies,
        StaffLifecycleService lifecycle,
        string suffix,
        string initialRole,
        int initialOrganizationId,
        string targetRole,
        int targetOrganizationId)
    {
        var reopenFirst = await SeedAdditionalCopyRaceAsync(
            $"{suffix}-reopen-first",
            closed: true,
            claimed: true,
            initialRole,
            initialOrganizationId);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockTask = new SqlCommand(
                             "SELECT [Id] FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockTask.Parameters.AddWithValue("@id", reopenFirst.TaskId);
                Assert.AreEqual(reopenFirst.TaskId, Convert.ToInt64(await blockTask.ExecuteScalarAsync()));
            }
            var reopen = copies.SetClosedAsync(
                actor,
                reopenFirst.TaskId,
                new VersionInput(StaffVersion.Encode(reopenFirst.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(reopen.IsCompleted, suffix);
            var roleChange = lifecycle.ChangeRoleAsync(
                actor,
                reopenFirst.StaffId,
                new StaffRoleInput(StaffVersion.Encode(reopenFirst.StaffVersion), targetRole, targetOrganizationId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(roleChange.IsCompleted, suffix);
            await blockerTransaction.CommitAsync();
            var reopenResult = await reopen;
            Assert.AreEqual("updated", reopenResult.Code, suffix);
            Assert.IsNull(reopenResult.ClaimClearedReason, suffix);
            var lifecycleResult = await roleChange;
            Assert.AreEqual("updated", lifecycleResult.Code, suffix);
            Assert.AreEqual(1, lifecycleResult.OpenAdditionalCopyClaimsCleared, suffix);
        }
        await AssertAdditionalCopyClaimStateAsync(
            reopenFirst.TaskId,
            null,
            "staff_scope_contracted",
            1,
            reopenFirst.StaffId,
            "open");

        var lifecycleFirst = await SeedAdditionalCopyRaceAsync(
            $"{suffix}-lifecycle-first",
            closed: true,
            claimed: true,
            initialRole,
            initialOrganizationId);
        await using (var blockerConnection = new SqlConnection(databaseConnectionString))
        {
            await blockerConnection.OpenAsync();
            await using var blockerTransaction = (SqlTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var blockStaff = new SqlCommand(
                             "SELECT [Id] FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]=@id;",
                             blockerConnection,
                             blockerTransaction))
            {
                blockStaff.Parameters.AddWithValue("@id", lifecycleFirst.StaffId);
                Assert.AreEqual(lifecycleFirst.StaffId, Convert.ToInt64(await blockStaff.ExecuteScalarAsync()));
            }
            var roleChange = lifecycle.ChangeRoleAsync(
                actor,
                lifecycleFirst.StaffId,
                new StaffRoleInput(StaffVersion.Encode(lifecycleFirst.StaffVersion), targetRole, targetOrganizationId),
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(roleChange.IsCompleted, suffix);
            var reopen = copies.SetClosedAsync(
                actor,
                lifecycleFirst.TaskId,
                new VersionInput(StaffVersion.Encode(lifecycleFirst.TaskVersion)),
                reopen: true,
                CancellationToken.None);
            await Task.Delay(250);
            Assert.IsFalse(reopen.IsCompleted, suffix);
            await blockerTransaction.CommitAsync();
            var lifecycleResult = await roleChange;
            Assert.AreEqual("updated", lifecycleResult.Code, suffix);
            Assert.AreEqual(0, lifecycleResult.OpenAdditionalCopyClaimsCleared, suffix);
            var reopenResult = await reopen;
            Assert.AreEqual("updated", reopenResult.Code, suffix);
            Assert.AreEqual("claimant_out_of_scope", reopenResult.ClaimClearedReason, suffix);
        }
        await AssertAdditionalCopyClaimStateAsync(
            lifecycleFirst.TaskId,
            null,
            "claimant_out_of_scope",
            0,
            lifecycleFirst.StaffId,
            "open");
    }

    private static async Task<AdditionalCopyRaceSeed> SeedAdditionalCopyRaceAsync(
        string suffix,
        bool closed,
        bool claimed,
        string staffRole = "staff",
        int staffOrganizationId = 2)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id]=83)
            BEGIN
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                VALUES (83, N'Additional Copy Race Library', N'ACR', 1);
            END;
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive])
            VALUES
                (@tenantId, NEWID(), N'additional-copy-' + @suffix + N'@example.org', UPPER(N'additional-copy-' + @suffix + N'@example.org'),
                 N'Race claimant ' + @suffix, N'additional-copy-' + @suffix + N'@example.org', @staffRole, @staffOrganizationId, 1);
            DECLARE @staffId bigint = SCOPE_IDENTITY();
            DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code]=N'book');

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [MaterialFormatId], [FormatSnapshot],
                 [Status], [Notes], [CreatedUtc], [UpdatedUtc], [ClaimedByStaffUserId], [ClaimedByDisplayName],
                 [ClaimedAtUtc], [ClaimType], [ClosedUtc])
            VALUES
                (2, N'Test Library snapshot', N'race-bib-' + @suffix, N'Race task ' + @suffix, @formatId, N'book',
                 @status, N'Race note ' + @suffix, DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(hour, -1, SYSUTCDATETIME()),
                 CASE WHEN @claimed=1 THEN @staffId END,
                 CASE WHEN @claimed=1 THEN N'Race claimant snapshot ' + @suffix END,
                 CASE WHEN @claimed=1 THEN DATEADD(hour, -3, SYSUTCDATETIME()) END,
                 CASE WHEN @claimed=1 THEN N'manual' END,
                 CASE WHEN @closed=1 THEN DATEADD(minute, -30, SYSUTCDATETIME()) END);
            DECLARE @taskId bigint = SCOPE_IDENTITY();
            SELECT @staffId, @taskId, staffRow.[RowVersion], taskRow.[RowVersion]
            FROM [asap].[StaffUser] staffRow
            JOIN [asap].[AdditionalCopyRequest] taskRow ON taskRow.[Id]=@taskId
            WHERE staffRow.[Id]=@staffId;
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        seed.Parameters.AddWithValue("@status", closed ? "closed" : "open");
        seed.Parameters.AddWithValue("@closed", closed);
        seed.Parameters.AddWithValue("@claimed", claimed);
        seed.Parameters.AddWithValue("@staffRole", staffRole);
        seed.Parameters.AddWithValue("@staffOrganizationId", staffOrganizationId);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new AdditionalCopyRaceSeed(
            reader.GetInt64(0),
            reader.GetInt64(1),
            (byte[])reader[2],
            (byte[])reader[3]);
    }

    private static async Task<AdditionalCopyCandidateChangeSeed> SeedAdditionalCopyCandidateChangeAsync(string suffix)
    {
        var tenantId = Guid.Parse(TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin.TenantId!);
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (@tenantId, NEWID(), @suffix + N'.first@example.org', UPPER(@suffix + N'.first@example.org'),
                 N'First candidate', N'staff', 2, 1);
            DECLARE @firstId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (@tenantId, NEWID(), @suffix + N'.second@example.org', UPPER(@suffix + N'.second@example.org'),
                 N'Second candidate', N'staff', 2, 1);
            DECLARE @secondId bigint = SCOPE_IDENTITY();
            DECLARE @formatId bigint = (SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code]=N'book');

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [MaterialFormatId], [FormatSnapshot],
                 [Status], [Notes], [CreatedUtc], [UpdatedUtc], [ClaimedByStaffUserId], [ClaimedByDisplayName],
                 [ClaimedAtUtc], [ClaimType], [ClosedUtc])
            VALUES
                (2, N'Test Library snapshot', N'candidate-bib', N'Candidate-change task', @formatId, N'book',
                 N'closed', N'Candidate note', DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(hour, -1, SYSUTCDATETIME()),
                 @firstId, N'First candidate snapshot', DATEADD(hour, -3, SYSUTCDATETIME()), N'manual',
                 DATEADD(minute, -30, SYSUTCDATETIME()));
            DECLARE @taskId bigint = SCOPE_IDENTITY();
            SELECT @taskId, @secondId, taskRow.[RowVersion]
            FROM [asap].[AdditionalCopyRequest] taskRow WHERE taskRow.[Id]=@taskId;
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@suffix", suffix);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new AdditionalCopyCandidateChangeSeed(reader.GetInt64(0), reader.GetInt64(1), (byte[])reader[2]);
    }

    private static async Task AssertAdditionalCopyClaimStateAsync(
        long taskId,
        long? expectedClaimantId,
        string? expectedNoteFragment,
        int expectedAuditCleanupCount,
        long? expectedLifecycleTargetId,
        string expectedStatus = "open",
        bool expectLifecycleAudit = true)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.[Status], r.[ClaimedByStaffUserId], r.[ClaimedByDisplayName], r.[ClaimedAtUtc], r.[ClaimType],
                   r.[ClaimRuleId], r.[Notes],
                   (SELECT COUNT(*) FROM [asap].[AdministrativeAudit] a
                    WHERE a.[TargetType]=N'StaffUser'
                      AND a.[TargetId]=CONVERT(nvarchar(100), @lifecycleTargetId)
                      AND JSON_VALUE(a.[DetailsJson], '$.openAdditionalCopyClaimsCleared')=@expectedCleanup
                      AND a.[CreatedUtc] >= r.[CreatedUtc])
            FROM [asap].[AdditionalCopyRequest] r WHERE r.[Id]=@taskId;
            """;
        command.Parameters.AddWithValue("@taskId", taskId);
        command.Parameters.AddWithValue("@lifecycleTargetId", (object?)expectedLifecycleTargetId ?? DBNull.Value);
        command.Parameters.AddWithValue("@expectedCleanup", expectedAuditCleanupCount.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(expectedStatus, reader.GetString(0));
        if (expectedClaimantId.HasValue)
        {
            Assert.AreEqual(expectedClaimantId.Value, reader.GetInt64(1));
        }
        else
        {
            Assert.IsTrue(reader.IsDBNull(1));
            Assert.IsTrue(reader.IsDBNull(2));
            Assert.IsTrue(reader.IsDBNull(3));
            Assert.IsTrue(reader.IsDBNull(4));
            Assert.IsTrue(reader.IsDBNull(5));
        }
        var notes = reader.IsDBNull(6) ? null : reader.GetString(6);
        if (expectedNoteFragment is null)
        {
            Assert.IsFalse(notes?.Contains("System cleared", StringComparison.Ordinal) ?? false);
        }
        else
        {
            StringAssert.Contains(notes, expectedNoteFragment);
        }
        Assert.AreEqual(expectLifecycleAudit ? 1 : 0, reader.GetInt32(7));
    }

    private static async Task<SeededStaffBrowserState> SeedStaffBrowserStateAsync(
        Guid tenantId,
        Guid superObjectId,
        Guid staffObjectId)
    {
        const string legacyRequestId = "staffbrowserlegacy0001";
        var invalidClaimantObjectId = Guid.NewGuid();
        var foreignStaffObjectId = Guid.NewGuid();
        var invalidTenantId = Guid.NewGuid();
        var invalidTenantStaffObjectId = Guid.NewGuid();
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            """
            IF NOT EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = 82)
            BEGIN
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                VALUES (82, N'Other Browser Library', N'OBR', 1);
            END;

            DECLARE @superId bigint = (
                SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG');
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                 [WeeklyActionSummaryEnabled], [PurchaseReminderDefault], [AdditionalCopyReminderDefault],
                 [DefaultMineUnclaimedFilter])
            VALUES
                (@tenantId, @staffObjectId, N'browser.staff@example.org', N'BROWSER.STAFF@EXAMPLE.ORG',
                 N'Browser Staff', N'browser.staff@example.org', N'staff', 2, 1, 0, 0, 0, 0);
            DECLARE @staffId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                 [WeeklyActionSummaryEnabled], [PurchaseReminderDefault], [AdditionalCopyReminderDefault],
                 [DefaultMineUnclaimedFilter])
            VALUES
                (@tenantId, @invalidClaimantObjectId, N'browser.invalid-copy@example.org',
                 N'BROWSER.INVALID-COPY@EXAMPLE.ORG', N'Browser Invalid Claimant',
                 N'browser.invalid-copy@example.org', N'staff', 2, 1, 0, 0, 0, 0);
            DECLARE @invalidClaimantId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (@tenantId, @foreignStaffObjectId, N'browser.foreign@example.org', N'BROWSER.FOREIGN@EXAMPLE.ORG',
                 N'Browser Foreign Staff', N'staff', 82, 1);
            DECLARE @foreignStaffId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (@invalidTenantId, @invalidTenantStaffObjectId, N'browser.invalid-tenant@example.org',
                 N'BROWSER.INVALID-TENANT@EXAMPLE.ORG', N'Browser Invalid Tenant', N'staff', 2, 1);
            DECLARE @invalidTenantStaffId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [Role], [OrganizationId], [IsActive])
            VALUES
                (NULL, NULL, N'browser.unbound@example.org', N'BROWSER.UNBOUND@EXAMPLE.ORG',
                 N'Browser Never Signed In Staff', N'staff', 2, 1);
            DECLARE @unboundStaffId bigint = SCOPE_IDENTITY();
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');

            INSERT INTO [asap].[FormatAutoClaimRule]
                ([LibraryOrganizationId], [MaterialFormatId], [StaffUserId], [IsActive], [CreatedUtc], [DeactivatedUtc])
            VALUES
                (2, @formatId, @invalidClaimantId, 0, '2026-08-31T12:00:00', '2026-08-31T13:00:00');
            DECLARE @legacyRuleId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[PublicationOptionSet] ([OrganizationId]) VALUES (2);
            INSERT INTO [asap].[PublicationOption]
                ([OrganizationId], [OptionKey], [Label], [IsEnabled], [SortOrder])
            VALUES
                (2, N'library-early', N'Library early release', 1, 10),
                (2, N'library-backlist', N'Library backlist', 1, 20);

            INSERT INTO [asap].[PatronCustomField]
                ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [HelpText], [IsEnabled], [SortOrder])
            VALUES
                (2, N'audience_note', N'text', N'Audience note', N'Local audience details', 1, 10);
            DECLARE @audienceFieldId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[PatronCustomField]
                ([LibraryOrganizationId], [FieldKey], [FieldType], [Label], [IsEnabled], [SortOrder])
            VALUES
                (2, N'binding', N'select', N'Binding', 1, 20);
            DECLARE @bindingFieldId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[PatronCustomFieldOption]
                ([PatronCustomFieldId], [OptionKey], [Label], [IsEnabled], [SortOrder])
            VALUES
                (@bindingFieldId, N'hardback', N'Hardback', 1, 10),
                (@bindingFieldId, N'paperback', N'Paperback', 1, 20);
            INSERT INTO [asap].[MaterialFormatCustomFieldRule]
                ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId], [Mode])
            VALUES
                (2, @formatId, @audienceFieldId, N'optional'),
                (2, @formatId, @bindingFieldId, N'required');

            INSERT INTO [asap].[TitleRequest]
                ([LegacyId], [LibraryOrganizationId], [Barcode], [Email], [NameFirst], [NameLast], [Title],
                 [Author], [Identifier], [Publication], [CustomFieldsJson], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [ClaimedByStaffUserId],
                 [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (@legacyId, 2, N'20000000002901', N'browser.patron@example.org', N'Browser', N'Patron',
                 N'Browser staff title', N'Browser Author', N'9780000002901', N'Historical browser publication',
                 N'{"audience_note":{"label":"Audience note","type":"text","value":"Historic audience"},"binding":{"label":"Binding","type":"select","value":"historic","displayValue":"Historic binding"},"retired":{"label":"Retired field","type":"text","value":"Keep me"}}', 1,
                 @formatId, N'suggestion', NULL, 101, N'Main Library', @superId, N'Initial Administrator',
                 SYSUTCDATETIME(), N'manual', N'not_found', SYSUTCDATETIME(), SYSUTCDATETIME());
            DECLARE @primaryId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[LegacyPocketBaseMapping] ([EntityType], [PocketBaseId], [NewId])
            VALUES (N'title_request', @legacyId, @primaryId);

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                 [Status], [BibId], [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'20000000002902', N'Blocked browser recovery title', N'9780000002902', 1, @formatId,
                 N'suggestion', N'92902', 101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
            DECLARE @blockedId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                 [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CreateStartedUtc],
                 [CreateResponseObservedUtc], [OutcomeEvidenceKind], [LastErrorCode])
            VALUES
                (@blockedId, N'20000000002902', N'92902', 101, 1, N'operator_required', N'create_started', 1,
                 DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(minute, -4, SYSUTCDATETIME()),
                 DATEADD(minute, -4, SYSUTCDATETIME()), N'create_transport_ambiguous', N'hold_identity_ambiguous');

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (82, N'20000000002903', N'Other browser library title', 0, @formatId, N'suggestion',
                 SYSUTCDATETIME(), SYSUTCDATETIME());
            DECLARE @otherId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'20000000002904', N'Operator evidence browser title', 1, @formatId, N'pending_hold', N'92904',
                 101, N'Main Library', N'found', DATEADD(minute, -8, SYSUTCDATETIME()), DATEADD(minute, -8, SYSUTCDATETIME()));
            DECLARE @resolutionId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                 [AttemptNumber], [State], [Phase], [ExecutionEpoch], [RequestStartedUtc], [CreateStartedUtc],
                 [CreateResponseObservedUtc], [ResultCode], [OutcomeEvidenceKind], [RecoveryAttemptCount],
                 [LastRecoveryUtc], [LastErrorCode], [DetailJson])
            VALUES
                (@resolutionId, N'20000000002904', N'92904', 101, 2, N'operator_required', N'create_started', 3,
                 DATEADD(minute, -7, SYSUTCDATETIME()), DATEADD(minute, -6, SYSUTCDATETIME()),
                 DATEADD(minute, -5, SYSUTCDATETIME()), N'ambiguous', N'create_transport_ambiguous', 2,
                 DATEADD(minute, -4, SYSUTCDATETIME()), N'provider_timeout', N'{"providerObservation":{"result":"ambiguous"}}');

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [Barcode], [Title], [Author], [Identifier],
                 [Publication], [AutoHold], [MaterialFormatId], [Status], [BibId], [ClaimedByStaffUserId],
                 [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClaimRuleId], [IsbnCheckStatus],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Browser Source Library Snapshot', N'20000000002905', N'Browser additional copy source',
                 N'Browser Copy Author', N'9780000002905', N'Browser Copy Publication', 1, @formatId,
                 N'hold_placed', N'92905', @superId, N'Browser Legacy Claimant', '2026-09-01T12:13:14',
                 N'legacy', @legacyRuleId, N'found', '2026-09-01T12:10:11', '2026-09-01T12:14:15');
            DECLARE @copySourceId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [Author], [Identifier],
                 [Publication], [MaterialFormatId], [FormatSnapshot], [Status], [Notes], [CreatedByStaffUserId],
                 [CreatedByDisplayName], [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Frozen Mobile Library', N'92905', N'Mobile browser additional copy',
                 N'Mobile Copy Author', N'9780000002906', N'Mobile Copy Publication', @formatId, N'book',
                 N'open', N'Mobile browser note', @staffId, N'Browser Staff',
                 '2026-09-01T12:20:00', '2026-09-01T12:20:00');
            DECLARE @mobileCopyId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([SourceTitleRequestId], [LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title],
                 [MaterialFormatId], [FormatSnapshot], [Status], [Notes], [CreatedByStaffUserId],
                 [CreatedByDisplayName], [CreatedUtc], [UpdatedUtc], [ClaimedByStaffUserId],
                 [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [ClosedByStaffUserId],
                 [ClosedByDisplayName], [ClosedUtc])
            VALUES
                (@copySourceId, 2, N'Frozen Closed Library', N'92907', N'Closed retained-claim browser task',
                 @formatId, N'book', N'closed', N'Closed history remains intact.', @superId,
                 N'Initial Administrator', '2026-09-01T12:30:00', '2026-09-01T12:32:00',
                 @invalidClaimantId, N'Browser Invalid Claimant', '2026-09-01T12:30:30', N'manual',
                 @superId, N'Initial Administrator', '2026-09-01T12:31:00');
            DECLARE @invalidClosedCopyId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'20000000002911', N'Stale title assignment A', 0, @formatId, N'suggestion',
                 '2026-09-01T13:00:00', '2026-09-01T13:00:00');
            DECLARE @staleTitleAId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'20000000002912', N'Stale title assignment B', 0, @formatId, N'suggestion',
                 '2026-09-01T13:01:00', '2026-09-01T13:01:00');
            DECLARE @staleTitleBId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [MaterialFormatId],
                 [FormatSnapshot], [Status], [CreatedByStaffUserId], [CreatedByDisplayName],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Stale Assignment Library', N'92911', N'Stale additional-copy assignment A', @formatId,
                 N'book', N'open', @superId, N'Initial Administrator',
                 '2026-09-01T13:02:00', '2026-09-01T13:02:00');
            DECLARE @staleCopyAId bigint = SCOPE_IDENTITY();
            INSERT INTO [asap].[AdditionalCopyRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [BibId], [Title], [MaterialFormatId],
                 [FormatSnapshot], [Status], [CreatedByStaffUserId], [CreatedByDisplayName],
                 [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Stale Assignment Library', N'92912', N'Stale additional-copy assignment B', @formatId,
                 N'book', N'open', @superId, N'Initial Administrator',
                 '2026-09-01T13:03:00', '2026-09-01T13:03:00');
            DECLARE @staleCopyBId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [LibraryNameSnapshot], [Barcode], [Title], [Author], [Identifier],
                 [Publication], [AutoHold], [MaterialFormatId], [Status], [BibId], [PreferredPickupBranchId],
                 [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, N'Stale Creation Library', N'20000000002913', N'Stale additional-copy creation source',
                 N'Stale Creation Author', N'9780000002913', N'Stale Creation Publication', 0, @formatId,
                 N'hold_placed', N'92913', 101, N'Main Library', N'found',
                 '2026-09-01T13:04:00', '2026-09-01T13:04:00');
            DECLARE @staleCreateSourceId bigint = SCOPE_IDENTITY();

            SELECT @superId, @staffId, @primaryId, @blockedId, @resolutionId, @otherId,
                   @copySourceId, @invalidClosedCopyId, @invalidClaimantId, @legacyRuleId, @mobileCopyId,
                   @foreignStaffId, @invalidTenantStaffId, @unboundStaffId,
                   @staleTitleAId, @staleTitleBId, @staleCopyAId, @staleCopyBId, @staleCreateSourceId;
            """;
        seed.Parameters.AddWithValue("@superObjectId", superObjectId);
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@staffObjectId", staffObjectId);
        seed.Parameters.AddWithValue("@invalidClaimantObjectId", invalidClaimantObjectId);
        seed.Parameters.AddWithValue("@foreignStaffObjectId", foreignStaffObjectId);
        seed.Parameters.AddWithValue("@invalidTenantId", invalidTenantId);
        seed.Parameters.AddWithValue("@invalidTenantStaffObjectId", invalidTenantStaffObjectId);
        seed.Parameters.AddWithValue("@legacyId", legacyRequestId);
        await using var result = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        return new SeededStaffBrowserState(
            result.GetInt64(0),
            result.GetInt64(1),
            legacyRequestId,
            result.GetInt64(2),
            result.GetInt64(3),
            result.GetInt64(4),
            result.GetInt64(5),
            result.GetInt64(6),
            result.GetInt64(7),
            result.GetInt64(8),
            result.GetInt64(9),
            result.GetInt64(10),
            result.GetInt64(11),
            result.GetInt64(12),
            result.GetInt64(13),
            result.GetInt64(14),
            result.GetInt64(15),
            result.GetInt64(16),
            result.GetInt64(17),
            result.GetInt64(18));
    }

    private sealed record SeededStaffBrowserState(
        long SuperId,
        long StaffId,
        string LegacyRequestId,
        long PrimaryRequestId,
        long BlockedRequestId,
        long ResolutionRequestId,
        long OtherRequestId,
        long CopySourceRequestId,
        long InvalidClosedCopyId,
        long InvalidClaimantId,
        long LegacyRuleId,
        long MobileCopyId,
        long ForeignStaffId,
        long InvalidTenantStaffId,
        long UnboundStaffId,
        long StaleTitleAId,
        long StaleTitleBId,
        long StaleCopyAId,
        long StaleCopyBId,
        long StaleCreateSourceId);

    private static async Task SeedLibraryAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
            VALUES (2, N'Test Library', N'TEST', 1);
            INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
            VALUES (101, N'Main Library Branch', N'MAIN', 0);
            UPDATE [asap].[EmailSettings]
            SET [FromAddress] = N'asap@example.org', [FromName] = N'ASAP Tests', [UpdatedUtc] = SYSUTCDATETIME()
            WHERE [OrganizationId] = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupSharedSlice2TestConfigurationAsync()
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE targetRule
            SET targetRule.[IsActive] = 0,
                targetRule.[DeactivatedUtc] = COALESCE(targetRule.[DeactivatedUtc], SYSUTCDATETIME())
            FROM [asap].[FormatAutoClaimRule] targetRule
            JOIN [asap].[StaffUser] staff ON staff.[Id] = targetRule.[StaffUserId]
            WHERE staff.[UserPrincipalName] = N'contracting.admin@example.org'
              AND targetRule.[IsActive] = 1;

            DELETE customRule
            FROM [asap].[MaterialFormatCustomFieldRule] customRule
            JOIN [asap].[PatronCustomField] field ON field.[Id] = customRule.[PatronCustomFieldId]
            WHERE field.[LibraryOrganizationId] = 2
              AND field.[FieldKey] IN (N'audience_note', N'binding');
            DELETE optionRow
            FROM [asap].[PatronCustomFieldOption] optionRow
            JOIN [asap].[PatronCustomField] field ON field.[Id] = optionRow.[PatronCustomFieldId]
            WHERE field.[LibraryOrganizationId] = 2
              AND field.[FieldKey] IN (N'audience_note', N'binding');
            DELETE FROM [asap].[PatronCustomField]
            WHERE [LibraryOrganizationId] = 2
              AND [FieldKey] IN (N'audience_note', N'binding');

            DELETE FROM [asap].[PublicationOption]
            WHERE [OrganizationId] = 2
              AND [OptionKey] IN (N'library-early', N'library-backlist');
            DELETE FROM [asap].[PublicationOptionSet]
            WHERE [OrganizationId] = 2
              AND NOT EXISTS
                  (SELECT 1 FROM [asap].[PublicationOption] WHERE [OrganizationId] = 2);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<PolarisPatronProvider> CreatePolarisProviderAsync(HttpMessageHandler handler)
    {
        var services = factory!.Services;
        var protector = services.GetRequiredService<IntegrationCredentialProtector>();
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE [asap].[PolarisSettings]
                SET [Host] = N'https://polaris.invalid',
                    [AccessId] = N'test-access',
                    [ProtectedApiKey] = @apiKey,
                    [StaffDomain] = N'TEST',
                    [AdminUser] = N'test-admin',
                    [ProtectedAdminPassword] = @password,
                    [WorkstationId] = 99,
                    [SystemPolarisUserId] = 42,
                    [OrganizationIdForRequests] = 7,
                    [PickupOrganizationId] = 101,
                    [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;
                """;
            command.Parameters.AddWithValue("@apiKey", protector.Protect("test-api-key"));
            command.Parameters.AddWithValue("@password", protector.Protect("test-password"));
            await command.ExecuteNonQueryAsync();
        }

        return new PolarisPatronProvider(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            protector,
            new SingleClientFactory(new HttpClient(handler)));
    }

    private async Task<SeededSensitiveOutbox> SeedSensitiveOutboxAsync(
        string key,
        string role = "staff",
        int staffOrganizationId = 2,
        int authorizationOrganizationId = 2,
        string addressKind = "notification_email",
        bool weeklyEnabled = false,
        string? notificationEmail = null,
        string? weeklyEmail = null,
        string? toAddress = null)
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var objectId = Guid.NewGuid();
        notificationEmail ??= $"outbox-{key}-{Guid.NewGuid():N}@example.org";
        toAddress ??= addressKind == "weekly_summary"
            ? weeklyEmail ?? notificationEmail
            : notificationEmail;
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = 2;
            DECLARE @staff TABLE ([Id] bigint);
            INSERT INTO [asap].[StaffUser]
                ([EntraTenantId], [EntraObjectId], [UserPrincipalName], [NormalizedUserPrincipalName],
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                 [WeeklyActionSummaryEnabled], [WeeklyActionSummaryEmail])
            OUTPUT inserted.[Id] INTO @staff
            VALUES
                (@tenantId, @objectId, @email, UPPER(@email), N'Outbox Staff', @email,
                 @role, @staffOrganizationId, 1, @weeklyEnabled, @weeklyEmail);

            INSERT INTO [asap].[EmailOutbox]
                ([OrganizationId], [BusinessKey], [DeliveryClass], [RecipientStaffUserId],
                 [RecipientAuthenticationEmail], [AuthorizationOrganizationId],
                 [RecipientAddressKind], [ToAddress], [FromAddress], [Subject], [BodyText],
                 [Status], [NextAttemptUtc], [CreatedUtc])
            OUTPUT inserted.[Id], inserted.[RecipientStaffUserId]
            SELECT @authorizationOrganizationId, @businessKey, N'staff_authorization_sensitive', [Id],
                   UPPER(@email), @authorizationOrganizationId, @addressKind,
                   @toAddress, N'asap@example.org', N'Sensitive update', N'Body',
                   N'pending', SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM @staff;
            """;
        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@objectId", objectId);
        command.Parameters.AddWithValue("@email", notificationEmail);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@staffOrganizationId", staffOrganizationId);
        command.Parameters.AddWithValue("@authorizationOrganizationId", authorizationOrganizationId);
        command.Parameters.AddWithValue("@addressKind", addressKind);
        command.Parameters.AddWithValue("@weeklyEnabled", weeklyEnabled);
        command.Parameters.AddWithValue("@weeklyEmail", (object?)weeklyEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("@toAddress", toAddress);
        command.Parameters.AddWithValue("@businessKey", $"sensitive-{key}:{Guid.NewGuid():N}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new SeededSensitiveOutbox(reader.GetInt64(0), reader.GetInt64(1), toAddress);
    }

    private EmailOutboxJobs CreateEmailOutboxJobs(
        IEmailSender sender,
        EmailOutboxRuntimeOptions options,
        List<string>? allowedDomains = null)
    {
        var configuration = TestConfigurationFactory.Create(
            allowedDomains: allowedDomains ?? ["example.org"]);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        return new EmailOutboxJobs(
            configuration,
            sender,
            new RecipientDomainPolicy(configuration),
            dispatcher!,
            options,
            NullLogger<EmailOutboxJobs>.Instance);
    }

    private PatronSuggestionService CreatePatronSuggestionService(
        List<string> allowedDomains,
        IEmailOutboxDispatcher outboxDispatcher,
        IEmailSender? emailSender = null)
    {
        var configuration = TestConfigurationFactory.Create(allowedDomains: allowedDomains);
        configuration.ConnectionStrings.AsapDatabase = databaseConnectionString;
        configuration.ConnectionStrings.HangfireDatabase = databaseConnectionString;
        return new PatronSuggestionService(
            configuration,
            factory!.Services.GetRequiredService<PatronConfigurationService>(),
            new DeterministicTestingPatronProvider(),
            outboxDispatcher,
            emailSender ?? new RecordingEmailSender(),
            new RecipientDomainPolicy(configuration),
            TimeProvider.System,
            NullLogger<PatronSuggestionService>.Instance);
    }

    private static PatronSuggestionInput Suggestion(string title) =>
        new(
            "book",
            title,
            "Test Author",
            null,
            "Coming soon",
            101,
            true,
            new Dictionary<string, string?>());

    private static async Task<long> FindSubmissionOutboxIdAsync(long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT [Id] FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey;";
        command.Parameters.AddWithValue("@businessKey", $"patron-submission:{requestId}");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string FrameAncestorsDirective(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var match = Regex.Match(policy, @"frame-ancestors ([^;]+);");
        Assert.IsTrue(match.Success, policy);
        return match.Groups[1].Value;
    }

    private static async Task<int> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<PatronCodeRows> ReadPatronCodeRowsAsync(int organizationId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId; " +
            "SELECT [PatronCodeId] FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId ORDER BY [PatronCodeId];";
        command.Parameters.AddWithValue("@organizationId", organizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        var setCount = reader.GetInt32(0);
        Assert.IsTrue(await reader.NextResultAsync());
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return new PatronCodeRows(setCount, values.ToArray());
    }

    private static async Task<JsonDocument> ReadSettingsDocumentAsync(HttpClient client, string organizationId)
    {
        using var response = await client.GetAsync($"/api/asap/staff/settings?orgId={Uri.EscapeDataString(organizationId)}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> SaveSettingsDocumentAsync(
        HttpClient client,
        JsonElement currentSettings,
        string organizationId,
        IDictionary<string, object?> values)
    {
        var payload = new Dictionary<string, object?>(values, StringComparer.Ordinal)
        {
            ["orgId"] = organizationId,
            ["version"] = currentSettings.GetProperty("version").GetString()
        };
        using var response = await client.PostAsJsonAsync("/api/asap/staff/settings", payload);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IReadOnlyList<ScalarSettingCase> BuildScalarSettingCases(string rejectionTemplateIdA, string rejectionTemplateIdB)
    {
        var fields = new List<ScalarSettingCase>();
        var index = 0;
        void AddText(string section, string name)
        {
            index++;
            fields.Add(new ScalarSettingCase(
                section,
                name,
                $"Slice 4 system {section} {name}",
                $"Slice 4 library {section} {name}"));
        }
        void AddBool(string section, string name)
        {
            index++;
            fields.Add(new ScalarSettingCase(section, name, index % 2 == 0, index % 2 != 0));
        }
        void AddInt(string section, string name)
        {
            index++;
            fields.Add(new ScalarSettingCase(section, name, 20 + index, 120 + index));
        }

        AddInt("workflow", "suggestionLimit");
        AddText("workflow", "suggestionLimitMessage");
        AddBool("workflow", "outstandingTimeoutEnabled");
        AddInt("workflow", "outstandingTimeoutDays");
        AddBool("workflow", "outstandingTimeoutSendEmail");
        fields.Add(new ScalarSettingCase("workflow", "outstandingTimeoutRejectionTemplateId", rejectionTemplateIdA, rejectionTemplateIdB));
        AddBool("workflow", "holdPickupTimeoutEnabled");
        AddInt("workflow", "holdPickupTimeoutDays");
        AddBool("workflow", "pendingHoldTimeoutEnabled");
        AddInt("workflow", "pendingHoldTimeoutDays");
        AddBool("workflow", "additionalCopyTimeoutEnabled");
        AddInt("workflow", "additionalCopyTimeoutDays");
        AddBool("workflow", "autoPromote");
        AddBool("workflow", "commonAuthorsEnabled");
        AddText("workflow", "commonAuthorsLabel");
        AddText("workflow", "commonAuthorsHelp");
        AddText("workflow", "commonAuthorsMessage");
        AddBool("workflow", "allowPatronAutoholdOptOut");
        AddBool("workflow", "allowAnyRegisteredCardLogin");
        AddBool("workflow", "patronCodeEligibilityEnabled");
        AddText("workflow", "patronCodeEligibilityMessage");

        foreach (var name in new[]
                 {
                     "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
                     "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
                     "eaudiobookMessage", "suggestionStatusLabel", "outstandingPurchaseStatusLabel",
                     "pendingHoldStatusLabel", "holdPlacedStatusLabel", "closedStatusLabel", "rejectedStatusLabel",
                     "holdCompletedStatusLabel", "holdNotPickedUpStatusLabel", "manualStatusLabel", "silentStatusLabel"
                 })
        {
            AddText("patron", name);
        }

        fields.Add(new ScalarSettingCase("email", "fromAddress", "slice4-system@example.org", "slice4-library@example.org"));
        fields.Add(new ScalarSettingCase("email", "fromName", "Slice 4 System Sender", "Slice 4 Library Sender"));
        return fields;
    }

    private static Dictionary<string, object?> ScalarPayload(
        IEnumerable<ScalarSettingCase> fields,
        Func<ScalarSettingCase, object?> valueFactory)
    {
        return fields
            .GroupBy(field => field.Section)
            .ToDictionary(
                group => group.Key,
                group => (object?)group.ToDictionary(
                    field => field.Name,
                    field => valueFactory(field),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> CaptureScalarRestorePayload(JsonElement settings)
    {
        var configuredSystem = settings.GetProperty("stored").GetProperty("configuredSystem");
        var fieldNames = BuildScalarSettingCases("0", "0")
            .Where(field => field.Name != "outstandingTimeoutRejectionTemplateId")
            .Select(field => field)
            .Append(new ScalarSettingCase("workflow", "outstandingTimeoutRejectionTemplateId", null, null))
            .ToArray();
        return ScalarPayload(fieldNames, field =>
            ReadJsonScalar(configuredSystem.GetProperty(field.Section), field.Name));
    }

    private static string FindTemplateId(JsonElement settings, string key)
    {
        var templates = settings.GetProperty("stored")
            .GetProperty("configuredSystem")
            .GetProperty("templates")
            .EnumerateArray();
        var template = templates.Single(item => item.GetProperty("templateKey").GetString() == key);
        return template.GetProperty("id").GetString()!;
    }

    private static object? ReadJsonScalar(JsonElement parent, string propertyName)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private static void AssertResolvedScalarEquals(
        object? expected,
        JsonElement settings,
        ScalarSettingCase field,
        string context)
    {
        var stored = settings.GetProperty("stored");
        var configuredSystem = stored.GetProperty("configuredSystem").GetProperty(field.Section);
        var libraryOverride = stored.GetProperty("libraryOverride");
        JsonElement librarySection = default;
        if (libraryOverride.ValueKind == JsonValueKind.Object &&
            libraryOverride.TryGetProperty(field.Section, out var candidate))
        {
            librarySection = candidate;
        }

        var actual = ReadJsonScalar(librarySection, field.Name) ??
                     ReadJsonScalar(configuredSystem, field.Name);
        AssertScalarValueEquals(expected, actual, context);
    }

    private static void AssertScalarEquals(object? expected, JsonElement parent, string propertyName, string context)
    {
        var actual = ReadJsonScalar(parent, propertyName);
        AssertScalarValueEquals(expected, actual, context);
    }

    private static void AssertScalarValueEquals(object? expected, object? actual, string context)
    {
        switch (expected)
        {
            case null:
                Assert.IsNull(actual, context);
                break;
            case bool expectedBool:
                Assert.IsInstanceOfType(actual, typeof(bool), context);
                Assert.AreEqual(expectedBool, (bool)actual, context);
                break;
            case int expectedInt:
                Assert.AreEqual(expectedInt, Convert.ToInt32(actual), context);
                break;
            default:
                Assert.AreEqual(expected.ToString(), actual?.ToString(), context);
                break;
        }
    }

    private static async Task UpsertTestOrganizationAsync(int organizationId, string name, string abbreviation)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF EXISTS (SELECT 1 FROM [asap].[Organization] WHERE [Id] = @id)
                UPDATE [asap].[Organization]
                SET [DisplayName] = @name, [Abbreviation] = @abbreviation, [IsActive] = 1
                WHERE [Id] = @id;
            ELSE
                INSERT INTO [asap].[Organization] ([Id], [DisplayName], [Abbreviation], [IsActive])
                VALUES (@id, @name, @abbreviation, 1);
            """;
        command.Parameters.AddWithValue("@id", organizationId);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@abbreviation", abbreviation);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupScalarSettingsTestDataAsync(int organizationId, string templateKeyA, string templateKeyB)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PublicationOption] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PublicationOptionSet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[CommonCreatorTerm] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[CommonCreatorSet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[ExternalSearchProviderOverride] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[MaterialFormatCustomFieldRule] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[FormatAutoClaimRule] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[MaterialFormatOverride] WHERE [LibraryOrganizationId] = @organizationId;
            DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[Branding] WHERE [OrganizationId] = @organizationId;
            DELETE FROM [asap].[Organization] WHERE [Id] = @organizationId;
            DELETE FROM [asap].[EmailTemplate] WHERE [OrganizationId] = 1 AND [TemplateKey] IN (@templateKeyA, @templateKeyB);
            """;
        command.Parameters.AddWithValue("@organizationId", organizationId);
        command.Parameters.AddWithValue("@templateKeyA", templateKeyA);
        command.Parameters.AddWithValue("@templateKeyB", templateKeyB);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForOutboxStatusAsync(
        long outboxId,
        string expectedStatus,
        TimeSpan timeout)
    {
        var expires = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < expires)
        {
            if ((await ReadOutboxStateAsync(outboxId)).Status == expectedStatus)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Outbox {outboxId} did not reach {expectedStatus} within {timeout}.");
    }

    private static async Task<EmailOutboxState> ReadOutboxStateAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT [Status], [LastErrorCode], [ProviderMessageId], [SuppressionReason] FROM [asap].[EmailOutbox] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", outboxId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new EmailOutboxState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task SetLeaseExpiryAsync(long outboxId, bool expired)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE [asap].[EmailOutbox]
            SET [LeaseExpiresUtc] =
                CASE WHEN @expired = 1 THEN DATEADD(millisecond, 1, [SendingStartedUtc])
                     ELSE DATEADD(second, 1, SYSUTCDATETIME()) END
            WHERE [Id] = @id AND [Status] = N'sending';
            IF @expired = 1 WAITFOR DELAY '00:00:00.010';
            """;
        command.Parameters.AddWithValue("@expired", expired);
        command.Parameters.AddWithValue("@id", outboxId);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class RecordingOutboxDispatcher : IEmailOutboxDispatcher
    {
        public List<long> EnqueuedIds { get; } = [];

        public void Enqueue(long outboxId) => EnqueuedIds.Add(outboxId);
    }

    private static async Task SetIncompleteOperationAsync(long requestId, bool present)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = present
            ? """
              INSERT INTO [asap].[HoldPlacementOperation]
                  ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [AttemptNumber], [State], [Phase],
                   [ExecutionEpoch], [RequestStartedUtc])
              VALUES (@requestId, N'20000000002104', N'9001', 1, N'ambiguous', N'create_started', 1, SYSUTCDATETIME());
              """
            : "DELETE FROM [asap].[HoldPlacementOperation] WHERE [TitleRequestId] = @requestId;";
        command.Parameters.AddWithValue("@requestId", requestId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int?> ReadPickupSnapshotAsync(long requestId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [PreferredPickupBranchId] FROM [asap].[TitleRequest] WHERE [Id] = @id;",
            connection);
        command.Parameters.AddWithValue("@id", requestId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private sealed record SeededSensitiveOutbox(long OutboxId, long StaffUserId, string ToAddress);

    private sealed record CompletedHoldIdentitySeed(
        long RequestId,
        long OperationId,
        byte[] RequestVersion,
        byte[] OperationVersion);

    private sealed record OperatorResolutionSeed(
        long RequestId,
        long OperationId,
        string RequestVersion,
        string OperationVersion);

    private sealed record StaffMetadataRaceSeed(
        long ActorId,
        Guid TenantId,
        Guid ActorObjectId,
        byte[] ActorVersion,
        long TargetId,
        byte[] TargetVersion,
        string OriginalTargetDisplayName);

    private sealed record LifecycleClaimRaceSeed(
        long StaffId,
        long RequestId,
        byte[] StaffVersion,
        byte[] RequestVersion);

    private sealed record LifecycleAutoClaimRaceSeed(
        long StaffId,
        long RuleId,
        byte[] StaffVersion);

    private sealed record AdditionalCopyLegacySourceSeed(
        long SourceRequestId,
        long CurrentClaimantId,
        long HistoricalRuleId,
        byte[] SourceVersion);

    private sealed record AdditionalCopyRaceSeed(
        long StaffId,
        long TaskId,
        byte[] StaffVersion,
        byte[] TaskVersion);

    private sealed record AdditionalCopyCandidateChangeSeed(
        long TaskId,
        long SecondStaffId,
        byte[] TaskVersion);

    private sealed record EmailOutboxState(
        string Status,
        string? LastErrorCode,
        string? ProviderMessageId,
        string? SuppressionReason);

    private sealed record PatronCodeRows(int SetCount, string[] Values);

    private sealed record ScalarSettingCase(string Section, string Name, object? SystemValue, object? LibraryValue);

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailEnvelope> Envelopes { get; } = [];

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.Configured);

        public Task<EmailSendResult> SendAsync(
            EmailEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return Task.FromResult(new EmailSendResult("test-message"));
        }
    }

    private sealed class FailingPatronCodeReferenceProvider : IPolarisReferenceProvider
    {
        public Task<PolarisConnectionTestResult> TestConnectionAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(false, 0, "testing_patron_codes_failure"));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>([]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken) =>
            throw new PolarisOperationalException(
                "testing_patron_codes_failure",
                "Testing patron-code reference failure.");
    }

    private sealed class DisallowedPreferredPickupPatronProvider : IPatronProvider
    {
        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PatronSnapshot(
                35,
                barcode,
                "patron@example.org",
                "Test",
                "Patron",
                "1",
                null,
                300,
                2,
                "Test Library",
                300));

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken) =>
            AuthenticateAsync(barcode, string.Empty, cancellationToken);

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(200, "Allowed Branch")]);

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));
    }

    private sealed class ControllablePickupPatronProvider : IPatronProvider
    {
        public int CurrentPickupBranchId { get; private set; } = 101;
        public int UpdateCount { get; private set; }
        public bool FailUpdate { get; set; }

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => RefreshAsync(barcode, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PatronSnapshot(
                7004,
                barcode,
                "pickup@example.org",
                "Pickup",
                "Patron",
                "1",
                "Adult",
                101,
                2,
                "Test Library",
                CurrentPickupBranchId));
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PickupBranch>>(
                [new PickupBranch(101, "Main Library"), new PickupBranch(102, "North Branch")]);
        }

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCount++;
            if (FailUpdate)
            {
                throw new PolarisOperationalException("testing_pickup_failure", "Testing pickup failure.");
            }
            CurrentPickupBranchId = pickupBranchId;
            return Task.CompletedTask;
        }

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));
    }

    private sealed class ScriptedHoldProvider : IPatronProvider, IStaffPolarisProvider
    {
        private HoldProviderResult createResult;
        private HoldProviderResult replyResult;

        private ScriptedHoldProvider(HoldProviderResult createResult, HoldProviderResult replyResult)
        {
            this.createResult = createResult;
            this.replyResult = replyResult;
        }

        public Guid RequestGuid { get; } = Guid.NewGuid();
        public int CreateCount { get; private set; }
        public int ReplyCount { get; private set; }
        public int HoldReadCount { get; private set; }
        public IReadOnlyList<PolarisHoldSnapshot> Holds { get; set; } = [];
        public Exception? CreateException { get; set; }
        public Exception? ReplyException { get; set; }
        public PolarisHoldSnapshot? HoldAfterCreate { get; set; }
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HoldProviderResult>? PendingCreate { get; private set; }
        public TaskCompletionSource ReplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HoldProviderResult>? PendingReply { get; private set; }

        public static ScriptedHoldProvider ReplyRequiredThenSuccess()
        {
            var provider = new ScriptedHoldProvider(
                new HoldProviderResult(
                    HoldProviderOutcome.ReplyRequired,
                    null,
                    null,
                    "group-qualifier",
                    "transaction-qualifier",
                    3,
                    5,
                    "create_status_5_reply_required"),
                new HoldProviderResult(
                    HoldProviderOutcome.FinalSuccess,
                    null,
                    "8123",
                    "group-qualifier",
                    "transaction-qualifier",
                    2,
                    0,
                    "documented_reply_success"));
            provider.createResult = provider.createResult with { RequestGuid = provider.RequestGuid.ToString() };
            provider.replyResult = provider.replyResult with { RequestGuid = provider.RequestGuid.ToString() };
            return provider;
        }

        public static ScriptedHoldProvider AmbiguousCreate() => new(
            new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                null,
                null,
                null,
                null,
                null,
                null,
                "create_transport_ambiguous",
                "provider_transport_error"),
            new HoldProviderResult(
                HoldProviderOutcome.Ambiguous,
                null,
                null,
                null,
                null,
                null,
                null,
                "reply_transport_ambiguous",
                "provider_transport_error"));

        public void BlockCreate() => PendingCreate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedCreate(HoldProviderResult result) =>
            PendingCreate!.TrySetResult(result);

        public void BlockReply() => PendingReply = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedReply(HoldProviderResult result) =>
            PendingReply!.TrySetResult(result);

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => RefreshAsync(barcode, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PatronSnapshot(
                7105,
                barcode,
                "hold-patron@example.org",
                "Hold",
                "Patron",
                "1",
                "Adult",
                101,
                2,
                "Test Library",
                101));
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(101, "Main Library")]);

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));

        public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken) =>
            Task.FromResult(new BibValidationResult(true));

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HoldReadCount++;
            return Task.FromResult(Holds);
        }

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            if (CreateException is not null) throw CreateException;
            if (HoldAfterCreate is not null) Holds = [HoldAfterCreate];
            if (PendingCreate is not null)
            {
                CreateStarted.TrySetResult();
                return PendingCreate.Task;
            }
            return Task.FromResult(createResult);
        }

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplyCount++;
            if (ReplyException is not null) throw ReplyException;
            Assert.AreEqual(RequestGuid, command.RequestGuid);
            Assert.AreEqual("group-qualifier", command.TxnGroupQualifier);
            Assert.AreEqual("transaction-qualifier", command.TxnQualifier);
            if (PendingReply is not null)
            {
                ReplyStarted.TrySetResult();
                return PendingReply.Task;
            }
            return Task.FromResult(replyResult);
        }
    }

    private sealed class RejectingBibStaffProvider : IStaffPolarisProvider
    {
        public int ValidationCount { get; private set; }

        public Task<BibValidationResult> ValidateBibAsync(int bibId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidationCount++;
            return Task.FromResult(new BibValidationResult(false));
        }

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HoldProviderResult> CreateHoldAsync(
            HoldCreateCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HoldProviderResult> ReplyToHoldAsync(
            HoldReplyCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MutableReadinessEmailSender(bool isConfigured) : IEmailSender
    {
        public bool IsConfigured { get; set; } = isConfigured;

        public int SendCount { get; private set; }

        public bool ReturnNotConfiguredOnSend { get; init; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                IsConfigured
                    ? EmailTransportReadiness.Configured
                    : EmailTransportReadiness.NotConfigured);
        }

        public Task<EmailSendResult> SendAsync(
            EmailEnvelope envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            return Task.FromResult(
                ReturnNotConfiguredOnSend
                    ? EmailSendResult.NotConfigured
                    : new EmailSendResult("test-message"));
        }
    }

    private sealed class CancellationAwareTimeoutEmailSender : IEmailSender
    {
        public int CallCount { get; private set; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.Configured);

        public async Task<EmailSendResult> SendAsync(
            EmailEnvelope envelope,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-aware sender should not complete normally.");
        }
    }

    private sealed class FencedCompletionEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource<EmailSendResult> firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(EmailTransportReadiness.Configured);

        public Task<EmailSendResult> SendAsync(
            EmailEnvelope envelope,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1)
            {
                FirstCallStarted.TrySetResult();
                return firstCompletion.Task;
            }

            return Task.FromResult(new EmailSendResult("new-owner-message"));
        }

        public void CompleteFirstCall(EmailSendResult? result = null) =>
            firstCompletion.TrySetResult(result ?? new EmailSendResult("stale-owner-message"));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
        }
    }

    private sealed class SequenceResponseHandler(
        params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Content)> remaining = new(responses);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (!remaining.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No fake Polaris response remains for this request.");
            }

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Content),
                RequestMessage = request
            });
        }
    }

    private sealed class ProtectedUpdateResponseHandler(string updateContent) : HttpMessageHandler
    {
        private const string ProtectedTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";

        public int UpdateRequestCount { get; private set; }
        public string UpdateRequestPath { get; private set; } = string.Empty;
        public string UpdateRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isAuthentication = request.RequestUri!.AbsolutePath.Contains(
                "/authenticator/staff",
                StringComparison.OrdinalIgnoreCase);
            if (!isAuthentication)
            {
                UpdateRequestCount++;
                UpdateRequestPath = request.RequestUri.AbsolutePath;
                UpdateRequestBody = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isAuthentication ? ProtectedTokenContent : updateContent),
                RequestMessage = request
            };
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}

internal static class TestArtifactPaths
{
    public static string FindDacpac()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var path = FindRepositoryFile(
            "database", "Asap.Database", "bin", configuration, "Asap.Database.dacpac");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Build the DACPAC before running SQL tests.", path);
    }

    public static string FindRepositoryFile(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Asap.sln")))
        {
            root = root.Parent;
        }

        return root is null
            ? throw new FileNotFoundException("Could not locate Asap.sln from the test output directory.")
            : Path.Combine([root.FullName, .. segments]);
    }
}
