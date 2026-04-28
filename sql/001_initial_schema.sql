CREATE TABLE dbo.Suggestions
(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    PatronBarcode NVARCHAR(40) NOT NULL,
    Title NVARCHAR(256) NOT NULL,
    Author NVARCHAR(256) NOT NULL,
    Format NVARCHAR(80) NOT NULL,
    Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Suggestions_Status DEFAULT('New'),
    CreatedUtc DATETIMEOFFSET(0) NOT NULL CONSTRAINT DF_Suggestions_CreatedUtc DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Suggestions_Status_CreatedUtc ON dbo.Suggestions (Status, CreatedUtc DESC);
