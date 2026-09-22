CREATE TABLE [asap].[PatronSession]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_PatronSession] PRIMARY KEY,
    [TokenHash] binary(32) NOT NULL,
    [Barcode] nvarchar(50) NOT NULL,
    [HomeOrganizationId] int NULL,
    [ExperienceOrganizationId] int NULL,
    [EffectiveOrganizationId] int NOT NULL,
    [CreatedUtc] datetime2(7) NOT NULL,
    [ExpiresUtc] datetime2(7) NOT NULL,
    [RevokedUtc] datetime2(7) NULL,
    CONSTRAINT [FK_PatronSession_EffectiveOrganization] FOREIGN KEY ([EffectiveOrganizationId]) REFERENCES [asap].[Organization]([Id]),
    CONSTRAINT [CK_PatronSession_Expiration] CHECK ([ExpiresUtc] > [CreatedUtc]),
    CONSTRAINT [UQ_PatronSession_TokenHash] UNIQUE ([TokenHash])
);
GO

CREATE INDEX [IX_PatronSession_EffectiveOrganization]
    ON [asap].[PatronSession]([EffectiveOrganizationId], [RevokedUtc]);
GO

CREATE INDEX [IX_PatronSession_ExpiresUtc]
    ON [asap].[PatronSession]([ExpiresUtc]);
GO
