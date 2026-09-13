using Microsoft.EntityFrameworkCore;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Patron;

public sealed record EffectiveFieldRule(string Mode, string Label);

public sealed record EffectiveCustomFieldRule(string Mode, string? Label);

public sealed record EffectiveFormatRule(
    long Id,
    string Code,
    string Label,
    int SortOrder,
    bool IsEnabled,
    string MessageBehavior,
    string? Message,
    EffectiveFieldRule Title,
    EffectiveFieldRule Author,
    EffectiveFieldRule Identifier,
    EffectiveFieldRule Publication,
    IReadOnlyDictionary<string, EffectiveCustomFieldRule> CustomFields);

public sealed record EffectiveCustomFieldOption(string Key, string Label, int SortOrder);

public sealed record EffectiveCustomField(
    long Id,
    string Key,
    string Type,
    string Label,
    string? HelpText,
    int SortOrder,
    IReadOnlyList<EffectiveCustomFieldOption> Options);

public sealed record EffectiveEmailConfiguration(
    string? FromAddress,
    string? FromName,
    string? ProtectedServerToken);

public sealed record EffectiveEmailTemplate(
    string TemplateKey,
    string SubjectTemplate,
    string BodyTemplate);

public sealed record EffectiveBranding(
    byte[]? LogoData,
    string? LogoContentType,
    string? LogoFileName,
    string LogoAltText);

public sealed record EffectiveExternalSearchProvider(
    string Key,
    bool IsEnabled,
    string Label,
    string UrlTemplate,
    int SortOrder);

public sealed record EffectivePatronConfiguration(
    int OrganizationId,
    string OrganizationName,
    bool IsActive,
    int SuggestionLimit,
    string SuggestionLimitMessage,
    bool AllowAnyRegisteredCardLogin,
    bool AllowPatronAutoholdOptOut,
    bool PatronCodeEligibilityEnabled,
    string PatronCodeEligibilityMessage,
    IReadOnlySet<string> AllowedPatronCodeIds,
    string PageTitle,
    string BarcodeLabel,
    string PinLabel,
    string LoginPrompt,
    string LoginNote,
    string SuggestionFormNote,
    string NoEmailMessage,
    string SuccessTitle,
    string SuccessMessage,
    string AlreadySubmittedMessage,
    string SystemNotEnabledMessage,
    string EbookMessage,
    string EaudiobookMessage,
    IReadOnlyDictionary<string, string> DuplicateStatusLabels,
    IReadOnlyList<EffectiveExternalSearchProvider> ExternalSearchProviders,
    IReadOnlyList<string> PublicationOptions,
    IReadOnlyList<string> CommonCreators,
    string CommonCreatorsLabel,
    string CommonCreatorsHelp,
    string CommonCreatorsMessage,
    bool CommonCreatorsEnabled,
    IReadOnlyList<EffectiveFormatRule> Formats,
    IReadOnlyList<EffectiveCustomField> CustomFields,
    EffectiveEmailConfiguration Email,
    EffectiveEmailTemplate? SubmissionTemplate,
    bool HasLogo,
    string LogoAltText);

