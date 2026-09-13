using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asap.Tests.Sql;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Asap.Web.Infrastructure.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.Dac;

namespace Asap.Tests.Integration;

[TestClass]
[DoNotParallelize]
public sealed class PatronJourneyTests
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

    private WebApplicationFactory<Program> CreateApplicationFactory(string settingsPath) =>
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
            });
        });

    [TestCleanup]
    public async Task StopApplication()
    {
        if (factory is not null)
        {
            await factory.DisposeAsync();
        }
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
                     [RecipientEntraTenantId], [RecipientEntraObjectId], [AuthorizationOrganizationId],
                     [RecipientAddressKind], [ToAddress], [FromAddress], [Subject], [BodyText],
                     [Status], [NextAttemptUtc], [CreatedUtc])
                OUTPUT inserted.[Id]
                SELECT 2, @businessKey, N'staff_authorization_sensitive', [Id],
                       @tenantId, @objectId, 2, N'notification_email',
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

    private async Task<SeededSensitiveOutbox> SeedSensitiveOutboxAsync(string key)
    {
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var objectId = Guid.NewGuid();
        await using var connection = new SqlConnection(databaseConnectionString);
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
                (@tenantId, @objectId, @email, UPPER(@email), N'Outbox Staff', @email, N'staff', 2, 1);

            INSERT INTO [asap].[EmailOutbox]
                ([OrganizationId], [BusinessKey], [DeliveryClass], [RecipientStaffUserId],
                 [RecipientEntraTenantId], [RecipientEntraObjectId], [AuthorizationOrganizationId],
                 [RecipientAddressKind], [ToAddress], [FromAddress], [Subject], [BodyText],
                 [Status], [NextAttemptUtc], [CreatedUtc])
            OUTPUT inserted.[Id], inserted.[RecipientStaffUserId]
            SELECT 2, @businessKey, N'staff_authorization_sensitive', [Id],
                   @tenantId, @objectId, 2, N'notification_email',
                   @email, N'asap@example.org', N'Sensitive update', N'Body',
                   N'pending', SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM @staff;
            """;
        var email = $"outbox-{key}-{Guid.NewGuid():N}@example.org";
        command.Parameters.AddWithValue("@tenantId", tenantId);
        command.Parameters.AddWithValue("@objectId", objectId);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@businessKey", $"sensitive-{key}:{Guid.NewGuid():N}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new SeededSensitiveOutbox(reader.GetInt64(0), reader.GetInt64(1));
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

    private sealed record SeededSensitiveOutbox(long OutboxId, long StaffUserId);

    private sealed record EmailOutboxState(
        string Status,
        string? LastErrorCode,
        string? ProviderMessageId,
        string? SuppressionReason);

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
