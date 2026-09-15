# Asap.Database

This SDK-style `Microsoft.Build.Sql` project is the source of truth for
ASAP-owned SQL Server objects. It targets SQL Server 2022 (`Sql160`) and owns
only the `[asap]` schema. Hangfire owns `[HangFire]` and is deliberately absent.

The application compatibility version starts at `1`. Deployment artifact
hashes remain separate in `[asap].[DeploymentState]` and do not change the
compatibility version by themselves.
