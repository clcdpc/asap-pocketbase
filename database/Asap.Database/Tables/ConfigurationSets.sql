CREATE TABLE [asap].[CommonCreatorSet]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_CommonCreatorSet] PRIMARY KEY,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_CommonCreatorSet_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id])
);
GO

CREATE TABLE [asap].[CommonCreatorTerm]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CommonCreatorTerm] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [Value] nvarchar(500) NOT NULL,
    [SortOrder] int NOT NULL,
    CONSTRAINT [FK_CommonCreatorTerm_Set] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[CommonCreatorSet]([OrganizationId]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_CommonCreatorTerm_Order]
    ON [asap].[CommonCreatorTerm]([OrganizationId], [SortOrder], [Id]);
GO

CREATE TABLE [asap].[PatronCodeEligibilitySet]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_PatronCodeEligibilitySet] PRIMARY KEY,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_PatronCodeEligibilitySet_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id])
);
GO

CREATE TABLE [asap].[PatronCodeEligibilityMember]
(
    [OrganizationId] int NOT NULL,
    [PatronCodeId] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_PatronCodeEligibilityMember] PRIMARY KEY ([OrganizationId], [PatronCodeId]),
    CONSTRAINT [FK_PatronCodeEligibilityMember_Set] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[PatronCodeEligibilitySet]([OrganizationId]) ON DELETE CASCADE
);
GO

CREATE TABLE [asap].[PublicationOptionSet]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_PublicationOptionSet] PRIMARY KEY,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_PublicationOptionSet_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id])
);
GO

CREATE TABLE [asap].[PublicationOption]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PublicationOption] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [OptionKey] nvarchar(100) NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [IsEnabled] bit NOT NULL,
    [SortOrder] int NOT NULL,
    CONSTRAINT [FK_PublicationOption_Set] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[PublicationOptionSet]([OrganizationId]) ON DELETE CASCADE,
    CONSTRAINT [UQ_PublicationOption_Key] UNIQUE ([OrganizationId], [OptionKey])
);
GO

CREATE INDEX [IX_PublicationOption_Order]
    ON [asap].[PublicationOption]([OrganizationId], [SortOrder], [Id]);
GO

CREATE TABLE [asap].[ExternalSearchProvider]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ExternalSearchProvider] PRIMARY KEY,
    [OrganizationId] int NOT NULL,
    [ProviderKey] nvarchar(100) NOT NULL,
    [IsEnabled] bit NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [UrlTemplate] nvarchar(max) NOT NULL,
    [SortOrder] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_ExternalSearchProvider_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_ExternalSearchProvider_SystemOnly] CHECK ([OrganizationId] = 1),
    CONSTRAINT [UQ_ExternalSearchProvider_Key] UNIQUE ([ProviderKey])
);
GO

CREATE INDEX [IX_ExternalSearchProvider_Order]
    ON [asap].[ExternalSearchProvider]([SortOrder], [Id]);
GO

CREATE TABLE [asap].[ExternalSearchProviderOverride]
(
    [LibraryOrganizationId] int NOT NULL,
    [ExternalSearchProviderId] bigint NOT NULL,
    [IsEnabled] bit NULL,
    [Label] nvarchar(256) NULL,
    [UrlTemplate] nvarchar(max) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_ExternalSearchProviderOverride] PRIMARY KEY ([LibraryOrganizationId], [ExternalSearchProviderId]),
    CONSTRAINT [FK_ExternalSearchProviderOverride_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [FK_ExternalSearchProviderOverride_Provider] FOREIGN KEY ([ExternalSearchProviderId]) REFERENCES [asap].[ExternalSearchProvider]([Id]),
    CONSTRAINT [CK_ExternalSearchProviderOverride_LibraryOnly] CHECK ([LibraryOrganizationId] <> 1)
);
GO

CREATE TABLE [asap].[PatronCustomField]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PatronCustomField] PRIMARY KEY,
    [LibraryOrganizationId] int NOT NULL,
    [FieldKey] nvarchar(100) NOT NULL,
    [FieldType] nvarchar(20) NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [HelpText] nvarchar(max) NULL,
    [IsEnabled] bit NOT NULL,
    [SortOrder] int NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_PatronCustomField_Organization] FOREIGN KEY ([LibraryOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_PatronCustomField_LibraryOnly] CHECK ([LibraryOrganizationId] <> 1),
    CONSTRAINT [CK_PatronCustomField_Type] CHECK ([FieldType] IN (N'text', N'textarea', N'select')),
    CONSTRAINT [UQ_PatronCustomField_Key] UNIQUE ([LibraryOrganizationId], [FieldKey])
);
GO

CREATE INDEX [IX_PatronCustomField_Order]
    ON [asap].[PatronCustomField]([LibraryOrganizationId], [SortOrder], [Id]);
GO

CREATE TABLE [asap].[PatronCustomFieldOption]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PatronCustomFieldOption] PRIMARY KEY,
    [PatronCustomFieldId] bigint NOT NULL,
    [OptionKey] nvarchar(100) NOT NULL,
    [Label] nvarchar(256) NOT NULL,
    [IsEnabled] bit NOT NULL,
    [SortOrder] int NOT NULL,
    CONSTRAINT [FK_PatronCustomFieldOption_Field] FOREIGN KEY ([PatronCustomFieldId]) REFERENCES [asap].[PatronCustomField]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_PatronCustomFieldOption_Key] UNIQUE ([PatronCustomFieldId], [OptionKey])
);
GO

CREATE INDEX [IX_PatronCustomFieldOption_Order]
    ON [asap].[PatronCustomFieldOption]([PatronCustomFieldId], [SortOrder], [Id]);
GO

CREATE TABLE [asap].[Branding]
(
    [OrganizationId] int NOT NULL CONSTRAINT [PK_Branding] PRIMARY KEY,
    [LogoData] varbinary(max) NULL,
    [LogoContentType] nvarchar(100) NULL,
    [LogoFileName] nvarchar(500) NULL,
    [LogoAltText] nvarchar(500) NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [FK_Branding_Organization] FOREIGN KEY ([OrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_Branding_LogoTuple] CHECK
    (
        ([LogoData] IS NULL AND [LogoContentType] IS NULL AND [LogoFileName] IS NULL) OR
        ([LogoData] IS NOT NULL AND [LogoContentType] IS NOT NULL AND [LogoFileName] IS NOT NULL)
    )
);
GO
