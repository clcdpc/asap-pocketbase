-- A patron preference is external state. Persist dispatch intent before PAPI,
-- then complete this record in the transaction that accepts the local snapshot.
CREATE TABLE [asap].[PickupPreferenceOperation]
(
    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_PickupPreferenceOperation] PRIMARY KEY,
    [Barcode] nvarchar(50) NOT NULL,
    [PatronId] int NOT NULL,
    [LibraryOrganizationId] int NOT NULL,
    [TitleRequestId] bigint NULL,
    [Origin] nvarchar(32) NOT NULL,
    [ActorStaffUserId] bigint NULL,
    [FromPickupBranchId] int NULL,
    [FromPickupBranchName] nvarchar(256) NULL,
    [ToPickupBranchId] int NOT NULL,
    [ToPickupBranchName] nvarchar(256) NOT NULL,
    [State] int NOT NULL,
    [DispatchStartedUtc] datetime2(7) NOT NULL,
    [DispatchFinishedUtc] datetime2(7) NULL,
    [ProviderConfirmedUtc] datetime2(7) NULL,
    [ConfirmedByRead] bit NOT NULL,
    [ObservedPickupBranchId] int NULL,
    [ResolvedByStaffUserId] bigint NULL,
    [FailureCode] nvarchar(80) NULL,
    [CompletedUtc] datetime2(7) NULL,
    CONSTRAINT [FK_PickupPreferenceOperation_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_PickupPreferenceOperation_Identity] CHECK
        ([PatronId] > 0 AND [LibraryOrganizationId] > 1 AND [ToPickupBranchId] > 1
         AND ([FromPickupBranchId] IS NULL OR [FromPickupBranchId] > 1)
         AND ([ObservedPickupBranchId] IS NULL OR [ObservedPickupBranchId] > 1)),
    CONSTRAINT [CK_PickupPreferenceOperation_Origin] CHECK
        ([Origin] IN (N'request', N'patron_suggestion', N'staff_suggestion')),
    CONSTRAINT [CK_PickupPreferenceOperation_State] CHECK
        (([State] = 1 AND [CompletedUtc] IS NULL AND [ProviderConfirmedUtc] IS NULL)
         OR ([State] = 2 AND [CompletedUtc] IS NULL AND [ProviderConfirmedUtc] IS NOT NULL AND [DispatchFinishedUtc] IS NOT NULL)
         OR ([State] = 3 AND [CompletedUtc] IS NOT NULL AND [ProviderConfirmedUtc] IS NOT NULL AND [DispatchFinishedUtc] IS NOT NULL)
         OR ([State] = 4 AND [CompletedUtc] IS NOT NULL AND [ProviderConfirmedUtc] IS NULL
             AND [DispatchFinishedUtc] IS NOT NULL AND [FailureCode] IS NOT NULL))
);
GO

-- Serialize uncertain writes across libraries and entry points for one patron.
CREATE UNIQUE INDEX [UX_PickupPreferenceOperation_Incomplete]
    ON [asap].[PickupPreferenceOperation]([Barcode]) WHERE [CompletedUtc] IS NULL;
GO

CREATE INDEX [IX_PickupPreferenceOperation_Request]
    ON [asap].[PickupPreferenceOperation]([TitleRequestId], [DispatchStartedUtc]);
GO
