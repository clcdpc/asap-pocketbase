using System.Globalization;
using System.Text.Json.Serialization;
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

    public sealed record BibLookupInput(string? RequestId, int? LibraryOrgId,
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int? BibId,
        string? Mode, string? Query, string? Title, string? Author);

    public static IEndpointRouteBuilder MapTitleRequestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/asap/staff/title-requests").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/{id}/rejection-templates", RejectionTemplatesAsync);
        endpoints.MapGet("/api/asap/staff/research-configuration", ResearchConfigurationAsync)
            .RequireAuthorization();
        endpoints.MapGet("/api/asap/staff/suggestion-configuration", SuggestionConfigurationAsync)
            .RequireAuthorization();
        endpoints.MapPost("/api/asap/staff/bib-lookup", BibLookupAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/patron-lookup", LookupPatronAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/suggestions", CreateSuggestionAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("", CreateSuggestionAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/claim", ClaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/unclaim", UnclaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/clear-claim", ClearClaimAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/assign", AssignAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/action", ActionAsync).AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/retry-identifier-check", RetryIdentifierAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/pickup-options", PickupOptionsAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        group.MapPost("/{id:long}/pickup-preference", PickupPreferenceAsync)
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/pickup-operations/{id:guid}/reconcile", ReconcilePickupAsync)
            .RequireAuthorization()
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

    private static async Task<IResult> SuggestionConfigurationAsync(
        HttpContext context,
        int? libraryOrgId,
        StaffSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await suggestions.GetConfigurationAsync(
                Current(context),
                libraryOrgId,
                cancellationToken);
            return Results.Json(new
            {
                libraryOrgId = result.LibraryOrgId,
                libraryOrgName = result.LibraryOrgName,
                configuration = result.EffectiveConfiguration
            });
        }
        catch (StaffSuggestionException exception)
        {
            return StaffSuggestionError(exception);
        }
    }

    private static async Task<IResult> LookupPatronAsync(
        HttpContext context,
        StaffPatronLookupInput input,
        StaffSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Json(await suggestions.LookupAsync(
                Current(context),
                input,
                cancellationToken));
        }
        catch (StaffSuggestionException exception)
        {
            return StaffSuggestionError(exception);
        }
    }

    private static async Task<IResult> CreateSuggestionAsync(
        HttpContext context,
        StaffSuggestionInput input,
        StaffSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await suggestions.CreateAsync(Current(context), input, cancellationToken);
            var id = created.Id.ToString(CultureInfo.InvariantCulture);
            return Results.Json(new
            {
                id,
                created.SuccessTitle,
                created.SuccessMessage,
                notificationStatus = created.NotificationStatus,
                libraryOrgId = created.LibraryOrganizationId ?? input.LibraryOrgId,
                requestUrl = $"/staff/?request={Uri.EscapeDataString(id)}"
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (StaffSuggestionException exception)
        {
            return StaffSuggestionError(exception);
        }
    }

    private static IResult StaffSuggestionError(StaffSuggestionException exception)
    {
        if (exception.Response is PatronSuggestionDuplicateConflict conflict)
        {
            return Results.Json(
                new
                {
                    code = "duplicate",
                    conflict.Message,
                    conflictTitle = conflict.ConflictTitle,
                    conflictMessage = conflict.ConflictMessage,
                    duplicate = DuplicatePayload(conflict.Duplicate)
                },
                statusCode: exception.StatusCode);
        }

        if (exception.Response is PatronSuggestionPickupChangedFailure partial)
        {
            return Results.Json(
                new
                {
                    code = partial.Code,
                    partial.Message,
                    partial.PickupPreferenceChanged,
                    conflictTitle = partial.DuplicateConflict?.ConflictTitle,
                    conflictMessage = partial.DuplicateConflict?.ConflictMessage,
                    duplicateMessage = partial.DuplicateConflict?.Message,
                    duplicate = partial.DuplicateConflict is { } partialConflict
                        ? DuplicatePayload(partialConflict.Duplicate)
                        : null
                },
                statusCode: exception.StatusCode);
        }

        return Results.Json(
            exception.Response ?? new { code = exception.Code, message = exception.Message },
            statusCode: exception.StatusCode);
    }

    private static object DuplicatePayload(PatronSuggestionDuplicate duplicate)
    {
        var id = duplicate.Id.ToString(CultureInfo.InvariantCulture);
        return new
        {
            id,
            duplicate.Created,
            duplicate.Status,
            closeReason = duplicate.CloseReason,
            duplicate.Title,
            duplicate.Author,
            format = duplicate.Format,
            matchType = duplicate.MatchType,
            requestUrl = $"/staff/?request={Uri.EscapeDataString(id)}"
        };
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
                var resolvedPatronId = await patrons.GetPatronIdAsync(request.Barcode, scope.OrganizationId, cancellationToken);
                if (resolvedPatronId is > 0)
                {
                    patronId = resolvedPatronId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PolarisOperationalException exception)
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

        if (mode is not null && mode != "bib" &&
            mode is not ("identifier" or "title" or "author" or "title_author"))
        {
            return Results.BadRequest(new { code = "invalid_search_mode", message = "Choose a supported Polaris search mode." });
        }
        var exact = mode == "bib" || input.BibId.HasValue;
        if (exact && input.BibId is not > 0)
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
                var search = await polaris.SearchBibsAsync(mode!, query, title, author, scope.OrganizationId, cancellationToken);
                return Results.Json(new { status = search.Results.Count == 0 ? "not_found" : "found",
                    search.TotalMatches, results = search.Results.Take(10) });
            }
            var bibId = input.BibId!.Value;
            var bib = await polaris.ValidateBibAsync(bibId, scope.OrganizationId, cancellationToken);
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
                    var holds = await polaris.GetPatronHoldsAsync(request.Barcode, scope.OrganizationId, cancellationToken);
                    patronHasHold = holds.Any(hold => hold.BibId == bibId &&
                        !HoldPlacementService.IsTerminal(hold.StatusId));
                }
                catch (PolarisOperationalException exception)
                {
                    loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                        .LogWarning(exception, "Patron hold context was unavailable during BIB lookup");
                }
            }
            return Results.Json(new { bibId, bib.Title, bib.Author,
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
        if (!LibraryScope.TryParse(scope, LibraryScope.All, out var parsedScope))
        {
            return Results.BadRequest(new { code = "invalid_scope" });
        }
        var result = await views.ListAsync(Current(context), parsedScope, cancellationToken);
        return result is null
            ? Results.BadRequest(new { code = "invalid_scope", message = "The workflow scope is invalid." })
            : Results.Json(result);
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string id,
        string? scope,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        if (!LibraryScope.TryParse(scope, LibraryScope.All, out var parsedScope))
        {
            return Results.BadRequest(new { code = "invalid_scope" });
        }
        var result = await views.GetAsync(Current(context), id, cancellationToken, parsedScope);
        return result is null ? Results.NotFound() : Results.Json(result);
    }

    private static async Task<IResult> RejectionTemplatesAsync(
        HttpContext context,
        string id,
        TitleRequestViewService views,
        CancellationToken cancellationToken)
    {
        try
        {
            var choices = await views.GetRejectionTemplatesAsync(Current(context), id, cancellationToken);
            return choices is null ? Results.NotFound(new { code = "not_found" }) :
                Results.Json(new { items = choices, defaultTemplateId = (string?)null });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { code = "template_dependency_unavailable",
                message = "Rejection templates are temporarily unavailable." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> ClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ClaimAsync(Current(context), id, input, false, cancellationToken),
            views, loggerFactory, cancellationToken);

    private static Task<IResult> UnclaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ClaimAsync(Current(context), id, input, true, cancellationToken),
            views, loggerFactory, cancellationToken);

    private static Task<IResult> ClearClaimAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.ClearClaimAsync(Current(context), id, input, cancellationToken),
            views, loggerFactory, cancellationToken);

    private static Task<IResult> AssignAsync(
        HttpContext context,
        long id,
        AssignTitleRequestInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.AssignAsync(Current(context), id, input, cancellationToken),
            views, loggerFactory, cancellationToken);

    private static async Task<IResult> ActionAsync(
        HttpContext context,
        long id,
        TitleRequestActionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        TitleRequestMutationResult result;
        try
        {
            result = await mutations.ActionAsync(Current(context), id, input.ToCommand(), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { code = "request_outcome_unconfirmed",
                message = "The request outcome could not be confirmed. Reload before trying again." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (result.Code != "updated")
        {
            return ToErrorOrSuccess(result, Results.NoContent());
        }
        TitleRequestDto? row = null;
        try
        {
            row = await views.GetAsync(Current(context), id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        // The mutation committed; a detail-refresh failure must still return the accepted outcome.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                .LogError(exception, "Request detail refresh failed after action {RequestId} committed", id);
        }
        if (row is not null)
        {
            return Results.Json(row with
            {
                Committed = true,
                FinalStatus = result.FinalStatus,
                NotificationStatus = result.NotificationStatus,
                NotificationReason = result.NotificationReason,
                PatronNotificationStatus = result.PatronNotificationStatus,
                PatronNotificationReason = result.PatronNotificationReason
            });
        }
        return Results.Json(new
        {
            committed = true,
            request = (TitleRequestDto?)null,
            finalStatus = result.FinalStatus,
            notificationStatus = result.NotificationStatus,
            notificationReason = result.NotificationReason,
            patronNotificationStatus = result.PatronNotificationStatus,
            patronNotificationReason = result.PatronNotificationReason,
            refreshUnavailable = row is null
        });
    }

    private static Task<IResult> RetryIdentifierAsync(
        HttpContext context,
        long id,
        VersionInput input,
        TitleRequestMutationService mutations,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        MutateAndLoadAsync(context, id, mutations.RetryIdentifierAsync(Current(context), id, input, cancellationToken),
            views, loggerFactory, cancellationToken);

    private static async Task<IResult> PickupOptionsAsync(
        HttpContext context,
        long id,
        StaffPickupService pickup,
        CancellationToken cancellationToken)
    {
        var result = await pickup.GetOptionsAsync(Current(context), id, cancellationToken);
        return result.Code == "loaded" ? Results.Json(result.Options) : PickupError(result.Code);
    }

    private static async Task<IResult> PickupPreferenceAsync(
        HttpContext context,
        long id,
        PickupPreferenceInput input,
        StaffPickupService pickup,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var result = await pickup.UpdateAsync(Current(context), id, input, cancellationToken);
        if (result.Code != "updated")
        {
            if (result.OperationId is not null)
            {
                return Results.Conflict(new
                {
                    result.Code,
                    pickupPreferenceChanged = result.PickupChanged,
                    outcomeUnconfirmed = result.Code is "pickup_outcome_unconfirmed" or "pickup_reconciliation_required",
                    result.OperationId,
                    result.LocalFailureCode,
                    message = result.PickupChanged
                        ? "Polaris pickup changed, but this request was not updated. Reload the request and live pickup preference before retrying. The operation is durably recorded."
                        : "The pickup outcome requires reconciliation. Reload the live preference; another write will not be attempted while this operation is uncertain."
                });
            }
            return PickupError(result.Code);
        }
        var row = await TryLoadCommittedAsync(context, id, views, loggerFactory, cancellationToken);
        return Results.Json(new
        {
            committed = true,
            request = row,
            finalStatus = row?.Status,
            result.PickupChanged,
            result.SnapshotChanged,
            refreshUnavailable = row is null
        });
    }

    private static async Task<IResult> ReconcilePickupAsync(
        HttpContext context, Guid id, PickupReconciliationInput input, StaffPickupService pickup,
        TitleRequestViewService views, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var result = await pickup.ReconcileAsync(Current(context), id, input, cancellationToken);
        if (result.Code != "updated")
        {
            return PickupError(result.Code);
        }
        var row = result.RequestId is { } requestId
            ? await TryLoadCommittedAsync(context, requestId, views, loggerFactory, cancellationToken)
            : null;
        return Results.Json(new { committed = true, request = row, result.FinalStatus,
            result.OperationId, result.SnapshotChanged, refreshUnavailable = row is null,
            pickupPreferenceChanged = false, confirmedByRead = true });
    }

    private static async Task<IResult> PlaceHoldAsync(
        HttpContext context,
        long id,
        VersionInput input,
        HoldPlacementService holds,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        HoldPlacementResult result;
        try
        {
            result = await holds.PlaceAsync(Current(context), id, input, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HoldOutcomeUnconfirmed();
        }
        if (result.Code != "updated")
        {
            return HoldError(result);
        }
        var row = await TryLoadCommittedAsync(context, id, views, loggerFactory, cancellationToken);
        return row is null
            ? Results.Json(new { committed = true, request = (TitleRequestDto?)null,
                finalStatus = result.FinalStatus, notificationStatus = result.NotificationStatus,
                notificationReason = result.NotificationReason, refreshUnavailable = true })
            : Results.Json(row with { Committed = true, FinalStatus = result.FinalStatus ?? row.Status,
                NotificationStatus = result.NotificationStatus, NotificationReason = result.NotificationReason });
    }

    private static async Task<IResult> ReconcileHoldAsync(
        HttpContext context,
        long id,
        VersionInput input,
        HoldPlacementService holds,
        CancellationToken cancellationToken)
    {
        try
        {
            return HoldOperationResult(await holds.ReconcileAsync(Current(context), id, input, cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HoldOutcomeUnconfirmed();
        }
    }

    private static async Task<IResult> ResolveHoldAsync(
        HttpContext context,
        long id,
        ResolveHoldOperationInput input,
        HoldPlacementService holds,
        CancellationToken cancellationToken)
    {
        try
        {
            return HoldOperationResult(await holds.ResolveAsync(Current(context), id, input, cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HoldOutcomeUnconfirmed();
        }
    }

    private static IResult HoldOutcomeUnconfirmed() => Results.Json(new
    {
        code = "hold_outcome_unconfirmed",
        message = "The hold outcome could not be confirmed. Review the operation before retrying."
    }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> DeleteAsync(
        HttpContext context,
        long id,
        [FromBody] VersionInput input,
        TitleRequestMutationService mutations,
        CancellationToken cancellationToken)
    {
        TitleRequestMutationResult result;
        try
        {
            result = await mutations.DeleteClosedAsync(Current(context), id, input, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new
            {
                code = "request_outcome_unconfirmed",
                message = "The deletion outcome could not be confirmed. Reload Closed work before retrying."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return ToErrorOrSuccess(result, Results.Json(new { deleted = true }));
    }

    private static async Task<IResult> MutateAndLoadAsync(
        HttpContext context,
        long id,
        Task<TitleRequestMutationResult> mutation,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        TitleRequestMutationResult result;
        try
        {
            result = await mutation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { code = "request_outcome_unconfirmed",
                message = "The request outcome could not be confirmed. Reload before trying again." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (result.Code != "updated")
        {
            return ToErrorOrSuccess(result, Results.NoContent());
        }
        var row = await TryLoadCommittedAsync(context, id, views, loggerFactory, cancellationToken);
        return row is null
            ? Results.Json(new { committed = true, request = (TitleRequestDto?)null,
                finalStatus = result.FinalStatus, notificationStatus = result.NotificationStatus,
                notificationReason = result.NotificationReason, refreshUnavailable = true })
            : Results.Json(row with { Committed = true, FinalStatus = result.FinalStatus ?? row.Status,
                NotificationStatus = result.NotificationStatus, NotificationReason = result.NotificationReason });
    }

    private static async Task<TitleRequestDto?> TryLoadCommittedAsync(
        HttpContext context,
        long id,
        TitleRequestViewService views,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await views.GetAsync(Current(context), id.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        // This read follows a committed mutation; log its failure and return explicit committed metadata.
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            loggerFactory.CreateLogger("Asap.Web.Features.Staff.TitleRequestEndpoints")
                .LogError(exception, "Request detail refresh failed after mutation {RequestId} committed", id);
            return null;
        }
    }

    private static IResult ToErrorOrSuccess(TitleRequestMutationResult result, IResult success) => result.Code switch
    {
        "updated" or "deleted" => success,
        "not_found" => Results.NotFound(new { code = result.Code }),
        "staff_scope_forbidden" or "claim_forbidden" or "delete_forbidden" => Results.Json(
            new { code = result.Code, message = "This request is outside your authorized scope." },
            statusCode: StatusCodes.Status403Forbidden),
        "duplicate_open_request" => Results.Conflict(new
        {
            code = result.Code,
            message = "This patron already has an open request or hold for this BIB. The request was not changed.",
            duplicate = result.Duplicate is { } duplicate ? new
            {
                id = duplicate.Id.ToString(CultureInfo.InvariantCulture),
                title = duplicate.Title,
                status = duplicate.Status,
                bibid = duplicate.BibId,
                matchType = duplicate.MatchType
            } : null
        }),
        "stale_version" or "actor_changed_since_preview" or "claim_conflict" or "claim_rule_changed" or "hold_operation_incomplete" or
            "identifier_locked_by_stage" or "identifier_retry_not_allowed" or "organization_inactive" or
            "hold_history_retained" or "pickup_reconciliation_required" => Results.Conflict(new
            {
                code = result.Code,
                message = "The request changed or is blocked by its current workflow state. Reload it before continuing."
            }),
        "invalid_rejection_template" => Results.Conflict(new
        {
            code = result.Code,
            message = "The selected rejection template is no longer available for this request. Choose a current template."
        }),
        "invalid_custom_fields" => Results.BadRequest(new
        {
            code = result.Code,
            message = "The request's custom fields do not match the selected format. Review the current fields before saving."
        }),
        "notification_dependency_unavailable" => Results.Json(new
        {
            code = result.Code,
            message = "Notification configuration is temporarily unavailable. The request was not changed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        "staff_session_invalid" => Results.Json(new { code = result.Code }, statusCode: StatusCodes.Status401Unauthorized),
        "bib_validation_unavailable" => Results.Json(
            new { code = result.Code, message = "Catalog validation is temporarily unavailable." },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        "bib_unverified" => Results.Conflict(new
        {
            code = result.Code,
            message = "Verify the current BIB in Polaris before moving this request to Pending hold."
        }),
        "bib_not_found" => Results.BadRequest(new
        {
            code = result.Code,
            message = "The BIB was not found in Polaris. The request was not changed."
        }),
        _ => Results.BadRequest(new { code = result.Code, message = "The request change is invalid." })
    };

    private static IResult PickupError(string code) => code switch
    {
        "not_found" => Results.NotFound(new { code }),
        "pickup_provider_error" => Results.Json(
            new { code, message = "Preferred pickup information is temporarily unavailable." },
            statusCode: StatusCodes.Status502BadGateway),
        "stale_version" or "hold_operation_incomplete" or "pickup_reconciliation_required" or "pickup_changed_since_load" or "pickup_read_only" =>
            Results.Conflict(new { code, message = "Pickup information changed or is blocked. Reload before continuing." }),
        "staff_scope_forbidden" => Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.BadRequest(new { code, message = "The pickup preference is invalid." })
    };

    private static IResult HoldError(HoldPlacementResult result) => result.Code switch
    {
        "not_found" => Results.NotFound(new { result.Code, result.ProviderOutcomeRecorded }),
        "staff_scope_forbidden" => Results.Json(new { result.Code, result.ProviderOutcomeRecorded,
            operationId = result.OperationId?.ToString() }, statusCode: StatusCodes.Status403Forbidden),
        "duplicate_open_request" => Results.Conflict(new
        {
            result.Code,
            result.ProviderOutcomeRecorded,
            message = "This patron already has an active request or hold for this BIB. No new hold was attempted.",
            duplicate = result.Duplicate is { } duplicate ? new
            {
                id = duplicate.Id.ToString(CultureInfo.InvariantCulture),
                title = duplicate.Title,
                status = duplicate.Status,
                bibid = duplicate.BibId,
                matchType = duplicate.MatchType
            } : null
        }),
        "stale_version" or "hold_operation_incomplete" or "pickup_reconciliation_required" or "operation_ownership_lost" or
            "hold_identity_ambiguous" or "hold_operator_required" =>
            Results.Conflict(new { result.Code, result.ProviderOutcomeRecorded,
                message = "Hold placement is blocked or requires reconciliation." }),
        "bib_unverified" => Results.Conflict(new
        {
            result.Code,
            message = "Verify the current BIB in Polaris before placing a hold. No hold was attempted."
        }),
        "hold_provider_error" => Results.Json(
            new { result.Code, message = "Hold placement could not be confirmed." },
            statusCode: StatusCodes.Status502BadGateway),
        _ => Results.BadRequest(new { result.Code, result.ProviderOutcomeRecorded,
            message = "The hold cannot be placed from the current request state." })
    };

    private static IResult HoldOperationResult(HoldPlacementResult result) => result.Code switch
    {
        "updated" or "resolved" => Results.Json(new { result.Code, operationId = result.OperationId?.ToString(),
            committed = true, finalStatus = result.FinalStatus, notificationStatus = result.NotificationStatus,
            notificationReason = result.NotificationReason }),
        "not_found" => Results.NotFound(new { result.Code, result.ProviderOutcomeRecorded }),
        "hold_resolution_forbidden" => Results.Json(new { result.Code, result.ProviderOutcomeRecorded,
            operationId = result.OperationId?.ToString() }, statusCode: StatusCodes.Status403Forbidden),
        "hold_resolution_dependency_unavailable" => Results.Json(new
        {
            result.Code,
            message = "Hold resolution could not start because a dependency is unavailable. The operation was not changed."
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        "hold_provider_error" => Results.Json(new
        {
            result.Code,
            message = "The hold provider outcome could not be confirmed. Review the operation before retrying."
        }, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Conflict(new { result.Code, result.ProviderOutcomeRecorded,
            operationId = result.OperationId?.ToString() })
    };

    private static CurrentStaff Current(HttpContext context) =>
        StaffAuthenticationEndpoints.RequireCurrentStaff(context);
}
