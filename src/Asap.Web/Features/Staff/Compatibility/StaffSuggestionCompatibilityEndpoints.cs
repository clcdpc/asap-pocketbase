using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;

namespace Asap.Web.Features.Staff.Compatibility;

public sealed record StaffPatronLookupInput(string? Query, string? Barcode, string? LibraryOrgId);

public sealed record StaffSuggestionInput(
    string? Barcode,
    string? Title,
    string? Author,
    string? Identifier,
    string? Format,
    string? Publication,
    string? ExactPublicationDate,
    string? PreferredPickupBranchId,
    bool? Autohold,
    string? LibraryOrgId);

public static class StaffSuggestionCompatibilityEndpoints
{
    public static IEndpointRouteBuilder MapStaffSuggestionCompatibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/asap/staff/patron-lookup", LookupAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        endpoints.MapPost("/api/asap/staff/suggestions", CreateAsync)
            .RequireAuthorization()
            .AddEndpointFilter<StaffAntiforgeryFilter>();
        return endpoints;
    }

    private static async Task<IResult> LookupAsync(
        HttpContext context,
        StaffPatronLookupInput input,
        IPatronProvider patrons,
        IStaffPolarisProvider searches,
        PatronConfigurationService configurations,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        var query = Clean(input.Query) ?? Clean(input.Barcode);
        if (query is null)
        {
            return Results.BadRequest(new { code = "patron_barcode_required", message = "Enter a patron barcode or name." });
        }

        try
        {
            PatronSnapshot? patron = null;
            if (System.Text.RegularExpressions.Regex.IsMatch(query, "^[A-Za-z0-9._:-]+$"))
            {
                try
                {
                    patron = await patrons.RefreshAsync(query, cancellationToken);
                }
                catch (PolarisOperationalException exception) when (
                    exception.Code == "polaris_patron_not_found" && Clean(input.Query) is not null)
                {
                    // A barcode-like query (including a single name) falls back only on not-found.
                }
            }
            var candidates = patron is null
                ? await searches.SearchPatronsAsync(query, cancellationToken)
                : [patron];
            var usable = new List<PatronSnapshot>();
            ScopeResult? selectedScope = null;
            foreach (var candidate in candidates.DistinctBy(item => item.Barcode).Take(10))
            {
                var requestedOrganizationId = actor.Role == "super_admin" && string.IsNullOrWhiteSpace(input.LibraryOrgId)
                    ? candidate.HomeLibraryOrganizationId.ToString()
                    : input.LibraryOrgId;
                var scope = await ResolveScopeAsync(actor, requestedOrganizationId, configurations, cancellationToken);
                if (scope.Error is not null)
                {
                    return scope.Error;
                }
                try
                {
                    PatronSuggestionService.EnforceStaffPatronEligibility(scope.Configuration!, candidate);
                }
                catch (PatronFlowException) when (patron is null)
                {
                    continue;
                }
                selectedScope = scope;
                usable.Add(candidate);
            }

            if (usable.Count == 0)
            {
                return Results.NotFound(new { status = "not_found", results = Array.Empty<object>(),
                    message = "No patron found. Try barcode, name, or first name then last name." });
            }
            if (usable.Count > 1)
            {
                return Results.Json(new
                {
                    status = "multiple", totalMatches = usable.Count,
                    patronSearchLimitedToLibrary = !selectedScope!.Configuration!.AllowAnyRegisteredCardLogin,
                    results = usable.Select(item => new
                    {
                        status = "candidate", item.Barcode, item.NameFirst, item.NameLast, item.Email,
                        name = string.Join(' ', new[] { item.NameFirst, item.NameLast }.Where(value => !string.IsNullOrWhiteSpace(value))),
                        patronOrgId = item.PatronOrganizationId, libraryOrgId = item.HomeLibraryOrganizationId,
                        libraryOrgName = item.HomeLibraryOrganizationName
                    })
                });
            }
            patron = usable[0];
            var branches = await patrons.GetPickupBranchesAsync(patron, cancellationToken);
            var selected = branches.SingleOrDefault(item => item.Id == patron.PreferredPickupBranchId);
            return Results.Json(new
            {
                status = "selected",
                patronSearchLimitedToLibrary = !selectedScope!.Configuration!.AllowAnyRegisteredCardLogin,
                patron.PatronId,
                polarisPatronId = patron.PatronId,
                patron.Barcode,
                patron.Email,
                patron.NameFirst,
                patron.NameLast,
                patronName = string.Join(' ', new[] { patron.NameFirst, patron.NameLast }.Where(value => !string.IsNullOrWhiteSpace(value))),
                libraryOrgId = patron.HomeLibraryOrganizationId,
                libraryOrgName = patron.HomeLibraryOrganizationName,
                patron.PreferredPickupBranchId,
                preferredPickupBranchName = selected?.Label,
                pickupBranches = branches.Select(item => new { item.Id, item.Label }),
                selectedPickupBranchId = selected?.Id,
                pickupBranchWarning = selected is null ? "Choose a preferred pickup location before submitting." : string.Empty
            });
        }
        catch (PatronFlowException exception)
        {
            return Results.Json(exception.Response ?? new { message = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (PolarisOperationalException exception) when (exception.Code == "polaris_patron_not_found")
        {
            return Results.NotFound(new { status = "not_found", results = Array.Empty<object>(), message = "No patron found." });
        }
        catch (PolarisOperationalException)
        {
            return Results.Json(
                new { code = "patron_lookup_unavailable", message = "Current patron information could not be loaded from Polaris." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        StaffSuggestionInput input,
        PatronConfigurationService configurations,
        PatronSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        var actor = StaffAuthenticationEndpoints.RequireCurrentStaff(context);
        var scope = await ResolveScopeAsync(actor, input.LibraryOrgId, configurations, cancellationToken);
        if (scope.Error is not null)
        {
            return scope.Error;
        }

        var barcode = Clean(input.Barcode);
        if (barcode is null || !int.TryParse(input.PreferredPickupBranchId, out var pickupBranchId) || pickupBranchId <= 0)
        {
            return Results.BadRequest(new { code = "invalid_staff_suggestion", message = "A verified patron and pickup location are required." });
        }

        var publication = Clean(input.Publication) ?? Clean(input.ExactPublicationDate);
        var patronSession = new PatronSessionContext(
            0,
            barcode,
            null,
            scope.OrganizationId,
            scope.OrganizationId,
            DateTime.UtcNow.AddMinutes(5));
        try
        {
            var created = await suggestions.CreateAsync(
                patronSession,
                new PatronSuggestionInput(
                    input.Format,
                    input.Title,
                    input.Author,
                    input.Identifier,
                    publication,
                    pickupBranchId,
                    input.Autohold,
                    null),
                cancellationToken,
                staffActor: actor);
            return Results.Json(created, statusCode: StatusCodes.Status201Created);
        }
        catch (PatronFlowException exception)
        {
            return Results.Json(
                exception.Response ?? new { message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }

    private static async Task<ScopeResult> ResolveScopeAsync(
        CurrentStaff actor,
        string? requestedOrganizationId,
        PatronConfigurationService configurations,
        CancellationToken cancellationToken)
    {
        int organizationId;
        if (actor.Role == "super_admin")
        {
            if (!int.TryParse(requestedOrganizationId, out organizationId) || organizationId <= 1)
            {
                return new ScopeResult(0, null, Results.BadRequest(new
                {
                    code = "library_scope_required",
                    message = "Select a servicing library before looking up a patron."
                }));
            }
        }
        else
        {
            organizationId = actor.OrganizationId;
            if (!string.IsNullOrWhiteSpace(requestedOrganizationId) &&
                (!int.TryParse(requestedOrganizationId, out var requested) || requested != organizationId))
            {
                return new ScopeResult(0, null, Results.Json(
                    new { code = "staff_scope_forbidden" },
                    statusCode: StatusCodes.Status403Forbidden));
            }
        }

        var configuration = await configurations.GetAsync(organizationId, cancellationToken);
        if (configuration is null)
        {
            return new ScopeResult(0, null, Results.NotFound(new { code = "organization_not_found" }));
        }
        if (!configuration.IsActive)
        {
            return new ScopeResult(0, null, Results.Conflict(new { code = "organization_inactive" }));
        }
        return new ScopeResult(organizationId, configuration, null);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ScopeResult(
        int OrganizationId,
        EffectivePatronConfiguration? Configuration,
        IResult? Error);
}
