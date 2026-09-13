namespace Asap.Web.Infrastructure.Data;

public sealed class Organization
{
    public int Id { get; set; }
    public required string DisplayName { get; set; }
    public string? Abbreviation { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastSyncedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class StaffUser
{
    public long Id { get; set; }
    public Guid? EntraTenantId { get; set; }
    public Guid? EntraObjectId { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? NormalizedUserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
    public string? NotificationEmail { get; set; }
    public required string Role { get; set; }
    public int OrganizationId { get; set; }
    public bool IsActive { get; set; }
    public bool WeeklyActionSummaryEnabled { get; set; }
    public string? WeeklyActionSummaryEmail { get; set; }
    public bool PurchaseReminderDefault { get; set; }
    public bool AdditionalCopyReminderDefault { get; set; }
    public bool DefaultMineUnclaimedFilter { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SystemSettings
{
    public int OrganizationId { get; set; }
    public string? StaffApplicationUrl { get; set; }
    public string? LeapBibUrlPattern { get; set; }
    public string? LeapPatronUrlPattern { get; set; }
    public string? MaterialTypeIconUrlPattern { get; set; }
    public string? SystemNotEnabledMessage { get; set; }
    public string? MisconfiguredMessage { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronEmbedAllowedOrigin
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public required string Origin { get; set; }
    public required string NormalizedOrigin { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class PolarisSettings
{
    public int OrganizationId { get; set; }
    public string? Host { get; set; }
    public string? AccessId { get; set; }
    public string? ProtectedApiKey { get; set; }
    public string? StaffDomain { get; set; }
    public string? AdminUser { get; set; }
    public string? ProtectedAdminPassword { get; set; }
    public int? WorkstationId { get; set; }
    public int? SystemPolarisUserId { get; set; }
    public int? OrganizationIdForRequests { get; set; }
    public int? PickupOrganizationId { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WorkflowSettings
{
    public int OrganizationId { get; set; }
    public int? SuggestionLimit { get; set; }
    public string? SuggestionLimitMessage { get; set; }
    public bool? OutstandingTimeoutEnabled { get; set; }
    public int? OutstandingTimeoutDays { get; set; }
    public bool? OutstandingTimeoutSendEmail { get; set; }
    public long? OutstandingTimeoutRejectionTemplateId { get; set; }
    public bool? HoldPickupTimeoutEnabled { get; set; }
    public int? HoldPickupTimeoutDays { get; set; }
    public bool? PendingHoldTimeoutEnabled { get; set; }
    public int? PendingHoldTimeoutDays { get; set; }
    public bool? AdditionalCopyTimeoutEnabled { get; set; }
    public int? AdditionalCopyTimeoutDays { get; set; }
    public bool? AutoPromote { get; set; }
    public bool? CommonAuthorsEnabled { get; set; }
    public string? CommonAuthorsLabel { get; set; }
    public string? CommonAuthorsHelp { get; set; }
    public string? CommonAuthorsMessage { get; set; }
    public bool? AllowPatronAutoholdOptOut { get; set; }
    public bool? AllowAnyRegisteredCardLogin { get; set; }
    public bool? PatronCodeEligibilityEnabled { get; set; }
    public string? PatronCodeEligibilityMessage { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronSettings
{
    public int OrganizationId { get; set; }
    public string? PageTitle { get; set; }
    public string? BarcodeLabel { get; set; }
    public string? PinLabel { get; set; }
    public string? LoginPrompt { get; set; }
    public string? LoginNote { get; set; }
    public string? SuggestionFormNote { get; set; }
    public string? NoEmailMessage { get; set; }
    public string? SuccessTitle { get; set; }
    public string? SuccessMessage { get; set; }
    public string? AlreadySubmittedMessage { get; set; }
    public string? EbookMessage { get; set; }
    public string? EaudiobookMessage { get; set; }
    public string? SuggestionStatusLabel { get; set; }
    public string? OutstandingPurchaseStatusLabel { get; set; }
    public string? PendingHoldStatusLabel { get; set; }
    public string? HoldPlacedStatusLabel { get; set; }
    public string? ClosedStatusLabel { get; set; }
    public string? RejectedStatusLabel { get; set; }
    public string? HoldCompletedStatusLabel { get; set; }
    public string? HoldNotPickedUpStatusLabel { get; set; }
    public string? ManualStatusLabel { get; set; }
    public string? SilentStatusLabel { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class EmailSettings
{
    public int OrganizationId { get; set; }
    public string? ProtectedServerToken { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class CommonCreatorSet
{
    public int OrganizationId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class CommonCreatorTerm
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public required string Value { get; set; }
    public int SortOrder { get; set; }
}

public sealed class PatronCodeEligibilitySet
{
    public int OrganizationId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronCodeEligibilityMember
{
    public int OrganizationId { get; set; }
    public required string PatronCodeId { get; set; }
}

public sealed class PublicationOptionSet
{
    public int OrganizationId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PublicationOption
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public required string OptionKey { get; set; }
    public required string Label { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ExternalSearchProvider
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public required string ProviderKey { get; set; }
    public bool IsEnabled { get; set; }
    public required string Label { get; set; }
    public required string UrlTemplate { get; set; }
    public int SortOrder { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ExternalSearchProviderOverride
{
    public int LibraryOrganizationId { get; set; }
    public long ExternalSearchProviderId { get; set; }
    public bool? IsEnabled { get; set; }
    public string? Label { get; set; }
    public string? UrlTemplate { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronCustomField
{
    public long Id { get; set; }
    public int LibraryOrganizationId { get; set; }
    public required string FieldKey { get; set; }
    public required string FieldType { get; set; }
    public required string Label { get; set; }
    public string? HelpText { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronCustomFieldOption
{
    public long Id { get; set; }
    public long PatronCustomFieldId { get; set; }
    public required string OptionKey { get; set; }
    public required string Label { get; set; }
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
}

public sealed class Branding
{
    public int OrganizationId { get; set; }
    public byte[]? LogoData { get; set; }
    public string? LogoContentType { get; set; }
    public string? LogoFileName { get; set; }
    public string? LogoAltText { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class MaterialFormat
{
    public long Id { get; set; }
    public int OwnerOrganizationId { get; set; }
    public required string Code { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; }
    public string? MessageBehavior { get; set; }
    public string? Message { get; set; }
    public string? TitleMode { get; set; }
    public string? TitleLabel { get; set; }
    public string? AuthorMode { get; set; }
    public string? AuthorLabel { get; set; }
    public string? IdentifierMode { get; set; }
    public string? IdentifierLabel { get; set; }
    public string? PublicationMode { get; set; }
    public string? PublicationLabel { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class MaterialFormatOverride
{
    public long Id { get; set; }
    public int LibraryOrganizationId { get; set; }
    public long MaterialFormatId { get; set; }
    public string? Label { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsEnabled { get; set; }
    public string? MessageBehavior { get; set; }
    public string? Message { get; set; }
    public string? TitleMode { get; set; }
    public string? TitleLabel { get; set; }
    public string? AuthorMode { get; set; }
    public string? AuthorLabel { get; set; }
    public string? IdentifierMode { get; set; }
    public string? IdentifierLabel { get; set; }
    public string? PublicationMode { get; set; }
    public string? PublicationLabel { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class MaterialFormatCustomFieldRule
{
    public long Id { get; set; }
    public int LibraryOrganizationId { get; set; }
    public long MaterialFormatId { get; set; }
    public long PatronCustomFieldId { get; set; }
    public required string Mode { get; set; }
    public string? LabelOverride { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class FormatAutoClaimRule
{
    public long Id { get; set; }
    public int LibraryOrganizationId { get; set; }
    public long MaterialFormatId { get; set; }
    public long? StaffUserId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DeactivatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PatronSession
{
    public long Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public required string Barcode { get; set; }
    public int? HomeOrganizationId { get; set; }
    public int? ExperienceOrganizationId { get; set; }
    public int EffectiveOrganizationId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
}

public sealed class TitleRequest
{
    public long Id { get; set; }
    public string? LegacyId { get; set; }
    public int LibraryOrganizationId { get; set; }
    public int? PatronOrganizationId { get; set; }
    public int? StaffLibraryOrganizationIdCreatedBy { get; set; }
    public required string Barcode { get; set; }
    public string? Email { get; set; }
    public string? NameFirst { get; set; }
    public string? NameLast { get; set; }
    public string? PatronCodeId { get; set; }
    public string? PatronCodeDescription { get; set; }
    public int? PreferredPickupBranchId { get; set; }
    public string? PreferredPickupBranchName { get; set; }
    public string? LibraryNameSnapshot { get; set; }
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? Identifier { get; set; }
    public string? Publication { get; set; }
    public DateOnly? ExactPublicationDate { get; set; }
    public string? CustomFieldsJson { get; set; }
    public bool AutoHold { get; set; }
    public long MaterialFormatId { get; set; }
    public required string Status { get; set; }
    public string? CloseReason { get; set; }
    public string? BibId { get; set; }
    public string? Notes { get; set; }
    public long? ClaimedByStaffUserId { get; set; }
    public string? ClaimedByDisplayName { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public string? ClaimType { get; set; }
    public long? ClaimRuleId { get; set; }
    public DateTime? LastPromoterCheckUtc { get; set; }
    public string? IsbnCheckStatus { get; set; }
    public string? IsbnCheckResult { get; set; }
    public int IsbnCheckRetryCount { get; set; }
    public string? IsbnCheckLastErrorCode { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class TitleRequestEvent
{
    public long Id { get; set; }
    public long TitleRequestId { get; set; }
    public required string EventType { get; set; }
    public string? Status { get; set; }
    public string? CloseReason { get; set; }
    public required string ActorType { get; set; }
    public long? StaffUserId { get; set; }
    public string? ActorName { get; set; }
    public string? Message { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class WorkflowTag
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string Label { get; set; }
    public int SortOrder { get; set; }
}

public sealed class TitleRequestWorkflowTag
{
    public long TitleRequestId { get; set; }
    public long WorkflowTagId { get; set; }
}

public sealed class EmailTemplate
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public required string TemplateKey { get; set; }
    public long? SourceTemplateId { get; set; }
    public string? DisplayName { get; set; }
    public string? SubjectTemplate { get; set; }
    public string? BodyTemplate { get; set; }
    public bool IsHidden { get; set; }
    public bool IsCustom { get; set; }
    public int SortOrder { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class EmailOutbox
{
    public long Id { get; set; }
    public int OrganizationId { get; set; }
    public string? BusinessKey { get; set; }
    public required string DeliveryClass { get; set; }
    public long? RecipientStaffUserId { get; set; }
    public Guid? RecipientEntraTenantId { get; set; }
    public Guid? RecipientEntraObjectId { get; set; }
    public int? AuthorizationOrganizationId { get; set; }
    public string? RecipientAddressKind { get; set; }
    public string? ToAddress { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? Subject { get; set; }
    public string? BodyText { get; set; }
    public string? BodyHtml { get; set; }
    public required string Status { get; set; }
    public string? SuppressionReason { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? SendingStartedUtc { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? SuppressedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class EmailDeliveryEvent
{
    public long Id { get; set; }
    public long? EmailOutboxId { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ProviderEventId { get; set; }
    public required string EventType { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class LegacyPocketBaseMapping
{
    public required string EntityType { get; set; }
    public required string PocketBaseId { get; set; }
    public long NewId { get; set; }
}
