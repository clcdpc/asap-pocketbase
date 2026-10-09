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
    public async Task<WorkflowRunResult> SendWeeklyStaffSummaryAsync(
        Guid? manualRunId = null,
        int? scopeOrganizationId = null,
        CancellationToken cancellationToken = default,
        StaffIdentityEvidence? manualActorEvidence = null)
    {
        using var guard = workflowGuard.TryAcquire(cancellationToken);
        if (guard is null)
        {
            return new WorkflowRunResult("workflow_processing_busy", ManualRunId: manualRunId);
        }

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
                          (item.Status == RequestStatus.Suggestion || item.Status == RequestStatus.OutstandingPurchase))
            .ToListAsync(cancellationToken);
        var copyRequests = await readContext.AdditionalCopyRequests.AsNoTracking()
            .Where(item => organizationIds.Contains(item.LibraryOrganizationId) && item.Status == "open")
            .ToListAsync(cancellationToken);
        var systemSettings = await readContext.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var staffUrl = systemSettings?.StaffApplicationUrl;
        var createdCount = 0;
        var visited = 0;
        foreach (var recipientSnapshot in staff)
        {
            visited++;
            var authorizationOrganizationId = recipientSnapshot.Role == StaffRole.SuperAdmin ? LibraryScope.SystemOrganizationId : recipientSnapshot.OrganizationId;
            if (authorizationOrganizationId != LibraryScope.SystemOrganizationId && !organizationIds.Contains(authorizationOrganizationId))
            {
                continue;
            }

            var recipientRequests = titleRequests.Where(item => authorizationOrganizationId == LibraryScope.SystemOrganizationId ||
                item.LibraryOrganizationId == authorizationOrganizationId).ToList();
            var newSubmissions = recipientRequests.Where(item => item.Status == RequestStatus.Suggestion)
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).ToList();
            var purchases = recipientRequests.Where(item => item.Status == RequestStatus.OutstandingPurchase && item.BibId == null)
                .OrderByDescending(item => item.UpdatedUtc).ThenByDescending(item => item.Id).ToList();
            var copies = copyRequests.Where(item => authorizationOrganizationId == LibraryScope.SystemOrganizationId ||
                item.LibraryOrganizationId == authorizationOrganizationId)
                .OrderByDescending(item => item.CreatedUtc).ThenByDescending(item => item.Id).ToList();
            if (newSubmissions.Count == 0 && purchases.Count == 0 && copies.Count == 0)
            {
                continue;
            }

            EmailTransportReadiness readiness;
            try
            {
                readiness = await emailSender.CheckReadinessAsync(authorizationOrganizationId, cancellationToken);
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Weekly summary readiness failed ({FailureType}).", exception.GetType().Name);
                return new WorkflowRunResult("operational_failure", visited, createdCount, visited - createdCount, manualRunId);
            }
            var businessKey = manualRunId is null
                ? $"weekly-summary:{recipientSnapshot.Id}:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}"
                : $"weekly-summary-force:{manualRunId.Value:N}:{recipientSnapshot.Id}";
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
            if (await context.EmailOutbox.AnyAsync(item => item.BusinessKey == businessKey, cancellationToken))
            {
                continue;
            }

            var librarySettings = await context.EmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == recipient.OrganizationId, cancellationToken);
            var systemEmail = await context.EmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
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
                createdCount++;
                DispatchCommittedOutbox(outbox);
            }
            catch (DbUpdateException exception) when (exception.InnerException is DbException)
            {
                logger.LogInformation(exception, "Weekly summary duplicate or concurrent recipient mutation was ignored.");
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
            DispatchCommittedOutbox(pendingOutbox);
        }
        return result;
    }

    private void DispatchCommittedOutbox(EmailOutbox? outbox)
    {
        if (outbox?.Status != "pending")
        {
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
        lines.AddRange(WeeklySampleLines(copies.Take(5).Select(item => new WeeklySummaryItem(item.Title, item.Author ?? item.BibId.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
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
