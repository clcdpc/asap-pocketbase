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
    IF EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1 AND [Version] > 12)
    BEGIN
        THROW 51000, 'The database schema is newer than this DACPAC.', 1;
    END;
END;
ELSE IF OBJECT_ID(N'[asap].[TitleRequest]', N'U') IS NOT NULL
     OR OBJECT_ID(N'[asap].[DeploymentState]', N'U') IS NOT NULL
     OR OBJECT_ID(N'[asap].[PolarisSettings]', N'U') IS NOT NULL
BEGIN
    THROW 51000, 'An existing application database has no schema identity. Recreate it from this DACPAC.', 1;
END;
-- Invalid former defaults become explicitly unconfigured, never invented IDs.
IF OBJECT_ID(N'[asap].[PolarisSettings]', N'U') IS NOT NULL
BEGIN
    UPDATE [asap].[PolarisSettings] SET [WorkstationId] = NULL WHERE [WorkstationId] <= 0;
    UPDATE [asap].[PolarisSettings] SET [SystemPolarisUserId] = NULL WHERE [SystemPolarisUserId] <= 0;
END;
-- The former native journal allowed system scope 1 as a previous/observed
-- preference. It is absence, not a branch. Canonicalize before the stronger
-- identity constraint while retaining the operation, state and all history.
IF OBJECT_ID(N'[asap].[PickupPreferenceOperation]', N'U') IS NOT NULL
BEGIN
    UPDATE [asap].[PickupPreferenceOperation]
        SET [FromPickupBranchId] = NULL WHERE [FromPickupBranchId] = 1;
    UPDATE [asap].[PickupPreferenceOperation]
        SET [ObservedPickupBranchId] = NULL WHERE [ObservedPickupBranchId] = 1;
END;
-- These former global member-library defaults are no longer target authority.
-- Retire only these DACPAC-owned columns on a native schema-7 target; the row
-- and its global credentials/integration identity remain intact.
IF COL_LENGTH(N'asap.PolarisSettings', N'OrganizationIdForRequests') IS NOT NULL
BEGIN
    ALTER TABLE [asap].[PolarisSettings] DROP COLUMN [OrganizationIdForRequests];
END;
IF COL_LENGTH(N'asap.PolarisSettings', N'PickupOrganizationId') IS NOT NULL
BEGIN
    ALTER TABLE [asap].[PolarisSettings] DROP COLUMN [PickupOrganizationId];
END;
