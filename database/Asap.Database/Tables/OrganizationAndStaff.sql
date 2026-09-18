CREATE TABLE [asap].[Organization]
(
    [Id] int NOT NULL CONSTRAINT [PK_Organization] PRIMARY KEY,
    [DisplayName] nvarchar(256) NOT NULL,
    [Abbreviation] nvarchar(64) NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_Organization_IsActive] DEFAULT (0),
    [LastSyncedUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [CK_Organization_SystemActive] CHECK ([Id] <> 1 OR [IsActive] = 1)
);
GO

CREATE TABLE [asap].[StaffUser]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_StaffUser] PRIMARY KEY,
    [EntraTenantId] uniqueidentifier NULL,
    [EntraObjectId] uniqueidentifier NULL,
    [UserPrincipalName] nvarchar(320) NULL,
    [NormalizedUserPrincipalName] nvarchar(320) NULL,
    [DisplayName] nvarchar(256) NULL,
    [NotificationEmail] nvarchar(320) NULL,
    [Role] nvarchar(32) NOT NULL,
    [OrganizationId] int NOT NULL,
    [IsActive] bit NOT NULL,
    [WeeklyActionSummaryEnabled] bit NOT NULL CONSTRAINT [DF_StaffUser_WeeklyActionSummaryEnabled] DEFAULT (0),
    [WeeklyActionSummaryEmail] nvarchar(320) NULL,
    [PurchaseReminderDefault] bit NOT NULL CONSTRAINT [DF_StaffUser_PurchaseReminderDefault] DEFAULT (0),
    [AdditionalCopyReminderDefault] bit NOT NULL CONSTRAINT [DF_StaffUser_AdditionalCopyReminderDefault] DEFAULT (0),
    [DefaultMineUnclaimedFilter] bit NOT NULL CONSTRAINT [DF_StaffUser_DefaultMineUnclaimedFilter] DEFAULT (0),
    [LastLoginUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_StaffUser_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_StaffUser_Role] CHECK ([Role] IN (N'staff', N'admin', N'super_admin')),
    CONSTRAINT [CK_StaffUser_ActiveIdentity] CHECK
    (
        [IsActive] = 0 OR
        (
            NULLIF(LTRIM(RTRIM([UserPrincipalName])), N'') IS NOT NULL AND
            NULLIF(LTRIM(RTRIM([NormalizedUserPrincipalName])), N'') IS NOT NULL AND
            [NormalizedUserPrincipalName] = UPPER(LTRIM(RTRIM([UserPrincipalName]))) AND
            [NormalizedUserPrincipalName] NOT LIKE N'%@STAFF.ASAP.LOCAL'
        )
    ),
    CONSTRAINT [CK_StaffUser_RoleOrganization] CHECK (([Role] = N'super_admin' AND [OrganizationId] = 1) OR ([Role] IN (N'staff', N'admin') AND [OrganizationId] <> 1))
);
GO

CREATE UNIQUE INDEX [UX_StaffUser_NormalizedUserPrincipalName]
    ON [asap].[StaffUser]([NormalizedUserPrincipalName])
    WHERE [NormalizedUserPrincipalName] IS NOT NULL;
GO

CREATE INDEX [IX_StaffUser_Organization_Role_Active]
    ON [asap].[StaffUser]([OrganizationId], [Role], [IsActive]);
GO
