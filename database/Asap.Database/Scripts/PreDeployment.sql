-- Native Polaris identities are a pre-release reset boundary. Refuse an older
-- target before the deployment plan can alter its columns or seed any rows.
IF OBJECT_ID(N'[asap].[SchemaVersion]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
    BEGIN
        THROW 51000, 'An existing application database has no schema identity. Recreate it from this DACPAC.', 1;
    END;
    IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] < 7)
    BEGIN
        THROW 51000, 'Schema 7 is a pre-release reset boundary. Recreate the application database from this DACPAC.', 1;
    END;
    IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] > 7)
    BEGIN
        THROW 51000, 'The database schema is newer than this DACPAC.', 1;
    END;
END;
ELSE IF OBJECT_ID(N'[asap].[TitleRequest]', N'U') IS NOT NULL
     OR OBJECT_ID(N'[asap].[DeploymentState]', N'U') IS NOT NULL
BEGIN
    THROW 51000, 'An existing application database has no schema identity. Recreate it from this DACPAC.', 1;
END;
