# Asap.Database

This SDK-style `Microsoft.Build.Sql` project is the source of truth for
ASAP-owned SQL Server objects. It targets SQL Server 2022 (`Sql160`) and owns
only the `[asap]` schema. Hangfire owns `[HangFire]` and is deliberately absent.

The application compatibility version is `12`. Native schema versions `7`
through `11` upgrade in place; versions below `7` and above `12` are rejected.
Deployment artifact hashes remain separate in `[asap].[DeploymentState]` and
do not change the compatibility version by themselves.

`[asap].[Organization]` stores Polaris `OrganizationCodeId` and
`ParentOrganizationId` as nullable native reference values. Only organization
1 is the system scope; an organization is a library only when its native code
is `2`. Existing rows with no native code remain non-authoritative until a
trusted Polaris reference refresh classifies them. Other native rows, including
branches, stay available to preserve historical references.
