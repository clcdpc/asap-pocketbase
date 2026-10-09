using Asap.Web.Infrastructure.Data;
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
        group.MapPost("/{id:long}/clear-claim", ClearClaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
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
        if (!LibraryScope.TryParse(scope, LibraryScope.All, out var parsedScope))
        {
            return Results.BadRequest(new { code = "invalid_scope_or_status" });
        }
        var result = await service.ListAsync(Current(context), parsedScope, status, cancellationToken);
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
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        AdditionalCopyMutationResult result;
        try
        {
            result = await service.CreateAsync(Current(context), id, input, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OutcomeUnconfirmed();
        }
        if (result.Code != "created")
        {
            return Error(result.Code);
        }
        var request = await TryLoadCommittedAsync(context, result.RequestId!.Value, null,
            service, loggerFactory, cancellationToken);
        return Results.Json(new
        {
            committed = true,
            additionalCopyRequestId = result.RequestId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            finalStatus = result.FinalStatus,
            notificationStatus = result.NotificationStatus,
            notificationReason = result.NotificationReason,
            additionalCopyRequest = request,
            refreshUnavailable = request is null,
            result.OpenCountBefore,
            result.OpenCountAfter,
            purchaseReminderEmail = new
            {
                requested = result.ReminderRequested,
                queued = result.NotificationStatus == "queued"
            }
        });
    }

    private static Task<IResult> ClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.ClaimAsync(Current(context), id, input, false, cancellationToken), service, loggerFactory, cancellationToken);

    private static Task<IResult> UnclaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.ClaimAsync(Current(context), id, input, true, cancellationToken), service, loggerFactory, cancellationToken);

    private static Task<IResult> ClearClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.ClearClaimAsync(Current(context), id, input, cancellationToken), service, loggerFactory, cancellationToken);

    private static Task<IResult> AssignAsync(
        HttpContext context,
        long id,
        AssignAdditionalCopyInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.AssignAsync(Current(context), id, input, cancellationToken), service, loggerFactory, cancellationToken);

    private static Task<IResult> CloseAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.SetClosedAsync(Current(context), id, input, false, cancellationToken), service, loggerFactory, cancellationToken);

    private static Task<IResult> ReopenAsync(
        HttpContext context,
        long id,
        VersionInput input,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, service.SetClosedAsync(Current(context), id, input, true, cancellationToken), service, loggerFactory, cancellationToken);

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        long id,
        [FromBody] VersionInput input,
        AdditionalCopyService service,
        CancellationToken cancellationToken)
    {
        AdditionalCopyMutationResult result;
        try
        {
            result = await service.DeleteClosedAsync(Current(context), id, input, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OutcomeUnconfirmed();
        }
        return result.Code == "deleted" ? Results.Json(new { deleted = true }) : Error(result.Code);
    }

    private static async Task<IResult> MutateAndLoadAsync(
        HttpContext context,
        long id,
        Task<AdditionalCopyMutationResult> mutation,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        AdditionalCopyMutationResult result;
        try
        {
            result = await mutation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OutcomeUnconfirmed();
        }
        if (result.Code != "updated")
        {
            return Error(result.Code);
        }
        var request = await TryLoadCommittedAsync(context, id, result.ClaimClearedReason,
            service, loggerFactory, cancellationToken);
        return request is null
            ? Results.Json(new { committed = true, request = (AdditionalCopyDto?)null,
                finalStatus = result.FinalStatus, notificationStatus = result.NotificationStatus,
                notificationReason = result.NotificationReason, refreshUnavailable = true })
            : Results.Json(request with { Committed = true,
                FinalStatus = result.FinalStatus ?? request.Status,
                NotificationStatus = result.NotificationStatus,
                NotificationReason = result.NotificationReason });
    }

    private static IResult OutcomeUnconfirmed() => Results.Json(new
    {
        code = "additional_copy_outcome_unconfirmed",
        message = "The additional-copy outcome could not be confirmed. Reload before trying again."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<AdditionalCopyDto?> TryLoadCommittedAsync(
        HttpContext context,
        long id,
        string? claimClearedReason,
        AdditionalCopyService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await service.GetAsync(Current(context), id.ToString(), claimClearedReason, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        // The task mutation committed; preserve that accepted outcome if the optional detail refresh fails.
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("Asap.Web.Features.Staff.AdditionalCopyEndpoints")
                .LogError(exception, "Additional-copy detail refresh failed after task {RequestId} committed", id);
            return null;
        }
    }

    private static IResult Error(string code) => code switch
    {
        "not_found" => Results.NotFound(new { code }),
        "staff_session_invalid" => Results.Json(new { code }, statusCode: StatusCodes.Status401Unauthorized),
        "staff_scope_forbidden" or "claim_forbidden" or "delete_forbidden" =>
            Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden),
        "stale_version" or "actor_changed_since_preview" or "claim_conflict" or "organization_inactive" =>
            Results.Conflict(new { code, message = "The task changed or is no longer actionable. Reload before continuing." }),
        "notification_dependency_unavailable" => Results.Json(new
        {
            code,
            message = "Reminder configuration is temporarily unavailable. The task was not changed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new { code, message = "The additional-copy change is invalid." })
    };

    private static CurrentStaff Current(HttpContext context) =>
        StaffAuthenticationEndpoints.RequireCurrentStaff(context);
}
