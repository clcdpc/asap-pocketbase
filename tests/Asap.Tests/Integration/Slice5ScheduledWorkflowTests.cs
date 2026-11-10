using System.Diagnostics;
using Asap.Web.Features.Email;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Asap.Tests.Integration;

public sealed partial class PatronJourneyTests
{
    [TestMethod]
    [DataRow("completed", true)]
    [DataRow("organization_inactive", true)]
    [DataRow("sql_failure", false)]
    [DataRow("operational_failure", false)]
    [DataRow("stale_progress_fence", false)]
    [DataRow("organization_snapshot_empty", false)]
    [DataRow("organization_snapshot_invalid", false)]
    [DataRow("staff_scope_forbidden", false)]
    [DataRow("future_unknown_code", false)]
    public void ScheduledWorkflowClassifiesOnlyExplicitSuccessAsComplete(string code, bool succeeds)
    {
        var result = new WorkflowRunResult(code, 2, 1, 1);
        if (succeeds)
        {
            Assert.AreSame(result, ScheduledWorkflowAttribute.RequireCompletion(result, CancellationToken.None));
            return;
        }
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ScheduledWorkflowAttribute.RequireCompletion(result, CancellationToken.None));
        StringAssert.Contains(error.Message, code);
        StringAssert.Contains(error.Message, "changed 1");
    }

    [TestMethod]
    public void ScheduledWorkflowNeverDefersProgressOrSwallowsCancellation()
    {
        Assert.ThrowsExactly<WorkflowAdmissionBusyException>(() => ScheduledWorkflowAttribute.RequireCompletion(
            new("workflow_processing_busy"), CancellationToken.None));
        Assert.ThrowsExactly<InvalidOperationException>(() => ScheduledWorkflowAttribute.RequireCompletion(
            new("workflow_processing_busy", 1), CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => ScheduledWorkflowAttribute.RequireCompletion(
            new("completed"), cancellation.Token));
    }

    [TestMethod]
    [DataRow("workflow")]
    [DataRow("identifier")]
    [DataRow("organizations")]
    [DataRow("summary")]
    public async Task ScheduledWorkflowDefersSameStoredInvocationAcrossWorkerReconstruction(string family)
    {
        var services = factory!.Services;
        const int scope = 99050;
        await EnsureSlice5IsolatedLibraryAsync(services.GetRequiredService<IDbContextFactory<AsapDbContext>>(), scope);
        var storage = services.GetRequiredService<JobStorage>();
        var queue = $"admission-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var job = family switch
        {
            "workflow" => Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.ProcessWorkflowAsync(scope, CancellationToken.None)),
            "identifier" => Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.ProcessIdentifierAsync(CancellationToken.None)),
            "organizations" => Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.RefreshOrganizationsAsync(CancellationToken.None)),
            _ => Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.SendWeeklyStaffSummaryAsync(null, scope, CancellationToken.None))
        };
        var id = jobs.Create(job, new EnqueuedState(queue));
        DateTime occurredUtc;
        DateTime deadline;
        try
        {
            // This is the same SQL guard used by every other workflow family.
            using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
            using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
                using var connection = storage.GetConnection();
                occurredUtc = Hangfire.Common.SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.OccurrenceParameter));
                deadline = Hangfire.Common.SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter));
                Assert.AreEqual("1", connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
                Assert.IsNull(connection.GetJobParameter(id, ScheduledWorkflowAttribute.AdmittedParameter));
                Assert.AreEqual(connection.GetJobData(id).CreatedAt, occurredUtc);
                Assert.AreEqual(job.Method, ReadEnqueuedHangfireJob(storage, id).Method);
            }

            // Reconstruct the service provider and server, then advance only the
            // Hangfire schedule. No new job/arguments or in-memory budget survives.
            await using var reconstructed = CreateApplicationFactory(configurationPath);
            using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
            using (var server = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters))
            {
                Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
                await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
            }
            using (var pending = storage.GetConnection())
            {
                Assert.AreEqual(deadline, SerializationHelper.Deserialize<DateTime>(pending.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter)));
                Assert.AreEqual("2", pending.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
                Assert.IsNull(pending.GetJobParameter(id, ScheduledWorkflowAttribute.AdmittedParameter));
            }
            Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
            using (var server = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, SucceededState.StateName);
            }
            using var verify = storage.GetConnection();
            Assert.AreEqual(occurredUtc, SerializationHelper.Deserialize<DateTime>(verify.GetJobParameter(id, ScheduledWorkflowAttribute.OccurrenceParameter)));
            Assert.AreEqual(deadline, SerializationHelper.Deserialize<DateTime>(verify.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter)));
            Assert.AreEqual("2", verify.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
            Assert.AreEqual("true", verify.GetJobParameter(id, ScheduledWorkflowAttribute.AdmittedParameter));
            Assert.IsNull(verify.GetJobParameter(id, "RetryCount"));
            Assert.AreEqual(1, storage.GetMonitoringApi().JobDetails(id).History.Count(item => item.StateName == SucceededState.StateName));
        }
        finally
        {
            jobs.Delete(id);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledWorkflowExhaustedAdmissionRemainsFailedAfterReconstruction(bool expiredDeadline)
    {
        var services = factory!.Services;
        var storage = services.GetRequiredService<JobStorage>();
        var queue = $"exhausted-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var id = jobs.Create(Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.RefreshOrganizationsAsync(CancellationToken.None)),
            new EnqueuedState(queue));
        try
        {
            using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
            using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
            }
            using (var connection = storage.GetConnection())
            {
                if (expiredDeadline)
                {
                    connection.SetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter,
                        SerializationHelper.Serialize(DateTime.UtcNow.AddMinutes(-1)));
                }
                else
                {
                    connection.SetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter,
                        SerializationHelper.Serialize(ScheduledWorkflowAttribute.MaximumDeferrals));
                }
            }
            await using var reconstructed = CreateApplicationFactory(configurationPath);
            using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
            using (var server = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters))
            {
                Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
                await WaitForScheduledStateAsync(storage, id, FailedState.StateName);
            }
            using var verify = storage.GetConnection();
            var failed = verify.GetStateData(id);
            StringAssert.Contains(failed.Data["ExceptionMessage"], "exhausted");
            Assert.IsNull(verify.GetJobParameter(id, ScheduledWorkflowAttribute.AdmittedParameter));
            Assert.AreEqual(expiredDeadline ? "1" : ScheduledWorkflowAttribute.MaximumDeferrals.ToString(),
                verify.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
            Assert.IsNull(verify.GetJobParameter(id, "RetryCount"));
            Assert.AreEqual(1, storage.GetMonitoringApi().JobDetails(id).History.Count(item => item.StateName == ScheduledState.StateName));
        }
        finally
        {
            jobs.Delete(id);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledOrganizationSnapshotFailuresArePersistedAsFailedWithoutReplay(bool invalid)
    {
        var provider = new ConflictingOrganizationReferenceProvider(invalid
            ? [new(2, "First", null, 2, 1), new(2, "Conflict", null, 3, 1)] : []);
        await using var application = CreateApplicationFactory(configurationPath, provider);
        var storage = application.Services.GetRequiredService<JobStorage>();
        var queue = $"invalid-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var id = jobs.Create(Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.RefreshOrganizationsAsync(CancellationToken.None)),
            new EnqueuedState(queue));
        try
        {
            using (var server = StartScheduledFixtureServer(application.Services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, FailedState.StateName);
            }
            using var connection = storage.GetConnection();
            StringAssert.Contains(connection.GetStateData(id).Data["ExceptionMessage"], invalid ? "organization_snapshot_invalid" : "organization_snapshot_empty");
            Assert.IsNull(connection.GetJobParameter(id, "RetryCount"));
            Assert.IsNull(connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
            Assert.AreEqual(0, provider.PatronCodeLookupCount);
        }
        finally
        {
            jobs.Delete(id);
        }
    }

    [TestMethod]
    [DataRow("completed")]
    [DataRow("recurring")]
    [DataRow("partial_failure")]
    [DataRow("authority_changed")]
    [DataRow("shutdown")]
    public async Task ScheduledWeeklySummaryPreservesPeriodAndCommittedEvidenceAcrossDeferral(string scenario)
    {
        const int scope = 99049;
        var contexts = factory!.Services.GetRequiredService<IDbContextFactory<AsapDbContext>>();
        await EnsureSlice5IsolatedLibraryAsync(contexts, scope);
        var superAdmin = await ReadConfiguredSuperAdminAsync();
        var first = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var second = await CreateCorrectiveStaffAsync(superAdmin, "admin", scope);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(factory.Services.GetRequiredService<ExternalConfiguration>().Application.BusinessTimeZone!);
        var localOccurrence = new DateTime(2030, 3, 17, 23, 59, 0, DateTimeKind.Unspecified);
        var occurredUtc = TimeZoneInfo.ConvertTimeToUtc(localOccurrence, timezone);
        var nextDay = TimeZoneInfo.ConvertTimeToUtc(localOccurrence.AddMinutes(2), timezone);
        timeProvider!.SetUtcNow(new DateTimeOffset(occurredUtc));
        string? staffUrl;
        long requestId;
        long[] enabledGlobalRecipients;
        await using (var seed = await contexts.CreateDbContextAsync())
        {
            var system = await seed.SystemSettings.SingleAsync(item => item.OrganizationId == 1);
            staffUrl = system.StaffApplicationUrl;
            system.StaffApplicationUrl = "https://staff.example.org/staff/";
            // A scoped summary also includes opted-in global super-admins. Keep
            // this partial-progress fixture's recipient sequence wholly owned.
            var globalRecipients = await seed.StaffUsers.Where(item => item.OrganizationId == 1 &&
                item.Role == StaffRole.SuperAdmin && item.WeeklyActionSummaryEnabled).ToListAsync();
            enabledGlobalRecipients = globalRecipients.Select(item => item.Id).ToArray();
            foreach (var globalRecipient in globalRecipients)
            {
                globalRecipient.WeeklyActionSummaryEnabled = false;
            }
            foreach (var staffId in new[] { first.Id, second.Id })
            {
                var staff = await seed.StaffUsers.SingleAsync(item => item.Id == staffId);
                staff.WeeklyActionSummaryEnabled = true;
                staff.WeeklyActionSummaryEmail = null;
                staff.NotificationEmail = $"period-{staffId}@example.org";
            }
            var request = new TitleRequest
            {
                LibraryOrganizationId = scope, Barcode = "period-boundary", Title = "Period boundary",
                MaterialFormatId = await seed.MaterialFormats.Where(item => item.Code == "book").Select(item => item.Id).SingleAsync(),
                Status = "suggestion", CreatedUtc = occurredUtc, UpdatedUtc = occurredUtc
            };
            seed.TitleRequests.Add(request);
            await seed.SaveChangesAsync();
            requestId = request.Id;
        }
        var readinessCalls = 0;
        var secondRecipientEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new ReadinessSender(async token =>
        {
            if (Interlocked.Increment(ref readinessCalls) == 2)
            {
                if (scenario == "partial_failure")
                {
                    throw new EmailOperationalException("Failure after first recipient committed");
                }
                if (scenario == "shutdown")
                {
                    secondRecipientEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
            return EmailTransportReadiness.Configured;
        });
        var storage = factory.Services.GetRequiredService<JobStorage>();
        var queue = $"period-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var id = jobs.Create(Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.SendWeeklyStaffSummaryAsync(null, scope, CancellationToken.None)),
            new EnqueuedState(queue));
        if (scenario == "recurring")
        {
            using var connection = storage.GetConnection();
            connection.SetJobParameter(id, "RecurringJobId", SerializationHelper.Serialize("asap-weekly-staff-summary"));
            connection.SetJobParameter(id, "Time", SerializationHelper.Serialize(JobHelper.ToTimestamp(occurredUtc)));
        }
        else
        {
            await ExecuteNonQueryAsync("UPDATE [HangFire].[Job] SET [CreatedAt] = @occurred WHERE [Id] = @id;",
                ("@occurred", occurredUtc), ("@id", long.Parse(id)));
        }
        try
        {
            using (factory.Services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
            using (var server = StartScheduledFixtureServer(factory.Services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
            }
            timeProvider.SetUtcNow(new DateTimeOffset(nextDay));
            if (scenario == "authority_changed")
            {
                await ExecuteNonQueryAsync("UPDATE [asap].[Organization] SET [IsActive] = 0 WHERE [Id] = @scope;", ("@scope", scope));
            }
            await using var reconstructed = CreateApplicationFactory(configurationPath).WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(sender);
                }));
            Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
            EmailOutbox? committed = null;
            using (var server = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters))
            {
                if (scenario == "shutdown")
                {
                    await secondRecipientEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await using var pending = await contexts.CreateDbContextAsync();
                    committed = await pending.EmailOutbox.AsNoTracking().SingleAsync(item => item.RecipientStaffUserId == first.Id);
                    server.SendStop();
                }
                else
                {
                    await WaitForScheduledStateAsync(storage, id, scenario == "partial_failure" ? FailedState.StateName : SucceededState.StateName);
                }
            }
            if (scenario == "shutdown")
            {
                Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [HangFire].[JobQueue] WHERE [JobId] = {long.Parse(id)} AND [FetchedAt] IS NULL;"));
                using (var pending = storage.GetConnection())
                {
                    Assert.AreEqual(ProcessingState.StateName, pending.GetStateData(id).Name);
                    Assert.IsFalse(storage.GetMonitoringApi().JobDetails(id).History.Any(item => item.StateName == SucceededState.StateName));
                    pending.SetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter,
                        SerializationHelper.Serialize(DateTime.UtcNow.AddMinutes(-1)));
                }
                timeProvider.SetUtcNow(new DateTimeOffset(nextDay.AddDays(1)));
                await using var resumed = CreateApplicationFactory(configurationPath).WithWebHostBuilder(builder =>
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<IEmailSender>();
                        services.AddSingleton<IEmailSender>(sender);
                    }));
                using var server = StartScheduledFixtureServer(resumed.Services, storage, queue, filters);
                await WaitForScheduledStateAsync(storage, id, SucceededState.StateName);
            }
            await using var verify = await contexts.CreateDbContextAsync();
            var rows = await verify.EmailOutbox.AsNoTracking().Where(item => item.RecipientStaffUserId == first.Id || item.RecipientStaffUserId == second.Id).ToListAsync();
            Assert.HasCount(scenario == "authority_changed" ? 0 : scenario == "partial_failure" ? 1 : 2, rows);
            foreach (var row in rows)
            {
                Assert.AreEqual($"weekly-summary:{row.RecipientStaffUserId}:20300311-20300318", row.BusinessKey);
                Assert.AreEqual("pending", row.Status);
                var expectedCreatedUtc = scenario == "shutdown" && row.RecipientStaffUserId == second.Id ? nextDay.AddDays(1) : nextDay;
                Assert.AreEqual(expectedCreatedUtc, row.CreatedUtc, "Audit timestamps use execution time; reporting identity uses occurrence time.");
            }
            if (committed is not null)
            {
                var preserved = rows.Single(item => item.RecipientStaffUserId == first.Id);
                Assert.AreEqual(committed.Id, preserved.Id);
                CollectionAssert.AreEqual(committed.RowVersion, preserved.RowVersion, "Recovery must retain the committed outbox row without rewriting it.");
            }
            using var connection = storage.GetConnection();
            Assert.AreEqual(occurredUtc, SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.OccurrenceParameter)));
            Assert.AreEqual("1", connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
            Assert.IsNull(connection.GetJobParameter(id, "RetryCount"));
            if (scenario == "partial_failure")
            {
                StringAssert.Contains(connection.GetStateData(id).Data["ExceptionMessage"], "operational_failure (visited 2, changed 1");
                Assert.AreEqual(2, readinessCalls);
                // Reconstructing another worker does not replay failed business work.
                using var nextServer = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters);
                Assert.AreEqual(FailedState.StateName, connection.GetStateData(id).Name);
                Assert.AreEqual(2, readinessCalls);
            }
        }
        finally
        {
            jobs.Delete(id);
            await ExecuteNonQueryAsync("""
                DELETE FROM [asap].[EmailOutbox] WHERE [RecipientStaffUserId] IN (@first, @second);
                DELETE FROM [asap].[TitleRequest] WHERE [Id] = @request;
                UPDATE [asap].[SystemSettings] SET [StaffApplicationUrl] = @url WHERE [OrganizationId] = 1;
                UPDATE [asap].[Organization] SET [IsActive] = 1 WHERE [Id] = @scope;
                """, ("@first", first.Id), ("@second", second.Id), ("@request", requestId),
                ("@url", (object?)staffUrl ?? DBNull.Value), ("@scope", scope));
            await DeactivateCorrectiveStaffAsync(first.Id);
            await DeactivateCorrectiveStaffAsync(second.Id);
            await using var restore = await contexts.CreateDbContextAsync();
            await restore.StaffUsers.Where(item => enabledGlobalRecipients.Contains(item.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.WeeklyActionSummaryEnabled, true));
        }
    }

    [TestMethod]
    public async Task ScheduledIdentifierDefersWhileOrganizationFamilyOwnsGuard()
    {
        var provider = new WaitingOrganizationProvider();
        await using var application = CreateApplicationFactory(configurationPath, provider);
        var services = application.Services;
        var storage = services.GetRequiredService<JobStorage>();
        var queue = $"cross-family-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var refresh = services.GetRequiredService<BackgroundWorkflowJobs>().RefreshOrganizationsAsync(CancellationToken.None);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var id = jobs.Create(Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.ProcessIdentifierAsync(CancellationToken.None)), new EnqueuedState(queue));
        try
        {
            using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
            }
            provider.Release.TrySetResult();
            Assert.AreEqual("completed", (await refresh).Code);
            Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
            using var nextServer = StartScheduledFixtureServer(services, storage, queue, filters);
            await WaitForScheduledStateAsync(storage, id, SucceededState.StateName);
        }
        finally
        {
            provider.Release.TrySetResult();
            await refresh;
            jobs.Delete(id);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledWorkflowShutdownRequeuesWithoutSuccessfulCompletionOrBusinessFailure(bool deferred)
    {
        var provider = new WaitingOrganizationProvider();
        await using var application = CreateApplicationFactory(configurationPath, provider);
        var services = application.Services;
        var storage = services.GetRequiredService<JobStorage>();
        var queue = $"shutdown-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var job = Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.RefreshOrganizationsAsync(CancellationToken.None));
        var id = jobs.Create(job, new EnqueuedState(queue));
        try
        {
            DateTime? admissionDeadline = null;
            var priorFailures = 0;
            if (deferred)
            {
                using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
                using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
                {
                    await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
                }
                using var connection = storage.GetConnection();
                admissionDeadline = SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter));
                Assert.AreEqual("1", connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
                Assert.IsFalse(provider.Entered.Task.IsCompleted, "Guard contention must precede business work.");
                priorFailures = storage.GetMonitoringApi().JobDetails(id).History.Count(item => item.StateName == FailedState.StateName);
                Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
            }
            using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
            {
                await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                server.SendStop();
            }
            await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            DateTime occurredUtc;
            using (var connection = storage.GetConnection())
            {
                // Hangfire returns the SQL queue lease on shutdown while retaining
                // Processing state until the next worker claims this same job.
                Assert.AreEqual(ProcessingState.StateName, connection.GetStateData(id).Name);
                var shutdownHistory = storage.GetMonitoringApi().JobDetails(id).History;
                Assert.AreEqual(priorFailures, shutdownHistory.Count(item => item.StateName == FailedState.StateName));
                Assert.IsFalse(shutdownHistory.Any(item => item.StateName == SucceededState.StateName));
                Assert.AreEqual(deferred ? "1" : null, connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
                occurredUtc = SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.OccurrenceParameter));
                Assert.AreEqual(connection.GetJobData(id).CreatedAt, occurredUtc);
                if (deferred)
                {
                    Assert.AreEqual(admissionDeadline, SerializationHelper.Deserialize<DateTime>(connection.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter)));
                    // Expire the original admission window deterministically after
                    // admission and shutdown, before reconstructing either worker.
                    connection.SetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter,
                        SerializationHelper.Serialize(DateTime.UtcNow.AddMinutes(-1)));
                }
            }
            Assert.AreEqual(1, await ReadCountAsync($"SELECT COUNT(*) FROM [HangFire].[JobQueue] WHERE [JobId] = {long.Parse(id)} AND [FetchedAt] IS NULL;"));
            await using var reconstructed = CreateApplicationFactory(configurationPath);
            if (deferred)
            {
                // Recovery is still serialized with other workflow families even
                // though the original pending-admission deadline is retired.
                using (services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None))
                using (var server = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters))
                {
                    await WaitForScheduledStateAsync(storage, id, ScheduledState.StateName);
                }
                using var pending = storage.GetConnection();
                Assert.AreEqual("1", pending.GetJobParameter(id, ScheduledWorkflowAttribute.DeferralCountParameter));
                Assert.IsTrue(SerializationHelper.Deserialize<DateTime>(pending.GetJobParameter(id, ScheduledWorkflowAttribute.DeadlineParameter)) < DateTime.UtcNow);
                priorFailures = storage.GetMonitoringApi().JobDetails(id).History.Count(item => item.StateName == FailedState.StateName);
                Assert.IsTrue(jobs.ChangeState(id, new EnqueuedState(queue), ScheduledState.StateName));
            }
            using var nextServer = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters);
            await WaitForScheduledStateAsync(storage, id, SucceededState.StateName);
            using var verify = storage.GetConnection();
            Assert.AreEqual(occurredUtc, SerializationHelper.Deserialize<DateTime>(verify.GetJobParameter(id, ScheduledWorkflowAttribute.OccurrenceParameter)));
            var resumedJob = ReadEnqueuedHangfireJob(storage, id);
            Assert.AreEqual(job.Method, resumedJob.Method);
            CollectionAssert.AreEqual(job.Args.ToArray(), resumedJob.Args.ToArray());
            Assert.IsNull(verify.GetJobParameter(id, "RetryCount"));
            var history = storage.GetMonitoringApi().JobDetails(id).History;
            Assert.AreEqual(1, history.Count(item => item.StateName == SucceededState.StateName));
            Assert.AreEqual(priorFailures, history.Count(item => item.StateName == FailedState.StateName));
        }
        finally
        {
            provider.Release.TrySetResult();
            jobs.Delete(id);
        }
    }

    [TestMethod]
    public async Task ScheduledWorkflowAdmissionWriteFailureReleasesGuardWithoutBusinessWorkOrReplay()
    {
        var provider = new WaitingOrganizationProvider();
        await using var application = CreateApplicationFactory(configurationPath, provider);
        var services = application.Services;
        var storage = services.GetRequiredService<JobStorage>();
        var queue = $"admission-write-{Guid.NewGuid():N}";
        var filters = new SavedEmailQueueFilters(queue);
        var jobs = new BackgroundJobClient(storage, filters);
        var id = jobs.Create(Job.FromExpression<BackgroundWorkflowJobs>(worker => worker.RefreshOrganizationsAsync(CancellationToken.None)), new EnqueuedState(queue));
        try
        {
            // Fail only this job's admission write, inside real Hangfire SQL storage.
            await ExecuteNonQueryAsync($"""
                CREATE TRIGGER [HangFire].[FixtureFailWorkflowAdmission] ON [HangFire].[JobParameter]
                AFTER INSERT, UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE [JobId] = {long.Parse(id)} AND [Name] = N'{ScheduledWorkflowAttribute.AdmittedParameter}')
                    BEGIN
                        THROW 51000, 'Controlled admission persistence failure.', 1;
                    END
                END;
                """);
            using (var server = StartScheduledFixtureServer(services, storage, queue, filters))
            {
                await WaitForScheduledStateAsync(storage, id, FailedState.StateName);
            }
            using var connection = storage.GetConnection();
            StringAssert.Contains(connection.GetStateData(id).Data["ExceptionMessage"], "Controlled admission persistence failure");
            Assert.IsNull(connection.GetJobParameter(id, ScheduledWorkflowAttribute.AdmittedParameter));
            Assert.IsNull(connection.GetJobParameter(id, "RetryCount"));
            Assert.IsFalse(provider.Entered.Task.IsCompleted);
            using var guard = services.GetRequiredService<WorkflowProcessingGuard>().TryAcquire(CancellationToken.None);
            Assert.IsNotNull(guard, "Failed persistence must release the acquired shared guard.");
            await using var reconstructed = CreateApplicationFactory(configurationPath, provider);
            using var nextServer = StartScheduledFixtureServer(reconstructed.Services, storage, queue, filters);
            Assert.AreEqual(FailedState.StateName, connection.GetStateData(id).Name);
            Assert.IsFalse(provider.Entered.Task.IsCompleted);
        }
        finally
        {
            await ExecuteNonQueryAsync("DROP TRIGGER IF EXISTS [HangFire].[FixtureFailWorkflowAdmission];");
            provider.Release.TrySetResult();
            jobs.Delete(id);
        }
    }

    private sealed class WaitingOrganizationProvider : IPolarisReferenceProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PolarisConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PolarisConnectionTestResult(true, 1));

        public async Task<IReadOnlyList<PolarisOrganizationSnapshot>> GetOrganizationsAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            return [new(2, "Test Library", "Test", 2, 1)];
        }

        public Task<IReadOnlyList<PolarisPatronCodeSnapshot>> GetPatronCodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PolarisPatronCodeSnapshot>>([]);
    }

    private static BackgroundJobServer StartScheduledFixtureServer(IServiceProvider services, JobStorage storage,
        string queue, IJobFilterProvider filters) => new(new BackgroundJobServerOptions
        {
            Queues = [queue], WorkerCount = 1, Activator = new SavedEmailFixtureActivator(services),
            FilterProvider = filters, SchedulePollingInterval = TimeSpan.FromHours(1),
            CancellationCheckInterval = TimeSpan.FromMilliseconds(100), ShutdownTimeout = TimeSpan.FromSeconds(10)
        }, storage);

    private static async Task WaitForScheduledStateAsync(JobStorage storage, string id, string expected)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            using var connection = storage.GetConnection();
            var state = connection.GetStateData(id);
            if (state?.Name == expected)
            {
                return;
            }
            if (state is not null && state.Name == FailedState.StateName)
            {
                Assert.Fail($"Expected {expected}, job failed: {string.Join(", ", state.Data.Values)}");
            }
            await Task.Delay(50);
        }
        using var final = storage.GetConnection();
        var finalState = final.GetStateData(id);
        Assert.Fail($"Expected {expected}, actual {finalState?.Name}: {string.Join(", ", finalState?.Data.Values ?? [])}");
    }
}
