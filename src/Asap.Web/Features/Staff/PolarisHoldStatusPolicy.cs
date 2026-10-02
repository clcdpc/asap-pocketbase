using Clc.Polaris.Api.Models;

namespace Asap.Web.Features.Staff;

internal static class PolarisHoldStatusPolicy
{
    public static string? TerminalReason(int statusId) => (HoldStatus)statusId switch
    {
        HoldStatus.Unclaimed => "hold_unclaimed",
        HoldStatus.Expired => "hold_expired",
        HoldStatus.Cancelled => "hold_cancelled",
        _ => null
    };

    public static bool IsTerminal(int statusId) => TerminalReason(statusId) is not null;
}
