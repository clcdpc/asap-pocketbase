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

public sealed record WorkflowRunResult(
    string Code,
    int Visited = 0,
    int Changed = 0,
    int Skipped = 0,
    string? ManualRunId = null);

internal sealed record WorkflowItemResult(
    string Code,
    bool Changed = false,
    bool Stop = false,
    bool FenceLost = false,
    bool LocalCommit = false,
    byte[]? ProgressVersion = null);

internal sealed record WorkflowCycleState(
    QueueProgress Progress,
    bool Inactive = false,
    bool FenceLost = false);

internal sealed record WeeklySummaryItem(string Title, string? Author);

public sealed class WorkflowProcessingService(
    IDbContextFactory<AsapDbContext> contextFactory,
    QueueProgressService progressService,
    HoldPlacementService holdPlacement,
    PatronSuggestionService suggestionService,
    IStaffPolarisProvider polaris,
    IPolarisReferenceProvider referenceProvider,
    ExternalConfiguration configuration,
    WorkflowProcessingGuard workflowGuard,
    RecipientDomainPolicy recipientDomainPolicy,
    IEmailSender emailSender,
    IEmailOutboxDispatcher outboxDispatcher,
    TimeProvider timeProvider,
    ILogger<WorkflowProcessingService> logger)
{
    private static readonly string[] IdentifierDerivedTagCodes =
        ["polaris_bib_found", "polaris_bib_not_found", "polaris_multiple_matches"];

    private readonly TimeZoneInfo businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        configuration.Application.BusinessTimeZone!);
    private readonly HashSet<Guid> allowedTenantIds = configuration.Authentication.Entra.AllowedTenantIds!
        .Select(Guid.Parse)
        .ToHashSet();

    public async Task<WorkflowRunResult> ProcessIdentifierAsync(
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null) return new WorkflowRunResult("workflow_processing_busy");
        try
        {
            return await ProcessTitleQueueAsync(
                QueueNames.IdentifierProcessing,
                scopeOrganizationId,
                item => item.Status == "suggestion" && item.IsbnCheckStatus == "pending",
                ProcessIdentifierRowAsync,
                cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            logger.LogError(exception, "SQL failure stopped identifier processing.");
            return new WorkflowRunResult("sql_failure");
        }
    }

    public async Task<WorkflowRunResult> ProcessWorkflowAsync(
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null) return new WorkflowRunResult("workflow_processing_busy");

        var result = new WorkflowRunResult("completed");
        var phases = new Func<Task<WorkflowRunResult>>[]
        {
            () => ProcessRecoveryQueueAsync(scopeOrganizationId, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.OutstandingTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.PendingHoldTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.HoldPickupTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTimeoutAsync(TimeoutFamily.AdditionalCopyTimeout, scopeOrganizationId, manualActorEvidence, cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.PurchasePromotion,
                scopeOrganizationId,
                item => item.Status == "outstanding_purchase",
                (item, scanScope, expectedVersion, token) => PromoteRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.HoldPlacement,
                scopeOrganizationId,
                item => item.Status == "pending_hold" && item.AutoHold,
                (item, scanScope, expectedVersion, token) => PlaceHoldRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken),
            () => ProcessTitleQueueAsync(
                QueueNames.FulfillmentTracking,
                scopeOrganizationId,
                item => item.Status == "hold_placed",
                (item, scanScope, expectedVersion, token) => FulfillRowAsync(item, scanScope, expectedVersion, manualActorEvidence, token),
                cancellationToken)
        };
        foreach (var phase in phases)
        {
            WorkflowRunResult phaseResult;
            try
            {
                phaseResult = await phase();
            }
            catch (Exception exception) when (exception is DbUpdateException or DbException)
            {
                logger.LogError(exception, "SQL failure stopped workflow processing.");
                phaseResult = new WorkflowRunResult("sql_failure");
            }
            result = Add(result, phaseResult);
            if (StopsWorkflow(phaseResult.Code)) break;
        }
        return result;
    }

    public async Task<WorkflowRunResult> RefreshOrganizationsAsync(CancellationToken cancellationToken = default)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null) return new WorkflowRunResult("workflow_processing_busy");
        var snapshots = await referenceProvider.GetOrganizationsAsync(cancellationToken);
        var patronCodes = await referenceProvider.GetPatronCodesAsync(cancellationToken);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var changed = 0;
        foreach (var snapshot in snapshots.Where(item => item.Id > 1))
        {
            var organization = await context.Organizations.SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);
            if (organization is null)
            {
                organization = new Organization
                {
                    Id = snapshot.Id,
                    DisplayName = snapshot.DisplayName,
                    Abbreviation = snapshot.Abbreviation,
                    IsActive = false
                };
                context.Organizations.Add(organization);
                changed++;
                continue;
            }
            if (organization.DisplayName != snapshot.DisplayName || organization.Abbreviation != snapshot.Abbreviation)
            {
                organization.DisplayName = snapshot.DisplayName;
                organization.Abbreviation = snapshot.Abbreviation;
                changed++;
            }
            organization.LastSyncedUtc = UtcNow();
        }
        await context.SaveChangesAsync(cancellationToken);
        return new WorkflowRunResult("completed", snapshots.Count + patronCodes.Count, changed);
    }

    public async Task<WorkflowRunResult> SendWeeklyStaffSummaryAsync(
        string? manualRunId = null,
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null) return new WorkflowRunResult("workflow_processing_busy", ManualRunId: manualRunId);

        var now = UtcNow();
        var localNow = TimeZoneInfo.ConvertTime(new DateTimeOffset(now, TimeSpan.Zero), businessTimeZone);
        var periodEndLocal = localNow.Date.AddDays(1);
        var periodStartLocal = periodEndLocal.AddDays(-7);
        var periodStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodStartLocal, DateTimeKind.Unspecified), businessTimeZone);
        var periodEnd = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodEndLocal, DateTimeKind.Unspecified), businessTimeZone);

        if (manualRunId is not null && manualActorEvidence is not null)
        {
            await using var auditContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var alreadyAudited = await auditContext.AdministrativeAudits.AnyAsync(item =>
                item.Action == "weekly_summary_force_queued" && item.TargetId == manualRunId,
                cancellationToken);
            if (!alreadyAudited)
            {
                auditContext.AdministrativeAudits.Add(new AdministrativeAudit
                {
                    ActorStaffUserId = manualActorEvidence.StaffUserId,
                    ActorName = manualActorEvidence.AuthenticationEmail,
                    OrganizationId = scopeOrganizationId ?? 1,
                    Action = "weekly_summary_force_queued",
                    TargetType = "WeeklyStaffSummary",
                    TargetId = manualRunId,
                    DetailsJson = JsonSerializer.Serialize(new { manualRunId, scopeOrganizationId }),
                    CreatedUtc = now
                });
                await auditContext.SaveChangesAsync(cancellationToken);
            }
        }

        await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await readContext.Organizations.AsNoTracking()
            .Where(item => item.IsActive && item.Id > 1 &&
                           (!scopeOrganizationId.HasValue || item.Id == scopeOrganizationId.Value))
            .ToListAsync(cancellationToken);
        var organizationIds = organizations.Select(item => item.Id).ToHashSet();
        var staff = await readContext.StaffUsers.AsNoTracking()
            .Where(item => item.IsActive && item.WeeklyActionSummaryEnabled &&
                           item.NormalizedUserPrincipalName != null &&
                           (!scopeOrganizationId.HasValue || item.OrganizationId == scopeOrganizationId.Value ||
                            item.Role == "super_admin" && item.OrganizationId == 1))
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var titleRequests = await readContext.TitleRequests.AsNoTracking()
            .Where(item => organizationIds.Contains(item.LibraryOrganizationId) &&
                          (item.Status == "suggestion" || item.Status == "outstanding_purchase"))
            .ToListAsync(cancellationToken);
        var copyRequests = await readContext.AdditionalCopyRequests.AsNoTracking()
            .Where(item => organizationIds.Contains(item.LibraryOrganizationId) && item.Status == "open")
            .ToListAsync(cancellationToken);
        var systemSettings = await readContext.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var staffUrl = systemSettings?.StaffApplicationUrl;
        var createdCount = 0;
        var visited = 0;
        foreach (var recipientSnapshot in staff)
        {
            visited++;
            var authorizationOrganizationId = recipientSnapshot.Role == "super_admin" ? 1 : recipientSnapshot.OrganizationId;
            if (authorizationOrganizationId != 1 && !organizationIds.Contains(authorizationOrganizationId)) continue;
            var recipientRequests = titleRequests.Where(item => authorizationOrganizationId == 1 ||
                item.LibraryOrganizationId == authorizationOrganizationId).ToList();
            var newSubmissions = recipientRequests.Where(item => item.Status == "suggestion")
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).ToList();
            var purchases = recipientRequests.Where(item => item.Status == "outstanding_purchase" && string.IsNullOrWhiteSpace(item.BibId))
                .OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id).ToList();
            var copies = copyRequests.Where(item => authorizationOrganizationId == 1 ||
                item.LibraryOrganizationId == authorizationOrganizationId)
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).ToList();
            if (newSubmissions.Count == 0 && purchases.Count == 0 && copies.Count == 0) continue;
            var readiness = await emailSender.CheckReadinessAsync(authorizationOrganizationId, cancellationToken);
            var businessKey = manualRunId is null
                ? $"weekly-summary:{recipientSnapshot.Id}:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}"
                : $"weekly-summary-force:{manualRunId}:{recipientSnapshot.Id}";
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            if (!await LockManualOrganizationsAsync(
                    context,
                    manualActorEvidence,
                    scopeOrganizationId ?? authorizationOrganizationId,
                    cancellationToken))
            {
                continue;
            }
            var authorization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {authorizationOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            var recipient = await context.StaffUsers.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {recipientSnapshot.Id}")
                .SingleOrDefaultAsync(cancellationToken);
            var manualActorAllowed = await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                scopeOrganizationId ?? authorizationOrganizationId,
                cancellationToken);
            if (authorization is null || !authorization.IsActive || recipient is null || !recipient.IsActive ||
                recipient.OrganizationId != recipientSnapshot.OrganizationId || !recipient.WeeklyActionSummaryEnabled ||
                recipient.NormalizedUserPrincipalName != recipientSnapshot.NormalizedUserPrincipalName ||
                !StaffEmail.IsValidAuthenticationEmail(recipient) ||
                !IsWeeklyRecipientRoleAllowed(recipient, authorizationOrganizationId) ||
                !manualActorAllowed)
            {
                continue;
            }
            if (await context.EmailOutbox.AnyAsync(item => item.BusinessKey == businessKey, cancellationToken)) continue;
            var librarySettings = await context.EmailSettings.AsNoTracking()
                .Where(item => item.OrganizationId == recipient.OrganizationId)
                .Select(item => new { item.FromAddress, item.FromName })
                .SingleOrDefaultAsync(cancellationToken);
            var systemEmail = await context.EmailSettings.AsNoTracking()
                .Where(item => item.OrganizationId == 1)
                .Select(item => new { item.FromAddress, item.FromName })
                .SingleOrDefaultAsync(cancellationToken);
            var fromAddress = Clean(librarySettings?.FromAddress) ?? Clean(systemEmail?.FromAddress);
            var fromName = Clean(librarySettings?.FromName) ?? Clean(systemEmail?.FromName);
            var currentAddress = !string.IsNullOrWhiteSpace(recipient.WeeklyActionSummaryEmail)
                ? recipient.WeeklyActionSummaryEmail
                : recipient.NotificationEmail;
            var currentNormalized = StaffEmail.TryNormalize(currentAddress, out var currentValidAddress)
                ? currentValidAddress
                : null;
            var suppression = currentNormalized is null ? "recipient_missing_or_invalid" :
                !recipientDomainPolicy.IsAllowed(currentNormalized) ? "recipient_domain_not_allowed" :
                string.IsNullOrWhiteSpace(staffUrl) ? "staff_url_missing" :
                string.IsNullOrWhiteSpace(fromAddress) ? "sender_missing" :
                !readiness.IsConfigured ? "mail_not_configured" : null;
            var body = suppression is null ? WeeklyBody(
                newSubmissions, purchases, copies, staffUrl!, periodStartLocal, periodEndLocal) : null;
            var outbox = new EmailOutbox
            {
                OrganizationId = authorizationOrganizationId,
                BusinessKey = businessKey,
                DeliveryClass = "staff_authorization_sensitive",
                RecipientStaffUserId = recipient.Id,
                RecipientAuthenticationEmail = recipient.NormalizedUserPrincipalName,
                AuthorizationOrganizationId = authorizationOrganizationId,
                RecipientAddressKind = "weekly_summary",
                ToAddress = currentNormalized,
                FromAddress = fromAddress,
                FromName = fromName,
                Subject = suppression is null ? $"Weekly ASAP action summary: {newSubmissions.Count} new, {purchases.Count} awaiting bibs, {copies.Count} additional copies" : null,
                BodyText = body,
                Status = suppression is null ? "pending" : "suppressed",
                SuppressionReason = suppression,
                NextAttemptUtc = suppression is null ? now : null,
                CreatedUtc = now,
                SuppressedUtc = suppression is null ? null : now
            };
            context.EmailOutbox.Add(outbox);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (outbox.Status == "pending") outboxDispatcher.Enqueue(outbox.Id);
                createdCount++;
            }
            catch (DbUpdateException exception) when (exception.InnerException is DbException)
            {
                logger.LogInformation(exception, "Weekly summary duplicate or concurrent recipient mutation was ignored.");
            }
        }
        return new WorkflowRunResult("completed", visited, createdCount, visited - createdCount, manualRunId);
    }

    public async Task<int> CleanupSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcNow();
        var rows = await context.PatronSessions
            .Where(item => item.ExpiresUtc <= now || item.RevokedUtc != null && item.RevokedUtc <= now.AddDays(-1))
            .ToListAsync(cancellationToken);
        context.PatronSessions.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    public async Task<int> CleanupEmailPayloadsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = UtcNow().AddDays(-90);
        var rows = await context.EmailOutbox
            .Where(item => (item.Status == "sent" && item.SentUtc < cutoff) ||
                           (item.Status == "suppressed" && item.SuppressedUtc < cutoff))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Subject = null;
            row.BodyText = null;
            row.BodyHtml = null;
        }
        await context.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

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
        if (state.Inactive) return new WorkflowRunResult("organization_inactive");
        if (state.FenceLost) return new WorkflowRunResult("stale_progress_fence");
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
                if (outcome.Changed) changed++;
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
        if (state.Inactive) return new WorkflowRunResult("organization_inactive");
        if (state.FenceLost) return new WorkflowRunResult("stale_progress_fence");
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
                if (outcome.Changed) changed++;
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
        if (state.FenceLost) return new WorkflowRunResult("stale_progress_fence");
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
                if (outcome.Changed) changed++;
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
        if (row.CycleMaxId is > 0) return new WorkflowCycleState(row);

        var maxId = recovery
            ? await context.HoldPlacementOperations
                .Where(item => item.Id > 0 && item.CompletedUtc == null && item.State != "operator_required" &&
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

    private async Task<WorkflowItemResult> ProcessIdentifierRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.Identifier))
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            var request = organization is null
                ? null
                : await context.TitleRequests.FromSqlInterpolated(
                        $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                    .SingleOrDefaultAsync(cancellationToken);
            var eligible = organization?.IsActive == true && request is not null &&
                          request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
                          request.RowVersion.SequenceEqual(candidate.RowVersion) &&
                          request.Status == "suggestion" && request.IsbnCheckStatus == "pending";
            var protectedByHistoryOrOperation = eligible &&
                await IsIdentifierMutationProtectedAsync(context, request!.Id, cancellationToken);
            var changed = eligible && !protectedByHistoryOrOperation;
            if (changed)
            {
                var derivedTags = await (
                    from link in context.TitleRequestWorkflowTags
                    join tag in context.WorkflowTags on link.WorkflowTagId equals tag.Id
                    where link.TitleRequestId == request!.Id && IdentifierDerivedTagCodes.Contains(tag.Code)
                    select link).ToListAsync(cancellationToken);
                context.TitleRequestWorkflowTags.RemoveRange(derivedTags);
                request!.BibId = null;
                request!.IsbnCheckStatus = "skipped_no_isbn";
                request.IsbnCheckRetryCount = 0;
                request.IsbnCheckResult = null;
                request.IsbnCheckLastErrorCode = null;
                request.LastCheckedUtc = null;
                request.UpdatedUtc = UtcNow();
            }
            return await CommitLocalOutcomeAsync(
                context,
                transaction,
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                candidate.CreatedUtc,
                candidate.Id,
                "processed",
                changed,
                cancellationToken);
        }

        if (!await IsProgressFenceCurrentAsync(
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        try
        {
            var lookup = await suggestionService.ProcessIdentifierLookupForQueueAsync(
                candidate.Id,
                candidate.Identifier,
                candidate.LibraryOrganizationId,
                candidate.RowVersion,
                QueueNames.IdentifierProcessing,
                scanScope,
                expectedProgressVersion,
                candidate.CreatedUtc,
                candidate.Id,
                cancellationToken);
            return new WorkflowItemResult(
                lookup.Outcome == IdentifierLookupOutcome.OperationalFailure ? "operational_failure" : "processed",
                Stop: lookup.Outcome == IdentifierLookupOutcome.OperationalFailure,
                LocalCommit: lookup.QueueProgressVersion is not null,
                ProgressVersion: lookup.QueueProgressVersion);
        }
        catch (PolarisOperationalException)
        {
            return new WorkflowItemResult("operational_failure", Stop: true);
        }
    }

    private async Task<WorkflowItemResult> PromoteRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = organization is null
            ? null
            : await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var code = "skipped";
        var changed = false;
        if (organization?.IsActive == true && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == "outstanding_purchase")
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var incomplete = await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            if (incomplete)
            {
                code = "hold_operation_incomplete";
            }
            else if (settings.AutoPromote == true && !string.IsNullOrWhiteSpace(request.BibId))
            {
                request.Status = request.AutoHold ? "pending_hold" : "closed";
                request.CloseReason = request.AutoHold ? null : "purchased_no_hold";
                request.LastPromoterCheckUtc = UtcNow();
                request.UpdatedUtc = UtcNow();
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "promoted",
                    Status = request.Status,
                    CloseReason = request.CloseReason,
                    ActorType = "system",
                    Message = request.AutoHold ? "Purchase promoted to hold placement." : "Purchase completed without an automatic hold.",
                    CreatedUtc = UtcNow()
                });
                code = "changed";
                changed = true;
            }
        }

        return await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.PurchasePromotion,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
    }

    private async Task<WorkflowItemResult> PlaceHoldRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        if (!await IsProgressFenceCurrentAsync(
                QueueNames.HoldPlacement,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        var result = await holdPlacement.PlaceBackgroundAsync(
            candidate.Id,
            candidate.RowVersion,
            QueueNames.HoldPlacement,
            scanScope,
            expectedProgressVersion,
            cancellationToken,
            manualActorEvidence);
        return new WorkflowItemResult(
            result.Code == "updated" ? "changed" : result.Code,
            Changed: result.Code == "updated",
            Stop: result.Code is "hold_provider_error" or "operation_ownership_lost");
    }

    private async Task<WorkflowItemResult> FulfillRowAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        if (!await IsProgressFenceCurrentAsync(
                QueueNames.FulfillmentTracking,
                scanScope,
                expectedProgressVersion,
                cancellationToken))
        {
            return new WorkflowItemResult("stale_progress_fence", FenceLost: true);
        }

        // A bounded HoldPickupTimeout scan may leave a due row for this later phase.
        // Check the current row before calling Polaris so fulfillment cannot outrun it.
        if (await IsFulfillmentTimeoutDueAsync(candidate, cancellationToken))
        {
            return new WorkflowItemResult("deferred_timeout");
        }

        var operation = await LatestTrackedOperationAsync(candidate.Id, cancellationToken);
        IReadOnlyList<PolarisCheckoutSnapshot> checkouts;
        try
        {
            checkouts = await polaris.GetPatronCheckoutsAsync(candidate.Barcode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Patron checkout evidence unavailable for request {RequestId}.", candidate.Id);
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (checkouts.Any(item => item.BibId <= 0 || item.PatronBarcode is not null &&
                                  !string.Equals(item.PatronBarcode, candidate.Barcode, StringComparison.Ordinal)))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken);
        }
        if (checkouts.Any(item => item.BibId.ToString() == candidate.BibId))
        {
            return await CloseFulfilledAsync(
                candidate,
                scanScope,
                new FulfillmentEvidence(null, operation),
                expectedProgressVersion,
                cancellationToken,
                manualActorEvidence);
        }

        IReadOnlyList<PolarisHoldSnapshot> holds;
        try
        {
            holds = await polaris.GetPatronHoldsAsync(candidate.Barcode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Patron hold evidence unavailable for request {RequestId}.", candidate.Id);
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (holds.Any(item => item.HoldRequestId <= 0 || item.BibId <= 0 || item.PatronBarcode is not null &&
                              !string.Equals(item.PatronBarcode, candidate.Barcode, StringComparison.Ordinal)))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_tracking_provider_error",
                stop: true,
                cancellationToken,
                manualActorEvidence);
        }

        if (operation is null || string.IsNullOrWhiteSpace(operation.PolarisHoldId))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_unavailable",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }
        if (operation.BibIdSnapshot != candidate.BibId ||
            !string.Equals(operation.PatronBarcodeSnapshot, candidate.Barcode, StringComparison.Ordinal))
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        var tracked = holds.Where(item => item.HoldRequestId.ToString() == operation.PolarisHoldId).ToList();
        if (tracked.Count != 1)
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                tracked.Count == 0 ? "hold_identity_unavailable" : "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        var trackedHold = tracked[0];
        if (trackedHold.BibId.ToString() != candidate.BibId)
        {
            return await RecordFulfillmentDiagnosticAsync(
                candidate,
                scanScope,
                expectedProgressVersion,
                operation,
                "hold_identity_ambiguous",
                stop: false,
                cancellationToken,
                manualActorEvidence);
        }

        if (!IsTerminal(trackedHold.StatusDescription))
        {
            return new WorkflowItemResult("not_terminal");
        }

        return await CloseFulfilledAsync(
            candidate,
            scanScope,
            new FulfillmentEvidence(TerminalReason(trackedHold.StatusDescription), operation),
            expectedProgressVersion,
            cancellationToken,
            manualActorEvidence);
    }

    private async Task<WorkflowItemResult> CloseTitleTimeoutAsync(
        TitleRequest candidate,
        TimeoutFamily family,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        var readiness = EmailTransportReadiness.NotConfigured;
        if (family == TimeoutFamily.OutstandingTimeout)
        {
            await using var readinessContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            var readinessSettings = await EffectiveWorkflowAsync(
                readinessContext,
                candidate.LibraryOrganizationId,
                cancellationToken);
            if (readinessSettings.OutstandingTimeoutSendEmail == true)
            {
                readiness = await emailSender.CheckReadinessAsync(candidate.LibraryOrganizationId, cancellationToken);
            }
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = organization is null
            ? null
            : await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var code = "skipped";
        var changed = false;
        EmailOutbox? pendingOutbox = null;
        if (organization?.IsActive == true && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == StatusFor(family))
        {
            var incomplete = await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var (enabled, days) = TimeoutSetting(settings, family);
            var age = family == TimeoutFamily.OutstandingTimeout ? request.CreatedUtc : request.UpdatedUtc;
            if (!incomplete && enabled && days.HasValue &&
                TimeoutSemantics.IsExpired(age, timeProvider.GetUtcNow(), businessTimeZone, days.Value))
            {
                request.Status = "closed";
                request.CloseReason = family == TimeoutFamily.HoldPickupTimeout ? "hold_not_picked_up" : "rejected";
                request.UpdatedUtc = UtcNow();
                var note = $"{family} closed this request after the configured timeout.";
                request.Notes = AppendNote(request.Notes, note);
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "timeout_closed",
                    Status = "closed",
                    CloseReason = request.CloseReason,
                    ActorType = "system",
                    Message = note,
                    CreatedUtc = UtcNow()
                });
                if (family == TimeoutFamily.OutstandingTimeout && settings.OutstandingTimeoutSendEmail == true)
                {
                    pendingOutbox = await AddTimeoutEmailAsync(context, request, settings, readiness, cancellationToken);
                }
                code = "changed";
                changed = true;
            }
        }

        var result = await CommitLocalOutcomeAsync(
            context,
            transaction,
            family.ToString(),
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
        if (pendingOutbox?.Status == "pending") outboxDispatcher.Enqueue(pendingOutbox.Id);
        return result;
    }

    private async Task<WorkflowItemResult> CloseCopyTimeoutAsync(
        AdditionalCopyRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        StaffIdentityEvidence? manualActorEvidence,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = organization is null
            ? null
            : await context.AdditionalCopyRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[AdditionalCopyRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var code = "skipped";
        var changed = false;
        if (organization?.IsActive == true && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == "open")
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            if (settings.AdditionalCopyTimeoutEnabled == true && settings.AdditionalCopyTimeoutDays.HasValue &&
                TimeoutSemantics.IsExpired(request.UpdatedUtc, timeProvider.GetUtcNow(), businessTimeZone, settings.AdditionalCopyTimeoutDays.Value))
            {
                request.Status = "closed";
                request.ClosedUtc = UtcNow();
                request.UpdatedUtc = UtcNow();
                request.Notes = AppendNote(request.Notes, "Additional-copy task closed after the configured timeout.");
                code = "changed";
                changed = true;
            }
        }

        return await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.AdditionalCopyTimeout,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
    }

    private async Task<WorkflowItemResult> CloseFulfilledAsync(
        TitleRequest candidate,
        int scanScope,
        FulfillmentEvidence evidence,
        byte[] expectedProgressVersion,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var organization = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = organization is null
            ? null
            : await context.TitleRequests.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
                .SingleOrDefaultAsync(cancellationToken);
        var code = "skipped";
        var changed = false;
        if (organization?.IsActive == true && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == "hold_placed" &&
            request.BibId == candidate.BibId &&
            string.Equals(request.Barcode, candidate.Barcode, StringComparison.Ordinal))
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var (timeoutEnabled, timeoutDays) = TimeoutSetting(settings, TimeoutFamily.HoldPickupTimeout);
            var incomplete = await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            if (incomplete)
            {
                code = "hold_operation_incomplete";
            }
            else if (timeoutEnabled && timeoutDays.HasValue &&
                     TimeoutSemantics.IsExpired(request.UpdatedUtc, timeProvider.GetUtcNow(), businessTimeZone, timeoutDays.Value))
            {
                code = "deferred_timeout";
            }
            else if (evidence.Operation is not null)
            {
                var operation = await LoadLockedOperationAsync(context, evidence.Operation.Id, cancellationToken);
                var latest = await LoadLatestSucceededOperationAsync(context, request.Id, cancellationToken);
                if (operation is null || latest is null || !SameOperationIdentity(latest, evidence.Operation) ||
                    !SameOperationIdentity(operation, evidence.Operation) ||
                    operation.BibIdSnapshot != request.BibId ||
                    !string.Equals(operation.PatronBarcodeSnapshot, request.Barcode, StringComparison.Ordinal))
                {
                    code = "hold_identity_ambiguous";
                }
                else
                {
                    code = "changed";
                    changed = true;
                }
            }
            else
            {
                var latest = await LoadLatestSucceededOperationAsync(context, request.Id, cancellationToken);
                if (latest is not null)
                {
                    code = "hold_identity_ambiguous";
                }
                else
                {
                    code = "changed";
                    changed = true;
                }
            }

            if (changed)
            {
                request.Status = "closed";
                request.CloseReason = evidence.TerminalReason ?? "hold_completed";
                request.UpdatedUtc = UtcNow();
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "fulfilled",
                    Status = "closed",
                    CloseReason = request.CloseReason,
                    ActorType = "system",
                    Message = "Polaris checkout/hold evidence completed this request.",
                    CreatedUtc = UtcNow()
                });
            }
        }

        return await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.FulfillmentTracking,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed,
            cancellationToken);
    }

    private async Task<WorkflowItemResult> RecordFulfillmentDiagnosticAsync(
        TitleRequest candidate,
        int scanScope,
        byte[] expectedProgressVersion,
        HoldPlacementOperation? expectedOperation,
        string code,
        bool stop,
        CancellationToken cancellationToken,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockManualOrganizationsAsync(context, manualActorEvidence, candidate.LibraryOrganizationId, cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        _ = await context.Organizations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.LibraryOrganizationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await IsManualActorAllowedLockedAsync(
                context,
                manualActorEvidence,
                candidate.LibraryOrganizationId,
                cancellationToken))
        {
            return new WorkflowItemResult("staff_scope_forbidden", Stop: true);
        }
        var request = await context.TitleRequests.FromSqlInterpolated(
                $"SELECT * FROM [asap].[TitleRequest] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {candidate.Id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (expectedOperation is not null && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == "hold_placed")
        {
            var operation = await LoadLockedOperationAsync(context, expectedOperation.Id, cancellationToken);
            if (operation is not null && SameOperationIdentity(operation, expectedOperation))
            {
                operation.LastErrorCode = code;
            }
        }

        var result = await CommitLocalOutcomeAsync(
            context,
            transaction,
            QueueNames.FulfillmentTracking,
            scanScope,
            expectedProgressVersion,
            candidate.CreatedUtc,
            candidate.Id,
            code,
            changed: false,
            cancellationToken);
        return result with { Stop = stop };
    }

    private async Task<EmailOutbox?> AddTimeoutEmailAsync(
        AsapDbContext context,
        TitleRequest request,
        WorkflowSettings settings,
        EmailTransportReadiness readiness,
        CancellationToken cancellationToken)
    {
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .Select(item => new { item.FromAddress, item.FromName })
            .SingleOrDefaultAsync(cancellationToken);
        var libraryEmail = request.LibraryOrganizationId == 1
            ? null
            : await context.EmailSettings.AsNoTracking()
                .Where(item => item.OrganizationId == request.LibraryOrganizationId)
                .Select(item => new { item.FromAddress, item.FromName })
                .SingleOrDefaultAsync(cancellationToken);
        var fromAddress = Clean(libraryEmail?.FromAddress) ?? Clean(systemEmail?.FromAddress);
        var fromName = Clean(libraryEmail?.FromName) ?? Clean(systemEmail?.FromName);
        var template = settings.OutstandingTimeoutRejectionTemplateId.HasValue
            ? await context.EmailTemplates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == settings.OutstandingTimeoutRejectionTemplateId.Value, cancellationToken)
            : null;
        var subject = template?.SubjectTemplate?.Replace("{{title}}", request.Title, StringComparison.Ordinal) ?? "Suggestion update";
        var body = template?.BodyTemplate?.Replace("{{title}}", request.Title, StringComparison.Ordinal) ?? "Your suggestion was not selected before the review timeout.";
        var ready = !string.IsNullOrWhiteSpace(request.Email) && !string.IsNullOrWhiteSpace(fromAddress) && readiness.IsConfigured;
        var allowed = ready && recipientDomainPolicy.IsAllowed(request.Email!);
        var outbox = new EmailOutbox
        {
            OrganizationId = request.LibraryOrganizationId,
            BusinessKey = $"timeout:OutstandingTimeout:{request.Id}",
            DeliveryClass = "business_event",
            ToAddress = request.Email,
            FromAddress = fromAddress,
            FromName = fromName,
            Subject = subject,
            BodyText = body,
            Status = allowed ? "pending" : "suppressed",
            SuppressionReason = allowed ? null : ready ? "recipient_domain_not_allowed" : "mail_not_configured",
            NextAttemptUtc = allowed ? UtcNow() : null,
            CreatedUtc = UtcNow(),
            SuppressedUtc = allowed ? null : UtcNow()
        };
        context.EmailOutbox.Add(outbox);
        return allowed ? outbox : null;
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
        if (progress.CycleMaxId.HasValue) query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
        var scopeId = QueueProgressService.NormalizeScope(scope);
        if (scopeId != 1) query = query.Where(item => item.LibraryOrganizationId == scopeId);
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
        if (progress.CycleMaxId.HasValue) query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
        var scopeId = QueueProgressService.NormalizeScope(scope);
        if (scopeId != 1) query = query.Where(item => item.LibraryOrganizationId == scopeId);
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
            item.CompletedUtc == null && item.State != "operator_required");
        if (progress.CycleMaxId.HasValue) query = query.Where(item => item.Id <= progress.CycleMaxId.Value);
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
            item => item.Id == scope && item.IsActive,
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

    private async Task<bool> IsFulfillmentTimeoutDueAsync(
        TitleRequest candidate,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.TitleRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == candidate.Id, cancellationToken);
        if (request is null || request.LibraryOrganizationId != candidate.LibraryOrganizationId ||
            !request.RowVersion.SequenceEqual(candidate.RowVersion) || request.Status != "hold_placed")
        {
            return false;
        }

        var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
        var (enabled, days) = TimeoutSetting(settings, TimeoutFamily.HoldPickupTimeout);
        return enabled && days.HasValue && TimeoutSemantics.IsExpired(
            request.UpdatedUtc,
            timeProvider.GetUtcNow(),
            businessTimeZone,
            days.Value);
    }

    private static async Task<bool> IsIdentifierMutationProtectedAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken)
    {
        if (await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == requestId && item.CompletedUtc == null,
                cancellationToken) ||
            await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == requestId && item.State == "succeeded",
                cancellationToken))
        {
            return true;
        }

        var events = await context.TitleRequestEvents.AsNoTracking()
            .Where(item => item.TitleRequestId == requestId)
            .ToListAsync(cancellationToken);
        return TitleRequestViewService.HasLegacyPlacedProtection(events);
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
        if (row is null || !row.RowVersion.SequenceEqual(expectedVersion)) return null;
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

    private async Task<bool> IsManualActorAllowedLockedAsync(
        AsapDbContext context,
        StaffIdentityEvidence? evidence,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        if (evidence is null) return true;
        var actor = await context.StaffUsers.FromSqlInterpolated(
                $"SELECT * FROM [asap].[StaffUser] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {evidence.StaffUserId}")
            .SingleOrDefaultAsync(cancellationToken);
        return actor is not null && actor.IsActive &&
            allowedTenantIds.Contains(evidence.TenantId) &&
            StaffEmail.MatchesAuthenticationEmail(actor, evidence.AuthenticationEmail) &&
            (actor.Role == "super_admin" && actor.OrganizationId == 1 ||
            actor.Role == "admin" && actor.OrganizationId == targetOrganizationId);
    }

    private static async Task<bool> LockManualOrganizationsAsync(
        AsapDbContext context,
        StaffIdentityEvidence? evidence,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        if (evidence is null) return true;
        foreach (var organizationId in new[] { 1, targetOrganizationId }.Distinct().Order())
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {organizationId}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null) return false;
        }
        return true;
    }

    private async Task<WorkflowSettings> EffectiveWorkflowAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken = default)
    {
        var system = await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken) ?? new WorkflowSettings();
        var library = organizationId == 1 ? null : await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return new WorkflowSettings
        {
            OrganizationId = organizationId,
            AutoPromote = library?.AutoPromote ?? system.AutoPromote,
            OutstandingTimeoutEnabled = library?.OutstandingTimeoutEnabled ?? system.OutstandingTimeoutEnabled,
            OutstandingTimeoutDays = library?.OutstandingTimeoutDays ?? system.OutstandingTimeoutDays,
            OutstandingTimeoutSendEmail = library?.OutstandingTimeoutSendEmail ?? system.OutstandingTimeoutSendEmail,
            OutstandingTimeoutRejectionTemplateId = library?.OutstandingTimeoutRejectionTemplateId ?? system.OutstandingTimeoutRejectionTemplateId,
            PendingHoldTimeoutEnabled = library?.PendingHoldTimeoutEnabled ?? system.PendingHoldTimeoutEnabled,
            PendingHoldTimeoutDays = library?.PendingHoldTimeoutDays ?? system.PendingHoldTimeoutDays,
            HoldPickupTimeoutEnabled = library?.HoldPickupTimeoutEnabled ?? system.HoldPickupTimeoutEnabled,
            HoldPickupTimeoutDays = library?.HoldPickupTimeoutDays ?? system.HoldPickupTimeoutDays,
            AdditionalCopyTimeoutEnabled = library?.AdditionalCopyTimeoutEnabled ?? system.AdditionalCopyTimeoutEnabled,
            AdditionalCopyTimeoutDays = library?.AdditionalCopyTimeoutDays ?? system.AdditionalCopyTimeoutDays
        };
    }

    private async Task<HoldPlacementOperation?> LatestTrackedOperationAsync(
        long requestId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.HoldPlacementOperations.AsNoTracking()
            .Where(item => item.TitleRequestId == requestId && item.State == "succeeded" && item.CompletedUtc != null)
            .OrderByDescending(item => item.AttemptNumber)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<HoldPlacementOperation?> LoadLockedOperationAsync(
        AsapDbContext context,
        long operationId,
        CancellationToken cancellationToken) =>
        await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {operationId}")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<HoldPlacementOperation?> LoadLatestSucceededOperationAsync(
        AsapDbContext context,
        long requestId,
        CancellationToken cancellationToken) =>
        await context.HoldPlacementOperations.FromSqlInterpolated(
                $"SELECT TOP (1) * FROM [asap].[HoldPlacementOperation] WITH (UPDLOCK,HOLDLOCK) WHERE [TitleRequestId] = {requestId} AND [State] = N'succeeded' AND [CompletedUtc] IS NOT NULL ORDER BY [AttemptNumber] DESC, [Id] DESC")
            .SingleOrDefaultAsync(cancellationToken);

    private static bool SameOperationIdentity(HoldPlacementOperation current, HoldPlacementOperation expected) =>
        current.Id == expected.Id && current.AttemptNumber == expected.AttemptNumber &&
        current.State == "succeeded" && current.CompletedUtc.HasValue &&
        current.RowVersion.SequenceEqual(expected.RowVersion) &&
        string.Equals(current.PatronBarcodeSnapshot, expected.PatronBarcodeSnapshot, StringComparison.Ordinal) &&
        string.Equals(current.BibIdSnapshot, expected.BibIdSnapshot, StringComparison.Ordinal) &&
        string.Equals(current.PolarisHoldId, expected.PolarisHoldId, StringComparison.Ordinal);

    private sealed record FulfillmentEvidence(
        string? TerminalReason,
        HoldPlacementOperation? Operation);

    private static (bool Enabled, int? Days) TimeoutSetting(WorkflowSettings settings, TimeoutFamily family) => family switch
    {
        TimeoutFamily.OutstandingTimeout => (settings.OutstandingTimeoutEnabled == true, settings.OutstandingTimeoutDays),
        TimeoutFamily.PendingHoldTimeout => (settings.PendingHoldTimeoutEnabled == true, settings.PendingHoldTimeoutDays),
        TimeoutFamily.HoldPickupTimeout => (settings.HoldPickupTimeoutEnabled == true, settings.HoldPickupTimeoutDays),
        TimeoutFamily.AdditionalCopyTimeout => (settings.AdditionalCopyTimeoutEnabled == true, settings.AdditionalCopyTimeoutDays),
        _ => (false, null)
    };

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

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string StatusFor(TimeoutFamily family) => family switch
    {
        TimeoutFamily.OutstandingTimeout => "suggestion",
        TimeoutFamily.PendingHoldTimeout => "pending_hold",
        TimeoutFamily.HoldPickupTimeout => "hold_placed",
        _ => ""
    };
    private static bool IsTerminal(string? status) => status?.Trim().ToLowerInvariant() is "unclaimed" or "cancelled" or "expired";
    private static string TerminalReason(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "cancelled" => "hold_cancelled",
        "expired" => "hold_expired",
        _ => "hold_unclaimed"
    };
    private static string WeeklyBody(
        IReadOnlyList<TitleRequest> submissions,
        IReadOnlyList<TitleRequest> purchases,
        IReadOnlyList<AdditionalCopyRequest> copies,
        string staffUrl,
        DateTime periodStartLocal,
        DateTime periodEndLocal)
    {
        var endDisplay = periodEndLocal.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "Weekly ASAP action summary",
            $"Business period: {periodStartLocal:yyyy-MM-dd} through {endDisplay}",
            "",
            "New submissions",
            $"{submissions.Count} active requests",
            "Five most recent:"
        };
        lines.AddRange(WeeklySampleLines(submissions.Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author))));
        lines.AddRange(["", $"View new submissions: {WeeklyLink(staffUrl, "submitted")}", "", "Approved purchases without bibs", $"{purchases.Count} active requests", "Five most recent:"]);
        lines.AddRange(WeeklySampleLines(purchases.Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author))));
        lines.AddRange(["", $"View purchases awaiting bibs: {WeeklyLink(staffUrl, "purchased_waiting_for_bib")}", "", "Additional copies", $"{copies.Count} open tasks", "Five most recent:"]);
        lines.AddRange(WeeklySampleLines(copies.Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author ?? item.BibId))));
        lines.Add($"\nView additional copies: {WeeklyLink(staffUrl, "additional_copies")}");
        return string.Join("\n", lines);
    }

    private static IEnumerable<string> WeeklySampleLines(IEnumerable<WeeklySummaryItem> items)
    {
        var values = items.ToList();
        if (values.Count == 0) return ["None"];
        return values.Select((item, index) => $"{index + 1}. {CleanWeekly(item.Title) ?? "Untitled"}{(string.IsNullOrWhiteSpace(item.Author) ? "" : $" - {CleanWeekly(item.Author)}")}");
    }

    private static string WeeklyLink(string staffUrl, string stage)
    {
        var separator = staffUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{staffUrl}{separator}stage={Uri.EscapeDataString(stage)}";
    }

    private static string? CleanWeekly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsWeeklyRecipientRoleAllowed(StaffUser recipient, int authorizationOrganizationId) =>
        StaffEmail.IsValidAuthenticationEmail(recipient) &&
        (recipient.Role == "super_admin" && recipient.OrganizationId == 1 && authorizationOrganizationId == 1 ||
         recipient.Role is "staff" or "admin" && recipient.OrganizationId == authorizationOrganizationId &&
         authorizationOrganizationId > 1);

    private static string AppendNote(string? current, string note) => string.IsNullOrWhiteSpace(current) ? note : $"{current.TrimEnd()}\n{note}";
    private static async Task<bool> SaveWithConcurrencyAsync(AsapDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private static bool StopsWorkflow(string code) =>
        code is "sql_failure" or "stale_progress_fence" or "operational_failure";

    private static WorkflowRunResult Add(WorkflowRunResult left, WorkflowRunResult right)
    {
        var code = StopsWorkflow(right.Code)
            ? right.Code
            : left.Code == "completed" && right.Code != "completed" && right.Code != "organization_inactive"
                ? right.Code
                : left.Code;
        return new WorkflowRunResult(code, left.Visited + right.Visited, left.Changed + right.Changed, left.Skipped + right.Skipped);
    }
}