public sealed class PatronConfigurationService(IDbContextFactory<AsapDbContext> contextFactory)
{
    public async Task<EffectivePatronConfiguration?> GetAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organization = await context.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        var systemWorkflow = await context.WorkflowSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var libraryWorkflow = organizationId == 1
            ? null
            : await context.WorkflowSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var systemPatron = await context.PatronSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var libraryPatron = organizationId == 1
            ? null
            : await context.PatronSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var systemSettings = await context.SystemSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);

        var formats = await LoadFormatsAsync(context, organizationId, cancellationToken);
        var customFields = await LoadCustomFieldsAsync(context, organizationId, cancellationToken);
        var publicationOptions = await LoadPublicationOptionsAsync(context, organizationId, cancellationToken);
        var creators = await LoadCommonCreatorsAsync(context, organizationId, cancellationToken);
        var patronCodes = await LoadPatronCodesAsync(context, organizationId, cancellationToken);
        var email = await LoadEmailAsync(context, organizationId, cancellationToken);
        var template = await LoadSubmissionTemplateAsync(context, organizationId, cancellationToken);
        var branding = await LoadBrandingAsync(context, organizationId, organization.DisplayName, cancellationToken);
        var externalSearchProviders = await LoadExternalSearchProvidersAsync(
            context,
            organizationId,
            cancellationToken);
        var duplicateStatusLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["suggestion"] = Pick(libraryPatron?.SuggestionStatusLabel, systemPatron.SuggestionStatusLabel, "Received"),
            ["outstanding_purchase"] = Pick(libraryPatron?.OutstandingPurchaseStatusLabel, systemPatron.OutstandingPurchaseStatusLabel, "Under review"),
            ["pending_hold"] = Pick(libraryPatron?.PendingHoldStatusLabel, systemPatron.PendingHoldStatusLabel, "Being prepared"),
            ["hold_placed"] = Pick(libraryPatron?.HoldPlacedStatusLabel, systemPatron.HoldPlacedStatusLabel, "Hold placed"),
            ["closed"] = Pick(libraryPatron?.ClosedStatusLabel, systemPatron.ClosedStatusLabel, "Completed"),
            ["rejected"] = Pick(libraryPatron?.RejectedStatusLabel, systemPatron.RejectedStatusLabel, "Not selected for purchase"),
            ["hold_completed"] = Pick(libraryPatron?.HoldCompletedStatusLabel, systemPatron.HoldCompletedStatusLabel, "Completed"),
            ["hold_not_picked_up"] = Pick(libraryPatron?.HoldNotPickedUpStatusLabel, systemPatron.HoldNotPickedUpStatusLabel, "Closed"),
            ["manual"] = Pick(libraryPatron?.ManualStatusLabel, systemPatron.ManualStatusLabel, "Closed"),
            ["silent"] = Pick(libraryPatron?.SilentStatusLabel, systemPatron.SilentStatusLabel, "Closed"),
            ["Silently Closed"] = Pick(libraryPatron?.SilentStatusLabel, systemPatron.SilentStatusLabel, "Closed")
        };

        return new EffectivePatronConfiguration(
            organization.Id,
            organization.DisplayName,
            organization.IsActive,
            libraryWorkflow?.SuggestionLimit ?? systemWorkflow.SuggestionLimit ?? 5,
            Pick(libraryWorkflow?.SuggestionLimitMessage, systemWorkflow.SuggestionLimitMessage,
                "Weekly suggestion limit reached. You can try again after {{next_available_date}}."),
            libraryWorkflow?.AllowAnyRegisteredCardLogin ?? systemWorkflow.AllowAnyRegisteredCardLogin ?? false,
            libraryWorkflow?.AllowPatronAutoholdOptOut ?? systemWorkflow.AllowPatronAutoholdOptOut ?? false,
            libraryWorkflow?.PatronCodeEligibilityEnabled ?? systemWorkflow.PatronCodeEligibilityEnabled ?? false,
            Pick(libraryWorkflow?.PatronCodeEligibilityMessage, systemWorkflow.PatronCodeEligibilityMessage,
                "Your library card is not eligible to use this suggestion service."),
            patronCodes,
            Pick(libraryPatron?.PageTitle, systemPatron.PageTitle, "Material Suggestion"),
            Pick(libraryPatron?.BarcodeLabel, systemPatron.BarcodeLabel, "Library Card"),
            Pick(libraryPatron?.PinLabel, systemPatron.PinLabel, "Pin"),
            Pick(libraryPatron?.LoginPrompt, systemPatron.LoginPrompt,
                "Please enter your information below to start the suggestion process."),
            Pick(libraryPatron?.LoginNote, systemPatron.LoginNote,
                "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN."),
            Pick(libraryPatron?.SuggestionFormNote, systemPatron.SuggestionFormNote,
                "If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update."),
            Pick(libraryPatron?.NoEmailMessage, systemPatron.NoEmailMessage,
                "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates."),
            Pick(libraryPatron?.SuccessTitle, systemPatron.SuccessTitle, "Suggestion Submitted"),
            Pick(libraryPatron?.SuccessMessage, systemPatron.SuccessMessage,
                "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>"),
            Pick(libraryPatron?.AlreadySubmittedMessage, systemPatron.AlreadySubmittedMessage,
                "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>"),
            Pick(null, systemSettings.SystemNotEnabledMessage,
                    "{{library}} does not currently participate in this suggestion service.")
                .Replace("{{library}}", organization.DisplayName, StringComparison.Ordinal),
            Pick(libraryPatron?.EbookMessage, systemPatron.EbookMessage,
                "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
            Pick(libraryPatron?.EaudiobookMessage, systemPatron.EaudiobookMessage,
                "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
            duplicateStatusLabels,
            externalSearchProviders,
            publicationOptions,
            creators,
            Pick(libraryWorkflow?.CommonAuthorsLabel, systemWorkflow.CommonAuthorsLabel, "Popular Creators"),
            Pick(libraryWorkflow?.CommonAuthorsHelp, systemWorkflow.CommonAuthorsHelp, string.Empty),
            Pick(libraryWorkflow?.CommonAuthorsMessage, systemWorkflow.CommonAuthorsMessage, string.Empty),
            libraryWorkflow?.CommonAuthorsEnabled ?? systemWorkflow.CommonAuthorsEnabled ?? false,
            formats,
            customFields,
            email,
            template,
            branding.LogoData is { Length: > 0 },
            branding.LogoAltText);
    }

    public async Task<EffectiveBranding?> GetBrandingAsync(
        int organizationId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var organization = await context.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);
        return organization is null
            ? null
            : await LoadBrandingAsync(context, organizationId, organization.DisplayName, cancellationToken);
    }

    private static async Task<IReadOnlyList<EffectiveFormatRule>> LoadFormatsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == 1 || item.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == 1
            ? []
            : await context.MaterialFormatOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var overrideByFormat = overrides.ToDictionary(item => item.MaterialFormatId);
        var customRules = organizationId == 1
            ? []
            : await context.MaterialFormatCustomFieldRules.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .Join(
                    context.PatronCustomFields.AsNoTracking().Where(item => item.IsEnabled),
                    rule => rule.PatronCustomFieldId,
                    field => field.Id,
                    (rule, field) => new
                    {
                        rule.MaterialFormatId,
                        field.FieldKey,
                        rule.Mode,
                        rule.LabelOverride
                    })
                .ToListAsync(cancellationToken);

        return formats.Select(format =>
            {
                overrideByFormat.TryGetValue(format.Id, out var value);
                return new EffectiveFormatRule(
                    format.Id,
                    format.Code,
                    value?.Label ?? format.Label,
                    value?.SortOrder ?? format.SortOrder,
                    value?.IsEnabled ?? format.IsEnabled,
                    value?.MessageBehavior ?? format.MessageBehavior ?? "none",
                    value?.Message ?? format.Message,
                    Field(value?.TitleMode ?? format.TitleMode ?? "required", value?.TitleLabel ?? format.TitleLabel, "Title"),
                    Field(value?.AuthorMode ?? format.AuthorMode ?? "optional", value?.AuthorLabel ?? format.AuthorLabel, "Author"),
                    Field(value?.IdentifierMode ?? format.IdentifierMode ?? "optional", value?.IdentifierLabel ?? format.IdentifierLabel, "Identifier number"),
                    Field(value?.PublicationMode ?? format.PublicationMode ?? "optional", value?.PublicationLabel ?? format.PublicationLabel, "Publication Timing"),
                    customRules
                        .Where(item => item.MaterialFormatId == format.Id)
                        .ToDictionary(
                            item => item.FieldKey,
                            item => new EffectiveCustomFieldRule(item.Mode, item.LabelOverride),
                            StringComparer.Ordinal));
            })
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToArray();
    }

    private static async Task<IReadOnlyList<EffectiveCustomField>> LoadCustomFieldsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == 1)
        {
            return [];
        }

        var fields = await context.PatronCustomFields.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId && item.IsEnabled)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var ids = fields.Select(item => item.Id).ToArray();
        var options = await context.PatronCustomFieldOptions.AsNoTracking()
            .Where(item => ids.Contains(item.PatronCustomFieldId) && item.IsEnabled)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        return fields.Select(field => new EffectiveCustomField(
                field.Id,
                field.FieldKey,
                field.FieldType,
                field.Label,
                field.HelpText,
                field.SortOrder,
                options.Where(item => item.PatronCustomFieldId == field.Id)
                    .Select(item => new EffectiveCustomFieldOption(item.OptionKey, item.Label, item.SortOrder))
                    .ToArray()))
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> LoadPublicationOptionsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var ownerId = organizationId != 1 && await context.PublicationOptionSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ? organizationId
            : 1;
        return await context.PublicationOptions.AsNoTracking()
            .Where(item => item.OrganizationId == ownerId && item.IsEnabled)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => item.Label)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> LoadCommonCreatorsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var ownerId = organizationId != 1 && await context.CommonCreatorSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ? organizationId
            : 1;
        return await context.CommonCreatorTerms.AsNoTracking()
            .Where(item => item.OrganizationId == ownerId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => item.Value)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> LoadPatronCodesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var ownerId = organizationId != 1 && await context.PatronCodeEligibilitySets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ? organizationId
            : 1;
        var values = await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == ownerId)
            .Select(item => item.PatronCodeId)
            .ToListAsync(cancellationToken);
        return values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<EffectiveEmailConfiguration> LoadEmailAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var system = await context.EmailSettings.AsNoTracking()
            .SingleAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = organizationId == 1
            ? null
            : await context.EmailSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return new EffectiveEmailConfiguration(
            library?.FromAddress ?? system.FromAddress,
            library?.FromName ?? system.FromName,
            library?.ProtectedServerToken ?? system.ProtectedServerToken);
    }

    private static async Task<EffectiveEmailTemplate?> LoadSubmissionTemplateAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var system = await context.EmailTemplates.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.OrganizationId == 1 && item.TemplateKey == "suggestion_submitted",
                cancellationToken);
        if (system is null)
        {
            return null;
        }

        var library = organizationId == 1
            ? null
            : await context.EmailTemplates.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.OrganizationId == organizationId && item.SourceTemplateId == system.Id,
                    cancellationToken);
        var selected = library ?? system;
        return selected.SubjectTemplate is null || selected.BodyTemplate is null
            ? null
            : new EffectiveEmailTemplate(system.TemplateKey, selected.SubjectTemplate, selected.BodyTemplate);
    }

    private static async Task<EffectiveBranding> LoadBrandingAsync(
        AsapDbContext context,
        int organizationId,
        string organizationName,
        CancellationToken cancellationToken)
    {
        var system = await context.Branding.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == 1, cancellationToken);
        var library = organizationId == 1
            ? null
            : await context.Branding.AsNoTracking()
                .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var imageSource = library?.LogoData is { Length: > 0 } ? library : system;
        return new EffectiveBranding(
            imageSource?.LogoData,
            imageSource?.LogoContentType,
            imageSource?.LogoFileName,
            Pick(library?.LogoAltText, system?.LogoAltText, "Library Logo"));
    }

    private static async Task<IReadOnlyList<EffectiveExternalSearchProvider>> LoadExternalSearchProvidersAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var providers = await context.ExternalSearchProviders.AsNoTracking()
            .Where(item => item.OrganizationId == 1)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == 1
            ? []
            : await context.ExternalSearchProviderOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var overridesByProvider = overrides.ToDictionary(item => item.ExternalSearchProviderId);

        return providers.Select(provider =>
            {
                overridesByProvider.TryGetValue(provider.Id, out var value);
                return new EffectiveExternalSearchProvider(
                    provider.ProviderKey,
                    value?.IsEnabled ?? provider.IsEnabled,
                    value?.Label ?? provider.Label,
                    value?.UrlTemplate ?? provider.UrlTemplate,
                    provider.SortOrder);
            })
            .ToArray();
    }

    private static EffectiveFieldRule Field(string mode, string? label, string fallbackLabel) =>
        new(mode, string.IsNullOrWhiteSpace(label) ? fallbackLabel : label);

    private static string Pick(string? library, string? system, string fallback) =>
        !string.IsNullOrWhiteSpace(library)
            ? library
            : !string.IsNullOrWhiteSpace(system)
                ? system
                : fallback;
}
