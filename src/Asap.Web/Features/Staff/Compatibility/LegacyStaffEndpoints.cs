using System.Text.Json;
using Asap.Web.Features.Administration;
using Microsoft.AspNetCore.Antiforgery;

namespace Asap.Web.Features.Staff.Compatibility;

public static class LegacyStaffEndpoints
{
    private sealed record LegacyLibraryChoice(string OrgId, string Name);
    public static IEndpointRouteBuilder MapLegacyStaffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/staff/legacy/settings", LoadSettingsAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/legacy/settings", SaveSettingsAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/legacy/session", Session)
            .AllowAnonymous();
        endpoints.MapPost("/api/asap/staff/legacy/profile", SaveProfileAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/legacy/organizations", OrganizationsAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/legacy/patron-codes", PatronCodesAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/legacy/title-requests", TitleRequestsAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/legacy/additional-copies", AdditionalCopiesAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static IResult Session(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        if (context.Items[StaffCurrentUserMiddleware.ForbiddenItemKey] is string code)
        {
            return Results.Json(new
            {
                authenticated = true,
                accessAllowed = false,
                code,
                antiforgeryToken = tokens.RequestToken
            });
        }
        var staff = context.Items[StaffCurrentUserMiddleware.ItemKey] as CurrentStaff;
        return staff is null
            ? Results.Json(new { authenticated = false, antiforgeryToken = tokens.RequestToken })
            : Results.Json(new
            {
                authenticated = true,
                accessAllowed = true,
                antiforgeryToken = tokens.RequestToken,
                staff = ToLegacyStaff(staff)
            });
    }

    private static object ToLegacyStaff(CurrentStaff staff) => new
    {
        id = staff.Id.ToString(),
        userPrincipalName = staff.UserPrincipalName,
        username = staff.UserPrincipalName,
        identityKey = staff.UserPrincipalName,
        displayName = staff.DisplayName,
        notificationEmail = staff.NotificationEmail,
        role = staff.Role,
        organizationId = staff.OrganizationId,
        organizationName = staff.OrganizationName,
        libraryOrgId = staff.OrganizationId.ToString(),
        libraryOrgName = staff.OrganizationName,
        weeklyActionSummaryEnabled = staff.WeeklyActionSummaryEnabled,
        weeklyActionSummaryEmail = staff.WeeklyActionSummaryEmail,
        purchaseReminderDefault = staff.PurchaseReminderDefault,
        additionalCopyReminderDefault = staff.AdditionalCopyReminderDefault,
        defaultMineUnclaimedFilter = staff.DefaultMineUnclaimedFilter,
        weekly_action_summary_enabled = staff.WeeklyActionSummaryEnabled,
        weekly_action_summary_email = staff.WeeklyActionSummaryEmail,
        purchase_reminder_default = staff.PurchaseReminderDefault,
        additional_copy_reminder_default = staff.AdditionalCopyReminderDefault,
        default_mine_unclaimed_filter = staff.DefaultMineUnclaimedFilter,
        version = StaffVersion.Encode(staff.RowVersion)
    };

    private static async Task<IResult> SaveProfileAsync(
        HttpContext context,
        StaffProfileInput input,
        StaffProfileService profiles,
        CancellationToken cancellationToken)
    {
        var result = await profiles.UpdateAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context), input, cancellationToken);
        return result.Code switch
        {
            "updated" => Results.Json(new { staff = ToLegacyStaff(result.Staff!) }),
            "stale_version" => Results.Conflict(new
            {
                code = result.Code,
                message = "The profile changed. Review the latest values before saving again."
            }),
            "staff_session_invalid" => Results.Json(new { code = result.Code },
                statusCode: StatusCodes.Status401Unauthorized),
            "staff_scope_forbidden" => Results.Json(new { code = result.Code, accessAllowed = false },
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.BadRequest(new { code = result.Code, message = "The profile values are invalid." })
        };
    }

