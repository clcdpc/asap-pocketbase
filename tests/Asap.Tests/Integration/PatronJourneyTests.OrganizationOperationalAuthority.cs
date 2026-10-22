using System.Net;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    public async Task OperationalRunNowRequiresSystemOrActiveLibraryAuthorityBeforeQueueing()
    {
        const int firstTestOrganizationId = 9876100;
        var inactiveLibraryId = firstTestOrganizationId;
        var activeBranchId = firstTestOrganizationId + 1;
        var inactiveBranchId = firstTestOrganizationId + 2;
        var unclassifiedOrganizationId = firstTestOrganizationId + 3;
        var testOrganizationIds = new[]
        {
            inactiveLibraryId,
            activeBranchId,
            inactiveBranchId,
            unclassifiedOrganizationId
        };
        var contextFactory = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var jobs = factory.Services.GetRequiredService<IBackgroundJobClient>();
        var jobIds = new List<string>();
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        StaffUser? ordinaryAdmin = null;
        int? originalSystemOrganizationCodeId = null;
        var systemOrganizationCodeCaptured = false;
        var testOrganizationsSeeded = false;

        try
        {
            await using (var seed = await contextFactory.CreateDbContextAsync())
            {
                var system = await seed.Organizations.SingleAsync(item => item.Id == 1);
                originalSystemOrganizationCodeId = system.OrganizationCodeId;
                systemOrganizationCodeCaptured = true;
                system.OrganizationCodeId = 1;
                seed.Organizations.AddRange(
                    new Organization
                    {
                        Id = inactiveLibraryId,
                        DisplayName = "Inactive operational library fixture",
                        Abbreviation = "IOF",
                        OrganizationCodeId = 2,
                        ParentOrganizationId = 1,
                        IsActive = false
                    },
                    new Organization
                    {
                        Id = activeBranchId,
                        DisplayName = "Active branch operational fixture",
                        Abbreviation = "ABF",
                        OrganizationCodeId = 3,
                        ParentOrganizationId = 2,
                        IsActive = true
                    },
                    new Organization
                    {
                        Id = inactiveBranchId,
                        DisplayName = "Inactive branch operational fixture",
                        Abbreviation = "IBF",
                        OrganizationCodeId = 3,
                        ParentOrganizationId = 2,
                        IsActive = false
                    },
                    new Organization
                    {
                        Id = unclassifiedOrganizationId,
                        DisplayName = "Unclassified operational fixture",
                        Abbreviation = "UOF",
                        OrganizationCodeId = null,
                        ParentOrganizationId = 2,
                        IsActive = true
                    });
                await seed.SaveChangesAsync();
                testOrganizationsSeeded = true;
            }

            ordinaryAdmin = await CreateCorrectiveStaffAsync(superAdmin, "admin", 2);
            var ordinaryActor = await ReadCorrectiveStaffAsync(ordinaryAdmin);
            using var superClient = factory.CreateClient();
            AddTestingStaffHeaders(superClient, superAdmin.Id, superAdmin.EntraTenantId,
                superAdmin.AuthenticationEmail);
            superClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(superClient));
            using var ordinaryClient = factory.CreateClient();
            AddTestingStaffHeaders(ordinaryClient, ordinaryActor.Id, ordinaryActor.EntraTenantId,
                ordinaryActor.AuthenticationEmail);
            ordinaryClient.DefaultRequestHeaders.Add("X-ASAP-Antiforgery", await ReadAntiforgeryTokenAsync(ordinaryClient));

            var baselineWorkflowJobs = ReadOperationalAuthorityEnqueuedJobIds(storage, "asap-workflow");
            var baselineAdminJobs = ReadOperationalAuthorityEnqueuedJobIds(storage, "asap-admin");
            var baselineSql = await ReadOperationalAuthoritySqlSnapshotAsync(contextFactory);

            foreach (var invalidScope in testOrganizationIds)
            {
                var forcedOperationId = Guid.NewGuid().ToString("D");
                foreach (var path in new[]
                {
                    $"/api/asap/staff/workflow/run-now?organizationId={invalidScope}",
                    $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={invalidScope}&force=false",
                    $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId={invalidScope}&force=true&operationId={forcedOperationId}"
                })
                {
                    using var rejected = await superClient.PostAsync(path, content: null);
                    var rejectedBody = await rejected.Content.ReadAsStringAsync();
                    if (rejected.IsSuccessStatusCode)
                    {
                        using var acceptedDocument = JsonDocument.Parse(rejectedBody);
                        if (acceptedDocument.RootElement.TryGetProperty("jobId", out var acceptedJobId) &&
                            acceptedJobId.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(acceptedJobId.GetString()))
                        {
                            jobIds.Add(acceptedJobId.GetString()!);
                        }
                    }
                    Assert.AreEqual(HttpStatusCode.Forbidden, rejected.StatusCode,
                        $"Scope {invalidScope} must be rejected before queuing {path}: {rejectedBody}");
                    using var error = JsonDocument.Parse(rejectedBody);
                    Assert.AreEqual("staff_scope_forbidden", error.RootElement.GetProperty("code").GetString());
                }
            }

            CollectionAssert.AreEqual(baselineWorkflowJobs,
                ReadOperationalAuthorityEnqueuedJobIds(storage, "asap-workflow"),
                "Rejected workflow scopes cannot create Hangfire jobs.");
            CollectionAssert.AreEqual(baselineAdminJobs,
                ReadOperationalAuthorityEnqueuedJobIds(storage, "asap-admin"),
                "Rejected weekly-summary scopes cannot create Hangfire jobs.");
            Assert.AreEqual(baselineSql, await ReadOperationalAuthoritySqlSnapshotAsync(contextFactory),
                "Rejected operational scopes cannot change business, audit, queue-progress, or outbox rows.");

            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/run-now",
                "asap-workflow",
                nameof(BackgroundWorkflowJobs.ProcessManualWorkflowAsync),
                expectedScopeOrganizationId: 1);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/run-now?organizationId=1",
                "asap-workflow",
                nameof(BackgroundWorkflowJobs.ProcessManualWorkflowAsync),
                expectedScopeOrganizationId: 1);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/run-now?organizationId=2",
                "asap-workflow",
                nameof(BackgroundWorkflowJobs.ProcessManualWorkflowAsync),
                expectedScopeOrganizationId: 2);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/weekly-summary/run-now?force=false",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: null);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/weekly-summary/run-now?organizationId=1&force=false",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: 1);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                "/api/asap/staff/workflow/weekly-summary/run-now?organizationId=2&force=false",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: 2);
            await AssertOperationalAuthorityJobAsync(
                superClient,
                superAdmin,
                jobIds,
                $"/api/asap/staff/workflow/weekly-summary/run-now?organizationId=2&force=true&operationId={Guid.NewGuid():D}",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendForcedWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: 2,
                forced: true);

            await AssertOperationalAuthorityJobAsync(
                ordinaryClient,
                ordinaryActor,
                jobIds,
                "/api/asap/staff/workflow/run-now",
                "asap-workflow",
                nameof(BackgroundWorkflowJobs.ProcessManualWorkflowAsync),
                expectedScopeOrganizationId: 2);
            await AssertOperationalAuthorityJobAsync(
                ordinaryClient,
                ordinaryActor,
                jobIds,
                "/api/asap/staff/workflow/weekly-summary/run-now?force=false",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendManualWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: 2);
            await AssertOperationalAuthorityJobAsync(
                ordinaryClient,
                ordinaryActor,
                jobIds,
                $"/api/asap/staff/workflow/weekly-summary/run-now?force=true&operationId={Guid.NewGuid():D}",
                "asap-admin",
                nameof(BackgroundWorkflowJobs.SendForcedWeeklyStaffSummaryAsync),
                expectedScopeOrganizationId: 2,
                forced: true);

            Assert.AreEqual(baselineSql, await ReadOperationalAuthoritySqlSnapshotAsync(contextFactory),
                "Accepted requests only enqueue manual work and do not execute it or write business state.");
        }
        finally
        {
            DeleteHangfireJobs(jobs, jobIds);
            if (ordinaryAdmin is not null)
            {
                await DeactivateCorrectiveStaffAsync(ordinaryAdmin.Id);
            }
            await using var cleanup = await contextFactory.CreateDbContextAsync();
            if (testOrganizationsSeeded)
            {
                var organizations = await cleanup.Organizations
                    .Where(item => testOrganizationIds.Contains(item.Id))
                    .ToListAsync();
                cleanup.Organizations.RemoveRange(organizations);
            }
            if (systemOrganizationCodeCaptured)
            {
                var system = await cleanup.Organizations.SingleAsync(item => item.Id == 1);
                system.OrganizationCodeId = originalSystemOrganizationCodeId;
                await cleanup.SaveChangesAsync();
            }
        }
    }

    private async Task AssertOperationalAuthorityJobAsync(
        HttpClient client,
        CurrentStaff actor,
        ICollection<string> trackedJobIds,
        string path,
        string expectedQueue,
        string expectedMethod,
        int? expectedScopeOrganizationId,
        bool forced = false)
    {
        using var response = await client.PostAsync(path, content: null);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            using var acceptedDocument = JsonDocument.Parse(responseBody);
            if (acceptedDocument.RootElement.TryGetProperty("jobId", out var acceptedJobId) &&
                acceptedJobId.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(acceptedJobId.GetString()))
            {
                trackedJobIds.Add(acceptedJobId.GetString()!);
            }
        }
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        Assert.AreEqual("queued", root.GetProperty("code").GetString());
        Assert.AreEqual(expectedScopeOrganizationId ?? 1, root.GetProperty("organizationId").GetInt32());
        var jobId = root.GetProperty("jobId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

        var storage = factory!.Services.GetRequiredService<JobStorage>();
        var loadedJob = ReadEnqueuedHangfireJob(storage, jobId!);
        Assert.AreEqual(typeof(BackgroundWorkflowJobs), loadedJob.Type);
        Assert.AreEqual(expectedMethod, loadedJob.Method.Name);
        Assert.IsGreaterThanOrEqualTo(1, loadedJob.Args.Count);
        var evidence = loadedJob.Args[0] as StaffJobEvidence;
        Assert.IsNotNull(evidence);
        Assert.AreEqual(actor.Id, evidence!.StaffUserId);
        Assert.AreEqual(actor.AuthenticationEmail, evidence.AuthenticationEmail);
        Assert.AreEqual(actor.EntraTenantId, evidence.TenantId);

        var expectedQueueJobIds = ReadOperationalAuthorityEnqueuedJobIds(storage, expectedQueue);
        CollectionAssert.Contains(expectedQueueJobIds, jobId);
        if (expectedMethod == nameof(BackgroundWorkflowJobs.ProcessManualWorkflowAsync))
        {
            Assert.AreEqual(expectedScopeOrganizationId ?? 1, Convert.ToInt32(loadedJob.Args[1]));
        }
        else if (expectedScopeOrganizationId.HasValue)
        {
            Assert.AreEqual(expectedScopeOrganizationId.Value, Convert.ToInt32(loadedJob.Args[1]));
        }
        else
        {
            Assert.IsNull(loadedJob.Args[1]);
        }

        if (forced)
        {
            var responseOperationId = root.GetProperty("manualRunId").GetGuid();
            Assert.AreEqual(responseOperationId, (Guid)loadedJob.Args[2]);
        }
        else
        {
            Assert.IsFalse(root.TryGetProperty("manualRunId", out _));
        }
    }

    private static string[] ReadOperationalAuthorityEnqueuedJobIds(JobStorage storage, string queue) =>
        storage.GetMonitoringApi().EnqueuedJobs(queue, 0, 1000)
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static async Task<string> ReadOperationalAuthoritySqlSnapshotAsync(
        IDbContextFactory<AsapDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var snapshot = new
        {
            titleRequests = await context.TitleRequests.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            titleRequestEvents = await context.TitleRequestEvents.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            queueProgress = await context.QueueProgress.AsNoTracking()
                .OrderBy(item => item.QueueName).ThenBy(item => item.ScopeOrganizationId).ToArrayAsync(),
            emailOutbox = await context.EmailOutbox.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            administrativeAudits = await context.AdministrativeAudits.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()
        };
        return JsonSerializer.Serialize(snapshot);
    }
}
