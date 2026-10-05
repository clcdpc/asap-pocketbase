using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asap.Shared;
using Asap.Web.Features.Email;
using Asap.Web.Features.Patron;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;
using Asap.Web.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Asap.Web.Features.Administration;

public sealed partial class AdministrationService
{
    private static SystemSettingsView ToSystemSettings(SystemSettings row, IReadOnlyList<string> origins) => new SystemSettingsView
    {
        StaffUrl = row.StaffApplicationUrl,
        LeapBibUrlPattern = row.LeapBibUrlPattern,
        LeapPatronUrlPattern = row.LeapPatronUrlPattern,
        FormatIconUrlPattern = row.MaterialTypeIconUrlPattern,
        SystemNotEnabledMessage = row.SystemNotEnabledMessage,
        MisconfiguredMessage = row.MisconfiguredMessage,
        PatronEmbedAllowedOrigins = origins,
        Version = StaffVersion.Encode(row.RowVersion)
    };

    private static PolarisSettingsView ToPolarisSettings(PolarisSettings row) => new PolarisSettingsView
    {
        Host = row.Host,
        AccessId = row.AccessId,
        StaffDomain = row.StaffDomain,
        AdminUser = row.AdminUser,
        WorkstationId = row.WorkstationId,
        SystemPolarisUserId = row.SystemPolarisUserId,
        HasApiKey = !string.IsNullOrWhiteSpace(row.ProtectedApiKey),
        HasAdminPassword = !string.IsNullOrWhiteSpace(row.ProtectedAdminPassword),
        Version = StaffVersion.Encode(row.RowVersion)
    };

    private static WorkflowSettingsView ToWorkflowRow(WorkflowSettings row) => new WorkflowSettingsView
    {
        SuggestionLimit = row.SuggestionLimit,
        SuggestionLimitMessage = row.SuggestionLimitMessage,
        OutstandingTimeoutEnabled = row.OutstandingTimeoutEnabled,
        OutstandingTimeoutDays = row.OutstandingTimeoutDays,
        OutstandingTimeoutSendEmail = row.OutstandingTimeoutSendEmail,
        OutstandingTimeoutRejectionTemplateId = row.OutstandingTimeoutRejectionTemplateId?.ToString(),
        HoldPickupTimeoutEnabled = row.HoldPickupTimeoutEnabled,
        HoldPickupTimeoutDays = row.HoldPickupTimeoutDays,
        PendingHoldTimeoutEnabled = row.PendingHoldTimeoutEnabled,
        PendingHoldTimeoutDays = row.PendingHoldTimeoutDays,
        AdditionalCopyTimeoutEnabled = row.AdditionalCopyTimeoutEnabled,
        AdditionalCopyTimeoutDays = row.AdditionalCopyTimeoutDays,
        AutoPromote = row.AutoPromote,
        CommonAuthorsEnabled = row.CommonAuthorsEnabled,
        CommonAuthorsLabel = row.CommonAuthorsLabel,
        CommonAuthorsHelp = row.CommonAuthorsHelp,
        CommonAuthorsMessage = row.CommonAuthorsMessage,
        AllowPatronAutoholdOptOut = row.AllowPatronAutoholdOptOut,
        AllowAnyRegisteredCardLogin = row.AllowAnyRegisteredCardLogin,
        PatronCodeEligibilityEnabled = row.PatronCodeEligibilityEnabled,
        PatronCodeEligibilityMessage = row.PatronCodeEligibilityMessage,
        Version = StaffVersion.Encode(row.RowVersion)
    };

    private static WorkflowSettingsView? ToWorkflow(WorkflowSettings? row) => row is null ? null : ToWorkflowRow(row);

