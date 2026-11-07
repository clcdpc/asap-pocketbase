using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Analytics;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/staff/analytics", GetAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string? scope,
        string? orgId,
        string? range,
        string? startDate,
        string? endDate,
        AnalyticsService service,
        CancellationToken cancellationToken)
    {
        if (!LibraryScope.TryParse(string.IsNullOrWhiteSpace(scope) ? orgId : scope, LibraryScope.All, out var parsedScope))
        {
            return Results.BadRequest(new { code = "invalid_scope" });
        }
        var result = await service.GetAsync(
            StaffAuthenticationEndpoints.RequireCurrentStaff(context),
            parsedScope,
            range,
            startDate,
            endDate,
            cancellationToken);
        return result.Code switch
        {
            "invalid_scope" => Results.BadRequest(new { code = "invalid_scope", message = "The analytics scope is invalid." }),
            "invalid_date_range" => Results.BadRequest(new
            {
                code = "invalid_date_range",
                message = "Choose a valid custom date range.",
                errors = result.Errors
            }),
            _ => Results.Json(result.Data)
        };
    }
}
