CREATE TABLE [asap].[LegacyPocketBaseMapping]
(
    [EntityType] nvarchar(100) NOT NULL,
    [PocketBaseId] nvarchar(100) NOT NULL,
    [NewId] bigint NOT NULL,
    CONSTRAINT [PK_LegacyPocketBaseMapping] PRIMARY KEY ([EntityType], [PocketBaseId])
);
GO

CREATE INDEX [IX_LegacyPocketBaseMapping_NewId]
    ON [asap].[LegacyPocketBaseMapping]([EntityType], [NewId]);
GO
