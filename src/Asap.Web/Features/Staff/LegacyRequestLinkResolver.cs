using System.Globalization;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

internal static class LegacyRequestLinkResolver
{
    public const string TitleRequestEntityType = "title_request";
    public const string AdditionalCopyEntityType = "additional_copy";

    public static async Task<long?> ResolveAsync(
        AsapDbContext context,
        IQueryable<long> currentIds,
        string entityType,
        string? rawId,
        CancellationToken cancellationToken)
    {
        var id = rawId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        // A current target ID wins when it exists; otherwise a typed mapping also accepts
        // numeric-looking PocketBase IDs without confusing the two request types.
        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var currentId) &&
            currentId > 0 &&
            await currentIds.AnyAsync(candidate => candidate == currentId, cancellationToken))
        {
            return currentId;
        }

        return await context.LegacyPocketBaseMappings.AsNoTracking()
            .Where(item => item.EntityType == entityType && item.PocketBaseId == id)
            .Select(item => (long?)item.NewId)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