    private static async Task<IResult> OrganizationsAsync(
        HttpContext context,
        AdministrationService administration,
        CancellationToken cancellationToken)
    {
        var result = await administration.ListOrganizationsAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context), cancellationToken);
        return result.Code == "ok" ? Results.Json(result.Data) : ToResult(result);
    }

    private static async Task<IResult> PatronCodesAsync(
        HttpContext context,
        string? orgId,
        AdministrationService administration,
        CancellationToken cancellationToken)
    {
        var result = await administration.ListPatronCodesAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context), orgId, cancellationToken);
        return result.Code == "ok" ? Results.Json(result.Data) : ToResult(result);
    }

    private static async Task<IResult> TitleRequestsAsync(
        HttpContext context,
        string? scope,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        var result = await views.ListAsync(actor, scope, cancellationToken);
        if (result is null)
        {
            return Results.BadRequest(new { code = "invalid_scope" });
        }
        return Results.Json(new
        {
            result.Items,
            scope = LegacyScope(actor, result.Scope, result.Organizations),
            availableLibraries = LegacyLibraries(result.Organizations)
        });
    }

    private static async Task<IResult> AdditionalCopiesAsync(
        HttpContext context,
        string? scope,
        string? status,
        AdditionalCopyService copies,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        var result = await copies.ListAsync(actor, scope, status, cancellationToken);
        if (result is null)
        {
            return Results.BadRequest(new { code = "invalid_scope_or_status" });
        }
        return Results.Json(new
        {
            result.Items,
            result.Status,
            scope = LegacyScope(actor, result.Scope, result.AvailableLibraries),
            availableLibraries = LegacyLibraries(result.AvailableLibraries)
        });
    }

    private static object LegacyScope(CurrentStaff actor, string selected, IReadOnlyList<object> libraries)
    {
        var choices = LegacyLibraries(libraries);
        return new
        {
            superAdmin = actor.Role == "super_admin",
            mode = selected == "all" ? "all" : "library",
            libraryOrgId = selected == "all" ? null : selected,
            label = selected == "all" ? "All libraries" :
                choices.SingleOrDefault(item => item.OrgId == selected)?.Name ?? selected
        };
    }

    private static IReadOnlyList<LegacyLibraryChoice> LegacyLibraries(IReadOnlyList<object> libraries) =>
        libraries.Select(item =>
        {
            var value = JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new LegacyLibraryChoice(value.GetProperty("id").ToString(),
                value.GetProperty("name").GetString() ?? "");
        }).ToArray();

    private static async Task<IResult> LoadSettingsAsync(
        HttpContext context,
        string? orgId,
        LegacyStaffSettingsService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.LoadAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context), orgId, cancellationToken);
            return result.Code == "ok" ? Results.Json(result.Data) : ToResult(result);
        }
        catch (InvalidOperationException error)
        {
            return Results.BadRequest(new { code = "settings_invalid", message = error.Message });
        }
    }

    private static async Task<IResult> SaveSettingsAsync(
        HttpContext context,
        JsonElement payload,
        LegacyStaffSettingsService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToResult(await service.SaveAsync(
                StaffAuthenticationEndpoints.RequireCurrentStaff(context), payload, cancellationToken));
        }
        catch (InvalidOperationException error)
        {
            return Results.BadRequest(new { code = "settings_invalid", message = error.Message });
        }
    }

    private static IResult ToResult(AdministrationResult result)
    {
        var status = result.Code switch
        {
            "staff_scope_forbidden" => StatusCodes.Status403Forbidden,
            "staff_session_invalid" => StatusCodes.Status401Unauthorized,
            "organization_not_found" => StatusCodes.Status404NotFound,
            "stale_version" => StatusCodes.Status409Conflict,
            "saved" or "partial" => StatusCodes.Status200OK,
            _ => StatusCodes.Status400BadRequest
        };
        var operationPhase = result.Code switch
        {
            "saved" => "complete",
            "partial" => "partial",
            _ => "rejected"
        };
        return Results.Json(new { code = result.Code, message = result.Message, data = result.Data, operationPhase }, statusCode: status);
    }
}
