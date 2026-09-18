using Hangfire;
using Asap.Web.Features.Email;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Development;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Jobs;

public sealed class HangfireWorkerHostedService(
    IHangfireSchemaCompatibilityChecker schemaChecker,
    ExternalConfiguration configuration,
    RuntimeInitializationState initializationState,
    IDbContextFactory<AsapDbContext> contextFactory,
    JobStorage storage,
    ILogger<HangfireWorkerHostedService> logger) : IHostedService, IDisposable
{
    private BackgroundJobServer? server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!initializationState.IsHealthy)
            {
                logger.LogError(
                    "Hangfire worker startup is disabled because application initialization failed with {ErrorCode}.",
                    initializationState.ErrorCode);
                return;
            }

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var appSchemaVersion = await context.SchemaVersions.AsNoTracking()
                .Where(item => item.Id == 1)
                .Select(item => (int?)item.Version)
                .SingleOrDefaultAsync(cancellationToken);
            if (appSchemaVersion != SchemaVersion.ExpectedVersion)
            {
                initializationState.MarkFailed("schema_version_mismatch");
                logger.LogError(
                    "Application schema version {ActualVersion} is incompatible; expected {ExpectedVersion}.",
                    appSchemaVersion,
                    SchemaVersion.ExpectedVersion);
                return;
            }

            var actualVersion = await schemaChecker.GetActualVersionAsync(cancellationToken);
            if (actualVersion != HangfireStorageConfiguration.ExpectedSchemaVersion)
            {
                initializationState.MarkFailed("hangfire_schema_version_mismatch");
                logger.LogError(
                    "Hangfire schema version {ActualVersion} is incompatible; expected {ExpectedVersion}.",
                    actualVersion,
                    HangfireStorageConfiguration.ExpectedSchemaVersion);
                return;
            }

            var schedules = configuration.Hangfire.Schedules!;
            var recurringOptions = new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration.Application.BusinessTimeZone!)
            };
            using (new JobStorageScope(storage))
            {
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-workflow-processing",
                    "asap-workflow",
                    worker => worker.ProcessWorkflowAsync(null, CancellationToken.None),
                    schedules["WorkflowProcessing"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-identifier-processing",
                    "asap-identifier",
                    worker => worker.ProcessIdentifierAsync(CancellationToken.None),
                    schedules["IdentifierProcessing"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-organization-refresh",
                    "asap-admin",
                    worker => worker.RefreshOrganizationsAsync(CancellationToken.None),
                    schedules["OrganizationRefresh"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-weekly-staff-summary",
                    "asap-admin",
                    worker => worker.SendWeeklyStaffSummaryAsync(null, null, CancellationToken.None),
                    schedules["WeeklyStaffSummary"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<EmailOutboxJobs>(
                    "asap-email-outbox-sweep",
                    "asap-email",
                    worker => worker.SweepAsync(CancellationToken.None),
                    schedules["EmailOutboxSweep"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-patron-session-cleanup",
                    "asap-admin",
                    worker => worker.CleanupSessionsAsync(CancellationToken.None),
                    schedules["PatronSessionCleanup"],
                    recurringOptions);
                RecurringJob.AddOrUpdate<BackgroundWorkflowJobs>(
                    "asap-email-payload-cleanup",
                    "asap-admin",
                    worker => worker.CleanupEmailPayloadsAsync(CancellationToken.None),
                    schedules["EmailPayloadCleanup"],
                    recurringOptions);
            }

            server = new BackgroundJobServer(
                new BackgroundJobServerOptions
                {
                    Queues = ["asap-workflow", "asap-identifier", "asap-admin", "asap-email"],
                    WorkerCount = 4,
                    ServerName = $"{Environment.MachineName}:asap-slice5"
                },
                storage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            initializationState.MarkFailed("hangfire_unavailable");
            logger.LogError(exception, "Hangfire worker startup failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        server?.SendStop();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        server?.Dispose();
    }

    private sealed class JobStorageScope : IDisposable
    {
        private readonly JobStorage previous = JobStorage.Current;

        public JobStorageScope(JobStorage storage)
        {
            JobStorage.Current = storage;
        }

        public void Dispose()
        {
            JobStorage.Current = previous;
        }
    }
}
