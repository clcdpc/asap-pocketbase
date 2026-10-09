using System.Globalization;
using System.Text.Json;
using Asap.Web.Features.Staff;
using Asap.Web.Infrastructure.Data;

namespace Asap.Web.Features.Administration;

public sealed record WorkflowSettingsPatch
{
    public SuppliedValue<string?> SuggestionLimitMessage { get; init; }
    public SuppliedValue<string?> CommonAuthorsLabel { get; init; }
    public SuppliedValue<string?> CommonAuthorsHelp { get; init; }
    public SuppliedValue<string?> CommonAuthorsMessage { get; init; }
    public SuppliedValue<string?> PatronCodeEligibilityMessage { get; init; }
    public SuppliedValue<bool?> OutstandingTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> OutstandingTimeoutSendEmail { get; init; }
    public SuppliedValue<bool?> HoldPickupTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> PendingHoldTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> AdditionalCopyTimeoutEnabled { get; init; }
    public SuppliedValue<bool?> AutoPromote { get; init; }
    public SuppliedValue<bool?> CommonAuthorsEnabled { get; init; }
    public SuppliedValue<bool?> AllowPatronAutoholdOptOut { get; init; }
    public SuppliedValue<bool?> AllowAnyRegisteredCardLogin { get; init; }
    public SuppliedValue<bool?> PatronCodeEligibilityEnabled { get; init; }
    public SuppliedValue<int?> SuggestionLimit { get; init; }
    public SuppliedValue<int?> OutstandingTimeoutDays { get; init; }
    public SuppliedValue<int?> HoldPickupTimeoutDays { get; init; }
    public SuppliedValue<int?> PendingHoldTimeoutDays { get; init; }
    public SuppliedValue<int?> AdditionalCopyTimeoutDays { get; init; }
}

public sealed record PatronTextPatch
{
    public SuppliedValue<string?> PageTitle { get; init; }
    public SuppliedValue<string?> BarcodeLabel { get; init; }
    public SuppliedValue<string?> PinLabel { get; init; }
    public SuppliedValue<string?> LoginPrompt { get; init; }
    public SuppliedValue<string?> LoginNote { get; init; }
    public SuppliedValue<string?> SuggestionFormNote { get; init; }
    public SuppliedValue<string?> NoEmailMessage { get; init; }
    public SuppliedValue<string?> SuccessTitle { get; init; }
    public SuppliedValue<string?> SuccessMessage { get; init; }
    public SuppliedValue<string?> AlreadySubmittedMessage { get; init; }
    public SuppliedValue<string?> EbookMessage { get; init; }
    public SuppliedValue<string?> EaudiobookMessage { get; init; }
    public SuppliedValue<string?> SuggestionStatusLabel { get; init; }
    public SuppliedValue<string?> OutstandingPurchaseStatusLabel { get; init; }
    public SuppliedValue<string?> PendingHoldStatusLabel { get; init; }
    public SuppliedValue<string?> HoldPlacedStatusLabel { get; init; }
    public SuppliedValue<string?> ClosedStatusLabel { get; init; }
    public SuppliedValue<string?> RejectedStatusLabel { get; init; }
    public SuppliedValue<string?> HoldCompletedStatusLabel { get; init; }
    public SuppliedValue<string?> HoldNotPickedUpStatusLabel { get; init; }
    public SuppliedValue<string?> ManualStatusLabel { get; init; }
    public SuppliedValue<string?> SilentStatusLabel { get; init; }
}

public sealed record SystemSettingsPatch
{
    public SuppliedValue<string?> StaffUrl { get; init; }
    public SuppliedValue<string?> LeapBibUrlPattern { get; init; }
    public SuppliedValue<string?> LeapPatronUrlPattern { get; init; }
    public SuppliedValue<string?> FormatIconUrlPattern { get; init; }
    public SuppliedValue<string?> SystemNotEnabledMessage { get; init; }
    public SuppliedValue<string?> MisconfiguredMessage { get; init; }
    public bool HasChanges => StaffUrl.IsSupplied || LeapBibUrlPattern.IsSupplied || LeapPatronUrlPattern.IsSupplied ||
        FormatIconUrlPattern.IsSupplied || SystemNotEnabledMessage.IsSupplied || MisconfiguredMessage.IsSupplied;
}

