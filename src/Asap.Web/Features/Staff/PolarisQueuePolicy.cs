namespace Asap.Web.Features.Staff;

internal static class PolarisQueuePolicy
{
    public static string? HoldingsFailure(StaffBibHoldingsSummary? holdings, bool additionalCopy)
    {
        if (holdings is null || holdings.MyLibraryCount < 0 || holdings.OtherLibraryCount < 0 ||
            (long)holdings.MyLibraryCount + holdings.OtherLibraryCount != holdings.ConsortiumCount ||
            holdings.ConsortiumCount == 0 && holdings.IsHoldable ||
            holdings.HasHoldableAtMyLibrary && (holdings.MyLibraryCount == 0 || !holdings.IsHoldable))
        {
            return "holdings_unavailable";
        }
        if (additionalCopy && holdings.ConsortiumCount == 0)
        {
            return "bib_not_owned";
        }
        return holdings.IsHoldable ? null : "bib_not_holdable";
    }
}
