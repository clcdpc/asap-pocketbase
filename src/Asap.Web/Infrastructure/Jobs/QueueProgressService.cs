using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Infrastructure.Jobs;

public static class QueueNames
{
    public const string IdentifierProcessing = "IdentifierProcessing";
    public const string PurchasePromotion = "PurchasePromotion";
    public const string HoldPlacement = "HoldPlacement";
    public const string FulfillmentTracking = "FulfillmentTracking";
    public const string OutstandingTimeout = "OutstandingTimeout";
    public const string PendingHoldTimeout = "PendingHoldTimeout";
    public const string HoldPickupTimeout = "HoldPickupTimeout";
    public const string AdditionalCopyTimeout = "AdditionalCopyTimeout";
    public const string HoldRecovery = "HoldRecovery";

    public static readonly IReadOnlySet<string> Configured = new HashSet<string>(
        [IdentifierProcessing, PurchasePromotion, HoldPlacement, FulfillmentTracking,
         OutstandingTimeout, PendingHoldTimeout, HoldPickupTimeout, AdditionalCopyTimeout],
        StringComparer.Ordinal);
}

public sealed record QueueProgressSnapshot(
    string QueueName,
    int ScopeOrganizationId,
    DateTime? LastCreatedUtc,
    long? LastItemId,
    long? CycleMaxId,
    long? LastOutcomeItemId,
    string? LastOutcomeCode,
    DateTime? LastOutcomeUtc,
    DateTime UpdatedUtc,
    string Version);

public sealed class QueueProgressService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task<QueueProgress> GetOrCreateAsync(
        string queueName,
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        if (!QueueNames.Configured.Contains(queueName) && queueName != QueueNames.HoldRecovery)
        {
            throw new ArgumentOutOfRangeException(nameof(queueName), queueName, "Unknown workflow queue.");
        }
        var scope = NormalizeScope(scopeOrganizationId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var progress = await context.QueueProgress
            .SingleOrDefaultAsync(
                item => item.QueueName == queueName && item.ScopeOrganizationId == scope,
                cancellationToken);
        if (progress is not null)
        {
            return progress;
        }

        progress = new QueueProgress
        {
            QueueName = queueName,
            ScopeOrganizationId = scope,
            CycleMaxId = null,
            UpdatedUtc = DateTime.UtcNow
        };
        context.QueueProgress.Add(progress);
        await context.SaveChangesAsync(cancellationToken);
        return progress;
    }

    public static bool IsAfter(
        DateTime createdUtc,
        long id,
        DateTime? lastCreatedUtc,
        long? lastId) =>
        !lastCreatedUtc.HasValue ||
        createdUtc > lastCreatedUtc.Value ||
        createdUtc == lastCreatedUtc.Value && id > lastId.GetValueOrDefault();

    public static bool IsInCycle(long id, long? cycleMaxId) =>
        !cycleMaxId.HasValue || id <= cycleMaxId.Value;

    public async Task<QueueProgressSnapshot?> GetSnapshotAsync(
        string queueName,
        int? scopeOrganizationId,
        CancellationToken cancellationToken)
    {
        var scope = NormalizeScope(scopeOrganizationId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var progress = await context.QueueProgress.AsNoTracking().SingleOrDefaultAsync(
            item => item.QueueName == queueName && item.ScopeOrganizationId == scope,
            cancellationToken);
        return progress is null
            ? null
            : new QueueProgressSnapshot(
                progress.QueueName,
                progress.ScopeOrganizationId,
                progress.LastCreatedUtc,
                progress.LastItemId,
                progress.CycleMaxId,
                progress.LastOutcomeItemId,
                progress.LastOutcomeCode,
                progress.LastOutcomeUtc,
                progress.UpdatedUtc,
                Convert.ToBase64String(progress.RowVersion));
    }

    public static int NormalizeScope(int? scopeOrganizationId) => scopeOrganizationId ?? 1;
}
