using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Features.Staff;

public static class TitleRequestEndpoints
{
    public sealed record BibLookupInput(string? BibId);

    public static IEndpointRouteBuilder MapTitleRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/asap/staff/title-requests").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/{id:long}/claim", ClaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/unclaim", UnclaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/assign", AssignAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/action", ActionAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/retry-identifier-check", RetryIdentifierAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/pickup-options", PickupOptionsAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/pickup-preference", PickupPreferenceAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/place-hold", PlaceHoldAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/hold-operations/{id:long}/reconcile", ReconcileHoldAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/hold-operations/{id:long}/resolve", ResolveHoldAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapDelete("/api/asap/staff/requests/{id:long}", DeleteAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/bib-lookup", BibLookupAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    private static async Task<IResult> BibLookupAsync(
        BibLookupInput input,
        IStaffPolarisProvider provider,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(input.BibId, out var bibId) || bibId <= 0)
        {
            return Results.BadRequest(new { code = "invalid_bib", message = "Enter a positive Polaris BIB ID." });
        }

        try
        {
            var result = await provider.ValidateBibAsync(bibId, cancellationToken);
            return result.IsValid
                ? Results.Json(new { bibId = bibId.ToString(), result.Title, result.Author })
                : Results.NotFound(new { code = "bib_not_found", message = "The Polaris BIB was not found." });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Json(
                new { code = "bib_validation_unavailable", message = "Catalog validation is temporarily unavailable." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        string? scope,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var result = await views.ListAsync(Current(context), scope, cancellationToken);
        return result is null
            ? Results.BadRequest(new { code = "invalid_scope", message = "The workflow scope is invalid." })
            : Results.Json(result);
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string id,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var result = await views.GetAsync(Current(context), id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    private static Task<IResult> ClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ClaimAsync(Current(context), id, input, false, cancellationToken), views, cancellationToken);

    private static Task<IResult> UnclaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ClaimAsync(Current(context), id, input, true, cancellationToken), views, cancellationToken);

    private static Task<IResult> AssignAsync(
        HttpContext context,
        long id,
        AssignTitleRequestInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.AssignAsync(Current(context), id, input, cancellationToken), views, cancellationToken);

    private static Task<IResult> ActionAsync(
        HttpContext context,
        long id,
        TitleRequestActionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ActionAsync(Current(context), id, input, cancellationToken), views, cancellationToken);

    private static Task<IResult> RetryIdentifierAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.RetryIdentifierAsync(Current(context), id, input, cancellationToken), views, cancellationToken);

    private static async Task<IResult> PickupOptionsAsync(
        HttpContext context,
        long id,
        PickupOptionsInput input,
        StaffPickupService pickup,
        CancellationToken cancellationToken)
    {
        var result = await pickup.GetOptionsAsync(Current(context), id, input, cancellationToken);
        return result.Code == "loaded" ? Results.Json(result.Options) : PickupError(result.Code);
    }

    private static async Task<IResult> PickupPreferenceAsync(
        HttpContext context,
        long id,
        PickupPreferenceInput input,
        StaffPickupService pickup,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var result = await pickup.UpdateAsync(Current(context), id, input, cancellationToken);
        if (result.Code != "updated") return PickupError(result.Code);
        var row = await views.GetAsync(Current(context), id.ToString(), cancellationToken);
        return row is null
            ? Results.NotFound()
            : Results.Json(new { request = row, result.PickupChanged, result.SnapshotChanged });
    }

    private static async Task<IResult> PlaceHoldAsync(
        HttpContext context,
        long id,
        VersionInput input,
        HoldPlacementService holds,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var result = await holds.PlaceAsync(Current(context), id, input, cancellationToken);
        if (result.Code != "updated") return HoldError(result.Code);
        var row = await views.GetAsync(Current(context), id.ToString(), cancellationToken);
        return row is null ? Results.NotFound() : Results.Json(row);
    }

    private static async Task<IResult> ReconcileHoldAsync(
        HttpContext context,
        long id,
        VersionInput input,
        HoldPlacementService holds,
        CancellationToken cancellationToken) =>
        HoldOperationResult(await holds.ReconcileAsync(Current(context), id, input, cancellationToken));

    private static async Task<IResult> ResolveHoldAsync(
        HttpContext context,
        long id,
        ResolveHoldOperationInput input,
        HoldPlacementService holds,
        CancellationToken cancellationToken) =>
        HoldOperationResult(await holds.ResolveAsync(Current(context), id, input, cancellationToken));

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        long id,
        [FromBody] VersionInput input,
        TitleRequestMutationService mutations,
        CancellationToken cancellationToken) =>
        ToErrorOrSuccess(await mutations.DeleteClosedAsync(Current(context), id, input, cancellationToken),
            Results.Json(new { deleted = true }));

