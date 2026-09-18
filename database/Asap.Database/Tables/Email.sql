CREATE TABLE [asap].[EmailTemplate]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmailTemplate] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [TemplateKey] nvarchar(100) NOT NULL,
    [SourceTemplateId] bigint NULL,
    [DisplayName] nvarchar(256) NULL,
    [SubjectTemplate] nvarchar(max) NULL,
    [BodyTemplate] nvarchar(max) NULL,
    [IsHidden] bit NOT NULL,
    [IsCustom] bit NOT NULL,
    [SortOrder] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_EmailTemplate_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_EmailTemplate_Source] FOREIGN KEY ([SourceTemplateId]) REFERENCES [asap].[EmailTemplate]([Id]),
    CONSTRAINT [CK_EmailTemplate_Ownership] CHECK
    (
        ([OrganizationId] = 1 AND [SourceTemplateId] IS NULL AND [IsCustom] = 0) OR
        ([OrganizationId] <> 1 AND [SourceTemplateId] IS NOT NULL AND [IsCustom] = 0) OR
        ([OrganizationId] <> 1 AND [SourceTemplateId] IS NULL AND [IsCustom] = 1)
    ),
    CONSTRAINT [CK_EmailTemplate_Content] CHECK
    (
        [SourceTemplateId] IS NOT NULL OR
        ([SubjectTemplate] IS NOT NULL AND [BodyTemplate] IS NOT NULL)
    )
);
GO

CREATE UNIQUE INDEX [UX_EmailTemplate_SystemKey]
    ON [asap].[EmailTemplate]([TemplateKey]) WHERE [OrganizationId] = 1;
GO

CREATE UNIQUE INDEX [UX_EmailTemplate_LibrarySource]
    ON [asap].[EmailTemplate]([OrganizationId], [SourceTemplateId]) WHERE [SourceTemplateId] IS NOT NULL;
GO

CREATE UNIQUE INDEX [UX_EmailTemplate_LibraryCustomKey]
    ON [asap].[EmailTemplate]([OrganizationId], [TemplateKey]) WHERE [IsCustom] = 1;
GO

CREATE INDEX [IX_EmailTemplate_OrganizationOrder]
    ON [asap].[EmailTemplate]([OrganizationId], [SortOrder], [Id]);
GO

