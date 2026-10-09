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
    private async Task ApplySystemSettingsAsync(
        AsapDbContext context,
        AdministrationSettingsCommand command,
        CancellationToken cancellationToken)
    {
        var system = await context.SystemSettings.SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var polaris = await context.PolarisSettings.SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        var email = await context.EmailSettings.SingleAsync(item => item.OrganizationId == LibraryScope.SystemOrganizationId, cancellationToken);
        Apply(command.System.StaffUrl, value => system.StaffApplicationUrl = NormalizeSystemText(value));
        Apply(command.System.LeapBibUrlPattern, value => system.LeapBibUrlPattern = NormalizeSystemText(value));
        Apply(command.System.LeapPatronUrlPattern, value => system.LeapPatronUrlPattern = NormalizeSystemText(value));
        Apply(command.System.FormatIconUrlPattern, value => system.MaterialTypeIconUrlPattern = NormalizeSystemText(value));
        Apply(command.System.SystemNotEnabledMessage, value => system.SystemNotEnabledMessage = NormalizeSystemText(value));
        Apply(command.System.MisconfiguredMessage, value => system.MisconfiguredMessage = NormalizeSystemText(value));
        var systemSection = GetObject(command.CollectionEdits, "systemSettings", "system");
        await ReplaceOriginsIfPresentAsync(context, command.CollectionEdits, systemSection, cancellationToken);
        await ApplyParticipationAsync(context, command.CollectionEdits, systemSection, cancellationToken);

        Apply(command.Polaris.Host, value => polaris.Host = NormalizeSystemText(value));
        Apply(command.Polaris.AccessId, value => polaris.AccessId = NormalizeSystemText(value));
        Apply(command.Polaris.StaffDomain, value => polaris.StaffDomain = NormalizeSystemText(value));
        Apply(command.Polaris.AdminUser, value => polaris.AdminUser = NormalizeSystemText(value));
        Apply(command.Polaris.WorkstationId, value => polaris.WorkstationId = value);
        Apply(command.Polaris.SystemPolarisUserId, value => polaris.SystemPolarisUserId = value);
        if (Clean(command.Polaris.ApiKey.Value) is { } apiKey)
        {
            polaris.ProtectedApiKey = credentialProtector.Protect(apiKey);
        }
        if (Clean(command.Polaris.AdminPassword.Value) is { } password)
        {
            polaris.ProtectedAdminPassword = credentialProtector.Protect(password);
        }
        if (command.Polaris.ClearApiKey.Value == true)
        {
            polaris.ProtectedApiKey = null;
        }
        if (command.Polaris.ClearAdminPassword.Value == true)
        {
            polaris.ProtectedAdminPassword = null;
        }
        if (Clean(command.Email.ServerToken.Value) is { } token)
        {
            email.ProtectedServerToken = credentialProtector.Protect(token);
        }
        if (command.Email.ClearServerToken.Value == true)
        {
            email.ProtectedServerToken = null;
        }
    }

    private static void Apply<T>(SuppliedValue<T> patch, Action<T> setter)
    {
        if (patch.IsSupplied)
        {
            setter(patch.Value);
        }
    }

    private async Task ApplyScopedSettingsAsync(
        AsapDbContext context,
        int organizationId,
        AdministrationSettingsCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.CollectionEdits;
        var workflowSection = GetObject(payload, "workflow");
        var patronSection = GetObject(payload, "ui_text", "patron");
        if (workflowSection.ValueKind == JsonValueKind.Undefined)
        {
            workflowSection = payload;
        }

        if (patronSection.ValueKind == JsonValueKind.Undefined)
        {
            patronSection = payload;
        }

        var isSystem = organizationId == LibraryScope.SystemOrganizationId;

        var workflow = await GetOrCreateWorkflowAsync(context, organizationId, cancellationToken);
        if (command.Workflow.SuggestionLimitMessage.IsSupplied)
        {
            workflow.SuggestionLimitMessage = NormalizeScopedText(command.Workflow.SuggestionLimitMessage.Value);
        }
        if (command.Workflow.CommonAuthorsLabel.IsSupplied)
        {
            workflow.CommonAuthorsLabel = NormalizeScopedText(command.Workflow.CommonAuthorsLabel.Value);
        }
        if (command.Workflow.CommonAuthorsHelp.IsSupplied)
        {
            workflow.CommonAuthorsHelp = NormalizeScopedText(command.Workflow.CommonAuthorsHelp.Value);
        }
        if (command.Workflow.CommonAuthorsMessage.IsSupplied)
        {
            workflow.CommonAuthorsMessage = NormalizeScopedText(command.Workflow.CommonAuthorsMessage.Value);
        }
        if (command.Workflow.PatronCodeEligibilityMessage.IsSupplied)
        {
            workflow.PatronCodeEligibilityMessage = NormalizeScopedText(command.Workflow.PatronCodeEligibilityMessage.Value);
        }
        if (command.Workflow.OutstandingTimeoutEnabled.IsSupplied)
        {
            workflow.OutstandingTimeoutEnabled = command.Workflow.OutstandingTimeoutEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.OutstandingTimeoutSendEmail.IsSupplied)
        {
            workflow.OutstandingTimeoutSendEmail = command.Workflow.OutstandingTimeoutSendEmail.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.HoldPickupTimeoutEnabled.IsSupplied)
        {
            workflow.HoldPickupTimeoutEnabled = command.Workflow.HoldPickupTimeoutEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.PendingHoldTimeoutEnabled.IsSupplied)
        {
            workflow.PendingHoldTimeoutEnabled = command.Workflow.PendingHoldTimeoutEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.AdditionalCopyTimeoutEnabled.IsSupplied)
        {
            workflow.AdditionalCopyTimeoutEnabled = command.Workflow.AdditionalCopyTimeoutEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.AutoPromote.IsSupplied)
        {
            workflow.AutoPromote = command.Workflow.AutoPromote.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.CommonAuthorsEnabled.IsSupplied)
        {
            workflow.CommonAuthorsEnabled = command.Workflow.CommonAuthorsEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.AllowPatronAutoholdOptOut.IsSupplied)
        {
            workflow.AllowPatronAutoholdOptOut = command.Workflow.AllowPatronAutoholdOptOut.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.AllowAnyRegisteredCardLogin.IsSupplied)
        {
            workflow.AllowAnyRegisteredCardLogin = command.Workflow.AllowAnyRegisteredCardLogin.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.PatronCodeEligibilityEnabled.IsSupplied)
        {
            workflow.PatronCodeEligibilityEnabled = command.Workflow.PatronCodeEligibilityEnabled.Value ?? (isSystem ? false : (bool?)null);
        }
        if (command.Workflow.SuggestionLimit.IsSupplied)
        {
            workflow.SuggestionLimit = command.Workflow.SuggestionLimit.Value;
        }
        if (command.Workflow.OutstandingTimeoutDays.IsSupplied)
        {
            workflow.OutstandingTimeoutDays = command.Workflow.OutstandingTimeoutDays.Value;
        }
        if (command.Workflow.HoldPickupTimeoutDays.IsSupplied)
        {
            workflow.HoldPickupTimeoutDays = command.Workflow.HoldPickupTimeoutDays.Value;
        }
        if (command.Workflow.PendingHoldTimeoutDays.IsSupplied)
        {
            workflow.PendingHoldTimeoutDays = command.Workflow.PendingHoldTimeoutDays.Value;
        }
        if (command.Workflow.AdditionalCopyTimeoutDays.IsSupplied)
        {
            workflow.AdditionalCopyTimeoutDays = command.Workflow.AdditionalCopyTimeoutDays.Value;
        }
        if (HasProperty(workflowSection, "outstandingTimeoutRejectionTemplateId"))
        {
            workflow.OutstandingTimeoutRejectionTemplateId = await ResolveTemplateReferenceAsync(
                context,
                GetString(workflowSection, "outstandingTimeoutRejectionTemplateId"),
                organizationId,
                cancellationToken);
        }
        if (HasProperty(workflowSection, "outstandingTimeoutRejectionTemplate"))
        {
            workflow.OutstandingTimeoutRejectionTemplateId = await ResolveTemplateReferenceAsync(
                context,
                GetString(workflowSection, "outstandingTimeoutRejectionTemplate"),
                organizationId,
                cancellationToken);
        }
        if (context.Entry(workflow).State == EntityState.Detached && !IsEmpty(workflow))
        {
            context.WorkflowSettings.Add(workflow);
        }

        var patron = await GetOrCreatePatronAsync(context, organizationId, cancellationToken);
        if (command.Patron.PageTitle.IsSupplied)
        {
            patron.PageTitle = NormalizeScopedText(command.Patron.PageTitle.Value);
        }
        if (command.Patron.BarcodeLabel.IsSupplied)
        {
            patron.BarcodeLabel = NormalizeScopedText(command.Patron.BarcodeLabel.Value);
        }
        if (command.Patron.PinLabel.IsSupplied)
        {
            patron.PinLabel = NormalizeScopedText(command.Patron.PinLabel.Value);
        }
        if (command.Patron.LoginPrompt.IsSupplied)
        {
            patron.LoginPrompt = NormalizeScopedText(command.Patron.LoginPrompt.Value);
        }
        if (command.Patron.LoginNote.IsSupplied)
        {
            patron.LoginNote = NormalizeScopedText(command.Patron.LoginNote.Value);
        }
        if (command.Patron.SuggestionFormNote.IsSupplied)
        {
            patron.SuggestionFormNote = NormalizeScopedText(command.Patron.SuggestionFormNote.Value);
        }
        if (command.Patron.NoEmailMessage.IsSupplied)
        {
            patron.NoEmailMessage = NormalizeScopedText(command.Patron.NoEmailMessage.Value);
        }
        if (command.Patron.SuccessTitle.IsSupplied)
        {
            patron.SuccessTitle = NormalizeScopedText(command.Patron.SuccessTitle.Value);
        }
        if (command.Patron.SuccessMessage.IsSupplied)
        {
            patron.SuccessMessage = NormalizeScopedText(command.Patron.SuccessMessage.Value);
        }
        if (command.Patron.AlreadySubmittedMessage.IsSupplied)
        {
            patron.AlreadySubmittedMessage = NormalizeScopedText(command.Patron.AlreadySubmittedMessage.Value);
        }
        if (command.Patron.EbookMessage.IsSupplied)
        {
            patron.EbookMessage = NormalizeScopedText(command.Patron.EbookMessage.Value);
        }
        if (command.Patron.EaudiobookMessage.IsSupplied)
        {
            patron.EaudiobookMessage = NormalizeScopedText(command.Patron.EaudiobookMessage.Value);
        }
        if (command.Patron.SuggestionStatusLabel.IsSupplied)
        {
            patron.SuggestionStatusLabel = NormalizeScopedText(command.Patron.SuggestionStatusLabel.Value);
        }
        if (command.Patron.OutstandingPurchaseStatusLabel.IsSupplied)
        {
            patron.OutstandingPurchaseStatusLabel = NormalizeScopedText(command.Patron.OutstandingPurchaseStatusLabel.Value);
        }
        if (command.Patron.PendingHoldStatusLabel.IsSupplied)
        {
            patron.PendingHoldStatusLabel = NormalizeScopedText(command.Patron.PendingHoldStatusLabel.Value);
        }
        if (command.Patron.HoldPlacedStatusLabel.IsSupplied)
        {
            patron.HoldPlacedStatusLabel = NormalizeScopedText(command.Patron.HoldPlacedStatusLabel.Value);
        }
        if (command.Patron.ClosedStatusLabel.IsSupplied)
        {
            patron.ClosedStatusLabel = NormalizeScopedText(command.Patron.ClosedStatusLabel.Value);
        }
        if (command.Patron.RejectedStatusLabel.IsSupplied)
        {
            patron.RejectedStatusLabel = NormalizeScopedText(command.Patron.RejectedStatusLabel.Value);
        }
        if (command.Patron.HoldCompletedStatusLabel.IsSupplied)
        {
            patron.HoldCompletedStatusLabel = NormalizeScopedText(command.Patron.HoldCompletedStatusLabel.Value);
        }
        if (command.Patron.HoldNotPickedUpStatusLabel.IsSupplied)
        {
            patron.HoldNotPickedUpStatusLabel = NormalizeScopedText(command.Patron.HoldNotPickedUpStatusLabel.Value);
        }
        if (command.Patron.ManualStatusLabel.IsSupplied)
        {
            patron.ManualStatusLabel = NormalizeScopedText(command.Patron.ManualStatusLabel.Value);
        }
        if (command.Patron.SilentStatusLabel.IsSupplied)
        {
            patron.SilentStatusLabel = NormalizeScopedText(command.Patron.SilentStatusLabel.Value);
        }
        ApplyDuplicateLabels(patronSection, patron, isSystem);
        if (context.Entry(patron).State == EntityState.Detached && !IsEmpty(patron))
        {
            context.PatronSettings.Add(patron);
        }

        var email = await GetOrCreateEmailAsync(context, organizationId, cancellationToken);
        Apply(command.Email.FromAddress, value => email.FromAddress = NormalizeScopedText(value));
        Apply(command.Email.FromName, value => email.FromName = NormalizeScopedText(value));
        if (context.Entry(email).State == EntityState.Detached && !IsEmpty(email))
        {
            context.EmailSettings.Add(email);
        }
        await ApplyWholeSetsAsync(context, organizationId, workflowSection, patronSection, cancellationToken);
        await ApplyProvidersAsync(context, organizationId, workflowSection, payload, cancellationToken);
        await ApplyFormatsAsync(context, organizationId, payload, patronSection, cancellationToken);
        await ApplyCustomFieldsAsync(context, organizationId, patronSection, payload, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        if (HasEffectiveFormatOrCustomFieldEdits(payload, patronSection))
        {
            await ValidateEffectiveRequiredSelectRulesAsync(context, organizationId, cancellationToken);
        }
        await ApplyAutoClaimRulesAsync(context, organizationId, payload, cancellationToken);
        await ApplyTemplatesAsync(context, organizationId, payload, cancellationToken);
        await ValidateRejectionTemplateReferencesAsync(context, organizationId, cancellationToken);
        await ApplyBrandingAsync(context, organizationId, patronSection, payload, cancellationToken);

        if (!isSystem)
        {
            await RemoveEmptyOverrideRowsAsync(context, organizationId, cancellationToken);
        }
    }

    private async Task ResetLibrarySettingsInTransactionAsync(
        AsapDbContext context,
        int organizationId,
        CurrentStaff actor,
        CancellationToken cancellationToken)
    {
        var workflow = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (workflow is not null)
        {
            workflow.OutstandingTimeoutRejectionTemplateId = null;
            context.WorkflowSettings.Remove(workflow);
        }
        var patron = await context.PatronSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (patron is not null)
        {
            context.PatronSettings.Remove(patron);
        }

        var email = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (email is not null)
        {
            context.EmailSettings.Remove(email);
        }

        var publication = await context.PublicationOptionSets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (publication is not null)
        {
            context.PublicationOptionSets.Remove(publication);
        }

        var creators = await context.CommonCreatorSets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (creators is not null)
        {
            context.CommonCreatorSets.Remove(creators);
        }

        var patronCodes = await context.PatronCodeEligibilitySets.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (patronCodes is not null)
        {
            context.PatronCodeEligibilitySets.Remove(patronCodes);
        }

        context.ExternalSearchProviderOverrides.RemoveRange(
            await context.ExternalSearchProviderOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken));
        context.MaterialFormatOverrides.RemoveRange(
            await context.MaterialFormatOverrides.Where(item => item.LibraryOrganizationId == organizationId).ToListAsync(cancellationToken));
        context.EmailTemplates.RemoveRange(
            await context.EmailTemplates.Where(item => item.OrganizationId == organizationId && item.SourceTemplateId != null).ToListAsync(cancellationToken));
        var branding = await context.Branding.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        if (branding is not null)
        {
            context.Branding.Remove(branding);
        }

        await AddAuditAsync(
            context,
            actor,
            organizationId,
            "library_settings_reset",
            "Configuration",
            organizationId.ToString(),
            new
            {
                preserved = new[] { "custom_fields", "custom_formats", "format_rules", "auto_claim_rules", "custom_templates" },
                removed = new[] { "scalar_overrides", "whole_sets", "provider_overrides", "format_overrides", "inherited_templates", "branding" }
            });
    }

    private static async Task<WorkflowSettings> GetOrCreateWorkflowAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.WorkflowSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return row ?? new WorkflowSettings { OrganizationId = organizationId };
    }

    private static async Task<PatronSettings> GetOrCreatePatronAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.PatronSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return row ?? new PatronSettings { OrganizationId = organizationId };
    }

    private static async Task<EmailSettings> GetOrCreateEmailAsync(AsapDbContext context, int organizationId, CancellationToken cancellationToken)
    {
        var row = await context.EmailSettings.SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken);
        return row ?? new EmailSettings { OrganizationId = organizationId };
    }

    private static void SetPatronText(PatronSettings row, string property, string? value, bool system)
    {
        var normalized = NormalizeScopedText(value);
        switch (property)
        {
            case nameof(PatronSettings.PageTitle): row.PageTitle = normalized; break;
            case nameof(PatronSettings.BarcodeLabel): row.BarcodeLabel = normalized; break;
            case nameof(PatronSettings.PinLabel): row.PinLabel = normalized; break;
            case nameof(PatronSettings.LoginPrompt): row.LoginPrompt = normalized; break;
            case nameof(PatronSettings.LoginNote): row.LoginNote = normalized; break;
            case nameof(PatronSettings.SuggestionFormNote): row.SuggestionFormNote = normalized; break;
            case nameof(PatronSettings.NoEmailMessage): row.NoEmailMessage = normalized; break;
            case nameof(PatronSettings.SuccessTitle): row.SuccessTitle = normalized; break;
            case nameof(PatronSettings.SuccessMessage): row.SuccessMessage = normalized; break;
            case nameof(PatronSettings.AlreadySubmittedMessage): row.AlreadySubmittedMessage = normalized; break;
            case nameof(PatronSettings.EbookMessage): row.EbookMessage = normalized; break;
            case nameof(PatronSettings.EaudiobookMessage): row.EaudiobookMessage = normalized; break;
            case nameof(PatronSettings.SuggestionStatusLabel): row.SuggestionStatusLabel = normalized; break;
            case nameof(PatronSettings.OutstandingPurchaseStatusLabel): row.OutstandingPurchaseStatusLabel = normalized; break;
            case nameof(PatronSettings.PendingHoldStatusLabel): row.PendingHoldStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldPlacedStatusLabel): row.HoldPlacedStatusLabel = normalized; break;
            case nameof(PatronSettings.ClosedStatusLabel): row.ClosedStatusLabel = normalized; break;
            case nameof(PatronSettings.RejectedStatusLabel): row.RejectedStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldCompletedStatusLabel): row.HoldCompletedStatusLabel = normalized; break;
            case nameof(PatronSettings.HoldNotPickedUpStatusLabel): row.HoldNotPickedUpStatusLabel = normalized; break;
            case nameof(PatronSettings.ManualStatusLabel): row.ManualStatusLabel = normalized; break;
            case nameof(PatronSettings.SilentStatusLabel): row.SilentStatusLabel = normalized; break;
        }
    }

    private static void ApplyDuplicateLabels(JsonElement section, PatronSettings row, bool system)
    {
        var labels = GetObject(section, "duplicateStatusLabels", "duplicateLabels");
        foreach (var pair in DuplicateLabelFields)
        {
            ApplyText(labels, pair.Key, value => SetPatronText(row, pair.Value, value, system));
        }
    }

    private static string? NormalizeSystemText(string? value) => Clean(value);

    private static string? NormalizeScopedText(string? value) =>
        Clean(value);

}
