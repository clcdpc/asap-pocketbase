# Current development status

ASAP is the .NET 10 / ASP.NET Core 10 application under src/Asap.Web, backed by
SQL Server 2022 and the schema-13 DACPAC. main is the integration branch.
The tracked browser source is src/Asap.Web/Frontend; generated wwwroot is ignored.
Entra identity, scoped authorization, current configuration and durable hold,
pickup and email evidence remain required application contracts.

## Proof-of-concept retirement

PocketBase was exclusively an internal proof of concept, never production, and
there is no historical data to migrate. PR #422 retires its migration executable,
source contracts, mapping table, imported-record protection bit and historical
schema 7–12 upgrade transformations. These are not future release requirements.
Git history preserves the previous porting pack and milestone journals.

Fresh deployment and repeated schema-13 deployment preserve current data and
configuration. Unsupported schema versions fail explicitly; neither the web
host nor the deployment scripts automatically reset an existing database.
Future upgrades must be designed deliberately for supported production versions.

## Qualification

Current .NET/SQL tests, browser and frontend-serving contracts, the complete
frontend suite, and current-commit publication/package/ValidateOnly checks
remain mandatory. Exact native test discovery and outcome identities qualify
the complete population. Browser and non-browser jobs own independent SQL
containers and credentials; the required gate waits for all three qualification
workers and validated packaging.

Polaris protocol cases run in a hostless fixture with real protected-credential
configuration. Shared mutable SQL fixtures remain serial. The separate
acceptance runner and deterministic provider boundaries remain available.

See [CI contracts](../../scripts/ci/test-selection.md) and
[implementation and evidence](ci-simplification.md). Earlier milestone counts
and reviews are historical evidence in Git; they are not current test inventory.

## Deployment boundary

`test_cd_activation: active` records the verified initial activation of
`v1.0.0-test.32` at `46c60a8cdb1218f34622620cbf0b31cc92e6bd07` on November 5,
2026 at approximately 7:23 p.m. Eastern. See the
[exact deployment receipt and its limits](test-iis-activation.md).
Current installed source and host health have not been verified by this work.

Repository qualification does not activate IIS, provision runners, create
production tags, deploy production or merge implementation PRs. Live Polaris/Postmark,
production release/cutover, backup/recovery rehearsal and repository rename
remain separately authorized future work.
