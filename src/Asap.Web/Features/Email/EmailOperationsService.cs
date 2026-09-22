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
    string Version);

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
        }

        // Readiness may call the final transport/configuration boundary. Keep it outside
        // the short SQL transaction that commits the durable intent.
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

        var currentActor = locked.Staff!;
        var settings = await ReadEffectiveSettingsAsync(context, targetOrganizationId, cancellationToken);
        var address = StaffEmail.TryNormalize(currentActor.NotificationEmail, out var normalized)
            ? normalized
            : null;
        var suppression = address is null ? "recipient_missing_or_invalid" :
            !recipientDomainPolicy.IsAllowed(address) ? "recipient_domain_not_allowed" :
            string.IsNullOrWhiteSpace(settings.FromAddress) ? "sender_missing" :
            !readiness.IsConfigured ? "mail_not_configured" : null;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var outbox = new EmailOutbox
        {
            OrganizationId = targetOrganizationId,
            BusinessKey = null,
            DeliveryClass = "operational_test",
            ToAddress = address,
            FromAddress = settings.FromAddress,
            FromName = settings.FromName,
            Subject = suppression is null ? "ASAP test email" : null,
            BodyText = suppression is null ? "This is a test email from ASAP." : null,
            Status = suppression is null ? "pending" : "suppressed",
            SuppressionReason = suppression,
            NextAttemptUtc = suppression is null ? now : null,
            CreatedUtc = now,
            SuppressedUtc = suppression is null ? null : now
        };
        context.EmailOutbox.Add(outbox);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (suppression is null) dispatcher.Enqueue(outbox.Id);
        return new EmailOperationResult(
            suppression is null ? "queued" : "suppressed",
            new { id = outbox.Id, code = suppression, version = StaffVersion.Encode(outbox.RowVersion) });
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
                item.CreatedUtc, item.SentUtc, StaffVersion.Encode(item.RowVersion)))
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
}
