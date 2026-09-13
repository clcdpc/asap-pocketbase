using Microsoft.AspNetCore.Authentication;

namespace Asap.Web.Features.Staff;

public sealed class StaffCurrentUserMiddleware(RequestDelegate next)
{
    public const string ItemKey = "Asap.CurrentStaff";

    public async Task InvokeAsync(HttpContext context, StaffEligibilityService eligibility)
    {
        var protectedPath = context.Request.Path.StartsWithSegments("/api/asap/staff") ||
                            context.Request.Path.StartsWithSegments("/hangfire");
        if (!protectedPath || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (!StaffClaims.TryRead(context.User, out var evidence))
        {
            await RejectAsync(context, "staff_session_invalid");
            return;
        }

        var result = await eligibility.EvaluateAsync(
            evidence,
            null,
            StaffRoleRequirement.Any,
            requireParticipation: true,
            context.RequestAborted);
        if (result.Outcome == StaffEligibilityOutcome.InvalidIdentity)
        {
            await context.SignOutAsync("AsapStaffCookie");
            await RejectAsync(context, result.Code);
            return;
        }

        if (result.Outcome == StaffEligibilityOutcome.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = result.Code, message = "Access is not currently available." }, context.RequestAborted);
            return;
        }

        context.Items[ItemKey] = result.Staff!;
        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code, message = "Your staff session is no longer valid." }, context.RequestAborted);
    }
}
