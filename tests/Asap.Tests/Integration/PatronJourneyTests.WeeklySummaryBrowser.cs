using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [TestCategory(TestCategories.Browser)]
    public async Task WeeklySummaryBrowserPreservesGestureIdentityRecoveryAndAuthorizedRunScope()
    {
        factory!.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var actor = await ReadConfiguredSuperAdminAsync();
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, Slice5IsolatedLibraryId);
        var admin = await CreateCorrectiveStaffAsync(actor, "admin", Slice5IsolatedLibraryId);
        var ordinaryActor = await ReadCorrectiveStaffAsync(admin);
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var jobs = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var jobIds = new List<string>();
        var root = Path.GetDirectoryName(TestArtifactPaths.FindRepositoryFile("Asap.sln"))!;
        var artifacts = Path.Combine(root, ".artifacts", "browser", $"weekly-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        try
        {
            var (exitCode, stdout, stderr) = await RunBrowserScriptAsync(root, "weekly-summary.cjs",
                client.BaseAddress!.GetLeftPart(UriPartial.Authority), artifacts,
                Slice5IsolatedLibraryId.ToString(CultureInfo.InvariantCulture), actor.Id.ToString(CultureInfo.InvariantCulture),
                actor.EntraTenantId.ToString(), actor.AuthenticationEmail,
                ordinaryActor.Id.ToString(CultureInfo.InvariantCulture), ordinaryActor.EntraTenantId.ToString(), ordinaryActor.AuthenticationEmail);
            // Retain job IDs before assertions so a failed browser witness still
            // cleans up every real request that reached this test's server.
            var reportPath = Path.Combine(artifacts, "browser-results.json");
            if (File.Exists(reportPath))
            {
                using var cleanupReport = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
                jobIds.AddRange(cleanupReport.RootElement.GetProperty("posts").EnumerateArray()
                    .Where(item => item.TryGetProperty("jobId", out _))
                    .Select(item => item.GetProperty("jobId").GetString()!));
            }
            Assert.AreEqual(0, exitCode, $"{artifacts}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.AreEqual(0, report.RootElement.GetProperty("externalRequests").GetInt32());
            Assert.AreEqual(0, report.RootElement.GetProperty("pageErrors").GetArrayLength());
            Assert.AreEqual(4, report.RootElement.GetProperty("states").GetArrayLength());
            var posts = report.RootElement.GetProperty("posts").EnumerateArray().ToArray();
            Assert.HasCount(8, posts, "Six distinct super-admin runs (one retried) and one ordinary-admin run.");
            Assert.AreEqual(7, posts.Select(item => item.GetProperty("operationId").GetString()).Distinct().Count());
            foreach (var post in posts)
            {
                var job = ReadEnqueuedHangfireJob(storage, post.GetProperty("jobId").GetString()!);
                Assert.AreEqual(nameof(BackgroundWorkflowJobs.SendForcedWeeklyStaffSummaryAsync), job.Method.Name);
                Assert.AreEqual(Guid.Parse(post.GetProperty("operationId").GetString()!), (Guid)job.Args[2]);
                var expectedScope = post.GetProperty("scope").GetString();
                if (expectedScope == "all")
                {
                    Assert.IsNull(job.Args[1]);
                }
                else
                {
                    Assert.AreEqual(int.Parse(expectedScope!, CultureInfo.InvariantCulture), Convert.ToInt32(job.Args[1]));
                }
            }
            Assert.HasCount(0, dispatcher!.EnqueuedIds, "Manual actions enqueue workflow jobs; they do not deliver synchronously.");
            Assert.HasCount(0, ((RecordingEmailSender)factory.Services.GetRequiredService<IEmailSender>()).Envelopes);
        }
        finally
        {
            DeleteHangfireJobs(jobs, jobIds);
            await DeactivateCorrectiveStaffAsync(admin.Id);
        }
    }
}
