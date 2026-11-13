using System.Text.Json.Serialization;

namespace Asap.Web.Features.Administration;

public sealed record SettingsProviderView
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("key")]
    public string? Key { get; init; }
    [JsonPropertyName("isEnabled")]
    public bool? IsEnabled { get; init; }
    [JsonPropertyName("label")]
    public string? Label { get; init; }
    [JsonPropertyName("urlTemplate")]
    public string? UrlTemplate { get; init; }
    [JsonPropertyName("sortOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SortOrder { get; init; }
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record SettingsFormatView
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("materialFormatId")]
    public string? MaterialFormatId { get; init; }
    [JsonPropertyName("ownerOrganizationId")]
    public string OwnerOrganizationId { get; init; } = string.Empty;
    [JsonPropertyName("code")]
    public string? Code { get; init; }
    [JsonPropertyName("label")]
    public string? Label { get; init; }
    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; init; }
    [JsonPropertyName("isEnabled")]
    public bool? IsEnabled { get; init; }
    [JsonPropertyName("messageBehavior")]
    public string? MessageBehavior { get; init; }
    [JsonPropertyName("message")]
    public string? Message { get; init; }
    [JsonPropertyName("titleMode")]
    public string? TitleMode { get; init; }
    [JsonPropertyName("titleLabel")]
    public string? TitleLabel { get; init; }
    [JsonPropertyName("authorMode")]
    public string? AuthorMode { get; init; }
    [JsonPropertyName("authorLabel")]
    public string? AuthorLabel { get; init; }
    [JsonPropertyName("identifierMode")]
    public string? IdentifierMode { get; init; }
    [JsonPropertyName("identifierLabel")]
    public string? IdentifierLabel { get; init; }
    [JsonPropertyName("publicationMode")]
    public string? PublicationMode { get; init; }
    [JsonPropertyName("publicationLabel")]
    public string? PublicationLabel { get; init; }
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record SettingsSetView<T>(
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("values")] IReadOnlyList<T> Values);

public sealed record PublicationOptionSettingsView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("sortOrder")] int SortOrder);

public sealed record CommonCreatorSettingsView(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("sortOrder")] int SortOrder);

public sealed record EmailTemplateSettingsView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organizationId")] string OrganizationId,
    [property: JsonPropertyName("templateKey")] string TemplateKey,
    [property: JsonPropertyName("sourceTemplateId")] string? SourceTemplateId,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("isCustom")] bool IsCustom,
    [property: JsonPropertyName("sortOrder")] int SortOrder,
    [property: JsonPropertyName("version")] string Version);

public sealed record SettingsScopeView(
    [property: JsonPropertyName("workflow")] WorkflowSettingsView? Workflow,
    [property: JsonPropertyName("patron")] PatronTextSettingsView? Patron,
    [property: JsonPropertyName("email")] EmailSettingsView? Email,
    [property: JsonPropertyName("publicationOptions")] SettingsSetView<PublicationOptionSettingsView> PublicationOptions,
    [property: JsonPropertyName("commonCreators")] SettingsSetView<CommonCreatorSettingsView> CommonCreators,
    [property: JsonPropertyName("allowedPatronCodeIds")] SettingsSetView<int> AllowedPatronCodeIds,
    [property: JsonPropertyName("providers")] IReadOnlyList<SettingsProviderView> Providers,
    [property: JsonPropertyName("formats")] IReadOnlyList<SettingsFormatView> Formats,
    [property: JsonPropertyName("templates")] IReadOnlyList<EmailTemplateSettingsView> Templates,
    [property: JsonPropertyName("branding")] BrandingSettingsView Branding)
{
    [JsonPropertyName("catalog")]
    public Staff.CatalogPresentationValues? Catalog { get; init; }
}

