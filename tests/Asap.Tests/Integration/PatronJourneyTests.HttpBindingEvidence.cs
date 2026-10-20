using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task TitleRequestsAliasCreatesStaffSuggestionWithPersistedAttribution()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var barcode = "20000000001401";
        var titleSuffix = Convert.ToUInt64(Guid.NewGuid().ToString("N")[..16], 16)
            .ToString(CultureInfo.InvariantCulture);
        var title = $"Title Request Alias {titleSuffix}";
        using var client = await CreateAuthenticatedStaffClientAsync(factory!, actor);

        try
        {
            using var response = await client.PostAsJsonAsync("/api/asap/staff/title-requests", new
            {
                libraryOrgId = 2,
                barcode,
                format = "book",
                title,
                author = "Alias Test Author",
                publication = "Already published",
                preferredPickupBranchId = 101,
                currentPreferredPickupBranchIdAtLoad = 101,
                currentPreferredPickupBranchObservedAtLoad = true,
                autohold = false,
                emailPatronConfirmation = false,
                customFields = new Dictionary<string, string?>()
            });

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var idElement = body.RootElement.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, idElement.ValueKind);
            Assert.IsTrue(long.TryParse(idElement.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out _));
            Assert.AreEqual(2, body.RootElement.GetProperty("libraryOrgId").GetInt32());

            await using var connection = new SqlConnection(databaseConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT r.[Id], r.[LibraryOrganizationId], r.[StaffLibraryOrganizationIdCreatedBy], r.[Barcode],
                       r.[Title], r.[Status], e.[ActorType], e.[StaffUserId], e.[EventType],
                       (SELECT COUNT(*) FROM [asap].[TitleRequest] match WHERE match.[Title] = @title AND match.[Barcode] = @barcode)
                FROM [asap].[TitleRequest] r
                JOIN [asap].[TitleRequestEvent] e ON e.[TitleRequestId] = r.[Id] AND e.[EventType] = N'created'
                WHERE r.[Title] = @title AND r.[Barcode] = @barcode;
                """,
                connection);
            command.Parameters.AddWithValue("@title", title);
            command.Parameters.AddWithValue("@barcode", barcode);
            await using var result = await command.ExecuteReaderAsync();
            Assert.IsTrue(await result.ReadAsync());
            Assert.AreEqual(idElement.GetString(), result.GetInt64(0).ToString(CultureInfo.InvariantCulture));
            Assert.AreEqual(2, result.GetInt32(1));
            Assert.AreEqual(2, result.GetInt32(2));
            Assert.AreEqual(barcode, result.GetString(3));
            Assert.AreEqual(title, result.GetString(4));
            Assert.AreEqual("suggestion", result.GetString(5));
            Assert.AreEqual("staff", result.GetString(6));
            Assert.AreEqual(actor.Id, result.GetInt64(7));
            Assert.AreEqual("created", result.GetString(8));
            Assert.AreEqual(1, result.GetInt32(9));
            Assert.IsFalse(await result.ReadAsync());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[TitleRequestWorkflowTag] WHERE [TitleRequestId] IN (SELECT [Id] FROM [asap].[TitleRequest] WHERE [Title] = @title AND [Barcode] = @barcode); " +
                "DELETE FROM [asap].[TitleRequestEvent] WHERE [TitleRequestId] IN (SELECT [Id] FROM [asap].[TitleRequest] WHERE [Title] = @title AND [Barcode] = @barcode); " +
                "DELETE FROM [asap].[TitleRequest] WHERE [Title] = @title AND [Barcode] = @barcode;",
                ("@title", title), ("@barcode", barcode));
        }
    }

    [TestMethod]
    public async Task LibraryLogoHttpRoutesUploadAndDeleteWithVersionedScopeAndEffectiveReload()
    {
        await AssertLibraryLogoImageClearPreservesAltTextAsync(multipartClear: false);
    }

    [TestMethod]
    public async Task LibraryLogoMultipartImageClearPreservesIndependentAltText()
    {
        await AssertLibraryLogoImageClearPreservesAltTextAsync(multipartClear: true);
    }

    private async Task AssertLibraryLogoImageClearPreservesAltTextAsync(bool multipartClear)
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var auditBefore = await ReadAuditHighWatermarkAsync();
        BrandingSnapshot? original;
        await using (var snapshot = await contextFactory.CreateDbContextAsync())
        {
            original = await snapshot.Branding.AsNoTracking()
                .Where(item => item.OrganizationId == 2)
                .Select(item => new BrandingSnapshot(
                    item.LogoData, item.LogoContentType, item.LogoFileName, item.LogoAltText, item.UpdatedUtc))
                .SingleOrDefaultAsync();
        }

        using var client = await CreateAuthenticatedStaffClientAsync(factory!, actor);
        try
        {
            using var initialLibrary = await ReadSettingsDocumentAsync(client, "2");
            using var initialSystem = await ReadSettingsDocumentAsync(client, "system");
            var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

            using var multipart = new MultipartFormDataContent();
            using var image = new ByteArrayContent(gif);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
            multipart.Add(image, "logo", "journey.gif");
            multipart.Add(new StringContent("Library logo from multipart route"), "logoAlt");
            multipart.Add(new StringContent(initialLibrary.RootElement.GetProperty("version").GetString()!), "version");

            using var uploaded = await client.PostAsync("/api/asap/staff/settings/logo?orgId=2", multipart);
            Assert.AreEqual(HttpStatusCode.OK, uploaded.StatusCode, await uploaded.Content.ReadAsStringAsync());
            using var uploadBody = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            Assert.AreEqual("branding_saved", uploadBody.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(2, uploadBody.RootElement.GetProperty("data").GetProperty("organizationId").GetInt32());
            Assert.IsTrue(uploadBody.RootElement.GetProperty("data").GetProperty("hasLogo").GetBoolean());

            await using (var saved = await contextFactory.CreateDbContextAsync())
            {
                var branding = await saved.Branding.AsNoTracking().SingleAsync(item => item.OrganizationId == 2);
                CollectionAssert.AreEqual(gif, branding.LogoData);
                Assert.AreEqual("image/gif", branding.LogoContentType);
                Assert.AreEqual("journey.gif", branding.LogoFileName);
                Assert.AreEqual("Library logo from multipart route", branding.LogoAltText);
            }

            using var afterUpload = await ReadSettingsDocumentAsync(client, "2");
            Assert.AreEqual(uploadBody.RootElement.GetProperty("data").GetProperty("version").GetString(),
                afterUpload.RootElement.GetProperty("version").GetString());
            Assert.IsTrue(afterUpload.RootElement.GetProperty("effective").GetProperty("hasLogo").GetBoolean());
            Assert.AreEqual("Library logo from multipart route",
                afterUpload.RootElement.GetProperty("effective").GetProperty("logoAltText").GetString());

            var deleteUrl = "/api/asap/staff/settings/logo?orgId=2";
            using var clearForm = new MultipartFormDataContent();
            clearForm.Add(new StringContent("true"), "clearLogo");
            clearForm.Add(new StringContent(afterUpload.RootElement.GetProperty("version").GetString()!), "version");
            using var deleteRequest = multipartClear
                ? new HttpRequestMessage(HttpMethod.Post, deleteUrl) { Content = clearForm }
                : new HttpRequestMessage(HttpMethod.Delete, deleteUrl + "&version=" +
                    Uri.EscapeDataString(afterUpload.RootElement.GetProperty("version").GetString()!));
            using var delete = await client.SendAsync(deleteRequest);
            Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode, await delete.Content.ReadAsStringAsync());
            using var deleteBody = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
            Assert.AreEqual("branding_saved", deleteBody.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(deleteBody.RootElement.GetProperty("data").GetProperty("hasLogo").GetBoolean());

            await using (var cleared = await contextFactory.CreateDbContextAsync())
            {
                var branding = await cleared.Branding.AsNoTracking().SingleAsync(item => item.OrganizationId == 2);
                Assert.IsNull(branding.LogoData);
                Assert.IsNull(branding.LogoContentType);
                Assert.IsNull(branding.LogoFileName);
                Assert.AreEqual("Library logo from multipart route", branding.LogoAltText,
                    "An image-only clear must preserve the independently owned alt-text override.");
            }

            using var afterDelete = await ReadSettingsDocumentAsync(client, "2");
            var systemLogo = initialSystem.RootElement.GetProperty("stored").GetProperty("configuredSystem")
                .GetProperty("branding");
            Assert.AreEqual(systemLogo.GetProperty("hasLogo").GetBoolean(),
                afterDelete.RootElement.GetProperty("effective").GetProperty("hasLogo").GetBoolean());
            Assert.AreEqual("Library logo from multipart route",
                afterDelete.RootElement.GetProperty("effective").GetProperty("logoAltText").GetString());

            using var clearAlt = new MultipartFormDataContent();
            clearAlt.Add(new StringContent(string.Empty), "logoAlt");
            clearAlt.Add(new StringContent(afterDelete.RootElement.GetProperty("version").GetString()!), "version");
            using var altCleared = await client.PostAsync(deleteUrl, clearAlt);
            Assert.AreEqual(HttpStatusCode.OK, altCleared.StatusCode, await altCleared.Content.ReadAsStringAsync());
            await using var noOverrides = await contextFactory.CreateDbContextAsync();
            Assert.IsNull(await noOverrides.Branding.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == 2),
                "Explicitly clearing the last independent override removes the empty library row.");
        }
        finally
        {
            await using var restore = await contextFactory.CreateDbContextAsync();
            var row = await restore.Branding.SingleOrDefaultAsync(item => item.OrganizationId == 2);
            if (original is null)
            {
                if (row is not null)
                {
                    restore.Branding.Remove(row);
                }
            }
            else
            {
                row ??= new Branding { OrganizationId = 2 };
                row.LogoData = original.LogoData?.ToArray();
                row.LogoContentType = original.LogoContentType;
                row.LogoFileName = original.LogoFileName;
                row.LogoAltText = original.LogoAltText;
                row.UpdatedUtc = original.UpdatedUtc;
                if (restore.Entry(row).State == EntityState.Detached)
                {
                    restore.Branding.Add(row);
                }
            }
            await restore.SaveChangesAsync();
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[AdministrativeAudit] WHERE [Id] > @auditBefore;",
                ("@auditBefore", auditBefore));
        }
    }

    [TestMethod]
    public async Task PolarisTestHttpEndpointMapsDeterministicProviderResultsWithoutSqlWrites()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var provider = new DeterministicReferenceTestProvider(new PolarisConnectionTestResult(true, 7));
        await using var providerFactory = CreateApplicationFactory(configurationPath, provider);
        using var client = await CreateAuthenticatedStaffClientAsync(providerFactory, actor);
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var beforeBusinessRows = await ReadOperationalAuthoritySqlSnapshotAsync(contextFactory);
        using var beforeSettings = await ReadSettingsDocumentAsync(client, "system");

        using var connected = await client.PostAsync("/api/asap/staff/polaris/test", content: null);
        Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode, await connected.Content.ReadAsStringAsync());
        using var connectedBody = JsonDocument.Parse(await connected.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_connected", connectedBody.RootElement.GetProperty("code").GetString());
        Assert.IsTrue(connectedBody.RootElement.GetProperty("data").GetProperty("connected").GetBoolean());
        Assert.AreEqual(7, connectedBody.RootElement.GetProperty("data").GetProperty("organizationCount").GetInt32());

        provider.Result = new PolarisConnectionTestResult(false, 0, "test_provider_unavailable");
        using var unavailable = await client.PostAsync("/api/asap/staff/polaris/test", content: null);
        Assert.AreEqual(HttpStatusCode.BadGateway, unavailable.StatusCode, await unavailable.Content.ReadAsStringAsync());
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("polaris_unavailable", unavailableBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("test_provider_unavailable",
            unavailableBody.RootElement.GetProperty("data").GetProperty("errorCode").GetString());
        Assert.AreEqual(2, provider.TestConnectionCount);

        using var afterSettings = await ReadSettingsDocumentAsync(client, "system");
        Assert.AreEqual(beforeSettings.RootElement.GetProperty("version").GetString(),
            afterSettings.RootElement.GetProperty("version").GetString());
        Assert.AreEqual(beforeBusinessRows, await ReadOperationalAuthoritySqlSnapshotAsync(contextFactory));
    }

    [TestMethod]
    public async Task EmailTestHttpQueryQueuesOneStoredDeliveryAndReplaysSameIntent()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        var operationId = Guid.NewGuid();
        var businessKey = $"operational-test:2:{actor.Id}:{operationId:N}";
        await using var emailFactory = factory!.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailOutboxDispatcher>();
                services.AddSingleton<IEmailOutboxDispatcher>(serviceProvider =>
                    new EmailOutboxDispatcher(serviceProvider.GetRequiredService<IBackgroundJobClient>()));
            }));
        using var client = await CreateAuthenticatedStaffClientAsync(emailFactory, actor);
        var storage = emailFactory.Services.GetRequiredService<JobStorage>();
        var savedEmailContext = await ReadSavedEmailContextAsync(client, 2);
        long? outboxId = null;

        try
        {
            using var first = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId=2&operationId={operationId:D}",
                SavedEmailInput(savedEmailContext));
            Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.AreEqual("queued", firstBody.RootElement.GetProperty("code").GetString());
            var firstData = firstBody.RootElement.GetProperty("data");
            var returnedId = firstData.GetProperty("id");
            Assert.AreEqual(JsonValueKind.String, returnedId.ValueKind);
            Assert.IsFalse(firstData.GetProperty("dispatchDelayed").GetBoolean());
            Assert.IsTrue(long.TryParse(returnedId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId));
            outboxId = parsedId;

            using var replay = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId=2&operationId={operationId:D}",
                SavedEmailInput(savedEmailContext));
            Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode, await replay.Content.ReadAsStringAsync());
            using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.AreEqual("queued", replayBody.RootElement.GetProperty("code").GetString());
            var replayData = replayBody.RootElement.GetProperty("data");
            Assert.AreEqual(returnedId.GetString(), replayData.GetProperty("id").GetString());
            Assert.IsTrue(replayData.GetProperty("replayed").GetBoolean());

            var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            await using (var context = await contextFactory.CreateDbContextAsync())
            {
                var rows = await context.EmailOutbox.AsNoTracking()
                    .Where(item => item.BusinessKey == businessKey)
                    .ToArrayAsync();
                Assert.HasCount(1, rows);
                Assert.AreEqual(parsedId, rows[0].Id);
                Assert.AreEqual(2, rows[0].OrganizationId);
                Assert.AreEqual("operational_test", rows[0].DeliveryClass);
                Assert.AreEqual("pending", rows[0].Status);
            }

            var matchingJobs = ReadQueuedJobIds(storage, "asap-email")
                .Select(jobId => (Id: jobId, Job: ReadEnqueuedHangfireJob(storage, jobId)))
                .Where(item => item.Job.Type == typeof(EmailOutboxJobs) &&
                               item.Job.Method.Name == nameof(EmailOutboxJobs.DeliverAsync) &&
                               Convert.ToInt64(item.Job.Args[0], CultureInfo.InvariantCulture) == parsedId)
                .ToArray();
            Assert.HasCount(1, matchingJobs);
            var storedJob = matchingJobs[0].Job;
            Assert.AreEqual(2, storedJob.Args.Count);
            Assert.AreEqual(parsedId, Convert.ToInt64(storedJob.Args[0], CultureInfo.InvariantCulture));
            Assert.AreEqual(CancellationToken.None, (CancellationToken)storedJob.Args[1]);
            CollectionAssert.Contains(ReadQueuedJobIds(storage, "asap-email"), matchingJobs[0].Id);
            var sender = (RecordingEmailSender)emailFactory.Services.GetRequiredService<IEmailSender>();
            Assert.HasCount(0, sender.Envelopes);
        }
        finally
        {
            var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
            if (!outboxId.HasValue)
            {
                await using var context = await contextFactory.CreateDbContextAsync();
                outboxId = await context.EmailOutbox.AsNoTracking()
                    .Where(item => item.BusinessKey == businessKey)
                    .Select(item => (long?)item.Id)
                    .SingleOrDefaultAsync();
            }
            if (outboxId.HasValue)
            {
                var jobIds = ReadQueuedJobIds(storage, "asap-email")
                    .Where(jobId =>
                    {
                        var job = ReadEnqueuedHangfireJob(storage, jobId);
                        return job.Type == typeof(EmailOutboxJobs) &&
                               job.Method.Name == nameof(EmailOutboxJobs.DeliverAsync) &&
                               Convert.ToInt64(job.Args[0], CultureInfo.InvariantCulture) == outboxId.Value;
                    })
                    .ToArray();
                DeleteHangfireJobs(emailFactory.Services.GetRequiredService<IBackgroundJobClient>(), jobIds);
            }
            await ExecuteNonQueryAsync(
                "DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey); " +
                "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey;",
                ("@businessKey", businessKey));
        }
    }

    [TestMethod]
    public async Task EmailTestHttpMapsScopeInactiveReadinessAndSuppressionOutcomes()
    {
        var actor = await ReadConfiguredSuperAdminAsync();
        using var client = await CreateAuthenticatedStaffClientAsync(factory!, actor);
        var savedEmailContext = await ReadSavedEmailContextAsync(client, 2);
        var rejectedOutboxCount = await ReadEmailOutboxCountAsync(
            factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>());

        using (var forbidden = await client.PostAsJsonAsync(
                   $"/api/asap/staff/email-operations/test?organizationId=0&operationId={Guid.NewGuid():D}",
                   SavedEmailInput(savedEmailContext)))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode, await forbidden.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
            Assert.AreEqual("staff_scope_forbidden", body.RootElement.GetProperty("code").GetString());
        }

        using (var emptyGuid = await client.PostAsJsonAsync(
                   $"/api/asap/staff/email-operations/test?organizationId=2&operationId={Guid.Empty:D}",
                   SavedEmailInput(savedEmailContext)))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, emptyGuid.StatusCode, await emptyGuid.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await emptyGuid.Content.ReadAsStringAsync());
            Assert.AreEqual("operation_id_invalid", body.RootElement.GetProperty("code").GetString());
        }

        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        bool originalActive;
        await using (var context = await contextFactory.CreateDbContextAsync())
        {
            var organization = await context.Organizations.SingleAsync(item => item.Id == 2);
            originalActive = organization.IsActive;
            organization.IsActive = false;
            await context.SaveChangesAsync();
        }
        try
        {
            using var inactive = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId=2&operationId={Guid.NewGuid():D}",
                SavedEmailInput(savedEmailContext));
            Assert.AreEqual(HttpStatusCode.Conflict, inactive.StatusCode, await inactive.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await inactive.Content.ReadAsStringAsync());
            Assert.AreEqual("organization_inactive", body.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await ExecuteNonQueryAsync(
                "UPDATE [asap].[Organization] SET [IsActive] = @active WHERE [Id] = 2;",
                ("@active", originalActive));
        }
        Assert.AreEqual(rejectedOutboxCount, await ReadEmailOutboxCountAsync(
            factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>()));

        var suppressedOperationId = Guid.NewGuid();
        var suppressedBusinessKey = $"operational-test:2:{actor.Id}:{suppressedOperationId:N}";
        await using (var suppressedFactory = factory!.WithWebHostBuilder(builder =>
                         builder.ConfigureServices(services =>
                         {
                             services.RemoveAll<IEmailSender>();
                             services.AddSingleton<IEmailSender>(new ReadinessSender(
                                 _ => Task.FromResult(EmailTransportReadiness.NotConfigured)));
                         })))
        {
            using var suppressedClient = await CreateAuthenticatedStaffClientAsync(suppressedFactory, actor);
            try
            {
                var blockedContext = await ReadSavedEmailContextAsync(suppressedClient, 2);
                using var suppressed = await suppressedClient.PostAsJsonAsync(
                    $"/api/asap/staff/email-operations/test?organizationId=2&operationId={suppressedOperationId:D}",
                    SavedEmailInput(blockedContext));
                Assert.AreEqual(HttpStatusCode.Conflict, suppressed.StatusCode, await suppressed.Content.ReadAsStringAsync());
                using var body = JsonDocument.Parse(await suppressed.Content.ReadAsStringAsync());
                Assert.AreEqual("live_delivery_disabled", body.RootElement.GetProperty("code").GetString());

                await using var context = await contextFactory.CreateDbContextAsync();
                Assert.IsFalse(await context.EmailOutbox.AnyAsync(item => item.BusinessKey == suppressedBusinessKey),
                    "An explicitly live test cannot silently become a non-delivery or unavailable transport intent.");
            }
            finally
            {
                await ExecuteNonQueryAsync(
                    "DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey); " +
                    "DELETE FROM [asap].[EmailOutbox] WHERE [BusinessKey] = @businessKey;",
                    ("@businessKey", suppressedBusinessKey));
            }
        }

        var beforeReadinessFailure = await ReadEmailOutboxCountAsync(contextFactory);
        await using (var unavailableFactory = factory!.WithWebHostBuilder(builder =>
                         builder.ConfigureServices(services =>
                         {
                             services.RemoveAll<IEmailSender>();
                             services.AddSingleton<IEmailSender>(new ReadinessSender(
                                 _ => Task.FromException<EmailTransportReadiness>(
                                     new EmailOperationalException("Deterministic readiness failure."))));
                         })))
        {
            using var unavailableClient = await CreateAuthenticatedStaffClientAsync(unavailableFactory, actor);
            using var unavailable = await unavailableClient.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId=2&operationId={Guid.NewGuid():D}",
                SavedEmailInput(savedEmailContext));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode,
                await unavailable.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
            Assert.AreEqual("email_transport_unavailable", body.RootElement.GetProperty("code").GetString());
        }
        Assert.AreEqual(beforeReadinessFailure, await ReadEmailOutboxCountAsync(contextFactory));
    }

    private static async Task<HttpClient> CreateAuthenticatedStaffClientAsync(
        WebApplicationFactory<Program> applicationFactory,
        CurrentStaff actor)
    {
        var client = applicationFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        try
        {
            AddTestingStaffHeaders(client, actor.Id, actor.EntraTenantId, actor.AuthenticationEmail);
            client.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(client));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string[] ReadQueuedJobIds(JobStorage storage, string queue) =>
        storage.GetMonitoringApi().EnqueuedJobs(queue, 0, 1000)
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static async Task<int> ReadEmailOutboxCountAsync(IDbContextFactory<AsapDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.EmailOutbox.CountAsync();
    }

    private sealed record BrandingSnapshot(
        byte[]? LogoData,
        string? LogoContentType,
        string? LogoFileName,
        string? LogoAltText,
        DateTime UpdatedUtc);

    private sealed class DeterministicReferenceTestProvider(PolarisConnectionTestResult result) : IPolarisReferenceProvider
    {
        public PolarisConnectionTestResult Result { get; set; } = result;

        public int TestConnectionCount { get; private set; }

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken)
        {
            TestConnectionCount++;
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisOrganizationSnapshot>>([]);

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>([]);
    }
}
