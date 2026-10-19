using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Email;

internal static class PatronNotificationIdentity
{
    public static string? SuppressionReason(
        PatronSnapshot? patron,
        TitleRequest request,
        HoldPlacementOperation? operation = null)
    {
        if (patron is null)
        {
            return "patron_refresh_unavailable";
        }
        if (patron.PatronId <= 0 ||
            !patron.KnownBarcodeAliases.Contains(request.Barcode, StringComparer.OrdinalIgnoreCase) ||
            operation is not null &&
            !patron.KnownBarcodeAliases.Contains(operation.PatronBarcodeSnapshot, StringComparer.OrdinalIgnoreCase))
        {
            return "patron_identity_unverified";
        }
        if (request.PatronIdSnapshot.HasValue && request.PatronIdSnapshot.Value != patron.PatronId ||
            operation?.PatronIdSnapshot is { } operationPatronId && operationPatronId != patron.PatronId)
        {
            return "native_patron_identity_mismatch";
        }
        // A current barcode lookup cannot prove ownership of an unbound historical request.
        // A hold journal with its own saved native principal can establish that correlation.
        if (!request.PatronIdSnapshot.HasValue && operation?.PatronIdSnapshot is null)
        {
            return "native_patron_identity_unavailable";
        }

        return null;
    }
}
