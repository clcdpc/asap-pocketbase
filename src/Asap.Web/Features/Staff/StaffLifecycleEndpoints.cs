using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Features.Staff;

public static class StaffLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapStaffLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/staff/assignment-candidates", AssignmentCandidatesAsync)
            .RequireAuthorization();
        var group = endpoints.MapGroup("/api/asap/staff/users")
            .RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPatch("/{id:long}", UpdateMetadataAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/role", ChangeRoleAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapDelete("/{id:long}", DeactivateAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/rebind", RebindAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    private static async Task<IResult> AssignmentCandidatesAsync(
        HttpContext context,
        int libraryOrgId,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (libraryOrgId <= 1 || actor.Role != "super_admin" && actor.OrganizationId != libraryOrgId)
        {
            return Results.Json(
                new { code = "staff_scope_forbidden", message = "Staff access is not available for this library." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var candidates = await lifecycle.ListAssignmentCandidatesAsync(libraryOrgId, cancellationToken);
        return Results.Json(new
        {
            candidates = candidates.Select(item => new
            {
                id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.DisplayName
            })
        });
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        int? orgId,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        if (actor.Role is not ("admin" or "super_admin"))
        {
            return Forbidden();
        }

        var users = await lifecycle.ListAsync(actor, orgId, cancellationToken);
        return Results.Json(new
        {
            canAssignSuperAdmin = actor.Role == "super_admin",
            users = users.Select(ToDto)
        });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        StaffCreateInput input,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.CreateAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            input,
            cancellationToken),
            StatusCodes.Status201Created);

    private static async Task<IResult> UpdateMetadataAsync(
        HttpContext context,
        long id,
        StaffMetadataInput input,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.UpdateMetadataAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            id,
            input,
            cancellationToken));

    private static async Task<IResult> ChangeRoleAsync(
        HttpContext context,
        long id,
        StaffRoleInput input,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.ChangeRoleAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            id,
            input,
            cancellationToken));

    private static async Task<IResult> DeactivateAsync(
        HttpContext context,
        long id,
        [FromBody] StaffDeactivateInput input,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.DeactivateAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            id,
            input,
            cancellationToken));

    private static async Task<IResult> RebindAsync(
        HttpContext context,
        long id,
        StaffRebindInput input,
        StaffLifecycleService lifecycle,
        CancellationToken cancellationToken) =>
        ToResult(await lifecycle.RebindAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            id,
            input,
            cancellationToken));

    private static IResult ToResult(StaffLifecycleResult result, int successStatus = StatusCodes.Status200OK) =>
        result.Code switch
        {
            "created" or "updated" => Results.Json(new
            {
                user = ToDto(result.User!),
                cleanup = new
                {
                    rulesDeactivated = result.RulesDeactivated,
                    openTitleClaimsCleared = result.OpenTitleClaimsCleared,
                    openAdditionalCopyClaimsCleared = result.OpenAdditionalCopyClaimsCleared
                }
            }, statusCode: successStatus),
            "not_found" or "organization_not_found" => Results.NotFound(new { code = result.Code }),
            "staff_scope_forbidden" => Forbidden(),
            "stale_version" or "active_super_admin_required" or "organization_inactive" or
                "staff_invariant_busy" or "identity_already_exists" => Results.Conflict(new
                {
                    code = result.Code,
                    message = result.Code == "active_super_admin_required"
                        ? "At least one currently usable super administrator must remain."
                        : "The staff access change conflicts with current state."
                }),
            _ => Results.BadRequest(new { code = result.Code, message = "The staff access values are invalid." })
        };

    internal static object ToDto(Infrastructure.Data.StaffUser user) => new
    {
        id = user.Id.ToString(),
        tenantId = user.EntraTenantId,
        objectId = user.EntraObjectId,
        userPrincipalName = user.UserPrincipalName,
        displayName = user.DisplayName,
        notificationEmail = user.NotificationEmail,
        role = user.Role,
        organizationId = user.OrganizationId,
        active = user.IsActive,
        weeklyActionSummaryEnabled = user.WeeklyActionSummaryEnabled,
        weeklyActionSummaryEmail = user.WeeklyActionSummaryEmail,
        purchaseReminderDefault = user.PurchaseReminderDefault,
        additionalCopyReminderDefault = user.AdditionalCopyReminderDefault,
        defaultMineUnclaimedFilter = user.DefaultMineUnclaimedFilter,
        version = StaffVersion.Encode(user.RowVersion)
    };

    private static IResult Forbidden() => Results.Json(
        new { code = "staff_scope_forbidden", message = "Admin access is required." },
        statusCode: StatusCodes.Status403Forbidden);
}
