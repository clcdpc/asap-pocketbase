CREATE TABLE [asap].[SystemSettings]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_SystemSettings] PRIMARY KEY,
    [StaffApplicationUrl] nvarchar(2048) NULL,
    [LeapBibUrlPattern] nvarchar(2048) NULL,
    [LeapPatronUrlPattern] nvarchar(2048) NULL,
    [MaterialTypeIconUrlPattern] nvarchar(2048) NULL,
    [SystemNotEnabledMessage] nvarchar(max) NULL,
    [MisconfiguredMessage] nvarchar(max) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_SystemSettings_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_SystemSettings_SystemOnly] CHECK ([OrganizationId] = 1)
);
GO

CREATE TABLE [asap].[PatronEmbedAllowedOrigin]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PatronEmbedAllowedOrigin] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [Origin] nvarchar(2048) NOT NULL,
    [NormalizedOrigin] nvarchar(2048) NOT NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    CONSTRAINT [FK_PatronEmbedAllowedOrigin_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_PatronEmbedAllowedOrigin_SystemOnly] CHECK ([OrganizationId] = 1),
    CONSTRAINT [UQ_PatronEmbedAllowedOrigin_NormalizedOrigin] UNIQUE ([NormalizedOrigin])
);
GO

CREATE TABLE [asap].[PolarisSettings]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_PolarisSettings] PRIMARY KEY,
    [Host] nvarchar(2048) NULL,
    [AccessId] nvarchar(256) NULL,
    [ProtectedApiKey] nvarchar(max) NULL,
    [StaffDomain] nvarchar(256) NULL,
    [AdminUser] nvarchar(256) NULL,
    [ProtectedAdminPassword] nvarchar(max) NULL,
    [WorkstationId] int NULL,
    [SystemPolarisUserId] int NULL,
    [OrganizationIdForRequests] int NULL,
    [PickupOrganizationId] int NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_PolarisSettings_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_PolarisSettings_SystemOnly] CHECK ([OrganizationId] = 1)
);
GO

CREATE TABLE [asap].[WorkflowSettings]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_WorkflowSettings] PRIMARY KEY,
    [SuggestionLimit] int NULL,
    [SuggestionLimitMessage] nvarchar(max) NULL,
    [OutstandingTimeoutEnabled] bit NULL,
    [OutstandingTimeoutDays] int NULL,
    [OutstandingTimeoutSendEmail] bit NULL,
    [OutstandingTimeoutRejectionTemplateId] bigint NULL,
    [HoldPickupTimeoutEnabled] bit NULL,
    [HoldPickupTimeoutDays] int NULL,
    [PendingHoldTimeoutEnabled] bit NULL,
    [PendingHoldTimeoutDays] int NULL,
    [AdditionalCopyTimeoutEnabled] bit NULL,
    [AdditionalCopyTimeoutDays] int NULL,
    [AutoPromote] bit NULL,
    [CommonAuthorsEnabled] bit NULL,
    [CommonAuthorsLabel] nvarchar(256) NULL,
    [CommonAuthorsHelp] nvarchar(max) NULL,
    [CommonAuthorsMessage] nvarchar(max) NULL,
    [AllowPatronAutoholdOptOut] bit NULL,
    [AllowAnyRegisteredCardLogin] bit NULL,
    [PatronCodeEligibilityEnabled] bit NULL,
    [PatronCodeEligibilityMessage] nvarchar(max) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_WorkflowSettings_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_WorkflowSettings_TimeoutTemplate] FOREIGN KEY ([OutstandingTimeoutRejectionTemplateId]) REFERENCES [asap].[EmailTemplate]([Id]),
    CONSTRAINT [CK_WorkflowSettings_SuggestionLimit] CHECK ([SuggestionLimit] IS NULL OR [SuggestionLimit] BETWEEN 1 AND 1000),
    CONSTRAINT [CK_WorkflowSettings_TimeoutDays] CHECK
    (
        ([OutstandingTimeoutDays] IS NULL OR [OutstandingTimeoutDays] BETWEEN 1 AND 3650) AND
        ([HoldPickupTimeoutDays] IS NULL OR [HoldPickupTimeoutDays] BETWEEN 1 AND 3650) AND
        ([PendingHoldTimeoutDays] IS NULL OR [PendingHoldTimeoutDays] BETWEEN 1 AND 3650) AND
        ([AdditionalCopyTimeoutDays] IS NULL OR [AdditionalCopyTimeoutDays] BETWEEN 1 AND 3650)
    )
);
GO

CREATE TABLE [asap].[PatronSettings]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_PatronSettings] PRIMARY KEY,
    [PageTitle] nvarchar(256) NULL,
    [BarcodeLabel] nvarchar(128) NULL,
    [PinLabel] nvarchar(128) NULL,
    [LoginPrompt] nvarchar(max) NULL,
    [LoginNote] nvarchar(max) NULL,
    [SuggestionFormNote] nvarchar(max) NULL,
    [NoEmailMessage] nvarchar(max) NULL,
    [SuccessTitle] nvarchar(256) NULL,
    [SuccessMessage] nvarchar(max) NULL,
    [AlreadySubmittedMessage] nvarchar(max) NULL,
    [EbookMessage] nvarchar(max) NULL,
    [EaudiobookMessage] nvarchar(max) NULL,
    [SuggestionStatusLabel] nvarchar(128) NULL,
    [OutstandingPurchaseStatusLabel] nvarchar(128) NULL,
    [PendingHoldStatusLabel] nvarchar(128) NULL,
    [HoldPlacedStatusLabel] nvarchar(128) NULL,
    [ClosedStatusLabel] nvarchar(128) NULL,
    [RejectedStatusLabel] nvarchar(128) NULL,
    [HoldCompletedStatusLabel] nvarchar(128) NULL,
    [HoldNotPickedUpStatusLabel] nvarchar(128) NULL,
    [ManualStatusLabel] nvarchar(128) NULL,
    [SilentStatusLabel] nvarchar(128) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_PatronSettings_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id])
);
GO

CREATE TABLE [asap].[EmailSettings]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_EmailSettings] PRIMARY KEY,
    [ProtectedServerToken] nvarchar(max) NULL,
    [FromAddress] nvarchar(320) NULL,
    [FromName] nvarchar(256) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_EmailSettings_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id])
);
GO
