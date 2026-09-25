namespace Asap.Web.Features.Staff;

internal static class StaffHoldStatus
{
    internal static bool IsTerminal(string? status) =>
        status?.Trim().ToLowerInvariant() is "unclaimed" or "cancelled" or "expired";

    internal static bool IsActiveSameBib(PolarisHoldSnapshot hold, int bibId) =>
        hold.BibId == bibId && !IsTerminal(hold.StatusDescription);
}
