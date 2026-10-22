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
    IStaffPolarisProvider polaris,
    PatronSuggestionService suggestions,
    TimeProvider timeProvider)
{
    internal const string PatronCodeEligibilityMessage =
        "This patron cannot submit suggestions because their Polaris patron code is not eligible for the selected servicing library. Staff cannot override this restriction in this workflow.";

    private static readonly Regex BarcodeLike = new(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<StaffSuggestionConfiguration> GetConfigurationAsync(
        CurrentStaff actor,
        int? requestedOrganizationId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(actor, requestedOrganizationId, cancellationToken);
        return new StaffSuggestionConfiguration(
            scope.OrganizationId,
            scope.Configuration.OrganizationName,
            PatronEndpoints.BuildConfigurationPayload(scope.Configuration));
    }

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
            direct = await RefreshAsync(explicitBarcode, scope.OrganizationId, cancellationToken);
        }
        else if (BarcodeLike.IsMatch(query!))
        {
            try
            {
                direct = await RefreshAsync(query!, scope.OrganizationId, cancellationToken);
            }
            catch (StaffSuggestionException exception) when (exception.Code == "patron_not_found")
            {
                // A barcode-shaped name search falls back only after a definitive miss.
            }
        }

        if (direct is not null)
        {
            // Patron/provider work can outlive the initial scope read. Re-resolve the
            // target configuration before exposing eligibility or form context.
            scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
            await EnsurePatronScopeAsync(direct, scope.Configuration, cancellationToken);
            return await VerifiedResultAsync(actor, input.LibraryOrgId, scope, direct, cancellationToken);
        }

        IReadOnlyList<PatronSnapshot> candidates;
        try
        {
            candidates = await polaris.SearchPatronsAsync(query!, scope.OrganizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw ProviderFailure(exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var refreshedPatrons = new List<PatronSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Take(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var barcode = Clean(candidate.Barcode);
            if (barcode is null || !seen.Add(barcode))
            {
                continue;
            }

            PatronSnapshot patron;
            try
            {
                // Search rows are candidates only. Refresh the selected identity before it
                // crosses into the browser-facing result.
                patron = await RefreshAsync(barcode, scope.OrganizationId, cancellationToken);
            }
            catch (StaffSuggestionException exception) when (exception.Code == "patron_not_found")
            {
                continue;
            }

            refreshedPatrons.Add(patron);
        }

        // Search/refresh calls are provider boundaries. Resolve the effective target
        // configuration again before filtering candidates or returning form context.
        scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        var matches = new List<StaffPatronMatch>();
        var codeRejected = false;
        var libraryRejected = false;
        foreach (var patron in refreshedPatrons)
        {
            var rejection = await PatronRejectionCodeAsync(patron, scope.Configuration, cancellationToken);
            if (rejection is not null)
            {
                codeRejected |= rejection == "patron_ineligible";
                libraryRejected |= rejection == "patron_library_forbidden";
                continue;
            }

            matches.Add(ToMatch(patron));
        }

        if (matches.Count == 0)
        {
            var code = codeRejected
                ? libraryRejected ? "patron_selection_ineligible" : "patron_ineligible"
                : libraryRejected ? "patron_library_forbidden" : null;
            return new StaffPatronLookupResult(
                code is not null ? "ineligible" : "no_match",
                scope.OrganizationId,
                scope.Configuration.OrganizationName,
                !scope.Configuration.AllowAnyRegisteredCardLogin,
                null,
                [],
                code switch
                {
                    "patron_ineligible" => PatronCodeEligibilityMessage,
                    "patron_selection_ineligible" => "No matching patron can be selected. Matches were excluded by the selected servicing library's registration or participation rules, or by Polaris patron-code eligibility. Staff cannot override these restrictions in this workflow.",
                    "patron_library_forbidden" => "The matching patron is not eligible for the selected servicing library.",
                    _ => "No patron matched that search."
                }) { Code = code };
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

        var selected = await RefreshAsync(matches[0].Barcode, scope.OrganizationId, cancellationToken);
        scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        await EnsurePatronScopeAsync(selected, scope.Configuration, cancellationToken);
        return await VerifiedResultAsync(actor, input.LibraryOrgId, scope, selected, cancellationToken);
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

        // The lookup response is never authority for creation. The persistence service
        // refreshes again and performs the locked actor/configuration gate immediately before
        // any pickup mutation, then repeats its authoritative checks for the insert.
        var patron = await RefreshAsync(barcode, scope.OrganizationId, cancellationToken);
        scope = await ResolveScopeAsync(actor, input.LibraryOrgId, cancellationToken);
        await EnsurePatronScopeAsync(patron, scope.Configuration, cancellationToken);

        try
        {
            return await suggestions.CreateForStaffAsync(
                actor,
                scope.OrganizationId,
                input with { Barcode = barcode },
                cancellationToken);
        }
        catch (PatronFlowException exception)
        {
            var response = exception.Response;
            if (response is PatronSuggestionPickupChangedFailure partialFailure &&
                exception.InnerException is PatronFlowException { Response: { } cause } &&
                cause.GetType().GetProperty("code")?.GetValue(cause) is "patron_ineligible")
            {
                response = partialFailure with { ReasonCode = "patron_ineligible" };
            }

            throw new StaffSuggestionException(
                exception.StatusCode,
                response is PatronSuggestionPickupChangedFailure partial
                    ? partial.Code
                    : response is { } details &&
                    details.GetType().GetProperty("code")?.GetValue(details) is string code
                        ? code
                        : "staff_suggestion_invalid",
                exception.Message,
                response,
                exception);
        }
    }

    private async Task<(int OrganizationId, EffectivePatronConfiguration Configuration)> ResolveScopeAsync(
        CurrentStaff actor,
        int? requestedOrganizationId,
        CancellationToken cancellationToken)
    {
        if (actor.Role == StaffRole.SuperAdmin && requestedOrganizationId is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status400BadRequest,
                "servicing_library_required",
                "Choose a servicing library before continuing.");
        }

        if (actor.Role != StaffRole.SuperAdmin && requestedOrganizationId.HasValue &&
            requestedOrganizationId.Value != actor.OrganizationId)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                "staff_scope_forbidden",
                "This servicing library is outside your authorized scope.");
        }

        var organizationId = actor.Role == StaffRole.SuperAdmin
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
            requireParticipation: false,
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

        if (result.Staff is null || !result.Staff.OrganizationIsActive)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                "staff_scope_forbidden",
                "This staff account is no longer authorized for suggestion work.");
        }

        var configuration = await configurationService.GetAsync(organizationId.Value, cancellationToken);
        if (configuration is null)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status404NotFound,
                "organization_not_found",
                "The selected servicing library could not be found.");
        }

        if (!configuration.IsActive)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status409Conflict,
                "organization_inactive",
                "The selected servicing library is not participating.");
        }

        return (organizationId.Value, configuration);
    }

    private async Task<PatronSnapshot> RefreshAsync(
        string barcode,
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var patron = await patronProvider.RefreshAsync(barcode, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return patron;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (exception is PolarisOperationalException { Code: "polaris_patron_not_found" or "polaris_patron_invalid_barcode" })
            {
                throw new StaffSuggestionException(
                    StatusCodes.Status404NotFound,
                    "patron_not_found",
                    "That patron could not be found in Polaris.",
                    new { status = "not_found", code = "patron_not_found" },
                    exception);
            }

            throw ProviderFailure(exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private async Task EnsurePatronScopeAsync(
        PatronSnapshot patron,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var code = await PatronRejectionCodeAsync(patron, configuration, cancellationToken);
        if (code is not null)
        {
            var message = code == "patron_ineligible"
                ? PatronCodeEligibilityMessage
                : "This patron is not eligible for the selected servicing library.";
            throw new StaffSuggestionException(
                StatusCodes.Status403Forbidden,
                code,
                message,
                new
                {
                    code,
                    status = "ineligible",
                    message
                });
        }
    }

    private async Task<string?> PatronRejectionCodeAsync(
        PatronSnapshot patron,
        EffectivePatronConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (patron.PatronOrganizationId <= 1 || patron.HomeLibraryOrganizationId <= 1)
        {
            return "patron_library_forbidden";
        }

        if (!configuration.AllowAnyRegisteredCardLogin &&
            patron.HomeLibraryOrganizationId != configuration.OrganizationId)
        {
            return "patron_library_forbidden";
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organizationIds = new[] { patron.PatronOrganizationId, patron.HomeLibraryOrganizationId };
        var organizations = await context.Organizations.AsNoTracking()
            .Where(item => organizationIds.Contains(item.Id))
            .Select(item => new { item.Id, item.OrganizationCodeId, item.IsActive })
            .ToListAsync(cancellationToken);
        var byId = organizations.ToDictionary(item => item.Id);
        if (byId.Count != organizationIds.Distinct().Count() ||
            !OrganizationAuthority.IsLibrary(patron.HomeLibraryOrganizationId,
                byId[patron.HomeLibraryOrganizationId].OrganizationCodeId) ||
            !byId[patron.HomeLibraryOrganizationId].IsActive)
        {
            return "patron_library_forbidden";
        }

        // Only explain code policy after library admission; excluded candidates must
        // not reveal their patron-code eligibility through a name search.
        return configuration.PatronCodeEligibilityEnabled &&
               configuration.AllowedPatronCodeIds.Count > 0 &&
               patron.PatronCodeId.HasValue &&
               !configuration.AllowedPatronCodeIds.Contains(patron.PatronCodeId.Value)
            ? "patron_ineligible"
            : null;
    }

    private async Task<StaffPatronLookupResult> VerifiedResultAsync(
        CurrentStaff actor,
        int? requestedOrganizationId,
        (int OrganizationId, EffectivePatronConfiguration Configuration) scope,
        PatronSnapshot patron,
        CancellationToken cancellationToken)
    {
        var branches = await GetPickupBranchesAsync(patron, scope.OrganizationId, cancellationToken);
        await using var researchContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var metadata = await researchContext.SystemSettings.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .Select(item => new
            {
                item.LeapBibUrlPattern,
                item.LeapPatronUrlPattern,
                PatronOrganizationName = researchContext.Organizations.Where(organization => organization.Id == patron.PatronOrganizationId)
                    .Select(organization => organization.DisplayName).FirstOrDefault()
            })
            .SingleAsync(cancellationToken);
        scope = await ResolveScopeAsync(actor, requestedOrganizationId, cancellationToken);
        await EnsurePatronScopeAsync(patron, scope.Configuration, cancellationToken);
        var current = patron.EffectivePickupPreferenceState == PatronPickupPreferenceState.Current
            ? branches.SingleOrDefault(item => item.Id == patron.PreferredPickupBranchId) : null;
        var warning = branches.Count == 0 ? "No eligible pickup locations are available; a suggestion cannot be created." :
            current is null ? "The current Polaris preference is missing or ineligible. Choose an eligible preferred pickup location." : null;
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
            !scope.Configuration.AllowAnyRegisteredCardLogin)
        {
            Research = new StaffResearchContext(metadata.LeapBibUrlPattern, metadata.LeapPatronUrlPattern,
                patron.PatronId is > 0 ? patron.PatronId : null),
            PatronOrganizationName = metadata.PatronOrganizationName,
            CurrentPreferredPickupState = patron.EffectivePickupPreferenceState switch
            {
                PatronPickupPreferenceState.Current => "current",
                PatronPickupPreferenceState.ExplicitInvalid => "invalid",
                _ => "absent"
            }
        };
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
        int organizationId, CancellationToken cancellationToken)
    {
        try
        {
            var branches = await patronProvider.GetPickupBranchesAsync(patron, organizationId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return branches;
        }
        catch (Exception exception) when (exception is PolarisOperationalException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new StaffSuggestionException(
                StatusCodes.Status502BadGateway,
                "pickup_branches_unavailable",
                "Eligible pickup locations could not be loaded from Polaris.",
                innerException: exception);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static StaffPatronMatch ToMatch(PatronSnapshot patron) => new(
        patron.Barcode,
        string.Join(' ', new[] { patron.NameFirst, patron.NameLast }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim(),
        patron.PatronOrganizationId,
        patron.HomeLibraryOrganizationId,
        patron.HomeLibraryOrganizationName);

    private static StaffSuggestionException ProviderFailure(Exception exception) => new(
        StatusCodes.Status502BadGateway,
        "polaris_unavailable",
        "Current patron information could not be loaded from Polaris.",
        innerException: exception);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
