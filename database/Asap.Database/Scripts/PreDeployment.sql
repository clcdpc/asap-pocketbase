-- Only the current, never-production schema is supported. Validate identity
-- before the deployment plan can alter existing objects. Older databases need
-- an explicitly authorized reset; deployment never performs one automatically.
IF OBJECT_ID(N'[asap].[SchemaVersion]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
    BEGIN
        THROW 51000, 'An existing application database has no schema identity.', 1;
    END;
    IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] < 13)
    BEGIN
        THROW 51000, 'This DACPAC supports schema 13 only. An older pre-production database requires an explicitly authorized reset.', 1;
    END;
    IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] > 13)
    BEGIN
        THROW 51000, 'The database schema is newer than this DACPAC.', 1;
    END;
END;
ELSE IF EXISTS (SELECT 1 FROM sys.tables WHERE [schema_id] = SCHEMA_ID(N'asap'))
BEGIN
    THROW 51000, 'An existing application database has no schema identity.', 1;
END;
