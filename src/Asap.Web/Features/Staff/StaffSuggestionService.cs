using System.Text.RegularExpressions;
using Asap.Web.Features.Patron;
using Asap.Web.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Asap.Web.Features.Staff;

public sealed class StaffSuggestionService(
    IDbContextFactory<AsapDbContext> contextFactory,
    StaffEligibilityService eligibility,
    PatronConfigurationService configurationService,
    IPatronProvider patronProvider,
    IStaffPatronLookupProvider patronLookupProvider,
    IStaffCatalogSearchProvider catalogSearchProvider,
    PatronSuggestionService suggestions,
    TimeProvider timeProvider)
{
    private static readonly Regex BarcodeLike = new(
        "(?=.*\\d)^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<StaffPatronLookupResult> LookupAsync(
        CurrentStaff actor,
        StaffPatronLookupInput input,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        var query = Clean(input.Query);
        var explicitBarcode = Clean(input.Barcode);
        if (explicitBarcode is null && query is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "patron_query_required",
                "Enter a patron barcode or name.");
        }

        PatronSnapshot? direct = null;
        if (explicitBarcode is not null)
        {
            direct = await RefreshAsync(explicitBarcode, cancellationToken);
        }
        else if (BarcodeLike.IsMatch(query!))
        {
            try
            {
                direct = await RefreshAsync(query!, cancellationToken);
            }
            catch (StaffSuggestionException exception) when (exception.Code == "patron_not_found")
            {
                // A general barcode-shaped search may fall back to name search only after a clear miss.
            }
        }

        if (direct is not null)
        {
            await EnsurePatronScopeAsync(direct, scope.Configuration, cancellationToken);
            return await VerifiedResultAsync(scope, direct, cancellationToken);
        }

        IReadOnlyList<StaffPatronSearchCandidate> candidates;
        try
        {
            candidates = await patronLookupProvider.SearchAsync(
                query!,
                scope.Configuration.AllowAnyRegisteredCardLogin ? null : scope.OrganizationId,
                cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            throw ProviderFailure(exception);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "polaris_unavailable",
                "Current patron information could not be loaded from Polaris. Please try again.");
        }

        var matches = new List<StaffPatronMatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Take(10))
        {
            var barcode = Clean(candidate.Barcode);
            if (barcode is null || !seen.Add(barcode))
            {
                continue;
            }

            PatronSnapshot patron;
            try
            {
                patron = await RefreshAsync(barcode, cancellationToken);
            }
            catch (StaffSuggestionException exception) when (exception.Code == "patron_not_found")
            {
                continue;
            }

            if (!await IsPatronInScopeAsync(patron, scope.Configuration, cancellationToken))
            {
                continue;
            }

            matches.Add(ToMatch(patron));
        }

        if (matches.Count == 0)
        {
            return new StaffPatronLookupResult(
                "no_match",
                scope.OrganizationId,
                scope.Configuration.OrganizationName,
                !scope.Configuration.AllowAnyRegisteredCardLogin,
                null,
                []);
        }

        if (matches.Count > 1)
        {
            return new StaffPatronLookupResult(
                "multiple_matches",
                scope.OrganizationId,
                scope.Configuration.OrganizationName,
                !scope.Configuration.AllowAnyRegisteredCardLogin,
                null,
                matches);
        }

        var selected = await RefreshAsync(matches[0].Barcode, cancellationToken);
        return await VerifiedResultAsync(scope, selected, cancellationToken);
    }

    public async Task<PatronSuggestionResult> CreateAsync(
        CurrentStaff actor,
        StaffSuggestionInput input,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        var barcode = Clean(input.Barcode);
        if (barcode is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "patron_barcode_required",
                "Verify a patron before submitting the suggestion.");
        }

        var patron = await RefreshAsync(barcode, cancellationToken);
        await EnsurePatronScopeAsync(patron, scope.Configuration, cancellationToken);

        // Re-read the actor and effective library immediately before the creation boundary so
        // a role, identity, participation, or library-policy change cannot authorize this write
        // solely from the initial lookup context.
        scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        await EnsurePatronScopeAsync(patron, scope.Configuration, cancellationToken);

        return await suggestions.CreateForStaffAsync(
            actor,
            scope.OrganizationId,
            input with { Barcode = barcode },
            cancellationToken);
    }

    public async Task<IReadOnlyList<StaffCatalogSearchCandidate>> SearchCatalogAsync(
        CurrentStaff actor,
        StaffCatalogSearchInput input,
        CancellationToken cancellationToken)
    {
        await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        var query = Clean(input.Query);
        if (query is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "catalog_query_required",
                "Enter a title, author, or identifier to search the catalog.");
        }

        if (query.Length > 200)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "catalog_query_too_long",
                "Catalog searches must be 200 characters or fewer.");
        }

        var mode = Clean(input.Mode)?.ToLowerInvariant() switch
        {
            "author" => "author",
            "identifier" => "identifier",
            _ => "title"
        };
        try
        {
            return await catalogSearchProvider.SearchAsync(query, mode, cancellationToken);
        }
        catch (PolarisOperationalException)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "polaris_catalog_unavailable",
                "The Polaris catalog could not be searched. Please try again.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "polaris_catalog_unavailable",
                "The Polaris catalog could not be searched. Please try again.");
        }
    }

    private async Task<(int OrganizationId, EffectivePatronConfiguration Configuration)> ResolveScopeAsync(
        CurrentStaff actor,
        int? requestedOrganizationId,
        CancellationToken cancellationToken)
    {
        if (actor.Role == "super_admin" && requestedOrganizationId is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "servicing_library_required",
                "Choose a servicing library before looking up a patron.");
        }

        var organizationId = actor.Role == "super_admin"
            ? requestedOrganizationId
            : actor.OrganizationId;
        if (organizationId is null or <= 1)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "servicing_library_required",
                "Choose a participating servicing library.");
        }

        var result = await eligibility.EvaluateAsync(
            new StaffIdentityEvidence(actor.Id, actor.AuthenticationEmail, actor.EntraTenantId),
            organizationId,
            StaffRoleRequirement.Any,
            requireParticipation: true,
            cancellationToken);
        if (result.Outcome == StaffEligibilityOutcome.InvalidIdentity)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status401Unauthorized,
                result.Code,
                "The staff session is no longer valid.");
        }

        if (result.Outcome != StaffEligibilityOutcome.Allowed)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                result.Code,
                "This servicing library is outside your authorized scope.");
        }

        var configuration = await configurationService.GetAsync(organizationId.Value, cancellationToken);
        if (configuration is null || !configuration.IsActive)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                "organization_inactive",
                "The selected servicing library is not participating.");
        }

        return (organizationId.Value, configuration);
    }

    private async Task<PatronSnapshot> RefreshAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await patronProvider.RefreshAsync(barcode, cancellationToken);
        }
        catch (PolarisOperationalException exception)
        {
            if (exception.Code is "polaris_patron_not_found" or "polaris_patron_invalid_barcode")
            {
                throw new StaffSuggestionException(
                    StatusCodes.Status404NotFound,
                    "patron_not_found",
                    "That patron could not be found in Polaris.");
            }

            throw ProviderFailure(exception);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "polaris_unavailable",
                "Current patron information could not be loaded from Polaris. Please try again.");
        }
    }

    private async Task EnsurePatronScopeAsync(
        PatronSnapshot patron,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!await IsPatronInScopeAsync(patron, configuration, cancellationToken))
        {
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                "patron_library_forbidden",
                "This patron is not eligible for the selected servicing library.");
        }
    }

    private async Task<bool> IsPatronInScopeAsync(
        PatronSnapshot patron,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (patron.PatronOrganizationId <= 1 || patron.HomeLibraryOrganizationId <= 1)
        {
            return false;
        }

        if (!configuration.AllowAnyRegisteredCardLogin &&
            patron.HomeLibraryOrganizationId != configuration.OrganizationId)
        {
            return false;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizationIds = new[] { patron.PatronOrganizationId, patron.HomeLibraryOrganizationId };
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => organizationIds.Contains(item.Id))
            .Select(item => new { item.Id, item.IsActive })
            .ToListAsync(cancellationToken);
        var byId = organizations.ToDictionary(item => item.Id);
        return byId.Count == organizationIds.Distinct().Count() &&
               byId[patron.HomeLibraryOrganizationId].IsActive;
    }

    private async Task<StaffPatronLookupResult> VerifiedResultAsync(
        (int OrganizationId, EffectivePatronConfiguration Configuration) scope,
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        var branches = await GetPickupBranchesAsync(patron, cancellationToken);
        var current = branches.SingleOrDefault(item => item.Id == patron.PreferredPickupBranchId);
        var warning = current is null ? "Choose an eligible preferred pickup location." : null;
        var context = new StaffPatronContext(
            ToMatch(patron),
            patron.Email,
            patron.PreferredPickupBranchId,
            current?.Label,
            branches,
            timeProvider.GetUtcNow(),
            warning,
            branches.Count == 0,
            scope.OrganizationId,
            scope.Configuration.OrganizationName,
            !scope.Configuration.AllowAnyRegisteredCardLogin);
        return new StaffPatronLookupResult(
            "verified",
            scope.OrganizationId,
            scope.Configuration.OrganizationName,
            !scope.Configuration.AllowAnyRegisteredCardLogin,
            context,
            []);
    }

    private async Task<IReadOnlyList<PickupBranch>> GetPickupBranchesAsync(
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        try
        {
            return await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
        }
        catch (PolarisOperationalException)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "pickup_branches_unavailable",
                "Eligible pickup locations could not be loaded from Polaris.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "pickup_branches_unavailable",
                "Eligible pickup locations could not be loaded from Polaris.");
        }
    }

    private static StaffPatronMatch ToMatch(PatronSnapshot patron) => new(
        patron.Barcode,
        string.Join(' ', new[] { patron.NameFirst, patron.NameLast }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
        patron.PatronOrganizationId,
        patron.HomeLibraryOrganizationId,
        patron.HomeLibraryOrganizationName);

    private static StaffSuggestionException ProviderFailure(PolarisOperationalException exception) => new(
        StatusCodes.Status502BadGateway,
        "polaris_unavailable",
        "Current patron information could not be loaded from Polaris. Please try again.");

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
