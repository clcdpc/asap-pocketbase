CREATE TABLE [asap].[SchemaVersion]
(
    [Id] tinyint NOT NULL
        CONSTRAINT [PK_SchemaVersion] PRIMARY KEY,
    [Version] int NOT NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    CONSTRAINT [CK_SchemaVersion_Singleton] CHECK ([Id] = 1),
    CONSTRAINT [CK_SchemaVersion_PositiveVersion] CHECK ([Version] > 0)
);
