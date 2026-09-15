using Hangfire;
using Asap.Web.Features.Staff;

namespace Asap.Web.Infrastructure.Jobs;

public sealed record StaffJobEvidence(long StaffUserId, Guid TenantId, Guid ObjectId);

public sealed class BackgroundWorkflowJobs(
    WorkflowProcessingService workflow,
    StaffEligibilityService staffEligibility)
{
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    [Queue("asap-workflow")]
    public Task<WorkflowRunResult> ProcessWorkflowAsync(
        int? scopeOrganizationId,
        CancellationToken cancellationToken) =>
        workflow.ProcessWorkflowAsync(scopeOrganizationId, cancellationToken);

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
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.TenantId, evidence.ObjectId));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [Queue("asap-identifier")]
    public Task<WorkflowRunResult> ProcessIdentifierAsync(CancellationToken cancellationToken) =>
        workflow.ProcessIdentifierAsync(cancellationToken: cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public Task<WorkflowRunResult> RefreshOrganizationsAsync(CancellationToken cancellationToken) =>
        workflow.RefreshOrganizationsAsync(cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public Task<WorkflowRunResult> SendWeeklyStaffSummaryAsync(
        string? manualRunId,
        int? scopeOrganizationId,
        CancellationToken cancellationToken) =>
        workflow.SendWeeklyStaffSummaryAsync(manualRunId, scopeOrganizationId, cancellationToken);

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
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.TenantId, evidence.ObjectId));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 1800)]
    [Queue("asap-admin")]
    public async Task<WorkflowRunResult> SendForcedWeeklyStaffSummaryAsync(
        StaffJobEvidence evidence,
        int? scopeOrganizationId,
        string manualRunId,
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
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.TenantId, evidence.ObjectId));
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
            new StaffIdentityEvidence(evidence.StaffUserId, evidence.TenantId, evidence.ObjectId),
            scopeOrganizationId,
            StaffRoleRequirement.Admin,
            requireParticipation: true,
            cancellationToken);
        return result.Outcome == StaffEligibilityOutcome.Allowed;
    }
}
