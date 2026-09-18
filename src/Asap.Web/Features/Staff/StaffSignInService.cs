using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed class StaffSignInService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task RecordSuccessfulSignInAsync(
        long staffUserId,
        string normalizedAuthenticationEmail,
        Guid tenantId,
        Guid objectId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || objectId == Guid.Empty ||
            !StaffEmail.TryNormalizeAuthenticationEmail(
                normalizedAuthenticationEmail,
                out _,
                out var normalizedEmail))
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var staff = await context.StaffUsers.SingleOrDefaultAsync(item => item.Id == staffUserId &&
            item.IsActive && item.NormalizedUserPrincipalName == normalizedEmail, cancellationToken);
        if (staff is null) return;
        var normalizedDisplayName = Clean(displayName);
        staff.EntraTenantId = tenantId;
        staff.EntraObjectId = objectId;
        if (normalizedDisplayName is not null)
        {
            staff.DisplayName = normalizedDisplayName;
        }
        staff.LastLoginUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
