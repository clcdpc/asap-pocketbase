using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Asap.Web.Features.Staff;

public static class StaffAuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapStaffAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/staff/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var staff = context.Items[StaffCurrentUserMiddleware.ItemKey] as CurrentStaff;
            if (context.Items[StaffCurrentUserMiddleware.ForbiddenItemKey] is string code)
            {
                return Results.Json(new { authenticated = true, accessAllowed = false, code, antiforgeryToken = tokens.RequestToken });
            }
            return Results.Json(staff is null
                ? new
                {
                    authenticated = false,
                    antiforgeryToken = tokens.RequestToken
                }
                : new
                {
                    authenticated = true,
                    accessAllowed = true,
                    antiforgeryToken = tokens.RequestToken,
                    staff = ToSessionDto(staff)
                });
        }).AllowAnonymous();

        endpoints.MapGet("/api/asap/staff/sign-in", (string? returnUrl) =>
        {
            var target = LocalReturnUrl(returnUrl);
            return Results.Challenge(
                new OpenIdConnectChallengeProperties { RedirectUri = target, Prompt = "select_account" },
                ["AsapEntra"]);
        }).AllowAnonymous();

        endpoints.MapPost("/api/asap/staff/sign-out", async (HttpContext context) =>
        {
            await context.SignOutAsync("AsapStaffCookie");
            return Results.Json(new { signedOut = true });
        }).RequireAuthorization().AddEndpointFilter<StaffAntiforgeryFilter>();

        endpoints.MapPost("/api/asap/staff/profile", UpdateProfileAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();

        return endpoints;
    }

    public static CurrentStaff RequireCurrentStaff(HttpContext context) =>
        context.Items[StaffCurrentUserMiddleware.ItemKey] as CurrentStaff ??
        throw new InvalidOperationException("Current staff was not resolved.");

    internal static string LocalReturnUrl(string? value, string fallback = "/staff/")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (value.Any(char.IsControl))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            trimmed.Contains('\\') ||
            trimmed.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(trimmed, UriKind.Relative, out _) ||
            !HasValidEscapes(trimmed) ||
            !HasSafeEscapedCharacters(trimmed) ||
            !HasSafeEscapedPath(trimmed))
        {
            return fallback;
        }

        return trimmed;
    }

    private static bool HasValidEscapes(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool HasSafeEscapedPath(string value)
    {
        var pathEnd = value.IndexOfAny(['?', '#']);
        var path = pathEnd < 0 ? value : value[..pathEnd];
        for (var depth = 0; depth < 8; depth++)
        {
            var decoded = Uri.UnescapeDataString(path);
            if (decoded.Any(char.IsControl) ||
                decoded.Contains('\\') ||
                decoded.Contains("//", StringComparison.Ordinal) ||
                decoded.Split('/').Any(segment => segment is "." or ".."))
            {
                return false;
            }

            if (decoded == path)
            {
                return true;
            }

            path = decoded;
        }

        return false;
    }

    private static bool HasSafeEscapedCharacters(string value)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            var decoded = Uri.UnescapeDataString(value);
            if (decoded.Any(char.IsControl) || decoded.Contains('\\'))
            {
                return false;
            }

            if (decoded == value)
            {
                return true;
            }

            value = decoded;
        }

        return false;
    }

    internal static object ToSessionDto(CurrentStaff staff) => new
    {
        id = staff.Id.ToString(),
        tenantId = staff.EntraTenantId.ToString("D"),
        authenticationEmail = staff.AuthenticationEmail,
        userPrincipalName = staff.UserPrincipalName,
        displayName = staff.DisplayName,
        notificationEmail = staff.NotificationEmail,
        role = staff.Role,
        organizationId = staff.OrganizationId,
        organizationName = staff.OrganizationName,
        weeklyActionSummaryEnabled = staff.WeeklyActionSummaryEnabled,
        weeklyActionSummaryEmail = staff.WeeklyActionSummaryEmail,
        purchaseReminderDefault = staff.PurchaseReminderDefault,
        additionalCopyReminderDefault = staff.AdditionalCopyReminderDefault,
        defaultMineUnclaimedFilter = staff.DefaultMineUnclaimedFilter,
        version = Convert.ToBase64String(staff.RowVersion)
    };

    private static async Task<IResult> UpdateProfileAsync(
        HttpContext context,
        StaffProfileInput input,
        StaffProfileService profiles,
        CancellationToken cancellationToken)
    {
        var result = await profiles.UpdateAsync(
            RequireCurrentStaff(context),
            input,
            cancellationToken);
        return result.Code switch
        {
            "updated" => Results.Json(new { staff = ToSessionDto(result.Staff!) }),
            "stale_version" => Results.Conflict(new
            {
                code = result.Code,
                message = "The profile changed. Review the latest values before saving again."
            }),
            "staff_session_invalid" => Results.Json(
                new { code = result.Code },
                statusCode: StatusCodes.Status401Unauthorized),
            "staff_scope_forbidden" => Results.Json(
                new { code = result.Code, accessAllowed = false },
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.BadRequest(new { code = result.Code, message = "The profile values are invalid." })
        };
    }
}
