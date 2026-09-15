CREATE TABLE [asap].[AdditionalCopyRequest]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AdditionalCopyRequest] PRIMARY KEY,
    [LegacyId] nvarchar(64) NULL,
    [SourceTitleRequestId] bigint NULL,
    [LibraryOrganizationId] int NOT NULL,
    [LibraryNameSnapshot] nvarchar(256) NULL,
    [BibId] nvarchar(128) NOT NULL,
    [Title] nvarchar(500) NOT NULL,
    [Author] nvarchar(500) NULL,
    [Identifier] nvarchar(100) NULL,
    [Publication] nvarchar(200) NULL,
    [MaterialFormatId] bigint NULL,
    [FormatSnapshot] nvarchar(100) NULL,
    [Status] nvarchar(32) NOT NULL,
    [Notes] nvarchar(max) NULL,
    [CreatedByStaffUserId] bigint NULL,
    [CreatedByDisplayName] nvarchar(256) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [ClaimedByStaffUserId] bigint NULL,
    [ClaimedByDisplayName] nvarchar(256) NULL,
    [ClaimedAtUtc] datetime2(7) NULL,
    [ClaimType] nvarchar(32) NULL,
    [ClaimRuleId] bigint NULL,
    [ClosedByStaffUserId] bigint NULL,
    [ClosedByDisplayName] nvarchar(256) NULL,
    [ClosedUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_AdditionalCopyRequest_SourceTitleRequest] FOREIGN KEY ([SourceTitleRequestId])
        REFERENCES [asap].[TitleRequest]([Id]) ON DELETE SET NULL,
    CONSTRAINT [FK_AdditionalCopyRequest_Organization] FOREIGN KEY ([LibraryOrganizationId])
        REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_AdditionalCopyRequest_MaterialFormat] FOREIGN KEY ([MaterialFormatId])
        REFERENCES [asap].[MaterialFormat]([Id]),
    CONSTRAINT [FK_AdditionalCopyRequest_CreatedByStaffUser] FOREIGN KEY ([CreatedByStaffUserId])
        REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_AdditionalCopyRequest_ClaimedByStaffUser] FOREIGN KEY ([ClaimedByStaffUserId])
        REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [FK_AdditionalCopyRequest_ClaimRule] FOREIGN KEY ([ClaimRuleId])
        REFERENCES [asap].[FormatAutoClaimRule]([Id]),
    CONSTRAINT [FK_AdditionalCopyRequest_ClosedByStaffUser] FOREIGN KEY ([ClosedByStaffUserId])
        REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [CK_AdditionalCopyRequest_Status] CHECK ([Status] IN (N'open', N'closed')),
    CONSTRAINT [CK_AdditionalCopyRequest_Updated] CHECK ([UpdatedUtc] >= [CreatedUtc]),
    CONSTRAINT [CK_AdditionalCopyRequest_Closed] CHECK
    (
        ([Status] = N'open' AND [ClosedByStaffUserId] IS NULL AND [ClosedByDisplayName] IS NULL AND [ClosedUtc] IS NULL) OR
        ([Status] = N'closed' AND [ClosedUtc] IS NOT NULL)
    ),
    CONSTRAINT [CK_AdditionalCopyRequest_ClaimType] CHECK ([ClaimType] IS NULL OR [ClaimType] IN (N'manual', N'automatic_format_rule', N'legacy')),
    CONSTRAINT [CK_AdditionalCopyRequest_ClaimRule] CHECK
        ([ClaimRuleId] IS NULL OR [ClaimType] IN (N'automatic_format_rule', N'legacy')),
    CONSTRAINT [CK_AdditionalCopyRequest_ClaimAttribution] CHECK
    (
        ([ClaimedByStaffUserId] IS NULL AND [ClaimedByDisplayName] IS NULL AND [ClaimedAtUtc] IS NULL AND [ClaimType] IS NULL AND [ClaimRuleId] IS NULL) OR
        ([ClaimedByDisplayName] IS NOT NULL AND [ClaimedAtUtc] IS NOT NULL AND ([Status] = N'closed' OR [ClaimedByStaffUserId] IS NOT NULL))
    )
);
GO

CREATE UNIQUE INDEX [UX_AdditionalCopyRequest_LegacyId]
    ON [asap].[AdditionalCopyRequest]([LegacyId]) WHERE [LegacyId] IS NOT NULL;
GO

CREATE INDEX [IX_AdditionalCopyRequest_LibraryStatus]
    ON [asap].[AdditionalCopyRequest]([LibraryOrganizationId], [Status], [CreatedUtc] DESC, [Id] DESC);
GO

CREATE INDEX [IX_AdditionalCopyRequest_Source]
    ON [asap].[AdditionalCopyRequest]([SourceTitleRequestId], [Status]);
GO

CREATE INDEX [IX_AdditionalCopyRequest_LibraryBibStatus]
    ON [asap].[AdditionalCopyRequest]([LibraryOrganizationId], [BibId], [Status]);
GO

CREATE INDEX [IX_AdditionalCopyRequest_OpenClaimant]
    ON [asap].[AdditionalCopyRequest]([ClaimedByStaffUserId], [LibraryOrganizationId], [Id])
    WHERE [Status] = N'open' AND [ClaimedByStaffUserId] IS NOT NULL;
GO
