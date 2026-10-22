using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SavedEmailTestHttpRequiresContextAndDeduplicatesConcurrentClicksBeforeCooldown()
    {
        const int scope = 382930;
        var staff = await CreateSavedEmailActorAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        using var client = await CreateAuthenticatedStaffClientAsync(factory!, actor);
        await SeedSavedEmailScopeAsync(scope);
        try
        {
            var saved = await ReadSavedEmailContextAsync(client, scope);
            Assert.AreEqual("primary-test@example.org", saved.GetProperty("recipient").GetString());
            Assert.AreEqual("Saved library sender", saved.GetProperty("fromName").GetString());
            Assert.AreEqual("asap@example.org", saved.GetProperty("fromAddress").GetString(), "Sender fields inherit independently.");
            using var idless = await client.PostAsJsonAsync($"/api/asap/staff/email-operations/test?organizationId={scope}", SavedEmailInput(saved));
            Assert.AreEqual(HttpStatusCode.BadRequest, idless.StatusCode);
            using var contextless = await client.PostAsync($"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", null);
            Assert.AreEqual(HttpStatusCode.BadRequest, contextless.StatusCode);
            Assert.AreEqual(0, await CountSavedEmailRowsAsync(scope));

            var operationId = Guid.NewGuid();
            var url = $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={operationId:D}";
            var requests = await Task.WhenAll(client.PostAsJsonAsync(url, SavedEmailInput(saved)), client.PostAsJsonAsync(url, SavedEmailInput(saved)));
            try
            {
                Assert.IsTrue(requests.All(item => item.StatusCode == HttpStatusCode.Accepted));
                using var first = JsonDocument.Parse(await requests[0].Content.ReadAsStringAsync());
                using var replay = JsonDocument.Parse(await requests[1].Content.ReadAsStringAsync());
                var id = first.RootElement.GetProperty("data").GetProperty("id").GetString()!;
                Assert.AreEqual(id, replay.RootElement.GetProperty("data").GetProperty("id").GetString());
                Assert.AreEqual(1, await CountSavedEmailRowsAsync(scope));
                Assert.HasCount(1, dispatcher!.EnqueuedIds);

                using var cooldown = await client.PostAsJsonAsync(
                    $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved));
                Assert.AreEqual(HttpStatusCode.TooManyRequests, cooldown.StatusCode, await cooldown.Content.ReadAsStringAsync());
                using var cooldownJson = JsonDocument.Parse(await cooldown.Content.ReadAsStringAsync());
                Assert.IsTrue(cooldownJson.RootElement.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32() > 0);
                Assert.AreEqual(1, await CountSavedEmailRowsAsync(scope));

                var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
                await using (var context = await contexts.CreateDbContextAsync())
                {
                    var row = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.OrganizationId == scope);
                    Assert.AreEqual(staff.Id, row.TestRequestedByStaffUserId);
                    Assert.AreEqual("postmark", row.IntendedDeliveryMode);
                    Assert.AreEqual(64, row.TestConfigurationVersion!.Length);
                    Assert.IsNull(row.AttemptDeliveryMode);
                    StringAssert.Contains(row.BodyText!, operationId.ToString("N"));
                    StringAssert.Contains(row.BodyText!, $"({scope})");
                    StringAssert.Contains(row.BodyText!, "UTC");
                    Assert.AreEqual("primary-test@example.org", row.ToAddress);
                }
                var jobs = factory.Services.GetRequiredService<EmailOutboxJobs>();
                await jobs.DeliverAsync(long.Parse(id, CultureInfo.InvariantCulture), CancellationToken.None);
                await jobs.DeliverAsync(long.Parse(id, CultureInfo.InvariantCulture), CancellationToken.None);
                var sender = (RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>();
                Assert.HasCount(1, sender.Envelopes, "Worker retries of a recorded result never send twice.");
                using var byId = await client.GetAsync($"/api/asap/staff/email-operations/{id}?organizationId={scope}");
                Assert.AreEqual(HttpStatusCode.OK, byId.StatusCode);
                using var status = JsonDocument.Parse(await byId.Content.ReadAsStringAsync());
                Assert.AreEqual("sent", status.RootElement.GetProperty("data").GetProperty("status").GetString());
                Assert.AreEqual("postmark", status.RootElement.GetProperty("data").GetProperty("deliveryMode").GetString());
                Assert.IsFalse(status.RootElement.GetProperty("data").TryGetProperty("toAddress", out _));
                Assert.IsFalse(status.RootElement.GetProperty("data").TryGetProperty("bodyText", out _));
                using var byClick = await client.GetAsync($"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={operationId:D}");
                using var byClickJson = JsonDocument.Parse(await byClick.Content.ReadAsStringAsync());
                Assert.AreEqual(id, byClickJson.RootElement.GetProperty("data").GetProperty("id").GetString());
                using var wrongScope = await client.GetAsync($"/api/asap/staff/email-operations/{id}?organizationId=2");
                Assert.AreEqual(HttpStatusCode.NotFound, wrongScope.StatusCode);
                var admin = await CreateCorrectiveStaffAsync(await ReadConfiguredSuperAdminAsync(), "admin", 2);
                try
                {
                    using var otherClient = await CreateAuthenticatedStaffClientAsync(factory, await ReadCorrectiveStaffAsync(admin));
                    using var forbidden = await otherClient.GetAsync($"/api/asap/staff/email-operations/{id}?organizationId={scope}");
                    Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
                }
                finally
                {
                    await DeactivateCorrectiveStaffAsync(admin.Id);
                }
            }
            finally
            {
                foreach (var response in requests)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    public async Task SavedEmailTestCooldownSerializesActorAcrossScopesAndFiveClicksPerScope()
    {
        const int scope = 382937, otherScope = 382938;
        var actors = new List<(StaffUser Staff, HttpClient Client)>();
        await SeedSavedEmailScopeAsync(scope);
        await SeedSavedEmailScopeAsync(otherScope);
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var staff = await CreateSavedEmailActorAsync();
                var client = await CreateAuthenticatedStaffClientAsync(factory!, await ReadCorrectiveStaffAsync(staff));
                actors.Add((staff, client));
            }
            var saved = await Task.WhenAll(actors.Select(item => ReadSavedEmailContextAsync(item.Client, scope)));
            var firstId = Guid.NewGuid();
            var firstUrl = $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={firstId:D}";
            using var first = await actors[0].Client.PostAsJsonAsync(firstUrl, SavedEmailInput(saved[0]));
            Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode, await first.Content.ReadAsStringAsync());
            var otherSaved = await ReadSavedEmailContextAsync(actors[0].Client, otherScope);
            using var actorCooldown = await actors[0].Client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={otherScope}&operationId={Guid.NewGuid():D}", SavedEmailInput(otherSaved));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, actorCooldown.StatusCode,
                "Switching between Settings/Operations scopes cannot bypass the actor cooldown.");
            Assert.AreEqual(0, await CountSavedEmailRowsAsync(otherScope));
            var concurrent = await Task.WhenAll(actors.Skip(1).Select((item, index) => item.Client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved[index + 1]))));
            try
            {
                Assert.AreEqual(4, concurrent.Count(item => item.StatusCode == HttpStatusCode.Accepted));
                Assert.AreEqual(1, concurrent.Count(item => item.StatusCode == HttpStatusCode.TooManyRequests));
                Assert.AreEqual(5, await CountSavedEmailRowsAsync(scope));
                Assert.HasCount(5, dispatcher!.EnqueuedIds);
                using var replay = await actors[0].Client.PostAsJsonAsync(firstUrl, SavedEmailInput(saved[0]));
                Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode,
                    "The original click resolves before a full scope allowance is charged.");
                Assert.HasCount(5, dispatcher.EnqueuedIds);
            }
            finally
            {
                foreach (var response in concurrent)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await CleanupSavedEmailScopeAsync(otherScope);
            foreach (var (staff, client) in actors)
            {
                client.Dispose();
                await DeactivateCorrectiveStaffAsync(staff.Id);
            }
        }
    }

    [TestMethod]
    [DataRow("sender")]
    [DataRow("credential")]
    [DataRow("recipient")]
    [DataRow("mode")]
    public async Task SavedEmailTestRejectsChangedDisplayedContextWithoutDurableIntent(string change)
    {
        const int scope = 382931;
        var staff = await CreateSavedEmailActorAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        var sender = new SavedEmailRecordingSender();
        await using var application = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>(); services.AddSingleton<IEmailSender>(sender);
        }));
        using var client = await CreateAuthenticatedStaffClientAsync(application, actor);
        await SeedSavedEmailScopeAsync(scope);
        try
        {
            var saved = await ReadSavedEmailContextAsync(client, scope);
            await ChangeSavedEmailFixtureAsync(scope, staff.Id, change, sender);
            using var response = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved));
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var rejected = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("email_test_configuration_changed", rejected.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(0, await CountSavedEmailRowsAsync(scope));
            Assert.HasCount(0, sender.Envelopes);
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("sender")]
    [DataRow("credential")]
    [DataRow("mode")]
    public async Task SavedEmailTestWorkerFencesQueuedConfigurationAndModeChanges(string change)
    {
        const int scope = 382932;
        var staff = await CreateSavedEmailActorAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        var sender = new SavedEmailRecordingSender();
        await using var application = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>(); services.AddSingleton<IEmailSender>(sender);
        }));
        using var client = await CreateAuthenticatedStaffClientAsync(application, actor);
        await SeedSavedEmailScopeAsync(scope);
        try
        {
            var saved = await ReadSavedEmailContextAsync(client, scope);
            using var queued = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved));
            Assert.AreEqual(HttpStatusCode.Accepted, queued.StatusCode, await queued.Content.ReadAsStringAsync());
            using var queuedBody = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
            var id = long.Parse(queuedBody.RootElement.GetProperty("data").GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            await ChangeSavedEmailFixtureAsync(scope, staff.Id, change, sender);
            var jobs = application.Services.GetRequiredService<EmailOutboxJobs>();
            await jobs.DeliverAsync(id, CancellationToken.None);
            await jobs.DeliverAsync(id, CancellationToken.None);
            Assert.HasCount(0, sender.Envelopes);
            await using var context = await application.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
            var row = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.AreEqual("suppressed", row.Status);
            Assert.AreEqual(change == "mode" ? "test_delivery_mode_changed" : "test_configuration_changed", row.SuppressionReason);
            Assert.IsNull(row.ProviderMessageId);
            Assert.IsNull(row.SentUtc);
            Assert.IsNull(row.LeaseId);
            Assert.AreEqual(1, row.AttemptCount);
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("recipient")]
    [DataRow("sender")]
    [DataRow("mode")]
    public async Task SavedEmailTestContextReportsSafeBlockingReasonsWithoutSending(string reason)
    {
        const int scope = 382936;
        var staff = await CreateSavedEmailActorAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        var sender = new SavedEmailRecordingSender();
        await using var application = factory!.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>(); services.AddSingleton<IEmailSender>(sender);
        }));
        using var client = await CreateAuthenticatedStaffClientAsync(application, actor);
        await SeedSavedEmailScopeAsync(scope);
        var contexts = application.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        string? oldSender;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            oldSender = system.FromAddress;
            if (reason == "sender")
            {
                system.FromAddress = null;
            }
            if (reason == "recipient")
            {
                (await context.StaffUsers.SingleAsync(item => item.Id == staff.Id)).NotificationEmail = null;
            }
            if (reason == "mode")
            {
                sender.Readiness = EmailTransportReadiness.LocalCapture;
            }
            await context.SaveChangesAsync();
        }
        try
        {
            var saved = await ReadSavedEmailContextAsync(client, scope);
            Assert.AreEqual("blocked", saved.GetProperty("state").GetString());
            var expected = reason == "recipient" ? "recipient_missing_or_invalid" : reason == "sender" ? "sender_missing" : "live_delivery_disabled";
            Assert.AreEqual(expected, saved.GetProperty("blockingReason").GetString());
            Assert.AreEqual(0, await CountSavedEmailRowsAsync(scope), "Read-only context checks create no intent.");
            using var submitted = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved));
            if (reason == "mode")
            {
                Assert.AreEqual(HttpStatusCode.Conflict, submitted.StatusCode);
                Assert.AreEqual(0, await CountSavedEmailRowsAsync(scope));
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.OK, submitted.StatusCode);
                await using var verify = await contexts.CreateDbContextAsync();
                var row = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.OrganizationId == scope);
                Assert.AreEqual("suppressed", row.Status);
                Assert.AreEqual(expected, row.SuppressionReason);
            }
            Assert.HasCount(0, sender.Envelopes);
            Assert.HasCount(0, dispatcher!.EnqueuedIds);
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await using var context = await contexts.CreateDbContextAsync();
            (await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1)).FromAddress = oldSender;
            await context.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    [TestMethod]
    [DataRow("live")]
    [DataRow("sandbox_at_dispatch")]
    [DataRow("configuration_at_dispatch")]
    [DataRow("system_before_enqueue")]
    [DataRow("system_during_final_readiness")]
    public async Task SavedEmailTestUsesRecordingProductionPostmarkAndFinalDispatchEvidence(string outcome)
    {
        const int scope = 382933;
        var staff = await CreateSavedEmailActorAsync();
        var actor = await ReadCorrectiveStaffAsync(staff);
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        string? originalToken;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            originalToken = system.ProtectedServerToken;
            system.ProtectedServerToken = factory.Services.GetRequiredService<IntegrationCredentialProtector>().Protect("system-controlled-test-token");
            await context.SaveChangesAsync();
        }
        var rotatedDuringReadiness = false;
        Func<Task>? finalReadinessMutation = outcome == "system_during_final_readiness" ? async () =>
        {
            await using var changed = await contexts.CreateDbContextAsync();
            (await changed.EmailSettings.SingleAsync(item => item.OrganizationId == 1)).ProtectedServerToken =
                factory.Services.GetRequiredService<IntegrationCredentialProtector>().Protect("rotated-during-final-readiness");
            await changed.SaveChangesAsync();
            rotatedDuringReadiness = true;
        } : null;
        var handler = new SavedEmailPostmarkHandler(outcome == "sandbox_at_dispatch", finalReadinessMutation);
        await using var application = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(provider =>
            {
                IEmailSender actual = new PostmarkEmailSender(provider.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
                    provider.GetRequiredService<IntegrationCredentialProtector>(), new SingleClientFactory(new HttpClient(handler)),
                    NullLogger<PostmarkEmailSender>.Instance);
                return outcome == "configuration_at_dispatch"
                    ? new SavedEmailDispatchChangeSender(actual, () => ChangeSavedEmailFixtureAsync(scope, staff.Id, "sender", new SavedEmailRecordingSender()))
                    : actual;
            });
        }));
        using var client = await CreateAuthenticatedStaffClientAsync(application, actor);
        await SeedSavedEmailScopeAsync(scope);
        try
        {
            var saved = await ReadSavedEmailContextAsync(client, scope);
            if (outcome == "system_before_enqueue")
            {
                await using var changed = await contexts.CreateDbContextAsync();
                (await changed.EmailSettings.SingleAsync(item => item.OrganizationId == 1)).ProtectedServerToken =
                    factory.Services.GetRequiredService<IntegrationCredentialProtector>().Protect("rotated-system-controlled-token");
                await changed.SaveChangesAsync();
            }
            using var queued = await client.PostAsJsonAsync(
                $"/api/asap/staff/email-operations/test?organizationId={scope}&operationId={Guid.NewGuid():D}", SavedEmailInput(saved));
            if (outcome == "system_before_enqueue")
            {
                Assert.AreEqual(HttpStatusCode.Conflict, queued.StatusCode);
                Assert.AreEqual(0, await CountSavedEmailRowsAsync(scope));
                Assert.HasCount(0, handler.Sends);
                return;
            }
            Assert.AreEqual(HttpStatusCode.Accepted, queued.StatusCode, await queued.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
            var id = long.Parse(body.RootElement.GetProperty("data").GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            var jobs = application.Services.GetRequiredService<EmailOutboxJobs>();
            await jobs.DeliverAsync(id, CancellationToken.None);
            await jobs.DeliverAsync(id, CancellationToken.None);
            await using var context = await contexts.CreateDbContextAsync();
            var row = await context.EmailOutbox.AsNoTracking().SingleAsync(item => item.Id == id);
            if (outcome == "live")
            {
                Assert.HasCount(1, handler.Sends);
                Assert.AreEqual("system-controlled-test-token", handler.Sends[0].Token);
                using var sent = JsonDocument.Parse(handler.Sends[0].Body);
                Assert.AreEqual("primary-test@example.org", sent.RootElement.GetProperty("To").GetString());
                StringAssert.Contains(sent.RootElement.GetProperty("From").GetString()!, "Saved library sender");
                Assert.AreEqual("sent", row.Status);
                Assert.AreEqual("postmark", row.AttemptDeliveryMode);
                Assert.AreEqual("controlled-provider-reference", row.ProviderMessageId);
                Assert.IsFalse(row.BodyText!.Contains("system-controlled-test-token", StringComparison.Ordinal));
            }
            else
            {
                if (outcome == "system_during_final_readiness")
                {
                    Assert.IsTrue(rotatedDuringReadiness, "The configuration change occurs inside the final provider readiness read.");
                }
                Assert.HasCount(0, handler.Sends, "The final adapter fence must reject changed configuration/mode before any POST.");
                Assert.AreEqual("suppressed", row.Status);
                Assert.AreEqual("test_configuration_changed", row.SuppressionReason);
            }
        }
        finally
        {
            await CleanupSavedEmailScopeAsync(scope);
            await using var context = await contexts.CreateDbContextAsync();
            var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            system.ProtectedServerToken = originalToken; await context.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    private async Task<StaffUser> CreateSavedEmailActorAsync()
    {
        var staff = await CreateCorrectiveStaffAsync(await ReadConfiguredSuperAdminAsync(), "super_admin", 1);
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        var row = await context.StaffUsers.SingleAsync(item => item.Id == staff.Id);
        row.NotificationEmail = "primary-test@example.org";
        row.WeeklyActionSummaryEmail = "weekly-test@example.org";
        await context.SaveChangesAsync();
        return row;
    }

    private async Task SeedSavedEmailScopeAsync(int scope)
    {
        await UpsertTestOrganizationAsync(scope, "Saved Email Test Library", "SET");
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        context.EmailSettings.Add(new EmailSettings { OrganizationId = scope, FromName = "Saved library sender", UpdatedUtc = DateTime.UtcNow,
            ProtectedServerToken = "obsolete-library-token-must-not-be-used" });
        await context.SaveChangesAsync();
    }

    private async Task CleanupSavedEmailScopeAsync(int scope)
    {
        await ExecuteNonQueryAsync("""
            DELETE FROM [asap].[EmailDeliveryEvent] WHERE [EmailOutboxId] IN (SELECT [Id] FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @scope);
            DELETE FROM [asap].[EmailOutbox] WHERE [OrganizationId] = @scope;
            DELETE FROM [asap].[EmailSettings] WHERE [OrganizationId] = @scope;
            DELETE FROM [asap].[AdministrativeAudit] WHERE [OrganizationId] = @scope;
            DELETE FROM [asap].[Organization] WHERE [Id] = @scope;
            """, ("@scope", scope));
    }

    private async Task<int> CountSavedEmailRowsAsync(int scope)
    {
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        return await context.EmailOutbox.CountAsync(item => item.OrganizationId == scope);
    }

    private static async Task<JsonElement> ReadSavedEmailContextAsync(HttpClient client, int scope)
    {
        using var response = await client.GetAsync($"/api/asap/staff/email-operations/test-context?organizationId={scope}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static object SavedEmailInput(JsonElement saved) => new
    {
        configurationVersion = saved.GetProperty("configurationVersion").GetString(), intendedDeliveryMode = "postmark"
    };

    private async Task ChangeSavedEmailFixtureAsync(int scope, long staffId, string change, SavedEmailRecordingSender sender)
    {
        if (change == "mode")
        {
            sender.Readiness = EmailTransportReadiness.LocalCapture; return;
        }
        await using var context = await factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>().CreateDbContextAsync();
        if (change == "recipient")
        {
            var staff = await context.StaffUsers.SingleAsync(item => item.Id == staffId);
            staff.NotificationEmail = "changed-primary@example.org";
        }
        else
        {
            var setting = await context.EmailSettings.SingleAsync(item => item.OrganizationId == scope);
            if (change == "credential")
            {
                // A library credential is ignored, but its saved context/version
                // still changed; the System-only production token test is separate.
                setting.ProtectedServerToken = "changed-obsolete-library-token";
            }
            else
            {
                setting.FromAddress = "changed-sender@example.org";
            }
        }
        await context.SaveChangesAsync();
    }

    private sealed class SavedEmailRecordingSender : IEmailSender
    {
        public EmailTransportReadiness Readiness { get; set; } = EmailTransportReadiness.Configured;
        public List<EmailEnvelope> Envelopes { get; } = [];
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Readiness);
        }
        public Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Envelopes.Add(envelope);
            return Task.FromResult(new EmailSendResult("controlled-send") { DeliveryMode = Readiness.DeliveryMode });
        }
    }

    private sealed class SavedEmailDispatchChangeSender(IEmailSender inner, Func<Task> change) : IEmailSender
    {
        public Task<EmailTransportReadiness> CheckReadinessAsync(int organizationId, CancellationToken cancellationToken) =>
            inner.CheckReadinessAsync(organizationId, cancellationToken);
        public async Task<EmailSendResult> SendAsync(EmailEnvelope envelope, CancellationToken cancellationToken)
        {
            await change(); return await inner.SendAsync(envelope, cancellationToken);
        }
    }

    private sealed class SavedEmailPostmarkHandler(bool sandboxAtDispatch = false, Func<Task>? finalReadinessMutation = null) : HttpMessageHandler
    {
        private int readinessCalls;
        public List<(string Token, string Body)> Sends { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var count = Interlocked.Increment(ref readinessCalls);
                if (count == 4 && finalReadinessMutation is not null)
                {
                    await finalReadinessMutation();
                }
                var mode = sandboxAtDispatch && count >= 4 ? "Sandbox" : "Live";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"ID":1,"DeliveryType":"{{mode}}"}""", Encoding.UTF8, "application/json") };
            }
            Sends.Add((request.Headers.GetValues("X-Postmark-Server-Token").Single(), await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ErrorCode":0,"MessageID":"controlled-provider-reference"}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
