using System.Net;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Patron;

public sealed record PatronLoginInput(
    string? Username,
    string? Barcode,
    string? Password,
    string? Pin,
    int? LibraryOrgId);

public static class PatronEndpoints
{
    public static RouteGroupBuilder MapPatronEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/asap/config", GetConfigurationAsync);
        endpoints.MapGet("/api/asap/config/logo", GetLogoAsync);

        var group = endpoints.MapGroup("/api/asap/patron");
        group.MapPost("/login", LoginAsync).RequireRateLimiting("patron-login");
        group.MapGet("/session", RestoreSessionAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/suggestions", CreateSuggestionAsync);
        return group;
    }

    private static async Task<IResult> GetConfigurationAsync(
        int? libraryOrgId,
        PatronConfigurationService configurations,
        CancellationToken cancellationToken)
    {
        var organizationId = libraryOrgId ?? 1;
        var configuration = await configurations.GetAsync(organizationId, cancellationToken);
        return configuration is null
            ? Results.NotFound(new { message = "Library configuration was not found." })
            : Results.Json(BuildConfigurationPayload(configuration));
    }

    private static async Task<IResult> GetLogoAsync(
        int? libraryOrgId,
        PatronConfigurationService configurations,
        CancellationToken cancellationToken)
    {
        var organizationId = libraryOrgId ?? 1;
        var branding = await configurations.GetBrandingAsync(organizationId, cancellationToken);
        return branding?.LogoData is { Length: > 0 } data &&
               !string.IsNullOrWhiteSpace(branding.LogoContentType)
            ? Results.File(data, branding.LogoContentType)
            : Results.NotFound();
    }