CREATE TABLE [asap].[EmailOutbox]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmailOutbox] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [BusinessKey] nvarchar(450) NULL,
    [DeliveryClass] nvarchar(40) NOT NULL,
    [RequestedByStaffUserId] bigint NULL,
    [RequestId] nvarchar(128) NULL,
    [DeliveryMode] nvarchar(16) NULL,
    [RecipientStaffUserId] bigint NULL,
    [RecipientAuthenticationEmail] nvarchar(320) NULL,
    [AuthorizationOrganizationId] int NULL,
    [RecipientAddressKind] nvarchar(32) NULL,
    [ToAddress] nvarchar(320) NULL,
    [FromAddress] nvarchar(320) NULL,
    [FromName] nvarchar(256) NULL,
    [Subject] nvarchar(max) NULL,
    [BodyText] nvarchar(max) NULL,
    [BodyHtml] nvarchar(max) NULL,
    [Status] nvarchar(16) NOT NULL,
    [SuppressionReason] nvarchar(100) NULL,
    [AttemptCount] int NOT NULL CONSTRAINT [DF_EmailOutbox_AttemptCount] DEFAULT (0),
    [NextAttemptUtc] datetime2(7) NULL,
    [LastAttemptUtc] datetime2(7) NULL,
    [SendingStartedUtc] datetime2(7) NULL,
    [LeaseId] uniqueidentifier NULL,
    [LeaseExpiresUtc] datetime2(7) NULL,
    [LastErrorCode] nvarchar(100) NULL,
    [LastErrorDetail] nvarchar(max) NULL,
    [ProviderMessageId] nvarchar(256) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [SentUtc] datetime2(7) NULL,
    [SuppressedUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_EmailOutbox_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_EmailOutbox_RequestedByStaffUser] FOREIGN KEY ([RequestedByStaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_EmailOutbox_RecipientStaffUser] FOREIGN KEY ([RecipientStaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_EmailOutbox_AuthorizationOrganization] FOREIGN KEY ([AuthorizationOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_EmailOutbox_DeliveryClass] CHECK ([DeliveryClass] IN (N'business_event', N'staff_authorization_sensitive', N'operational_test')),
    CONSTRAINT [CK_EmailOutbox_Status] CHECK ([Status] IN (N'pending', N'sending', N'sent', N'failed', N'suppressed')),
    CONSTRAINT [CK_EmailOutbox_DeliveryMode] CHECK ([DeliveryMode] IS NULL OR [DeliveryMode] IN (N'capture', N'live')),
    CONSTRAINT [CK_EmailOutbox_AttemptCount] CHECK ([AttemptCount] >= 0),
    CONSTRAINT [CK_EmailOutbox_AuthorizationTuple] CHECK
    (
        ([DeliveryClass] = N'staff_authorization_sensitive' AND
         [RecipientStaffUserId] IS NOT NULL AND
         NULLIF(LTRIM(RTRIM([RecipientAuthenticationEmail])), N'') IS NOT NULL AND
         [AuthorizationOrganizationId] IS NOT NULL AND
         [RecipientAddressKind] IS NOT NULL AND
         [RecipientAddressKind] IN (N'notification_email', N'weekly_summary')) OR
        ([DeliveryClass] <> N'staff_authorization_sensitive' AND
         [RecipientStaffUserId] IS NULL AND
         [RecipientAuthenticationEmail] IS NULL AND
         [AuthorizationOrganizationId] IS NULL AND
         [RecipientAddressKind] IS NULL)
    ),
    CONSTRAINT [CK_EmailOutbox_DeliverablePayload] CHECK
    (
        [Status] = N'suppressed' OR
        (
            NULLIF(LTRIM(RTRIM([ToAddress])), N'') IS NOT NULL AND
            NULLIF(LTRIM(RTRIM([FromAddress])), N'') IS NOT NULL AND
            (
                [Status] = N'sent' OR
                ([Status] IN (N'pending', N'sending', N'failed') AND
                 [Subject] IS NOT NULL AND
                 ([BodyText] IS NOT NULL OR [BodyHtml] IS NOT NULL))
            )
        )
    ),
    CONSTRAINT [CK_EmailOutbox_SendingLease] CHECK
    (
        [Status] <> N'sending' OR
        ([SendingStartedUtc] IS NOT NULL AND [LeaseId] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL AND [LeaseExpiresUtc] > [SendingStartedUtc])
    ),
    CONSTRAINT [CK_EmailOutbox_TerminalTimestamp] CHECK
    (
        ([Status] = N'sent' AND [SentUtc] IS NOT NULL AND [SuppressedUtc] IS NULL) OR
        ([Status] = N'suppressed' AND [SuppressedUtc] IS NOT NULL AND NULLIF(LTRIM(RTRIM([SuppressionReason])), N'') IS NOT NULL AND [SentUtc] IS NULL) OR
        ([Status] IN (N'pending', N'sending', N'failed') AND [SentUtc] IS NULL AND [SuppressedUtc] IS NULL)
    )
);
GO

CREATE UNIQUE INDEX [UX_EmailOutbox_BusinessKey]
    ON [asap].[EmailOutbox]([BusinessKey]) WHERE [BusinessKey] IS NOT NULL;
GO

CREATE UNIQUE INDEX [UX_EmailOutbox_OperationalRequest]
    ON [asap].[EmailOutbox]([RequestedByStaffUserId], [OrganizationId], [RequestId])
    WHERE [DeliveryClass] = N'operational_test'
      AND [RequestedByStaffUserId] IS NOT NULL
      AND [RequestId] IS NOT NULL;
GO

CREATE INDEX [IX_EmailOutbox_OperationalCooldown]
    ON [asap].[EmailOutbox]([RequestedByStaffUserId], [OrganizationId], [CreatedUtc])
    WHERE [DeliveryClass] = N'operational_test';
GO

CREATE INDEX [IX_EmailOutbox_Pending]
    ON [asap].[EmailOutbox]([Status], [NextAttemptUtc], [CreatedUtc], [Id]);
GO

CREATE INDEX [IX_EmailOutbox_ExpiredLease]
    ON [asap].[EmailOutbox]([Status], [LeaseExpiresUtc], [Id]);
GO

CREATE INDEX [IX_EmailOutbox_RecipientAuthorization]
    ON [asap].[EmailOutbox]([RecipientStaffUserId], [AuthorizationOrganizationId], [Status]);
GO

CREATE TABLE [asap].[EmailDeliveryEvent]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_EmailDeliveryEvent] PRIMARY KEY,
    [EmailOutboxId] bigint NULL,
    [ProviderMessageId] nvarchar(256) NULL,
    [ProviderEventId] nvarchar(256) NULL,
    [EventType] nvarchar(100) NOT NULL,
    [ReceivedUtc] datetime2(7) NOT NULL,
    [MetadataJson] nvarchar(max) NULL,
    CONSTRAINT [FK_EmailDeliveryEvent_Outbox] FOREIGN KEY ([EmailOutboxId]) REFERENCES [asap].[EmailOutbox]([Id]),
    CONSTRAINT [CK_EmailDeliveryEvent_MetadataJson] CHECK ([MetadataJson] IS NULL OR ISJSON([MetadataJson]) = 1)
);
GO

CREATE UNIQUE INDEX [UX_EmailDeliveryEvent_ProviderEventId]
    ON [asap].[EmailDeliveryEvent]([ProviderEventId]) WHERE [ProviderEventId] IS NOT NULL;
GO

CREATE INDEX [IX_EmailDeliveryEvent_OutboxReceived]
    ON [asap].[EmailDeliveryEvent]([EmailOutboxId], [ReceivedUtc], [Id]);
GO
