using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data;
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
using Microsoft.Extensions.Logging;
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
                services.RemoveAll<DeterministicTestingPatronProvider>();
                services.AddSingleton(DeterministicJourneyScenario.Create());
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
        // Failed external writes intentionally leave durable evidence. Each test
        // owns its journal rows; clear them after all assertions, including recovery.
        await ExecuteNonQueryAsync("DELETE FROM [asap].[PickupPreferenceOperation];");
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
        var requestIdElement = submissionDocument.RootElement.GetProperty("id");
        Assert.AreEqual(JsonValueKind.String, requestIdElement.ValueKind);
        var requestIdText = requestIdElement.GetString();
        Assert.IsNotNull(requestIdText);
        Assert.IsTrue(long.TryParse(requestIdText, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var requestId));
        Assert.AreEqual(requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), requestIdText);
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
            Assert.AreEqual(9001, reader.GetInt32(4));
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
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var snapshot = await contextFactory.CreateDbContextAsync();
        var originalSettings = await snapshot.SystemSettings.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .Select(item => new { item.LeapBibUrlPattern, item.LeapPatronUrlPattern, item.StaffApplicationUrl })
            .SingleAsync();
        var originalBranding = await snapshot.Branding.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .Select(item => new { item.LogoAltText })
            .SingleOrDefaultAsync();
        var originalProviders = await snapshot.ExternalSearchProviders.AsNoTracking()
            .Where(item => item.ProviderKey == "external_search_1" || item.ProviderKey == "external_search_3")
            .Select(item => new { item.Id, item.Label, item.UrlTemplate, item.IsEnabled })
            .ToListAsync();
        var provider2Id = await snapshot.ExternalSearchProviders.AsNoTracking()
            .Where(item => item.ProviderKey == "external_search_2")
            .Select(item => item.Id)
            .SingleAsync();
        var originalProvider2Override = await snapshot.ExternalSearchProviderOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == 2 && item.ExternalSearchProviderId == provider2Id)
            .Select(item => new { item.IsEnabled, item.Label, item.UrlTemplate })
            .SingleOrDefaultAsync();
        try
        {
        using var client = factory.CreateClient();
        var baseAddress = client.BaseAddress
            ?? throw new InvalidOperationException("The Kestrel test host did not expose a base address.");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var staffObjectId = Guid.NewGuid();
        var seeded = await SeedStaffBrowserStateAsync(
            Guid.Parse(identity.TenantId!),
            Guid.Parse(identity.ObjectId!),
            staffObjectId);
        await using (var linkContext = await contextFactory.CreateDbContextAsync())
        {
            var systemLinks = await linkContext.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            systemLinks.StaffApplicationUrl = $"{baseAddress.GetLeftPart(UriPartial.Authority)}/staff/";
            var systemBranding = await linkContext.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (systemBranding is null)
            {
                systemBranding = new Branding { OrganizationId = 1 };
                linkContext.Branding.Add(systemBranding);
            }
            systemBranding.LogoAltText = "Browser saved system alt";
            await linkContext.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", "browser.staff@example.org");
        using var researchSession = await client.GetAsync("/api/asap/staff/session");
        using var researchSessionBody = JsonDocument.Parse(await researchSession.Content.ReadAsStringAsync());
        var researchToken = researchSessionBody.RootElement.GetProperty("antiforgeryToken").GetString();
        using var noToken = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
            new { requestId = seeded.PrimaryRequestId.ToString(), mode = "title", query = "catalog" });
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", researchToken);
        using var foreignLookup = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
            new { requestId = seeded.OtherRequestId.ToString(), mode = "title", query = "catalog" });
        Assert.AreEqual(HttpStatusCode.NotFound, foreignLookup.StatusCode);
        foreach (var requestId in new[] { "0", "-1", "not-a-number", "9223372036854775808" })
        {
            using var invalidRequestId = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
                new { requestId, libraryOrgId = 2, mode = "title", query = "catalog" });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidRequestId.StatusCode, requestId);
        }
        using var exactBigintLookup = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
            new { requestId = "9007199254741993", mode = "title", query = "Browser staff title" });
        Assert.AreEqual(HttpStatusCode.OK, exactBigintLookup.StatusCode,
            await exactBigintLookup.Content.ReadAsStringAsync());
        using var mismatchedRequestLibrary = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
            new { requestId = "9007199254741993", libraryOrgId = 82, mode = "title", query = "catalog" });
        Assert.AreEqual(HttpStatusCode.BadRequest, mismatchedRequestLibrary.StatusCode);
        using var mismatchedRequestLibraryBody = JsonDocument.Parse(
            await mismatchedRequestLibrary.Content.ReadAsStringAsync());
        Assert.AreEqual("library_scope_mismatch",
            mismatchedRequestLibraryBody.RootElement.GetProperty("code").GetString());
        using var foreignLibrarySearch = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup",
            new { libraryOrgId = 82, mode = "title", query = "catalog" });
        Assert.AreEqual(HttpStatusCode.Forbidden, foreignLibrarySearch.StatusCode);
        using var foreignResearch = await client.GetAsync(
            $"/api/asap/staff/research-configuration?requestId={seeded.OtherRequestId}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreignResearch.StatusCode);

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
            Assert.HasCount(58, report.RootElement.GetProperty("states").EnumerateArray().ToArray());
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
                   JSON_VALUE(r.[CustomFieldsJson], '$.retired.value'), r.[BibId]
            FROM [asap].[TitleRequest] r
            JOIN [asap].[StaffUser] s ON s.[Id] = @superId
            WHERE r.[Id] = @requestId;
            """;
        command.Parameters.AddWithValue("@requestId", seeded.PrimaryRequestId);
        command.Parameters.AddWithValue("@superId", seeded.SuperId);
        await using var verified = await command.ExecuteReaderAsync();
        Assert.IsTrue(await verified.ReadAsync());
        Assert.AreEqual("Catalog title 9001 (Browser staff title edited)", verified.GetString(0));
        Assert.AreEqual("closed", verified.GetString(1));
        Assert.AreEqual(seeded.SuperId, verified.GetInt64(2));
        Assert.IsTrue(verified.GetBoolean(3));
        Assert.AreEqual("browser-weekly@example.org", verified.GetString(4));
        Assert.IsTrue(verified.GetBoolean(5));
        Assert.AreEqual(2, verified.GetInt32(6));
        Assert.AreEqual("Library backlist", verified.GetString(7));
        Assert.AreEqual("Edited audience", verified.GetString(8));
        Assert.AreEqual("hardback", verified.GetString(9));
        Assert.AreEqual("Keep me", verified.GetString(10));
        Assert.AreEqual(9001, verified.GetInt32(11));
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
        Assert.AreEqual(8456, resolved.GetInt32(4));
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
                   AND COALESCE([Notes], N'') NOT LIKE N'%Additional copy request created for BIB 92905.%'),
                (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                 WHERE [TitleRequestId]=@copySourceId
                   AND [EventType]=N'additional_copy_created'
                   AND [Message] LIKE N'%BIB 92905%'),
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
        Assert.AreEqual(1, copyState.GetInt32(5));
        Assert.AreEqual(0, copyState.GetInt32(6));
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var settings = await restore.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            settings.LeapBibUrlPattern = originalSettings.LeapBibUrlPattern;
            settings.LeapPatronUrlPattern = originalSettings.LeapPatronUrlPattern;
            settings.StaffApplicationUrl = originalSettings.StaffApplicationUrl;
            var branding = await restore.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (originalBranding is null && branding is not null)
            {
                restore.Branding.Remove(branding);
            }
            else if (originalBranding is not null && branding is not null)
            {
                branding.LogoAltText = originalBranding.LogoAltText;
            }
            foreach (var original in originalProviders)
            {
                var provider = await restore.ExternalSearchProviders.SingleAsync(item => item.Id == original.Id);
                provider.Label = original.Label;
                provider.UrlTemplate = original.UrlTemplate;
                provider.IsEnabled = original.IsEnabled;
            }
            var provider2Override = await restore.ExternalSearchProviderOverrides.SingleOrDefaultAsync(item =>
                item.LibraryOrganizationId == 2 && item.ExternalSearchProviderId == provider2Id);
            if (originalProvider2Override is null && provider2Override is not null)
            {
                restore.ExternalSearchProviderOverrides.Remove(provider2Override);
            }
            else if (originalProvider2Override is not null && provider2Override is not null)
            {
                provider2Override.IsEnabled = originalProvider2Override.IsEnabled;
                provider2Override.Label = originalProvider2Override.Label;
                provider2Override.UrlTemplate = originalProvider2Override.UrlTemplate;
            }
            await restore.SaveChangesAsync();
        }
    }

    [TestMethod]
    public async Task SystemBrandingAltClearReturnsEffectiveFallback()
    {
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var branding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
        var originalAlt = branding?.LogoAltText;
        var created = branding is null;
        branding ??= new Branding { OrganizationId = 1 };
        try
        {
            if (created)
            {
                context.Branding.Add(branding);
            }
            branding.LogoAltText = "Saved system alternate text";
            await context.SaveChangesAsync();

            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var before = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual("Saved system alternate text", before.RootElement.GetProperty("effective")
                .GetProperty("logoAltText").GetString());
            using var saved = await SaveSettingsDocumentAsync(client, before.RootElement, "system",
                new Dictionary<string, object?> { ["branding"] = new { altText = (string?)null } });
            using var after = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual(JsonValueKind.Null, after.RootElement.GetProperty("stored")
                .GetProperty("configuredSystem").GetProperty("branding").GetProperty("altText").ValueKind);
            Assert.AreEqual("Library Logo", after.RootElement.GetProperty("effective")
                .GetProperty("logoAltText").GetString());
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var restoredBranding = await restore.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 1);
            if (created && restoredBranding is not null)
            {
                restore.Branding.Remove(restoredBranding);
            }
            else if (restoredBranding is not null)
            {
                restoredBranding.LogoAltText = originalAlt;
            }
            await restore.SaveChangesAsync();
        }
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
                    (1, N'https://*.ported.example.org:8443', N'https://*.ported.example.org:8443', SYSUTCDATETIME()),
                    (1, N'https://*.legacy.example.org:443', N'https://*.legacy.example.org:443', SYSUTCDATETIME());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory!.CreateClient();
            const string storedDirective =
                "'self' https://*.legacy.example.org:443 https://*.plain.example.org https://*.ported.example.org:8443";
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
                },
                new
                {
                    Referer = "https://child.legacy.example.org:443/path",
                    Expected = $"{storedDirective} https://child.legacy.example.org:443"
                },
                new
                {
                    Referer = "https://child.legacy.example.org/path",
                    Expected = storedDirective
                },
                new
                {
                    Referer = "https://child.legacy.example.org:444/path",
                    Expected = storedDirective
                }
            };

            foreach (var item in cases)
            {
                if (item.Referer == "https://child.legacy.example.org:443/path")
                {
                    var context = await factory.Server.SendAsync(context =>
                    {
                        context.Request.Method = "GET";
                        context.Request.Path = "/patron/";
                        context.Request.Headers["Referer"] = item.Referer;
                    });
                    Assert.AreEqual((int)HttpStatusCode.OK, context.Response.StatusCode);
                    var policy = context.Response.Headers["Content-Security-Policy"].ToString();
                    var directive = Regex.Match(policy, @"frame-ancestors ([^;]+);");
                    Assert.IsTrue(directive.Success, policy);
                    Assert.AreEqual(item.Expected, directive.Groups[1].Value, item.Referer);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/patron/");
                    Assert.IsTrue(request.Headers.TryAddWithoutValidation("Referer", item.Referer));
                    using var response = await client.SendAsync(request);
                    Assert.AreEqual(item.Expected, FrameAncestorsDirective(response), item.Referer);
                }
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
            .Select(item => item.GetInt32())
            .ToArray();
        var systemEffectiveCodes = initial.GetProperty("effective").GetProperty("allowedPatronCodeIds")
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .ToArray();

        try
        {
            using var choicesResponse = await client.GetAsync("/api/asap/staff/polaris/patron-codes?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, choicesResponse.StatusCode, await choicesResponse.Content.ReadAsStringAsync());
            using var choicesDocument = JsonDocument.Parse(await choicesResponse.Content.ReadAsStringAsync());
            var choices = choicesDocument.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.IsTrue(choices.Any(item => item.GetProperty("id").GetInt32() == 1));
            Assert.IsTrue(choices.All(item => item.GetProperty("id").ValueKind == JsonValueKind.Number));

            using var saveResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = initial.GetProperty("version").GetString(),
                    workflow = new
                    {
                        suggestionLimitMessage = "Library-specific limit message",
                        allowedPatronCodeIds = new[] { 1 }
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
                new[] { 1 },
                saved.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                    .GetProperty("values").EnumerateArray().Select(item => item.GetInt32()).ToArray());

            using var invalidResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = saved.GetProperty("version").GetString(),
                    workflow = new { allowedPatronCodeIds = new[] { 999 } }
                });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode, await invalidResponse.Content.ReadAsStringAsync());
            using var invalidDocument = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("patron_code_unknown", invalidDocument.RootElement.GetProperty("code").GetString());

            using var afterInvalidResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, afterInvalidResponse.StatusCode, await afterInvalidResponse.Content.ReadAsStringAsync());
            using var afterInvalidDocument = JsonDocument.Parse(await afterInvalidResponse.Content.ReadAsStringAsync());
            var afterInvalid = afterInvalidDocument.RootElement;
            CollectionAssert.AreEqual(
                new[] { 1 },
                afterInvalid.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                    .GetProperty("values").EnumerateArray().Select(item => item.GetInt32()).ToArray());

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
                    workflow = new { allowedPatronCodeIds = Array.Empty<int>() }
                });
            Assert.AreEqual(HttpStatusCode.OK, clearSetResponse.StatusCode, await clearSetResponse.Content.ReadAsStringAsync());

            using var finalResponse = await client.GetAsync("/api/asap/staff/settings?orgId=2");
            Assert.AreEqual(HttpStatusCode.OK, finalResponse.StatusCode, await finalResponse.Content.ReadAsStringAsync());
            using var finalDocument = JsonDocument.Parse(await finalResponse.Content.ReadAsStringAsync());
            var final = finalDocument.RootElement;
            Assert.IsTrue(final.GetProperty("stored").GetProperty("libraryOverride").GetProperty("allowedPatronCodeIds")
                .GetProperty("exists").GetBoolean());
            CollectionAssert.AreEqual(
                Array.Empty<int>(),
                final.GetProperty("effective").GetProperty("allowedPatronCodeIds")
                    .EnumerateArray().Select(item => item.GetInt32()).ToArray());

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var verify = connection.CreateCommand();
            verify.CommandText =
                "SELECT (SELECT COUNT(*) FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = 2), " +
                "(SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 2);";
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            Assert.AreEqual(0, reader.GetInt32(1));
            await reader.DisposeAsync();

            using var inheritResponse = await client.PostAsJsonAsync(
                "/api/asap/staff/settings",
                new
                {
                    orgId = "2",
                    version = final.GetProperty("version").GetString(),
                    workflow = new { allowedPatronCodeIds = (int[]?)null }
                });
            Assert.AreEqual(HttpStatusCode.OK, inheritResponse.StatusCode, await inheritResponse.Content.ReadAsStringAsync());
            using var inherited = await ReadSettingsDocumentAsync(client, "2");
            Assert.IsFalse(inherited.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            CollectionAssert.AreEqual(systemEffectiveCodes,
                inherited.RootElement.GetProperty("effective").GetProperty("allowedPatronCodeIds")
                    .EnumerateArray().Select(item => item.GetInt32()).ToArray());
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
                            allowedPatronCodeIds = originalCodesExist ? originalCodeValues : null
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

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var legacy = connection.CreateCommand();
                legacy.CommandText = "UPDATE [asap].[EmailSettings] SET [ProtectedServerToken] = N'legacy-library-token' WHERE [OrganizationId] = @organizationId";
                legacy.Parameters.AddWithValue("@organizationId", libraryId);
                Assert.AreEqual(1, await legacy.ExecuteNonQueryAsync());
            }

            using var secretBase = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.IsFalse(secretBase.RootElement.GetRawText().Contains("legacy-library-token", StringComparison.Ordinal));
            foreach (var mutation in new object[]
            {
                new { postmarkToken = "forged-library-token" },
                new { clearPostmarkToken = true },
                new { serverToken = "forged-legacy-alias" },
                new { clearServerToken = true }
            })
            {
                using var forged = await client.PostAsJsonAsync("/api/asap/staff/settings", new
                {
                    orgId = libraryId.ToString(),
                    version = secretBase.RootElement.GetProperty("version").GetString(),
                    email = mutation
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
                using var rejected = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());
                Assert.AreEqual("postmark_token_system_only", rejected.RootElement.GetProperty("code").GetString());
            }

            using (var saveSender = await SaveSettingsDocumentAsync(client, secretBase.RootElement,
                       libraryId.ToString(), new Dictionary<string, object?>
                       {
                           ["email"] = new Dictionary<string, object?>
                           {
                               ["fromAddress"] = "slice4-secret-sentinel@example.org",
                               ["fromName"] = "Library sender"
                           }
                       }))
            {
                Assert.AreEqual("saved", saveSender.RootElement.GetProperty("code").GetString());
            }

            using var afterSenderSave = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var emailOverride = afterSenderSave.RootElement.GetProperty("stored")
                .GetProperty("libraryOverride").GetProperty("email");
            AssertScalarEquals(false, emailOverride, "hasPostmarkToken", "legacy library token is not offered as an override");
            AssertScalarEquals("slice4-secret-sentinel@example.org", emailOverride, "fromAddress", "sender address override");
            AssertScalarEquals("Library sender", emailOverride, "fromName", "sender name override");
            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var verify = connection.CreateCommand();
                verify.CommandText = "SELECT [ProtectedServerToken] FROM [asap].[EmailSettings] WHERE [OrganizationId] = @organizationId";
                verify.Parameters.AddWithValue("@organizationId", libraryId);
                Assert.AreEqual("legacy-library-token", await verify.ExecuteScalarAsync());
            }
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
    public async Task ReferencedCustomRejectionTemplateCannotBeDeletedUntilWorkflowReferenceIsSavedAway()
    {
        using var client = factory!.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
        const int libraryId = 91405;
        var key = $"rejection:dependency_{Guid.NewGuid():N}";
        await UpsertTestOrganizationAsync(libraryId, "Template Dependency Library", "TDL");
        try
        {
            using var initial = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using var created = await SaveSettingsDocumentAsync(client, initial.RootElement,
                libraryId.ToString(), new Dictionary<string, object?>
                {
                    ["templates"] = new object[]
                    {
                        new { templateKey = key, isCustom = true, displayName = "Dependency test",
                            subject = "Subject", body = "Body", enabled = true }
                    }
                });
            Assert.AreEqual("saved", created.RootElement.GetProperty("code").GetString());

            using var withTemplate = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var templateId = withTemplate.RootElement.GetProperty("stored").GetProperty("templates")
                .EnumerateArray().Single(item => item.GetProperty("templateKey").GetString() == key)
                .GetProperty("id").GetString();
            using var referenced = await SaveSettingsDocumentAsync(client, withTemplate.RootElement,
                libraryId.ToString(), new Dictionary<string, object?>
                {
                    ["workflow"] = new { outstandingTimeoutRejectionTemplateId = templateId }
                });
            Assert.AreEqual("saved", referenced.RootElement.GetProperty("code").GetString());

            using var current = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using var blocked = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = current.RootElement.GetProperty("version").GetString(),
                templates = new[] { new { templateKey = key, isCustom = true, reset = true } }
            });
            Assert.AreEqual(HttpStatusCode.Conflict, blocked.StatusCode);
            using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
            Assert.AreEqual("template_referenced", blockedBody.RootElement.GetProperty("code").GetString());

            using var legacyBlocked = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = current.RootElement.GetProperty("version").GetString(),
                emails = new { rejection_templates = new[] { new { templateKey = key, isCustom = true, reset = true } } }
            });
            Assert.AreEqual(HttpStatusCode.Conflict, legacyBlocked.StatusCode);
            using var legacyBlockedBody = JsonDocument.Parse(await legacyBlocked.Content.ReadAsStringAsync());
            Assert.AreEqual("template_referenced", legacyBlockedBody.RootElement.GetProperty("code").GetString());

            using var namedLegacyBlocked = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = current.RootElement.GetProperty("version").GetString(),
                emails = new Dictionary<string, object?>
                {
                    [key] = new { libraryCustom = true, reset = true }
                }
            });
            Assert.AreEqual(HttpStatusCode.Conflict, namedLegacyBlocked.StatusCode);
            using var namedLegacyBlockedBody = JsonDocument.Parse(await namedLegacyBlocked.Content.ReadAsStringAsync());
            Assert.AreEqual("template_referenced", namedLegacyBlockedBody.RootElement.GetProperty("code").GetString());

            using var stillReferenced = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.AreEqual(templateId, stillReferenced.RootElement.GetProperty("stored")
                .GetProperty("libraryOverride").GetProperty("workflow")
                .GetProperty("outstandingTimeoutRejectionTemplateId").GetString());
            Assert.IsTrue(stillReferenced.RootElement.GetProperty("stored").GetProperty("templates")
                .EnumerateArray().Any(item => item.GetProperty("templateKey").GetString() == key));

            using var changedWorkflow = await SaveSettingsDocumentAsync(client, stillReferenced.RootElement,
                libraryId.ToString(), new Dictionary<string, object?>
                {
                    ["workflow"] = new { outstandingTimeoutRejectionTemplateId = (string?)null }
                });
            using var withoutReference = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using var deleted = await SaveSettingsDocumentAsync(client, withoutReference.RootElement,
                libraryId.ToString(), new Dictionary<string, object?>
                {
                    ["templates"] = new object[] { new { templateKey = key, isCustom = true, reset = true } }
                });
            Assert.AreEqual("saved", deleted.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await CleanupScalarSettingsTestDataAsync(libraryId, key, key);
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
    public async Task PatronCodeReferenceLookupRunsBeforeSettingsLocks()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new LockProbePatronCodeReferenceProvider(databaseConnectionString);
        await using var probeFactory = CreateApplicationFactory(configurationPath, provider);
        using var client = probeFactory.CreateClient();
        AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

        using var response = await client.PostAsJsonAsync("/api/asap/staff/settings", new
        {
            orgId = "2",
            version = Convert.ToBase64String(new byte[32]),
            workflow = new { allowedPatronCodeIds = new[] { "1" } }
        });
        Assert.IsTrue(provider.LockProbeSucceeded, "Polaris lookup must happen before actor and organization locks.");
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("stale_version", body.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task LibraryEmptyPatronCodeOverrideRemainsDistinctFromInheritance()
    {
        const int libraryId = 8830;
        bool addedSystemCode;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (8830, N'Empty Code Override Library', N'ECL', 2, 1, 1);
                SELECT COUNT(*) FROM [asap].[PatronCodeEligibilityMember]
                WHERE [OrganizationId] = 1 AND [PatronCodeId] = N'1';
                """;
            addedSystemCode = Convert.ToInt32(await seed.ExecuteScalarAsync()) == 0;
            if (addedSystemCode)
            {
                seed.CommandText =
                    "INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId]) VALUES (1, N'1');";
                await seed.ExecuteNonQueryAsync();
            }
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            using var system = await ReadSettingsDocumentAsync(client, "system");
            using var invalidSystemInheritance = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = "system",
                version = system.RootElement.GetProperty("version").GetString(),
                workflow = new { allowedPatronCodeIds = (string[]?)null }
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidSystemInheritance.StatusCode,
                await invalidSystemInheritance.Content.ReadAsStringAsync());
            using var initial = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            using var emptySave = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = initial.RootElement.GetProperty("version").GetString(),
                workflow = new { patronCodeEligibilityEnabled = true, allowedPatronCodeIds = Array.Empty<string>() }
            });
            Assert.AreEqual(HttpStatusCode.OK, emptySave.StatusCode, await emptySave.Content.ReadAsStringAsync());
            using var empty = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.IsTrue(empty.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            Assert.IsTrue(empty.RootElement.GetProperty("effective").GetProperty("patronCodeEligibilityEnabled").GetBoolean());
            Assert.AreEqual(0, empty.RootElement.GetProperty("effective").GetProperty("allowedPatronCodeIds").GetArrayLength());

            using var reset = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = empty.RootElement.GetProperty("version").GetString(),
                workflow = new { allowedPatronCodeIds = (string[]?)null }
            });
            Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode, await reset.Content.ReadAsStringAsync());
            using var inherited = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.IsFalse(inherited.RootElement.GetProperty("stored").GetProperty("libraryOverride")
                .GetProperty("allowedPatronCodeIds").GetProperty("exists").GetBoolean());
            Assert.IsTrue(inherited.RootElement.GetProperty("effective").GetProperty("allowedPatronCodeIds")
                .EnumerateArray().Any(item => item.GetInt32() == 1));
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText =
                """
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] = 8830;
                DELETE FROM [asap].[Organization] WHERE [Id] = 8830;
                """ + (addedSystemCode
                    ? "DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] = 1 AND [PatronCodeId] = N'1';"
                    : string.Empty);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task SettingsPublicLinksIgnoreRequestHostAndHistoricalCodesStayScoped()
    {
        const int libraryId = 8828;
        string? previousStaffUrl;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                "SELECT [StaffApplicationUrl] FROM [asap].[SystemSettings] WHERE [OrganizationId] = 1;";
            previousStaffUrl = (await seed.ExecuteScalarAsync()) as string;
            seed.CommandText =
                """
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (8828, N'Patron Link Library', N'PLL', 2, 1, 1),
                       (8829, N'Foreign Code Library', N'FCL', 2, 1, 1);
                INSERT INTO [asap].[PatronCodeEligibilitySet] ([OrganizationId]) VALUES (8828), (8829);
                INSERT INTO [asap].[PatronCodeEligibilityMember] ([OrganizationId], [PatronCodeId])
                VALUES (8828, 8828), (8829, 8829);
                UPDATE [asap].[SystemSettings] SET [StaffApplicationUrl] = N'https://trusted.example.org/staff/'
                WHERE [OrganizationId] = 1;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        try
        {
            var actor = await ReadConfiguredSuperAdminAsync();
            using var client = factory!.CreateClient();
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Host = "poison.example.org";
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));

            using var settings = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            var links = settings.RootElement.GetProperty("publicPatron");
            Assert.AreEqual("https://trusted.example.org/patron/?libraryOrgId=8828",
                links.GetProperty("url").GetString());
            Assert.IsFalse(links.GetProperty("autoResize").GetString()!.Contains("poison.example.org", StringComparison.Ordinal));

            using var system = await ReadSettingsDocumentAsync(client, "system");
            Assert.AreEqual(JsonValueKind.Null, system.RootElement.GetProperty("publicPatron").ValueKind);

            using var forged = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = settings.RootElement.GetProperty("version").GetString(),
                workflow = new { allowedPatronCodeIds = new[] { 8828, 8829 } }
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode, await forged.Content.ReadAsStringAsync());
            using (var body = JsonDocument.Parse(await forged.Content.ReadAsStringAsync()))
            {
                Assert.AreEqual("patron_code_unknown", body.RootElement.GetProperty("code").GetString());
            }

            using var preserved = await client.PostAsJsonAsync("/api/asap/staff/settings", new
            {
                orgId = libraryId.ToString(),
                version = settings.RootElement.GetProperty("version").GetString(),
                workflow = new { allowedPatronCodeIds = new[] { 8828, 1 } }
            });
            Assert.AreEqual(HttpStatusCode.OK, preserved.StatusCode, await preserved.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { 1, 8828 },
                (await ReadPatronCodeRowsAsync(libraryId)).Values);

            await using (var connection = new SqlConnection(databaseConnectionString))
            {
                await connection.OpenAsync();
                await using var deactivate = new SqlCommand(
                    "UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @id;", connection);
                deactivate.Parameters.AddWithValue("@id", libraryId);
                await deactivate.ExecuteNonQueryAsync();
            }
            using var inactive = await ReadSettingsDocumentAsync(client, libraryId.ToString());
            Assert.AreEqual(JsonValueKind.Null, inactive.RootElement.GetProperty("publicPatron").ValueKind);
            StringAssert.Contains(inactive.RootElement.GetProperty("publicPatronUnavailableReason").GetString()!, "inactive");
        }
        finally
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText =
                """
                UPDATE [asap].[SystemSettings] SET [StaffApplicationUrl] = @staffUrl WHERE [OrganizationId] = 1;
                DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[PatronSettings] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[PatronCodeEligibilityMember] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[PatronCodeEligibilitySet] WHERE [OrganizationId] IN (8828, 8829);
                DELETE FROM [asap].[Organization] WHERE [Id] IN (8828, 8829);
                """;
            cleanup.Parameters.AddWithValue("@staffUrl", (object?)previousStaffUrl ?? DBNull.Value);
            await cleanup.ExecuteNonQueryAsync();
        }
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

        await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, "{}"));
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
        Assert.AreEqual("{\"status\":\"healthy\"}", await ready.Content.ReadAsStringAsync());
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
            actorId = Convert.ToInt64(await WithFixtureClock(command).ExecuteScalarAsync());
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
            openRequestId = Convert.ToInt64(await WithFixtureClock(command).ExecuteScalarAsync());

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
            closedRequestId = Convert.ToInt64(await WithFixtureClock(command).ExecuteScalarAsync());
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
        await using var reader = await WithFixtureClock(verifyCommand).ExecuteReaderAsync();
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
                    INSERT INTO [asap].[Organization]
                        ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                    VALUES (91320, N'Role contraction library', N'RCL', 2, 1, 1);
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
            await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
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
        await using var result = await WithFixtureClock(command).ExecuteReaderAsync();
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
        var firstSession = await IssueTestPatronSessionAsync("20000000002041");
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
        await using (var clockContext = await factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync())
        {
            var created = await clockContext.TitleRequests.AsNoTracking().SingleAsync(item => item.Id == firstResult.Id);
            Assert.AreEqual(timeProvider!.GetUtcNow().UtcDateTime, created.CreatedUtc);
            Assert.AreEqual(created.CreatedUtc, created.UpdatedUtc);
        }
        await AssertAutomaticRuleRaceStateAsync(
            submissionFirst,
            firstResult.Id,
            expectedAssignedEvents: 1,
            expectedCleanupEvents: 1);

        var lifecycleFirst = await SeedLifecycleAutoClaimRaceAsync("lifecycle-first");
        var secondSession = await IssueTestPatronSessionAsync("20000000002042");
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
        await DeleteTestPatronSessionAsync(firstSession.Id);
        await DeleteTestPatronSessionAsync(secondSession.Id);
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
                    INSERT INTO [asap].[Organization]
                        ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                    VALUES (82, N'Candidate Foreign Library', N'CFL', 2, 1, 1);
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
                    (2, N'Candidate Library Snapshot', 97001, N'Candidate API task', @formatId,
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
        Assert.AreEqual(95001, preview.Preview.Bibid);

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
            new VersionInput(StaffVersion.Encode(sourceVersion), StaffVersion.Encode(actor.RowVersion)),
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
            new VersionInput(task!.Version, StaffVersion.Encode(actor.RowVersion)),
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
                   AND [Title]=N'Legacy source workflow' AND [BibId]=95001
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
                UPDATE [asap].[TitleRequest] SET [LegacyHoldProtected] = 1 WHERE [Id] = @protectedId;
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
            Assert.IsFalse(protectedBody.RootElement.GetProperty("capabilities").GetProperty("canChangeWorkflowState").GetBoolean());
        }
        using var reopen = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{protectedRequestId}/action",
            new { version = protectedVersion, action = "reopen", status = "suggestion" });
        Assert.AreEqual(HttpStatusCode.Conflict, reopen.StatusCode, await reopen.Content.ReadAsStringAsync());
        using var reopenBody = JsonDocument.Parse(await reopen.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_history_retained", reopenBody.RootElement.GetProperty("code").GetString());

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
    public async Task StaffClosedPlacedHoldHistoryCannotReopenWithoutProviderCompensation()
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
                UPDATE [asap].[TitleRequest] SET [LegacyHoldProtected] = 1 WHERE [Id] = @legacyId;

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

        async Task AssertReopenBlockedAsync(long requestId)
        {
            using var get = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
            using var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.IsFalse(body.RootElement.GetProperty("capabilities").GetProperty("canChangeWorkflowState").GetBoolean());
            using var reopen = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestId}/action",
                new { version = body.RootElement.GetProperty("version").GetString(), action = "reopen", status = "suggestion" });
            Assert.AreEqual(HttpStatusCode.Conflict, reopen.StatusCode, await reopen.Content.ReadAsStringAsync());
            using var result = JsonDocument.Parse(await reopen.Content.ReadAsStringAsync());
            Assert.AreEqual("hold_history_retained", result.RootElement.GetProperty("code").GetString());
        }

        await AssertReopenBlockedAsync(legacyRequestId);
        await AssertReopenBlockedAsync(successfulRequestId);

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
        Assert.AreEqual(9040, result.GetInt32(1));
        Assert.AreEqual("9780000002141", result.GetString(2));
        Assert.AreEqual(9041, result.GetInt32(3));
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
            await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
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
        await using var result = await WithFixtureClock(command).ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(outstandingId, result.GetInt64(0));
        Assert.AreEqual("outstanding_purchase", result.GetString(1));
        Assert.AreEqual("9780000002190", result.GetString(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
        Assert.AreEqual("Identifier processing was not completed before this request left suggestions.",
            result.GetString(6));
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
        Assert.AreEqual(9052, result.GetInt32(4));
        Assert.AreEqual("found", result.GetString(5));
        Assert.AreEqual("Placed result", result.GetString(6));

        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(closedId, result.GetInt64(0));
        Assert.AreEqual("closed", result.GetString(1));
        Assert.AreEqual("9780000002153", result.GetString(3));
        Assert.AreEqual(9053, result.GetInt32(4));
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
        var actorVersion = sessionBody.RootElement.GetProperty("staff").GetProperty("version").GetString()!;

        async Task<string> VersionAsync(long requestId)
        {
            using var response = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("version").GetString()!;
        }

        using var retained = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{retainedId}")
        {
            Content = JsonContent.Create(new { version = await VersionAsync(retainedId), actorVersion })
        };
        using var retainedResponse = await client.SendAsync(retained);
        Assert.AreEqual(HttpStatusCode.Conflict, retainedResponse.StatusCode, await retainedResponse.Content.ReadAsStringAsync());
        using (var retainedBody = JsonDocument.Parse(await retainedResponse.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("hold_history_retained", retainedBody.RootElement.GetProperty("code").GetString());
        }

        using var stale = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{deletableId}")
        {
            Content = JsonContent.Create(new { version = Convert.ToBase64String(new byte[8]), actorVersion })
        };
        using var staleResponse = await client.SendAsync(stale);
        Assert.AreEqual(HttpStatusCode.Conflict, staleResponse.StatusCode, await staleResponse.Content.ReadAsStringAsync());

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/asap/staff/requests/{deletableId}")
        {
            Content = JsonContent.Create(new { version = await VersionAsync(deletableId), actorVersion })
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
        Assert.AreEqual(9042, result.GetInt32(6));
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
        var storage = factory!.Services.GetRequiredService<JobStorage>();
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
            await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            actorId = reader.GetInt64(0);
            requestId = reader.GetInt64(1);
            await reader.CloseAsync();

            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM [HangFire].[Job] WHERE [InvocationData] LIKE N'%IdentifierLookupJobs%';",
                connection);
            initialIdentifierJobCount = Convert.ToInt32(await WithFixtureClock(count).ExecuteScalarAsync());
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
                    WHERE [InvocationData] LIKE N'%IdentifierLookupJobs%'),
                   r.[RowVersion]
            FROM [asap].[TitleRequest] r
            WHERE r.[Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await WithFixtureClock(command).ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        byte[] retryVersion = (byte[])result[7];
        Assert.AreEqual("pending", result.GetString(0));
        Assert.AreEqual(0, result.GetInt32(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.IsTrue(result.IsDBNull(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.AreEqual(1, result.GetInt32(5));
        Assert.AreEqual(initialIdentifierJobCount + 1, result.GetInt32(6));

        var matchingIdentifierJobIds = new List<string>();
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var commandForJob = connection.CreateCommand();
            commandForJob.CommandText =
                """
                SELECT CONVERT(nvarchar(30), j.[Id])
                FROM [HangFire].[Job] j
                WHERE j.[InvocationData] LIKE N'%IdentifierLookupJobs%'
                  AND EXISTS
                  (
                      SELECT 1
                      FROM OPENJSON(j.[Arguments]) argument
                      WHERE argument.[key] = N'0'
                        AND TRY_CONVERT(bigint, argument.[value]) = @requestId
                  );
                """;
            commandForJob.Parameters.AddWithValue("@requestId", requestId);
            await using var jobs = await commandForJob.ExecuteReaderAsync();
            while (await jobs.ReadAsync())
            {
                matchingIdentifierJobIds.Add(jobs.GetString(0));
            }
        }

        Assert.HasCount(1, matchingIdentifierJobIds);
        var storedJobId = matchingIdentifierJobIds.Single();
        var storedJob = ReadEnqueuedHangfireJob(storage, storedJobId);
        Assert.AreEqual(typeof(IdentifierLookupJobs), storedJob.Type);
        Assert.AreEqual(nameof(IdentifierLookupJobs.ProcessAsync), storedJob.Method.Name);
        Assert.AreEqual(5, storedJob.Args.Count);
        Assert.AreEqual(requestId, Convert.ToInt64(storedJob.Args[0]));
        Assert.AreEqual("9780000002110", storedJob.Args[1] as string);
        Assert.AreEqual(2, Convert.ToInt32(storedJob.Args[2]));
        CollectionAssert.AreEqual(retryVersion, (byte[])storedJob.Args[3]);
        Assert.AreEqual(CancellationToken.None, (CancellationToken)storedJob.Args[4]);
        var jobHistory = storage.GetMonitoringApi().JobDetails(storedJobId);
        Assert.IsNotNull(jobHistory);
        var enqueuedState = jobHistory!.History.FirstOrDefault(state =>
            state.StateName == Hangfire.States.EnqueuedState.StateName);
        Assert.IsNotNull(enqueuedState);
        Assert.AreEqual("asap-identifier", enqueuedState!.Data["Queue"]);
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
            await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
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
        string originalTitle;
        string originalStatus;
        int originalBib;
        byte[] originalRowVersion;
        int originalEventCount;
        int originalOperationCount;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var baselineCommand = connection.CreateCommand();
            baselineCommand.CommandText =
                """
                SELECT r.[Title], r.[Status], r.[BibId], r.[RowVersion],
                       (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id]),
                       (SELECT COUNT(*) FROM [asap].[HoldPlacementOperation] h WHERE h.[TitleRequestId] = r.[Id])
                FROM [asap].[TitleRequest] r
                WHERE r.[Id] = @id;
                """;
            baselineCommand.Parameters.AddWithValue("@id", requestId);
            await using var baselineReader = await WithFixtureClock(baselineCommand).ExecuteReaderAsync();
            Assert.IsTrue(await baselineReader.ReadAsync());
            originalTitle = baselineReader.GetString(0);
            originalStatus = baselineReader.GetString(1);
            originalBib = baselineReader.GetInt32(2);
            originalRowVersion = (byte[])baselineReader[3];
            originalEventCount = baselineReader.GetInt32(4);
            originalOperationCount = baselineReader.GetInt32(5);
        }
        var originalDispatchCount = dispatcher!.EnqueuedIds.Count;

        using var update = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "edit", status = "suggestion", bibid = "7777" });

        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var updateBody = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_not_found", updateBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, rejectingProvider.ValidationCount);

        using var transition = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn" });
        Assert.AreEqual(HttpStatusCode.BadRequest, transition.StatusCode,
            await transition.Content.ReadAsStringAsync());
        using var transitionBody = JsonDocument.Parse(await transition.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_not_found", transitionBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(2, rejectingProvider.ValidationCount,
            "An unchanged stored BIB must be checked before a manual Pending hold transition.");

        using var purchase = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "purchase" });
        Assert.AreEqual(HttpStatusCode.BadRequest, purchase.StatusCode,
            await purchase.Content.ReadAsStringAsync());
        using var purchaseBody = JsonDocument.Parse(await purchase.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_not_found", purchaseBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(3, rejectingProvider.ValidationCount,
            "Purchase must validate an unchanged BIB when it resolves to Pending hold.");

        rejectingProvider.OperationalFailure = true;
        using var exactLookup = await client.PostAsJsonAsync(
            "/api/asap/staff/bib-lookup",
            new { requestId = requestId.ToString(), mode = "bib", bibId = "9001" });
        Assert.AreEqual(HttpStatusCode.BadGateway, exactLookup.StatusCode,
            await exactLookup.Content.ReadAsStringAsync());
        using var exactLookupBody = JsonDocument.Parse(await exactLookup.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_validation_unavailable", exactLookupBody.RootElement.GetProperty("code").GetString());

        foreach (var (action, status) in new[]
                 {
                     ("edit", "suggestion"),
                     ("alreadyOwn", (string?)null),
                     ("purchase", (string?)null)
                 })
        {
            var input = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["action"] = action
            };
            if (status is not null)
            {
                input["status"] = status;
            }
            if (action == "edit")
            {
                input["bibid"] = "7777";
            }

            using var unavailable = await client.PostAsJsonAsync(
                $"/api/asap/staff/title-requests/{requestId}/action",
                input);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode,
                await unavailable.Content.ReadAsStringAsync());
            using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.AreEqual("bib_validation_unavailable", unavailableBody.RootElement.GetProperty("code").GetString());
        }
        Assert.AreEqual(7, rejectingProvider.ValidationCount);

        rejectingProvider.OperationalFailure = false;
        rejectingProvider.ValidationException = new OperationCanceledException("Provider timeout");
        using var timedOut = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn" });
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode,
            await timedOut.Content.ReadAsStringAsync());
        using var timedOutBody = JsonDocument.Parse(await timedOut.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_validation_unavailable", timedOutBody.RootElement.GetProperty("code").GetString());
        rejectingProvider.ValidationException = null;

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT r.[Title], r.[Status], r.[BibId], r.[RowVersion],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id]),
                   (SELECT COUNT(*) FROM [asap].[HoldPlacementOperation] h WHERE h.[TitleRequestId] = r.[Id])
            FROM [asap].[TitleRequest] r
            WHERE r.[Id] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using (var reader = await WithFixtureClock(command).ExecuteReaderAsync())
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(originalTitle, reader.GetString(0));
            Assert.AreEqual(originalStatus, reader.GetString(1));
            Assert.AreEqual(originalBib, reader.GetInt32(2));
            CollectionAssert.AreEqual(originalRowVersion, (byte[])reader[3]);
            Assert.AreEqual(originalEventCount, reader.GetInt32(4));
            Assert.AreEqual(originalOperationCount, reader.GetInt32(5));
        }
        Assert.AreEqual(originalDispatchCount, dispatcher.EnqueuedIds.Count);

        rejectingProvider.ValidBib = true;
        using var unverified = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn" });
        Assert.AreEqual(HttpStatusCode.Conflict, unverified.StatusCode,
            await unverified.Content.ReadAsStringAsync());
        using var unverifiedBody = JsonDocument.Parse(await unverified.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_unverified", unverifiedBody.RootElement.GetProperty("code").GetString());

        using var selected = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn", bibid = "9001", staffSelectedBibId = "9001" });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        using var selectedBody = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.AreEqual("pending_hold", selectedBody.RootElement.GetProperty("finalStatus").GetString());
        Assert.IsTrue(selectedBody.RootElement.GetProperty("bibidStaffVerified").GetBoolean());

        using var repeated = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn", bibid = "9001", staffSelectedBibId = "9001" });
        Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode,
            await repeated.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task MalformedNominalBibGetSuccessCannotMoveRequestToPendingHold()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"BibGetRows":[{}]}
            """);
        var polarisProvider = await CreatePolarisProviderAsync(handler);
        await using var malformedFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(polarisProvider);
            }));
        using var client = malformedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
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
                VALUES (2, N'20000000002106', N'Malformed BIB validation title', N'9780000000216', 1, @formatId,
                        N'suggestion', N'9001', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                SELECT @actorId, CONVERT(bigint, SCOPE_IDENTITY());
                """;
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

        async Task<(string Title, string Status, int BibId, byte[] RowVersion, int EventCount,
            int OperationCount, int OutboxCount)> ReadRequestStateAsync()
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT r.[Title], r.[Status], r.[BibId], r.[RowVersion],
                       (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id]),
                       (SELECT COUNT(*) FROM [asap].[HoldPlacementOperation] h WHERE h.[TitleRequestId] = r.[Id]),
                       (SELECT COUNT(*) FROM [asap].[EmailOutbox] o
                        WHERE o.[BusinessKey] LIKE N'purchase-reminder:' + CONVERT(nvarchar(20), r.[Id]) + N':%'
                           OR o.[BusinessKey] LIKE N'title-hold-placed:' + CONVERT(nvarchar(20), r.[Id]) + N':%')
                FROM [asap].[TitleRequest] r
                WHERE r.[Id] = @id;
                """,
                connection);
            command.Parameters.AddWithValue("@id", requestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                (byte[])reader[3],
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6));
        }

        var original = await ReadRequestStateAsync();
        Assert.AreEqual("suggestion", original.Status);
        Assert.AreEqual(9001, original.BibId);
        Assert.AreEqual(0, original.OperationCount);
        Assert.AreEqual(0, original.OutboxCount);
        var originalDispatchCount = dispatcher!.EnqueuedIds.Count;

        using var transition = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/action",
            new { version, action = "alreadyOwn" });

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, transition.StatusCode,
            await transition.Content.ReadAsStringAsync());
        using var transitionBody = JsonDocument.Parse(await transition.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_validation_unavailable", transitionBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(1, handler.RequestCount);
        StringAssert.Contains(handler.RequestPaths[0], "/bib/9001");

        var after = await ReadRequestStateAsync();
        Assert.AreEqual(original.Title, after.Title);
        Assert.AreEqual("suggestion", after.Status);
        Assert.AreEqual(original.BibId, after.BibId);
        CollectionAssert.AreEqual(original.RowVersion, after.RowVersion);
        Assert.AreEqual(original.EventCount, after.EventCount);
        Assert.AreEqual(original.OperationCount, after.OperationCount);
        Assert.AreEqual(original.OutboxCount, after.OutboxCount);
        Assert.AreEqual(originalDispatchCount, dispatcher.EnqueuedIds.Count);
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

        timeProvider!.SetUtcNow(DateTimeOffset.UtcNow);

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
        Assert.AreEqual(HttpStatusCode.Conflict, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        Assert.AreEqual(1, pickupProvider.UpdateCount);
        Assert.AreEqual(101, await ReadPickupSnapshotAsync(requestId));

        pickupProvider.FailUpdate = false;
        using var uncertainRetry = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-preference",
            new { version, preferredPickupBranchId = 102, currentPreferredPickupBranchIdAtLoad = 101 });
        Assert.AreEqual(HttpStatusCode.Conflict, uncertainRetry.StatusCode);
        Assert.AreEqual(1, pickupProvider.UpdateCount, "An uncertain dispatch must not be repeated.");

        // A subsequent authoritative read proves the desired value is now live.
        pickupProvider.CurrentPickupBranchId = 102;
        using var updated = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/pickup-preference",
            new { version, preferredPickupBranchId = 102, currentPreferredPickupBranchIdAtLoad = 101 });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        Assert.AreEqual(1, pickupProvider.UpdateCount);
        Assert.AreEqual(102, pickupProvider.CurrentPickupBranchId);
        Assert.AreEqual(102, await ReadPickupSnapshotAsync(requestId));

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var eventCount = new SqlCommand(
            "SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @id AND [EventType] = N'pickup_preference_reconciled';",
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
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IPatronProvider>(holdProvider);
                services.AddSingleton<IStaffPolarisProvider>(holdProvider);
                services.AddSingleton<IEmailOutboxDispatcher, CanceledOutboxDispatcher>();
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
                VALUES (2, N'20000000002105', N'Hold placement title', 1, @formatId, N'pending_hold', N'09001',
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

        using var unverified = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = getBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.Conflict, unverified.StatusCode);
        using var unverifiedBody = JsonDocument.Parse(await unverified.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_unverified", unverifiedBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(0, holdProvider.CreateCount);
        await using (var markVerified = new SqlConnection(databaseConnectionString))
        {
            await markVerified.OpenAsync();
            await using var update = new SqlCommand(
                "UPDATE [asap].[TitleRequest] SET [BibIdStaffVerified] = 1 WHERE [Id] = @id;", markVerified);
            update.Parameters.AddWithValue("@id", requestId);
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        }
        using var verifiedGet = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var verifiedBody = JsonDocument.Parse(await verifiedGet.Content.ReadAsStringAsync());

        using var placed = await client.PostAsJsonAsync(
            $"/api/asap/staff/title-requests/{requestId}/place-hold",
            new { version = verifiedBody.RootElement.GetProperty("version").GetString() });
        Assert.AreEqual(HttpStatusCode.OK, placed.StatusCode, await placed.Content.ReadAsStringAsync());
        using var placedBody = JsonDocument.Parse(await placed.Content.ReadAsStringAsync());
        Assert.AreEqual("hold_placed", placedBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("dispatch_failed", placedBody.RootElement.GetProperty("notificationStatus").GetString());
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
        Assert.AreEqual(holdProvider.RequestGuid, result.GetGuid(2));
        Assert.AreEqual(8123, result.GetInt32(3));
        Assert.AreEqual(1, result.GetInt32(4));
        Assert.AreEqual(3, result.GetInt32(5));
        Assert.AreEqual(2, result.GetInt32(6));
        Assert.AreEqual(1, result.GetInt32(7));
        for (var index = 8; index <= 12; index++) Assert.IsFalse(result.IsDBNull(index));
        Assert.AreEqual("hold_placed", result.GetString(13));
    }

    [TestMethod]
    public async Task StaffHoldPlacementAdoptsOneLiveSameBibHoldWithoutMutationMarkers()
    {
        var holdProvider = ScriptedHoldProvider.AmbiguousCreate();
        holdProvider.Holds = [new PolarisHoldSnapshot(8451, 9002, 3, "Active", 101)];
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002107', N'Existing hold adoption title', 1, @formatId, N'pending_hold', N'09002', 1,
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
            SELECT [State], [Phase], [PolarisHoldId], [OutcomeEvidenceKind], [CreateStartedUtc], [ReplyStartedUtc],
                   [PickupBranchIdSnapshot], [RequestingOrganizationIdSnapshot]
            FROM [asap].[HoldPlacementOperation]
            WHERE [TitleRequestId] = @id;
            """,
            verify);
        command.Parameters.AddWithValue("@id", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("succeeded", result.GetString(0));
        Assert.AreEqual("result_recorded", result.GetString(1));
        Assert.AreEqual(8451, result.GetInt32(2));
        Assert.AreEqual("existing_hold_adoption", result.GetString(3));
        Assert.IsTrue(result.IsDBNull(4));
        Assert.IsTrue(result.IsDBNull(5));
        Assert.AreEqual(101, result.GetInt32(6), "Adoption records the exact pickup of the existing hold.");
        Assert.IsTrue(result.IsDBNull(7),
            "The adopted hold's original create route is unknown; the current patron registration is not historical evidence.");
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002108', N'Throwing hold placement title', 1, @formatId, N'pending_hold', N'9003', 1,
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002109', N'Correlated ambiguous hold title', 1, @formatId, N'pending_hold', N'9004', 1,
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
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (91211, N'Inactive recovery library', N'IRL', 2, 1, 0);
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (91211, N'20000000002111', N'Expired acquired hold title', 1, @formatId, N'pending_hold', N'9005', 1,
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
    public async Task StaffHoldReconcileWillNotDispatchAcquiredWorkAfterBibVerificationIsLost()
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

        var requestId = await SeedPendingHoldRequestAsync("Unverified acquired recovery", "20000000002133", 9033);
        long operationId;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new SqlCommand(
                """
                UPDATE [asap].[TitleRequest] SET [BibIdStaffVerified] = 0 WHERE [Id] = @requestId;
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                     [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch], [LeaseExpiresUtc], [RequestStartedUtc])
                VALUES (@requestId, N'20000000002133', N'9033', 101, 1, N'in_progress', N'acquired',
                        NEWID(), 1, DATEADD(minute, -1, SYSUTCDATETIME()), SYSUTCDATETIME());
                SELECT CONVERT(bigint, SCOPE_IDENTITY());
                """, connection);
            seed.Parameters.AddWithValue("@requestId", requestId);
            operationId = Convert.ToInt64(await seed.ExecuteScalarAsync());
        }

        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var actor = new SqlCommand(
                "SELECT [Id] FROM [asap].[StaffUser] WHERE [NormalizedUserPrincipalName] = N'ADMIN@EXAMPLE.ORG';", connection);
            client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", Convert.ToInt64(await actor.ExecuteScalarAsync()).ToString());
        }
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        using var session = await client.GetAsync("/api/asap/staff/session");
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());
        using var detail = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}");
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var version = detailBody.RootElement.GetProperty("holdOperation").GetProperty("version").GetString();

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/reconcile", new { version });
        Assert.AreEqual(HttpStatusCode.Conflict, reconcile.StatusCode);
        using var responseBody = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_unverified", responseBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(0, holdProvider.ReplyCount);

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT operation.[State], operation.[Phase], operation.[CreateStartedUtc], request.[Status]
            FROM [asap].[HoldPlacementOperation] operation
            JOIN [asap].[TitleRequest] request ON request.[Id] = operation.[TitleRequestId]
            WHERE operation.[Id] = @operationId;
            """, verify);
        command.Parameters.AddWithValue("@operationId", operationId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual("operator_required", result.GetString(0));
        Assert.AreEqual("acquired", result.GetString(1));
        Assert.IsTrue(result.IsDBNull(2));
        Assert.AreEqual("pending_hold", result.GetString(3));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(1)]
    [DataRow(3)]
    public async Task StaffHoldReconcileResumesDurableReplyReadyWithoutRepeatingCreate(int requestingOrganizationId)
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002130', N'Reply ready recovery title', 1, @formatId, N'pending_hold', N'9030', 1,
                        101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
                DECLARE @requestId bigint = SCOPE_IDENTITY();
                INSERT INTO [asap].[HoldPlacementOperation]
                    ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot],
                     [PickupBranchIdSnapshot], [RequestingOrganizationIdSnapshot], [WorkstationIdSnapshot],
                     [PolarisUserIdSnapshot], [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch],
                     [LeaseExpiresUtc], [RequestStartedUtc], [CreateStartedUtc], [CreateResponseObservedUtc],
                     [PolarisRequestGuid], [TxnGroupQualifier], [TxnQualifier], [ReplyAnswer], [ReplyState],
                     [ProviderStatusType], [ProviderStatusValue], [ResultCode], [OutcomeEvidenceKind])
                VALUES (@requestId, N'20000000002130', 7130, 9030, 101, @requestingOrg, 1, 1, 1,
                        N'in_progress', N'reply_ready', NEWID(), 1, DATEADD(minute, -1, SYSUTCDATETIME()),
                        DATEADD(minute, -5, SYSUTCDATETIME()), DATEADD(minute, -4, SYSUTCDATETIME()),
                        DATEADD(minute, -3, SYSUTCDATETIME()), @requestGuid, N'group-qualifier',
                        N'transaction-qualifier', N'1', N'3', N'3', N'5', N'reply_required',
                        N'create_status_5_reply_required');
                SELECT @actorId, @requestId;
                """;
            seed.Parameters.AddWithValue("@objectId", Guid.Parse(identity.ObjectId!));
            seed.Parameters.AddWithValue("@requestGuid", holdProvider.RequestGuid);
            seed.Parameters.AddWithValue("@requestingOrg", requestingOrganizationId);
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
        Assert.IsTrue(StaffVersion.TryDecode(getBody.RootElement.GetProperty("version").GetString(),
            out var originalRequestRowVersion));
        Assert.IsTrue(StaffVersion.TryDecode(operation.GetProperty("version").GetString(),
            out var originalOperationRowVersion));

        using var reconcile = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operation.GetProperty("id").GetString()}/reconcile",
            new { version = operation.GetProperty("version").GetString() });
        var replyAllowed = requestingOrganizationId > 1;
        Assert.AreEqual(replyAllowed ? HttpStatusCode.OK : HttpStatusCode.Conflict,
            reconcile.StatusCode, await reconcile.Content.ReadAsStringAsync());
        Assert.AreEqual(0, holdProvider.CreateCount);
        Assert.AreEqual(replyAllowed ? 1 : 0, holdProvider.ReplyCount);
        if (replyAllowed)
        {
            Assert.AreEqual(requestingOrganizationId, holdProvider.LastReplyCommand!.RequestingOrganizationId,
                "Reply must use the immutable native member route stored by the dispatched create.");
        }
        else
        {
            Assert.IsNull(holdProvider.LastReplyCommand,
                "A system-scope sentinel is not a valid native reply route.");
        }

        await using var verify = new SqlConnection(databaseConnectionString);
        await verify.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT request.[Status], operation.[State], operation.[Phase], operation.[ExecutionEpoch],
                   operation.[ReplyStartedUtc], operation.[ReplyResponseObservedUtc], operation.[PolarisHoldId],
                   operation.[RecoveryAttemptCount],
                   operation.[RequestingOrganizationIdSnapshot],
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent]
                    WHERE [TitleRequestId] = @requestId AND [EventType] = N'hold_placed'),
                   (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] = @requestId),
                   (SELECT COUNT(*) FROM [asap].[EmailOutbox]
                    WHERE [BusinessKey] LIKE CONCAT(N'title-hold-placed:', @requestId, N':%')),
                   request.[RowVersion], operation.[RowVersion]
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[TitleRequestId] = request.[Id]
            WHERE request.[Id] = @requestId;
            """,
            verify);
        command.Parameters.AddWithValue("@requestId", requestId);
        await using var result = await command.ExecuteReaderAsync();
        Assert.IsTrue(await result.ReadAsync());
        Assert.AreEqual(requestingOrganizationId, result.GetInt32(8),
            "The journal must retain the original requesting organization, independent of servicing scope.");
        var requestRowVersionUnchanged = originalRequestRowVersion.SequenceEqual((byte[])result[12]);
        var operationRowVersionUnchanged = originalOperationRowVersion.SequenceEqual((byte[])result[13]);
        Assert.AreEqual(!replyAllowed, requestRowVersionUnchanged,
            "Only a successful reply may transition the request row.");
        Assert.IsFalse(operationRowVersionUnchanged,
            "The operation rowversion must advance when reply-ready work succeeds or is fenced for operator review.");
        if (requestingOrganizationId == 1)
        {
            Assert.AreEqual("pending_hold", result.GetString(0));
            Assert.AreEqual("operator_required", result.GetString(1));
            Assert.AreEqual("reply_ready", result.GetString(2));
            Assert.IsTrue(result.IsDBNull(4));
            Assert.IsTrue(result.IsDBNull(5));
            Assert.IsTrue(result.IsDBNull(6));
            Assert.AreEqual(0, result.GetInt32(9));
            Assert.AreEqual(0, result.GetInt32(10));
            Assert.AreEqual(0, result.GetInt32(11));
            return;
        }
        Assert.AreEqual("hold_placed", result.GetString(0));
        Assert.AreEqual("succeeded", result.GetString(1));
        Assert.AreEqual("result_recorded", result.GetString(2));
        Assert.AreEqual(2L, result.GetInt64(3));
        Assert.IsFalse(result.IsDBNull(4));
        Assert.IsFalse(result.IsDBNull(5));
        Assert.AreEqual(8123, result.GetInt32(6));
        Assert.AreEqual(1, result.GetInt32(7));
        Assert.AreEqual(1, result.GetInt32(9));
        Assert.AreEqual(1, result.GetInt32(10));
        Assert.AreEqual(1, result.GetInt32(11));
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002131', N'Late create result title', 1, @formatId, N'pending_hold', N'9031', 1,
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
            8131,
            null,
            null,
            2,
            1,
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
        var requestId = await SeedPendingHoldRequestAsync("Late reply result title", "20000000002132", 9032);
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
            holdProvider.RequestGuid,
            8132,
            "group-qualifier",
            "transaction-qualifier",
            2,
            1,
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
                        DATEADD(minute, -4, SYSUTCDATETIME()), N'9b934869-b681-4523-b2fb-8604dd0d0832', 2, 1,
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
                new ProvenHoldIdentityEvidence(8460, "provider-operation-proof-9020"),
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
        Assert.AreEqual(9020, enriched.GetInt32(2));
        Assert.AreEqual("succeeded", enriched.GetString(4));
        Assert.AreEqual("result_recorded", enriched.GetString(5));
        Assert.IsFalse(enriched.IsDBNull(6));
        Assert.AreEqual(9020, enriched.GetInt32(7));
        Assert.AreEqual(2, enriched.GetInt32(8));
        Assert.AreEqual(3L, enriched.GetInt64(9));
        Assert.AreEqual(1, enriched.GetInt32(10));
        Assert.IsFalse(enriched.IsDBNull(11));
        Assert.IsTrue(enriched.IsDBNull(12));
        Assert.IsTrue(enriched.IsDBNull(13));
        Assert.AreEqual(Guid.Parse("9b934869-b681-4523-b2fb-8604dd0d0832"), enriched.GetGuid(14));
        Assert.AreEqual(2, enriched.GetInt32(15));
        Assert.AreEqual(1, enriched.GetInt32(16));
        Assert.AreEqual("success", enriched.GetString(17));
        Assert.AreEqual(8460, enriched.GetInt32(18));
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
        var seeded = await SeedCompletedHoldIdentityAsync("competing", "20000000002121", 9021);
        var service = factory!.Services.GetRequiredService<HoldPlacementService>();
        var writes = await Task.WhenAll(
            service.RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence(8461, "provider-operation-proof-9021-a"),
                CancellationToken.None),
            service.RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence(8462, "provider-operation-proof-9021-b"),
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
        var storedId = reader.GetInt32(0);
        using var detail = JsonDocument.Parse(reader.GetString(1));
        var storedReference = detail.RootElement
            .GetProperty("identityCorrelation")
            .GetProperty("evidenceReference")
            .GetString();
        Assert.IsTrue(
            storedId == 8461 && storedReference == "provider-operation-proof-9021-a" ||
            storedId == 8462 && storedReference == "provider-operation-proof-9021-b");
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentNeverReplacesExistingIdentity()
    {
        var seeded = await SeedCompletedHoldIdentityAsync(
            "existing",
            "20000000002122",
            9022,
            holdRequestId: 8463);
        var result = await factory!.Services.GetRequiredService<HoldPlacementService>()
            .RecordCompletedIdentityAsync(
                seeded.OperationId,
                seeded.RequestVersion,
                seeded.OperationVersion,
                new ProvenHoldIdentityEvidence(9999, "provider-operation-proof-9022"),
                CancellationToken.None);
        Assert.AreEqual("hold_identity_already_recorded", result.Code);
        Assert.AreEqual("8463", await ReadHoldIdentityAsync(seeded.OperationId));
    }

    [TestMethod]
    public async Task StaffCompletedHoldIdentityEnrichmentRequiresCurrentRequestAndOperationVersions()
    {
        var staleRequest = await SeedCompletedHoldIdentityAsync("stale-request", "20000000002123", 9023);
        var staleOperation = await SeedCompletedHoldIdentityAsync("stale-operation", "20000000002124", 9024);
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
            new ProvenHoldIdentityEvidence(8464, "provider-operation-proof-9023"),
            CancellationToken.None);
        var operationResult = await service.RecordCompletedIdentityAsync(
            staleOperation.OperationId,
            staleOperation.RequestVersion,
            staleOperation.OperationVersion,
            new ProvenHoldIdentityEvidence(8465, "provider-operation-proof-9024"),
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
            9025,
            operationBarcode: "20000000009999");
        var changedBib = await SeedCompletedHoldIdentityAsync(
            "changed-bib",
            "20000000002126",
            9026,
            operationBibId: 9999);
        var superseded = await SeedCompletedHoldIdentityAsync("superseded", "20000000002127", 9027);
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
            new ProvenHoldIdentityEvidence(8467, "provider-operation-proof-9025"),
            CancellationToken.None);
        var bibResult = await service.RecordCompletedIdentityAsync(
            changedBib.OperationId,
            changedBib.RequestVersion,
            changedBib.OperationVersion,
            new ProvenHoldIdentityEvidence(8468, "provider-operation-proof-9026"),
            CancellationToken.None);
        var supersededResult = await service.RecordCompletedIdentityAsync(
            superseded.OperationId,
            superseded.RequestVersion,
            superseded.OperationVersion,
            new ProvenHoldIdentityEvidence(8469, "provider-operation-proof-9027"),
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
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@id, N'Inactive identity library', N'IIL', 2, 1, 0);
                """;
            seedOrganization.Parameters.AddWithValue("@id", inactiveOrganizationId);
            Assert.AreEqual(1, await seedOrganization.ExecuteNonQueryAsync());
        }
        var inactive = await SeedCompletedHoldIdentityAsync(
            "inactive",
            "20000000002128",
            9028,
            organizationId: inactiveOrganizationId);
        var leased = await SeedCompletedHoldIdentityAsync("leased", "20000000002129", 9029);
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
            new ProvenHoldIdentityEvidence(8470, "provider-operation-proof-9028"),
            CancellationToken.None);
        var leasedResult = await service.RecordCompletedIdentityAsync(
            leased.OperationId,
            leased.RequestVersion,
            leased.OperationVersion,
            new ProvenHoldIdentityEvidence(8471, "provider-operation-proof-9029"),
            CancellationToken.None);
        Assert.AreEqual("organization_inactive", inactiveResult.Code);
        Assert.AreEqual("hold_identity_not_enrichable", leasedResult.Code);
    }

    [TestMethod]
    public async Task StaffHoldReplyExceptionPersistsMarkedAmbiguityAndNeverReplaysReply()
    {
        var holdProvider = ScriptedHoldProvider.ReplyRequiredThenSuccess();
        holdProvider.ReplyException = new PolarisOperationalException("provider_transport_ambiguous", "synthetic reply disconnect");
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002114', N'Reply ambiguity title', 1, @formatId, N'pending_hold', N'9009', 1,
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
        var emailSender = new MutableReadinessEmailSender(isConfigured: true);
        await using var dispatchFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailSender>(emailSender);
                services.AddSingleton<IEmailOutboxDispatcher, CanceledOutboxDispatcher>();
            }));
        using var client = dispatchFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
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
        emailSender.TimeoutReadiness = true;
        using var timedOut = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/resolve", resolution);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode,
            await timedOut.Content.ReadAsStringAsync());
        using (var timedOutBody = JsonDocument.Parse(await timedOut.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("hold_resolution_dependency_unavailable", timedOutBody.RootElement.GetProperty("code").GetString());
        }
        using (var unchanged = await client.GetAsync($"/api/asap/staff/title-requests/{requestId}"))
        using (var unchangedBody = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("pending_hold", unchangedBody.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(operation.GetProperty("version").GetString(),
                unchangedBody.RootElement.GetProperty("holdOperation").GetProperty("version").GetString());
        }
        emailSender.TimeoutReadiness = false;
        using var resolve = await client.PostAsJsonAsync(
            $"/api/asap/staff/hold-operations/{operationId}/resolve",
            resolution);
        Assert.AreEqual(HttpStatusCode.OK, resolve.StatusCode, await resolve.Content.ReadAsStringAsync());
        using (var resolvedBody = JsonDocument.Parse(await resolve.Content.ReadAsStringAsync()))
        {
            Assert.IsTrue(resolvedBody.RootElement.GetProperty("committed").GetBoolean());
            Assert.AreEqual("hold_placed", resolvedBody.RootElement.GetProperty("finalStatus").GetString());
            Assert.AreEqual("dispatch_failed", resolvedBody.RootElement.GetProperty("notificationStatus").GetString());
        }
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
                    ([LibraryOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                VALUES (2, N'20000000002106', N'Ambiguous hold title', 1, @formatId, N'pending_hold', N'9001', 1,
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
            var requestId = await SeedPendingHoldRequestAsync("Create marker fence", "20000000003201", 93201);
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
            var requestId = await SeedPendingHoldRequestAsync("Reply marker fence", "20000000003202", 93202);
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

        var result = await provider.LookupIdentifierAsync("9780000000099", 2, CancellationToken.None);

        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchUsesSupportedQueriesAndMapsCatalogRows()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"TotalRecordsFound":4,"BibSearchRows":[
              {"ControlNumber":9001,"Title":"  Catalog title  ","Author":"Catalog author",
               "PublicationDate":"2026","ISBN":"9780000000001","TypeOfMaterial":"1"},
              {"ControlNumber":9002,"Title":"Electronic title","PrimaryTypeOfMaterial":36},
              {"ControlNumber":9003,"Title":"Alternate title","Author":"Second author",
               "TypeOfMaterial":"Book","UPC":"012345678901"},
              {"ControlNumber":9001,"Title":"Duplicate title"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var byIdentifier = await provider.SearchBibsAsync("identifier", "978-0000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(2, byIdentifier.Results.Count);
        Assert.AreEqual(4, byIdentifier.TotalMatches);
        Assert.AreEqual(9001, byIdentifier.Results[0].BibId);
        Assert.AreEqual("Catalog title", byIdentifier.Results[0].Title);
        Assert.AreEqual("2026", byIdentifier.Results[0].Publication);
        Assert.AreEqual(9003, byIdentifier.Results[1].BibId);
        Assert.AreEqual("012345678901", byIdentifier.Results[1].Identifier);
        CollectionAssert.AreEqual(
            new[] { "ISBN", "boolean", "LCCN" },
            handler.RequestUris.Select(uri => uri.AbsolutePath.Split('/').Last()).ToArray());
        StringAssert.Contains(Uri.UnescapeDataString(handler.RequestUris[1].Query), "q=UPC=\"9780000000001\"");
        Assert.IsTrue(handler.RequestUris.All(uri =>
            Uri.UnescapeDataString(uri.Query).Contains("sortby=PDTI", StringComparison.Ordinal) &&
            Uri.UnescapeDataString(uri.Query).Contains("notran=1", StringComparison.Ordinal) &&
            !Uri.UnescapeDataString(uri.Query).Contains("sort=", StringComparison.Ordinal)));

        handler.RequestUris.Clear();
        await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("author", "An author", "", "", 2, CancellationToken.None);
        await provider.SearchBibsAsync("title_author", "", "A title", "An author", 2, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "TI", "AU", "boolean", "TI" },
            handler.RequestUris.Select(uri => uri.AbsolutePath.Split('/').Last()).ToArray());
        StringAssert.Contains(Uri.UnescapeDataString(handler.RequestUris[2].Query),
            "q=TI=\"A title\" AND AU=\"An author\"");
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsSparseRowsWithAValidBIBIdentity()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9001, result.Results[0].BibId);
        Assert.IsNull(result.Results[0].Title);
        Assert.AreEqual(1, result.TotalMatches);
    }

    [TestMethod]
    [DataRow("{\"BibID\":9001}")]
    [DataRow("{\"BibliographicRecordID\":9001}")]
    public async Task PolarisStaffSearchRejectsUndocumentedBIBIdentityAliases(string row)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            $$"""{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{{row}}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(() =>
            provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsOnlyAWellFormedNonnegativeEmptyResponseAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[],"ErrorMessage":""}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchTreatsCodeZeroEmptyInformationalMessageAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"ErrorMessage":"Search terms normalized","TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffTitleSearchAcceptsTheCoherentMinusOneEmptyResponse()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(0, result.TotalMatches);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchAcceptsPapiRowCountAndInformationalMessage()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":1,"ErrorMessage":"Search terms normalized","TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(9001, result.Results.Single().BibId);
    }

    [TestMethod]
    public async Task PolarisStaffSearchFiltersKnownMaterialTypesAfterValidatingTheirRows()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"PrimaryTypeOfMaterial":36}]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None);

        Assert.AreEqual(0, result.Results.Count);
        Assert.AreEqual(1, result.TotalMatches);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"General failure\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-9,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"papierrorcode\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":-1}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":\"not-a-number\"}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibID\":9002}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"papierrorcode\":-9,\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"bibsearchrows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"totalrecordsfound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow(200, "{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0,\"PrimaryTypeOfMaterial\":36}]}")]
    public async Task PolarisStaffSearchTreatsTransportAndMalformedResponsesAsUnavailable(
        int statusCode,
        string content)
    {
        var handler = new StaticResponseHandler((HttpStatusCode)statusCode, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.SearchBibsAsync("title", "A title", "", "", 2, CancellationToken.None));

        Assert.AreEqual("polaris_bib_search_failed", failure.Code);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierSearchKeepsTrustworthyPartialResultsAndFailsWhenEveryAttemptFails()
    {
        var valid = """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}""";
        var first = new SequenceResponseHandler(
            (HttpStatusCode.OK, valid),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"));
        var firstProvider = await CreatePolarisProviderAsync(first);
        var firstResult = await firstProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(9001, firstResult.Results.Single().BibId);
        Assert.AreEqual(3, first.RequestCount);

        var partial = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, valid),
            (HttpStatusCode.ServiceUnavailable, "{}"));
        var partialProvider = await CreatePolarisProviderAsync(partial);

        var result = await partialProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9001, result.Results[0].BibId);
        Assert.AreEqual(3, partial.RequestCount);

        var last = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, valid));
        var lastProvider = await CreatePolarisProviderAsync(last);
        var lastResult = await lastProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(9001, lastResult.Results.Single().BibId);
        Assert.AreEqual(3, last.RequestCount);

        var failed = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.BadGateway, "{}"));
        var failedProvider = await CreatePolarisProviderAsync(failed);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await failedProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None));
        Assert.AreEqual(3, failed.RequestCount);

        var empty = """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}""";
        var allEmpty = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, empty),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var emptyProvider = await CreatePolarisProviderAsync(allEmpty);
        var noMatches = await emptyProvider.SearchBibsAsync("identifier", "9780000000001", "", "", 2, CancellationToken.None);
        Assert.AreEqual(0, noMatches.Results.Count);
        Assert.AreEqual(3, allEmpty.RequestCount);

        var emptyAndFailed = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, empty));
        var emptyAndFailedProvider = await CreatePolarisProviderAsync(emptyAndFailed);
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await emptyAndFailedProvider.SearchBibsAsync(
                "identifier", "9780000000001", "", "", 2, CancellationToken.None));
        Assert.AreEqual(3, emptyAndFailed.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupAcceptsOnlyCoherentMinusOneAndZeroEmptyAttempts()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.NotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupFindsOnlyRowsWithValidatedControlNumbers()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":1,"TotalRecordsFound":2,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","BibGetRows":null}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.Found, result.Outcome);
        Assert.AreEqual(9001, result.BibId);
        Assert.IsTrue(result.MultipleMatches, "TotalRecordsFound may exceed the current page row count.");
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisIdentifierLookupPreservesCancellationDuringOptionalBibDetail()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9001,"Title":"Catalog title"}]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        handler.OnRequest = (count, token) =>
        {
            if (count == 4)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
        };
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.LookupIdentifierAsync("9780000000001", 2, cancellation.Token));

        Assert.AreEqual(4, handler.RequestCount);
        Assert.IsTrue(cancellation.IsCancellationRequested);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"ErrorMessage\":\"Search terms normalized\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"ErrorMessage\":\"\"}")]
    [DataRow("""{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[],"ErrorMessage":"  "}""")]
    public async Task PolarisIdentifierLookupTreatsInformationalOrBlankEmptyMessagesAsDefinitiveEmpty(string content)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000001", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.DefinitiveNotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisTitleAuthorSearchCanUseAValidFallbackAfterPrimaryFailure()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"TotalRecordsFound":1,"BibSearchRows":[{"ControlNumber":9002,"Title":"Fallback title","Author":"An author"}]}"""));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.SearchBibsAsync("title_author", "", "A title", "An author", 2, CancellationToken.None);

        Assert.AreEqual(1, result.Results.Count);
        Assert.AreEqual(9002, result.Results[0].BibId);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisStaffSearchPreservesCancellationBeforeTheProviderRequest()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"TotalRecordsFound":0,"BibSearchRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var canceled = false;
        try
        {
            await provider.SearchBibsAsync("title", "A title", "", "", 2, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert.IsTrue(canceled);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task StaffBibSearchEndpointReturnsUnavailableForProviderFailureAndNotFoundForDefinitiveEmpty()
    {
        using var bootstrapClient = factory!.CreateClient();
        using var bootstrapResponse = await bootstrapClient.GetAsync("/api/asap/staff/session");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedStaffBibLookupStateAsync(Guid.Parse(identity.TenantId!));
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":-1,"ErrorMessage":"SQL timeout","TotalRecordsFound":0,"BibSearchRows":[]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":-1,"TotalRecordsFound":0,"BibSearchRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);
        await using var searchFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        using var client = searchFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", seeded.Email);

        using var session = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        var input = new { requestId = seeded.RequestId.ToString(), mode = "title", query = "boundary" };
        using var unavailable = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", input);
        Assert.AreEqual(HttpStatusCode.BadGateway, unavailable.StatusCode);
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("bib_validation_unavailable", unavailableBody.RootElement.GetProperty("code").GetString());

        using var empty = await client.PostAsJsonAsync("/api/asap/staff/bib-lookup", input);
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
        using var emptyBody = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.AreEqual("not_found", emptyBody.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(0, emptyBody.RootElement.GetProperty("results").GetArrayLength());
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task StaffBibLookupReturnsVerifiedDetailWhenHoldingsFailAndStillReadsPatronHolds()
    {
        using var bootstrapClient = factory!.CreateClient();
        using var bootstrapResponse = await bootstrapClient.GetAsync("/api/asap/staff/session");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedStaffBibLookupStateAsync(Guid.Parse(identity.TenantId!));
        var handler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"BibGetRows":[{"ElementID":35,"Label":"Title","Value":"Verified title"}],"Title":"Verified title"}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","BibHoldingsGetRows":null}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);
        await using var lookupFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        using var client = lookupFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", seeded.Email);

        async Task<(string Title, string Status, int? BibId, byte[] RowVersion, int EventCount)> ReadRequestStateAsync()
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT r.[Title], r.[Status], r.[BibId], r.[RowVersion],
                       (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id])
                FROM [asap].[TitleRequest] r WHERE r.[Id] = @id;
                """;
            command.Parameters.AddWithValue("@id", seeded.RequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                (byte[])reader[3],
                reader.GetInt32(4));
        }

        var before = await ReadRequestStateAsync();
        using var session = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var response = await client.PostAsJsonAsync(
            "/api/asap/staff/bib-lookup",
            new { requestId = seeded.RequestId.ToString(), mode = "bib", bibId = "9001" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(9001, body.RootElement.GetProperty("bibId").GetInt32());
        Assert.AreEqual("Verified title", body.RootElement.GetProperty("title").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("holdingsUnavailable").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("holdingsSummary").ValueKind);
        Assert.IsFalse(body.RootElement.GetProperty("patronHasHold").GetBoolean());
        Assert.AreEqual(4, handler.RequestCount);
        Assert.IsTrue(handler.RequestUris.Any(uri => uri.AbsolutePath.Contains("/holdrequests/", StringComparison.Ordinal)));

        var after = await ReadRequestStateAsync();
        Assert.AreEqual(before.Title, after.Title);
        Assert.AreEqual(before.Status, after.Status);
        Assert.AreEqual(before.BibId, after.BibId);
        CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
        Assert.AreEqual(before.EventCount, after.EventCount);
    }

    [TestMethod]
    public async Task StaffBibLookupReturnsVerifiedDetailWithUnknownPatronHoldWhenHoldResponseIsMalformed()
    {
        using var bootstrapClient = factory!.CreateClient();
        using var bootstrapResponse = await bootstrapClient.GetAsync("/api/asap/staff/session");
        var identity = TestConfigurationFactory.Create().Authentication.Entra.InitialSuperAdmin;
        var seeded = await SeedStaffBibLookupStateAsync(Guid.Parse(identity.TenantId!));
        var handler = new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"BibGetRows":[{"ElementID":35,"Label":"Title","Value":"Verified title"}],"Title":"Verified title"}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":0,"BibHoldingsGetRows":[]}"""),
            (HttpStatusCode.OK,
             """{"PAPIErrorCode":-9,"papierrorcode":0,"PatronHoldRequestsGetRows":[]}"""));
        var provider = await CreatePolarisProviderAsync(handler);
        await using var lookupFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStaffPolarisProvider>();
                services.AddSingleton<IStaffPolarisProvider>(provider);
            }));
        using var client = lookupFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Id", seeded.StaffId.ToString());
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Tenant-Id", identity.TenantId);
        client.DefaultRequestHeaders.Add("X-ASAP-Test-Staff-Email", seeded.Email);

        async Task<(string Title, string Status, int? BibId, byte[] RowVersion, int EventCount, int OutboxCount, int HoldOperationCount)> ReadRequestStateAsync()
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT r.[Title], r.[Status], r.[BibId], r.[RowVersion],
                       (SELECT COUNT(*) FROM [asap].[TitleRequestEvent] e WHERE e.[TitleRequestId] = r.[Id]),
                       (SELECT COUNT(*) FROM [asap].[EmailOutbox] o WHERE o.[BusinessKey] LIKE N'%' + CONVERT(nvarchar(30), r.[Id]) + N'%'),
                       (SELECT COUNT(*) FROM [asap].[HoldPlacementOperation] h WHERE h.[TitleRequestId] = r.[Id])
                FROM [asap].[TitleRequest] r WHERE r.[Id] = @id;
                """;
            command.Parameters.AddWithValue("@id", seeded.RequestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                (byte[])reader[3],
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6));
        }

        var before = await ReadRequestStateAsync();
        using var session = await client.GetAsync("/api/asap/staff/session");
        Assert.AreEqual(HttpStatusCode.OK, session.StatusCode);
        using var sessionBody = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add(
            "X-ASAP-Antiforgery",
            sessionBody.RootElement.GetProperty("antiforgeryToken").GetString());

        using var response = await client.PostAsJsonAsync(
            "/api/asap/staff/bib-lookup",
            new { requestId = seeded.RequestId.ToString(), mode = "bib", bibId = "9001" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(9001, body.RootElement.GetProperty("bibId").GetInt32());
        Assert.AreEqual("Verified title", body.RootElement.GetProperty("title").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("holdingsUnavailable").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("patronHasHold").ValueKind);
        Assert.AreEqual(3, handler.RequestCount);

        var after = await ReadRequestStateAsync();
        Assert.AreEqual(before.Title, after.Title);
        Assert.AreEqual(before.Status, after.Status);
        Assert.AreEqual(before.BibId, after.BibId);
        CollectionAssert.AreEqual(before.RowVersion, after.RowVersion);
        Assert.AreEqual(before.EventCount, after.EventCount);
        Assert.AreEqual(before.OutboxCount, after.OutboxCount);
        Assert.AreEqual(before.HoldOperationCount, after.HoldOperationCount);
    }

    [TestMethod]
    public async Task PolarisPatronHoldsAcceptsValidEmptyAndPopulatedResponses()
    {
        var emptyProvider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[]}""")));
        var empty = await emptyProvider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);
        Assert.AreEqual(0, empty.Count);

        var populatedProvider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":51,"BibID":9001,"StatusID":6,"StatusDescription":"Held","PickupBranchID":101}]}""")));
        var populated = await populatedProvider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);
        Assert.AreEqual(1, populated.Count);
        Assert.AreEqual(51, populated[0].HoldRequestId);
        Assert.AreEqual(9001, populated[0].BibId);
        Assert.AreEqual(6, populated[0].StatusId);
        Assert.AreEqual("Held", populated[0].StatusDescription);
        Assert.IsFalse(HoldPlacementService.IsTerminal(populated[0].StatusId));
        Assert.AreEqual(101, populated[0].PickupBranchId);
        Assert.AreEqual("20000000000001", populated[0].PatronBarcode);
    }

    [TestMethod]
    public async Task PolarisPatronHoldsPreservesKnownTerminalStatusEvidence()
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":51,"BibID":9001,"StatusID":8,"StatusDescription":"Unclaimed"}]}""")));

        var holds = await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);

        Assert.AreEqual(8, holds.Single().StatusId);
        Assert.AreEqual("Unclaimed", holds.Single().StatusDescription);
        Assert.IsNull(holds.Single().PickupBranchId,
            "A terminal hold remains valid when this optional pickup field is omitted.");
        Assert.IsTrue(HoldPlacementService.IsTerminal(holds.Single().StatusId));
    }

    [TestMethod]
    public async Task PolarisPatronHoldsAcceptsCompatibleNumericStringIdentities()
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PatronHoldRequestsGetRows":[{"HoldRequestID":"51","BibID":"9001","StatusID":"6","StatusDescription":" Held "}]}""")));

        var holds = await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None);

        Assert.AreEqual(51, holds.Single().HoldRequestId);
        Assert.AreEqual(9001, holds.Single().BibId);
        Assert.AreEqual(6, holds.Single().StatusId);
        Assert.AreEqual("Held", holds.Single().StatusDescription);
        Assert.IsNull(holds.Single().PickupBranchId,
            "Compatible numeric-string identity fields do not make an omitted optional pickup fabricated evidence.");
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":\"0\",\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"failure\",\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-9,\"papierrorcode\":0,\"PatronHoldRequestsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":null}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":{}}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":0,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":0}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":-1,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":-1}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":\"bad\",\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":true}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51.5,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"holdrequestid\":51,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"holdrequestid\":52,\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"bibid\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001},{ }]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":0,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":2,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":\"bad\",\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6.5,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":null}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"  \"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"statusid\":6,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"statusid\":8,\"StatusDescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\",\"statusdescription\":\"Held\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\",\"statusdescription\":\"Active\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"PatronHoldRequestsGetRows\":[{\"HoldRequestID\":51,\"BibID\":9001,\"StatusID\":6,\"StatusDescription\":\"Held\"},{\"HoldRequestID\":52,\"BibID\":9002,\"StatusID\":3}]}")]
    public async Task PolarisPatronHoldsTreatsMalformedOrContradictoryResponsesAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new ProtectedSequenceResponseHandler(
            (HttpStatusCode.OK, content)));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetPatronHoldsAsync("20000000000001", 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibCarriesCatalogDetailsWithoutChangingRequestOptionSemantics()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"BibGetRows":[{"ElementID":35,"Label":"Title","Value":"Catalog title"},
              {"ElementID":18,"Label":"Author","Value":"Catalog author"},
              {"ElementID":2,"Label":"Publisher","Value":"Catalog publisher"},
              {"ElementID":6,"Label":"ISBN","Value":"9780000000001"},
              {"ElementID":17,"Label":"Format","Value":"Book"},
              {"ElementID":1,"Label":"Publication Date","Value":"2026"}],
             "Title":"Catalog title","Author":["Catalog author"],"Publisher":["Catalog publisher"],
             "Format":"Book","ISBN":"9780000000001"}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Catalog title", result.Title);
        Assert.AreEqual("Catalog author", result.Author);
        Assert.AreEqual("2026", result.Publication);
        Assert.AreEqual("Book", result.Format);
        Assert.AreEqual("9780000000001", result.Identifier);
        Assert.AreEqual("Catalog publisher", result.Publisher);
        StringAssert.Contains(handler.RequestPaths[0], "/bib/9001");
    }

    [TestMethod]
    public async Task PolarisExactBibAcceptsSparseRowWithoutOptionalMetadata()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"BibGetRows":[{"ElementID":13,"Value":"QA 76"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.Title);
        Assert.IsNull(result.Author);
        Assert.IsNull(result.Publication);
        Assert.IsNull(result.Format);
        Assert.IsNull(result.Identifier);
        Assert.IsNull(result.Publisher);
    }

    [TestMethod]
    public async Task PolarisExactBibClassifiesTheDocumentedInvalidBibResponseAsNotFound()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","BibGetRows":null}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.ValidateBibAsync(9001, 2, CancellationToken.None);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task PolarisExactBibDoesNotClassifyAmbiguousInvalidResponseAsNotFound()
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":-1,"ErrorMessage":"Invalid BibID","errormessage":"SQL timeout","BibGetRows":null}
            """));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibTreatsHttpFailureAsOperational()
    {
        var provider = await CreatePolarisProviderAsync(
            new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "{}"));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisExactBibPreservesCancellationBeforeLocalInputChecks()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "{}");
        var provider = await CreatePolarisProviderAsync(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.ValidateBibAsync(0, 2, cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(-9, "SQL timeout")]
    [DataRow(-1, "FAILURE - General")]
    public async Task PolarisExactBibTreatsOperationalPapiFailuresAsUnavailable(
        int papiErrorCode,
        string errorMessage)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            $$"""
            {"PAPIErrorCode":{{papiErrorCode}},"ErrorMessage":"{{errorMessage}}","BibGetRows":null}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[]}")]
    [DataRow("null")]
    public async Task PolarisExactBibTreatsMalformedOrMissingProtocolDataAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":0,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":\"35\",\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35.5,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":42}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":null}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"elementid\":0,\"Value\":\"Catalog title\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":\"Catalog title\"},{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibGetRows\":[{\"ElementID\":35,\"Value\":\"Catalog title\"}],\"bibgetrows\":[{}]}")]
    public async Task PolarisExactBibTreatsMalformedSuccessRowsAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));

        Assert.AreEqual(
            "polaris_bib_validation_protocol_failed",
            failure.Code,
            $"Original provider exception type: {failure.InnerException?.GetType().FullName ?? "<none>"}.");
    }

    [TestMethod]
    public async Task PolarisExactBibRejectsInconsistentRawPapiCodesAsUnavailable()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """
            {"PAPIErrorCode":0,"papierrorcode":-9,"BibGetRows":[{"Label":"Title","Value":"Catalog title"}]}
            """);
        var provider = await CreatePolarisProviderAsync(handler);

        var failure = await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.ValidateBibAsync(9001, 2, CancellationToken.None));
        Assert.AreEqual("polaris_bib_validation_protocol_failed", failure.Code);
    }

    [TestMethod]
    public async Task PolarisStaffHoldingsCountsOwningLibrariesAndReportsFailures()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK,
             """
             {"PAPIErrorCode":0,"BibHoldingsGetRows":[
               {"LocationID":"201","Holdable":"true"},
               {"LocationID":"202","Holdable":"false"},
               {"LocationID":"301","Holdable":"yes"}]}
             """),
            (HttpStatusCode.OK,
             """
             {"PAPIErrorCode":0,"OrganizationsGetRows":[
               {"OrganizationID":2,"OrganizationCodeID":2,"DisplayName":"My Library"},
               {"OrganizationID":201,"OrganizationCodeID":3,"ParentOrganizationID":2},
               {"OrganizationID":202,"OrganizationCodeID":3,"ParentOrganizationID":2},
               {"OrganizationID":3,"OrganizationCodeID":2,"DisplayName":"Other Library"},
               {"OrganizationID":301,"OrganizationCodeID":3,"ParentOrganizationID":3}]}
             """));
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None);

        Assert.AreEqual(2, result.MyLibraryCount);
        Assert.AreEqual(1, result.OtherLibraryCount);
        Assert.AreEqual(3, result.ConsortiumCount);
        Assert.IsTrue(result.IsHoldable);
        Assert.IsTrue(result.HasHoldableAtMyLibrary);
        Assert.AreEqual(2, handler.RequestUris.Count);
        StringAssert.Contains(handler.RequestUris[0].AbsolutePath, "/bib/9001/holdings");
        StringAssert.Contains(handler.RequestUris[1].AbsolutePath, "/organizations/");

        var failed = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, "{}"));
        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await failed.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    public async Task PolarisStaffHoldingsTreatsOnlyAWellFormedSuccessWithNoRowsAsZero()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK,
            """{"PAPIErrorCode":0,"ErrorMessage":"","BibHoldingsGetRows":[]}""");
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None);

        Assert.AreEqual(0, result.MyLibraryCount);
        Assert.AreEqual(0, result.OtherLibraryCount);
        Assert.AreEqual(0, result.ConsortiumCount);
        Assert.IsFalse(result.IsHoldable);
        Assert.IsFalse(result.HasHoldableAtMyLibrary);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"BibHoldingsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"Invalid BibID\",\"BibHoldingsGetRows\":null}")]
    public async Task PolarisStaffHoldingsTreatsEveryNegativePapiCodeAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    [DataRow("{\"PAPIErrorCode\":0}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":null}")]
    [DataRow("{\"PAPIErrorCode\":0,\"papierrorcode\":-9,\"BibHoldingsGetRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[],\"bibholdingsgetrows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"bad\",\"Holdable\":\"true\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"Holdable\":\"unknown\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"locationid\":\"202\",\"Holdable\":\"true\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibHoldingsGetRows\":[{\"LocationID\":\"201\",\"Holdable\":\"true\",\"holdable\":\"false\"}]}")]
    public async Task PolarisStaffHoldingsRejectsMalformedSuccessAsUnavailable(string content)
    {
        var provider = await CreatePolarisProviderAsync(new StaticResponseHandler(HttpStatusCode.OK, content));

        await Assert.ThrowsExactlyAsync<PolarisOperationalException>(async () =>
            await provider.GetBibHoldingsAsync(9001, 2, CancellationToken.None));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not-json")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"SQL timeout\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"ErrorMessage\":\"General failure\",\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":0,\"BibSearchRows\":[],\"papierrorcode\":0}")]
    [DataRow("{\"PAPIErrorCode\":-1,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":-9,\"TotalRecordsFound\":0,\"BibSearchRows\":[]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":0,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"totalrecordsfound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":2,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{\"ControlNumber\":9002},{\"ControlNumber\":9003}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":2,\"BibSearchRows\":[{\"ControlNumber\":9001},{\"ControlNumber\":\"bad\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":0}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":-1}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":1.5}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":\"bad\"}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"BibID\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9001}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"controlnumber\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibID\":9002}]}")]
    [DataRow("{\"PAPIErrorCode\":0,\"TotalRecordsFound\":1,\"BibSearchRows\":[{\"ControlNumber\":9001,\"BibliographicRecordID\":9002}]}")]
    public async Task PolarisIdentifierLookupRejectsSuccessfulMalformedProtocol(string content)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, content);
        var provider = await CreatePolarisProviderAsync(handler);

        var result = await provider.LookupIdentifierAsync("9780000000098", 2, CancellationToken.None);

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

        var result = await provider.LookupIdentifierAsync("9780000000097", 2, CancellationToken.None);

        Assert.AreEqual(IdentifierLookupOutcome.NotFound, result.Outcome);
        Assert.AreEqual(3, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(503, "{}")]
    [DataRow(429, "{}")]
    [DataRow(200, "{}")]
    [DataRow(200, "not-json")]
    [DataRow(200, "{\"PAPIErrorCode\":0}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"PatronID\":\"7001\"}")]
    [DataRow(200, "{\"PAPIErrorCode\":-1,\"PatronID\":7001,\"patronid\":7002}")]
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
                2, CancellationToken.None));

        var explicitSuccess = new ProtectedUpdateResponseHandler("{\"PAPIErrorCode\":0}");
        var successProvider = await CreatePolarisProviderAsync(explicitSuccess);
        await successProvider.UpdatePreferredPickupBranchAsync(
            "20000000000032",
            101,
            2, CancellationToken.None);

        Assert.AreEqual(1, missingCode.UpdateRequestCount);
        Assert.AreEqual(1, explicitSuccess.UpdateRequestCount);
        StringAssert.Contains(
            explicitSuccess.UpdateRequestPath,
            "/public/v1/1033/100/2/patron/20000000000032");
        using var body = JsonDocument.Parse(explicitSuccess.UpdateRequestBody);
        Assert.AreEqual(2, body.RootElement.GetProperty("LogonBranchId").GetInt32());
        Assert.AreEqual(42, body.RootElement.GetProperty("LogonUserId").GetInt32());
        Assert.AreEqual(99, body.RootElement.GetProperty("LogonWorkstationId").GetInt32());
        Assert.AreEqual(101, body.RootElement.GetProperty("RequestPickupBranchID").GetInt32());
    }

    [TestMethod]
    public async Task PolarisPickupBranchesMapTypedIdsAndOrganizationLabels()
    {
        var handler = new SequenceResponseHandler(
            (HttpStatusCode.OK, """{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":3},{"ID":4},{"ID":3}]}"""),
            (HttpStatusCode.OK, """
            {"PAPIErrorCode":2,"OrganizationsGetRows":[
              {"OrganizationID":3,"OrganizationCodeID":3,"DisplayName":"Zulu Branch"},
              {"OrganizationID":4,"OrganizationCodeID":3,"DisplayName":"Alpha Branch"}
            ]}
            """));
        var provider = await CreatePolarisProviderAsync(handler);
        var branches = await provider.GetPickupBranchesAsync(
            new PatronSnapshot(30, "20000000000033", "patron@example.org", "Test", "Patron",
                1, null, 77, 88, "Home Library", 4), 2, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 4, 3 }, branches.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "Alpha Branch", "Zulu Branch" }, branches.Select(item => item.Label).ToArray());
        Assert.AreEqual(2, handler.RequestCount);
        StringAssert.Contains(handler.RequestUris[0].AbsolutePath, "/public/v1/1033/100/77/pickupbranches");
        StringAssert.Contains(handler.RequestUris[1].AbsolutePath, "/organizations/");
    }

    [TestMethod]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"OrgID":3}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":0}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":1}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":-1}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":2147483648}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":[{"ID":3,"id":4}]}""")]
    [DataRow("""{"PAPIErrorCode":0,"PickupBranchesRows":{"PickupBranchRow":[{"ID":3}]}}""")]
    [DataRow("""{"PickupBranchesRows":[{"ID":3}]}""")]
    public async Task PolarisPickupBranchesRejectUnmodeledOrAmbiguousIdentity(string json)
    {
        var handler = new StaticResponseHandler(HttpStatusCode.OK, json);
        var provider = await CreatePolarisProviderAsync(handler);
        await Assert.ThrowsAsync<PolarisOperationalException>(() => provider.GetPickupBranchesAsync(
            new PatronSnapshot(30, "20000000000033", "patron@example.org", "Test", "Patron",
                1, null, 77, 88, "Home Library", 4), 2, CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount, "A malformed branch identity must not cause a context fallback.");
    }

    [TestMethod]
    public async Task PolarisPreferredPickupUsesRequestFieldOrRegisteredBranchWithoutRawAliasFallback()
    {
        var cases = new[]
        {
            (RequestField: "\"RequestPickupBranchID\":200,", Expected: (int?)200, State: PatronPickupPreferenceState.Current),
            (RequestField: string.Empty, Expected: (int?)300, State: PatronPickupPreferenceState.Absent),
            (RequestField: "\"RequestPickupBranchID\":0,", Expected: (int?)null, State: PatronPickupPreferenceState.ExplicitInvalid),
            (RequestField: "\"RequestPickupBranchID\":1,", Expected: (int?)null, State: PatronPickupPreferenceState.ExplicitInvalid)
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
            Assert.AreEqual(testCase.State, patron.EffectivePickupPreferenceState, testCase.RequestField);
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
            outboxId = Convert.ToInt64(await WithFixtureClock(command).ExecuteScalarAsync());
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
                    INSERT INTO [asap].[Organization]
                        ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                    VALUES (@id, N'Other sensitive library', N'OSL', 2, 1, 1);
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
        timeProvider!.SetUtcNow(timeProvider.GetUtcNow().AddMilliseconds(300));
        await transaction.CommitAsync();
        await delivery;

        Assert.AreEqual(0, sender.Envelopes.Count);
        var state = await ReadOutboxStateAsync(seeded.OutboxId);
        Assert.AreEqual("sending", state.Status);
        Assert.AreEqual("provider_start_deadline_exceeded", state.LastErrorCode);
    }

    [TestMethod]
    public async Task TimedOutOutboxIsQuarantinedAfterLeaseBoundaryWithoutReplay()
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
        var timedOut = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
        Assert.AreEqual("sending", timedOut.Status);
        Assert.AreEqual("provider_timeout", timedOut.LastErrorCode);
        Assert.AreEqual(1, timedOut.AttemptCount);
        Assert.IsNotNull(timedOut.LastAttemptUtc);
        Assert.IsNotNull(timedOut.SendingStartedUtc);
        Assert.IsNotNull(timedOut.LeaseId);
        Assert.IsNotNull(timedOut.LeaseExpiresUtc);

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        Assert.AreEqual(1, sender.CallCount, "A sending lease must not be claimed for an early retry.");

        await SetLeaseExpiryAsync(seeded.OutboxId, expired: false);
        await jobs.SweepAsync(CancellationToken.None);
        CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);
        Assert.AreEqual("sending", (await ReadOutboxStateAsync(seeded.OutboxId)).Status);

        await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
        var expired = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
        Assert.AreEqual("sending", expired.Status);
        Assert.AreEqual(timedOut.AttemptCount, expired.AttemptCount);
        Assert.AreEqual(timedOut.LastAttemptUtc, expired.LastAttemptUtc);
        Assert.AreEqual(timedOut.SendingStartedUtc, expired.SendingStartedUtc);
        Assert.AreEqual(timedOut.LeaseId, expired.LeaseId);
        Assert.AreEqual(timedOut.LastErrorCode, expired.LastErrorCode);
        Assert.AreEqual(timedOut.LastErrorDetail, expired.LastErrorDetail);
        await jobs.SweepAsync(CancellationToken.None);
        var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
        Assert.AreEqual("failed", quarantined.Status);
        Assert.AreEqual(expired.LastErrorCode, quarantined.LastErrorCode,
            "Quarantining an expired attempt preserves the original provider outcome evidence.");
        Assert.AreEqual(expired.LastErrorDetail, quarantined.LastErrorDetail);
        Assert.AreEqual(timedOut.AttemptCount, quarantined.AttemptCount);
        Assert.AreEqual(timedOut.LastAttemptUtc, quarantined.LastAttemptUtc);
        Assert.AreEqual(timedOut.SendingStartedUtc, quarantined.SendingStartedUtc);
        Assert.IsNull(quarantined.ProviderMessageId);
        Assert.IsNull(quarantined.NextAttemptUtc);
        Assert.IsNull(quarantined.LeaseId);
        Assert.IsNull(quarantined.LeaseExpiresUtc);
        CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);

        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        Assert.AreEqual(1, sender.CallCount, "An expired provider attempt with unknown outcome is never replayed.");
        AssertEmailOutcomeDispatchSnapshotEqual(
            quarantined,
            await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
    }

    [TestMethod]
    public async Task LateProviderSuccessCannotOverwriteQuarantinedExpiredLease()
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
        var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
        Assert.AreEqual("failed", quarantined.Status);
        Assert.AreEqual("ambiguous_expired_lease", quarantined.LastErrorCode);
        Assert.IsNull(quarantined.ProviderMessageId);
        Assert.AreEqual(1, quarantined.AttemptCount);
        CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);

        sender.CompleteFirstCall();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstDelivery);
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        AssertEmailOutcomeDispatchSnapshotEqual(
            quarantined,
            await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
        CollectionAssert.DoesNotContain(dispatcher.EnqueuedIds, seeded.OutboxId);
    }

    [TestMethod]
    public async Task LateNotConfiguredResultCannotOverwriteQuarantinedExpiredLease()
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
        var quarantined = await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId);
        Assert.AreEqual("failed", quarantined.Status);
        Assert.AreEqual("ambiguous_expired_lease", quarantined.LastErrorCode);
        Assert.AreEqual(1, quarantined.AttemptCount);
        CollectionAssert.DoesNotContain(dispatcher!.EnqueuedIds, seeded.OutboxId);

        sender.CompleteFirstCall(EmailSendResult.NotConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstDelivery);
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        AssertEmailOutcomeDispatchSnapshotEqual(
            quarantined,
            await ReadEmailOutcomeDispatchSnapshotAsync(seeded.OutboxId));
        CollectionAssert.DoesNotContain(dispatcher.EnqueuedIds, seeded.OutboxId);
    }

    [TestMethod]
    public async Task RecipientDomainPolicyIsAppliedAtIntentAndImmediatelyBeforeSend()
    {
        var blockedDispatcher = new RecordingOutboxDispatcher();
        var blockedSuggestionService = CreatePatronSuggestionService(
            allowedDomains: ["allowed.invalid"],
            blockedDispatcher);
        var blockedSession = await IssueTestPatronSessionAsync("20000000000040");
        var blockedResult = await blockedSuggestionService.CreateAsync(
            blockedSession,
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
        var allowedSession = await IssueTestPatronSessionAsync("20000000000041");
        var queuedResult = await allowedSuggestionService.CreateAsync(
            allowedSession,
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
        await DeleteTestPatronSessionAsync(blockedSession.Id);
        await DeleteTestPatronSessionAsync(allowedSession.Id);
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

        var session = await IssueTestPatronSessionAsync("20000000000050");
        var result = await service.CreateAsync(
            session,
            Suggestion($"Unavailable Transport {Guid.NewGuid():N}"),
            CancellationToken.None);

        var outboxId = await FindSubmissionOutboxIdAsync(result.Id);
        var outbox = await ReadOutboxStateAsync(outboxId);
        Assert.AreEqual("suppressed", outbox.Status);
        Assert.AreEqual("mail_not_configured", outbox.SuppressionReason);
        Assert.AreEqual(0, localDispatcher.EnqueuedIds.Count);
        await DeleteTestPatronSessionAsync(session.Id);
    }

    [TestMethod]
    public async Task TransportReadinessLostBeforeDeliveryFailsWithoutProviderCall()
    {
        var localDispatcher = new RecordingOutboxDispatcher();
        var sender = new MutableReadinessEmailSender(isConfigured: true);
        var service = CreatePatronSuggestionService(["example.org"], localDispatcher, sender);
        var session = await IssueTestPatronSessionAsync("20000000000051");
        var result = await service.CreateAsync(
            session,
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
        await DeleteTestPatronSessionAsync(session.Id);
    }

    [TestMethod]
    public async Task ReadinessTimeoutReleasesClaimWithoutSpendingProviderAttemptsAndLaterSends()
    {
        var seeded = await SeedSensitiveOutboxAsync("readiness-timeout");
        var sender = new MutableReadinessEmailSender(isConfigured: true)
        {
            ReadinessException = new TaskCanceledException("Synthetic dependency timeout")
        };
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);

        for (var index = 0; index < EmailOutboxRuntimeOptions.Default.MaxAttempts + 1; index++)
        {
            await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
            var state = await ReadPreSendStateAsync(seeded.OutboxId);
            Assert.AreEqual("pending", state.Status);
            Assert.AreEqual("mail_readiness_unavailable", state.LastErrorCode);
            Assert.AreEqual(0, state.AttemptCount);
            Assert.IsNull(state.LeaseId);
            Assert.IsNull(state.SendingStartedUtc);
            Assert.IsNull(state.LeaseExpiresUtc);
            Assert.IsTrue(state.NextAttemptUtc > DateTime.UtcNow.AddSeconds(30));
            Assert.AreEqual(0, sender.SendCount);
            await MakeOutboxDueAsync(seeded.OutboxId);
        }

        sender.ReadinessException = null;
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        var sent = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("sent", sent.Status);
        Assert.AreEqual(1, sent.AttemptCount);
        Assert.AreEqual(1, sender.SendCount);
    }

    [TestMethod]
    public async Task ReadinessDependencyExceptionAlsoReleasesClaim()
    {
        var seeded = await SeedSensitiveOutboxAsync("readiness-unavailable");
        var sender = new MutableReadinessEmailSender(isConfigured: true)
        {
            ReadinessException = new EmailOperationalException("Synthetic provider diagnostic")
        };
        await CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default)
            .DeliverAsync(seeded.OutboxId, CancellationToken.None);

        var state = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("pending", state.Status);
        Assert.AreEqual("mail_readiness_unavailable", state.LastErrorCode);
        Assert.AreEqual(0, state.AttemptCount);
        Assert.AreEqual(0, sender.SendCount);
        Assert.IsFalse(state.LastErrorDetail?.Contains("Synthetic", StringComparison.Ordinal) == true);
    }

    [TestMethod]
    public async Task CallerCancellationDuringReadinessPropagatesAndReleasesClaim()
    {
        var seeded = await SeedSensitiveOutboxAsync("readiness-caller-cancel");
        using var caller = new CancellationTokenSource();
        var sender = new ReadinessGateEmailSender();
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        var delivery = jobs.DeliverAsync(seeded.OutboxId, caller.Token);
        await sender.FirstCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await delivery);
        var state = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("pending", state.Status);
        Assert.AreEqual("pre_send_cancelled", state.LastErrorCode);
        Assert.AreEqual(0, state.AttemptCount);
        Assert.AreEqual(0, sender.SendCount);
    }

    [TestMethod]
    public async Task StaleReadinessFailureCannotOverwriteNewLeaseCompletion()
    {
        var seeded = await SeedSensitiveOutboxAsync("stale-readiness");
        var sender = new ReadinessGateEmailSender();
        var jobs = CreateEmailOutboxJobs(sender, EmailOutboxRuntimeOptions.Default);
        var firstDelivery = jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);
        await sender.FirstCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SetLeaseExpiryAsync(seeded.OutboxId, expired: true);
        await jobs.SweepAsync(CancellationToken.None);
        var reclaimed = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("pending", reclaimed.Status);
        Assert.AreEqual("mail_readiness_unavailable", reclaimed.LastErrorCode);
        Assert.AreEqual(0, reclaimed.AttemptCount);
        await MakeOutboxDueAsync(seeded.OutboxId);
        await jobs.DeliverAsync(seeded.OutboxId, CancellationToken.None);

        sender.FailFirstCheck();
        await firstDelivery;
        var state = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("sent", state.Status);
        Assert.AreEqual(1, state.AttemptCount);
        Assert.AreEqual(1, sender.SendCount);
    }

    [TestMethod]
    public async Task ExpiredProviderLeaseAtAttemptLimitFailsWithoutRecordedError()
    {
        var seeded = await SeedSensitiveOutboxAsync("expired-provider-limit");
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                UPDATE [asap].[EmailOutbox]
                SET [Status] = N'sending', [AttemptCount] = 5,
                    [SendingStartedUtc] = DATEADD(minute, -3, SYSUTCDATETIME()),
                    [LeaseId] = NEWID(), [LeaseExpiresUtc] = DATEADD(minute, -1, SYSUTCDATETIME()),
                    [LastErrorCode] = NULL
                WHERE [Id] = @id;
                """,
                connection);
            command.Parameters.AddWithValue("@id", seeded.OutboxId);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }

        await CreateEmailOutboxJobs(new MutableReadinessEmailSender(isConfigured: true),
            EmailOutboxRuntimeOptions.Default).SweepAsync(CancellationToken.None);
        var state = await ReadPreSendStateAsync(seeded.OutboxId);
        Assert.AreEqual("failed", state.Status);
        Assert.AreEqual("ambiguous_expired_lease", state.LastErrorCode);
        Assert.AreEqual(5, state.AttemptCount);
        Assert.IsNull(state.NextAttemptUtc);
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
        var duplicateId = duplicate.GetProperty("id");
        Assert.AreEqual(JsonValueKind.String, duplicateId.ValueKind);
        Assert.AreEqual(identifierRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), duplicateId.GetString());
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
        const int currentPatronId = 980011;
        var currentBarcode = $"349-cross-{Guid.NewGuid():N}";
        const string crossPatronNote = "Tagged as a duplicate suggestion because another patron has a suggestion with the same identifier number.";
        var legacyIdentifier = $"CROSS-PATRON-LEGACY-{Guid.NewGuid():N}";
        var reassignedIdentifier = $"CROSS-PATRON-REASSIGNED-{Guid.NewGuid():N}";
        var candidateIds = new List<long>();
        var requestIds = new List<long>();
        try
        {
            candidateIds.Add(await SeedDuplicateCandidateAsync("20000000000010", legacyIdentifier, null));

            var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            provider.AddPatron(
                new PatronSnapshot(currentPatronId, currentBarcode, "cross-patron@example.org", "Cross", "Patron",
                    1, "Adult", 101, 2, "Test Library", 101),
                [new PickupBranch(101, "Main Library")], 2);
            provider.SetIdentifierResult(legacyIdentifier, 2, new(IdentifierLookupOutcome.DefinitiveNotFound));
            provider.SetIdentifierResult(reassignedIdentifier, 2, new(IdentifierLookupOutcome.DefinitiveNotFound));
            using var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync(
                "/api/asap/patron/login",
                new { barcode = currentBarcode, pin = "1234", libraryOrgId = 2 });
            using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                loginDocument.RootElement.GetProperty("token").GetString());

            using var legacyResponse = await SubmitSuggestionAsync(client, legacyIdentifier, "Legacy Cross Patron Request");
            Assert.AreEqual(HttpStatusCode.Created, legacyResponse.StatusCode, await legacyResponse.Content.ReadAsStringAsync());
            var legacyRequestId = await ReadCreatedRequestIdAsync(legacyResponse);
            requestIds.Add(legacyRequestId);
            var legacyDuplicate = await ReadDuplicateResultAsync(legacyRequestId);
            CollectionAssert.Contains(legacyDuplicate.Notes.Split(["\r\n"], StringSplitOptions.None), crossPatronNote, legacyDuplicate.Notes);
            Assert.AreEqual(1, legacyDuplicate.DuplicateTagCount);
            Assert.AreEqual(currentPatronId, legacyDuplicate.PatronIdSnapshot);

            // A positive snapshot must also identify another patron when the barcode was later reused.
            candidateIds.Add(await SeedDuplicateCandidateAsync(currentBarcode, reassignedIdentifier, 980012));
            using var reassignedResponse = await SubmitSuggestionAsync(client, reassignedIdentifier, "Reassigned Barcode Request");
            Assert.AreEqual(HttpStatusCode.Created, reassignedResponse.StatusCode, await reassignedResponse.Content.ReadAsStringAsync());
            var reassignedRequestId = await ReadCreatedRequestIdAsync(reassignedResponse);
            requestIds.Add(reassignedRequestId);
            var reassignedDuplicate = await ReadDuplicateResultAsync(reassignedRequestId);
            CollectionAssert.Contains(reassignedDuplicate.Notes.Split(["\r\n"], StringSplitOptions.None), crossPatronNote, reassignedDuplicate.Notes);
            Assert.AreEqual(1, reassignedDuplicate.DuplicateTagCount);
            Assert.AreEqual(currentPatronId, reassignedDuplicate.PatronIdSnapshot);
        }
        finally
        {
            await using (var discover = new SqlConnection(databaseConnectionString))
            {
                await discover.OpenAsync();
                await using var findRequests = discover.CreateCommand();
                findRequests.CommandText =
                    "SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = 2 AND [Barcode] = @barcode " +
                    "AND [Title] IN (N'Legacy Cross Patron Request', N'Reassigned Barcode Request') " +
                    "AND [Identifier] IN (@legacy, @reassigned);";
                findRequests.Parameters.Add("@barcode", SqlDbType.NVarChar, 50).Value = currentBarcode;
                findRequests.Parameters.Add("@legacy", SqlDbType.NVarChar, 100).Value = legacyIdentifier;
                findRequests.Parameters.Add("@reassigned", SqlDbType.NVarChar, 100).Value = reassignedIdentifier;
                await using var rows = await findRequests.ExecuteReaderAsync();
                while (await rows.ReadAsync())
                {
                    var requestId = rows.GetInt64(0);
                    if (!requestIds.Contains(requestId))
                    {
                        requestIds.Add(requestId);
                    }
                }
            }
            await using (var cleanup = new SqlConnection(databaseConnectionString))
            {
                await cleanup.OpenAsync();
                await using var removeOutbox = cleanup.CreateCommand();
                removeOutbox.CommandText =
                    "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), @requestId);";
                removeOutbox.Parameters.Add("@requestId", SqlDbType.BigInt);
                foreach (var requestId in requestIds)
                {
                    removeOutbox.Parameters["@requestId"].Value = requestId;
                    await removeOutbox.ExecuteNonQueryAsync();
                }
            }
            foreach (var requestId in requestIds)
            {
                await DeleteRequestAsync(requestId);
            }
            await DeleteTestPatronSessionAsyncByBarcode(currentBarcode);
            if (candidateIds.Count > 0)
            {
                await using var cleanup = new SqlConnection(databaseConnectionString);
                await cleanup.OpenAsync();
                await using var removeCandidates = cleanup.CreateCommand();
                removeCandidates.CommandText = $"DELETE FROM [asap].[TitleRequest] WHERE [Id] IN ({string.Join(",", candidateIds.Select((_, index) => $"@candidate{index}"))});";
                for (var index = 0; index < candidateIds.Count; index++)
                {
                    removeCandidates.Parameters.AddWithValue($"@candidate{index}", candidateIds[index]);
                }
                await removeCandidates.ExecuteNonQueryAsync();
            }
        }

        async Task<long> SeedDuplicateCandidateAsync(string barcode, string identifier, int? patronIdSnapshot)
        {
            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO [asap].[TitleRequest]
                    ([LibraryOrganizationId], [PatronOrganizationId], [PatronIdSnapshot], [Barcode], [Title], [Author], [Identifier],
                     [PreferredPickupBranchId], [PreferredPickupBranchName], [LibraryNameSnapshot],
                     [AutoHold], [MaterialFormatId], [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
                OUTPUT INSERTED.[Id]
                VALUES
                    (2, 101, @patronIdSnapshot, @barcode, N'Existing Other Patron Request', N'Existing Author', @identifier,
                     101, N'Main Library', N'Test Library', 1,
                     (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                     N'suggestion', N'not_found', DATEADD(day, -1, SYSUTCDATETIME()), DATEADD(day, -1, SYSUTCDATETIME()));
                """;
            command.Parameters.Add("@patronIdSnapshot", SqlDbType.Int).Value = patronIdSnapshot is > 0
                ? patronIdSnapshot.Value
                : DBNull.Value;
            command.Parameters.Add("@barcode", SqlDbType.NVarChar, 50).Value = barcode;
            command.Parameters.Add("@identifier", SqlDbType.NVarChar, 100).Value = identifier;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        static Task<HttpResponseMessage> SubmitSuggestionAsync(HttpClient client, string identifier, string title) =>
            client.PostAsJsonAsync(
                "/api/asap/patron/suggestions",
                new
                {
                    format = "book",
                    title,
                    author = "New Author",
                    isbn = identifier,
                    publication = "Coming soon",
                    preferredPickupBranchId = 101,
                    autohold = true,
                    customFields = new Dictionary<string, string?>()
                });

        static async Task<long> ReadCreatedRequestIdAsync(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return long.Parse(document.RootElement.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }

        async Task<(string Notes, int DuplicateTagCount, int? PatronIdSnapshot)> ReadDuplicateResultAsync(long requestId)
        {
            await using var verify = new SqlConnection(databaseConnectionString);
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText =
                """
                SELECT request.[Notes], COUNT(tag.[Id]), request.[PatronIdSnapshot]
                FROM [asap].[TitleRequest] AS request
                LEFT JOIN [asap].[TitleRequestWorkflowTag] AS requestTag ON requestTag.[TitleRequestId] = request.[Id]
                LEFT JOIN [asap].[WorkflowTag] AS tag
                  ON tag.[Id] = requestTag.[WorkflowTagId] AND tag.[Code] = N'duplicate_suggestion'
                WHERE request.[Id] = @id
                GROUP BY request.[Notes], request.[PatronIdSnapshot];
                """;
            command.Parameters.AddWithValue("@id", requestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }
    }

    [TestMethod]
    public async Task WeeklyLimitUsesSevenBusinessCalendarDaysAcrossSpringGap()
    {
        int? originalLimit;
        string? originalMessage;
        DateTime originalUpdatedUtc;
        bool hadSettings;
        await using (var connection = new SqlConnection(databaseConnectionString))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT [SuggestionLimit], [SuggestionLimitMessage], [UpdatedUtc] FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2;";
            await using var reader = await read.ExecuteReaderAsync();
            hadSettings = await reader.ReadAsync();
            originalLimit = hadSettings && !reader.IsDBNull(0) ? reader.GetInt32(0) : null;
            originalMessage = hadSettings && !reader.IsDBNull(1) ? reader.GetString(1) : null;
            originalUpdatedUtc = hadSettings ? reader.GetDateTime(2) : default;
        }

        long? seededRequestId = null;
        long? submittedRequestId = null;
        var barcode = $"349-weekly-{Guid.NewGuid():N}";
        try
        {
            timeProvider!.SetUtcNow(new DateTimeOffset(2030, 3, 17, 6, 30, 0, TimeSpan.Zero));
            var provider = factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>();
            provider.AddPatron(
                new PatronSnapshot(989020, barcode, "weekly-limit@example.org", "Weekly", "Limit",
                    1, "Adult", 101, 2, "Test Library", 101),
                [new PickupBranch(101, "Main Library")], 2);
            provider.SetIdentifierResult("NEW-LIMIT-ID", 2, new(IdentifierLookupOutcome.DefinitiveNotFound));
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
                    OUTPUT INSERTED.[Id]
                    VALUES
                        (2, 101, @barcode, N'Outside Calendar Window', N'Test Author', N'OLD-LIMIT-ID',
                         101, N'Main Library', N'Test Library', 1,
                         (SELECT [Id] FROM [asap].[MaterialFormat] WHERE [OwnerOrganizationId] = 1 AND [Code] = N'book'),
                         N'suggestion', N'not_found', '2030-03-10T07:00:00Z', '2030-03-10T07:00:00Z');
                    """;
                command.Parameters.Add("@barcode", SqlDbType.NVarChar, 50).Value = barcode;
                seededRequestId = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            }

            using var client = factory!.CreateClient();
            var login = await client.PostAsJsonAsync(
                "/api/asap/patron/login",
                new { barcode, pin = "1234", libraryOrgId = 2 });
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
            using var submittedDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            submittedRequestId = long.Parse(
                submittedDocument.RootElement.GetProperty("id").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await DeleteTestPatronSessionAsyncByBarcode(barcode);
            var requestIds = new HashSet<long>(new[] { seededRequestId, submittedRequestId }.OfType<long>());
            await using (var discover = new SqlConnection(databaseConnectionString))
            {
                await discover.OpenAsync();
                await using var findRequests = discover.CreateCommand();
                findRequests.CommandText =
                    "SELECT [Id] FROM [asap].[TitleRequest] WHERE [LibraryOrganizationId] = 2 AND [Barcode] = @barcode " +
                    "AND [Identifier] IN (N'OLD-LIMIT-ID', N'NEW-LIMIT-ID');";
                findRequests.Parameters.Add("@barcode", SqlDbType.NVarChar, 50).Value = barcode;
                await using var rows = await findRequests.ExecuteReaderAsync();
                while (await rows.ReadAsync())
                {
                    requestIds.Add(rows.GetInt64(0));
                }
            }
            foreach (var requestId in requestIds)
            {
                await using (var cleanup = new SqlConnection(databaseConnectionString))
                {
                    await cleanup.OpenAsync();
                    await using var removeOutbox = new SqlCommand(
                        "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = N'patron-submission:' + CONVERT(nvarchar(40), @id);",
                        cleanup);
                    removeOutbox.Parameters.AddWithValue("@id", requestId);
                    await removeOutbox.ExecuteNonQueryAsync();
                }
                await DeleteRequestAsync(requestId);
            }
            await using var restore = new SqlConnection(databaseConnectionString);
            await restore.OpenAsync();
            await using var command = restore.CreateCommand();
            command.CommandText = hadSettings
                ? "UPDATE [asap].[WorkflowSettings] SET [SuggestionLimit] = @limit, [SuggestionLimitMessage] = @message, [UpdatedUtc] = @updated WHERE [OrganizationId] = 2;"
                : "DELETE FROM [asap].[WorkflowSettings] WHERE [OrganizationId] = 2;";
            if (hadSettings)
            {
                command.Parameters.Add("@limit", SqlDbType.Int).Value = originalLimit is { } limit ? limit : DBNull.Value;
                command.Parameters.Add("@message", SqlDbType.NVarChar, -1).Value = originalMessage is { } message ? message : DBNull.Value;
                command.Parameters.Add("@updated", SqlDbType.DateTime2).Value = originalUpdatedUtc;
            }
            await command.ExecuteNonQueryAsync();
        }
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

    private async Task<OperatorResolutionSeed> SeedOperatorResolutionAsync(
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
                    93100 + ABS(CHECKSUM(NEWID())) % 800,
                    101, N'Main Library', N'found', DATEADD(minute, -8, SYSUTCDATETIME()), DATEADD(minute, -8, SYSUTCDATETIME()));
            DECLARE @requestId bigint = SCOPE_IDENTITY();
            DECLARE @barcode nvarchar(100) = (SELECT [Barcode] FROM [asap].[TitleRequest] WHERE [Id] = @requestId);
            DECLARE @bibId int = (SELECT [BibId] FROM [asap].[TitleRequest] WHERE [Id] = @requestId);
            INSERT INTO [asap].[HoldPlacementOperation]
                ([TitleRequestId], [PatronBarcodeSnapshot], [PatronIdSnapshot], [BibIdSnapshot], [PickupBranchIdSnapshot],
                 [RequestingOrganizationIdSnapshot], [WorkstationIdSnapshot], [PolarisUserIdSnapshot],
                 [AttemptNumber], [State], [Phase], [OwnerToken], [ExecutionEpoch], [LeaseExpiresUtc], [RequestStartedUtc],
                 [CreateStartedUtc], [CreateResponseObservedUtc], [ReplyStartedUtc], [PolarisRequestGuid],
                 [TxnGroupQualifier], [TxnQualifier], [ReplyAnswer], [ReplyState], [ResultCode], [OutcomeEvidenceKind],
                 [RecoveryAttemptCount], [LastRecoveryUtc], [LastErrorCode], [DetailJson])
            VALUES
                (@requestId, @barcode, 7105, @bibId, 101, 2, 1, 1,
                 1, N'operator_required', @phase,
                 CASE WHEN @liveOwner = 1 THEN NEWID() ELSE NULL END, 2,
                 CASE WHEN @liveOwner = 1 THEN DATEADD(minute, 2, SYSUTCDATETIME()) ELSE NULL END,
                 DATEADD(minute, -7, SYSUTCDATETIME()),
                 CASE WHEN @phase IN (N'create_started', N'reply_started') THEN DATEADD(minute, -6, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN DATEADD(minute, -5, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN DATEADD(minute, -4, SYSUTCDATETIME()) ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN NEWID() ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'group-qualifier' ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN N'transaction-qualifier' ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN 1 ELSE NULL END,
                 CASE WHEN @phase = N'reply_started' THEN 3 ELSE NULL END,
                 N'ambiguous', N'provider_transport_ambiguous', 1, DATEADD(minute, -3, SYSUTCDATETIME()),
                 N'provider_timeout', N'{"providerObservation":{"result":"ambiguous","source":"CLC"}}');
            DECLARE @operationId bigint = SCOPE_IDENTITY();
            SELECT @requestId, @operationId, request.[RowVersion], operation.[RowVersion], request.[Barcode]
            FROM [asap].[TitleRequest] request
            JOIN [asap].[HoldPlacementOperation] operation ON operation.[Id] = @operationId
            WHERE request.[Id] = @requestId;
            """;
        seed.Parameters.AddWithValue("@suffix", suffix);
        seed.Parameters.AddWithValue("@phase", phase);
        seed.Parameters.AddWithValue("@liveOwner", liveOwner);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        factory!.Services.GetRequiredService<DeterministicTestingPatronProvider>().AddPatron(
            new PatronSnapshot(7105, reader.GetString(4), "operator@example.org", "Test", "Operator",
                1, "Adult", 101, 2, "Test Library", 101), [new(101, "Main Library"), new(102, "North Branch")], 2);
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

    private static async Task<long> SeedPendingHoldRequestAsync(
        string title,
        string barcode,
        int bibId,
        int? patronOrganizationId = 101)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            DECLARE @formatId bigint = (
                SELECT TOP (1) [Id] FROM [asap].[MaterialFormat] WHERE [Code] = N'book');
            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [PatronOrganizationId], [Barcode], [Title], [AutoHold], [MaterialFormatId], [Status], [BibId], [BibIdStaffVerified],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES (2, @patronOrganizationId, @barcode, @title, 1, @formatId, N'pending_hold', @bibId, 1,
                    101, N'Main Library', N'found', SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT CONVERT(bigint, SCOPE_IDENTITY());
            """,
            connection);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@barcode", barcode);
        command.Parameters.AddWithValue("@bibId", bibId);
        command.Parameters.AddWithValue("@patronOrganizationId", (object?)patronOrganizationId ?? DBNull.Value);
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
        int requestBibId,
        string? operationBarcode = null,
        int? operationBibId = null,
        int? holdRequestId = null,
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

    private async Task<LifecycleClaimRaceSeed> SeedLifecycleClaimRaceAsync(
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
        await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
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

    private async Task<LifecycleAutoClaimRaceSeed> SeedLifecycleAutoClaimRaceAsync(string suffix)
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
        await using var reader = await WithFixtureClock(seed).ExecuteReaderAsync();
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
                 @formatId, N'pending_hold', CASE WHEN @suffix = N'workflow' THEN 95001 ELSE 100000 + ABS(CHECKSUM(@suffix)) % 100000000 END, N'Original source note ' + @suffix,
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
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (83, N'Additional Copy Race Library', N'ACR', 2, 1, 1);
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
                (2, N'Test Library snapshot', 97003, N'Race task ' + @suffix, @formatId, N'book',
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
                (2, N'Test Library snapshot', 97002, N'Candidate-change task', @formatId, N'book',
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

    private static async Task<SeededStaffBibLookupState> SeedStaffBibLookupStateAsync(Guid tenantId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"bib.lookup.{suffix}@example.org";
        var barcode = $"20000000{suffix[..8]}";
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
                 [DisplayName], [NotificationEmail], [Role], [OrganizationId], [IsActive],
                 [WeeklyActionSummaryEnabled], [PurchaseReminderDefault], [AdditionalCopyReminderDefault],
                 [DefaultMineUnclaimedFilter])
            VALUES
                (@tenantId, @objectId, @email, UPPER(@email), N'BIB Lookup Test Staff', @email, N'staff', 2, 1,
                 0, 0, 0, 0);
            DECLARE @staffId bigint = SCOPE_IDENTITY();

            INSERT INTO [asap].[TitleRequest]
                ([LibraryOrganizationId], [Barcode], [Title], [Identifier], [AutoHold], [MaterialFormatId],
                 [Status], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (2, @barcode, N'BIB lookup boundary test', N'9780000000091', 1, @formatId,
                 N'suggestion', N'not_found', SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT @staffId, CONVERT(bigint, SCOPE_IDENTITY());
            """;
        seed.Parameters.AddWithValue("@tenantId", tenantId);
        seed.Parameters.AddWithValue("@objectId", objectId);
        seed.Parameters.AddWithValue("@email", email);
        seed.Parameters.AddWithValue("@barcode", barcode);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new SeededStaffBibLookupState(reader.GetInt64(0), reader.GetInt64(1), email);
    }

    private async Task<SeededStaffBrowserState> SeedStaffBrowserStateAsync(
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
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (82, N'Other Browser Library', N'OBR', 2, 1, 1);
            END;

            UPDATE [asap].[SystemSettings]
            SET [LeapBibUrlPattern] = N'https://leap.example.test/bib/{{bibid}}',
                [LeapPatronUrlPattern] = N'https://leap.example.test/patron/{{patron-id}}'
            WHERE [OrganizationId] = 1;
            UPDATE [asap].[ExternalSearchProvider]
            SET [Label] = N'Browser vendor', [UrlTemplate] = N'https://search.example.test/find?q={{title}}',
                [IsEnabled] = 1
            WHERE [ProviderKey] = N'external_search_1';
            UPDATE [asap].[ExternalSearchProvider]
            SET [UrlTemplate] = N'https://search.example.test/id?q={{identifier}}', [IsEnabled] = 1
            WHERE [ProviderKey] = N'external_search_3';
            INSERT INTO [asap].[ExternalSearchProviderOverride]
                ([LibraryOrganizationId], [ExternalSearchProviderId], [IsEnabled])
            SELECT 2, [Id], 0 FROM [asap].[ExternalSearchProvider]
            WHERE [ProviderKey] = N'external_search_2';

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

            DECLARE @previousTitleIdentity bigint = CONVERT(bigint, IDENT_CURRENT(N'[asap].[TitleRequest]'));
            DECLARE @primaryId bigint = CONVERT(bigint, '9007199254741993');
            SET IDENTITY_INSERT [asap].[TitleRequest] ON;
            INSERT INTO [asap].[TitleRequest]
                ([Id], [LegacyId], [LibraryOrganizationId], [Barcode], [Email], [NameFirst], [NameLast], [Title],
                 [Author], [Identifier], [Publication], [CustomFieldsJson], [AutoHold], [MaterialFormatId], [Status], [BibId],
                 [PreferredPickupBranchId], [PreferredPickupBranchName], [ClaimedByStaffUserId],
                 [ClaimedByDisplayName], [ClaimedAtUtc], [ClaimType], [IsbnCheckStatus], [CreatedUtc], [UpdatedUtc])
            VALUES
                (@primaryId, @legacyId, 2, N'20000000002901', N'browser.patron@example.org', N'Browser', N'Patron',
                 N'Browser staff title', N'Browser Author', N'9780000002901', N'Historical browser publication',
                 N'{"audience_note":{"label":"Audience note","type":"text","value":"Historic audience"},"binding":{"label":"Binding","type":"select","value":"historic","displayValue":"Historic binding"},"retired":{"label":"Retired field","type":"text","value":"Keep me"}}', 1,
                 @formatId, N'suggestion', NULL, 101, N'Main Library', @superId, N'Initial Administrator',
                 SYSUTCDATETIME(), N'manual', N'not_found', SYSUTCDATETIME(), SYSUTCDATETIME());
            SET IDENTITY_INSERT [asap].[TitleRequest] OFF;
            DECLARE @reseedSql nvarchar(200) = N'DBCC CHECKIDENT (''[asap].[TitleRequest]'', RESEED, '
                + CONVERT(nvarchar(30), @previousTitleIdentity) + N') WITH NO_INFOMSGS';
            EXEC sp_executesql @reseedSql;
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
        await using var result = await WithFixtureClock(seed).ExecuteReaderAsync();
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

    private sealed record SeededStaffBibLookupState(long StaffId, long RequestId, string Email);

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
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (2, N'Test Library', N'TEST', 2, 1, 1);
            INSERT INTO [asap].[Organization]
                ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
            VALUES (101, N'Main Library Branch', N'MAIN', 3, 2, 0);
            UPDATE [asap].[PolarisSettings]
            SET [WorkstationId] = 99, [SystemPolarisUserId] = 42 WHERE [OrganizationId] = 1;
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

    private async Task<PolarisPatronProvider> CreatePolarisProviderAsync(
        HttpMessageHandler handler,
        string accessId = "test-access",
        ILogger<PolarisPatronProvider>? cleanupLogger = null)
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
                    [AccessId] = @accessId,
                    [ProtectedApiKey] = @apiKey,
                    [StaffDomain] = N'TEST',
                    [AdminUser] = N'test-admin',
                    [ProtectedAdminPassword] = @password,
                    [WorkstationId] = 99,
                    [SystemPolarisUserId] = 42,
                    [UpdatedUtc] = SYSUTCDATETIME()
                WHERE [OrganizationId] = 1;
                """;
            command.Parameters.AddWithValue("@accessId", accessId);
            command.Parameters.AddWithValue("@apiKey", protector.Protect("test-api-key"));
            command.Parameters.AddWithValue("@password", protector.Protect("test-password"));
            await command.ExecuteNonQueryAsync();
        }

        return new PolarisPatronProvider(
            services.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
            protector,
            new SingleClientFactory(new HttpClient(handler)),
            services.GetRequiredService<TimeProvider>(),
            cleanupLogger ?? services.GetRequiredService<ILogger<PolarisPatronProvider>>());
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
            UPDATE [asap].[Organization]
            SET [IsActive] = 1
            WHERE [Id] = 2 AND @authorizationOrganizationId = 2;
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
        await using var reader = await WithFixtureClock(command).ExecuteReaderAsync();
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
            NullLogger<EmailOutboxJobs>.Instance, timeProvider!);
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
            DeterministicJourneyScenario.Create(),
            outboxDispatcher,
            emailSender ?? new RecordingEmailSender(),
            new RecipientDomainPolicy(configuration),
            timeProvider!,
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
        var values = new List<int>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetInt32(0));
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
                SET [DisplayName] = @name, [Abbreviation] = @abbreviation,
                    [OrganizationCodeId] = 2, [ParentOrganizationId] = 1, [IsActive] = 1
                WHERE [Id] = @id;
            ELSE
                INSERT INTO [asap].[Organization]
                    ([Id], [DisplayName], [Abbreviation], [OrganizationCodeId], [ParentOrganizationId], [IsActive])
                VALUES (@id, @name, @abbreviation, 2, 1, 1);
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

    private static async Task<PreSendOutboxState> ReadPreSendStateAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT [Status], [LastErrorCode], [LastErrorDetail], [AttemptCount], [NextAttemptUtc], " +
            "[SendingStartedUtc], [LeaseId], [LeaseExpiresUtc] FROM [asap].[EmailOutbox] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", outboxId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new PreSendOutboxState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7));
    }

    private async Task MakeOutboxDueAsync(long outboxId)
    {
        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "UPDATE [asap].[EmailOutbox] SET [NextAttemptUtc] = DATEADD(second, -1, SYSUTCDATETIME()) WHERE [Id] = @id;",
            connection);
        command.Parameters.AddWithValue("@id", outboxId);
        Assert.AreEqual(1, await WithFixtureClock(command).ExecuteNonQueryAsync());
    }

    private sealed record PreSendOutboxState(
        string Status,
        string? LastErrorCode,
        string? LastErrorDetail,
        int AttemptCount,
        DateTime? NextAttemptUtc,
        DateTime? SendingStartedUtc,
        Guid? LeaseId,
        DateTime? LeaseExpiresUtc);

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

    private sealed record PatronCodeRows(int SetCount, int[] Values);

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
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>(
                [new(1, "System", null, 1, null), new(2, "Test Library", "Test", 2, 1)]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken) =>
            throw new PolarisOperationalException(
                "testing_patron_codes_failure",
                "Testing patron-code reference failure.");
    }

    private sealed class LockProbePatronCodeReferenceProvider(string connectionString) : IPolarisReferenceProvider
    {
        public bool LockProbeSucceeded { get; private set; }

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, 1));

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>([]);

        public async Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(
            CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [Id] FROM [asap].[Organization] WITH (UPDLOCK, NOWAIT) WHERE [Id] = 2;";
            await command.ExecuteScalarAsync(cancellationToken);
            LockProbeSucceeded = true;
            return [new PolarisPatronCodeSnapshot(1, "Adult")];
        }
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
                1,
                null,
                300,
                2,
                "Test Library",
                300));

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken) =>
            AuthenticateAsync(barcode, string.Empty, cancellationToken);

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([new PickupBranch(200, "Allowed Branch")]);

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));
    }

    private sealed class ControllablePickupPatronProvider : IPatronProvider
    {
        public int? CurrentPickupBranchId { get; set; } = 101;
        public bool IncludeEastBranch { get; set; }
        public int RefreshCount { get; private set; }
        public int UpdateCount { get; private set; }
        public bool FailUpdate { get; set; }
        public Exception? RefreshException { get; set; }
        public Exception? PickupBranchesException { get; set; }
        public Action<CancellationToken>? BeforeRefresh { get; set; }
        public Action<CancellationToken>? BeforePickupBranches { get; set; }
        public Action? AfterUpdate { get; set; }
        public Action? BeforePickupBranchesReturn { get; set; }
        public Action? BeforeIdentifierLookup { get; set; }

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => RefreshAsync(barcode, 2, cancellationToken);

        public Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            RefreshCount++;
            BeforeRefresh?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (RefreshException is not null)
            {
                throw RefreshException;
            }
            return Task.FromResult(new PatronSnapshot(
                7004,
                barcode,
                "pickup@example.org",
                "Pickup",
                "Patron",
                1,
                "Adult",
                101,
                2,
                "Test Library",
                CurrentPickupBranchId));
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId, CancellationToken cancellationToken)
        {
            BeforePickupBranches?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (PickupBranchesException is not null)
            {
                throw PickupBranchesException;
            }
            BeforePickupBranchesReturn?.Invoke();
            return Task.FromResult<IReadOnlyList<PickupBranch>>(IncludeEastBranch
                ? [new PickupBranch(101, "Main Library"), new PickupBranch(102, "North Branch"),
                   new PickupBranch(103, "East Branch")]
                : [new PickupBranch(101, "Main Library"), new PickupBranch(102, "North Branch")]);
        }

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCount++;
            if (FailUpdate)
            {
                throw new PolarisOperationalException("testing_pickup_failure", "Testing pickup failure.");
            }
            CurrentPickupBranchId = pickupBranchId;
            AfterUpdate?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeIdentifierLookup?.Invoke();
            return Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));
        }
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
        public HoldReplyCommand? LastReplyCommand { get; private set; }
        public int HoldReadCount { get; private set; }
        public IReadOnlyList<PolarisHoldSnapshot> Holds { get; set; } = [];
        public Exception? HoldReadException { get; set; }
        public Exception? CreateException { get; set; }
        public Exception? ReplyException { get; set; }
        public PolarisHoldSnapshot? HoldAfterCreate { get; set; }
        public TaskCompletionSource CreateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HoldProviderResult>? PendingCreate { get; private set; }
        public TaskCompletionSource HoldReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PendingHoldRead { get; private set; }
        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PendingRefresh { get; private set; }
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
                    8123,
                    "group-qualifier",
                    "transaction-qualifier",
                    2,
                    1,
                    "documented_reply_success"));
            provider.createResult = provider.createResult with { RequestGuid = provider.RequestGuid };
            provider.replyResult = provider.replyResult with { RequestGuid = provider.RequestGuid };
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

        public void BlockHoldRead() => PendingHoldRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedHoldRead() => PendingHoldRead!.TrySetResult();

        public void BlockRefresh() => PendingRefresh = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedRefresh() => PendingRefresh!.TrySetResult();

        public void CompleteBlockedCreateWithConfiguredResult() => PendingCreate!.TrySetResult(createResult);

        public void CompleteBlockedCreate(HoldProviderResult result) =>
            PendingCreate!.TrySetResult(result);

        public void BlockReply() => PendingReply = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteBlockedReply(HoldProviderResult result) =>
            PendingReply!.TrySetResult(result);

        public Task<PatronSnapshot> AuthenticateAsync(
            string barcode,
            string pin,
            CancellationToken cancellationToken) => RefreshAsync(barcode, 2, cancellationToken);

        public async Task<PatronSnapshot> RefreshAsync(string barcode, int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PendingRefresh is not null)
            {
                RefreshStarted.TrySetResult();
                await PendingRefresh.Task.WaitAsync(cancellationToken);
            }
            var registrationOrganizationId = organizationId == 2 ? 101 : organizationId;
            return new PatronSnapshot(
                7105,
                barcode,
                "hold-patron@example.org",
                "Hold",
                "Patron",
                1,
                "Adult",
                registrationOrganizationId,
                organizationId > 1 ? organizationId : 2,
                "Test Library",
                registrationOrganizationId);
        }

        public Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
            PatronSnapshot patron,
            int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PickupBranch>>([
                new PickupBranch(patron.PatronOrganizationId, "Registered Branch")]);

        public Task UpdatePreferredPickupBranchAsync(
            string barcode,
            int pickupBranchId,
            int organizationId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IdentifierLookupResult> LookupIdentifierAsync(
            string identifier,
            int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new IdentifierLookupResult(IdentifierLookupOutcome.NotFound));

        public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new BibValidationResult(true));

        public async Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HoldReadCount++;
            if (HoldReadException is not null)
            {
                throw HoldReadException;
            }
            if (PendingHoldRead is not null)
            {
                HoldReadStarted.TrySetResult();
                await PendingHoldRead.Task.WaitAsync(cancellationToken);
            }
            return Holds;
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
            LastReplyCommand = command;
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

        public bool OperationalFailure { get; set; }

        public bool ValidBib { get; set; }

        public Exception? ValidationException { get; set; }

        public Exception? SearchException { get; set; }

        public Action<CancellationToken>? BeforeValidation { get; set; }

        public Action<CancellationToken>? BeforeSearch { get; set; }

        public int SearchCount { get; private set; }

        public Task<IReadOnlyList<PatronSnapshot>> SearchPatronsAsync(
            string query,
            int organizationId, CancellationToken cancellationToken)
        {
            SearchCount++;
            BeforeSearch?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SearchException is not null)
            {
                throw SearchException;
            }

            throw new NotSupportedException();
        }

        public Task<BibValidationResult> ValidateBibAsync(int bibId, int organizationId, CancellationToken cancellationToken)
        {
            ValidationCount++;
            BeforeValidation?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ValidationException is not null)
            {
                throw ValidationException;
            }

            if (OperationalFailure)
            {
                throw new PolarisOperationalException("polaris_bib_validation_failed", "Polaris is unavailable.");
            }

            return Task.FromResult(new BibValidationResult(ValidBib));
        }

        public Task<IReadOnlyList<PolarisHoldSnapshot>> GetPatronHoldsAsync(
            string barcode,
            int organizationId, CancellationToken cancellationToken) =>
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

        public bool TimeoutReadiness { get; set; }

        public Exception? ReadinessException { get; set; }

        public int SendCount { get; private set; }

        public bool ReturnNotConfiguredOnSend { get; init; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TimeoutReadiness)
            {
                throw new OperationCanceledException("Email readiness timed out without caller cancellation.");
            }
            if (ReadinessException is not null)
            {
                throw ReadinessException;
            }
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

    private sealed class ReadinessGateEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource<EmailTransportReadiness> firstCheck =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int checkCount;

        public TaskCompletionSource FirstCheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount { get; private set; }

        public Task<EmailTransportReadiness> CheckReadinessAsync(
            int organizationId,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref checkCount) == 1)
            {
                FirstCheckStarted.TrySetResult();
                return firstCheck.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(EmailTransportReadiness.Configured);
        }

        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new EmailSendResult("new-owner-message"));
        }

        public void FailFirstCheck() => firstCheck.TrySetException(new TaskCanceledException("Synthetic stale timeout"));
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
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestUris.Add(request.RequestUri);
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
        public List<Uri> RequestUris { get; } = [];
        public Action<int, CancellationToken>? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUris.Add(request.RequestUri!);
            OnRequest?.Invoke(RequestCount, cancellationToken);
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

    private sealed class ProtectedSequenceResponseHandler(
        params (HttpStatusCode StatusCode, string Content)[] responses) : HttpMessageHandler
    {
        private const string ProtectedTokenContent =
            "{\"PAPIErrorCode\":0,\"AccessToken\":\"protected-token\",\"AccessSecret\":\"protected-secret\",\"AuthExpDate\":\"2030-01-01T00:00:00Z\"}";
        private readonly Queue<(HttpStatusCode StatusCode, string Content)> remaining = new(responses);

        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = [];
        public Exception? ProtectedRequestException { get; set; }
        public Action<CancellationToken>? BeforeProtectedRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            RequestUris.Add(request.RequestUri!);
            var isAuthentication = request.RequestUri!.AbsolutePath.Contains(
                "/authenticator/staff",
                StringComparison.OrdinalIgnoreCase);
            if (isAuthentication)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ProtectedTokenContent),
                    RequestMessage = request
                });
            }

            BeforeProtectedRequest?.Invoke(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ProtectedRequestException is not null)
            {
                throw ProtectedRequestException;
            }

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

    // SQL fixture creation and application mutations share the same deterministic clock.
    private SqlCommand WithFixtureClock(SqlCommand command)
    {
        if (command.CommandText.Contains("SYSUTCDATETIME()", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = command.CommandText.Replace(
                "SYSUTCDATETIME()", "@fixtureUtc", StringComparison.OrdinalIgnoreCase);
            if (command.Parameters.Contains("@fixtureUtc"))
            {
                command.Parameters["@fixtureUtc"].Value = timeProvider!.GetUtcNow().UtcDateTime;
            }
            else
            {
                command.Parameters.Add("@fixtureUtc", System.Data.SqlDbType.DateTime2).Value = timeProvider!.GetUtcNow().UtcDateTime;
            }
        }
        return command;
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
