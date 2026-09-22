using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Features.Staff;

public static class AdditionalCopyEndpoints
{
    public static IEndpointRouteBuilder MapAdditionalCopyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/asap/staff/additional-copies").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/{id:long}/claim", ClaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/unclaim", UnclaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/assign", AssignAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/close", CloseAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/reopen", ReopenAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapDelete("/{id:long}", DeleteAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapGet("/api/asap/staff/title-requests/{id:long}/additional-copy", PreviewAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/title-requests/{id:long}/additional-copy", CreateAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        string? scope,
        string? status,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(Current(context), scope, status, cancellationToken);
        return result is null
            ? Results.BadRequest(new { code = "invalid_scope_or_status" })
            : Results.Json(result);
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string id,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(Current(context), id, null, cancellationToken);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    private static async Task<IResult> PreviewAsync(
        HttpContext context,
        long id,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await service.PreviewAsync(Current(context), id, cancellationToken);
        return result.Code == "loaded" ? Results.Json(result.Preview) : Error(result.Code);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        long id,
        AdditionalCopyCreateInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(Current(context), id, input, cancellationToken);
        if (result.Code != "created")
        {
            return Error(result.Code);
        }
        var request = await service.GetAsync(Current(context), result.RequestId!.Value.ToString(), null, cancellationToken);
        return Results.Json(new
        {
            additionalCopyRequest = request,
            result.OpenCountBefore,
            result.OpenCountAfter,
            purchaseReminderEmail = new
            {
                requested = result.ReminderRequested,
                queued = result.DispatchOutboxId.HasValue
            }
        });
    }

    private static Task<IResult> ClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.ClaimAsync(Current(context), id, input, false, cancellationToken), service, cancellationToken);

    private static Task<IResult> UnclaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.ClaimAsync(Current(context), id, input, true, cancellationToken), service, cancellationToken);

    private static Task<IResult> AssignAsync(
        HttpContext context,
        long id,
        AssignAdditionalCopyInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.AssignAsync(Current(context), id, input, cancellationToken), service, cancellationToken);

    private static Task<IResult> CloseAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.SetClosedAsync(Current(context), id, input, false, cancellationToken), service, cancellationToken);

    private static Task<IResult> ReopenAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.SetClosedAsync(Current(context), id, input, true, cancellationToken), service, cancellationToken);

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        long id,
        [FromBody] VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await service.DeleteClosedAsync(Current(context), id, input, cancellationToken);
        return result.Code == "deleted" ? Results.Json(new { deleted = true }) : Error(result.Code);
    }

    private static async Task<IResult> MutateAndLoadAsync(
        HttpContext context,
        long id,
        Task<AdditionalCopyMutationResult> mutation,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        var result = await mutation;
        if (result.Code != "updated")
        {
            return Error(result.Code);
        }
        var request = await service.GetAsync(
            Current(context),
            id.ToString(),
            result.ClaimClearedReason,
            cancellationToken);
        return request is null ? Results.NotFound() : Results.Json(request);
    }

    private static IResult Error(string code) => code switch
    {
        "not_found" => Results.NotFound(new { code }),
        "staff_session_invalid" => Results.Json(new { code }, statusCode: StatusCodes.Status401Unauthorized),
        "staff_scope_forbidden" or "claim_forbidden" or "delete_forbidden" =>
            Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden),
        "stale_version" or "claim_conflict" or "organization_inactive" =>
            Results.Conflict(new { code, message = "The task changed or is no longer actionable. Reload before continuing." }),
        _ => Results.BadRequest(new { code, message = "The additional-copy change is invalid." })
    };

    private static CurrentStaff Current(HttpContext context) =>
        StaffAuthenticationEndpoints.RequireCurrentStaff(context);
}
