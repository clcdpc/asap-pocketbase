using Asap.Web.Features.Patron;
using Microsoft.AspNetCore.Mvc;

namespace Asap.Web.Features.Staff;

public static class TitleRequestEndpoints
{
    public sealed record BibLookupInput(string? BibId, string? Mode, string? Query, string? Title, string? Author,
        string? RequestType, string? RequestId, string? Barcode, string? LibraryOrgId);

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
        HttpContext context,
        BibLookupInput input,
        IStaffPolarisProvider provider,
        IPatronProvider patrons,
        PatronConfigurationService configurations,
        TitleRequestViewService views,
        AdditionalCopyService additionalCopies,
        StaffEligibilityService staffEligibility,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var mode = input.Mode?.Trim().ToLowerInvariant();
        var search = string.IsNullOrWhiteSpace(input.BibId) && !string.IsNullOrEmpty(mode);
        if (search && mode is not ("identifier" or "title" or "author" or "title_author"))
        {
            return Results.BadRequest(new { message = "Invalid Polaris search mode." });
        }
        var bibId = 0;
        if (!search && (!int.TryParse(input.BibId, out bibId) || bibId <= 0))
        {
            return Results.BadRequest(new { code = "invalid_bib", message = "Enter a positive Polaris BIB ID." });
        }