    private static object ToWorkflow(EffectivePatronConfiguration row, bool system) => new
    {
        suggestionLimit = row.SuggestionLimit,
        suggestionLimitMessage = row.SuggestionLimitMessage,
        allowAnyRegisteredCardLogin = row.AllowAnyRegisteredCardLogin,
        allowPatronAutoholdOptOut = row.AllowPatronAutoholdOptOut,
        patronCodeEligibilityEnabled = row.PatronCodeEligibilityEnabled,
        patronCodeEligibilityMessage = row.PatronCodeEligibilityMessage,
        commonAuthorsEnabled = row.CommonCreatorsEnabled,
        commonAuthorsLabel = row.CommonCreatorsLabel,
        commonAuthorsHelp = row.CommonCreatorsHelp,
        commonAuthorsMessage = row.CommonCreatorsMessage,
        system
    };

    private static PatronTextSettingsView ToPatronRow(PatronSettings row) => new()
    {
        PageTitle = row.PageTitle,
        BarcodeLabel = row.BarcodeLabel,
        PinLabel = row.PinLabel,
        LoginPrompt = row.LoginPrompt,
        LoginNote = row.LoginNote,
        SuggestionFormNote = row.SuggestionFormNote,
        NoEmailMessage = row.NoEmailMessage,
        SuccessTitle = row.SuccessTitle,
        SuccessMessage = row.SuccessMessage,
        AlreadySubmittedMessage = row.AlreadySubmittedMessage,
        EbookMessage = row.EbookMessage,
        EaudiobookMessage = row.EaudiobookMessage,
        SuggestionStatusLabel = row.SuggestionStatusLabel,
        OutstandingPurchaseStatusLabel = row.OutstandingPurchaseStatusLabel,
        PendingHoldStatusLabel = row.PendingHoldStatusLabel,
        HoldPlacedStatusLabel = row.HoldPlacedStatusLabel,
        ClosedStatusLabel = row.ClosedStatusLabel,
        RejectedStatusLabel = row.RejectedStatusLabel,
        HoldCompletedStatusLabel = row.HoldCompletedStatusLabel,
        HoldNotPickedUpStatusLabel = row.HoldNotPickedUpStatusLabel,
        ManualStatusLabel = row.ManualStatusLabel,
        SilentStatusLabel = row.SilentStatusLabel,
    };

    private static PatronTextSettingsView? ToPatron(PatronSettings? row) => row is null ? null : ToPatronRow(row);

    private static EmailSettingsView ToEmailRow(EmailSettings row) => new EmailSettingsView
    {
        FromAddress = row.FromAddress,
        FromName = row.FromName,
        HasPostmarkToken = row.OrganizationId == LibraryScope.SystemOrganizationId && !string.IsNullOrWhiteSpace(row.ProtectedServerToken),
        Version = StaffVersion.Encode(row.RowVersion)
    };

    private static EmailSettingsView? ToEmail(EmailSettings? row) => row is null ? null : ToEmailRow(row);

    private static BrandingSettingsView ToBranding(Branding? row) => new BrandingSettingsView
    {
        HasLogo = row?.LogoData is { Length: > 0 },
        ContentType = row?.LogoContentType,
        FileName = row?.LogoFileName,
        AltText = row?.LogoAltText,
        Version = row is null ? null : StaffVersion.Encode(row.RowVersion)
    };

