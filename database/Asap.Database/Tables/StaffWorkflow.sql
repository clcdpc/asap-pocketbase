CREATE TABLE [asap].[DeletedRequestAudit]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DeletedRequestAudit] PRIMARY KEY,
    [RequestType] nvarchar(32) NOT NULL,
    [OriginalRequestKey] nvarchar(64) NOT NULL,
    [LibraryOrganizationId] int NOT NULL,
    [Title] nvarchar(500) NULL,
    [Author] nvarchar(500) NULL,
    [Identifier] nvarchar(100) NULL,
    [BibId] nvarchar(100) NULL,
    [Status] nvarchar(32) NULL,
    [CloseReason] nvarchar(64) NULL,
    [MaskedBarcode] nvarchar(50) NULL,
    [CreatedUtc] datetime2(7) NULL,
    [DeletedUtc] datetime2(7) NOT NULL,
    [DeletedByStaffUserId] bigint NULL,
    [DeletedByDisplayName] nvarchar(256) NULL,
    CONSTRAINT [FK_DeletedRequestAudit_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_DeletedRequestAudit_StaffUser] FOREIGN KEY ([DeletedByStaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [CK_DeletedRequestAudit_RequestType] CHECK ([RequestType] IN (N'title_request', N'additional_copy'))
);
GO

CREATE INDEX [IX_DeletedRequestAudit_LibraryDeleted]
    ON [asap].[DeletedRequestAudit]([LibraryOrganizationId], [DeletedUtc], [Id]);
GO

CREATE TABLE [asap].[AdministrativeAudit]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AdministrativeAudit] PRIMARY KEY,
    [ActorStaffUserId] bigint NULL,
    [ActorName] nvarchar(256) NULL,
    [OrganizationId] int NULL,
    [Action] nvarchar(100) NOT NULL,
    [TargetType] nvarchar(64) NULL,
    [TargetId] nvarchar(100) NULL,
    [DetailsJson] nvarchar(max) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    CONSTRAINT [FK_AdministrativeAudit_Actor] FOREIGN KEY ([ActorStaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_AdministrativeAudit_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_AdministrativeAudit_DetailsJson] CHECK ([DetailsJson] IS NULL OR ISJSON([DetailsJson]) = 1)
);
GO

CREATE INDEX [IX_AdministrativeAudit_OrganizationCreated]
    ON [asap].[AdministrativeAudit]([OrganizationId], [CreatedUtc], [Id]);
GO

CREATE TABLE [asap].[HoldPlacementOperation]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_HoldPlacementOperation] PRIMARY KEY,
    [TitleRequestId] bigint NOT NULL,
    [PatronBarcodeSnapshot] nvarchar(50) NOT NULL,
    [PatronIdSnapshot] nvarchar(100) NULL,
    [BibIdSnapshot] nvarchar(100) NOT NULL,
    [PickupBranchIdSnapshot] int NULL,
    [RequestingOrganizationIdSnapshot] int NULL,
    [WorkstationIdSnapshot] int NULL,
    [PolarisUserIdSnapshot] nvarchar(100) NULL,
    [AttemptNumber] int NOT NULL,
    [State] nvarchar(32) NOT NULL,
    [Phase] nvarchar(32) NOT NULL,
    [OwnerToken] uniqueidentifier NULL,
    [ExecutionEpoch] bigint NOT NULL,
    [LeaseExpiresUtc] datetime2(7) NULL,
    [RequestStartedUtc] datetime2(7) NOT NULL,
    [CreateStartedUtc] datetime2(7) NULL,
    [CreateResponseObservedUtc] datetime2(7) NULL,
    [ReplyStartedUtc] datetime2(7) NULL,
    [ReplyResponseObservedUtc] datetime2(7) NULL,
    [CompletedUtc] datetime2(7) NULL,
    [PolarisRequestGuid] nvarchar(100) NULL,
    [PolarisHoldId] nvarchar(100) NULL,
    [TxnGroupQualifier] nvarchar(256) NULL,
    [TxnQualifier] nvarchar(256) NULL,
    [ReplyAnswer] nvarchar(32) NULL,
    [ReplyState] nvarchar(32) NULL,
    [ProviderStatusType] nvarchar(64) NULL,
    [ProviderStatusValue] nvarchar(64) NULL,
    [ResultCode] nvarchar(100) NULL,
    [OutcomeEvidenceKind] nvarchar(100) NULL,
    [RecoveryAttemptCount] int NOT NULL CONSTRAINT [DF_HoldPlacementOperation_RecoveryAttemptCount] DEFAULT (0),
    [LastRecoveryUtc] datetime2(7) NULL,
    [LastErrorCode] nvarchar(100) NULL,
    [DetailJson] nvarchar(max) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_HoldPlacementOperation_TitleRequest] FOREIGN KEY ([TitleRequestId]) REFERENCES [asap].[TitleRequest]([Id]),
    CONSTRAINT [CK_HoldPlacementOperation_Attempt] CHECK ([AttemptNumber] > 0 AND [ExecutionEpoch] > 0 AND [RecoveryAttemptCount] >= 0),
    CONSTRAINT [CK_HoldPlacementOperation_State] CHECK ([State] IN (N'in_progress', N'ambiguous', N'operator_required', N'succeeded', N'no_hold', N'failed')),
    CONSTRAINT [CK_HoldPlacementOperation_Phase] CHECK ([Phase] IN (N'acquired', N'create_started', N'reply_ready', N'reply_started', N'result_recorded')),
    CONSTRAINT [CK_HoldPlacementOperation_Completion] CHECK
    (
        ([State] IN (N'succeeded', N'no_hold', N'failed') AND [CompletedUtc] IS NOT NULL) OR
        ([State] IN (N'in_progress', N'ambiguous', N'operator_required') AND [CompletedUtc] IS NULL)
    ),
    CONSTRAINT [CK_HoldPlacementOperation_Lease] CHECK
    (
        ([OwnerToken] IS NULL AND [LeaseExpiresUtc] IS NULL) OR
        ([OwnerToken] IS NOT NULL AND [LeaseExpiresUtc] IS NOT NULL)
    ),
    CONSTRAINT [CK_HoldPlacementOperation_ReplyContext] CHECK
    (
        [Phase] <> N'reply_started' OR
        ([PolarisRequestGuid] IS NOT NULL AND [TxnGroupQualifier] IS NOT NULL AND [TxnQualifier] IS NOT NULL AND [ReplyAnswer] IS NOT NULL AND [ReplyState] IS NOT NULL AND [ReplyStartedUtc] IS NOT NULL)
    ),
    CONSTRAINT [CK_HoldPlacementOperation_DetailJson] CHECK ([DetailJson] IS NULL OR ISJSON([DetailJson]) = 1),
    CONSTRAINT [UQ_HoldPlacementOperation_RequestAttempt] UNIQUE ([TitleRequestId], [AttemptNumber])
);
GO

CREATE UNIQUE INDEX [UX_HoldPlacementOperation_Incomplete]
    ON [asap].[HoldPlacementOperation]([TitleRequestId]) WHERE [CompletedUtc] IS NULL;
GO

CREATE INDEX [IX_HoldPlacementOperation_Recovery]
    ON [asap].[HoldPlacementOperation]([State], [RequestStartedUtc], [Id]) INCLUDE ([LeaseExpiresUtc], [Phase]);
GO

CREATE INDEX [IX_HoldPlacementOperation_RequestSucceeded]
    ON [asap].[HoldPlacementOperation]([TitleRequestId], [State], [AttemptNumber]);
GO