        try
        {
            if (search)
            {
                var found = await provider.SearchBibsAsync(mode!, input.Query ?? string.Empty,
                    input.Title ?? string.Empty, input.Author ?? string.Empty, cancellationToken);
                var results = found.Results.Take(10).ToArray();
                return Results.Json(new
                {
                    success = true, mode, query = input.Query, status = results.Length == 0 ? "not_found" : "found",
                    found.TotalMatches, multipleMatches = found.TotalMatches > 1 || results.Length > 1, results, error = string.Empty
                });
            }
            var result = await provider.ValidateBibAsync(bibId, cancellationToken);
            if (!result.IsValid)
            {
                return Results.NotFound(new { code = "bib_not_found", message = "The Polaris BIB was not found." });
            }
            var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
            var scope = await ResolveBibLookupScopeAsync(
                actor,
                input,
                views,
                additionalCopies,
                staffEligibility,
                cancellationToken);
            if (scope.Error is not null)
            {
                return scope.Error;
            }
            var configuration = await configurations.GetAsync(scope.OrganizationId, cancellationToken);
            if (configuration is null)
            {
                return Results.NotFound(new { code = "organization_not_found" });
            }
            var holdingsSummary = await provider.GetBibHoldingsAsync(bibId, scope.OrganizationId, cancellationToken);
            object? patronHoldCheck = null;
            if (!string.IsNullOrWhiteSpace(input.Barcode))
            {
                var barcode = input.Barcode.Trim();
                try
                {
                    var patron = await patrons.RefreshAsync(barcode, cancellationToken);
                    if (!configuration.AllowAnyRegisteredCardLogin &&
                        patron.HomeLibraryOrganizationId != scope.OrganizationId)
                    {
                        return Results.Json(
                            new
                            {
                                code = "patron_library_forbidden",
                                message = "This patron belongs to a different library."
                            },
                            statusCode: StatusCodes.Status403Forbidden);
                    }
                    var holds = await provider.GetPatronHoldsAsync(patron.Barcode, cancellationToken);
                    var hasHold = holds.Any(hold => hold.BibId == bibId &&
                        !new[] { "cancel", "expire", "filled", "deleted" }.Any(terminal =>
                            (hold.StatusDescription ?? string.Empty).Contains(terminal, StringComparison.OrdinalIgnoreCase)));
                    patronHoldCheck = new { ok = true, statusValue = hasHold ? 29 : 0, readOnly = true };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (PolarisOperationalException exception)
                {
                    loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                        .LogWarning(exception,
                            "Patron hold check failed during BIB lookup for patron barcode {PatronBarcode}",
                            barcode);
                }
            }
            return Results.Json(new { bibId = bibId.ToString(), result.Title, result.Author,
                result.Publication, result.Format, result.Identifier, result.Publisher, holdingsSummary, patronHoldCheck });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            return Results.Json(
                new { code = "bib_validation_unavailable", message = "Catalog validation is temporarily unavailable." },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (PatronFlowException exception)
        {
            return Results.Json(exception.Response ?? new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<BibLookupScopeResult> ResolveBibLookupScopeAsync(
        CurrentStaff actor,
        BibLookupInput input,
        TitleRequestViewService views,
        AdditionalCopyService additionalCopies,
        StaffEligibilityService staffEligibility,
        CancellationToken cancellationToken)
    {
        int? requestedOrganizationId = null;
        if (!string.IsNullOrWhiteSpace(input.LibraryOrgId))
        {
            if (!int.TryParse(input.LibraryOrgId, out var parsedOrganizationId) || parsedOrganizationId <= 1)
            {
                return actor.Role == "super_admin"
                    ? BibLookupScopeResult.BadRequest()
                    : BibLookupScopeResult.Forbidden();
            }
            requestedOrganizationId = parsedOrganizationId;
        }

        if (actor.Role != "super_admin" &&
            requestedOrganizationId.HasValue &&
            requestedOrganizationId.Value != actor.OrganizationId)
        {
            return BibLookupScopeResult.Forbidden();
        }

        if (!string.IsNullOrWhiteSpace(input.RequestId))
        {
            var requestId = input.RequestId.Trim();
            var requestType = input.RequestType?.Trim().ToLowerInvariant();
            if (requestType is null or "")
            {
                return BibLookupScopeResult.RequestTypeRequired();
            }
            if (requestType is not ("title_request" or "additional_copy"))
            {
                return BibLookupScopeResult.InvalidRequestType();
            }

            var requestOrganizationId = requestType == "title_request"
                ? (await views.GetAsync(actor, requestId, cancellationToken))?.LibraryOrgId
                : (await additionalCopies.GetAsync(actor, requestId, null, cancellationToken))?.LibraryOrgId;
            if (!requestOrganizationId.HasValue)
            {
                return new BibLookupScopeResult(
                    0,
                    Results.NotFound(new { code = "request_not_found" }));
            }
            requestedOrganizationId = requestOrganizationId.Value;
        }

        var effectiveOrganizationId = actor.Role == "super_admin"
            ? requestedOrganizationId
            : actor.OrganizationId;
        if (!effectiveOrganizationId.HasValue || effectiveOrganizationId.Value <= 1)
        {
            return BibLookupScopeResult.BadRequest();
        }

        var eligibility = await staffEligibility.EvaluateAsync(
            new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId),
            effectiveOrganizationId.Value,
            StaffRoleRequirement.Any,
            requireParticipation: true,
            cancellationToken);
        if (eligibility.Outcome == StaffEligibilityOutcome.InvalidIdentity)
        {
            return new BibLookupScopeResult(
                0,
                Results.Json(new { code = "staff_session_invalid" }, statusCode: StatusCodes.Status401Unauthorized));
        }
        if (eligibility.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return BibLookupScopeResult.Forbidden();
        }
        return new BibLookupScopeResult(effectiveOrganizationId.Value, null);
    }

    private sealed record BibLookupScopeResult(int OrganizationId, IResult? Error)
    {
        public static BibLookupScopeResult BadRequest() => new(
            0,
            Results.BadRequest(new
            {
                code = "library_scope_required",
                message = "Select a servicing library before looking up a BIB."
            }));

        public static BibLookupScopeResult Forbidden() => new(
            0,
            Results.Json(new { code = "staff_scope_forbidden" }, statusCode: StatusCodes.Status403Forbidden));

        public static BibLookupScopeResult RequestTypeRequired() => new(
            0,
            Results.BadRequest(new { code = "request_type_required" }));

        public static BibLookupScopeResult InvalidRequestType() => new(
            0,
            Results.BadRequest(new { code = "invalid_request_type" }));
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
        AdditionalCopyService additionalCopies,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ActionAsync(Current(context), id, input, cancellationToken), views, cancellationToken, additionalCopies);

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
        CancellationToken cancellationToken,
        AdditionalCopyService? additionalCopies = null)
    {
        var result = await mutation;
        if (result.Code != "updated")
        {
            return ToErrorOrSuccess(result, Results.NoContent());
        }
        var row = await views.GetAsync(Current(context), id.ToString(), cancellationToken);
        if (row is null)
        {
            return Results.NotFound();
        }
        if (result.AdditionalCopyRequestId is not long copyId || additionalCopies is null)
        {
            return result.ReminderRequested
                ? Results.Json(new
                {
                    request = row,
                    purchaseReminderEmail = new { requested = true, queued = result.ReminderQueued, reason = result.ReminderSkippedReason }
                })
                : Results.Json(row);
        }
        var copy = await additionalCopies.GetAsync(Current(context), copyId.ToString(), null, cancellationToken);
        return Results.Json(new
        {
            request = row,
            additionalCopyRequest = copy,
            additionalCopyRequestId = copyId.ToString(),
            purchaseReminderEmail = new { requested = result.ReminderRequested, queued = result.ReminderQueued, reason = result.ReminderSkippedReason }
        });
    }

    private static IResult ToErrorOrSuccess(TitleRequestMutationResult result, IResult success) => result.Code switch
    {
        "updated" or "deleted" => success,
        "not_found" => Results.NotFound(new { code = result.Code }),
        "staff_scope_forbidden" or "claim_forbidden" or "delete_forbidden" => Results.Json(
            new { code = result.Code, message = "This request is outside your authorized scope." },
            statusCode: StatusCodes.Status403Forbidden),
        "stale_version" or "claim_conflict" or "duplicate_open_request" or "hold_operation_incomplete" or
            "claim_rule_changed" or
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
