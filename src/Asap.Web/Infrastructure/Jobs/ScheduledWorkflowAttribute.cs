using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;

namespace Asap.Web.Infrastructure.Jobs;

// Only the four scheduled workflow adapters use this policy. Manual adapters keep
// their authorization and busy feedback; business failures never enter admission replay.
public sealed class ScheduledWorkflowAttribute : JobFilterAttribute, IServerFilter, IElectStateFilter
{
    public const string OccurrenceParameter = "AsapWorkflowOccurrenceUtc";
    public const string DeadlineParameter = "AsapWorkflowAdmissionDeadlineUtc";
    public const string DeferralCountParameter = "AsapWorkflowAdmissionDeferrals";
    public const int MaximumDeferrals = 10;
    public static readonly TimeSpan AdmissionDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(10);
    private static readonly AsyncLocal<DateTime?> occurrence = new();

    public static DateTime? OccurrenceUtc => occurrence.Value;

    public ScheduledWorkflowAttribute()
    {
        // Run after AutomaticRetry (which remains disabled on these methods).
        Order = 30;
    }

    public void OnPerforming(PerformingContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        // Hangfire 1.8 stores the recurring execution's scheduled UTC timestamp in
        // Time, which can precede job creation after scheduler downtime. Existing
        // non-recurring invocations fall back to their durable creation timestamp.
        var scheduledTime = context.GetJobParameter<string>("RecurringJobId") is not null
            ? context.GetJobParameter<long?>("Time") : null;
        var occurredUtc = context.GetJobParameter<DateTime?>(OccurrenceParameter) ??
            (scheduledTime.HasValue ? JobHelper.FromTimestamp(scheduledTime.Value) : context.BackgroundJob.CreatedAt);
        var deadline = context.GetJobParameter<DateTime?>(DeadlineParameter) ?? DateTime.UtcNow.Add(AdmissionWindow);
        context.SetJobParameter(OccurrenceParameter, occurredUtc);
        context.SetJobParameter(DeadlineParameter, deadline);
        if (context.GetJobParameter<int>(DeferralCountParameter) > 0 && DateTime.UtcNow >= deadline)
        {
            throw new InvalidOperationException("Workflow admission deferral exhausted its deadline; the occurrence is incomplete.");
        }
        occurrence.Value = DateTime.SpecifyKind(occurredUtc, DateTimeKind.Utc);
    }

    public void OnPerformed(PerformedContext context)
    {
        occurrence.Value = null;
    }

    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is not FailedState { Exception: WorkflowAdmissionBusyException })
        {
            return;
        }

        var count = context.GetJobParameter<int>(DeferralCountParameter);
        var deadline = context.GetJobParameter<DateTime?>(DeadlineParameter);
        var nextAttempt = DateTime.UtcNow.Add(AdmissionDelay);
        if (count >= MaximumDeferrals || deadline is null || nextAttempt >= deadline.Value)
        {
            context.CandidateState = new FailedState(new InvalidOperationException(
                $"Workflow admission deferral exhausted ({count} deferrals, deadline {deadline:O}); the occurrence is incomplete."))
            {
                Reason = "Workflow admission budget exhausted; business work has not started."
            };
            return;
        }

        // Persist the budget before acknowledging contention. Reuse the same stored
        // job and arguments, so reconstruction cannot reset identity or scope.
        context.SetJobParameter(DeferralCountParameter, count + 1);
        context.CandidateState = new ScheduledState(nextAttempt)
        {
            Reason = $"Workflow guard busy before work; admission deferral {count + 1}/{MaximumDeferrals}."
        };
    }

    public static WorkflowRunResult RequireCompletion(WorkflowRunResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return result.Code switch
        {
            "completed" or "organization_inactive" => result,
            "workflow_processing_busy" when result.Visited == 0 && result.Changed == 0 && result.Skipped == 0 =>
                throw new WorkflowAdmissionBusyException(),
            _ => throw new InvalidOperationException(
                $"Scheduled workflow incomplete: {result.Code} (visited {result.Visited}, changed {result.Changed}, skipped {result.Skipped}). Automatic business replay is disabled.")
        };
    }
}

public sealed class WorkflowAdmissionBusyException() : Exception("Workflow guard busy before business work started.");
