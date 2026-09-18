using System.Data;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Email;

public sealed record EmailOperationItem(
    long Id,
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
    string? DeliveryMode,
    string? ProviderMessageId);

public sealed record EmailTestContext(
    int OrganizationId,
    string OrganizationName,
    string? FromAddress,
    string? FromName,
    string? RecipientAddress,
    string DeliveryMode,
    bool IsConfigured,
    bool CanSend,
    string? ReadinessCode,
    string? BlockingReason);

public sealed record EmailOperationStatusItem(
    long Id,
    int OrganizationId,
    string Status,
    string DeliveryClass,
    int AttemptCount,
    string? LastErrorCode,
    string? SuppressionReason,
    DateTime CreatedUtc,
    DateTime? SentUtc,
    string Version,
    string? DeliveryMode,
    string? ProviderMessageId);

public sealed record EmailOperationResult(string Code, object? Data = null);

public sealed class EmailOperationsService(
    IDbContextFactory<AsapDbContext> contextFactory,
    IEmailOutboxDispatcher dispatcher,
    IEmailSender emailSender,
    RecipientDomainPolicy recipientDomainPolicy,
    TimeProvider timeProvider,
    StaffEligibilityService staffEligibility)
{
    public static bool CanOperate(CurrentStaff actor) => actor.Role is "admin" or "super_admin";

    public async Task<EmailOperationResult> QueueTestAsync(
        CurrentStaff actor,
        int? organizationId,
        CancellationToken cancellationToken)
        => await QueueTestAsync(actor, organizationId, requestId: null, cancellationToken: cancellationToken);

    public async Task<EmailOperationResult> QueueTestAsync(
        CurrentStaff actor,
        int? organizationId,
        string? requestId,
        CancellationToken cancellationToken)
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
            if (organization?.IsActive != true) return new EmailOperationResult("organization_inactive");
            var organizationName = organization.DisplayName;

            requestId = NormalizeRequestId(requestId);
            if (requestId is null)
            {
                return new EmailOperationResult("invalid_request_id");
            }

            // Keep the transport readiness check outside the short transaction. It may
            // cross the provider/configuration boundary and never supplies credentials
            // to the browser or to the durable record.
            var readiness = await emailSender.CheckReadinessAsync(targetOrganizationId, cancellationToken);
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

            var existing = await context.EmailOutbox.AsNoTracking()
                .Where(item => item.DeliveryClass == "operational_test" &&
                    item.RequestedByStaffUserId == actor.Id &&
                    item.OrganizationId == targetOrganizationId &&
                    item.RequestId == requestId)
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                return new EmailOperationResult("duplicate", OperationData(existing));
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var cooldown = await context.EmailOutbox.AsNoTracking()
                .Where(item => item.DeliveryClass == "operational_test" &&
                    item.RequestedByStaffUserId == actor.Id &&
                    item.OrganizationId == targetOrganizationId &&
                    item.CreatedUtc >= now.AddMinutes(-1))
                .OrderByDescending(item => item.CreatedUtc)
                .Select(item => new { item.CreatedUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (cooldown is not null)
            {
                var retryAfter = Math.Max(1, 60 - (int)Math.Floor((now - cooldown.CreatedUtc).TotalSeconds));
                return new EmailOperationResult("cooldown", new { retryAfterSeconds = retryAfter });
            }

            var currentActor = locked.Staff!;
            var settings = await ReadEffectiveSettingsAsync(context, targetOrganizationId, cancellationToken);
            var senderAddress = NormalizeSender(settings.FromAddress);
            var address = StaffEmail.TryNormalize(currentActor.NotificationEmail, out var normalized)
                ? normalized
                : null;
            var senderProblem = SenderProblem(settings.FromAddress, senderAddress);
            var suppression = address is null ? "recipient_missing_or_invalid" :
                !recipientDomainPolicy.IsAllowed(address) ? "recipient_domain_not_allowed" :
                senderProblem ?? (!readiness.IsConfigured ? "mail_not_configured" : null);
            var businessKey = $"operational-test:{actor.Id}:{targetOrganizationId}:{requestId}";
            var outbox = new EmailOutbox
            {
                OrganizationId = targetOrganizationId,
                BusinessKey = businessKey,
                DeliveryClass = "operational_test",
                RequestedByStaffUserId = actor.Id,
                RequestId = requestId,
                DeliveryMode = readiness.DeliveryMode,
                ToAddress = address,
                FromAddress = senderAddress ?? settings.FromAddress,
                FromName = settings.FromName,
                Subject = suppression is null ? $"ASAP test email - {organizationName}" : null,
                BodyText = suppression is null ? "A test email was requested." : null,
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
                outbox.BodyText = $"This is a real ASAP test email for {organizationName}.\n" +
                    $"Scope: {targetOrganizationId}.\n" +
                    $"Requested at: {now:O}.\n" +
                    $"Reference: {outbox.Id}.";
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            if (suppression is null) dispatcher.Enqueue(outbox.Id);
            return new EmailOperationResult(
                suppression is null ? "queued" : "suppressed",
                OperationData(outbox, suppression));
        }
    }

    public async Task<EmailOperationResult> GetTestContextAsync(
        CurrentStaff actor,
        int? organizationId,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out var scope))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        var targetOrganizationId = scope ?? 1;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organization = await context.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == targetOrganizationId, cancellationToken);
        if (organization is null) return new EmailOperationResult("organization_not_found");

        var settings = await ReadEffectiveSettingsAsync(context, targetOrganizationId, cancellationToken);
        var readiness = await emailSender.CheckReadinessAsync(targetOrganizationId, cancellationToken);
        var senderAddress = NormalizeSender(settings.FromAddress);
        var recipient = StaffEmail.TryNormalize(actor.NotificationEmail, out var normalized)
            ? normalized
            : null;
        var senderProblem = SenderProblem(settings.FromAddress, senderAddress);
        var blockingReason = !organization.IsActive ? "organization_inactive" :
            recipient is null ? "recipient_missing_or_invalid" :
            !recipientDomainPolicy.IsAllowed(recipient) ? "recipient_domain_not_allowed" :
            senderProblem ?? (!readiness.IsConfigured ? readiness.Code ?? "mail_not_configured" :
            !readiness.IsLive ? "non_delivery_mode" : null);
        var data = new EmailTestContext(
            targetOrganizationId,
            organization.DisplayName,
            settings.FromAddress,
            settings.FromName,
            recipient,
            readiness.DeliveryMode,
            readiness.IsConfigured,
            blockingReason is null,
            readiness.Code,
            blockingReason);
        return new EmailOperationResult("ok", data);
    }

    public async Task<EmailOperationResult> GetStatusAsync(
        CurrentStaff actor,
        long id,
        int? organizationId,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out var scope))
        {
            return new EmailOperationResult("staff_scope_forbidden");
        }

        var targetOrganizationId = scope ?? 1;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await context.EmailOutbox.AsNoTracking()
            .Where(row => row.Id == id && row.OrganizationId == targetOrganizationId &&
                row.DeliveryClass == "operational_test")
            .Select(row => new EmailOperationStatusItem(
                row.Id, row.OrganizationId, row.Status, row.DeliveryClass,
                row.AttemptCount, row.LastErrorCode, row.SuppressionReason,
                row.CreatedUtc, row.SentUtc, StaffVersion.Encode(row.RowVersion),
                row.DeliveryMode, row.ProviderMessageId))
            .SingleOrDefaultAsync(cancellationToken);
        return item is null
            ? new EmailOperationResult("not_found")
            : new EmailOperationResult("ok", item);
    }

    public async Task<IReadOnlyList<EmailOperationItem>> ListAsync(
        CurrentStaff actor,
        int? organizationId,
        string? status,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(actor, organizationId, out var scope)) return [];
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.EmailOutbox.AsNoTracking();
        if (scope.HasValue) query = query.Where(item => item.OrganizationId == scope.Value);
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(item => item.Status == "failed");
        }
        return await query
            .OrderByDescending(item => item.Status == "failed")
            .ThenByDescending(item => item.CreatedUtc)
            .Take(500)
            .Select(item => new EmailOperationItem(
                item.Id, item.OrganizationId, item.Status, item.BusinessKey, item.DeliveryClass,
                item.RecipientAddressKind, item.AttemptCount, item.LastErrorCode, item.SuppressionReason,
                item.CreatedUtc, item.SentUtc, StaffVersion.Encode(item.RowVersion),
                item.DeliveryMode, item.ProviderMessageId))
            .ToListAsync(cancellationToken);
    }

    public async Task<EmailOperationResult> RetryAsync(
        CurrentStaff actor,
        long id,
        string? encodedVersion,
        CancellationToken cancellationToken)
    {
        if (!CanOperate(actor)) return new EmailOperationResult("staff_scope_forbidden");
        if (!StaffVersion.TryDecode(encodedVersion, out var expectedVersion))
        {
            return new EmailOperationResult("invalid_version");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await context.EmailOutbox.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.OrganizationId })
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot is null) return new EmailOperationResult("not_found");
        if (!TryResolveScope(actor, snapshot.OrganizationId, out _))
            return new EmailOperationResult("staff_scope_forbidden");

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
        if (row is null) return new EmailOperationResult("not_found");
        if (!row.RowVersion.SequenceEqual(expectedVersion)) return new EmailOperationResult("stale_version");
        if (row.Status != "failed") return new EmailOperationResult("email_not_retryable");

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
        dispatcher.Enqueue(row.Id);
        return new EmailOperationResult("queued", new { id = row.Id, version = StaffVersion.Encode(row.RowVersion) });
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
            if (organization is null) return false;
        }
        return true;
    }

    private static int[] LockedOrganizations(CurrentStaff actor, int targetOrganizationId) =>
        new[] { actor.OrganizationId, targetOrganizationId }.Distinct().Order().ToArray();

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

    private static object OperationData(EmailOutbox item, string? code = null) => new
    {
        id = item.Id,
        code,
        status = item.Status,
        deliveryMode = item.DeliveryMode,
        providerMessageId = item.ProviderMessageId,
        version = StaffVersion.Encode(item.RowVersion)
    };

    private static string? NormalizeRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N");
        return Guid.TryParse(value.Trim(), out var parsed) && parsed != Guid.Empty
            ? parsed.ToString("N")
            : null;
    }

    private static bool TryResolveScope(CurrentStaff actor, int? requested, out int? scope)
    {
        if (!CanOperate(actor))
        {
            scope = null;
            return false;
        }
        if (actor.Role == "super_admin")
        {
            scope = requested;
            return !requested.HasValue || requested.Value > 0;
        }
        scope = actor.OrganizationId;
        return actor.OrganizationId > 1 && (!requested.HasValue || requested.Value == actor.OrganizationId);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSender(string? value) =>
        StaffEmail.TryNormalize(value, out var normalized) && normalized is not null
            ? normalized
            : null;

    private static string? SenderProblem(string? configured, string? normalized) =>
        string.IsNullOrWhiteSpace(configured)
            ? "sender_missing"
            : normalized is null ? "sender_invalid" : null;
}
