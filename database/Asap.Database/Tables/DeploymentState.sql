CREATE TABLE [asap].[DeploymentState]
(
    [Id] tinyint NOT NULL
        CONSTRAINT [PK_DeploymentState] PRIMARY KEY,
    [LastDacpacSha256] char(64) NULL,
    [LastHangfireSchemaVersion] nvarchar(100) NULL,
    [LastHangfireSchemaAssetSha256] char(64) NULL,
    [LastReleaseVersion] nvarchar(100) NULL,
    [LastReleaseCommitSha] char(40) NULL,
    [LastDeployedUtc] datetime2(7) NULL,
    CONSTRAINT [CK_DeploymentState_Singleton] CHECK ([Id] = 1),
    CONSTRAINT [CK_DeploymentState_DacpacHash] CHECK
        ([LastDacpacSha256] IS NULL OR [LastDacpacSha256] NOT LIKE '%[^0-9A-Fa-f]%'),
    CONSTRAINT [CK_DeploymentState_HangfireAssetHash] CHECK
        ([LastHangfireSchemaAssetSha256] IS NULL OR [LastHangfireSchemaAssetSha256] NOT LIKE '%[^0-9A-Fa-f]%'),
    CONSTRAINT [CK_DeploymentState_CommitSha] CHECK
        ([LastReleaseCommitSha] IS NULL OR [LastReleaseCommitSha] NOT LIKE '%[^0-9A-Fa-f]%')
);