    private static async Task<IResult> MutateAndLoadAsync(
        HttpContext context,
        long id,
        Task<TitleRequestMutationResult> mutation,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        var result = await mutation;
        if (result.Code != "updated")
        {
            return ToErrorOrSuccess(result, Results.NoContent());
        }
        var row = await views.GetAsync(Current(context), id.ToString(), cancellationToken);
        return row is null ? Results.NotFound() : Results.Json(row);
    }

    private static IResult ToErrorOrSuccess(TitleRequestMutationResult result, IResult success) => result.Code switch
    {
        "updated" or "deleted" => success,
        "not_found" => Results.NotFound(new { code = result.Code }),
        "staff_scope_forbidden" or "claim_forbidden" or "delete_forbidden" => Results.Json(
            new { code = result.Code, message = "This request is outside your authorized scope." },
            statusCode: StatusCodes.Status403Forbidden),
        "stale_version" or "claim_conflict" or "hold_operation_incomplete" or
            "identifier_locked_by_stage" or "identifier_retry_not_allowed" or "organization_inactive" or
            "hold_history_retained" => Results.Conflict(new
            {
                code = result.Code,
                message = "The request changed or is blocked by its current workflow state. Reload it before continuing."
            }),
        "staff_session_invalid" => Results.Json(new { code = result.Code }, statusCode: StatusCodes.Status401Unauthorized),
        "bib_validation_unavailable" => Results.Json(
            new { code = result.Code, message = "Catalog validation is temporarily unavailable." },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.BadRequest(new { code = result.Code, message = "The request change is invalid." })
    };

    private static IResult PickupError(string code) => code switch
    {
        "not_found" => Results.NotFound(new { code }),
        "pickup_provider_error" => Results.Json(
            new { code, message = "Preferred pickup information is temporarily unavailable." },
            statusCode: StatusCodes.Status502BadGateway),
        "stale_version" or "hold_operation_incomplete" or "pickup_changed_since_load" or "pickup_read_only" =>
            Results.Conflict(new { code, message = "Pickup information changed or is blocked. Reload before continuing." }),
        "staff_scope_forbidden" => Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new { code, message = "The pickup preference is invalid." })
    };

    private static IResult HoldError(string code) => code switch
    {
        "not_found" => Results.NotFound(new { code }),
        "staff_scope_forbidden" => Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden),
        "stale_version" or "hold_operation_incomplete" or "operation_ownership_lost" or
            "hold_identity_ambiguous" or "hold_operator_required" =>
            Results.Conflict(new { code, message = "Hold placement is blocked or requires reconciliation." }),
        "hold_provider_error" => Results.Json(
            new { code, message = "Hold placement could not be confirmed." },
            statusCode: StatusCodes.Status502BadGateway),
        _ => Results.BadRequest(new { code, message = "The hold cannot be placed from the current request state." })
    };

    private static IResult HoldOperationResult(HoldPlacementResult result) => result.Code switch
    {
        "updated" or "resolved" => Results.Json(new { result.Code, operationId = result.OperationId?.ToString() }),
        "not_found" => Results.NotFound(new { result.Code }),
        "hold_resolution_forbidden" => Results.Json(new { result.Code }, statusCode: StatusCodes.Status403Forbidden),
        "hold_provider_error" => Results.Json(new { result.Code }, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Conflict(new { result.Code, operationId = result.OperationId?.ToString() })
    };

    private static CurrentStaff Current(HttpContext context) =>
        StaffAuthenticationEndpoints.RequireCurrentStaff(context);
}
