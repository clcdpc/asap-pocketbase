using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed class StaffSignInService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task RecordSuccessfulSignInAsync(
        long staffUserId,
        string? userPrincipalName,
        string? displayName,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var staff = await context.StaffUsers.SingleAsync(item => item.Id == staffUserId, cancellationToken);
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