    private static async Task<IResult> LoginAsync(
        PatronLoginInput input,
        IPatronProvider patronProvider,
        PatronConfigurationService configurations,
        PatronSessionService sessions,
        CancellationToken cancellationToken)
    {
        var barcode = Clean(input.Username) ?? Clean(input.Barcode);
        var pin = input.Password ?? input.Pin;
        if (barcode is null || string.IsNullOrEmpty(pin))
        {
            return Results.BadRequest(new { message = "Barcode and PIN are required" });
        }

        PatronSnapshot patron;
        try
        {
            patron = await patronProvider.AuthenticateAsync(barcode, pin, cancellationToken);
        }
        catch (PatronAuthenticationException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (PolarisOperationalException)
        {
            return Results.Json(
                new { message = "The library suggestion system is currently misconfigured. Please contact staff." },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var home = await configurations.GetAsync(patron.HomeLibraryOrganizationId, cancellationToken);
        if (home is null)
        {
            return Results.Json(
                new { message = "Your library could not be determined from Polaris." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        EffectivePatronConfiguration? experience = null;
        if (input.LibraryOrgId.HasValue)
        {
            experience = await configurations.GetAsync(input.LibraryOrgId.Value, cancellationToken);
            if (experience is null || !experience.IsActive)
            {
                return Results.Json(
                    new { message = ParticipationMessage(experience, experience?.OrganizationName) },
                    statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var crossLibrary = experience?.AllowAnyRegisteredCardLogin == true;
        var effective = crossLibrary ? experience! : home;
        if (!effective.IsActive)
        {
            return Results.Json(
                new { message = ParticipationMessage(effective, effective.OrganizationName) },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (effective.PatronCodeEligibilityEnabled &&
            effective.AllowedPatronCodeIds.Count > 0 &&
            !string.IsNullOrWhiteSpace(patron.PatronCodeId) &&
            !effective.AllowedPatronCodeIds.Contains(patron.PatronCodeId))
        {
            return Results.Json(
                new { message = effective.PatronCodeEligibilityMessage },
                statusCode: StatusCodes.Status403Forbidden);
        }

        IReadOnlyList<PickupBranch> branches;
        string warning = string.Empty;
        try
        {
            branches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            branches = [];
            warning = "Pickup locations are temporarily unavailable. Please try again.";
        }

        var selected = branches.Any(item => item.Id == patron.PreferredPickupBranchId)
            ? patron.PreferredPickupBranchId
            : null;
        if (branches.Count > 0 && !selected.HasValue)
        {
            warning = "Choose a preferred pickup location before submitting.";
        }

        var issued = await sessions.IssueAsync(
            patron.Barcode,
            patron.HomeLibraryOrganizationId,
            input.LibraryOrgId,
            effective.OrganizationId,
            cancellationToken);
        if (issued is null)
        {
            return Results.Json(
                new { message = ParticipationMessage(effective, effective.OrganizationName) },
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Json(BuildSessionPayload(
            issued.Token,
            patron,
            branches,
            selected,
            warning,
            input.LibraryOrgId,
            experience?.OrganizationName,
            effective,
            crossLibrary));
    }

    private static async Task<IResult> RestoreSessionAsync(
        HttpRequest request,
        PatronSessionService sessions,
        PatronConfigurationService configurations,
        IPatronProvider patronProvider,
        CancellationToken cancellationToken)
    {
        var session = await AuthenticateAsync(request, sessions, cancellationToken);
        if (session is null)
        {
            return Unauthorized();
        }

        var configuration = await configurations.GetAsync(
            session.EffectiveOrganizationId,
            cancellationToken);
        if (configuration is null)
        {
            return Unauthorized();
        }

        try
        {
            var patron = await patronProvider.RefreshAsync(session.Barcode, cancellationToken);
            var branches = await patronProvider.GetPickupBranchesAsync(patron, cancellationToken);
            var selected = branches.Any(item => item.Id == patron.PreferredPickupBranchId)
                ? patron.PreferredPickupBranchId
                : null;
            return Results.Json(BuildSessionPayload(
                token: null,
                patron,
                branches,
                selected,
                selected.HasValue ? string.Empty : "Choose a preferred pickup location before submitting.",
                session.ExperienceOrganizationId,
                experienceName: null,
                configuration,
                session.ExperienceOrganizationId.HasValue &&
                session.ExperienceOrganizationId != session.HomeOrganizationId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(
                new { message = "Current patron information could not be loaded. Please log in again." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> LogoutAsync(
        HttpRequest request,
        PatronSessionService sessions,
        CancellationToken cancellationToken)
    {
        var token = BearerToken(request);
        if (token is not null)
        {
            await sessions.RevokeAsync(token, cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CreateSuggestionAsync(
        HttpRequest request,
        PatronSuggestionInput input,
        PatronSessionService sessions,
        PatronSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        var session = await AuthenticateAsync(request, sessions, cancellationToken);
        if (session is null)
        {
            return Unauthorized();
        }

        try
        {
            var result = await suggestions.CreateAsync(session, input, cancellationToken);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }
        catch (PatronFlowException exception)
        {
            return Results.Json(
                exception.Response ?? new { message = exception.Message },
                statusCode: exception.StatusCode);
        }
    }

    private static object BuildSessionPayload(
        string? token,
        PatronSnapshot patron,
        IReadOnlyList<PickupBranch> branches,
        int? selected,
        string warning,
        int? experienceId,
        string? experienceName,
        EffectivePatronConfiguration effective,
        bool crossLibrary) =>
        new
        {
            token,
            barcode = patron.Barcode,
            email = patron.Email ?? string.Empty,
            record = new
            {
                email = patron.Email ?? string.Empty,
                libraryOrgId = effective.OrganizationId
            },
            preferredPickupBranchId = patron.PreferredPickupBranchId,
            pickupBranches = branches.Select(item => new { item.Id, item.Label }),
            currentPreferredPickupBranchId = patron.PreferredPickupBranchId,
            selectedPickupBranchId = selected,
            pickupBranchWarning = warning,
            experienceLibraryOrgId = experienceId,
            experienceLibraryOrgName = experienceName,
            patronHomeLibraryOrgId = patron.HomeLibraryOrganizationId,
            patronHomeLibraryOrgName = patron.HomeLibraryOrganizationName,
            effectiveLibraryOrgId = effective.OrganizationId,
            effectiveLibraryOrgName = effective.OrganizationName,
            crossLibraryLogin = crossLibrary,
            ui_text = BuildConfigurationPayload(effective)
        };

    internal static object BuildConfigurationPayload(EffectivePatronConfiguration configuration)
    {
        var external = configuration.ExternalSearchProviders.ToDictionary(item => item.Key, StringComparer.Ordinal);
        EffectiveExternalSearchProvider Provider(string key) => external.TryGetValue(key, out var value)
            ? value
            : new EffectiveExternalSearchProvider(key, false, string.Empty, string.Empty, int.MaxValue);
        var external1 = Provider("external_search_1");
        var external2 = Provider("external_search_2");
        var external3 = Provider("external_search_3");
        var external4 = Provider("external_search_4");
        var formatRules = configuration.Formats.ToDictionary(
            item => item.Code,
            item => (object)new
            {
                item.MessageBehavior,
                item.Message,
                fields = new
                {
                    title = item.Title,
                    author = item.Author,
                    identifier = item.Identifier,
                    publication = item.Publication
                },
                customFields = item.CustomFields
            },
            StringComparer.Ordinal);
        return new
        {
            configuration.PageTitle,
            configuration.BarcodeLabel,
            configuration.PinLabel,
            configuration.LoginPrompt,
            configuration.LoginNote,
            configuration.SuggestionFormNote,
            configuration.NoEmailMessage,
            configuration.SuccessTitle,
            configuration.SuccessMessage,
            configuration.AlreadySubmittedMessage,
            duplicateStatusLabels = configuration.DuplicateStatusLabels,
            configuration.EbookMessage,
            configuration.EaudiobookMessage,
            publicationOptions = configuration.PublicationOptions,
            formatRules,
            formatLabels = configuration.Formats.ToDictionary(item => item.Code, item => item.Label),
            availableFormats = configuration.Formats.Where(item => item.IsEnabled).Select(item => item.Code),
            additionalFieldDefinitions = configuration.CustomFields.Select(item => new
            {
                id = item.Key,
                key = item.Key,
                item.Type,
                item.Label,
                item.HelpText,
                enabled = true,
                options = item.Options.Select(option => new
                {
                    id = option.Key,
                    option.Label,
                    enabled = true
                })
            }),
            commonAuthorsEnabled = configuration.CommonCreatorsEnabled,
            commonAuthorsList = string.Join('\n', configuration.CommonCreators),
            commonAuthorsLabel = configuration.CommonCreatorsLabel,
            commonAuthorsHelp = configuration.CommonCreatorsHelp,
            commonAuthorsMessage = configuration.CommonCreatorsMessage,
            externalSearch1Enabled = external1.IsEnabled,
            externalSearch1Label = external1.Label,
            externalSearch1UrlTemplate = external1.UrlTemplate,
            externalSearch2Enabled = external2.IsEnabled,
            externalSearch2Label = external2.Label,
            externalSearch2UrlTemplate = external2.UrlTemplate,
            externalSearch3Enabled = external3.IsEnabled,
            externalSearch3Label = external3.Label,
            externalSearch3UrlTemplate = external3.UrlTemplate,
            externalSearch4Enabled = external4.IsEnabled,
            externalSearch4Label = external4.Label,
            externalSearch4UrlTemplate = external4.UrlTemplate,
            configuration.AllowPatronAutoholdOptOut,
            logoUrl = configuration.HasLogo
                ? $"/api/asap/config/logo?libraryOrgId={configuration.OrganizationId}"
                : "/jpl.png",
            logoAlt = configuration.LogoAltText,
            systemNotEnabled = !configuration.IsActive,
            systemNotEnabledMessage = configuration.SystemNotEnabledMessage,
            library = configuration.OrganizationName
        };
    }

    private static async Task<PatronSessionContext?> AuthenticateAsync(
        HttpRequest request,
        PatronSessionService sessions,
        CancellationToken cancellationToken)
    {
        var token = BearerToken(request);
        return token is null
            ? null
            : await sessions.AuthenticateAsync(token, cancellationToken);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var value = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Clean(value[prefix.Length..])
            : null;
    }

    private static IResult Unauthorized() =>
        Results.Json(
            new { message = "Your session has expired. Please log in again." },
            statusCode: StatusCodes.Status401Unauthorized);

    private static string ParticipationMessage(
        EffectivePatronConfiguration? configuration,
        string? organizationName) =>
        configuration?.SystemNotEnabledMessage ??
        $"{organizationName ?? "Your library"} does not currently participate in this suggestion service.";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
