CREATE TABLE [asap].[PatronIdentity]
(
    [Barcode] nvarchar(50) NOT NULL CONSTRAINT [PK_PatronIdentity] PRIMARY KEY,
    [PolarisPatronId] nvarchar(100) NOT NULL,
    [UpdatedUtc] datetime2(7) NOT NULL,
    [RowVersion] rowversion NOT NULL
);
GO
