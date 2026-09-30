using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Clc.Polaris.Api.Models;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Jobs;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed record HoldPlacementResult(string Code, long? OperationId = null,
    string? FinalStatus = null, string? NotificationStatus = null, string? NotificationReason = null,
    bool ProviderOutcomeRecorded = false, TitleRequestDuplicateConflict? Duplicate = null);

public sealed record ResolveHoldOperationInput(
    string? Version,
    string? RequestVersion,
    string? Outcome,
    string? Reason,
    string? EvidenceKind,
    string? EvidenceReference,
    bool OperationSpecificProofAttested,
    string? ProofSource,
    string? CausalConnection,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int? ProvenFinalHoldId,
    bool OriginalExecutorExcluded,
    bool ExecutorExclusionAttested,
    string? ExecutorExclusionReference,
    string? ExecutorExclusionExplanation);

internal sealed record ProvenHoldIdentityEvidence(
    int HoldRequestId,
    string EvidenceReference);

public sealed class HoldPlacementService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IPatronProvider patronProvider,
    IStaffPolarisProvider staffPolarisProvider,
    ExternalConfiguration configuration,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailOutboxDispatcher outboxDispatcher,
    WorkflowProcessingGuard workflowProcessingGuard,
    TimeProvider timeProvider,
    ILogger<HoldPlacementService> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(60);
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();
    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        configuration.Application.BusinessTimeZone!);

    public async Task<HoldPlacementResult> PlaceAsync(
        CurrentStaff actor,
        long requestId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new HoldPlacementResult("invalid_version");
        }

        var acquisition = await AcquireAsync(
            actor,
            requestId,
            expectedVersion,
            queueName: null,
            queueScope: null,
            expectedQueueVersion: null,
            cancellationToken);
        if (acquisition.Code != "acquired")
        {
            return new HoldPlacementResult(acquisition.Code, acquisition.OperationId, Duplicate: acquisition.Duplicate);
        }
        return await ExecuteOwnedAsync(acquisition.Owner!, actor, null, cancellationToken);
    }

    public async Task<HoldPlacementResult> PlaceBackgroundAsync(
        long requestId,
        byte[] expectedVersion,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        var acquisition = await AcquireAsync(
            null,
            requestId,
            expectedVersion,
            queueName: null,
            queueScope: null,
            expectedQueueVersion: null,
            cancellationToken,
            manualActorEvidence);
        if (acquisition.Code != "acquired")
        {
            return new HoldPlacementResult(acquisition.Code, acquisition.OperationId, Duplicate: acquisition.Duplicate);
        }
        return await ExecuteOwnedAsync(acquisition.Owner!, null, manualActorEvidence, cancellationToken);
    }

    public async Task<HoldPlacementResult> PlaceBackgroundAsync(
        long requestId,
        byte[] expectedVersion,
        string queueName,
        int queueScope,
        byte[] expectedQueueVersion,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        var acquisition = await AcquireAsync(
            null,
            requestId,
            expectedVersion,
            queueName,
            queueScope,
            expectedQueueVersion,
            cancellationToken,
            manualActorEvidence);
        if (acquisition.Code != "acquired")
        {
            return new HoldPlacementResult(acquisition.Code, acquisition.OperationId, Duplicate: acquisition.Duplicate);
        }
        return await ExecuteOwnedAsync(acquisition.Owner!, null, manualActorEvidence, cancellationToken);
    }

    public async Task<int> RecoverBackgroundAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var operationIds = await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => item.CompletedUtc == null && item.State != "operator_required")
            .OrderBy(item => item.RequestStartedUtc)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        var recovered = 0;
        foreach (var operationId in operationIds)
        {
            var result = await RecoverBackgroundOperationAsync(operationId, scopeOrganizationId: null, cancellationToken);
            if (result.Code is "updated" or "hold_operator_required") recovered++;
        }
        return recovered;
    }

    public async Task<HoldPlacementResult> RecoverBackgroundOperationAsync(
        long operationId,
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        var scope = QueueProgressService.NormalizeScope(scopeOrganizationId);
        await using (var preContext = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var operationSnapshot = await preContext.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            var request = operationSnapshot is null
                ? null
                : await preContext.TitleRequests.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == operationSnapshot.TitleRequestId, cancellationToken);
            if (request is null || scope != 1 && request.LibraryOrganizationId != scope)
            {
                return new HoldPlacementResult("recovery_scope_changed", operationId);
            }
        }

        OwnedOperation owner;
        string phase;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var operationSnapshot = await context.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            var requestSnapshot = operationSnapshot is null
                ? null
                : await context.TitleRequests.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == operationSnapshot.TitleRequestId, cancellationToken);
            if (requestSnapshot is null || scope != 1 && requestSnapshot.LibraryOrganizationId != scope)
            {
                return new HoldPlacementResult("recovery_scope_changed", operationId);
            }
            _ = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestSnapshot.LibraryOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            _ = await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestSnapshot.Id}")
                .SingleOrDefaultAsync(cancellationToken);
            var operation = await context.HoldPlacementOperations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (operation is null || operation.CompletedUtc.HasValue) return new HoldPlacementResult("not_found", operationId);
            if (operation.State == "operator_required") return new HoldPlacementResult("hold_operator_required", operationId);
            var now = await SqlClockAsync(context, cancellationToken);
            if (operation.OwnerToken.HasValue && operation.LeaseExpiresUtc > now)
            {
                return new HoldPlacementResult("hold_operation_owned", operationId);
            }
            operation.OwnerToken = Guid.NewGuid();
            operation.ExecutionEpoch++;
            operation.LeaseExpiresUtc = now.Add(LeaseDuration);
            operation.State = operation.Phase is "acquired" or "reply_ready" or "result_recorded"
                ? "in_progress"
                : "ambiguous";
            owner = new OwnedOperation(operation.Id, operation.OwnerToken.Value, operation.ExecutionEpoch, IsRecovery: true);
            phase = operation.Phase;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return phase is "create_started" or "reply_started"
            ? await ObserveMarkedAmbiguityAsync(owner, cancellationToken)
            : await ExecuteOwnedAsync(owner, null, null, cancellationToken);
    }

    public async Task<HoldPlacementResult> ReconcileAsync(
        CurrentStaff actor,
        long operationId,
        VersionInput input,
        CancellationToken cancellationToken)
    {
        if (actor.Role != "super_admin") return new HoldPlacementResult("hold_resolution_forbidden", operationId);
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion))
        {
            return new HoldPlacementResult("invalid_version", operationId);
        }

        using var workflowLease = workflowProcessingGuard.TryAcquire(cancellationToken);
        if (workflowLease is null)
        {
            return new HoldPlacementResult("workflow_processing_busy", operationId);
        }

        HoldPlacementOperation? preOperation;
        await using (var preContext = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            preOperation = await preContext.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
        }
        if (preOperation is null)
        {
            return new HoldPlacementResult("not_found", operationId);
        }
        if (preOperation.CompletedUtc.HasValue)
        {
            return new HoldPlacementResult("hold_operation_completed", operationId);
        }

        OwnedOperation owner;
        string phase;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            var snapshot = await context.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            if (snapshot is null) return new HoldPlacementResult("not_found", operationId);
            var requestSnapshot = await context.TitleRequests.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == snapshot.TitleRequestId, cancellationToken);
            if (requestSnapshot is null) return new HoldPlacementResult("not_found", operationId);
            if (!await LockAuthorityOrganizationsAsync(
                    context, true, requestSnapshot.LibraryOrganizationId, cancellationToken) ||
                context.Organizations.Local.Single(item => item.Id == 1).IsActive == false)
            {
                return new HoldPlacementResult("hold_resolution_forbidden", operationId);
            }
            var staff = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {actor.Id}")
                .SingleOrDefaultAsync(cancellationToken);
            if (staff is null || !IsCurrentSuperAdmin(actor, staff))
            {
                return new HoldPlacementResult("hold_resolution_forbidden", operationId);
            }
            _ = await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {snapshot.TitleRequestId}")
                .SingleAsync(cancellationToken);
            var operation = await context.HoldPlacementOperations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
                .SingleAsync(cancellationToken);
            var now = await SqlClockAsync(context, cancellationToken);
            if (!operation.RowVersion.SequenceEqual(expectedVersion))
            {
                return new HoldPlacementResult("stale_version", operationId);
            }
            if (operation.CompletedUtc.HasValue) return new HoldPlacementResult("hold_operation_completed", operationId);
            if (operation.OwnerToken.HasValue && operation.LeaseExpiresUtc > now)
            {
                return new HoldPlacementResult("hold_operation_owned", operationId);
            }
            operation.OwnerToken = Guid.NewGuid();
            operation.ExecutionEpoch++;
            operation.LeaseExpiresUtc = now.Add(LeaseDuration);
            operation.State = operation.Phase is "acquired" or "reply_ready" or "result_recorded"
                ? "in_progress"
                : "ambiguous";
            owner = new OwnedOperation(operation.Id, operation.OwnerToken.Value, operation.ExecutionEpoch, IsRecovery: true);
            phase = operation.Phase;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return phase is "create_started" or "reply_started"
            ? await ObserveMarkedAmbiguityAsync(owner, cancellationToken)
            : await ExecuteOwnedAsync(owner, actor, null, cancellationToken);
    }

    public async Task<HoldPlacementResult> ResolveAsync(
        CurrentStaff actor,
        long operationId,
        ResolveHoldOperationInput input,
        CancellationToken cancellationToken)
    {
        var outcome = Clean(input.Outcome)?.ToLowerInvariant();
        var reason = Clean(input.Reason);
        var evidenceKind = Clean(input.EvidenceKind)?.ToLowerInvariant();
        var evidenceReference = Clean(input.EvidenceReference);
        var proofSource = Clean(input.ProofSource);
        var causalConnection = Clean(input.CausalConnection);
        var provenFinalHoldId = input.ProvenFinalHoldId;
        var exclusionReference = Clean(input.ExecutorExclusionReference);
        var exclusionExplanation = Clean(input.ExecutorExclusionExplanation);
        if (actor.Role != "super_admin") return new HoldPlacementResult("hold_resolution_forbidden", operationId);
        if (!StaffVersion.TryDecode(input.Version, out var expectedVersion) ||
            !StaffVersion.TryDecode(input.RequestVersion, out var expectedRequestVersion) ||
            outcome is not ("succeeded" or "not_performed") ||
            reason is null or { Length: > 2000 })
        {
            return new HoldPlacementResult("invalid_resolution", operationId);
        }
        var succeededEvidence = evidenceKind is "provider_final_success" or "authoritative_correlated_hold";
        var noEffectEvidence = evidenceKind is "provider_final_no_effect" or "fenced_never_dispatched";
        if (outcome == "succeeded" && !succeededEvidence || outcome == "not_performed" && !noEffectEvidence)
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }
        var serverFencedNeverDispatched = evidenceKind == "fenced_never_dispatched";
        if (!serverFencedNeverDispatched &&
            (evidenceReference is null or { Length: > 1000 } ||
             proofSource is null or { Length: > 500 } ||
             causalConnection is null or { Length: > 2000 } ||
             !input.OperationSpecificProofAttested))
        {
            return new HoldPlacementResult("invalid_resolution", operationId);
        }
        if (serverFencedNeverDispatched && evidenceReference is { Length: > 1000 })
        {
            return new HoldPlacementResult("invalid_resolution", operationId);
        }
        if (provenFinalHoldId is <= 0)
        {
            return new HoldPlacementResult("invalid_resolution", operationId);
        }
        if (evidenceKind == "authoritative_correlated_hold")
        {
            if (provenFinalHoldId is null)
            {
                return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
            }
        }
        else if (provenFinalHoldId is not null)
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }

        using var workflowLease = workflowProcessingGuard.TryAcquire(cancellationToken);
        if (workflowLease is null)
        {
            return new HoldPlacementResult("workflow_processing_busy", operationId);
        }

        HoldPlacementOperation? preOperation;
        TitleRequest? preRequest;
        await using (var preContext = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            preOperation = await preContext.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            preRequest = preOperation is null
                ? null
                : await preContext.TitleRequests.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == preOperation.TitleRequestId, cancellationToken);
            if (preOperation is null || preRequest is null)
            {
                return new HoldPlacementResult("not_found", operationId);
            }
            var currentStaff = await preContext.StaffUsers.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == actor.Id, cancellationToken);
            var systemActive = await preContext.Organizations.AsNoTracking()
                .AnyAsync(item => item.Id == 1 && item.IsActive, cancellationToken);
            if (currentStaff is null || !systemActive || !IsCurrentSuperAdmin(actor, currentStaff))
            {
                return new HoldPlacementResult("hold_resolution_forbidden", operationId);
            }
            if (!preOperation.RowVersion.SequenceEqual(expectedVersion) ||
                !preRequest.RowVersion.SequenceEqual(expectedRequestVersion))
            {
                return new HoldPlacementResult("stale_version", operationId);
            }
        }
        var markedMutation = preOperation.Phase is "create_started" or "reply_started";
        if (markedMutation &&
            (!input.OriginalExecutorExcluded || !input.ExecutorExclusionAttested ||
             exclusionReference is null or { Length: > 1000 } ||
             exclusionExplanation is null or { Length: > 2000 }))
        {
            return new HoldPlacementResult("original_executor_not_excluded", operationId);
        }
        if (serverFencedNeverDispatched &&
            (preOperation.Phase != "acquired" || preOperation.CreateStartedUtc.HasValue || preOperation.ReplyStartedUtc.HasValue))
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }
        if (!serverFencedNeverDispatched && preOperation.Phase == "acquired")
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }

        PatronSnapshot? patron = null;
        EmailTransportReadiness readiness = EmailTransportReadiness.NotConfigured;
        if (outcome == "succeeded")
        {
            try
            {
                patron = await TryRefreshPatronAsync(preOperation.PatronBarcodeSnapshot, cancellationToken);
                readiness = await emailSender.CheckReadinessAsync(preRequest.LibraryOrganizationId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new HoldPlacementResult("hold_resolution_dependency_unavailable", operationId);
            }
        }

        long? outboxId = null;
        EmailOutbox? pendingOutbox = null;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var organizations = new Dictionary<int, Organization>();
        foreach (var organizationId in new[] { 1, preRequest.LibraryOrganizationId }.Distinct().OrderBy(value => value))
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null) return new HoldPlacementResult("not_found", operationId);
            organizations.Add(organizationId, organization);
        }
        var staff = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {actor.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (staff is null || !organizations[1].IsActive || !IsCurrentSuperAdmin(actor, staff))
        {
            return new HoldPlacementResult("hold_resolution_forbidden", operationId);
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {preOperation.TitleRequestId}")
            .SingleAsync(cancellationToken);
        var operation = await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
            .SingleAsync(cancellationToken);
        var now = await SqlClockAsync(context, cancellationToken);
        if (!operation.RowVersion.SequenceEqual(expectedVersion) ||
            !request.RowVersion.SequenceEqual(expectedRequestVersion))
        {
            return new HoldPlacementResult("stale_version", operationId);
        }
        if (request.Id != preRequest.Id || request.LibraryOrganizationId != preRequest.LibraryOrganizationId ||
            operation.TitleRequestId != request.Id || request.Status != "pending_hold" ||
            !string.Equals(request.Barcode, operation.PatronBarcodeSnapshot, StringComparison.Ordinal) ||
            !SameBibIdentity(request.BibId, operation.BibIdSnapshot))
        {
            return new HoldPlacementResult("request_state_conflict", operationId);
        }
        if (operation.State != "operator_required" || operation.CompletedUtc.HasValue)
        {
            return new HoldPlacementResult("hold_resolution_not_allowed", operationId);
        }
        if (operation.OwnerToken.HasValue && operation.LeaseExpiresUtc > now)
        {
            return new HoldPlacementResult("hold_operation_owned", operationId);
        }
        var finalMarkedMutation = operation.Phase is "create_started" or "reply_started";
        if (finalMarkedMutation &&
            (!input.OriginalExecutorExcluded || !input.ExecutorExclusionAttested ||
             exclusionReference is null or { Length: > 1000 } ||
             exclusionExplanation is null or { Length: > 2000 }))
        {
            return new HoldPlacementResult("original_executor_not_excluded", operationId);
        }
        if (serverFencedNeverDispatched &&
            (operation.Phase != "acquired" || operation.CreateStartedUtc.HasValue || operation.ReplyStartedUtc.HasValue))
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }
        if (!serverFencedNeverDispatched && operation.Phase == "acquired" ||
            outcome == "not_performed" && operation.PolarisHoldId is not null)
        {
            return new HoldPlacementResult("unsupported_resolution_evidence", operationId);
        }
        if (provenFinalHoldId is not null && operation.PolarisHoldId is not null &&
            provenFinalHoldId != operation.PolarisHoldId)
        {
            return new HoldPlacementResult("hold_identity_conflict", operationId);
        }

        var epochBefore = operation.ExecutionEpoch;
        var epochAfter = checked(epochBefore + 1);
        var resolutionOwnerToken = Guid.NewGuid();
        var beforeState = new
        {
            operation.State,
            operation.Phase,
            operation.ResultCode,
            operation.OutcomeEvidenceKind,
            operation.LastErrorCode,
            operation.PolarisHoldId,
            ownerToken = operation.OwnerToken?.ToString(),
            operation.LeaseExpiresUtc
        };
        var afterState = outcome == "succeeded" ? "succeeded" : "no_hold";
        var afterPhase = outcome == "succeeded" ? "result_recorded" : operation.Phase;
        var outcomeEvidenceKind = serverFencedNeverDispatched
            ? "server_verified:fenced_never_dispatched"
            : $"operator_verified:{evidenceKind}";
        var details = AddOperatorResolution(
            operation.DetailJson,
            JsonSerializer.SerializeToNode(new
            {
                operationId = operation.Id,
                titleRequestId = request.Id,
                attemptNumber = operation.AttemptNumber,
                phase = operation.Phase,
                frozenPatronBarcodeSha256 = Fingerprint(operation.PatronBarcodeSnapshot),
                frozenBibId = operation.BibIdSnapshot,
                operationVersion = input.Version,
                requestVersion = input.RequestVersion,
                executionEpochBefore = epochBefore,
                executionEpochAfter = epochAfter,
                resolutionOwnerToken,
                verification = serverFencedNeverDispatched ? "server_journal_fence" : "operator_attested_external_evidence",
                outcome,
                reason,
                evidenceKind,
                evidenceReference,
                operationSpecificProofAttested = !serverFencedNeverDispatched && input.OperationSpecificProofAttested,
                proofSource = serverFencedNeverDispatched ? null : proofSource,
                causalConnection = serverFencedNeverDispatched ? null : causalConnection,
                provenFinalHoldId,
                originalExecutorExcluded = finalMarkedMutation && input.OriginalExecutorExcluded,
                executorExclusionAttested = finalMarkedMutation && input.ExecutorExclusionAttested,
                executorExclusionReference = finalMarkedMutation ? exclusionReference : null,
                executorExclusionExplanation = finalMarkedMutation ? exclusionExplanation : null,
                resolvedByStaffUserId = actor.Id,
                resolvedBy = actor.DisplayName ?? actor.UserPrincipalName,
                resolvedUtc = now,
                before = beforeState,
                after = new
                {
                    state = afterState,
                    phase = afterPhase,
                    resultCode = outcome == "succeeded" ? "success" : "definitive_no_effect",
                    outcomeEvidenceKind,
                    polarisHoldId = provenFinalHoldId ?? operation.PolarisHoldId,
                    ownerToken = (string?)null,
                    leaseExpiresUtc = (DateTime?)null,
                    completedUtc = now
                }
            })!.AsObject());
        operation.ExecutionEpoch = epochAfter;
        operation.OwnerToken = resolutionOwnerToken;
        operation.LeaseExpiresUtc = now.Add(LeaseDuration);
        await context.SaveChangesAsync(cancellationToken);

        operation.DetailJson = details;
        operation.OutcomeEvidenceKind = outcomeEvidenceKind;
        operation.LastErrorCode = null;
        operation.OwnerToken = null;
        operation.LeaseExpiresUtc = null;
        operation.CompletedUtc = now;

        if (outcome == "succeeded")
        {
            if (provenFinalHoldId is not null) operation.PolarisHoldId = provenFinalHoldId;
            operation.State = "succeeded";
            operation.Phase = "result_recorded";
            operation.ResultCode = "success";
            operation.LastErrorCode = operation.PolarisHoldId is null ? "hold_identity_unavailable" : null;
            request.Status = "hold_placed";
            request.CloseReason = null;
            request.UpdatedUtc = now;
            const string note = "Hold placement confirmed by operator resolution.";
            request.Notes = AppendNote(request.Notes, note);
            context.TitleRequestEvents.Add(new TitleRequestEvent
            {
                TitleRequestId = request.Id,
                EventType = "hold_placed",
                Status = "hold_placed",
                ActorType = "staff",
                StaffUserId = actor.Id,
                ActorName = actor.DisplayName ?? actor.UserPrincipalName,
                Message = note,
                MetadataJson = details,
                CreatedUtc = now
            });
            pendingOutbox = await AddPatronHoldOutboxAsync(
                context,
                request,
                operation,
                patron?.Email,
                readiness.IsConfigured,
                now,
                cancellationToken);
        }
        else
        {
            operation.State = "no_hold";
            operation.ResultCode = "definitive_no_effect";
            context.TitleRequestEvents.Add(new TitleRequestEvent
            {
                TitleRequestId = request.Id,
                EventType = "hold_operation_resolved_not_performed",
                Status = request.Status,
                ActorType = "staff",
                StaffUserId = actor.Id,
                ActorName = actor.DisplayName ?? actor.UserPrincipalName,
                Message = "Hold operation resolved as not performed.",
                MetadataJson = details,
                CreatedUtc = now
            });
        }
        context.AdministrativeAudits.Add(new AdministrativeAudit
        {
            ActorStaffUserId = actor.Id,
            ActorName = actor.DisplayName ?? actor.UserPrincipalName,
            OrganizationId = request.LibraryOrganizationId,
            Action = outcome == "succeeded" ? "hold_operation_resolved_succeeded" : "hold_operation_resolved_not_performed",
            TargetType = "hold_placement_operation",
            TargetId = operation.Id.ToString(),
            DetailsJson = details,
            CreatedUtc = now
        });
        await context.SaveChangesAsync(cancellationToken);
        outboxId = pendingOutbox?.Status == "pending" ? pendingOutbox.Id : null;
        await transaction.CommitAsync(cancellationToken);
        var notificationStatus = pendingOutbox is null ? "not_applicable" :
            pendingOutbox.Status == "pending" ? "queued" : "suppressed";
        var notificationReason = pendingOutbox?.SuppressionReason;
        if (outboxId.HasValue && !DispatchCommittedOutbox(outboxId.Value, request.Id))
        {
            notificationStatus = "dispatch_failed";
            notificationReason = "queue_unavailable";
        }
        return new HoldPlacementResult("resolved", operationId, request.Status,
            notificationStatus, notificationReason);
    }

    internal async Task<HoldPlacementResult> RecordCompletedIdentityAsync(
        long operationId,
        byte[] expectedRequestVersion,
        byte[] expectedOperationVersion,
        ProvenHoldIdentityEvidence evidence,
        CancellationToken cancellationToken)
    {
        var holdRequestId = evidence.HoldRequestId;
        var evidenceReference = Clean(evidence.EvidenceReference);
        if (expectedRequestVersion.Length == 0 || expectedOperationVersion.Length == 0 ||
            holdRequestId <= 0 ||
            evidenceReference is null or { Length: > 1000 })
        {
            return new HoldPlacementResult("invalid_identity_evidence", operationId);
        }

        HoldPlacementOperation? preOperation;
        TitleRequest? preRequest;
        await using (var preContext = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            preOperation = await preContext.HoldPlacementOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
            preRequest = preOperation is null
                ? null
                : await preContext.TitleRequests.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == preOperation.TitleRequestId, cancellationToken);
        }
        if (preOperation is null || preRequest is null)
        {
            return new HoldPlacementResult("not_found", operationId);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {preRequest.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (organization?.IsActive != true)
        {
            return new HoldPlacementResult("organization_inactive", operationId);
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {preRequest.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null || request.LibraryOrganizationId != organization.Id)
        {
            return new HoldPlacementResult("hold_identity_association_changed", operationId);
        }
        var operation = await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (operation is null || operation.TitleRequestId != request.Id)
        {
            return new HoldPlacementResult("hold_identity_association_changed", operationId);
        }
        if (!request.RowVersion.SequenceEqual(expectedRequestVersion) ||
            !operation.RowVersion.SequenceEqual(expectedOperationVersion))
        {
            return new HoldPlacementResult("stale_version", operationId);
        }
        if (operation.PolarisHoldId is not null)
        {
            return new HoldPlacementResult("hold_identity_already_recorded", operationId);
        }

        var latestSucceededAttempt = await context.HoldPlacementOperations
            .Where(item => item.TitleRequestId == request.Id && item.State == "succeeded")
            .Select(item => (int?)item.AttemptNumber)
            .MaxAsync(cancellationToken);
        if (operation.State != "succeeded" || operation.Phase != "result_recorded" ||
            operation.ResultCode != "success" || !operation.CompletedUtc.HasValue ||
            operation.OwnerToken.HasValue || operation.LeaseExpiresUtc.HasValue ||
            latestSucceededAttempt != operation.AttemptNumber)
        {
            return new HoldPlacementResult("hold_identity_not_enrichable", operationId);
        }
        if (request.Status != "hold_placed" ||
            request.Barcode != operation.PatronBarcodeSnapshot ||
            !SameBibIdentity(request.BibId, operation.BibIdSnapshot))
        {
            return new HoldPlacementResult("hold_identity_association_changed", operationId);
        }

        operation.PolarisHoldId = holdRequestId;
        operation.OutcomeEvidenceKind = "authoritative_provider_operation_correlation";
        operation.LastErrorCode = null;
        operation.DetailJson = AddIdentityEvidence(operation.DetailJson, evidenceReference);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new HoldPlacementResult("stale_version", operationId);
        }
        await transaction.CommitAsync(cancellationToken);
        return new HoldPlacementResult("updated", operationId);
    }

    private async Task<AcquisitionResult> AcquireAsync(
        CurrentStaff? actor,
        long requestId,
        byte[] expectedVersion,
        string? queueName,
        int? queueScope,
        byte[]? expectedQueueVersion,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var snapshot = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (snapshot is null || actor is not null && !TitleRequestViewService.CanAccess(actor, snapshot.LibraryOrganizationId))
        {
            return new AcquisitionResult("not_found");
        }
        if (!await LockAuthorityOrganizationsAsync(
                context, actor is not null || manualActorEvidence is not null,
                snapshot.LibraryOrganizationId, cancellationToken))
        {
            return new AcquisitionResult("staff_scope_forbidden");
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {snapshot.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (organization?.IsActive != true) return new AcquisitionResult("organization_inactive");
        var staff = actor is null
            ? null
            : await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {actor.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        if (actor is not null && (staff is null || !IsCurrentAndEligible(actor, staff, snapshot.LibraryOrganizationId) ||
                                  !IsLockedActorOrganizationActive(context, staff)))
        {
            return new AcquisitionResult("staff_scope_forbidden");
        }
        if (manualActorEvidence is not null)
        {
            var manualStaff = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {manualActorEvidence.StaffUserId}")
                .SingleOrDefaultAsync(cancellationToken);
            var permitted = manualStaff is not null &&
                IsManualEvidenceEligible(manualActorEvidence, manualStaff, snapshot.LibraryOrganizationId) &&
                IsLockedActorOrganizationActive(context, manualStaff);
            if (!permitted) return new AcquisitionResult("staff_scope_forbidden");
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request is null || request.LibraryOrganizationId != snapshot.LibraryOrganizationId)
        {
            return new AcquisitionResult("not_found");
        }
        if (!request.RowVersion.SequenceEqual(expectedVersion)) return new AcquisitionResult("stale_version");
        if (request.Status != "pending_hold" || !request.AutoHold) return new AcquisitionResult("hold_not_eligible");
        if (request.BibId is not > 0)
        {
            return new AcquisitionResult("bib_required");
        }
        var bibId = request.BibId.Value;
        if (!request.BibIdStaffVerified) return new AcquisitionResult("bib_unverified");
        if (string.IsNullOrWhiteSpace(request.Barcode)) return new AcquisitionResult("patron_barcode_missing");

        var existing = await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [TitleRequestId] = {requestId} AND [CompletedUtc] IS NULL")
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return new AcquisitionResult("hold_operation_incomplete", existing.Id);
        var otherActiveRequests = await context.TitleRequests.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == request.LibraryOrganizationId &&
                item.Barcode == request.Barcode && item.Id != request.Id &&
                (item.Status == "pending_hold" || item.Status == "hold_placed") && item.BibId != null)
            .OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Title, item.Status, item.BibId })
            .ToListAsync(cancellationToken);
        var duplicate = otherActiveRequests.FirstOrDefault(other => SameBibIdentity(other.BibId, request.BibId.Value));
        if (duplicate is not null)
        {
            return new AcquisitionResult("duplicate_open_request", Duplicate:
                new TitleRequestDuplicateConflict(duplicate.Id, duplicate.Title, duplicate.Status,
                    duplicate.BibId!.Value, "bibid"));
        }
        var attempt = await context.HoldPlacementOperations
            .Where(item => item.TitleRequestId == requestId)
            .Select(item => (int?)item.AttemptNumber)
            .MaxAsync(cancellationToken) ?? 0;
        var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
        if (settings.PendingHoldTimeoutEnabled == true && settings.PendingHoldTimeoutDays.HasValue &&
            TimeoutSemantics.IsExpired(
                request.UpdatedUtc,
                timeProvider.GetUtcNow(),
                businessTimeZone,
                settings.PendingHoldTimeoutDays.Value))
        {
            return new AcquisitionResult("hold_timeout_due");
        }

        if (queueName is not null)
        {
            var progress = await context.QueueProgress.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[QueueProgress] WITH (UPDLOCK,HOLDLOCK) WHERE [QueueName] = {queueName} AND [ScopeOrganizationId] = {QueueProgressService.NormalizeScope(queueScope)}")
                .SingleOrDefaultAsync(cancellationToken);
            if (progress is null || expectedQueueVersion is null ||
                !progress.RowVersion.SequenceEqual(expectedQueueVersion))
            {
                return new AcquisitionResult("stale_progress_fence");
            }
        }

        var now = await SqlClockAsync(context, cancellationToken);
        var token = Guid.NewGuid();
        var operation = new HoldPlacementOperation
        {
            TitleRequestId = request.Id,
            PatronBarcodeSnapshot = request.Barcode,
            BibIdSnapshot = bibId,
            PickupBranchIdSnapshot = request.PreferredPickupBranchId,
            AttemptNumber = checked(attempt + 1),
            State = "in_progress",
            Phase = "acquired",
            OwnerToken = token,
            ExecutionEpoch = 1,
            LeaseExpiresUtc = now.Add(LeaseDuration),
            RequestStartedUtc = now
        };
        context.HoldPlacementOperations.Add(operation);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AcquisitionResult("acquired", operation.Id, new OwnedOperation(operation.Id, token, 1));
    }

    private async Task<HoldPlacementResult> ExecuteOwnedAsync(
        OwnedOperation owner,
        CurrentStaff? actor,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        var operation = await LoadOperationAsync(owner.Id, cancellationToken);
        if (operation is null || operation.CompletedUtc.HasValue) return new HoldPlacementResult("operation_not_available", owner.Id);

        if (operation.Phase == "result_recorded")
        {
            return await CompleteRecordedResultAsync(owner, actor, null, cancellationToken, manualActorEvidence);
        }
        if (operation.Phase == "reply_ready")
        {
            return await ExecuteReplyAsync(owner, actor, null, cancellationToken, manualActorEvidence);
        }
        if (operation.Phase is "create_started" or "reply_started")
        {
            return await ObserveMarkedAmbiguityAsync(owner, cancellationToken);
        }
        if (operation.Phase != "acquired") return new HoldPlacementResult("operation_phase_invalid", owner.Id);

        var sourceError = await AcquiredSourceErrorAsync(operation, cancellationToken);
        if (sourceError is not null)
        {
            return await RequireOperatorAsync(owner, sourceError, cancellationToken)
                ? new HoldPlacementResult(sourceError, owner.Id)
                : new HoldPlacementResult("operation_ownership_lost", owner.Id);
        }

        PatronSnapshot patron;
        IReadOnlyList<PolarisHoldSnapshot> holds;
        try
        {
            patron = await CallWithLeaseAsync(owner, token => patronProvider.RefreshAsync(operation.PatronBarcodeSnapshot, token), cancellationToken);
            holds = await CallWithLeaseAsync(owner, token => staffPolarisProvider.GetPatronHoldsAsync(operation.PatronBarcodeSnapshot, token), cancellationToken);
        }
        catch (OwnershipLostException)
        {
            return new HoldPlacementResult("operation_ownership_lost", owner.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await FinishWithoutDispatchAsync(owner, "failed", "provider_precheck_failed", exception is ProviderCallTimeoutException ? "provider_timeout" : "provider_read_error", cancellationToken);
            return new HoldPlacementResult("hold_provider_error", owner.Id);
        }

        var activeSameBib = ActiveSameBibHolds(holds, operation.BibIdSnapshot);
        if (activeSameBib.Count == 1)
        {
            if (!await RecordAdoptionAsync(owner, patron, activeSameBib[0], cancellationToken))
            {
                return new HoldPlacementResult("operation_ownership_lost", owner.Id);
            }
            return await CompleteRecordedResultAsync(owner, actor, patron, cancellationToken, manualActorEvidence);
        }
        if (activeSameBib.Count > 1)
        {
            await RequireOperatorAsync(owner, "existing_hold_identity_ambiguous", cancellationToken);
            return new HoldPlacementResult("hold_identity_ambiguous", owner.Id);
        }
        if (patron.PreferredPickupBranchId is not > 0)
        {
            await FinishWithoutDispatchAsync(owner, "no_hold", "pickup_missing", "pickup_missing", cancellationToken);
            return new HoldPlacementResult("pickup_missing", owner.Id);
        }

        PolarisSettings? settings;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            settings = await context.PolarisSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        }
        if (settings?.OrganizationIdForRequests is not > 0 || settings.WorkstationId is not > 0 ||
            settings.SystemPolarisUserId is not > 0)
        {
            await FinishWithoutDispatchAsync(owner, "failed", "polaris_mutation_settings_missing", "polaris_settings_missing", cancellationToken);
            return new HoldPlacementResult("polaris_settings_missing", owner.Id);
        }

        var createStart = await MarkCreateStartedAsync(
            owner, actor, manualActorEvidence, patron, settings, activeSameBib, cancellationToken);
        if (createStart != "started")
        {
            return new HoldPlacementResult(createStart, owner.Id);
        }

        HoldProviderResult createResult;
        try
        {
            createResult = await CallWithLeaseAsync(
                owner,
                token => staffPolarisProvider.CreateHoldAsync(new HoldCreateCommand(
                    patron.PatronId,
                    operation.BibIdSnapshot,
                    patron.PreferredPickupBranchId.Value,
                    settings.OrganizationIdForRequests.Value,
                    settings.WorkstationId.Value,
                    settings.SystemPolarisUserId.Value), token),
                cancellationToken,
                markerRenewedLease: true);
        }
        catch (OwnershipLostException)
        {
            return new HoldPlacementResult("operation_ownership_lost", owner.Id);
        }
        catch (ProviderCallTimeoutException)
        {
            createResult = AmbiguousResult("create_provider_timeout", "provider_timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            createResult = AmbiguousResult("create_provider_exception", SafeProviderErrorCode(exception));
        }

        var persisted = await PersistCreateResultAsync(owner, createResult, cancellationToken);
        if (!persisted) return new HoldPlacementResult("operation_ownership_lost", owner.Id);
        return createResult.Outcome switch
        {
            HoldProviderOutcome.ReplyRequired => await ExecuteReplyAsync(owner, actor, patron, cancellationToken, manualActorEvidence),
            HoldProviderOutcome.FinalSuccess or HoldProviderOutcome.DefinitiveNoEffect =>
                await CompleteRecordedResultAsync(owner, actor, patron, cancellationToken, manualActorEvidence),
            _ => await ObserveMarkedAmbiguityAsync(owner, cancellationToken)
        };
    }

    private async Task<string?> AcquiredSourceErrorAsync(
        HoldPlacementOperation operation,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operation.TitleRequestId, cancellationToken);
        if (request is null || request.Status != "pending_hold" || !request.AutoHold ||
            !SameBibIdentity(request.BibId, operation.BibIdSnapshot))
        {
            return "hold_not_eligible";
        }
        return request.BibIdStaffVerified ? null : "bib_unverified";
    }

    private async Task<HoldPlacementResult> ExecuteReplyAsync(
        OwnedOperation owner,
        CurrentStaff? actor,
        PatronSnapshot? patron,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        var operation = await LoadOperationAsync(owner.Id, cancellationToken);
        if (operation is null || operation.Phase != "reply_ready" ||
            operation.PolarisRequestGuid is null || operation.PolarisRequestGuid == Guid.Empty ||
            string.IsNullOrWhiteSpace(operation.TxnGroupQualifier) ||
            string.IsNullOrWhiteSpace(operation.TxnQualifier) ||
            operation.RequestingOrganizationIdSnapshot is not > 0)
        {
            await RequireOperatorAsync(owner, "reply_context_missing", cancellationToken);
            return new HoldPlacementResult("hold_operator_required", owner.Id);
        }
        var replyStart = await MarkReplyStartedAsync(owner, actor, manualActorEvidence, cancellationToken);
        if (replyStart != "started")
        {
            return new HoldPlacementResult(replyStart, owner.Id, ProviderOutcomeRecorded: true);
        }

        HoldProviderResult replyResult;
        try
        {
            replyResult = await CallWithLeaseAsync(
                owner,
                token => staffPolarisProvider.ReplyToHoldAsync(new HoldReplyCommand(
                    operation.PolarisRequestGuid.Value,
                    operation.TxnGroupQualifier,
                    operation.TxnQualifier,
                    operation.RequestingOrganizationIdSnapshot.Value), token),
                cancellationToken,
                markerRenewedLease: true);
        }
        catch (OwnershipLostException)
        {
            return new HoldPlacementResult("operation_ownership_lost", owner.Id);
        }
        catch (ProviderCallTimeoutException)
        {
            replyResult = AmbiguousResult("reply_provider_timeout", "provider_timeout") with
            {
                RequestGuid = operation.PolarisRequestGuid,
                TxnGroupQualifier = operation.TxnGroupQualifier,
                TxnQualifier = operation.TxnQualifier
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            replyResult = AmbiguousResult("reply_provider_exception", SafeProviderErrorCode(exception)) with
            {
                RequestGuid = operation.PolarisRequestGuid,
                TxnGroupQualifier = operation.TxnGroupQualifier,
                TxnQualifier = operation.TxnQualifier
            };
        }

        if (!await PersistReplyResultAsync(owner, replyResult, cancellationToken))
        {
            return new HoldPlacementResult("operation_ownership_lost", owner.Id);
        }
        return replyResult.Outcome switch
        {
            HoldProviderOutcome.FinalSuccess or HoldProviderOutcome.DefinitiveNoEffect =>
                await CompleteRecordedResultAsync(owner, actor, patron, cancellationToken, manualActorEvidence),
            _ => await ObserveMarkedAmbiguityAsync(owner, cancellationToken)
        };
    }

    private async Task<string> MarkCreateStartedAsync(
        OwnedOperation owner,
        CurrentStaff? actor,
        StaffIdentityEvidence? manualActorEvidence,
        PatronSnapshot patron,
        PolarisSettings settings,
        IReadOnlyList<PolarisHoldSnapshot> baseline,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var detail = JsonSerializer.Serialize(new { preexistingHoldIds = baseline.Select(item => item.HoldRequestId).Order().ToArray() });
        return await MarkProviderStartedAsync(
            context, owner, actor, manualActorEvidence, "acquired",
            async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [Phase] = N'create_started', [CreateStartedUtc] = SYSUTCDATETIME(),
                [LeaseExpiresUtc] = DATEADD(minute, 2, SYSUTCDATETIME()),
                [PatronIdSnapshot] = {patron.PatronId},
                [PickupBranchIdSnapshot] = {patron.PreferredPickupBranchId},
                [RequestingOrganizationIdSnapshot] = {settings.OrganizationIdForRequests},
                [WorkstationIdSnapshot] = {settings.WorkstationId},
                [PolarisUserIdSnapshot] = {settings.SystemPolarisUserId},
                [DetailJson] = {detail}
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [State] = N'in_progress' AND [Phase] = N'acquired'
              AND EXISTS (SELECT 1 FROM [asap].[TitleRequest] AS request
                  WHERE request.[Id] = [TitleRequestId] AND request.[Status] = N'pending_hold'
                    AND request.[AutoHold] = 1 AND request.[BibIdStaffVerified] = 1
                    AND request.[BibId] = [BibIdSnapshot])
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken), cancellationToken);
    }

    private async Task<bool> PersistCreateResultAsync(
        OwnedOperation owner,
        HoldProviderResult result,
        CancellationToken cancellationToken)
    {
        var phase = result.Outcome == HoldProviderOutcome.ReplyRequired ? "reply_ready" :
            result.Outcome is HoldProviderOutcome.FinalSuccess or HoldProviderOutcome.DefinitiveNoEffect
                ? "result_recorded"
                : "create_started";
        var state = result.Outcome == HoldProviderOutcome.Ambiguous ? "ambiguous" : "in_progress";
        var resultCode = ResultCode(result);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var changed = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [Phase] = {phase}, [State] = {state}, [CreateResponseObservedUtc] = SYSUTCDATETIME(),
                [PolarisRequestGuid] = {result.RequestGuid}, [PolarisHoldId] = {result.HoldRequestId},
                [TxnGroupQualifier] = {Clean(result.TxnGroupQualifier)}, [TxnQualifier] = {Clean(result.TxnQualifier)},
                [ReplyAnswer] = CASE WHEN {phase} = N'reply_ready' THEN {(int)HoldRequestReplyAnswer.Yes} ELSE [ReplyAnswer] END,
                [ReplyState] = CASE WHEN {phase} = N'reply_ready' THEN {(int)HoldRequestReplyState.AcceptEvenWithExistingHolds} ELSE [ReplyState] END,
                [ProviderStatusType] = {result.StatusType}, [ProviderStatusValue] = {result.StatusValue},
                [ResultCode] = {resultCode}, [OutcomeEvidenceKind] = {result.EvidenceKind},
                [LastErrorCode] = {result.SafeErrorCode}
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [Phase] = N'create_started' AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken);
        return changed == 1;
    }

    private async Task<string> MarkReplyStartedAsync(
        OwnedOperation owner,
        CurrentStaff? actor,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await MarkProviderStartedAsync(
            context, owner, actor, manualActorEvidence, "reply_ready",
            async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [Phase] = N'reply_started', [ReplyStartedUtc] = SYSUTCDATETIME(),
                [LeaseExpiresUtc] = DATEADD(minute, 2, SYSUTCDATETIME())
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [State] = N'in_progress' AND [Phase] = N'reply_ready'
              AND [PolarisRequestGuid] IS NOT NULL AND [TxnGroupQualifier] IS NOT NULL AND [TxnQualifier] IS NOT NULL
              AND [ReplyAnswer] = {(int)HoldRequestReplyAnswer.Yes} AND [ReplyState] = {(int)HoldRequestReplyState.AcceptEvenWithExistingHolds} AND [LeaseExpiresUtc] > SYSUTCDATETIME()
              AND EXISTS (SELECT 1 FROM [asap].[TitleRequest] AS request
                  WHERE request.[Id] = [TitleRequestId] AND request.[Status] = N'pending_hold'
                    AND request.[AutoHold] = 1 AND request.[BibIdStaffVerified] = 1
                    AND request.[BibId] = [BibIdSnapshot]);
            """,
            cancellationToken), cancellationToken);
    }

    // The staff row stays locked through the durable dispatch marker and lease renewal.
    // The owner is rechecked after the marker, then Polaris is called without another SQL round trip.
    // An authority failure leaves the phase and provider evidence intact.
    private async Task<string> MarkProviderStartedAsync(
        AsapDbContext context,
        OwnedOperation owner,
        CurrentStaff? actor,
        StaffIdentityEvidence? manualActorEvidence,
        string expectedPhase,
        Func<Task<int>> mark,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var operationSnapshot = await context.HoldPlacementOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == owner.Id, cancellationToken);
        var requestSnapshot = operationSnapshot is null
            ? null
            : await context.TitleRequests.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == operationSnapshot.TitleRequestId, cancellationToken);
        if (requestSnapshot is null)
        {
            return "operation_ownership_lost";
        }

        if (actor is not null || manualActorEvidence is not null)
        {
            var organizationsPresent = await LockAuthorityOrganizationsAsync(
                context, true, requestSnapshot.LibraryOrganizationId, cancellationToken);
            var staffId = actor?.Id ?? manualActorEvidence!.StaffUserId;
            var staff = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {staffId}")
                .SingleOrDefaultAsync(cancellationToken);
            var permitted = organizationsPresent && staff is not null &&
                IsLockedActorOrganizationActive(context, staff) &&
                (owner.IsRecovery || context.Organizations.Local
                    .Single(item => item.Id == requestSnapshot.LibraryOrganizationId).IsActive) &&
                (actor is not null
                    ? owner.IsRecovery
                        ? IsCurrentSuperAdmin(actor, staff)
                        : IsCurrentAndEligible(actor, staff, requestSnapshot.LibraryOrganizationId)
                    : IsManualEvidenceEligible(manualActorEvidence!, staff, requestSnapshot.LibraryOrganizationId));
            if (!permitted)
            {
                var released = await ReleaseOwnedOperationAsync(
                    context, owner, expectedPhase, cancellationToken,
                    fenceAcquiredAuthorityRejection: expectedPhase == "acquired");
                await transaction.CommitAsync(cancellationToken);
                return released
                    ? actor is not null && owner.IsRecovery ? "hold_resolution_forbidden" : "staff_scope_forbidden"
                    : "operation_ownership_lost";
            }
        }

        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestSnapshot.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (request?.LibraryOrganizationId != requestSnapshot.LibraryOrganizationId)
        {
            return "operation_ownership_lost";
        }
        var changed = await mark();
        if (changed != 1)
        {
            return "operation_ownership_lost";
        }
        var marked = await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {owner.Id}")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        var now = await SqlClockAsync(context, cancellationToken);
        var startedPhase = expectedPhase == "acquired" ? "create_started" : "reply_started";
        if (marked is null || marked.OwnerToken != owner.Token || marked.ExecutionEpoch != owner.Epoch ||
            marked.State != "in_progress" || marked.Phase != startedPhase ||
            marked.LeaseExpiresUtc is null || marked.LeaseExpiresUtc <= now)
        {
            await transaction.CommitAsync(cancellationToken);
            return "operation_ownership_lost";
        }
        await transaction.CommitAsync(cancellationToken);
        return "started";
    }

    private static async Task<bool> ReleaseOwnedOperationAsync(
        AsapDbContext context,
        OwnedOperation owner,
        string expectedPhase,
        CancellationToken cancellationToken,
        bool fenceAcquiredAuthorityRejection = false) =>
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [State] = CASE WHEN {fenceAcquiredAuthorityRejection} = CAST(1 AS bit)
                    THEN N'operator_required' ELSE [State] END,
                [LastErrorCode] = CASE WHEN {fenceAcquiredAuthorityRejection} = CAST(1 AS bit)
                    THEN N'staff_authority_changed_before_dispatch' ELSE [LastErrorCode] END,
                [OwnerToken] = NULL, [LeaseExpiresUtc] = NULL
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [State] = N'in_progress' AND [Phase] = {expectedPhase} AND [CompletedUtc] IS NULL
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken) == 1;

    private async Task<bool> PersistReplyResultAsync(
        OwnedOperation owner,
        HoldProviderResult result,
        CancellationToken cancellationToken)
    {
        var final = result.Outcome is HoldProviderOutcome.FinalSuccess or HoldProviderOutcome.DefinitiveNoEffect;
        var phase = final ? "result_recorded" : "reply_started";
        var state = final ? "in_progress" : "ambiguous";
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [Phase] = {phase}, [State] = {state}, [ReplyResponseObservedUtc] = SYSUTCDATETIME(),
                [PolarisHoldId] = COALESCE({result.HoldRequestId}, [PolarisHoldId]),
                [ProviderStatusType] = {result.StatusType}, [ProviderStatusValue] = {result.StatusValue},
                [ResultCode] = {ResultCode(result)}, [OutcomeEvidenceKind] = {result.EvidenceKind},
                [LastErrorCode] = {result.SafeErrorCode}
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [Phase] = N'reply_started' AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken) == 1;
    }

    private async Task<bool> RecordAdoptionAsync(
        OwnedOperation owner,
        PatronSnapshot patron,
        PolarisHoldSnapshot hold,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [Phase] = N'result_recorded', [PatronIdSnapshot] = {patron.PatronId},
                [PickupBranchIdSnapshot] = {patron.PreferredPickupBranchId}, [PolarisHoldId] = {hold.HoldRequestId},
                [ResultCode] = N'success', [OutcomeEvidenceKind] = N'existing_hold_adoption',
                [ProviderStatusType] = NULL, [ProviderStatusValue] = NULL,
                [DetailJson] = {JsonSerializer.Serialize(new { adoptedHoldStatusId = hold.StatusId, adoptedHoldStatusDescription = hold.StatusDescription })}
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [State] = N'in_progress' AND [Phase] = N'acquired'
              AND EXISTS (SELECT 1 FROM [asap].[TitleRequest] AS request
                  WHERE request.[Id] = [TitleRequestId] AND request.[Status] = N'pending_hold'
                    AND request.[AutoHold] = 1 AND request.[BibIdStaffVerified] = 1
                    AND request.[BibId] = [BibIdSnapshot])
              AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken) == 1;
    }

    private async Task<HoldPlacementResult> CompleteRecordedResultAsync(
        OwnedOperation owner,
        CurrentStaff? actor,
        PatronSnapshot? patron,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        var snapshot = await LoadOperationAsync(owner.Id, cancellationToken);
        if (snapshot is null) return new HoldPlacementResult("operation_not_available", owner.Id);
        if (snapshot.ResultCode != "success")
        {
            var terminal = snapshot.ResultCode == "definitive_no_effect" ? "no_hold" : "failed";
            await FinishWithoutDispatchAsync(owner, terminal, snapshot.ResultCode ?? "hold_failed", snapshot.LastErrorCode, cancellationToken);
            return new HoldPlacementResult(terminal == "no_hold" ? "hold_not_placed" : "hold_failed", owner.Id);
        }

        patron ??= await TryRefreshPatronAsync(snapshot.PatronBarcodeSnapshot, cancellationToken);
        long? outboxId = null;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var requestSnapshot = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == snapshot.TitleRequestId, cancellationToken);
        if (requestSnapshot is null)
        {
            return new HoldPlacementResult("not_found", owner.Id, ProviderOutcomeRecorded: true);
        }
        EmailTransportReadiness readiness;
        try
        {
            readiness = await emailSender.CheckReadinessAsync(requestSnapshot.LibraryOrganizationId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new HoldPlacementResult("hold_resolution_dependency_unavailable", owner.Id,
                ProviderOutcomeRecorded: true);
        }
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockAuthorityOrganizationsAsync(
                context,
                actor is not null || manualActorEvidence is not null,
                requestSnapshot.LibraryOrganizationId,
                cancellationToken))
        {
            await ReleaseOwnedOperationAsync(context, owner, "result_recorded", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new HoldPlacementResult("staff_scope_forbidden", owner.Id, ProviderOutcomeRecorded: true);
        }
        _ = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {requestSnapshot.LibraryOrganizationId}")
            .SingleAsync(cancellationToken);
        StaffUser? manualStaff = null;
        if (actor is not null || manualActorEvidence is not null)
        {
            var staffId = actor?.Id ?? manualActorEvidence!.StaffUserId;
            manualStaff = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {staffId}")
                .SingleOrDefaultAsync(cancellationToken);
            var permitted = manualStaff is not null &&
                IsLockedActorOrganizationActive(context, manualStaff) &&
                (actor is not null
                    ? owner.IsRecovery
                        ? IsCurrentSuperAdmin(actor, manualStaff)
                        : IsCurrentAndEligible(actor, manualStaff, requestSnapshot.LibraryOrganizationId)
                    : IsManualEvidenceEligible(manualActorEvidence!, manualStaff, requestSnapshot.LibraryOrganizationId));
            if (!permitted)
            {
                await ReleaseOwnedOperationAsync(context, owner, "result_recorded", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new HoldPlacementResult(actor is not null && owner.IsRecovery
                    ? "hold_resolution_forbidden" : "staff_scope_forbidden", owner.Id,
                    ProviderOutcomeRecorded: true);
            }
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {snapshot.TitleRequestId}")
            .SingleAsync(cancellationToken);
        var operation = await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {owner.Id}")
            .SingleAsync(cancellationToken);
        var now = await SqlClockAsync(context, cancellationToken);
        if (operation.OwnerToken != owner.Token || operation.ExecutionEpoch != owner.Epoch ||
            operation.LeaseExpiresUtc <= now || operation.Phase != "result_recorded" || operation.ResultCode != "success")
        {
            return new HoldPlacementResult("operation_ownership_lost", owner.Id,
                ProviderOutcomeRecorded: true);
        }
        var timeoutSettings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
        if (timeoutSettings.PendingHoldTimeoutEnabled == true && timeoutSettings.PendingHoldTimeoutDays.HasValue &&
            TimeoutSemantics.IsExpired(
                request.UpdatedUtc,
                timeProvider.GetUtcNow(),
                businessTimeZone,
                timeoutSettings.PendingHoldTimeoutDays.Value))
        {
            await RequireOperatorInTransactionAsync(operation, now, "hold_timeout_due");
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new HoldPlacementResult("hold_timeout_due", owner.Id, ProviderOutcomeRecorded: true);
        }
        if (request.LibraryOrganizationId != requestSnapshot.LibraryOrganizationId ||
            !SameBibIdentity(request.BibId, operation.BibIdSnapshot) || request.Status != "pending_hold")
        {
            await RequireOperatorInTransactionAsync(operation, now, "request_state_conflict");
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new HoldPlacementResult("hold_operator_required", owner.Id,
                ProviderOutcomeRecorded: true);
        }

        request.Status = "hold_placed";
        request.CloseReason = null;
        request.UpdatedUtc = now;
        var actorName = manualStaff?.DisplayName ?? manualStaff?.UserPrincipalName;
        var note = "Hold placed in Polaris.";
        request.Notes = AppendNote(request.Notes, note);
        context.TitleRequestEvents.Add(new TitleRequestEvent
        {
            TitleRequestId = request.Id,
            EventType = "hold_placed",
            Status = "hold_placed",
            ActorType = actor is null && manualStaff is null ? "system" : "staff",
            StaffUserId = manualStaff?.Id,
            ActorName = actorName,
            Message = note,
            MetadataJson = JsonSerializer.Serialize(new
            {
                operationId = operation.Id,
                attemptNumber = operation.AttemptNumber,
                evidenceKind = operation.OutcomeEvidenceKind,
                holdIdentityAvailable = operation.PolarisHoldId is not null
            }),
            CreatedUtc = now
        });
        operation.State = "succeeded";
        operation.CompletedUtc = now;
        operation.OwnerToken = null;
        operation.LeaseExpiresUtc = null;
        operation.LastErrorCode = operation.PolarisHoldId is null ? "hold_identity_unavailable" : null;
        if (owner.IsRecovery)
        {
            operation.RecoveryAttemptCount++;
            operation.LastRecoveryUtc = now;
        }

        var outbox = await AddPatronHoldOutboxAsync(context, request, operation, patron?.Email, readiness.IsConfigured, now, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        outboxId = outbox?.Status == "pending" ? outbox.Id : null;
        await transaction.CommitAsync(cancellationToken);
        var notificationStatus = outbox is null ? "not_applicable" :
            outbox.Status == "pending" ? "queued" : "suppressed";
        var notificationReason = outbox?.SuppressionReason;
        if (outboxId.HasValue && !DispatchCommittedOutbox(outboxId.Value, request.Id))
        {
            notificationStatus = "dispatch_failed";
            notificationReason = "queue_unavailable";
        }
        return new HoldPlacementResult("updated", owner.Id, request.Status,
            notificationStatus, notificationReason);
    }

    private bool DispatchCommittedOutbox(long outboxId, long requestId)
    {
        try
        {
            outboxDispatcher.Enqueue(outboxId);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hold notification dispatch failed after request {RequestId} committed", requestId);
            return false;
        }
    }

    private async Task<EmailOutbox?> AddPatronHoldOutboxAsync(
        AsapDbContext context,
        TitleRequest request,
        HoldPlacementOperation operation,
        string? recipient,
        bool transportConfigured,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var system = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
        var from = Clean(library?.FromAddress) ?? Clean(system?.FromAddress);
        var fromName = Clean(library?.FromName) ?? Clean(system?.FromName);
        var to = StaffEmail.TryNormalize(recipient, out var normalized) ? normalized : null;
        string? suppression = null;
        if (to is null) suppression = "recipient_missing_or_invalid";
        else if (!recipientDomainPolicy.IsAllowed(to)) suppression = "recipient_domain_not_allowed";
        else if (from is null) suppression = "sender_missing";
        else if (!transportConfigured) suppression = "mail_not_configured";
        var outbox = new EmailOutbox
        {
            OrganizationId = request.LibraryOrganizationId,
            BusinessKey = $"title-hold-placed:{request.Id}:{operation.AttemptNumber}",
            DeliveryClass = "business_event",
            ToAddress = to,
            FromAddress = from,
            FromName = fromName,
            Subject = $"Hold placed: {request.Title}",
            BodyText = $"A hold has been placed for {request.Title} at {request.PreferredPickupBranchName ?? "your selected pickup location"}.",
            Status = suppression is null ? "pending" : "suppressed",
            SuppressionReason = suppression,
            NextAttemptUtc = suppression is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppression is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        return outbox;
    }

    private async Task FinishWithoutDispatchAsync(
        OwnedOperation owner,
        string terminalState,
        string resultCode,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [State] = {terminalState}, [ResultCode] = {resultCode}, [LastErrorCode] = {errorCode},
                [CompletedUtc] = SYSUTCDATETIME(), [OwnerToken] = NULL, [LeaseExpiresUtc] = NULL,
                [RecoveryAttemptCount] = [RecoveryAttemptCount] + CASE WHEN {owner.IsRecovery} = CAST(1 AS bit) THEN 1 ELSE 0 END,
                [LastRecoveryUtc] = CASE WHEN {owner.IsRecovery} = CAST(1 AS bit) THEN SYSUTCDATETIME() ELSE [LastRecoveryUtc] END
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [CompletedUtc] IS NULL AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken);
    }

    private async Task<HoldPlacementResult> ObserveMarkedAmbiguityAsync(
        OwnedOperation owner,
        CancellationToken cancellationToken)
    {
        await RequireOperatorAsync(owner, "hold_identity_ambiguous", cancellationToken);
        return new HoldPlacementResult("hold_operator_required", owner.Id);
    }

    private async Task<bool> RequireOperatorAsync(
        OwnedOperation owner,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [State] = N'operator_required', [RecoveryAttemptCount] = [RecoveryAttemptCount] + 1,
                [LastRecoveryUtc] = SYSUTCDATETIME(), [LastErrorCode] = {errorCode},
                [OwnerToken] = NULL, [LeaseExpiresUtc] = NULL
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [CompletedUtc] IS NULL AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken) == 1;
    }

    private static Task RequireOperatorInTransactionAsync(
        HoldPlacementOperation operation,
        DateTime now,
        string errorCode)
    {
        operation.State = "operator_required";
        operation.RecoveryAttemptCount++;
        operation.LastRecoveryUtc = now;
        operation.LastErrorCode = errorCode;
        operation.OwnerToken = null;
        operation.LeaseExpiresUtc = null;
        return Task.CompletedTask;
    }

    private async Task<T> CallWithLeaseAsync<T>(
        OwnedOperation owner,
        Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken,
        bool markerRenewedLease = false)
    {
        if (!markerRenewedLease && !await RenewLeaseAsync(owner, cancellationToken))
        {
            throw new OwnershipLostException();
        }
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(ProviderTimeout);
        var heartbeat = HeartbeatAsync(owner, execution);
        try
        {
            var result = await call(execution.Token);
            if (heartbeat.OwnershipLost) throw new OwnershipLostException();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (heartbeat.OwnershipLost) throw new OwnershipLostException();
            throw new ProviderCallTimeoutException();
        }
        finally
        {
            execution.Cancel();
            await heartbeat.Completion;
        }
    }

    private LeaseHeartbeat HeartbeatAsync(OwnedOperation owner, CancellationTokenSource execution)
    {
        var heartbeat = new LeaseHeartbeat();
        heartbeat.Completion = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(HeartbeatInterval, execution.Token);
                    if (!await RenewLeaseAsync(owner, execution.Token))
                    {
                        heartbeat.OwnershipLost = true;
                        execution.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        return heartbeat;
    }

    private async Task<bool> RenewLeaseAsync(OwnedOperation owner, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [asap].[HoldPlacementOperation]
            SET [LeaseExpiresUtc] = DATEADD(minute, 2, SYSUTCDATETIME())
            WHERE [Id] = {owner.Id} AND [OwnerToken] = {owner.Token} AND [ExecutionEpoch] = {owner.Epoch}
              AND [CompletedUtc] IS NULL AND [LeaseExpiresUtc] > SYSUTCDATETIME();
            """,
            cancellationToken) == 1;
    }

    private async Task<PatronSnapshot?> TryRefreshPatronAsync(string barcode, CancellationToken cancellationToken)
    {
        try
        {
            return await patronProvider.RefreshAsync(barcode, cancellationToken);
        }
        catch (PolarisOperationalException)
        {
            return null;
        }
    }

    private async Task<HoldPlacementOperation?> LoadOperationAsync(long operationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.HoldPlacementOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
    }

    private static Task<DateTime> SqlClockAsync(AsapDbContext context, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]")
            .SingleAsync(cancellationToken);

    private static string AddIdentityEvidence(string? existingDetailJson, string evidenceReference)
    {
        JsonObject detail;
        if (existingDetailJson is null)
        {
            detail = new JsonObject();
        }
        else
        {
            var existing = JsonNode.Parse(existingDetailJson);
            detail = existing as JsonObject ?? new JsonObject { ["priorOperationDetail"] = existing };
        }
        detail["identityCorrelation"] = new JsonObject
        {
            ["evidenceKind"] = "authoritative_provider_operation_correlation",
            ["evidenceReference"] = evidenceReference
        };
        return detail.ToJsonString();
    }

    private static string AddOperatorResolution(string? existingDetailJson, JsonObject resolution)
    {
        JsonObject detail;
        if (existingDetailJson is null)
        {
            detail = new JsonObject();
        }
        else
        {
            var existing = JsonNode.Parse(existingDetailJson);
            detail = existing as JsonObject ?? new JsonObject { ["priorOperationDetail"] = existing };
        }
        detail["operatorResolution"] = resolution;
        return detail.ToJsonString();
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private bool IsCurrentAndEligible(CurrentStaff actor, StaffUser row, int organizationId) =>
        row.IsActive && allowedTenantIds.Contains(actor.EntraTenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, actor.AuthenticationEmail) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role is "staff" or "admin" && row.OrganizationId == organizationId);

    private bool IsCurrentSuperAdmin(CurrentStaff actor, StaffUser row) =>
        row.IsActive && row.Role == "super_admin" && row.OrganizationId == 1 &&
        allowedTenantIds.Contains(actor.EntraTenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, actor.AuthenticationEmail);

    private bool IsManualEvidenceEligible(StaffIdentityEvidence evidence, StaffUser row, int organizationId) =>
        row.IsActive && allowedTenantIds.Contains(evidence.TenantId) &&
        StaffEmail.MatchesAuthenticationEmail(row, evidence.AuthenticationEmail) &&
        (row.Role == "super_admin" && row.OrganizationId == 1 ||
         row.Role == "admin" && row.OrganizationId == organizationId);

    private static bool IsLockedActorOrganizationActive(AsapDbContext context, StaffUser row) =>
        context.Organizations.Local.SingleOrDefault(item => item.Id == row.OrganizationId)?.IsActive == true;

    private static async Task<bool> LockAuthorityOrganizationsAsync(
        AsapDbContext context,
        bool lockOrganizations,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        if (!lockOrganizations) return true;
        foreach (var organizationId in new[] { 1, targetOrganizationId }.Distinct().Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null) return false;
        }
        return true;
    }

    private static List<PolarisHoldSnapshot> ActiveSameBibHolds(
        IReadOnlyList<PolarisHoldSnapshot> holds,
        int bibId) =>
        holds.Where(item => item.BibId == bibId && !IsTerminal(item.StatusId)).ToList();

    private static bool SameBibIdentity(int? currentBibId, int bibIdSnapshot) =>
        currentBibId is > 0 && currentBibId == bibIdSnapshot;

    private static async Task<WorkflowSettings> EffectiveWorkflowAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var system = await context.WorkflowSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken) ?? new WorkflowSettings();
        var library = organizationId == 1
            ? null
            : await context.WorkflowSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return new WorkflowSettings
        {
            OrganizationId = organizationId,
            PendingHoldTimeoutEnabled = library?.PendingHoldTimeoutEnabled ?? system.PendingHoldTimeoutEnabled,
            PendingHoldTimeoutDays = library?.PendingHoldTimeoutDays ?? system.PendingHoldTimeoutDays
        };
    }

    // Polaris PatronHoldRequestsGet defines 8 as Unclaimed, 9 as Expired, and 16 as Cancelled.
    internal static bool IsTerminal(int statusId) => statusId is 8 or 9 or 16;

    private static string ResultCode(HoldProviderResult result) => result.Outcome switch
    {
        HoldProviderOutcome.FinalSuccess => "success",
        HoldProviderOutcome.DefinitiveNoEffect => "definitive_no_effect",
        HoldProviderOutcome.ReplyRequired => "reply_required",
        _ => "ambiguous"
    };

    private static HoldProviderResult AmbiguousResult(string evidence, string code) => new(
        HoldProviderOutcome.Ambiguous, null, null, null, null, null, null, evidence, code);

    private static string SafeProviderErrorCode(Exception exception) => exception switch
    {
        ProviderCallTimeoutException => "provider_timeout",
        PolarisOperationalException => "provider_operational_error",
        _ => "provider_unknown_error"
    };

    private static string AppendNote(string? current, string note) =>
        string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AcquisitionResult(
        string Code,
        long? OperationId = null,
        OwnedOperation? Owner = null,
        TitleRequestDuplicateConflict? Duplicate = null);
    private sealed record OwnedOperation(long Id, Guid Token, long Epoch, bool IsRecovery = false);
    private sealed class LeaseHeartbeat
    {
        public bool OwnershipLost { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed class OwnershipLostException : Exception;
    private sealed class ProviderCallTimeoutException : Exception;
}
