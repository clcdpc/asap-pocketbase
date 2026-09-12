ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 160;

IF NOT EXISTS (SELECT 1 FROM [asap].[SchemaVersion] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[SchemaVersion] ([Id], [Version], [UpdatedUtc])
    VALUES (1, 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM [asap].[DeploymentState] WHERE [Id] = 1)
BEGIN
    INSERT INTO [asap].[DeploymentState] ([Id]) VALUES (1);
END;
