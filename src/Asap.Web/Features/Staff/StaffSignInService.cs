using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed class StaffSignInService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task RecordSuccessfulSignInAsync(
        StaffIdentityEvidence evidence,
        string? userPrincipalName,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (evidence.TenantId == Guid.Empty || evidence.ObjectId == Guid.Empty) return;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var staff = await context.StaffUsers.SingleOrDefaultAsync(item => item.Id == evidence.StaffUserId &&
            item.IsActive && item.EntraTenantId == evidence.TenantId && item.EntraObjectId == evidence.ObjectId, cancellationToken);
        if (staff is null) return;
        var normalizedUpn = Clean(userPrincipalName);
        var normalizedDisplayName = Clean(displayName);
        if (normalizedUpn is not null)
        {
            staff.UserPrincipalName = normalizedUpn;
            staff.NormalizedUserPrincipalName = normalizedUpn.ToUpperInvariant();
        }
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