public sealed record SystemSettingsView
{
    [JsonPropertyName("staffUrl")]
    public string? StaffUrl { get; init; }
    [JsonPropertyName("leapBibUrlPattern")]
    public string? LeapBibUrlPattern { get; init; }
    [JsonPropertyName("leapPatronUrlPattern")]
    public string? LeapPatronUrlPattern { get; init; }
    [JsonPropertyName("formatIconUrlPattern")]
    public string? FormatIconUrlPattern { get; init; }
    [JsonPropertyName("systemNotEnabledMessage")]
    public string? SystemNotEnabledMessage { get; init; }
    [JsonPropertyName("misconfiguredMessage")]
    public string? MisconfiguredMessage { get; init; }
    [JsonPropertyName("patronEmbedAllowedOrigins")]
    public IReadOnlyList<string> PatronEmbedAllowedOrigins { get; init; } = [];
    [JsonPropertyName("enabledLibraryOrgIds")]
    public IReadOnlyList<int> EnabledLibraryOrgIds { get; init; } = [];
    [JsonPropertyName("libraryOrgIds")]
    public IReadOnlyList<int> LibraryOrgIds { get; init; } = [];
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record PolarisSettingsView
{
    [JsonPropertyName("host")]
    public string? Host { get; init; }
    [JsonPropertyName("accessId")]
    public string? AccessId { get; init; }
    [JsonPropertyName("staffDomain")]
    public string? StaffDomain { get; init; }
    [JsonPropertyName("adminUser")]
    public string? AdminUser { get; init; }
    [JsonPropertyName("workstationId")]
    public int? WorkstationId { get; init; }
    [JsonPropertyName("systemPolarisUserId")]
    public int? SystemPolarisUserId { get; init; }
    [JsonPropertyName("hasApiKey")]
    public bool HasApiKey { get; init; }
    [JsonPropertyName("hasAdminPassword")]
    public bool HasAdminPassword { get; init; }
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record WorkflowSettingsView
{
    [JsonPropertyName("suggestionLimitMessage")]
    public string? SuggestionLimitMessage { get; init; }
    [JsonPropertyName("commonAuthorsLabel")]
    public string? CommonAuthorsLabel { get; init; }
    [JsonPropertyName("commonAuthorsHelp")]
    public string? CommonAuthorsHelp { get; init; }
    [JsonPropertyName("commonAuthorsMessage")]
    public string? CommonAuthorsMessage { get; init; }
    [JsonPropertyName("patronCodeEligibilityMessage")]
    public string? PatronCodeEligibilityMessage { get; init; }
    [JsonPropertyName("outstandingTimeoutEnabled")]
    public bool? OutstandingTimeoutEnabled { get; init; }
    [JsonPropertyName("outstandingTimeoutSendEmail")]
    public bool? OutstandingTimeoutSendEmail { get; init; }
    [JsonPropertyName("holdPickupTimeoutEnabled")]
    public bool? HoldPickupTimeoutEnabled { get; init; }
    [JsonPropertyName("pendingHoldTimeoutEnabled")]
    public bool? PendingHoldTimeoutEnabled { get; init; }
    [JsonPropertyName("additionalCopyTimeoutEnabled")]
    public bool? AdditionalCopyTimeoutEnabled { get; init; }
    [JsonPropertyName("autoPromote")]
    public bool? AutoPromote { get; init; }
    [JsonPropertyName("commonAuthorsEnabled")]
    public bool? CommonAuthorsEnabled { get; init; }
    [JsonPropertyName("allowPatronAutoholdOptOut")]
    public bool? AllowPatronAutoholdOptOut { get; init; }
    [JsonPropertyName("allowAnyRegisteredCardLogin")]
    public bool? AllowAnyRegisteredCardLogin { get; init; }
    [JsonPropertyName("patronCodeEligibilityEnabled")]
    public bool? PatronCodeEligibilityEnabled { get; init; }
    [JsonPropertyName("suggestionLimit")]
    public int? SuggestionLimit { get; init; }
    [JsonPropertyName("outstandingTimeoutDays")]
    public int? OutstandingTimeoutDays { get; init; }
    [JsonPropertyName("holdPickupTimeoutDays")]
    public int? HoldPickupTimeoutDays { get; init; }
    [JsonPropertyName("pendingHoldTimeoutDays")]
    public int? PendingHoldTimeoutDays { get; init; }
    [JsonPropertyName("additionalCopyTimeoutDays")]
    public int? AdditionalCopyTimeoutDays { get; init; }
    [JsonPropertyName("outstandingTimeoutRejectionTemplateId")]
    public string? OutstandingTimeoutRejectionTemplateId { get; init; }
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record EmailSettingsView
{
    [JsonPropertyName("fromAddress")]
    public string? FromAddress { get; init; }
    [JsonPropertyName("fromName")]
    public string? FromName { get; init; }
    [JsonPropertyName("hasPostmarkToken")]
    public bool HasPostmarkToken { get; init; }
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
}

public sealed record BrandingSettingsView
{
    [JsonPropertyName("hasLogo")]
    public bool HasLogo { get; init; }
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }
    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }
    [JsonPropertyName("altText")]
    public string? AltText { get; init; }
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed record PatronTextSettingsView
{
    [JsonPropertyName("pageTitle")]
    public string? PageTitle { get; init; }
    [JsonPropertyName("barcodeLabel")]
    public string? BarcodeLabel { get; init; }
    [JsonPropertyName("pinLabel")]
    public string? PinLabel { get; init; }
    [JsonPropertyName("loginPrompt")]
    public string? LoginPrompt { get; init; }
    [JsonPropertyName("loginNote")]
    public string? LoginNote { get; init; }
    [JsonPropertyName("suggestionFormNote")]
    public string? SuggestionFormNote { get; init; }
    [JsonPropertyName("noEmailMessage")]
    public string? NoEmailMessage { get; init; }
    [JsonPropertyName("successTitle")]
    public string? SuccessTitle { get; init; }
    [JsonPropertyName("successMessage")]
    public string? SuccessMessage { get; init; }
    [JsonPropertyName("alreadySubmittedMessage")]
    public string? AlreadySubmittedMessage { get; init; }
    [JsonPropertyName("ebookMessage")]
    public string? EbookMessage { get; init; }
    [JsonPropertyName("eaudiobookMessage")]
    public string? EaudiobookMessage { get; init; }
    [JsonPropertyName("suggestionStatusLabel")]
    public string? SuggestionStatusLabel { get; init; }
    [JsonPropertyName("outstandingPurchaseStatusLabel")]
    public string? OutstandingPurchaseStatusLabel { get; init; }
    [JsonPropertyName("pendingHoldStatusLabel")]
    public string? PendingHoldStatusLabel { get; init; }
    [JsonPropertyName("holdPlacedStatusLabel")]
    public string? HoldPlacedStatusLabel { get; init; }
    [JsonPropertyName("closedStatusLabel")]
    public string? ClosedStatusLabel { get; init; }
    [JsonPropertyName("rejectedStatusLabel")]
    public string? RejectedStatusLabel { get; init; }
    [JsonPropertyName("holdCompletedStatusLabel")]
    public string? HoldCompletedStatusLabel { get; init; }
    [JsonPropertyName("holdNotPickedUpStatusLabel")]
    public string? HoldNotPickedUpStatusLabel { get; init; }
    [JsonPropertyName("manualStatusLabel")]
    public string? ManualStatusLabel { get; init; }
    [JsonPropertyName("silentStatusLabel")]
    public string? SilentStatusLabel { get; init; }
}
