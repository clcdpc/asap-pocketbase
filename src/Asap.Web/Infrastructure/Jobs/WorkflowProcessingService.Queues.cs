using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Configuration;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Jobs;

public sealed partial class WorkflowProcessingService
{
    private async Task<WorkflowRunResult> ProcessTimeoutAsync(
        TimeoutFamily family,
        int? scopeOrganizationId,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        var queue = family.ToString();
        return family == TimeoutFamily.AdditionalCopyTimeout
            ? await ProcessCopyQueueAsync(queue, scopeOrganizationId, manualActorEvidence, cancellationToken)
            : await ProcessTitleQueueAsync(
                queue,
                scopeOrganizationId,
                item => item.Status == StatusFor(family),
                (item, scanScope, expectedProgressVersion, token) =>
                    CloseTitleTimeoutAsync(item, family, scanScope, expectedProgressVersion, manualActorEvidence, token),
                cancellationToken);
    }

    private async Task<WorkflowRunResult> ProcessCopyQueueAsync(
        string queue,
        int? scopeOrganizationId,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        return await ProcessCopyQueueCoreAsync(queue, scopeOrganizationId, manualActorEvidence, cancellationToken);
    }

    private async Task<WorkflowRunResult> ProcessCopyQueueCoreAsync(
        string queue,
        int? scopeOrganizationId,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        var limit = ResolveLimit(queue, timeout: true);
        var state = await BeginCycleAsync(queue, scopeOrganizationId, recovery: false, cancellationToken);
        if (state.Inactive)
        {
            return new WorkflowRunResult("organization_inactive");
        }

        if (state.FenceLost)
        {
            return new WorkflowRunResult("stale_progress_fence");
        }

        if (state.Progress.CycleMaxId == 0)
        {
            return await CompleteCycleAsync(queue, scopeOrganizationId, state.Progress.RowVersion, 0, 0, cancellationToken);
        }

        var visited = 0;
        var changed = 0;
        var expectedVersion = state.Progress.RowVersion;
        while (visited < limit.MaxPerRun!.Value)
        {
            var page = await LoadCopyPageAsync(
                state.Progress,
                scopeOrganizationId,
                Math.Min(limit.PageSize!.Value, limit.MaxPerRun.Value - visited),
                cancellationToken);
            if (page.Count == 0)
            {
                return await CompleteCycleAsync(queue, scopeOrganizationId, expectedVersion, visited, changed, cancellationToken);
            }

            foreach (var candidate in page)
            {
                visited++;
                WorkflowItemResult outcome;
                try
                {
                    outcome = await CloseCopyTimeoutAsync(
                        candidate,
                        QueueProgressService.NormalizeScope(scopeOrganizationId),
                        expectedVersion,
                        manualActorEvidence,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped {QueueName} at copy {CopyRequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }

                if (outcome.FenceLost)
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }

                try
                {
                    expectedVersion = await ApplyOutcomeAsync(
                        queue,
                        scopeOrganizationId,
                        candidate.CreatedUtc,
                        candidate.Id,
                        outcome,
                        expectedVersion,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped {QueueName} at copy checkpoint {CopyRequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }
                if (expectedVersion.Length == 0)
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }
                state.Progress.LastCreatedUtc = candidate.CreatedUtc;
                state.Progress.LastItemId = candidate.Id;
                state.Progress.RowVersion = expectedVersion;
                if (outcome.Changed)
                {
                    changed++;
                }

                if (outcome.Stop)
                {
                    logger.LogError("Operational workflow failure stopped {QueueName} at copy {CopyRequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("operational_failure", visited, changed, visited - changed);
                }
            }
        }

        return new WorkflowRunResult("completed", visited, changed, visited - changed);
    }

    private async Task<WorkflowRunResult> ProcessTitleQueueAsync(
        string queue,
        int? scopeOrganizationId,
        Expression<Func<TitleRequest, bool>> candidateFilter,
        Func<TitleRequest, int, byte[], CancellationToken, Task<WorkflowItemResult>> action,
        CancellationToken cancellationToken)
    {
        var limit = ResolveLimit(queue, queue.Contains("Timeout", StringComparison.Ordinal));
        var state = await BeginCycleAsync(queue, scopeOrganizationId, recovery: false, cancellationToken);
        if (state.Inactive)
        {
            return new WorkflowRunResult("organization_inactive");
        }

        if (state.FenceLost)
        {
            return new WorkflowRunResult("stale_progress_fence");
        }

        if (state.Progress.CycleMaxId == 0)
        {
            return await CompleteCycleAsync(queue, scopeOrganizationId, state.Progress.RowVersion, 0, 0, cancellationToken);
        }

        var visited = 0;
        var changed = 0;
        var expectedVersion = state.Progress.RowVersion;
        var scanScope = QueueProgressService.NormalizeScope(scopeOrganizationId);
        while (visited < limit.MaxPerRun!.Value)
        {
            var page = await LoadTitlePageAsync(
                state.Progress,
                scopeOrganizationId,
                candidateFilter,
                Math.Min(limit.PageSize!.Value, limit.MaxPerRun.Value - visited),
                cancellationToken);
            if (page.Count == 0)
            {
                return await CompleteCycleAsync(queue, scopeOrganizationId, expectedVersion, visited, changed, cancellationToken);
            }

            foreach (var candidate in page)
            {
                visited++;
                WorkflowItemResult outcome;
                try
                {
                    outcome = await action(candidate, scanScope, expectedVersion, cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped {QueueName} at request {RequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }

                if (outcome.FenceLost)
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }

                try
                {
                    expectedVersion = await ApplyOutcomeAsync(
                        queue,
                        scopeOrganizationId,
                        candidate.CreatedUtc,
                        candidate.Id,
                        outcome,
                        expectedVersion,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped {QueueName} at checkpoint {RequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }
                if (expectedVersion.Length == 0)
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }
                state.Progress.LastCreatedUtc = candidate.CreatedUtc;
                state.Progress.LastItemId = candidate.Id;
                state.Progress.RowVersion = expectedVersion;
                if (outcome.Changed)
                {
                    changed++;
                }

                if (outcome.Stop)
                {
                    logger.LogError("Operational workflow failure stopped {QueueName} at request {RequestId}.", queue, candidate.Id);
                    return new WorkflowRunResult("operational_failure", visited, changed, visited - changed);
                }
            }
        }

        return new WorkflowRunResult("completed", visited, changed, visited - changed);
    }

    private async Task<WorkflowRunResult> ProcessRecoveryQueueAsync(
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        var queue = QueueNames.HoldRecovery;
        var limit = ResolveLimit(QueueNames.HoldPlacement, timeout: false);
        var state = await BeginCycleAsync(queue, scopeOrganizationId, recovery: true, cancellationToken);
        if (state.FenceLost)
        {
            return new WorkflowRunResult("stale_progress_fence");
        }

        if (state.Progress.CycleMaxId == 0)
        {
            return await CompleteCycleAsync(queue, scopeOrganizationId, state.Progress.RowVersion, 0, 0, cancellationToken);
        }

        var scopeId = QueueProgressService.NormalizeScope(scopeOrganizationId);
        var visited = 0;
        var changed = 0;
        var expectedVersion = state.Progress.RowVersion;
        while (visited < limit.MaxPerRun!.Value)
        {
            var page = await LoadRecoveryPageAsync(
                state.Progress,
                scopeId,
                Math.Min(limit.PageSize!.Value, limit.MaxPerRun.Value - visited),
                cancellationToken);
            if (page.Count == 0)
            {
                return await CompleteCycleAsync(queue, scopeOrganizationId, expectedVersion, visited, changed, cancellationToken);
            }

            foreach (var operation in page)
            {
                visited++;
                if (!await IsProgressFenceCurrentAsync(queue, scopeOrganizationId, expectedVersion, cancellationToken))
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }

                HoldPlacementResult recovery;
                try
                {
                    recovery = await holdPlacement.RecoverBackgroundOperationAsync(operation.Id, scopeId, cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped hold recovery at operation {OperationId}.", operation.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }

                var outcome = new WorkflowItemResult(
                    recovery.Code,
                    recovery.Code is "updated" or "hold_operator_required",
                    recovery.Code is "hold_provider_error" or "operation_ownership_lost");
                try
                {
                    expectedVersion = await ApplyOutcomeAsync(
                        queue,
                        scopeOrganizationId,
                        operation.RequestStartedUtc,
                        operation.Id,
                        outcome,
                        expectedVersion,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is DbUpdateException or DbException)
                {
                    logger.LogWarning(exception, "SQL failure stopped hold recovery at checkpoint {OperationId}.", operation.Id);
                    return new WorkflowRunResult("sql_failure", visited, changed, visited - changed);
                }
                if (expectedVersion.Length == 0)
                {
                    return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
                }
                state.Progress.LastCreatedUtc = operation.RequestStartedUtc;
                state.Progress.LastItemId = operation.Id;
                state.Progress.RowVersion = expectedVersion;
                if (outcome.Changed)
                {
                    changed++;
                }

                if (outcome.Stop)
                {
                    logger.LogError("Operational hold recovery failure stopped at operation {OperationId}.", operation.Id);
                    return new WorkflowRunResult("operational_failure", visited, changed, visited - changed);
                }
            }
        }

        return new WorkflowRunResult("completed", visited, changed, visited - changed);
    }

    private async Task<WorkflowCycleState> BeginCycleAsync(
        string queue,
        int? scope,
        bool recovery,
        CancellationToken cancellationToken)
    {
        var scopeId = QueueProgressService.NormalizeScope(scope);
        var progress = await progressService.GetOrCreateAsync(queue, scope, cancellationToken);
        if (!recovery && scopeId != 1 && !await IsActiveScopeAsync(scopeId, cancellationToken))
        {
            return new WorkflowCycleState(progress, Inactive: true);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.QueueProgress.SingleAsync(
            item => item.QueueName == queue && item.ScopeOrganizationId == scopeId,
            cancellationToken);
        if (row.CycleMaxId is > 0)
        {
            return new WorkflowCycleState(row);
        }

        var maxId = recovery
            ? await context.HoldPlacementOperations
                .Where(item => item.Id > 0 && item.CompletedUtc == null && item.State != HoldOperationState.OperatorRequired &&
                               (scopeId == 1 || context.TitleRequests.Any(request =>
                                   request.Id == item.TitleRequestId && request.LibraryOrganizationId == scopeId)))
                .Select(item => (long?)item.Id)
                .MaxAsync(cancellationToken)
            : queue == QueueNames.AdditionalCopyTimeout
                ? await context.AdditionalCopyRequests
                    .Where(item => item.Id > 0 && (scopeId == 1 || item.LibraryOrganizationId == scopeId))
                    .Select(item => (long?)item.Id)
                    .MaxAsync(cancellationToken)
                : await context.TitleRequests
                    .Where(item => item.Id > 0 && (scopeId == 1 || item.LibraryOrganizationId == scopeId))
                    .Select(item => (long?)item.Id)
                    .MaxAsync(cancellationToken);
        row.LastCreatedUtc = null;
        row.LastItemId = null;
        row.CycleMaxId = maxId ?? 0;
        row.LastOutcomeItemId = null;
        row.LastOutcomeCode = maxId.HasValue ? "cycle_started" : "cycle_empty";
        row.LastOutcomeUtc = UtcNow();
        row.UpdatedUtc = UtcNow();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new WorkflowCycleState(row, FenceLost: true);
        }
        return new WorkflowCycleState(row);
    }

    private async Task<WorkflowRunResult> CompleteCycleAsync(
        string queue,
        int? scope,
        byte[] expectedVersion,
        int visited,
        int changed,
        CancellationToken cancellationToken)
    {
        var scopeId = QueueProgressService.NormalizeScope(scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.QueueProgress.SingleOrDefaultAsync(
            item => item.QueueName == queue && item.ScopeOrganizationId == scopeId,
            cancellationToken);
        if (row is null || !row.RowVersion.SequenceEqual(expectedVersion))
        {
            return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
        }
        var lastItemId = row.LastItemId;
        row.CycleMaxId = null;
        row.LastCreatedUtc = null;
        row.LastItemId = null;
        row.LastOutcomeItemId = lastItemId;
        row.LastOutcomeCode = "cycle_complete";
        row.LastOutcomeUtc = UtcNow();
        row.UpdatedUtc = UtcNow();
        if (!await SaveWithConcurrencyAsync(context, cancellationToken))
        {
            return new WorkflowRunResult("stale_progress_fence", visited, changed, visited - changed);
        }
        return new WorkflowRunResult("completed", visited, changed, visited - changed);
    }

    private async Task<byte[]?> CheckpointAsync(
        string queue,
        int? scope,
        byte[] expectedVersion,
        DateTime createdUtc,
        long id,
        string outcome,
        CancellationToken cancellationToken)
    {
        var scopeId = QueueProgressService.NormalizeScope(scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = new QueueProgress { QueueName = queue, ScopeOrganizationId = scopeId };
        context.QueueProgress.Attach(row);
        context.Entry(row).Property(item => item.RowVersion).OriginalValue = expectedVersion;
        row.LastCreatedUtc = createdUtc;
        row.LastItemId = id;
        row.LastOutcomeItemId = id;
        row.LastOutcomeCode = outcome;
        row.LastOutcomeUtc = UtcNow();
        row.UpdatedUtc = UtcNow();
        return await SaveWithConcurrencyAsync(context, cancellationToken) ? row.RowVersion : null;
    }

    private async Task<byte[]> ApplyOutcomeAsync(
        string queue,
        int? scope,
        DateTime createdUtc,
        long id,
        WorkflowItemResult outcome,
        byte[] expectedVersion,
        CancellationToken cancellationToken)
    {
        if (outcome.LocalCommit)
        {
            return outcome.ProgressVersion ?? [];
        }

        try
        {
            return await CheckpointAsync(
                queue,
                scope,
                expectedVersion,
                createdUtc,
                id,
                outcome.Code,
                cancellationToken) ?? [];
        }
        catch (DbUpdateConcurrencyException)
        {
            return [];
        }
    }

    private async Task<List<TitleRequest>> LoadTitlePageAsync(
        QueueProgress progress,
        int? scope,
        Expression<Func<TitleRequest, bool>> filter,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.TitleRequests.AsNoTracking().Where(filter);
        if (progress.CycleMaxId.HasValue)
        {
            query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
        }

        var scopeId = QueueProgressService.NormalizeScope(scope);
        if (scopeId != 1)
        {
            query = query.Where(item => item.LibraryOrganizationId == scopeId);
        }

        if (progress.LastCreatedUtc.HasValue)
        {
            var lastCreated = progress.LastCreatedUtc.Value;
            var lastId = progress.LastItemId!.Value;
            query = query.Where(item => item.CreatedUtc > lastCreated ||
                                        item.CreatedUtc == lastCreated && item.Id > lastId);
        }
        return await query
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<AdditionalCopyRequest>> LoadCopyPageAsync(
        QueueProgress progress,
        int? scope,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AdditionalCopyRequests.AsNoTracking().Where(item => item.Status == "open");
        if (progress.CycleMaxId.HasValue)
        {
            query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
        }

        var scopeId = QueueProgressService.NormalizeScope(scope);
        if (scopeId != 1)
        {
            query = query.Where(item => item.LibraryOrganizationId == scopeId);
        }

        if (progress.LastCreatedUtc.HasValue)
        {
            var lastCreated = progress.LastCreatedUtc.Value;
            var lastId = progress.LastItemId!.Value;
            query = query.Where(item => item.CreatedUtc > lastCreated ||
                                        item.CreatedUtc == lastCreated && item.Id > lastId);
        }
        return await query
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<HoldPlacementOperation>> LoadRecoveryPageAsync(
        QueueProgress progress,
        int scope,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.HoldPlacementOperations.AsNoTracking().Where(item =>
            item.CompletedUtc == null && item.State != HoldOperationState.OperatorRequired);
        if (progress.CycleMaxId.HasValue)
        {
            query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
        }

        if (scope != 1)
        {
            query = query.Where(item => context.TitleRequests.Any(request =>
                request.Id == item.TitleRequestId && request.LibraryOrganizationId == scope));
        }
        if (progress.LastCreatedUtc.HasValue)
        {
            var lastCreated = progress.LastCreatedUtc.Value;
            var lastId = progress.LastItemId!.Value;
            query = query.Where(item => item.RequestStartedUtc > lastCreated ||
                                        item.RequestStartedUtc == lastCreated && item.Id > lastId);
        }
        return await query
            .OrderBy(item => item.RequestStartedUtc)
            .ThenBy(item => item.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<bool> IsActiveScopeAsync(int scope, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Organizations.AsNoTracking().AnyAsync(
            item => item.Id == scope && item.IsActive &&
                    (item.Id == LibraryScope.SystemOrganizationId ||
                     item.Id > LibraryScope.SystemOrganizationId &&
                     item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId),
            cancellationToken);
    }

    private async Task<bool> IsProgressFenceCurrentAsync(
        string queue,
        int? scope,
        byte[] expectedVersion,
        CancellationToken cancellationToken)
    {
        var scopeId = QueueProgressService.NormalizeScope(scope);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var progress = await context.QueueProgress.AsNoTracking().SingleOrDefaultAsync(
            item => item.QueueName == queue && item.ScopeOrganizationId == scopeId,
            cancellationToken);
        return progress is not null && progress.RowVersion.SequenceEqual(expectedVersion);
    }

    private async Task<WorkflowItemResult> CommitLocalOutcomeAsync(
        AsapDbContext context,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string queue,
        int scope,
        byte[] expectedProgressVersion,
        DateTime createdUtc,
        long id,
        string code,
        bool changed,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            var version = await AdvanceProgressInTransactionAsync(
                context,
                queue,
                scope,
                expectedProgressVersion,
                createdUtc,
                id,
                code,
                cancellationToken);
            if (version is null)
            {
                return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
            }
            await transaction.CommitAsync(cancellationToken);
            return new WorkflowItemResult(code, changed, LocalCommit: true, ProgressVersion: version);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }
    }

    private async Task<byte[]?> AdvanceProgressInTransactionAsync(
        AsapDbContext context,
        string queue,
        int scope,
        byte[] expectedVersion,
        DateTime createdUtc,
        long id,
        string outcome,
        CancellationToken cancellationToken)
    {
        var row = await context.QueueProgress.FromSqlInterpolated(
                $"SELECT * FROM [asap].[QueueProgress] WITH (UPDLOCK,HOLDLOCK) WHERE [QueueName] = {queue} AND [ScopeOrganizationId] = {scope}")
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null || !row.RowVersion.SequenceEqual(expectedVersion))
        {
            return null;
        }

        row.LastCreatedUtc = createdUtc;
        row.LastItemId = id;
        row.LastOutcomeItemId = id;
        row.LastOutcomeCode = outcome;
        row.LastOutcomeUtc = UtcNow();
        row.UpdatedUtc = UtcNow();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return row.RowVersion;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    private ProcessingLimit ResolveLimit(string queue, bool timeout)
    {
        var queues = configuration.Hangfire.ProcessingLimits.Queues!;
        var selected = queues.TryGetValue(queue, out var q) ? q : null;
        var family = timeout ? configuration.Hangfire.ProcessingLimits.Timeouts : null;
        var global = configuration.Hangfire.ProcessingLimits.Default!;
        return new ProcessingLimit
        {
            PageSize = selected?.PageSize ?? family?.PageSize ?? global.PageSize,
            MaxPerRun = selected?.MaxPerRun ?? family?.MaxPerRun ?? global.MaxPerRun
        };
    }

}
