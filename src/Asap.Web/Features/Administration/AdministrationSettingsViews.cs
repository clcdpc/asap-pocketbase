using System.Text.Json.Serialization;

namespace Asap.Web.Features.Administration;

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
