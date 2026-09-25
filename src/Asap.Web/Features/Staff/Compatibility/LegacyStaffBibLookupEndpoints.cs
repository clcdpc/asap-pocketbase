using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Web.Features.Staff.Compatibility;

// The existing BIB lookup response is a narrow legacy staff presentation contract.
// Its provider calls and servicing-library checks remain owned by their feature services.
public static class LegacyStaffBibLookupEndpoints
{
    public static IEndpointRouteBuilder MapLegacyStaffBibLookupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/asap/staff/bib-lookup", BibLookupAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    public sealed record BibLookupInput(string? BibId, string? Mode, string? Query, string? Title, string? Author,
        string? RequestType, string? RequestId, string? Barcode, string? LibraryOrgId);


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
                    PatronSuggestionService.EnforceStaffPatronLibrary(configuration, patron);
                    var holds = await provider.GetPatronHoldsAsync(patron.Barcode, cancellationToken);
                    var hasHold = holds.Any(hold => StaffHoldStatus.IsActiveSameBib(hold, bibId));
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
                statusCode: StatusCodes.Status503ServiceUnavailable);
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

}
