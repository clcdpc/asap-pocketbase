using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

internal static class RequestLinkResolver
{
    public static async Task<long?> ResolveAsync(
        IQueryable<long> currentIds,
        string? rawId,
        CancellationToken cancellationToken)
    {
        var id = rawId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var currentId) &&
            currentId > 0 &&
            await currentIds.AnyAsync(candidate => candidate == currentId, cancellationToken))
        {
            return currentId;
        }

        return null;
    }
}