public sealed record PolarisSettingsPatch
{
    public SuppliedValue<string?> Host { get; init; }
    public SuppliedValue<string?> AccessId { get; init; }
    public SuppliedValue<string?> StaffDomain { get; init; }
    public SuppliedValue<string?> AdminUser { get; init; }
    public SuppliedValue<int?> WorkstationId { get; init; }
    public SuppliedValue<int?> SystemPolarisUserId { get; init; }
    public SuppliedValue<string?> ApiKey { get; init; }
    public SuppliedValue<string?> AdminPassword { get; init; }
    public SuppliedValue<bool?> ClearApiKey { get; init; }
    public SuppliedValue<bool?> ClearAdminPassword { get; init; }
    public bool HasChanges => Host.IsSupplied || AccessId.IsSupplied || StaffDomain.IsSupplied ||
        AdminUser.IsSupplied || WorkstationId.IsSupplied || SystemPolarisUserId.IsSupplied ||
        ApiKey.IsSupplied || AdminPassword.IsSupplied || ClearApiKey.IsSupplied || ClearAdminPassword.IsSupplied;
}

public sealed record EmailSettingsPatch
{
    public SuppliedValue<string?> FromAddress { get; init; }
    public SuppliedValue<string?> FromName { get; init; }
    public SuppliedValue<string?> ServerToken { get; init; }
    public SuppliedValue<bool?> ClearServerToken { get; init; }
    public bool HasTokenChanges => ServerToken.IsSupplied || ClearServerToken.IsSupplied;
}

public sealed class AdministrationInputException(string message) : Exception(message);

public sealed record AdministrationSettingsCommand(
    LibraryScope Scope, string? Version, bool Reset, WorkflowSettingsPatch Workflow, PatronTextPatch Patron,
    JsonElement CollectionEdits, string? BindingError)
{
    public SystemSettingsPatch System { get; init; } = new();
    public PolarisSettingsPatch Polaris { get; init; } = new();
    public EmailSettingsPatch Email { get; init; } = new();
}

// Legacy collection shapes are interpreted by the settings collection mapper. Scalar patches
// are bound once here, with omission distinct from an explicit inherited/null value.
public static class AdministrationSettingsBinding
{
    private static readonly string[][] AmbiguousPropertyAliases =
    [
        ["orgId", "organizationId"],
        ["patron", "ui_text"],
        ["systemSettings", "system"],
        ["emails", "email"],
        ["patronEmbedAllowedOrigins", "origins"],
        ["enabledLibraryOrgIds", "enabledLibraries"],
        ["commonAuthorsList", "commonCreators", "commonCreatorsList"],
        ["allowedPatronCodeIds", "patronCodeIds"],
        ["publicationOptions", "publicationOptionSet"],
        ["providers", "externalSearchProviders"],
        ["formats", "materialFormats"],
        ["formatRules", "patronFormatRules"],
        ["customFields", "additionalFieldDefinitions"],
        ["autoClaimRules", "formatClaimRules"],
        ["templates", "emailTemplates"],
        ["duplicateStatusLabels", "duplicateLabels"],
        ["outstandingTimeoutRejectionTemplateId", "outstandingTimeoutRejectionTemplate"],
        ["format", "code"],
        ["fieldKey", "key"],
        ["fieldType", "type"],
        ["isEnabled", "enabled"],
        ["deleted", "delete"],
        ["reset", "useSystemDefault"],
        ["isCustom", "custom"],
        ["sourceTemplateId", "sourceId"],
        ["materialFormatId", "formatId"],
        ["staffUserId", "staffId"],
        ["subject", "subjectTemplate"],
        ["body", "bodyTemplate"],
        ["active", "isActive"],
        ["urlTemplate", "url"],
        ["altText", "logoAlt", "logoAltText"],
        ["contentType", "logoContentType"],
        ["fileName", "logoFileName"],
        ["clearLogo", "removeLogo"]
    ];

    private static readonly string[] WorkflowScalarProperties =
    [
        "suggestionLimitMessage", "commonAuthorsLabel", "commonAuthorsHelp", "commonAuthorsMessage",
        "patronCodeEligibilityMessage", "outstandingTimeoutEnabled", "outstandingTimeoutSendEmail",
        "holdPickupTimeoutEnabled", "pendingHoldTimeoutEnabled", "additionalCopyTimeoutEnabled",
        "autoPromote", "commonAuthorsEnabled", "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin",
        "patronCodeEligibilityEnabled", "suggestionLimit", "outstandingTimeoutDays", "holdPickupTimeoutDays",
        "pendingHoldTimeoutDays", "additionalCopyTimeoutDays", "outstandingTimeoutRejectionTemplateId",
        "outstandingTimeoutRejectionTemplate"
    ];

