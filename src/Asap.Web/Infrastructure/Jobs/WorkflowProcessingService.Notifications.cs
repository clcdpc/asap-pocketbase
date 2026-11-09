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
using Microsoft.Data.SqlClient;

namespace Asap.Web.Infrastructure.Jobs;

public sealed partial class WorkflowProcessingService
{
    public async Task<WorkflowRunResult> SendWeeklyStaffSummaryAsync(
        Guid? manualRunId = null,
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default,
        StaffIdentityEvidence? manualActorEvidence = null,
        DateTime? occurrenceUtc = null)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null)
        {
            return new WorkflowRunResult("workflow_processing_busy", ManualRunId: manualRunId);
        }

        var now = UtcNow();
        var localNow = TimeZoneInfo.ConvertTime(new DateTimeOffset(occurrenceUtc ?? now, TimeSpan.Zero), businessTimeZone);
        var periodEndLocal = localNow.Date.AddDays(1);
        var periodStartLocal = periodEndLocal.AddDays(-7);
        var periodStart = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodStartLocal, DateTimeKind.Unspecified), businessTimeZone);
        var periodEnd = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(periodEndLocal, DateTimeKind.Unspecified), businessTimeZone);

        if (manualRunId is not null && manualActorEvidence is not null)
        {
            await using var auditContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var auditTransaction = await auditContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var auditScope = scopeOrganizationId ?? LibraryScope.SystemOrganizationId;
            if (!await LockManualOrganizationsAsync(auditContext, manualActorEvidence, auditScope, cancellationToken) ||
                !OrganizationAuthority.IsActiveScope(auditContext.Organizations.Local.Single(item => item.Id == auditScope)) ||
                !await IsManualActorAllowedLockedAsync(auditContext, manualActorEvidence, auditScope, cancellationToken))
            {
                return new WorkflowRunResult("staff_scope_forbidden", ManualRunId: manualRunId);
            }
            var manualRunAuditKey = manualRunId.Value.ToString("N");
            var alreadyAudited = await auditContext.AdministrativeAudits.AnyAsync(item =>
                item.Action == "weekly_summary_force_queued" && item.TargetId == manualRunAuditKey,
                cancellationToken);
            if (!alreadyAudited)
            {
                auditContext.AdministrativeAudits.Add(new AdministrativeAudit
                {
                    ActorStaffUserId = manualActorEvidence.StaffUserId,
                    ActorName = manualActorEvidence.AuthenticationEmail,
                    OrganizationId = scopeOrganizationId ?? LibraryScope.SystemOrganizationId,
                    Action = "weekly_summary_force_queued",
                    TargetType = "WeeklyStaffSummary",
                    TargetId = manualRunAuditKey,
                    DetailsJson = JsonSerializer.Serialize(new { manualRunId, scopeOrganizationId }),
                    CreatedUtc = now
                });
                await auditContext.SaveChangesAsync(cancellationToken);
            }
            await auditTransaction.CommitAsync(cancellationToken);
        }

        await using var readContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizations = await readContext.Organizations.AsNoTracking()
            .Where(item => item.IsActive && item.Id > LibraryScope.SystemOrganizationId &&
                           item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId &&
                           (!scopeOrganizationId.HasValue || item.Id == scopeOrganizationId.Value))
            .ToListAsync(cancellationToken);
        var organizationIds = organizations.Select(item => item.Id).ToHashSet();
        var staff = await readContext.StaffUsers.AsNoTracking()
            .Where(item => item.IsActive && item.WeeklyActionSummaryEnabled &&
                           item.NormalizedUserPrincipalName != null &&
                           (!scopeOrganizationId.HasValue || item.OrganizationId == scopeOrganizationId.Value ||
                            item.Role == StaffRole.SuperAdmin && item.OrganizationId == LibraryScope.SystemOrganizationId))
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var titleRequests = await readContext.TitleRequests.AsNoTracking()
            .Where(item => organizationIds.Contains(item.LibraryOrganizationId) &&
                          (item.Status == RequestStatus.Suggestion ||
                           item.Status == RequestStatus.OutstandingPurchase && item.BibId == null))
            .Select(item => new WeeklyTitleSnapshot(item.Id, item.LibraryOrganizationId, item.Status,
                item.Title, item.Author, item.CreatedUtc, item.UpdatedUtc))
            .ToListAsync(cancellationToken);
        var copyRequests = await readContext.AdditionalCopyRequests.AsNoTracking()
            .Where(item => organizationIds.Contains(item.LibraryOrganizationId) && item.Status == "open")
            .Select(item => new WeeklyCopySnapshot(item.Id, item.LibraryOrganizationId,
                item.Title, item.Author, item.BibId, item.CreatedUtc))
            .ToListAsync(cancellationToken);
        var systemSettings = await readContext.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var staffUrl = systemSettings?.StaffApplicationUrl;
        var createdCount = 0;
        var visited = 0;
        var summaries = new Dictionary<int, WeeklySummaryData>();
        foreach (var recipientSnapshot in staff)
        {
            visited++;
            var authorizationOrganizationId = recipientSnapshot.Role == StaffRole.SuperAdmin ? LibraryScope.SystemOrganizationId : recipientSnapshot.OrganizationId;
            if (authorizationOrganizationId != LibraryScope.SystemOrganizationId && !organizationIds.Contains(authorizationOrganizationId))
            {
                continue;
            }

            if (!summaries.TryGetValue(authorizationOrganizationId, out var summary))
            {
                summary = PrepareWeeklySummary(titleRequests, copyRequests, authorizationOrganizationId);
                summaries.Add(authorizationOrganizationId, summary);
            }
            if (summary.Submissions.Count == 0 && summary.Purchases.Count == 0 && summary.Copies.Count == 0)
            {
                continue;
            }

            var businessKey = manualRunId is null
                ? $"weekly-summary:{recipientSnapshot.Id}:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}"
                : $"weekly-summary-force:{manualRunId.Value:N}:{recipientSnapshot.Id}";
            // This unlocked read only avoids transport preparation. Replay still passes the
            // locked actor/recipient checks and final occurrence check below.
            var readinessRequired = !await readContext.EmailOutbox.AnyAsync(
                item => item.BusinessKey == businessKey, cancellationToken);
            while (true)
            {
                var readiness = EmailTransportReadiness.NotConfigured;
                if (readinessRequired)
                {
                    try
                    {
                        readiness = await emailSender.CheckReadinessAsync(authorizationOrganizationId, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
                        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Weekly summary readiness failed ({FailureType}).", exception.GetType().Name);
                        return new WorkflowRunResult("operational_failure", visited, createdCount, visited - createdCount, manualRunId);
                    }
                }
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                if (!await LockManualOrganizationsAsync(
                        context,
                        manualActorEvidence,
                        scopeOrganizationId ?? authorizationOrganizationId,
                        cancellationToken))
                {
                    break;
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
                    break;
                }
                if (await context.EmailOutbox.AnyAsync(item => item.BusinessKey == businessKey, cancellationToken))
                {
                    break;
                }
                if (!readinessRequired)
                {
                    // A removed prefilter hit is new work. Dispose this transaction before
                    // obtaining readiness, then repeat the ordinary locked preparation.
                    readinessRequired = true;
                    continue;
                }

                var librarySettings = await context.EmailSettings.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.OrganizationId == recipient.OrganizationId, cancellationToken);
                var systemEmail = await context.EmailSettings.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
                var fromAddress = Clean(librarySettings?.FromAddress) ?? Clean(systemEmail?.FromAddress);
                var fromName = Clean(librarySettings?.FromName) ?? Clean(systemEmail?.FromName);
                var primaryAddress = StaffEmail.TryNormalize(recipient.NotificationEmail, out var primary)
                    ? primary : null;
                var additionalAddress = StaffEmail.TryNormalize(recipient.WeeklyActionSummaryEmail, out var additional)
                    ? additional : null;
                var recipients = new List<(string? Address, string Kind, string Key)>
            {
                (primaryAddress, "weekly_summary_primary", businessKey)
            };
                if (!string.IsNullOrWhiteSpace(recipient.WeeklyActionSummaryEmail) &&
                    (additionalAddress is null || !string.Equals(primaryAddress, additionalAddress, StringComparison.OrdinalIgnoreCase)))
                {
                    recipients.Add((additionalAddress, "weekly_summary_additional", $"{businessKey}:additional"));
                }
                // The primary key anchors the whole operation, including historical single-recipient
                // operations. Commit both recipients together; retries never fill in or redirect copies.
                var outboxItems = new List<EmailOutbox>();
                var body = string.IsNullOrWhiteSpace(staffUrl) ? null : WeeklyBody(
                    summary, staffUrl, periodStartLocal, periodEndLocal);
                foreach (var (address, kind, key) in recipients)
                {
                    var suppression = address is null ? "recipient_missing_or_invalid" :
                        !recipientDomainPolicy.IsAllowed(address) ? "recipient_domain_not_allowed" :
                        string.IsNullOrWhiteSpace(staffUrl) ? "staff_url_missing" :
                        string.IsNullOrWhiteSpace(fromAddress) ? "sender_missing" :
                        !readiness.IsConfigured ? "mail_not_configured" : null;
                    outboxItems.Add(new EmailOutbox
                    {
                        OrganizationId = authorizationOrganizationId,
                        BusinessKey = key,
                        DeliveryClass = "staff_authorization_sensitive",
                        RecipientStaffUserId = recipient.Id,
                        RecipientAuthenticationEmail = recipient.NormalizedUserPrincipalName,
                        AuthorizationOrganizationId = authorizationOrganizationId,
                        RecipientAddressKind = kind,
                        ToAddress = address,
                        FromAddress = fromAddress,
                        FromName = fromName,
                        Subject = suppression is null ? $"Weekly ASAP action summary: {summary.Submissions.Count} new, {summary.Purchases.Count} awaiting bibs, {summary.Copies.Count} additional copies" : null,
                        BodyText = suppression is null ? body : null,
                        Status = suppression is null ? "pending" : "suppressed",
                        SuppressionReason = suppression,
                        NextAttemptUtc = suppression is null ? now : null,
                        CreatedUtc = now,
                        SuppressedUtc = suppression is null ? null : now
                    });
                }
                context.EmailOutbox.AddRange(outboxItems);
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    createdCount++;
                    foreach (var outbox in outboxItems)
                    {
                        DispatchCommittedOutbox(outbox, cancellationToken);
                    }
                }
                catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
                {
                    logger.LogInformation(exception, "Weekly summary duplicate or concurrent recipient mutation was ignored.");
                }
                break;
            }
        }
        return new WorkflowRunResult("completed", visited, createdCount, visited - createdCount, manualRunId);
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
                try
                {
                    readiness = await emailSender.CheckReadinessAsync(candidate.LibraryOrganizationId, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Timeout email readiness failed ({FailureType}).", exception.GetType().Name);
                    return new WorkflowItemResult("operational_failure", Stop: true);
                }
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
        if (organization is not null && OrganizationAuthority.IsActiveLibrary(organization) && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == StatusFor(family))
        {
            var incomplete = await PickupPreferenceMutationService.HasIncompleteAsync(context, request.Id, cancellationToken) ||
                await context.HoldPlacementOperations.AnyAsync(
                item => item.TitleRequestId == request.Id && item.CompletedUtc == null,
                cancellationToken);
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            var (enabled, days) = TimeoutSetting(settings, family);
            var age = family == TimeoutFamily.OutstandingTimeout ? request.CreatedUtc : request.UpdatedUtc;
            if (!incomplete && enabled && days.HasValue &&
                TimeoutSemantics.IsExpired(age, timeProvider.GetUtcNow(), businessTimeZone, days.Value))
            {
                request.Status = RequestStatus.Closed;
                request.CloseReason = family == TimeoutFamily.HoldPickupTimeout ? "hold_not_picked_up" : "rejected";
                request.UpdatedUtc = UtcNow();
                var note = $"{family} closed this request after the configured timeout.";
                request.Notes = AppendNote(request.Notes, note);
                context.TitleRequestEvents.Add(new TitleRequestEvent
                {
                    TitleRequestId = request.Id,
                    EventType = "timeout_closed",
                    Status = RequestStatus.Closed,
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
        if (result.LocalCommit)
        {
            DispatchCommittedOutbox(pendingOutbox, cancellationToken);
        }
        return result;
    }

    private void DispatchCommittedOutbox(EmailOutbox? outbox, CancellationToken cancellationToken)
    {
        if (outbox?.Status != "pending")
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Email outbox {OutboxId} awaits the scheduled sweep after caller cancellation.", outbox.Id);
            return;
        }

        try
        {
            outboxDispatcher.Enqueue(outbox.Id);
        }
        // Local state and the outbox are committed; the scheduled sweep retries delivery after any dispatch failure.
        catch (Exception exception)
        {
            logger.LogWarning("Email outbox {OutboxId} awaits the scheduled sweep after dispatch failure ({FailureType}).",
                outbox.Id, exception.GetType().Name);
        }
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
        if (organization is not null && OrganizationAuthority.IsActiveLibrary(organization) && request is not null &&
            request.LibraryOrganizationId == candidate.LibraryOrganizationId &&
            request.RowVersion.SequenceEqual(candidate.RowVersion) &&
            request.Status == "open")
        {
            var settings = await EffectiveWorkflowAsync(context, request.LibraryOrganizationId, cancellationToken);
            if (settings.AdditionalCopyTimeoutEnabled == true && settings.AdditionalCopyTimeoutDays.HasValue &&
                TimeoutSemantics.IsExpired(request.UpdatedUtc, timeProvider.GetUtcNow(), businessTimeZone, settings.AdditionalCopyTimeoutDays.Value))
            {
                request.Status = RequestStatus.Closed;
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

    private async Task<EmailOutbox?> AddTimeoutEmailAsync(
        AsapDbContext context,
        TitleRequest request,
        WorkflowSettings settings,
        EmailTransportReadiness readiness,
        CancellationToken cancellationToken)
    {
        var systemEmail = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var libraryEmail = request.LibraryOrganizationId == LibraryScope.SystemOrganizationId
            ? null
            : await context.EmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == request.LibraryOrganizationId, cancellationToken);
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
            TitleRequestId = request.Id,
            MessageKind = "timeout",
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
        RequestEmailEvidence.AddInitial(context, outbox);
        return allowed ? outbox : null;
    }

    private static (bool Enabled, int? Days) TimeoutSetting(WorkflowSettings settings, TimeoutFamily family) => family switch
    {
        TimeoutFamily.OutstandingTimeout => (settings.OutstandingTimeoutEnabled == true, settings.OutstandingTimeoutDays),
        TimeoutFamily.PendingHoldTimeout => (settings.PendingHoldTimeoutEnabled == true, settings.PendingHoldTimeoutDays),
        TimeoutFamily.HoldPickupTimeout => (settings.HoldPickupTimeoutEnabled == true, settings.HoldPickupTimeoutDays),
        TimeoutFamily.AdditionalCopyTimeout => (settings.AdditionalCopyTimeoutEnabled == true, settings.AdditionalCopyTimeoutDays),
        _ => (false, null)
    };

    private static string StatusFor(TimeoutFamily family) => family switch
    {
        TimeoutFamily.OutstandingTimeout => RequestStatus.Suggestion,
        TimeoutFamily.PendingHoldTimeout => RequestStatus.PendingHold,
        TimeoutFamily.HoldPickupTimeout => RequestStatus.HoldPlaced,
        _ => ""
    };
    private sealed record WeeklyTitleSnapshot(long Id, int LibraryOrganizationId, string Status,
        string Title, string? Author, DateTime CreatedUtc, DateTime UpdatedUtc);
    private sealed record WeeklyCopySnapshot(long Id, int LibraryOrganizationId,
        string Title, string? Author, int BibId, DateTime CreatedUtc);
    private sealed record WeeklySummaryCategory(int Count, IReadOnlyList<WeeklySummaryItem> Examples);
    private sealed record WeeklySummaryData(WeeklySummaryCategory Submissions, WeeklySummaryCategory Purchases, WeeklySummaryCategory Copies);

    private static WeeklySummaryData PrepareWeeklySummary(
        IReadOnlyList<WeeklyTitleSnapshot> titles,
        IReadOnlyList<WeeklyCopySnapshot> copyRequests,
        int organizationId)
    {
        var scopedTitles = titles.Where(item => organizationId == LibraryScope.SystemOrganizationId ||
            item.LibraryOrganizationId == organizationId).ToArray();
        var submissions = scopedTitles.Where(item => item.Status == RequestStatus.Suggestion).ToArray();
        var purchases = scopedTitles.Where(item => item.Status == RequestStatus.OutstandingPurchase).ToArray();
        var copies = copyRequests.Where(item => organizationId == LibraryScope.SystemOrganizationId ||
            item.LibraryOrganizationId == organizationId).ToArray();
        return new WeeklySummaryData(
            new WeeklySummaryCategory(submissions.Length, submissions.OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id).Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author)).ToArray()),
            new WeeklySummaryCategory(purchases.Length, purchases.OrderByDescending(item => item.UpdatedUtc)
                .ThenByDescending(item => item.Id).Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author)).ToArray()),
            new WeeklySummaryCategory(copies.Length, copies.OrderByDescending(item => item.CreatedUtc)
                .ThenByDescending(item => item.Id).Take(5).Select(item => new WeeklySummaryItem(item.Title,
                    item.Author ?? item.BibId.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray()));
    }

    private static string WeeklyBody(
        WeeklySummaryData summary,
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
            $"{summary.Submissions.Count} active requests",
            "Five most recent:"
        };
        lines.AddRange(WeeklySampleLines(summary.Submissions.Examples));
        lines.AddRange(["", $"View new submissions: {WeeklyLink(staffUrl, "submitted")}", "", "Approved purchases without bibs", $"{summary.Purchases.Count} active requests", "Five most recent:"]);
        lines.AddRange(WeeklySampleLines(summary.Purchases.Examples));
        lines.AddRange(["", $"View purchases awaiting bibs: {WeeklyLink(staffUrl, "purchased_waiting_for_bib")}", "", "Additional copies", $"{summary.Copies.Count} open tasks", "Five most recent:"]);
        lines.AddRange(WeeklySampleLines(summary.Copies.Examples));
        lines.Add($"\nView additional copies: {WeeklyLink(staffUrl, "additional_copies")}");
        return string.Join("\n", lines);
    }

    private static IEnumerable<string> WeeklySampleLines(IEnumerable<WeeklySummaryItem> items)
    {
        var values = items.ToList();
        if (values.Count == 0)
        {
            return ["None"];
        }

        return values.Select((item, index) => $"{index + 1}. {CleanWeekly(item.Title) ?? "Untitled"}{(string.IsNullOrWhiteSpace(item.Author) ? "" : $" - {CleanWeekly(item.Author)}")}");
    }

    private static string WeeklyLink(string staffUrl, string stage)
    {
        var separator = staffUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{staffUrl}{separator}stage={Uri.EscapeDataString(stage)}";
    }

    private static string? CleanWeekly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim();

    private static bool IsWeeklyRecipientRoleAllowed(StaffUser recipient, int authorizationOrganizationId) =>
        StaffEligibilityService.IsAssignmentEligible(recipient, authorizationOrganizationId);

}
