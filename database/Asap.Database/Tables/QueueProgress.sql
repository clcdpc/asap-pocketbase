CREATE TABLE [asap].[QueueProgress]
(
    [QueueName] nvarchar(64) NOT NULL,
    [ScopeOrganizationId] int NOT NULL,
    [CycleMaxId] bigint NULL,
    [LastCreatedUtc] datetime2(7) NULL,
    [LastItemId] bigint NULL,
    [LastOutcomeItemId] bigint NULL,
    [LastOutcomeCode] nvarchar(64) NULL,
    [LastOutcomeUtc] datetime2(7) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL CONSTRAINT [DF_QueueProgress_UpdatedUtc] DEFAULT (SYSUTCDATETIME()),
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_QueueProgress] PRIMARY KEY ([QueueName], [ScopeOrganizationId]),
    CONSTRAINT [FK_QueueProgress_Organization] FOREIGN KEY ([ScopeOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_QueueProgress_QueueName] CHECK
    (
        [QueueName] IN
        (
            N'IdentifierProcessing', N'PurchasePromotion', N'HoldPlacement',
            N'FulfillmentTracking', N'OutstandingTimeout', N'PendingHoldTimeout',
            N'HoldPickupTimeout', N'AdditionalCopyTimeout', N'HoldRecovery'
        )
    ),
    CONSTRAINT [CK_QueueProgress_ScopeOrganizationId] CHECK ([ScopeOrganizationId] >= 1),
    CONSTRAINT [CK_QueueProgress_CursorPair] CHECK
    (
        ([LastCreatedUtc] IS NULL AND [LastItemId] IS NULL) OR
        ([LastCreatedUtc] IS NOT NULL AND [LastItemId] IS NOT NULL)
    ),
    CONSTRAINT [CK_QueueProgress_CycleWatermark] CHECK
    (
        [CycleMaxId] IS NULL OR [CycleMaxId] >= 0
    )
);
GO
GO
