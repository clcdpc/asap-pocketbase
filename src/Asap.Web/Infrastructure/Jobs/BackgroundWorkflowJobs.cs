using Hangfire;
using Asap.Web.Features.Staff;

namespace Asap.Web.Infrastructure.Jobs;

public sealed record StaffJobEvidence(long StaffUserId, string AuthenticationEmail, Guid TenantId);

public sealed class BackgroundWorkflowJobs(
    WorkflowProcessingService workflow,
    StaffEligibilityService staffEligibility)
{
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [Queue("asap-workflow")]
    [ScheduledWorkflow]
    public async Task<WorkflowRunResult> ProcessWorkflowAsync(
        int? scopeOrganizationId,
        CancellationToken cancellationToken) =>
        ScheduledWorkflowAttribute.RequireCompletion(
            await workflow.ProcessWorkflowAsync(scopeOrganizationId, cancellationToken), cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [Queue("asap-workflow")]
    public async Task<WorkflowRunResult> ProcessManualWorkflowAsync(
        StaffJobEvidence evidence,
        int scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(evidence, scopeOrganizationId, cancellationToken))
        {
            return new WorkflowRunResult("staff_scope_forbidden");
        }

        return await workflow.ProcessWorkflowAsync(
            scopeOrganizationId,
            cancellationToken,
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.AuthenticationEmail, evidence.TenantId));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [Queue("asap-identifier")]
    [ScheduledWorkflow]
    public async Task<WorkflowRunResult> ProcessIdentifierAsync(CancellationToken cancellationToken) =>
        ScheduledWorkflowAttribute.RequireCompletion(
            await workflow.ProcessIdentifierAsync(cancellationToken: cancellationToken), cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    [ScheduledWorkflow]
    public async Task<WorkflowRunResult> RefreshOrganizationsAsync(CancellationToken cancellationToken) =>
        ScheduledWorkflowAttribute.RequireCompletion(
            await workflow.RefreshOrganizationsAsync(cancellationToken), cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    [ScheduledWorkflow]
    public async Task<WorkflowRunResult> SendWeeklyStaffSummaryAsync(
        Guid? manualRunId,
        int? scopeOrganizationId,
        CancellationToken cancellationToken) =>
        ScheduledWorkflowAttribute.RequireCompletion(
            await workflow.SendWeeklyStaffSummaryAsync(manualRunId, scopeOrganizationId, cancellationToken,
                occurrenceUtc: ScheduledWorkflowAttribute.OccurrenceUtc), cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public async Task<WorkflowRunResult> SendManualWeeklyStaffSummaryAsync(
        StaffJobEvidence evidence,
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(evidence, scopeOrganizationId, cancellationToken))
        {
            return new WorkflowRunResult("staff_scope_forbidden");
        }

        return await workflow.SendWeeklyStaffSummaryAsync(
            manualRunId: null,
            scopeOrganizationId,
            cancellationToken,
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.AuthenticationEmail, evidence.TenantId));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public async Task<WorkflowRunResult> SendForcedWeeklyStaffSummaryAsync(
        StaffJobEvidence evidence,
        int? scopeOrganizationId,
        Guid manualRunId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync(evidence, scopeOrganizationId, cancellationToken))
        {
            return new WorkflowRunResult("staff_scope_forbidden", ManualRunId: manualRunId);
        }

        return await workflow.SendWeeklyStaffSummaryAsync(
            manualRunId,
            scopeOrganizationId,
            cancellationToken,
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.AuthenticationEmail, evidence.TenantId));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public Task<int> CleanupSessionsAsync(CancellationToken cancellationToken) =>
        workflow.CleanupSessionsAsync(cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public Task<int> CleanupEmailPayloadsAsync(CancellationToken cancellationToken) =>
        workflow.CleanupEmailPayloadsAsync(cancellationToken);

    private Task<bool> IsAuthorizedAsync(
        StaffJobEvidence evidence,
        int? scopeOrganizationId,
        CancellationToken cancellationToken) =>
        IsAuthorizedCoreAsync(evidence, scopeOrganizationId, cancellationToken);

    private async Task<bool> IsAuthorizedCoreAsync(
        StaffJobEvidence evidence,
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        var result = await staffEligibility.EvaluateAsync(
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.AuthenticationEmail, evidence.TenantId),
            scopeOrganizationId,
            StaffRoleRequirement.Admin,
            requireParticipation: true,
            cancellationToken);
        return result.Outcome == StaffEligibilityOutcome.Allowed;
    }
}
