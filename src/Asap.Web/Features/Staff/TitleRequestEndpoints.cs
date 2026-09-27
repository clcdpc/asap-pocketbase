using System.Globalization;
using System.Text.RegularExpressions;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public static class TitleRequestEndpoints
{
    private static readonly HashSet<string> SupportedResearchTokens =
        ["title", "identifier", "bibid", "patron-id", "patronId"];

    public sealed record BibLookupInput(string? RequestId, int? LibraryOrgId, string? BibId,
        string? Mode, string? Query, string? Title, string? Author);

    public static IEndpointRouteBuilder MapTitleRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/asap/staff/title-requests").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{id}", GetAsync);
        endpoints.MapGet("/api/asap/staff/research-configuration", ResearchConfigurationAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/bib-lookup", BibLookupAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
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
        return endpoints;
    }

    private static async Task<IResult> ResearchConfigurationAsync(
        HttpContext context,
        long? requestId,
        int? libraryOrgId,
        TitleRequestViewService views,
        StaffEligibilityService eligibility,
        PatronConfigurationService configurations,
        IPatronProvider patrons,
        IDbContextFactory<AsapDbContext> contextFactory,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveResearchScopeAsync(Current(context), requestId, libraryOrgId,
            views, eligibility, cancellationToken);
        if (scope.Error is not null)
        {
            return scope.Error;
        }
        var configuration = await configurations.GetAsync(scope.OrganizationId, cancellationToken);
        if (configuration is null)
        {
            return Results.NotFound(new { code = "organization_not_found" });
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var system = await db.SystemSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        int? patronId = null;
        if (scope.Request is { Barcode.Length: > 0 } request &&
            HasUsablePatronResearchUrl(system.LeapPatronUrlPattern))
        {
            try
            {
                var resolvedPatronId = await patrons.GetPatronIdAsync(request.Barcode, cancellationToken);
                if (resolvedPatronId is > 0)
                {
                    patronId = resolvedPatronId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                    .LogWarning(exception, "Patron ID was unavailable for request research links");
            }
        }
        return Results.Json(new
        {
            system.LeapBibUrlPattern,
            system.LeapPatronUrlPattern,
            patronId,
            externalSearchProviders = configuration.ExternalSearchProviders
                .Where(item => item.IsEnabled)
                .Select(item => new { item.Key, item.Label, item.UrlTemplate, item.SortOrder })
        });
    }

    private static async Task<IResult> BibLookupAsync(
        HttpContext context,
        BibLookupInput input,
        TitleRequestViewService views,
        StaffEligibilityService eligibility,
        IStaffPolarisProvider polaris,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryParseRequestId(input.RequestId, out var requestId))
        {
            return Results.BadRequest(new
            {
                code = "invalid_request_id",
                message = "Request ID must be a positive Int64 decimal value."
            });
        }
        var scope = await ResolveResearchScopeAsync(Current(context), requestId, input.LibraryOrgId,
            views, eligibility, cancellationToken);
        if (scope.Error is not null)
        {
            return scope.Error;
        }
        var mode = input.Mode?.Trim().ToLowerInvariant();
        var bibText = input.BibId?.Trim();
        if (mode is not null && mode != "bib" &&
            mode is not ("identifier" or "title" or "author" or "title_author"))
        {
            return Results.BadRequest(new { code = "invalid_search_mode", message = "Choose a supported Polaris search mode." });
        }
        var exact = mode == "bib" || !string.IsNullOrEmpty(bibText);
        if (exact && (!int.TryParse(bibText, out var parsedBibId) || parsedBibId <= 0))
        {
            return Results.BadRequest(new { code = "invalid_bib", message = "Enter a positive Polaris BIB ID." });
        }
        var query = input.Query?.Trim() ?? string.Empty;
        var title = input.Title?.Trim() ?? string.Empty;
        var author = input.Author?.Trim() ?? string.Empty;
        if (!exact && (mode is null || mode == "title_author" && (title.Length == 0 || author.Length == 0) ||
                       mode != "title_author" && query.Length == 0))
        {
            return Results.BadRequest(new { code = "search_query_required", message = "Enter the catalog search text." });
        }
        if (query.Length > 250 || title.Length > 250 || author.Length > 250)
        {
            return Results.BadRequest(new { code = "search_query_too_long", message = "Catalog search text is too long." });
        }
        try
        {
            if (!exact)
            {
                var search = await polaris.SearchBibsAsync(mode!, query, title, author, cancellationToken);
                return Results.Json(new { status = search.Results.Count == 0 ? "not_found" : "found",
                    search.TotalMatches, results = search.Results.Take(10) });
            }
            var bibId = int.Parse(bibText!);
            var bib = await polaris.ValidateBibAsync(bibId, cancellationToken);
            if (!bib.IsValid)
            {
                return Results.NotFound(new { code = "bib_not_found", message = "The Polaris BIB was not found." });
            }
            StaffBibHoldingsSummary? holdings = null;
            var holdingsUnavailable = false;
            try
            {
                holdings = await polaris.GetBibHoldingsAsync(bibId, scope.OrganizationId, cancellationToken);
            }
            catch (PolarisOperationalException)
            {
                holdingsUnavailable = true;
            }
            bool? patronHasHold = null;
            if (scope.Request is { Barcode.Length: > 0 } request)
            {
                try
                {
                    var holds = await polaris.GetPatronHoldsAsync(request.Barcode, cancellationToken);
                    patronHasHold = holds.Any(hold => hold.BibId == bibId &&
                        !HoldPlacementService.IsTerminal(hold.StatusId));
                }
                catch (PolarisOperationalException exception)
                {
                    loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                        .LogWarning(exception, "Patron hold context was unavailable during BIB lookup");
                }
            }
            return Results.Json(new { bibId = bibId.ToString(), bib.Title, bib.Author,
                bib.Publication, bib.Format, bib.Identifier, bib.Publisher,
                holdingsSummary = holdings, holdingsUnavailable, patronHasHold });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PolarisOperationalException)
        {
            return Results.Json(new { code = "bib_validation_unavailable",
                message = "Catalog lookup is temporarily unavailable." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<ResearchScope> ResolveResearchScopeAsync(
        CurrentStaff actor,
        long? requestId,
        int? libraryOrgId,
        TitleRequestViewService views,
        StaffEligibilityService eligibility,
        CancellationToken cancellationToken)
    {
        TitleRequestDto? request = null;
        if (requestId.HasValue)
        {
            request = await views.GetAsync(actor, requestId.Value.ToString(), cancellationToken);
            if (request is null)
            {
                return new ResearchScope(0, null, Results.NotFound(new { code = "request_not_found" }));
            }
            if (libraryOrgId.HasValue && libraryOrgId.Value != request.LibraryOrgId)
            {
                return new ResearchScope(0, null, Results.BadRequest(new { code = "library_scope_mismatch" }));
            }
            libraryOrgId = request.LibraryOrgId;
        }
        if (libraryOrgId is null or <= 1)
        {
            return new ResearchScope(0, null, Results.BadRequest(new { code = "library_scope_required" }));
        }
        var result = await eligibility.EvaluateAsync(
            new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId),
            libraryOrgId.Value, StaffRoleRequirement.Any, requireParticipation: true, cancellationToken);
        if (result.Outcome == StaffEligibilityOutcome.InvalidIdentity)
        {
            return new ResearchScope(0, null, Results.Json(new { code = "staff_session_invalid" },
                statusCode: StatusCodes.Status401Unauthorized));
        }
        if (result.Outcome != StaffEligibilityOutcome.Allowed)
        {
            return new ResearchScope(0, null, Results.Json(new { code = "staff_scope_forbidden" },
                statusCode: StatusCodes.Status403Forbidden));
        }
        return new ResearchScope(libraryOrgId.Value, request, null);
    }

    internal static bool TryParseRequestId(string? value, out long? requestId)
    {
        requestId = null;
        if (value is null)
        {
            return true;
        }
        if (value.Length == 0 || value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return false;
        }
        requestId = parsed;
        return true;
    }

    private sealed record ResearchScope(int OrganizationId, TitleRequestDto? Request, IResult? Error);

    private static bool HasUsablePatronResearchUrl(string? pattern)
    {
        var value = pattern?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !value.Contains("{{patron-id}}", StringComparison.Ordinal) &&
            !value.Contains("{{patronId}}", StringComparison.Ordinal))
        {
            return false;
        }

        var hasUnsupportedToken = false;
        var candidate = Regex.Replace(value, "\\{\\{([^{}]+)\\}\\}", match =>
        {
            if (!SupportedResearchTokens.Contains(match.Groups[1].Value))
            {
                hasUnsupportedToken = true;
                return string.Empty;
            }
            return "1";
        });
        if (hasUnsupportedToken || candidate.Contains("{{", StringComparison.Ordinal) ||
            candidate.Contains("}}", StringComparison.Ordinal) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var url))
        {
            return false;
        }

        return (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) &&
               string.IsNullOrEmpty(url.UserInfo);
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
