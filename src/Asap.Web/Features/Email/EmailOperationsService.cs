using System.Data;
using System.Globalization;
using System.Net.Mail;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed record EmailOperationItem(
    string Id,
    int OrganizationId,
    string Status,
    string? BusinessKey,
    string DeliveryClass,
    string? RecipientAddressKind,
    int AttemptCount,
    string? LastErrorCode,
    string? SuppressionReason,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    string Version,
    bool CanRetry);

public sealed record EmailOperationResult(string Code, object? Data = null);

public sealed record EmailTestContext(
    int OrganizationId, string ScopeName, string? FromAddress, string? FromName,
    string? Recipient, string DeliveryMode, string State, string? BlockingReason, string ConfigurationVersion);

public sealed class EmailOperationsService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IEmailOutboxDispatcher dispatcher,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    TimeProvider timeProvider,
    StaffEligibilityService staffEligibility,
    ILogger<EmailOperationsService>? logger = null)
{
    public static bool CanOperate(CurrentStaff actor) =>
        StaffEligibilityService.RoleMeets(actor.Role, StaffRoleRequirement.Admin) &&
        StaffEligibilityService.IsValidRoleOrganization(actor.Role, actor.OrganizationId);

    public async Task<EmailOperationResult> GetTestContextAsync(
        CurrentStaff actor, int organizationId, CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out _))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organization = await context.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);
        if (organization is null || !OrganizationAuthority.IsActiveScope(organization))
        {
            return new EmailOperationResult("organization_inactive");
        }
        var saved = await EmailTestConfiguration.ReadAsync(context, organizationId, cancellationToken);
        EmailTransportReadiness readiness;
        try
        {
            readiness = await emailSender.CheckReadinessAsync(organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new EmailOperationResult("email_transport_unavailable");
        }
        var recipient = StaffEmail.TryNormalize(actor.NotificationEmail, out var normalized) ? normalized : null;
        var blocking = recipient is null ? "recipient_missing_or_invalid" :
            !recipientDomainPolicy.IsAllowed(recipient) ? "recipient_domain_not_allowed" :
            !HasValidSender(saved.FromAddress) ? "sender_missing" :
            !readiness.IsConfigured ? "mail_not_configured" :
            !readiness.IsLiveDelivery ? "live_delivery_disabled" : null;
        return new EmailOperationResult("ok", new EmailTestContext(organizationId, organization.DisplayName,
            saved.FromAddress, saved.FromName, recipient, readiness.DeliveryMode,
            blocking is null ? "ready" : "blocked", blocking,
            saved.ContextVersion(actor, readiness.DeliveryMode, organization.RowVersion)));
    }

    public async Task<EmailOperationResult> GetTestAsync(
        CurrentStaff actor, int organizationId, long? id, Guid? operationId, CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out _))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var businessKey = operationId.HasValue ? $"operational-test:{organizationId}:{actor.Id}:{operationId.Value:N}" : null;
        var row = await context.EmailOutbox.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OrganizationId == organizationId && item.DeliveryClass == "operational_test" &&
            (id.HasValue ? item.Id == id.Value : item.BusinessKey == businessKey), cancellationToken);
        if (row is null)
        {
            return new EmailOperationResult("not_found");
        }
        var mode = row.AttemptDeliveryMode ??
            (row.ProviderMessageId?.StartsWith("file:", StringComparison.Ordinal) == true ? "file" : "unknown");
        return new EmailOperationResult("ok", new
        {
            id = row.Id.ToString(CultureInfo.InvariantCulture), row.OrganizationId, row.Status,
            row.DeliveryClass, row.AttemptCount, row.LastErrorCode, row.SuppressionReason,
            createdUtc = DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc),
            sentUtc = row.SentUtc.HasValue ? DateTime.SpecifyKind(row.SentUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
            deliveryMode = mode, row.IntendedDeliveryMode
        });
    }

    public async Task<EmailOperationResult> GetReadinessAsync(
        CurrentStaff actor,
        CancellationToken cancellationToken,
        int? requestedOrganizationId = null)
    {
        if (requestedOrganizationId <= 0 ||
            actor.Role != StaffRole.SuperAdmin && requestedOrganizationId.HasValue &&
            requestedOrganizationId != actor.OrganizationId)
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }
        var organizationId = actor.Role == StaffRole.SuperAdmin
            ? requestedOrganizationId ?? 1 : actor.OrganizationId;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var active = await context.Organizations.AsNoTracking()
                .Where(item => item.Id == organizationId &&
                               (item.Id == LibraryScope.SystemOrganizationId ||
                                item.Id > LibraryScope.SystemOrganizationId &&
                                item.OrganizationCodeId == OrganizationAuthority.LibraryOrganizationCodeId))
                .Select(item => item.IsActive)
                .SingleOrDefaultAsync(cancellationToken);
            if (!active)
            {
                return new EmailOperationResult("organization_not_found");
            }
            var settings = await ReadEffectiveSettingsAsync(context, organizationId, cancellationToken);
            if (!HasValidSender(settings.FromAddress))
            {
                return new EmailOperationResult("ok", new { state = "not_configured", organizationId });
            }
            var transport = await emailSender.CheckReadinessAsync(organizationId, cancellationToken);
            var state = !transport.IsConfigured
                ? "not_configured"
                : transport.IsLiveDelivery ? "ready" : "non_delivery";
            return new EmailOperationResult("ok", new { state, organizationId });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EmailOperationResult("ok", new { state = "unavailable", organizationId });
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new EmailOperationResult("ok", new { state = "unavailable", organizationId });
        }
    }

    public async Task<EmailOperationResult> QueueTestAsync(
        CurrentStaff actor,
        int? organizationId,
        CancellationToken cancellationToken,
        Guid? operationId = null,
        string? configurationVersion = null,
        string? intendedDeliveryMode = null)
    {
        if (!TryResolveScope(actor, organizationId, out var scope))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        var targetOrganizationId = scope ?? 1;
        await using (var preflight = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var organization = await preflight.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == targetOrganizationId, cancellationToken);
            if (organization is null || !OrganizationAuthority.IsActiveScope(organization))
            {
                return new EmailOperationResult("organization_inactive");
            }
            if (operationId.HasValue)
            {
                var key = $"operational-test:{targetOrganizationId}:{actor.Id}:{operationId.Value:N}";
                var existing = await preflight.EmailOutbox.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.BusinessKey == key, cancellationToken);
                if (existing is not null)
                {
                    var eligible = await staffEligibility.EvaluateAsync(
                        new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId),
                        targetOrganizationId, StaffRoleRequirement.Admin, requireParticipation: true, cancellationToken);
                    return eligible.Outcome == StaffEligibilityOutcome.Allowed
                        ? ReplayTest(existing) : new EmailOperationResult(eligible.Code);
                }
            }
        }

        // Readiness may call the final transport/configuration boundary. Keep it outside
        // the short SQL transaction that commits the durable intent.
        EmailTransportReadiness readiness;
        try
        {
            readiness = await emailSender.CheckReadinessAsync(targetOrganizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or EmailOperationalException or OperationCanceledException)
        {
            return new EmailOperationResult("email_transport_unavailable");
        }
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOrganizationsAsync(context, actor, targetOrganizationId, cancellationToken))
        {
            return new EmailOperationResult("organization_not_found");
        }

        var locked = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            targetOrganizationId,
            StaffRoleRequirement.Admin,
            requireActorParticipation: true,
            LockedOrganizations(actor, targetOrganizationId).ToHashSet(),
            cancellationToken);
        if (locked.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return new EmailOperationResult(locked.Code);
        }

        var currentActor = locked.Staff!;
        var businessKey = operationId.HasValue
            ? $"operational-test:{targetOrganizationId}:{currentActor.Id}:{operationId.Value:N}"
            : null;
        if (businessKey is not null)
        {
            var existing = await context.EmailOutbox.AsNoTracking()
                .SingleOrDefaultAsync(item => item.BusinessKey == businessKey, cancellationToken);
            if (existing is not null)
            {
                return ReplayTest(existing);
            }
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var recent = await context.EmailOutbox.AsNoTracking().Where(item =>
                item.DeliveryClass == "operational_test" && item.TestRequestedByStaffUserId.HasValue && item.CreatedUtc > now.AddMinutes(-1) &&
                (item.TestRequestedByStaffUserId == currentActor.Id || item.OrganizationId == targetOrganizationId))
            .Select(item => new { item.TestRequestedByStaffUserId, item.OrganizationId, item.CreatedUtc })
            .ToArrayAsync(cancellationToken);
        var actorRecent = recent.Where(item => item.TestRequestedByStaffUserId == currentActor.Id).ToArray();
        var scopeRecent = recent.Where(item => item.OrganizationId == targetOrganizationId).ToArray();
        if (actorRecent.Length > 0 || scopeRecent.Length >= 5)
        {
            var latest = actorRecent.Length > 0 ? actorRecent.Max(item => item.CreatedUtc) : scopeRecent.Min(item => item.CreatedUtc);
            return new EmailOperationResult("email_test_cooldown", new
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((latest.AddMinutes(1) - now).TotalSeconds))
            });
        }
        var settings = await EmailTestConfiguration.ReadAsync(context, targetOrganizationId, cancellationToken, lockRows: true);
        var targetOrganization = await context.Organizations.SingleAsync(item => item.Id == targetOrganizationId, cancellationToken);
        if (configurationVersion is not null &&
            !string.Equals(configurationVersion, settings.ContextVersion(currentActor, readiness.DeliveryMode, targetOrganization.RowVersion), StringComparison.Ordinal))
        {
            return new EmailOperationResult("email_test_configuration_changed");
        }
        if (intendedDeliveryMode is not null &&
            (intendedDeliveryMode != "postmark" || !readiness.IsConfigured || !readiness.IsLiveDelivery || readiness.DeliveryMode != intendedDeliveryMode))
        {
            return new EmailOperationResult("live_delivery_disabled");
        }
        var address = StaffEmail.TryNormalize(currentActor.NotificationEmail, out var normalized)
            ? normalized
            : null;
        var suppression = address is null ? "recipient_missing_or_invalid" :
            !recipientDomainPolicy.IsAllowed(address) ? "recipient_domain_not_allowed" :
            !HasValidSender(settings.FromAddress) ? "sender_missing" :
            !readiness.IsConfigured ? "mail_not_configured" : null;
        var outbox = new EmailOutbox
        {
            OrganizationId = targetOrganizationId,
            BusinessKey = businessKey,
            DeliveryClass = "operational_test",
            TestRequestedByStaffUserId = currentActor.Id,
            TestConfigurationVersion = intendedDeliveryMode is null ? null : settings.Version,
            IntendedDeliveryMode = intendedDeliveryMode,
            ToAddress = address,
            FromAddress = settings.FromAddress,
            FromName = settings.FromName,
            Subject = suppression is null ? $"ASAP test email - {targetOrganization.DisplayName}" : null,
            BodyText = suppression is null
                ? $"An administrator requested this synthetic ASAP email test.\nScope: {targetOrganization.DisplayName} ({targetOrganizationId}).\nRequested at: {now:O} UTC.\nReference: {operationId?.ToString("N") ?? "internal-test"}.\nNo patron information is included."
                : null,
            Status = suppression is null ? "pending" : "suppressed",
            SuppressionReason = suppression,
            NextAttemptUtc = suppression is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppression is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync(cancellationToken);
        if (suppression is null)
        {
            outbox.BodyText += $"\nEmail operation reference: {outbox.Id.ToString(CultureInfo.InvariantCulture)}.";
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var dispatchDelayed = suppression is null && cancellationToken.IsCancellationRequested;
        if (suppression is null && !dispatchDelayed)
        {
            try
            {
                dispatcher.Enqueue(outbox.Id);
            }
            // The durable outbox is committed; preserve acceptance and recover through the scheduled sweep.
            catch (Exception exception)
            {
                dispatchDelayed = true;
                logger?.LogWarning("Email outbox {OutboxId} awaits the scheduled sweep after dispatch failure ({FailureType}).",
                    outbox.Id, exception.GetType().Name);
            }
        }
        return new EmailOperationResult(
            suppression is null ? "queued" : "suppressed",
            new { id = outbox.Id.ToString(CultureInfo.InvariantCulture), code = suppression,
                dispatchDelayed, version = StaffVersion.Encode(outbox.RowVersion) });
    }

    public async Task<IReadOnlyList<EmailOperationItem>> ListAsync(
        CurrentStaff actor,
        int? organizationId,
        string? status,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out var scope))
        {
            return [];
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.EmailOutbox.AsNoTracking();
        if (scope.HasValue)
        {
            query = query.Where(item => item.OrganizationId == scope.Value);
        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => item.Status == "failed");
        }
        var rows = await query
            .OrderByDescending(item => item.Status == "failed")
            .ThenByDescending(item => item.CreatedUtc)
            .Take(500)
            .Select(item => new
            {
                item.Id,
                item.OrganizationId,
                item.Status,
                item.BusinessKey,
                item.DeliveryClass,
                item.RecipientAddressKind,
                item.AttemptCount,
                item.LastErrorCode,
                item.SuppressionReason,
                item.CreatedUtc,
                item.SentUtc,
                item.RowVersion,
                item.ProviderMessageId,
                item.SendingStartedUtc,
                item.LeaseId,
                item.LeaseExpiresUtc,
                item.NextAttemptUtc
            })
            .ToListAsync(cancellationToken);
        return rows.Select(item => new EmailOperationItem(
            item.Id.ToString(CultureInfo.InvariantCulture), item.OrganizationId, item.Status,
            item.BusinessKey, item.DeliveryClass,
            item.RecipientAddressKind, item.AttemptCount, item.LastErrorCode, item.SuppressionReason,
            DateTime.SpecifyKind(item.CreatedUtc, DateTimeKind.Utc),
            item.SentUtc.HasValue ? DateTime.SpecifyKind(item.SentUtc.Value, DateTimeKind.Utc) : null,
            StaffVersion.Encode(item.RowVersion),
            IsCertifiedForRetry(item.Status, item.LastErrorCode, item.ProviderMessageId, item.SendingStartedUtc,
                item.LeaseId, item.LeaseExpiresUtc, item.NextAttemptUtc)))
            .ToArray();
    }

    public async Task<EmailOperationResult> RetryAsync(
        CurrentStaff actor,
        long id,
        string? encodedVersion,
        CancellationToken cancellationToken)
    {
        if (!CanOperate(actor))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        if (!StaffVersion.TryDecode(encodedVersion, out var expectedVersion))
        {
            return new EmailOperationResult("invalid_version");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await context.EmailOutbox.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.OrganizationId })
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null)
        {
            return new EmailOperationResult("not_found");
        }

        if (!TryResolveScope(actor, snapshot.OrganizationId, out _))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockOrganizationsAsync(context, actor, snapshot.OrganizationId, cancellationToken))
        {
            return new EmailOperationResult("organization_not_found");
        }
        var locked = await staffEligibility.RevalidateLockedAsync(
            context,
            actor,
            snapshot.OrganizationId,
            StaffRoleRequirement.Admin,
            requireActorParticipation: true,
            LockedOrganizations(actor, snapshot.OrganizationId).ToHashSet(),
            cancellationToken);
        if (locked.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return new EmailOperationResult(locked.Code);
        }

        var row = await context.EmailOutbox.FromSqlInterpolated(
                $"SELECT * FROM [asap].[EmailOutbox] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new EmailOperationResult("not_found");
        }

        if (!row.RowVersion.SequenceEqual(expectedVersion))
        {
            return new EmailOperationResult("stale_version");
        }

        if (!IsCertifiedForRetry(row.Status, row.LastErrorCode, row.ProviderMessageId, row.SendingStartedUtc,
                row.LeaseId, row.LeaseExpiresUtc, row.NextAttemptUtc))
        {
            return new EmailOperationResult("email_not_retryable");
        }

        row.Status = "pending";
        row.NextAttemptUtc = timeProvider.GetUtcNow().UtcDateTime;
        row.LastErrorCode = null;
        row.LastErrorDetail = null;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new EmailOperationResult("stale_version");
        }
        var dispatchDelayed = cancellationToken.IsCancellationRequested;
        if (!dispatchDelayed)
        {
            try
            {
                dispatcher.Enqueue(row.Id);
            }
            // Retry state is committed; report delayed dispatch and retain the pending row for the sweep.
            catch (Exception exception)
            {
                dispatchDelayed = true;
                logger?.LogWarning("Email outbox {OutboxId} awaits the scheduled sweep after dispatch failure ({FailureType}).",
                    row.Id, exception.GetType().Name);
            }
        }
        return new EmailOperationResult("queued", new
        {
            id = row.Id.ToString(CultureInfo.InvariantCulture),
            dispatchDelayed,
            version = StaffVersion.Encode(row.RowVersion)
        });
    }

    private async Task<bool> LockOrganizationsAsync(
        AsapDbContext context,
        CurrentStaff actor,
        int targetOrganizationId,
        CancellationToken cancellationToken)
    {
        foreach (var id in LockedOrganizations(actor, targetOrganizationId))
        {
            var organization = await context.Organizations.FromSqlInterpolated(
                    $"SELECT * FROM [asap].[Organization] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = {id}")
                .SingleOrDefaultAsync(cancellationToken);
            if (organization is null)
            {
                return false;
            }
        }
        return true;
    }

    private static int[] LockedOrganizations(CurrentStaff actor, int targetOrganizationId) =>
        new[] { actor.OrganizationId, targetOrganizationId }.Distinct().Order().ToArray();

    private static bool IsCertifiedForRetry(
        string status,
        string? lastErrorCode,
        string? providerMessageId,
        DateTime? sendingStartedUtc,
        Guid? leaseId,
        DateTime? leaseExpiresUtc,
        DateTime? nextAttemptUtc) =>
        status == "failed" &&
        string.Equals(lastErrorCode, "mail_not_configured", StringComparison.Ordinal) &&
        providerMessageId is null &&
        !sendingStartedUtc.HasValue &&
        !leaseId.HasValue &&
        !leaseExpiresUtc.HasValue &&
        !nextAttemptUtc.HasValue;

    private static EmailOperationResult ReplayTest(EmailOutbox row) =>
        new(row.Status == "suppressed" ? "suppressed" : "queued",
            new { id = row.Id.ToString(CultureInfo.InvariantCulture), code = row.SuppressionReason,
                version = StaffVersion.Encode(row.RowVersion), replayed = true, status = row.Status });

    private static async Task<EffectiveEmailSettings> ReadEffectiveSettingsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var system = await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = organizationId == 1 ? null : await context.EmailSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return new EffectiveEmailSettings(
            Clean(library?.FromAddress) ?? Clean(system?.FromAddress),
            Clean(library?.FromName) ?? Clean(system?.FromName));
    }

    private sealed record EffectiveEmailSettings(string? FromAddress, string? FromName);

    private static bool HasValidSender(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        try
        {
            return string.Equals(new MailAddress(address).Address, address.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryResolveScope(CurrentStaff actor, int? requested, out int? scope)
    {
        if (!CanOperate(actor))
        {
            scope = null;
            return false;
        }
        if (actor.Role == StaffRole.SuperAdmin)
        {
            scope = requested;
            return !requested.HasValue || requested.Value > 0;
        }
        scope = actor.OrganizationId;
        return actor.OrganizationId > 1 && (!requested.HasValue || requested.Value == actor.OrganizationId);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