    private static readonly string[] PatronScalarProperties =
    [
        "pageTitle", "barcodeLabel", "pinLabel", "loginPrompt", "loginNote", "suggestionFormNote",
        "noEmailMessage", "successTitle", "successMessage", "alreadySubmittedMessage", "ebookMessage",
        "eaudiobookMessage", "suggestionStatusLabel", "outstandingPurchaseStatusLabel", "pendingHoldStatusLabel",
        "holdPlacedStatusLabel", "closedStatusLabel", "rejectedStatusLabel", "holdCompletedStatusLabel",
        "holdNotPickedUpStatusLabel", "manualStatusLabel", "silentStatusLabel"
    ];

    private static readonly string[] PolarisScalarProperties =
    [
        "host", "accessId", "staffDomain", "adminUser", "workstationId", "systemPolarisUserId", "userId",
        "apiKey", "adminPassword", "clearApiKey", "clearAdminPassword"
    ];

    private static readonly string[] BrandingScalarProperties =
    [
        "altText", "logoAlt", "logoAltText", "logoData", "contentType", "logoContentType", "fileName",
        "logoFileName", "clearLogo", "removeLogo"
    ];

    private static readonly string[] LegacyProviderScalarProperties = Enumerable.Range(1, 4)
        .SelectMany(index => new[]
        {
            $"externalSearch{index}Enabled", $"externalSearch{index}Label", $"externalSearch{index}UrlTemplate"
        })
        .ToArray();

    private static readonly (string Status, string Property)[] DuplicateStatusProperties =
    [
        ("suggestion", "suggestionStatusLabel"),
        ("outstanding_purchase", "outstandingPurchaseStatusLabel"),
        ("pending_hold", "pendingHoldStatusLabel"),
        ("hold_placed", "holdPlacedStatusLabel"),
        ("closed", "closedStatusLabel"),
        ("rejected", "rejectedStatusLabel"),
        ("hold_completed", "holdCompletedStatusLabel"),
        ("hold_not_picked_up", "holdNotPickedUpStatusLabel"),
        ("manual", "manualStatusLabel"),
        ("silent", "silentStatusLabel")
    ];

    public static LibraryScope DefaultScope(CurrentStaff actor) => actor.Role == StaffRole.SuperAdmin
        ? LibraryScope.System : LibraryScope.ForLibrary(actor.OrganizationId);