    private static object ToEffectiveConfiguration(
        EffectivePatronConfiguration row,
        WorkflowSettings systemWorkflow,
        WorkflowSettings? libraryWorkflow) => new
    {
        workflow = ToEffectiveWorkflow(systemWorkflow, libraryWorkflow),
        row.OrganizationId,
        row.OrganizationName,
        row.IsActive,
        row.SuggestionLimit,
        row.SuggestionLimitMessage,
        row.AllowAnyRegisteredCardLogin,
        row.AllowPatronAutoholdOptOut,
        row.PatronCodeEligibilityEnabled,
        row.PatronCodeEligibilityMessage,
        allowedPatronCodeIds = row.AllowedPatronCodeIds,
        row.PageTitle,
        row.BarcodeLabel,
        row.PinLabel,
        row.LoginPrompt,
        row.LoginNote,
        row.SuggestionFormNote,
        row.NoEmailMessage,
        row.SuccessTitle,
        row.SuccessMessage,
        row.AlreadySubmittedMessage,
        row.SystemNotEnabledMessage,
        row.MisconfiguredMessage,
        row.EbookMessage,
        row.EaudiobookMessage,
        row.DuplicateStatusLabels,
        externalSearchProviders = row.ExternalSearchProviders.Select(item => new
        {
            id = item.Id.ToString(),
            key = item.Key,
            isEnabled = item.IsEnabled,
            label = item.Label,
            urlTemplate = item.UrlTemplate,
            sortOrder = item.SortOrder
        }).ToArray(),
        row.PublicationOptions,
        row.CommonCreators,
        row.CommonCreatorsLabel,
        row.CommonCreatorsHelp,
        row.CommonCreatorsMessage,
        row.CommonCreatorsEnabled,
        formats = row.Formats.Select(item => new
        {
            id = item.Id.ToString(),
            code = item.Code,
            label = item.Label,
            sortOrder = item.SortOrder,
            isEnabled = item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            title = new { mode = item.Title.Mode, label = item.Title.Label },
            author = new { mode = item.Author.Mode, label = item.Author.Label },
            identifier = new { mode = item.Identifier.Mode, label = item.Identifier.Label },
            publication = new { mode = item.Publication.Mode, label = item.Publication.Label },
            customFields = item.CustomFields.ToDictionary(
                pair => pair.Key,
                pair => (object)new { mode = pair.Value.Mode, labelOverride = pair.Value.Label },
                StringComparer.Ordinal)
        }).ToArray(),
        customFields = row.CustomFields.Select(item => new
        {
            id = item.Id.ToString(),
            key = item.Key,
            type = item.Type,
            label = item.Label,
            helpText = item.HelpText,
            sortOrder = item.SortOrder,
            options = item.Options.Select(option => new
            {
                id = option.Key,
                label = option.Label,
                sortOrder = option.SortOrder
            }).ToArray()
        }).ToArray(),
        email = new
        {
            row.Email.FromAddress,
            row.Email.FromName,
            hasServerToken = !string.IsNullOrWhiteSpace(row.Email.ProtectedServerToken)
        },
        row.SubmissionTemplate,
        row.HasLogo,
        row.LogoAltText
    };

    private static object ToEffectiveWorkflow(
        WorkflowSettings system,
        WorkflowSettings? library) => new
    {
        suggestionLimit = library?.SuggestionLimit ?? system.SuggestionLimit,
        suggestionLimitMessage = library?.SuggestionLimitMessage ?? system.SuggestionLimitMessage,
        outstandingTimeoutEnabled = library?.OutstandingTimeoutEnabled ?? system.OutstandingTimeoutEnabled,
        outstandingTimeoutDays = library?.OutstandingTimeoutDays ?? system.OutstandingTimeoutDays,
        outstandingTimeoutSendEmail = library?.OutstandingTimeoutSendEmail ?? system.OutstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId = (library?.OutstandingTimeoutRejectionTemplateId ?? system.OutstandingTimeoutRejectionTemplateId)?.ToString(),
        holdPickupTimeoutEnabled = library?.HoldPickupTimeoutEnabled ?? system.HoldPickupTimeoutEnabled,
        holdPickupTimeoutDays = library?.HoldPickupTimeoutDays ?? system.HoldPickupTimeoutDays,
        pendingHoldTimeoutEnabled = library?.PendingHoldTimeoutEnabled ?? system.PendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays = library?.PendingHoldTimeoutDays ?? system.PendingHoldTimeoutDays,
        additionalCopyTimeoutEnabled = library?.AdditionalCopyTimeoutEnabled ?? system.AdditionalCopyTimeoutEnabled,
        additionalCopyTimeoutDays = library?.AdditionalCopyTimeoutDays ?? system.AdditionalCopyTimeoutDays,
        autoPromote = library?.AutoPromote ?? system.AutoPromote,
        commonAuthorsEnabled = library?.CommonAuthorsEnabled ?? system.CommonAuthorsEnabled,
        commonAuthorsLabel = library?.CommonAuthorsLabel ?? system.CommonAuthorsLabel,
        commonAuthorsHelp = library?.CommonAuthorsHelp ?? system.CommonAuthorsHelp,
        commonAuthorsMessage = library?.CommonAuthorsMessage ?? system.CommonAuthorsMessage,
        allowPatronAutoholdOptOut = library?.AllowPatronAutoholdOptOut ?? system.AllowPatronAutoholdOptOut,
        allowAnyRegisteredCardLogin = library?.AllowAnyRegisteredCardLogin ?? system.AllowAnyRegisteredCardLogin,
        patronCodeEligibilityEnabled = library?.PatronCodeEligibilityEnabled ?? system.PatronCodeEligibilityEnabled,
        patronCodeEligibilityMessage = library?.PatronCodeEligibilityMessage ?? system.PatronCodeEligibilityMessage
    };

