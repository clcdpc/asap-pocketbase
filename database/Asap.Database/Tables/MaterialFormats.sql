CREATE TABLE [asap].[MaterialFormat]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MaterialFormat] PRIMARY KEY,
    [OwnerOrganizationId] int NOT NULL,
    [Code] nvarchar(100) NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [SortOrder] int NOT NULL,
    [IsEnabled] bit NOT NULL,
    [MessageBehavior] nvarchar(32) NULL,
    [Message] nvarchar(max) NULL,
    [TitleMode] nvarchar(16) NULL,
    [TitleLabel] nvarchar(256) NULL,
    [AuthorMode] nvarchar(16) NULL,
    [AuthorLabel] nvarchar(256) NULL,
    [IdentifierMode] nvarchar(16) NULL,
    [IdentifierLabel] nvarchar(256) NULL,
    [PublicationMode] nvarchar(16) NULL,
    [PublicationLabel] nvarchar(256) NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_MaterialFormat_Organization] FOREIGN KEY ([OwnerOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_MaterialFormat_MessageBehavior] CHECK ([MessageBehavior] IS NULL OR [MessageBehavior] IN (N'none', N'message', N'ebookMessage', N'eaudiobookMessage')),
    CONSTRAINT [CK_MaterialFormat_TitleRequired] CHECK ([TitleMode] IS NULL OR [TitleMode] = N'required'),
    CONSTRAINT [CK_MaterialFormat_AuthorMode] CHECK ([AuthorMode] IS NULL OR [AuthorMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [CK_MaterialFormat_IdentifierMode] CHECK ([IdentifierMode] IS NULL OR [IdentifierMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [CK_MaterialFormat_PublicationMode] CHECK ([PublicationMode] IS NULL OR [PublicationMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [UQ_MaterialFormat_OwnerCode] UNIQUE ([OwnerOrganizationId], [Code])
);
GO

CREATE UNIQUE INDEX [UX_MaterialFormat_SystemCode]
    ON [asap].[MaterialFormat]([Code]) WHERE [OwnerOrganizationId] = 1;
GO

CREATE TABLE [asap].[MaterialFormatOverride]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MaterialFormatOverride] PRIMARY KEY,
    [LibraryOrganizationId] int NOT NULL,
    [MaterialFormatId] bigint NOT NULL,
    [Label] nvarchar(256) NULL,
    [SortOrder] int NULL,
    [IsEnabled] bit NULL,
    [MessageBehavior] nvarchar(32) NULL,
    [Message] nvarchar(max) NULL,
    [TitleMode] nvarchar(16) NULL,
    [TitleLabel] nvarchar(256) NULL,
    [AuthorMode] nvarchar(16) NULL,
    [AuthorLabel] nvarchar(256) NULL,
    [IdentifierMode] nvarchar(16) NULL,
    [IdentifierLabel] nvarchar(256) NULL,
    [PublicationMode] nvarchar(16) NULL,
    [PublicationLabel] nvarchar(256) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_MaterialFormatOverride_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_MaterialFormatOverride_Format] FOREIGN KEY ([MaterialFormatId]) REFERENCES [asap].[MaterialFormat]([Id]),
    CONSTRAINT [CK_MaterialFormatOverride_LibraryOnly] CHECK ([LibraryOrganizationId] <> 1),
    CONSTRAINT [CK_MaterialFormatOverride_MessageBehavior] CHECK ([MessageBehavior] IS NULL OR [MessageBehavior] IN (N'none', N'message', N'ebookMessage', N'eaudiobookMessage')),
    CONSTRAINT [CK_MaterialFormatOverride_TitleRequired] CHECK ([TitleMode] IS NULL OR [TitleMode] = N'required'),
    CONSTRAINT [CK_MaterialFormatOverride_AuthorMode] CHECK ([AuthorMode] IS NULL OR [AuthorMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [CK_MaterialFormatOverride_IdentifierMode] CHECK ([IdentifierMode] IS NULL OR [IdentifierMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [CK_MaterialFormatOverride_PublicationMode] CHECK ([PublicationMode] IS NULL OR [PublicationMode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [UQ_MaterialFormatOverride_LibraryFormat] UNIQUE ([LibraryOrganizationId], [MaterialFormatId])
);
GO

CREATE TABLE [asap].[MaterialFormatCustomFieldRule]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_MaterialFormatCustomFieldRule] PRIMARY KEY,
    [LibraryOrganizationId] int NOT NULL,
    [MaterialFormatId] bigint NOT NULL,
    [PatronCustomFieldId] bigint NOT NULL,
    [Mode] nvarchar(16) NOT NULL,
    [LabelOverride] nvarchar(256) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_MaterialFormatCustomFieldRule_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_MaterialFormatCustomFieldRule_Format] FOREIGN KEY ([MaterialFormatId]) REFERENCES [asap].[MaterialFormat]([Id]),
    CONSTRAINT [FK_MaterialFormatCustomFieldRule_Field] FOREIGN KEY ([PatronCustomFieldId]) REFERENCES [asap].[PatronCustomField]([Id]),
    CONSTRAINT [CK_MaterialFormatCustomFieldRule_LibraryOnly] CHECK ([LibraryOrganizationId] <> 1),
    CONSTRAINT [CK_MaterialFormatCustomFieldRule_Mode] CHECK ([Mode] IN (N'required', N'optional', N'hidden')),
    CONSTRAINT [UQ_MaterialFormatCustomFieldRule] UNIQUE ([LibraryOrganizationId], [MaterialFormatId], [PatronCustomFieldId])
);
GO

CREATE TABLE [asap].[FormatAutoClaimRule]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_FormatAutoClaimRule] PRIMARY KEY,
    [LibraryOrganizationId] int NOT NULL,
    [MaterialFormatId] bigint NOT NULL,
    [StaffUserId] bigint NULL,
    [IsActive] bit NOT NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [DeactivatedUtc] datetime2(7) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_FormatAutoClaimRule_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_FormatAutoClaimRule_Format] FOREIGN KEY ([MaterialFormatId]) REFERENCES [asap].[MaterialFormat]([Id]),
    CONSTRAINT [FK_FormatAutoClaimRule_StaffUser] FOREIGN KEY ([StaffUserId]) REFERENCES [asap].[StaffUser]([Id]),
    CONSTRAINT [CK_FormatAutoClaimRule_LibraryOnly] CHECK ([LibraryOrganizationId] <> 1),
    CONSTRAINT [CK_FormatAutoClaimRule_ActiveStaff] CHECK ([IsActive] = 0 OR [StaffUserId] IS NOT NULL),
    CONSTRAINT [CK_FormatAutoClaimRule_Deactivation] CHECK (([IsActive] = 1 AND [DeactivatedUtc] IS NULL) OR [IsActive] = 0)
);
GO

CREATE UNIQUE INDEX [UX_FormatAutoClaimRule_Active]
    ON [asap].[FormatAutoClaimRule]([LibraryOrganizationId], [MaterialFormatId]) WHERE [IsActive] = 1;
GO
