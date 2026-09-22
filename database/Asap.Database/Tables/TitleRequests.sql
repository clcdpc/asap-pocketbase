CREATE TABLE [asap].[TitleRequest]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TitleRequest] PRIMARY KEY,
    [LegacyId] nvarchar(100) NULL,
    [LibraryOrganizationId] int NOT NULL,
    [PatronOrganizationId] int NULL,
    [StaffLibraryOrganizationIdCreatedBy] int NULL,
    [Barcode] nvarchar(50) NOT NULL,
    [Email] nvarchar(320) NULL,
    [NameFirst] nvarchar(256) NULL,
    [NameLast] nvarchar(256) NULL,
    [PatronCodeId] nvarchar(100) NULL,
    [PatronCodeDescription] nvarchar(256) NULL,
    [PreferredPickupBranchId] int NULL,
    [PreferredPickupBranchName] nvarchar(256) NULL,
    [LibraryNameSnapshot] nvarchar(256) NULL,
    [Title] nvarchar(500) NOT NULL,
    [Author] nvarchar(500) NULL,
    [Identifier] nvarchar(100) NULL,
    [Publication] nvarchar(200) NULL,
    [ExactPublicationDate] date NULL,
    [CustomFieldsJson] nvarchar(max) NULL,
    [AutoHold] bit NOT NULL,
    [MaterialFormatId] bigint NOT NULL,
    [Status] nvarchar(32) NOT NULL,
    [CloseReason] nvarchar(64) NULL,
    [BibId] nvarchar(100) NULL,
    [Notes] nvarchar(max) NULL,
    [ClaimedByStaffUserId] bigint NULL,
    [ClaimedByDisplayName] nvarchar(256) NULL,
    [ClaimedAtUtc] datetime2(7) NULL,
    [ClaimType] nvarchar(32) NULL,
    [ClaimRuleId] bigint NULL,
    [LastPromoterCheckUtc] datetime2(7) NULL,
    [IsbnCheckStatus] nvarchar(32) NULL,
    [IsbnCheckResult] nvarchar(max) NULL,
    [IsbnCheckRetryCount] int NOT NULL CONSTRAINT [DF_TitleRequest_IsbnCheckRetryCount] DEFAULT (0),
    [IsbnCheckLastErrorCode] nvarchar(100) NULL,
    [LastCheckedUtc] datetime2(7) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_TitleRequest_LibraryOrganization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_TitleRequest_PatronOrganization] FOREIGN KEY ([PatronOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_TitleRequest_StaffLibraryOrganization] FOREIGN KEY ([StaffLibraryOrganizationIdCreatedBy]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_TitleRequest_MaterialFormat] FOREIGN KEY ([MaterialFormatId]) REFERENCES [asap].[MaterialFormat]([Id]),
    CONSTRAINT [FK_TitleRequest_ClaimedByStaffUser] FOREIGN KEY ([ClaimedByStaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_TitleRequest_ClaimRule] FOREIGN KEY ([ClaimRuleId]) REFERENCES [asap].[FormatAutoClaimRule]([Id]),
    CONSTRAINT [CK_TitleRequest_LibraryNotSystem] CHECK ([LibraryOrganizationId] <> 1),
    CONSTRAINT [CK_TitleRequest_CustomFieldsJson] CHECK ([CustomFieldsJson] IS NULL OR ISJSON([CustomFieldsJson]) = 1),
    CONSTRAINT [CK_TitleRequest_Status] CHECK ([Status] IN (N'suggestion', N'outstanding_purchase', N'pending_hold', N'hold_placed', N'closed')),
    CONSTRAINT [CK_TitleRequest_CloseReason] CHECK ([CloseReason] IS NULL OR [CloseReason] IN (N'rejected', N'Silently Closed', N'hold_completed', N'hold_not_picked_up', N'hold_unclaimed', N'hold_cancelled', N'hold_expired', N'duplicate_hold', N'manual', N'purchased_no_hold')),
    CONSTRAINT [CK_TitleRequest_ClosedReason] CHECK (([Status] = N'closed' AND [CloseReason] IS NOT NULL) OR ([Status] <> N'closed' AND [CloseReason] IS NULL)),
    CONSTRAINT [CK_TitleRequest_ClaimTuple] CHECK
    (
        ([ClaimedByStaffUserId] IS NULL AND [ClaimedByDisplayName] IS NULL AND [ClaimedAtUtc] IS NULL AND [ClaimType] IS NULL AND [ClaimRuleId] IS NULL) OR
        ([ClaimedByDisplayName] IS NOT NULL AND [ClaimedAtUtc] IS NOT NULL AND [ClaimType] IS NOT NULL AND [ClaimType] IN (N'manual', N'automatic_format_rule', N'legacy'))
    ),
    CONSTRAINT [CK_TitleRequest_AutomaticClaimRule] CHECK ([ClaimType] <> N'automatic_format_rule' OR ([ClaimedByStaffUserId] IS NOT NULL AND [ClaimRuleId] IS NOT NULL)),
    CONSTRAINT [CK_TitleRequest_IsbnCheckStatus] CHECK ([IsbnCheckStatus] IS NULL OR [IsbnCheckStatus] IN (N'pending', N'found', N'not_found', N'skipped_no_isbn', N'error_max_retries')),
    CONSTRAINT [CK_TitleRequest_IsbnRetryCount] CHECK ([IsbnCheckRetryCount] >= 0),
    CONSTRAINT [CK_TitleRequest_FoundHasBib] CHECK ([IsbnCheckStatus] <> N'found' OR NULLIF(LTRIM(RTRIM([BibId])), N'') IS NOT NULL),
    CONSTRAINT [CK_TitleRequest_Timestamps] CHECK ([UpdatedUtc] >= [CreatedUtc])
);
GO

CREATE INDEX [IX_TitleRequest_LibraryStatusCreated]
    ON [asap].[TitleRequest]([LibraryOrganizationId], [Status], [CreatedUtc], [Id]);
GO

CREATE INDEX [IX_TitleRequest_PatronLimit]
    ON [asap].[TitleRequest]([LibraryOrganizationId], [Barcode], [CreatedUtc], [Id]);
GO

CREATE INDEX [IX_TitleRequest_PatronIdentifier]
    ON [asap].[TitleRequest]([LibraryOrganizationId], [Barcode], [Identifier]) WHERE [Identifier] IS NOT NULL;
GO

CREATE INDEX [IX_TitleRequest_PatronTitleFormat]
    ON [asap].[TitleRequest]([LibraryOrganizationId], [Barcode], [Title], [MaterialFormatId]);
GO

CREATE INDEX [IX_TitleRequest_IdentifierProcessing]
    ON [asap].[TitleRequest]([LibraryOrganizationId], [IsbnCheckStatus], [CreatedUtc], [Id]);
GO

CREATE TABLE [asap].[TitleRequestEvent]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TitleRequestEvent] PRIMARY KEY,
    [TitleRequestId] bigint NOT NULL,
    [EventType] nvarchar(64) NOT NULL,
    [Status] nvarchar(32) NULL,
    [CloseReason] nvarchar(64) NULL,
    [ActorType] nvarchar(16) NOT NULL,
    [StaffUserId] bigint NULL,
    [ActorName] nvarchar(256) NULL,
    [Message] nvarchar(max) NULL,
    [MetadataJson] nvarchar(max) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    CONSTRAINT [FK_TitleRequestEvent_TitleRequest] FOREIGN KEY ([TitleRequestId]) REFERENCES [asap].[TitleRequest]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_TitleRequestEvent_StaffUser] FOREIGN KEY ([StaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [CK_TitleRequestEvent_ActorType] CHECK ([ActorType] IN (N'system', N'staff', N'patron')),
    CONSTRAINT [CK_TitleRequestEvent_Status] CHECK ([Status] IS NULL OR [Status] IN (N'suggestion', N'outstanding_purchase', N'pending_hold', N'hold_placed', N'closed')),
    CONSTRAINT [CK_TitleRequestEvent_MetadataJson] CHECK ([MetadataJson] IS NULL OR ISJSON([MetadataJson]) = 1)
);
GO

CREATE INDEX [IX_TitleRequestEvent_RequestCreated]
    ON [asap].[TitleRequestEvent]([TitleRequestId], [CreatedUtc], [Id]);
GO

CREATE TABLE [asap].[WorkflowTag]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_WorkflowTag] PRIMARY KEY,
    [Code] nvarchar(100) NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [SortOrder] int NOT NULL,
    CONSTRAINT [UQ_WorkflowTag_Code] UNIQUE ([Code])
);
GO

CREATE INDEX [IX_WorkflowTag_Order]
    ON [asap].[WorkflowTag]([SortOrder], [Id]);
GO

CREATE TABLE [asap].[TitleRequestWorkflowTag]
(
    [TitleRequestId] bigint NOT NULL,
    [WorkflowTagId] bigint NOT NULL,
    CONSTRAINT [PK_TitleRequestWorkflowTag] PRIMARY KEY ([TitleRequestId], [WorkflowTagId]),
    CONSTRAINT [FK_TitleRequestWorkflowTag_Request] FOREIGN KEY ([TitleRequestId]) REFERENCES [asap].[TitleRequest]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_TitleRequestWorkflowTag_Tag] FOREIGN KEY ([WorkflowTagId]) REFERENCES [asap].[WorkflowTag]([Id])
);
GO
