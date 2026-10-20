using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task SettingsEmailTestBrowserDispatchesAutomaticallyThroughRecordingPostmarkAndObservesSameReference()
    {
        const int scope = 382935;
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
        var handler = new SavedEmailPostmarkHandler();
        var queue = $"email-test-{Guid.NewGuid():N}";
        var jobIds = new List<string>();
        await using var application = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(provider => new PostmarkEmailSender(
                provider.GetRequiredService<IDbContextFactory<AsapDbContext>>(),
                provider.GetRequiredService<IntegrationCredentialProtector>(),
                new SingleClientFactory(new HttpClient(handler)), NullLogger<PostmarkEmailSender>.Instance));
            services.RemoveAll<IEmailOutboxDispatcher>();
            services.AddSingleton<IEmailOutboxDispatcher>(provider => new SavedEmailFixtureDispatcher(
                new BackgroundJobClient(provider.GetRequiredService<JobStorage>(), new SavedEmailQueueFilters(queue)), queue, jobIds));
        }));
        application.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = application.CreateClient();
        await SeedSavedEmailScopeAsync(scope);
        try
        {
            var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
            var artifacts = Path.Combine(root, ".artifacts", "browser", $"saved-email-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifacts);
            // Only this test's existing DeliverAsync jobs run. Unrelated fixture
            // backlog and recurring workflow queues remain stopped.
            using (var server = new BackgroundJobServer(new BackgroundJobServerOptions
            {
                Queues = [queue], WorkerCount = 1,
                Activator = new SavedEmailFixtureActivator(application.Services)
            }, application.Services.GetRequiredService<JobStorage>()))
            {
                var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "saved-email-test.cjs",
                    client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                    scope.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                    actor.EntraTenantId.ToString(), actor.AuthenticationEmail);
                Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            }
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifacts, "browser-results.json")));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(2, report.RootElement.GetProperty("posts").GetArrayLength(), "One abandoned client submission and one explicit new test.");
            Assert.IsTrue(Guid.TryParse(report.RootElement.GetProperty("retiredUnrecordedIntent").GetString(), out _));
            Assert.AreEqual(5, report.RootElement.GetProperty("states").GetArrayLength());
            Assert.IsTrue(report.RootElement.GetProperty("sameReferenceAfterReload").GetBoolean());
            Assert.HasCount(1, handler.Sends, "One explicit browser click must produce exactly one provider POST.");
            Assert.HasCount(1, jobIds, "The ordinary durable dispatcher creates one automatic delivery job.");
            await using var verify = await contexts.CreateDbContextAsync();
            var row = await verify.EmailOutbox.AsNoTracking().SingleAsync(item => item.OrganizationId == scope);
            Assert.AreEqual("sent", row.Status);
            Assert.AreEqual("postmark", row.AttemptDeliveryMode);
            Assert.AreEqual(staff.Id, row.TestRequestedByStaffUserId);
            Assert.AreEqual("primary-test@example.org", row.ToAddress);
            StringAssert.Contains(row.BodyText!, $"Email operation reference: {row.Id}.");
            Assert.AreEqual("system-controlled-test-token", handler.Sends[0].Token);
            Assert.AreEqual(0, await verify.TitleRequests.CountAsync(item => item.LibraryOrganizationId == scope));
        }
        finally
        {
            var jobs = application.Services.GetRequiredService<IBackgroundJobClient>();
            foreach (var id in jobIds)
            {
                jobs.Delete(id);
            }
            await CleanupSavedEmailScopeAsync(scope);
            await using var context = await contexts.CreateDbContextAsync();
            var system = await context.EmailSettings.SingleAsync(item => item.OrganizationId == 1);
            system.ProtectedServerToken = originalToken; await context.SaveChangesAsync();
            await DeactivateCorrectiveStaffAsync(staff.Id);
        }
    }

    private sealed class SavedEmailFixtureDispatcher(IBackgroundJobClient jobs, string queue, List<string> ids) : IEmailOutboxDispatcher
    {
        public void Enqueue(long outboxId) => ids.Add(jobs.Create(
            Job.FromExpression<EmailOutboxJobs>(worker => worker.DeliverAsync(outboxId, CancellationToken.None)), new EnqueuedState(queue)));
    }

    private sealed class SavedEmailFixtureActivator(IServiceProvider services) : JobActivator
    {
        public override object ActivateJob(Type jobType) => services.GetRequiredService(jobType);
    }

    private sealed class SavedEmailQueueFilters(string queue) : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) => JobFilterProviders.Providers.GetFilters(job)
            .Append(new JobFilter(new QueueAttribute(queue), JobFilterScope.Global, int.MaxValue));
    }
}