    private static object ToEffectivePatronText(EffectivePatronConfiguration row) => new
    {
        row.PageTitle,
        row.BarcodeLabel,
        row.PinLabel,
        row.LoginPrompt,
        row.LoginNote,
        row.SuggestionFormNote,
        row.NoEmailMessage,
        row.SuccessTitle,
        row.SuccessMessage,
        row.AlreadySubmittedMessage,
        row.EbookMessage,
        row.EaudiobookMessage,
        row.DuplicateStatusLabels,
        row.SystemNotEnabledMessage,
        row.MisconfiguredMessage
    };

    private static async Task<object> ToEffectiveEmailAsync(
        AsapDbContext context,
        int organizationId,
        EffectivePatronConfiguration effective,
        CancellationToken cancellationToken)
    {
        var templates = await LoadTemplatesAsync(context, organizationId, cancellationToken);
        return new
        {
            fromAddress = effective.Email.FromAddress,
            fromName = effective.Email.FromName,
            hasPostmarkToken = !string.IsNullOrWhiteSpace(effective.Email.ProtectedServerToken),
            templates,
            submissionTemplate = effective.SubmissionTemplate
        };
    }

    private static async Task<bool> HasLibraryOverridesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        return await context.WorkflowSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PatronSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.EmailSettings.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PublicationOptionSets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.CommonCreatorSets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.PatronCodeEligibilitySets.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.ExternalSearchProviderOverrides.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.MaterialFormatOverrides.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.MaterialFormatCustomFieldRules.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.FormatAutoClaimRules.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.PatronCustomFields.AnyAsync(item => item.LibraryOrganizationId == organizationId, cancellationToken) ||
               await context.EmailTemplates.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken) ||
               await context.Branding.AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
    }

    private static async Task<object> LoadPublicationSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.PublicationOptionSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.PublicationOptions.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                id = item.OptionKey,
                label = item.Label,
                enabled = item.IsEnabled,
                sortOrder = item.SortOrder
            })
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<object> LoadCommonCreatorSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.CommonCreatorSets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.CommonCreatorTerms.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new { value = item.Value, sortOrder = item.SortOrder })
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<object> LoadPatronCodeSnapshotAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var exists = await context.PatronCodeEligibilitySets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken);
        var values = await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.PatronCodeId)
            .Select(item => item.PatronCodeId)
            .ToArrayAsync(cancellationToken);
        return new { exists, values };
    }

    private static async Task<IReadOnlyList<object>> LoadRawProviderOverridesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            var providers = await context.ExternalSearchProviders.AsNoTracking()
                .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
            return providers.Select(item => (object)new
            {
                kind = "system",
                id = item.Id.ToString(),
                key = item.ProviderKey,
                isEnabled = (bool?)item.IsEnabled,
                label = item.Label,
                urlTemplate = item.UrlTemplate,
                version = StaffVersion.Encode(item.RowVersion)
            }).ToArray();
        }

        var overrides = await context.ExternalSearchProviderOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.ExternalSearchProviderId)
            .ToListAsync(cancellationToken);
        return overrides.Select(item => (object)new
        {
            kind = "libraryOverride",
            id = item.ExternalSearchProviderId.ToString(),
            key = (string?)null,
            isEnabled = item.IsEnabled,
            label = item.Label,
            urlTemplate = item.UrlTemplate,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadRawFormatsAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var values = formats.Select(item => (object)new
        {
            kind = organizationId == LibraryScope.SystemOrganizationId ? "system" : "custom",
            id = item.Id.ToString(),
            materialFormatId = (string?)null,
            ownerOrganizationId = item.OwnerOrganizationId.ToString(),
            code = item.Code,
            label = item.Label,
            sortOrder = (int?)item.SortOrder,
            isEnabled = (bool?)item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            titleMode = item.TitleMode,
            titleLabel = item.TitleLabel,
            authorMode = item.AuthorMode,
            authorLabel = item.AuthorLabel,
            identifierMode = item.IdentifierMode,
            identifierLabel = item.IdentifierLabel,
            publicationMode = item.PublicationMode,
            publicationLabel = item.PublicationLabel,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToList();
        if (organizationId == LibraryScope.SystemOrganizationId)
        {
            return values;
        }

        var overrides = await context.MaterialFormatOverrides.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.MaterialFormatId)
            .ToListAsync(cancellationToken);
        values.AddRange(overrides.Select(item => (object)new
        {
            kind = "systemOverride",
            id = item.Id.ToString(),
            materialFormatId = item.MaterialFormatId.ToString(),
            ownerOrganizationId = "1",
            code = (string?)null,
            label = item.Label,
            sortOrder = item.SortOrder,
            isEnabled = item.IsEnabled,
            messageBehavior = item.MessageBehavior,
            message = item.Message,
            titleMode = item.TitleMode,
            titleLabel = item.TitleLabel,
            authorMode = item.AuthorMode,
            authorLabel = item.AuthorLabel,
            identifierMode = item.IdentifierMode,
            identifierLabel = item.IdentifierLabel,
            publicationMode = item.PublicationMode,
            publicationLabel = item.PublicationLabel,
            version = StaffVersion.Encode(item.RowVersion)
        }));
        return values;
    }

    private static async Task<IReadOnlyList<object>> LoadRawTemplatesAsync(
        AsapDbContext context,
        int organizationId,
        CancellationToken cancellationToken)
    {
        var templates = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == organizationId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return templates.Select(item => (object)new
        {
            id = item.Id.ToString(),
            organizationId = item.OrganizationId.ToString(),
            templateKey = item.TemplateKey,
            sourceTemplateId = item.SourceTemplateId?.ToString(),
            displayName = item.DisplayName,
            subject = item.SubjectTemplate,
            body = item.BodyTemplate,
            enabled = !item.IsHidden,
            isCustom = item.IsCustom,
            sortOrder = item.SortOrder,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadProvidersAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var providers = await context.ExternalSearchProviders.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await context.ExternalSearchProviderOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var byId = overrides.ToDictionary(item => item.ExternalSearchProviderId);
        return providers.Select(provider =>
        {
            byId.TryGetValue(provider.Id, out var value);
            return (object)new
            {
                key = provider.ProviderKey,
                id = provider.Id.ToString(),
                isEnabled = value?.IsEnabled ?? provider.IsEnabled,
                label = value?.Label ?? provider.Label,
                urlTemplate = value?.UrlTemplate ?? provider.UrlTemplate,
                system = new { provider.IsEnabled, provider.Label, provider.UrlTemplate },
                overridden = value is not null
            };
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadFormatsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var formats = await context.MaterialFormats.AsNoTracking()
            .Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId || item.OwnerOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var overrides = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await context.MaterialFormatOverrides.AsNoTracking()
                .Where(item => item.LibraryOrganizationId == organizationId)
                .ToListAsync(cancellationToken);
        var byId = overrides.ToDictionary(item => item.MaterialFormatId);
        return formats.Select(format =>
        {
            byId.TryGetValue(format.Id, out var value);
            return (object)new
            {
                id = format.Id.ToString(),
                code = format.Code,
                ownerOrganizationId = format.OwnerOrganizationId.ToString(),
                label = value?.Label ?? format.Label,
                sortOrder = value?.SortOrder ?? format.SortOrder,
                isEnabled = value?.IsEnabled ?? format.IsEnabled,
                messageBehavior = value?.MessageBehavior ?? format.MessageBehavior,
                message = value?.Message ?? format.Message,
                titleMode = value?.TitleMode ?? format.TitleMode,
                titleLabel = value?.TitleLabel ?? format.TitleLabel,
                authorMode = value?.AuthorMode ?? format.AuthorMode,
                authorLabel = value?.AuthorLabel ?? format.AuthorLabel,
                identifierMode = value?.IdentifierMode ?? format.IdentifierMode,
                identifierLabel = value?.IdentifierLabel ?? format.IdentifierLabel,
                publicationMode = value?.PublicationMode ?? format.PublicationMode,
                publicationLabel = value?.PublicationLabel ?? format.PublicationLabel,
                overridden = value is not null,
                version = value is null ? StaffVersion.Encode(format.RowVersion) : StaffVersion.Encode(value.RowVersion)
            };
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadCustomFieldsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var fields = await context.PatronCustomFields.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var ids = fields.Select(item => item.Id).ToArray();
        var options = await context.PatronCustomFieldOptions.AsNoTracking()
            .Where(item => ids.Contains(item.PatronCustomFieldId))
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return fields.Select(field => (object)new
        {
            id = field.Id.ToString(),
            key = field.FieldKey,
            type = field.FieldType,
            label = field.Label,
            helpText = field.HelpText,
            enabled = field.IsEnabled,
            sortOrder = field.SortOrder,
            options = options.Where(item => item.PatronCustomFieldId == field.Id).Select(item => new
            {
                id = item.OptionKey,
                label = item.Label,
                enabled = item.IsEnabled,
                sortOrder = item.SortOrder
            }).ToArray(),
            version = StaffVersion.Encode(field.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadTemplatesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var system = await context.EmailTemplates.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var library = organizationId == LibraryScope.SystemOrganizationId
            ? []
            : await context.EmailTemplates.AsNoTracking()
                .Where(item => item.OrganizationId == organizationId)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                .ToListAsync(cancellationToken);
        return system.Concat(library).Select(item => (object)new
        {
            id = item.Id.ToString(),
            organizationId = item.OrganizationId.ToString(),
            templateKey = item.TemplateKey,
            sourceTemplateId = item.SourceTemplateId?.ToString(),
            displayName = item.DisplayName,
            subject = item.SubjectTemplate,
            body = item.BodyTemplate,
            enabled = !item.IsHidden,
            isCustom = item.IsCustom,
            sortOrder = item.SortOrder,
            version = StaffVersion.Encode(item.RowVersion)
        }).ToArray();
    }

    private static async Task<IReadOnlyList<object>> LoadPublicationOptionsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var rows = await context.PublicationOptions.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId)
            .OrderBy(item => item.OrganizationId).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.OrganizationId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder })
            .ToListAsync(cancellationToken);
        return rows.Select(item => (object)new
        {
            organizationId = item.OrganizationId,
            id = item.OptionKey,
            label = item.Label,
            enabled = item.IsEnabled,
            sortOrder = item.SortOrder
        }).ToArray();
    }

    private static async Task<IReadOnlyList<string>> LoadCommonCreatorsAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var rows = await context.CommonCreatorTerms.AsNoTracking()
            .Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId)
            .OrderBy(item => item.OrganizationId).ThenBy(item => item.SortOrder).ThenBy(item => item.Id)
            .Select(item => new { item.OrganizationId, item.Value })
            .ToListAsync(cancellationToken);
        var owner = organizationId != LibraryScope.SystemOrganizationId && rows.Any(item => item.OrganizationId == organizationId)
            ? organizationId
            : 1;
        return rows.Where(item => item.OrganizationId == owner).Select(item => item.Value).ToArray();
    }

    private static async Task<IReadOnlyList<int>> LoadPatronCodesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var owner = organizationId != LibraryScope.SystemOrganizationId && await context.PatronCodeEligibilitySets.AsNoTracking()
            .AnyAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ? organizationId
            : 1;
        return await context.PatronCodeEligibilityMembers.AsNoTracking()
            .Where(item => item.OrganizationId == owner)
            .OrderBy(item => item.PatronCodeId)
            .Select(item => item.PatronCodeId)
            .ToArrayAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<object>> LoadAutoClaimRulesAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken) =>
        await context.FormatAutoClaimRules.AsNoTracking()
            .Where(item => item.LibraryOrganizationId == organizationId)
            .OrderBy(item => item.MaterialFormatId).ThenBy(item => item.Id)
            .Select(item => (object)new
            {
                id = item.Id.ToString(),
                materialFormatId = item.MaterialFormatId.ToString(),
                staffUserId = item.StaffUserId.HasValue ? item.StaffUserId.Value.ToString() : null,
                active = item.IsActive,
                version = StaffVersion.Encode(item.RowVersion)
            })
            .ToArrayAsync(cancellationToken);

    private static async Task<WorkflowSettings?> GetWorkflowAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken) =>
        await context.WorkflowSettings.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);

    private static async Task<string> ComputeSettingsVersionAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        async Task AddVersionsAsync<T>(IQueryable<T> query, Func<T, byte[]> version)
        {
            foreach (var item in await query.ToListAsync(cancellationToken))
            {
                parts.Add(Convert.ToHexString(version(item)));
            }
        }

        await AddVersionsAsync(
            context.Organizations.AsNoTracking()
                .Where(item => organizationId == LibraryScope.SystemOrganizationId || item.Id == 1 || item.Id == organizationId).OrderBy(item => item.Id),
            item => item.RowVersion);
        await AddVersionsAsync(context.SystemSettings.AsNoTracking().OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PolarisSettings.AsNoTracking().OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.WorkflowSettings.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronSettings.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.EmailSettings.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.CommonCreatorSets.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronCodeEligibilitySets.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.PublicationOptionSets.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        await AddVersionsAsync(context.ExternalSearchProviders.AsNoTracking().OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.ExternalSearchProviderOverrides.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.ExternalSearchProviderId), item => item.RowVersion);
        await AddVersionsAsync(context.PatronCustomFields.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormats.AsNoTracking().Where(item => item.OwnerOrganizationId == LibraryScope.SystemOrganizationId || item.OwnerOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormatOverrides.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.MaterialFormatId), item => item.RowVersion);
        await AddVersionsAsync(context.MaterialFormatCustomFieldRules.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.MaterialFormatId).ThenBy(item => item.PatronCustomFieldId), item => item.RowVersion);
        await AddVersionsAsync(context.FormatAutoClaimRules.AsNoTracking().Where(item => item.LibraryOrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.EmailTemplates.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.Id), item => item.RowVersion);
        await AddVersionsAsync(context.Branding.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId), item => item.RowVersion);
        parts.Add(JsonSerializer.Serialize(await context.PatronEmbedAllowedOrigins.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId).OrderBy(item => item.NormalizedOrigin).Select(item => new { item.Origin, item.NormalizedOrigin }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.CommonCreatorTerms.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.Id).Select(item => new { item.OrganizationId, item.Value, item.SortOrder }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PatronCodeEligibilityMembers.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.OrganizationId).ThenBy(item => item.PatronCodeId).Select(item => new { item.OrganizationId, item.PatronCodeId }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PublicationOptions.AsNoTracking().Where(item => item.OrganizationId == LibraryScope.SystemOrganizationId || item.OrganizationId == organizationId).OrderBy(item => item.Id).Select(item => new { item.OrganizationId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder }).ToListAsync(cancellationToken)));
        parts.Add(JsonSerializer.Serialize(await context.PatronCustomFieldOptions.AsNoTracking().Where(item => context.PatronCustomFields.Any(field => field.Id == item.PatronCustomFieldId && field.LibraryOrganizationId == organizationId)).OrderBy(item => item.Id).Select(item => new { item.PatronCustomFieldId, item.OptionKey, item.Label, item.IsEnabled, item.SortOrder }).ToListAsync(cancellationToken)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }

    // The writers below deliberately mirror the relational configuration model. Each method is
    // called only after the scope gate above, so a library payload cannot reach system-only rows.
}