    public static AdministrationSettingsCommand Bind(CurrentStaff actor, JsonElement payload)
    {
        string? error = null;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            error = "settings_payload_invalid";
        }
        else if (HasAmbiguousProperties(payload))
        {
            error = "settings_payload_invalid";
        }
        var scopeValue = ReadScope(payload, "orgId", ref error);
        var scopeText = scopeValue.IsSupplied ? scopeValue.Value : ReadScope(payload, "organizationId", ref error).Value;
        if (!LibraryScope.TryParse(scopeText, DefaultScope(actor), out var scope))
        {
            error ??= "organization_invalid";
        }
        if (scope.Kind == LibraryScopeKind.System &&
            (HasPropertyNamed(payload, "customFields") || HasPropertyNamed(payload, "additionalFieldDefinitions") ||
             HasPropertyNamed(payload, "autoClaimRules") || HasPropertyNamed(payload, "formatClaimRules")))
        {
            error ??= "settings_library_only";
        }
        if (scope.Kind != LibraryScopeKind.System &&
            (HasPropertyNamed(payload, "enabledLibraryOrgIds") || HasPropertyNamed(payload, "enabledLibraries") ||
             HasPropertyNamed(payload, "patronEmbedAllowedOrigins") || HasPropertyNamed(payload, "origins")))
        {
            error ??= "settings_system_only";
        }
        var workflow = Section(payload, ref error, "workflow");
        var patron = Section(payload, ref error, "ui_text", "patron");
        var hasWorkflowSection = HasAnyDirectProperty(payload, "workflow");
        var hasPatronSection = HasAnyDirectProperty(payload, "ui_text", "patron");
        if ((hasWorkflowSection && HasAnyDirectProperty(payload, WorkflowScalarProperties)) ||
            (hasPatronSection && HasAnyDirectProperty(payload, PatronScalarProperties)))
        {
            error ??= "settings_payload_invalid";
        }
        if (hasWorkflowSection &&
            ((HasAnyDirectProperty(payload, "providers", "externalSearchProviders") &&
              HasAnyDirectProperty(workflow, "providers", "externalSearchProviders")) ||
             (HasAnyDirectProperty(payload, "commonAuthorsList", "commonCreators", "commonCreatorsList") &&
              HasAnyDirectProperty(workflow, "commonAuthorsList", "commonCreators", "commonCreatorsList")) ||
             (HasAnyDirectProperty(payload, "allowedPatronCodeIds", "patronCodeIds") &&
              HasAnyDirectProperty(workflow, "allowedPatronCodeIds", "patronCodeIds"))))
        {
            error ??= "settings_payload_invalid";
        }
        if (hasPatronSection &&
            ((HasAnyDirectProperty(payload, "formats", "materialFormats") &&
              HasAnyDirectProperty(patron, "formats", "materialFormats")) ||
             (HasAnyDirectProperty(payload, "formatLabels") && HasAnyDirectProperty(patron, "formatLabels")) ||
             (HasAnyDirectProperty(payload, "formatOrder") && HasAnyDirectProperty(patron, "formatOrder")) ||
             (HasAnyDirectProperty(payload, "availableFormats") && HasAnyDirectProperty(patron, "availableFormats")) ||
             (HasAnyDirectProperty(payload, "customFields", "additionalFieldDefinitions") &&
              HasAnyDirectProperty(patron, "customFields", "additionalFieldDefinitions")) ||
             (HasAnyDirectProperty(payload, "formatRules", "patronFormatRules") &&
              HasAnyDirectProperty(patron, "formatRules", "patronFormatRules")) ||
             (HasAnyDirectProperty(payload, "publicationOptions", "publicationOptionSet") &&
              HasAnyDirectProperty(patron, "publicationOptions", "publicationOptionSet")) ||
             HasAnyDirectProperty(payload, "duplicateStatusLabels", "duplicateLabels") ||
             (HasAnyDirectProperty(payload, "branding") &&
              (HasAnyDirectProperty(patron, "branding") || HasAnyDirectProperty(patron, BrandingScalarProperties))) ||
             HasAnyDirectProperty(payload, BrandingScalarProperties)))
        {
            error ??= "settings_payload_invalid";
        }
        if ((HasAnyDirectProperty(payload, "branding") && HasAnyDirectProperty(payload, BrandingScalarProperties)) ||
            (HasAnyDirectProperty(patron, "branding") && HasAnyDirectProperty(patron, BrandingScalarProperties)))
        {
            error ??= "settings_payload_invalid";
        }
        var hasModernFormats = HasAnyDirectProperty(payload, "formats", "materialFormats") ||
                               HasAnyDirectProperty(patron, "formats", "materialFormats");
        var hasLegacyFormatMaps = HasAnyDirectProperty(payload, "formatLabels", "formatOrder", "availableFormats") ||
                                  HasAnyDirectProperty(patron, "formatLabels", "formatOrder", "availableFormats");
        if (hasModernFormats && hasLegacyFormatMaps)
        {
            error ??= "settings_payload_invalid";
        }
        var hasModernProviders = HasAnyDirectProperty(payload, "providers", "externalSearchProviders") ||
                                 HasAnyDirectProperty(workflow, "providers", "externalSearchProviders");
        var hasLegacyProviders = HasAnyDirectProperty(payload, LegacyProviderScalarProperties) ||
                                 HasAnyDirectProperty(workflow, LegacyProviderScalarProperties);
        if (hasModernProviders && hasLegacyProviders)
        {
            error ??= "settings_payload_invalid";
        }
        var workflowPatch = new WorkflowSettingsPatch
        {
            SuggestionLimitMessage = Text(workflow, "suggestionLimitMessage", ref error),
            CommonAuthorsLabel = Text(workflow, "commonAuthorsLabel", ref error),
            CommonAuthorsHelp = Text(workflow, "commonAuthorsHelp", ref error),
            CommonAuthorsMessage = Text(workflow, "commonAuthorsMessage", ref error),
            PatronCodeEligibilityMessage = Text(workflow, "patronCodeEligibilityMessage", ref error),
            OutstandingTimeoutEnabled = Boolean(workflow, "outstandingTimeoutEnabled", ref error),
            OutstandingTimeoutSendEmail = Boolean(workflow, "outstandingTimeoutSendEmail", ref error),
            HoldPickupTimeoutEnabled = Boolean(workflow, "holdPickupTimeoutEnabled", ref error),
            PendingHoldTimeoutEnabled = Boolean(workflow, "pendingHoldTimeoutEnabled", ref error),
            AdditionalCopyTimeoutEnabled = Boolean(workflow, "additionalCopyTimeoutEnabled", ref error),
            AutoPromote = Boolean(workflow, "autoPromote", ref error),
            CommonAuthorsEnabled = Boolean(workflow, "commonAuthorsEnabled", ref error),
            AllowPatronAutoholdOptOut = Boolean(workflow, "allowPatronAutoholdOptOut", ref error),
            AllowAnyRegisteredCardLogin = Boolean(workflow, "allowAnyRegisteredCardLogin", ref error),
            PatronCodeEligibilityEnabled = Boolean(workflow, "patronCodeEligibilityEnabled", ref error),
            SuggestionLimit = Integer(workflow, "suggestionLimit", ref error),
            OutstandingTimeoutDays = Integer(workflow, "outstandingTimeoutDays", ref error),
            HoldPickupTimeoutDays = Integer(workflow, "holdPickupTimeoutDays", ref error),
            PendingHoldTimeoutDays = Integer(workflow, "pendingHoldTimeoutDays", ref error),
            AdditionalCopyTimeoutDays = Integer(workflow, "additionalCopyTimeoutDays", ref error),
        };
        var patronPatch = new PatronTextPatch
        {
            PageTitle = Text(patron, "pageTitle", ref error),
            BarcodeLabel = Text(patron, "barcodeLabel", ref error),
            PinLabel = Text(patron, "pinLabel", ref error),
            LoginPrompt = Text(patron, "loginPrompt", ref error),
            LoginNote = Text(patron, "loginNote", ref error),
            SuggestionFormNote = Text(patron, "suggestionFormNote", ref error),
            NoEmailMessage = Text(patron, "noEmailMessage", ref error),
            SuccessTitle = Text(patron, "successTitle", ref error),
            SuccessMessage = Text(patron, "successMessage", ref error),
            AlreadySubmittedMessage = Text(patron, "alreadySubmittedMessage", ref error),
            EbookMessage = Text(patron, "ebookMessage", ref error),
            EaudiobookMessage = Text(patron, "eaudiobookMessage", ref error),
            SuggestionStatusLabel = Text(patron, "suggestionStatusLabel", ref error),
            OutstandingPurchaseStatusLabel = Text(patron, "outstandingPurchaseStatusLabel", ref error),
            PendingHoldStatusLabel = Text(patron, "pendingHoldStatusLabel", ref error),
            HoldPlacedStatusLabel = Text(patron, "holdPlacedStatusLabel", ref error),
            ClosedStatusLabel = Text(patron, "closedStatusLabel", ref error),
            RejectedStatusLabel = Text(patron, "rejectedStatusLabel", ref error),
            HoldCompletedStatusLabel = Text(patron, "holdCompletedStatusLabel", ref error),
            HoldNotPickedUpStatusLabel = Text(patron, "holdNotPickedUpStatusLabel", ref error),
            ManualStatusLabel = Text(patron, "manualStatusLabel", ref error),
            SilentStatusLabel = Text(patron, "silentStatusLabel", ref error),
        };
        var system = Section(payload, ref error, "systemSettings", "system");
        var polaris = Section(payload, ref error, "polaris");
        var email = Section(payload, ref error, "emails", "email");
        var hasSystemSection = HasAnyDirectProperty(payload, "systemSettings", "system");
        var hasEmailSection = HasAnyDirectProperty(payload, "emails", "email");
        var hasPolarisSection = HasAnyDirectProperty(payload, "polaris");
        if (hasPolarisSection && HasAnyDirectProperty(payload, PolarisScalarProperties))
        {
            error ??= "settings_payload_invalid";
        }
        if (hasSystemSection &&
            ((HasAnyDirectProperty(payload, "enabledLibraryOrgIds", "enabledLibraries") &&
              HasAnyDirectProperty(system, "enabledLibraryOrgIds", "enabledLibraries")) ||
             (HasAnyDirectProperty(payload, "patronEmbedAllowedOrigins", "origins") &&
              HasAnyDirectProperty(system, "patronEmbedAllowedOrigins", "origins"))))
        {
            error ??= "settings_payload_invalid";
        }
        if (HasAnyDirectProperty(payload, "templates", "emailTemplates") && hasEmailSection &&
            HasLegacyEmailTemplateMembers(email))
        {
            error ??= "settings_payload_invalid";
        }
        var smtp = Section(payload, ref error, "smtp");
        var staffUrl = Text(system, "staffUrl", ref error);
        var rootStaffUrl = Text(payload, "staffUrl", ref error);
        var leapBibUrlPattern = Text(system, "leapBibUrlPattern", ref error);
        var rootLeapBibUrlPattern = Text(payload, "leapBibUrlPattern", ref error);
        var leapPatronUrlPattern = Text(system, "leapPatronUrlPattern", ref error);
        var rootLeapPatronUrlPattern = Text(payload, "leapPatronUrlPattern", ref error);
        var formatIconUrlPattern = Text(system, "formatIconUrlPattern", ref error);
        var rootFormatIconUrlPattern = Text(payload, "formatIconUrlPattern", ref error);
        var systemNotEnabledMessage = Text(system, "systemNotEnabledMessage", ref error);
        var rootSystemNotEnabledMessage = Text(payload, "systemNotEnabledMessage", ref error);
        var patronSystemNotEnabledMessage = Text(patron, "systemNotEnabledMessage", ref error);
        var misconfiguredMessage = Text(system, "misconfiguredMessage", ref error);
        var rootMisconfiguredMessage = Text(payload, "misconfiguredMessage", ref error);
        var patronMisconfiguredMessage = Text(patron, "misconfiguredMessage", ref error);
        var systemPolarisUserId = Integer(polaris, "systemPolarisUserId", ref error, int.MaxValue, "polaris_identity_invalid");
        var legacyPolarisUserId = Integer(polaris, "userId", ref error, int.MaxValue, "polaris_identity_invalid");
        var emailFromAddress = Text(email, "fromAddress", ref error);
        var smtpFromAddress = Text(smtp, "fromAddress", ref error);
        var rootFromAddress = Text(payload, "fromAddress", ref error);
        var emailFromName = Text(email, "fromName", ref error);
        var smtpFromName = Text(smtp, "fromName", ref error);
        var rootFromName = Text(payload, "fromName", ref error);
        var emailPostmarkToken = Text(email, "postmarkToken", ref error);
        var emailServerToken = Text(email, "serverToken", ref error);
        var rootPostmarkToken = Text(payload, "postmarkToken", ref error);
        var rootServerToken = Text(payload, "serverToken", ref error);
        var emailClearPostmarkToken = Boolean(email, "clearPostmarkToken", ref error);
        var emailClearServerToken = Boolean(email, "clearServerToken", ref error);
        var rootClearPostmarkToken = Boolean(payload, "clearPostmarkToken", ref error);
        var rootClearServerToken = Boolean(payload, "clearServerToken", ref error);
        if (ConflictingSuppliedValues(staffUrl, rootStaffUrl) ||
            ConflictingSuppliedValues(leapBibUrlPattern, rootLeapBibUrlPattern) ||
            ConflictingSuppliedValues(leapPatronUrlPattern, rootLeapPatronUrlPattern) ||
            ConflictingSuppliedValues(formatIconUrlPattern, rootFormatIconUrlPattern) ||
            ConflictingSuppliedValues(systemNotEnabledMessage, rootSystemNotEnabledMessage, patronSystemNotEnabledMessage) ||
            ConflictingSuppliedValues(misconfiguredMessage, rootMisconfiguredMessage, patronMisconfiguredMessage) ||
            ConflictingSuppliedValues(systemPolarisUserId, legacyPolarisUserId) ||
            ConflictingSuppliedValues(emailFromAddress, smtpFromAddress, rootFromAddress) ||
            ConflictingSuppliedValues(emailFromName, smtpFromName, rootFromName) ||
            ConflictingSuppliedValues(emailPostmarkToken, emailServerToken, rootPostmarkToken, rootServerToken) ||
            ConflictingSuppliedValues(emailClearPostmarkToken, emailClearServerToken, rootClearPostmarkToken, rootClearServerToken))
        {
            error ??= "settings_payload_invalid";
        }
        ValidateDuplicateStatusLabels(patron, ref error);
        ValidateLegacyProviderFallbacks(workflow, payload, ref error);
        var systemPatch = new SystemSettingsPatch
        {
            StaffUrl = Last(staffUrl, rootStaffUrl),
            LeapBibUrlPattern = Last(leapBibUrlPattern, rootLeapBibUrlPattern),
            LeapPatronUrlPattern = Last(leapPatronUrlPattern, rootLeapPatronUrlPattern),
            FormatIconUrlPattern = Last(formatIconUrlPattern, rootFormatIconUrlPattern),
            SystemNotEnabledMessage = Last(Last(systemNotEnabledMessage, rootSystemNotEnabledMessage), patronSystemNotEnabledMessage),
            MisconfiguredMessage = Last(Last(misconfiguredMessage, rootMisconfiguredMessage), patronMisconfiguredMessage)
        };
        var polarisPatch = new PolarisSettingsPatch
        {
            Host = Text(polaris, "host", ref error, "polaris_host_invalid"),
            AccessId = Text(polaris, "accessId", ref error),
            StaffDomain = Text(polaris, "staffDomain", ref error),
            AdminUser = Text(polaris, "adminUser", ref error),
            WorkstationId = Integer(polaris, "workstationId", ref error, int.MaxValue, "polaris_identity_invalid"),
            SystemPolarisUserId = Last(systemPolarisUserId, legacyPolarisUserId),
            ApiKey = Text(polaris, "apiKey", ref error),
            AdminPassword = Text(polaris, "adminPassword", ref error),
            ClearApiKey = Boolean(polaris, "clearApiKey", ref error),
            ClearAdminPassword = Boolean(polaris, "clearAdminPassword", ref error)
        };
        var emailPatch = new EmailSettingsPatch
        {
            FromAddress = Last(Last(emailFromAddress, smtpFromAddress), rootFromAddress),
            FromName = Last(Last(emailFromName, smtpFromName), rootFromName),
            ServerToken = Last(Last(emailPostmarkToken, emailServerToken), Last(rootPostmarkToken, rootServerToken)),
            ClearServerToken = AnyTrue(AnyTrue(emailClearPostmarkToken, emailClearServerToken),
                AnyTrue(rootClearPostmarkToken, rootClearServerToken))
        };
        if (new[] { "organizationIdForRequests", "requestingOrgId", "pickupOrganizationId", "pickupOrgId" }
            .Any(name => polaris.ValueKind == JsonValueKind.Object && polaris.TryGetProperty(name, out _)))
        {
            error = "polaris_context_retired";
        }
        if (scope.Kind != LibraryScopeKind.System && systemPatch.HasChanges)
        {
            error = "settings_system_only";
        }
        if (scope.Kind != LibraryScopeKind.System && polarisPatch.HasChanges)
        {
            error = "polaris_settings_system_only";
        }
        if (scope.Kind != LibraryScopeKind.System && (emailPatch.HasTokenChanges ||
            new[] { "postmarkToken", "serverToken", "clearPostmarkToken", "clearServerToken" }
                .Any(name => smtp.ValueKind == JsonValueKind.Object && smtp.TryGetProperty(name, out _))))
        {
            error = "postmark_token_system_only";
        }
        if (!string.IsNullOrWhiteSpace(polarisPatch.Host.Value) &&
            !Asap.Web.Features.Patron.PolarisConfigurationValidation.IsHostValid(polarisPatch.Host.Value.Trim()))
        {
            error ??= "polaris_host_invalid";
        }
        if (!string.IsNullOrWhiteSpace(emailPatch.ServerToken.Value) && emailPatch.ClearServerToken.Value == true)
        {
            error ??= "postmark_token_intent_conflict";
        }
        return new(scope, Text(payload, "version", ref error).Value,
            string.Equals(Text(payload, "action", ref error).Value, "reset", StringComparison.OrdinalIgnoreCase),
            workflowPatch, patronPatch, payload.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : payload.Clone(), error)
        {
            System = systemPatch, Polaris = polarisPatch, Email = emailPatch
        };
    }

    internal static bool HasAmbiguousProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(HasAmbiguousProperties);
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var properties = value.EnumerateObject().ToArray();
        if (properties.Any(property => !names.Add(property.Name)) ||
            AmbiguousPropertyAliases.Any(group => group.Count(name => names.Contains(name)) > 1))
        {
            return true;
        }

        return properties.Any(property => HasAmbiguousProperties(property.Value));
    }

    private static bool HasPropertyNamed(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(item => HasPropertyNamed(item, name));
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal) || HasPropertyNamed(property.Value, name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasAnyDirectProperty(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var name in names)
        {
            if (value.TryGetProperty(name, out _))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLegacyEmailTemplateMembers(JsonElement email)
    {
        if (email.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var property in email.EnumerateObject())
        {
            if (property.Name == "rejection_templates" ||
                property.Name is not ("fromAddress" or "fromName" or "postmarkToken" or "serverToken" or
                    "clearPostmarkToken" or "clearServerToken"))
            {
                return true;
            }
        }
        return false;
    }

    private static SuppliedValue<bool?> AnyTrue(SuppliedValue<bool?> first, SuppliedValue<bool?> last) =>
        new(first.IsSupplied || last.IsSupplied, first.Value == true || last.Value == true);

    private static SuppliedValue<T> Last<T>(SuppliedValue<T> first, SuppliedValue<T> last) =>
        last.IsSupplied ? last : first;

    private static bool ConflictingSuppliedValues<T>(params SuppliedValue<T>[] values)
    {
        var supplied = values.Where(value => value.IsSupplied).ToArray();
        return supplied.Length > 1 && supplied.Skip(1).Any(value =>
            !EqualityComparer<T>.Default.Equals(supplied[0].Value, value.Value));
    }

    private static void ValidateDuplicateStatusLabels(JsonElement patron, ref string? error)
    {
        if (patron.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        JsonElement labels = default;
        if (patron.TryGetProperty("duplicateStatusLabels", out var currentLabels))
        {
            labels = currentLabels;
        }
        else if (patron.TryGetProperty("duplicateLabels", out currentLabels))
        {
            labels = currentLabels;
        }
        else
        {
            return;
        }

        if (labels.ValueKind != JsonValueKind.Object)
        {
            error ??= "settings_payload_invalid";
            return;
        }

        var knownStatuses = DuplicateStatusProperties.ToDictionary(
            item => item.Status,
            item => item.Property,
            StringComparer.Ordinal);
        foreach (var label in labels.EnumerateObject())
        {
            if (!knownStatuses.TryGetValue(label.Name, out var scalarProperty) ||
                label.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                patron.TryGetProperty(scalarProperty, out _))
            {
                error ??= "settings_payload_invalid";
            }
        }
    }

    private static void ValidateLegacyProviderFallbacks(JsonElement workflow, JsonElement payload, ref string? error)
    {
        for (var index = 1; index <= 4; index++)
        {
            var enabledKey = $"externalSearch{index}Enabled";
            var labelKey = $"externalSearch{index}Label";
            var urlKey = $"externalSearch{index}UrlTemplate";
            if (ConflictingSuppliedValues(Boolean(workflow, enabledKey, ref error), Boolean(payload, enabledKey, ref error)) ||
                ConflictingSuppliedValues(Text(workflow, labelKey, ref error), Text(payload, labelKey, ref error)) ||
                ConflictingSuppliedValues(Text(workflow, urlKey, ref error), Text(payload, urlKey, ref error)))
            {
                error ??= "settings_payload_invalid";
            }
        }
    }

    private static SuppliedValue<string?> ReadScope(JsonElement payload, string name, ref string? error)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number))
        {
            error ??= "organization_invalid";
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.Number ? value.GetRawText() :
            value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static JsonElement Section(JsonElement root, ref string? error, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var section))
            {
                if (section.ValueKind != JsonValueKind.Object)
                {
                    error ??= "settings_payload_invalid";
                    return default;
                }
                return section;
            }
        }
        return root;
    }

    private static SuppliedValue<string?> Text(JsonElement root, string name, ref string? error, string errorCode = "settings_payload_invalid")
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            error ??= errorCode;
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.String ? value.GetString() : null);
    }

    private static SuppliedValue<bool?> Boolean(JsonElement root, string name, ref string? error)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
        {
            error ??= "settings_payload_invalid";
            return default;
        }
        return new(true, value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean());
    }

    private static SuppliedValue<int?> Integer(JsonElement root, string name, ref string? error, int? upperLimit = null, string errorCode = "settings_payload_invalid")
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return default;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new(true, null);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ||
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            var maximum = upperLimit ?? (name == "suggestionLimit" ? 1000 : 3650);
            if (number <= 0 || number > maximum)
            {
                error ??= errorCode;
                return default;
            }
            return new(true, number);
        }
        error ??= errorCode;
        return default;
    }
}
