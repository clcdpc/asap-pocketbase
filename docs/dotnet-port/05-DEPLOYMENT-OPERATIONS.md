# Deployment and Operations

## 1. Production/nonproduction topology

Production and permanent nonproduction use separate Windows/IIS servers and separate SQL Server infrastructure/instances. The new .NET production server is different from the old PocketBase server and already exists.

Each .NET environment uses:

- dedicated ASAP IIS site;
- dedicated ASAP application pool;
- dedicated traditional domain service account as the app-pool identity;
- a **different environment-specific domain service account in production and permanent nonproduction from day one**;
- framework-dependent .NET 10 deployment;
- Windows Integrated Security to SQL Server;
- an external environment JSON file outside the application directory;
- persistent Data Protection key storage;
- local rolling application logs.

Use the existing public ASAP hostname after cutover.

## 2. IIS/server prerequisites

Provision once, outside the normal deployment script:

- .NET 10 ASP.NET Core Hosting Bundle;
- IIS site/app pool/bindings/certificate;
- app-pool domain service account and logon rights;
- filesystem directories/ACLs for app, staging, backups, logs, Data Protection keys, and external config;
- `AlwaysRunning` + preload for the app pool/site;
- production/nonproduction DNS and certificate prerequisites;
- SQL connectivity/firewall/Integrated Security permissions.

The normal deployment script does not create IIS sites, service accounts, SQL databases/logins, or infrastructure permissions.

## 3. Runtime vs deployment SQL identities

### Runtime

`Asap.Web` connects with an environment-specific IIS app-pool domain service account using Integrated Security. Production and permanent nonproduction use **different** runtime identities. The runtime identity is not `db_owner` and has no database backup, DACPAC, CREATE/ALTER schema, or other deployment privileges.

Deployment/release validation records both app-pool principals and proves they are distinct. Each principal must connect only to its intended environment database and must fail an equivalent cross-environment access attempt; both must also fail representative schema/backup/deployment operations. This is a day-one environment-isolation requirement, not deferred gMSA hardening.

Create purpose-built SQL roles/permissions for the application schema and Hangfire runtime needs. The baseline intent is only the DML/EXECUTE access actually required on `[asap]` plus the minimum rights required by the already-provisioned `[HangFire]` objects. Prove the effective permission set with integration/deployment testing rather than granting broad ownership for convenience.

Hangfire schema creation/upgrades run under the deployment operator using the version-matched Hangfire SQL initialization/upgrade assets; configure the production runtime so it does not need to create/alter Hangfire schema at startup.

### Deployment

The deployment PowerShell script connects to SQL using the **interactive administrator/operator's Windows identity**, which has the required DACPAC/backup/Hangfire-schema permissions. Do not grant those rights to the application identity solely to make releases easier.

Production/nonproduction SQL databases are pre-created and permissioned before deployment/cutover.

## 4. Connection strings

Canonical names:

- `AsapDatabase`
- `HangfireDatabase`

They may initially point to the same database/server but remain separate settings so Hangfire can be moved independently later.

## 5. External environment configuration

Set an environment/bootstrap pointer such as `Asap:ConfigFile` to an ACLed JSON file outside the app directory. Deployment never overwrites this file.

It includes environment-specific infrastructure/bootstrap values, not ordinary SQL-managed application configuration. SQL-managed configuration is split into the domain tables defined by `13-SETTINGS-SCOPE-INVENTORY.md`; there is no catch-all Settings table. Changes require application restart; there is no live reload. For AllowedTenantIds changes, stop/quiesce the app and validate a staged prospective configuration against the common current staff predicate, proving at least one usable super-admin before the operator replaces the external file/restarts. Deployment itself still never overwrites external configuration. Direct edits that bypass preflight are caught by the mandatory startup gate: no business endpoints or workers start, readiness fails, and the operator repairs the configuration. Existing cookies and queued sensitive mail recheck loaded tenant trust on next use; persistent key storage does not grandfather them.

Job configuration is first-class external operational configuration. `Hangfire:Schedules` contains the complete schedule keys in `01-PORTING-SPEC.md`. `Hangfire:ProcessingLimits` contains a required global default plus nullable timeout-family and queue-specific overrides. Effective precedence is queue-specific -> timeout-family for timeout queues -> global default. Defaults/ranges remain PageSize `50` (`1..500`) and MaxPerRun `500` (`1..5000`). Known target queue keys are `IdentifierProcessing`, `PurchasePromotion`, `HoldPlacement`, `FulfillmentTracking`, `OutstandingTimeout`, `PendingHoldTimeout`, `HoldPickupTimeout`, and `AdditionalCopyTimeout`. Startup/diagnostics validates the shape and reports effective values. Persisted QueueProgress is SQL-owned operational progress, not another external setting. HoldRecovery reuses HoldPlacement limits with its separate recovery-phase budget; recovery runs before ordinary workflow phases and continues for previously acquired inactive-library operations.

Local development uses an ignored `Development.local.json` with the same general structure.

See `examples/Config.example.json`. `04-MIGRATION-CUTOVER.md` and `examples/EffectiveLegacyOperationalConfig.example.json` define how existing PocketBase cron/queue-limit environment values are captured and reconciled before production jobs are enabled.

## 6. Data Protection

Persist ASP.NET Core Data Protection keys to an environment-specific protected location such as:

```text
C:\ProgramData\CLC\ASAP\DataProtection-Keys
```

Grant only the environment's app-pool identity and required deployment/migration administrators access. Protect persisted production/nonproduction keys with an **environment-specific X.509 key-encryption certificate** and a stable ASAP application-name/purpose convention. The certificate private-key ACL must be equally narrow. Production and nonproduction have separate key rings and separate key-encryption certificates. Do not bind durable production rings solely to machine-scope DPAPI because recovery must work on a replacement IIS host.

The key ring protects both cookie/antiforgery state **and reusable Polaris/Postmark credential ciphertext stored in SQL**. Therefore the key ring **and its X.509 certificate/private key** are durable recovery material: back both up securely, retain old Data Protection keys across rotation, and document importing the recovery certificate and restoring its private-key ACL before restoring/using a database on a replacement host. A SQL backup without the corresponding recoverable key ring/certificate may leave integration credentials undecryptable. Keep keys outside the deployed application directory so normal file replacement cannot remove them.

## 7. Logs

Suggested canonical production directory:

```text
C:\ProgramData\CLC\ASAP\Logs
```

NLog writes structured rolling files with:

- daily rollover;
- size-based rollover when a day becomes large;
- 30-day retention;
- correlation IDs;
- no secrets/PII/full barcodes/raw sensitive Polaris payloads.

Console logging remains enabled as useful for development/IIS diagnostics but is not the canonical long-term production log store.

## 8. Release artifacts

A version tag produces immutable artifacts.

### Application/deployment artifact

Contains:

- framework-dependent `Asap.Web` publish output;
- `Asap.Database.dacpac`;
- deployment PowerShell scripts;
- version-matched Hangfire SQL schema initialization/upgrade asset(s) needed for operator-owned provisioning, when required by the selected Hangfire storage package;
- PRTG sensor/check script;
- safe configuration documentation/templates;
- release manifest.

Does **not** contain:

- external environment config/secrets;
- `Asap.Migration` executable;
- PocketBase data/source runtime;
- Node/npm runtime/build dependencies.

### Migration artifact

Separate ZIP from the same tag containing a **self-contained `win-x64`** `Asap.Migration` publish and migration-only assets/documentation. It must include the SQLite native/runtime dependencies required by the old PocketBase server and must not require the .NET Hosting Bundle there.

### Manifest

At minimum:

- version/tag/commit SHA;
- expected `[asap].[SchemaVersion]`;
- build timestamp;
- file SHA-256 hashes;
- .NET target/runtime mode;
- DACPAC SHA-256 hash;
- exact Hangfire SQL storage package version, target schema version/shape, supported current and rollback storage compatibility, schema-asset hashes, and read-only detection of whether initialization/upgrade DDL is required; distinguish no-DDL package changes from schema migrations;
- migration artifact correlation/version and self-contained runtime identifier where useful.

## 9. Deployment script contract

A small explicit idempotent PowerShell script runs locally on the ASAP web server against an already-present immutable release ZIP. Downloading artifacts remains an operator concern. It must answer **Will this release mutate the production database?**, not only **Did its application DACPAC change?** Hangfire remains outside the application DACPAC.

1. Validate artifact structure/hashes/version, expected application SchemaVersion, DACPAC hash, exact dependency package/schema assets and compatibility metadata, external-config eligibility, and local prerequisites. Stage/extract outside the deployed directory; validation does not alter production DB/files.
2. Read actual application SchemaVersion and DeploymentState, installed file manifest, actual Hangfire storage schema/shape, and the target package's documented requirements. The dependency check is read-only; a check must not initialize/upgrade Hangfire as a side effect. Unknown/missing state is **not** proof of no change. Reject accidental downgrade or inconsistent artifact (including changed expected SchemaVersion with identical recorded DACPAC hash).
3. Set `DacpacChanged` from target/recorded hashes (missing state means changed). Independently set `DependencySchemaChangeRequired` from actual storage versus the version-matched target assets. Include explicit release data/backfill changes if any. `DatabaseChanging = DacpacChanged OR DependencySchemaChangeRequired OR ExplicitDatabaseChangeRequired`. If dependency state cannot be classified safely, block preflight pending operator resolution; never assume file-only.
4. For DatabaseChanging, stop/quiesce **all** relevant web/Hangfire workers and verify they stopped before any DDL or deployed-file replacement. Re-read actual DB state after stopping to ensure the plan still applies. For an existing production database/dataset, execute SQL backup under the operator identity and verify successful completion, correct database/recovery-point identity, discoverability and restore verification (`RESTORE VERIFYONLY` or the organization's equivalent verified-backup check). The destination is local to SQL Server and writable by its service account. Backup failure aborts before any DB/schema/deployed-file changes. A fresh empty first-cutover target may record the existing documented no-prior-dataset exception; an existing Hangfire database is not made exempt merely by an unchanged application DACPAC.
5. Only after that backup gate, apply the changed application DACPAC with data-loss blocking, any explicit release scripts, and required version-matched Hangfire schema assets under the deployment operator. Apply these in the tested artifact order, with all relevant workers still stopped. If app/Hangfire connection strings point to different databases, quiesce all relevant workers and verify backups for each existing database that will change **before the first DDL**. The runtime principal never gains schema/backup rights and automatic dependency schema preparation at startup remains disabled.
6. Verify expected application SchemaVersion **and** the actual dependency schema/shape against the target artifact's tested compatibility metadata. Only after every planned DB step succeeds, update DeploymentState's database-component hashes/versions and last database-release metadata. A partial failure does not record full success. Actual schema inspection, not bookkeeping alone, governs recovery/restart decisions.
7. If DatabaseChanging is false, take the genuine file-only fast path: no deployment-created SQL backup, DACPAC publish, dependency DDL, backfill, or DeploymentState write. The installed file manifest records the new app release; SQL bookkeeping continues to describe the last database deployment. Ordinary post-startup business/runtime writes are not deployment DDL. Stop the app pool before deployed-file replacement on this path too.
8. With workers stopped, make the timestamped app-file backup and replace files. Do not alter external JSON, key rings or certificate recovery material. In a normal release, validate configuration/usable-admin and both schema gates, start the app/workers, then verify live/ready/version/dependency health and least-privilege runtime operation. In `PrepareMigrationCutover` mode, leave workers stopped until explicit migration bootstrap and complete reconciliation pass.
9. Print the retained app backup, verified SQL backup(s) or precise file-only/fresh-target exemption, each DB component's actual/expected versions, recorded hashes, installed file version, and final stopped/running state. Do not automatically purge backups.

Dependency migrations use the selected package's own version-matched assets and operational precautions. A package version change does not automatically mean DDL, but a release cannot claim file-only without demonstrating that its actual storage needs no mutation. Do not move Hangfire tables into the application DACPAC to avoid this check.

## 10. Failure behavior

### Validation or backup fails before change

Exit without changing production database/schema/deployed files. App quiescence may already have occurred for backup; restart the untouched old artifact only after its configuration and both application/dependency compatibility checks succeed. Retain clear failure diagnostics.

### Any database step fails before new files are deployed

Leave the old app files untouched and all workers stopped by default. Inspect the resulting **actual** application and Hangfire schemas, including partial migrations; a version number left unchanged by a failed DDL script does not prove compatibility. An explicit operator restart of old code is permitted only after its expected SchemaVersion and documented dependency-schema compatibility are both proven. Otherwise repair forward or use the verified database recovery point; do not automatically start old or new workers against uncertain/incompatible storage.

### Readiness fails after database or file deployment

Stop/keep business and Hangfire workers disabled; do not automatically roll back code/database. Retain the staged/new files and restricted local diagnostics for repair-forward. Anonymous liveness may remain available when the diagnostic host can run safely, but normal functions remain blocked. Automatic rollback against a changed application or dependency schema is forbidden.

### Compatible file rollback

An explicit operator may restore a previously validated file artifact only when **both** its application SchemaVersion and dependency-schema support match the actual current database. A historical DACPAC hash difference alone is not a veto, but an equal SchemaVersion alone is not approval. Consult the artifact's exact Hangfire package/storage compatibility, including forward/backward and partial-migration limits; unknown means stopped pending repair. File rollback never silently publishes an old DACPAC or downgrades Hangfire schema. Restoring databases is a separate exceptional operator procedure with the usual production-write/data-loss boundary.

## 11. Health and monitoring

### Anonymous endpoints

`/health/live` and `/health/ready` are anonymous specifically to keep IIS/PRTG/deployment checks simple. Responses are intentionally sparse: healthy/unhealthy/status only, without server names, exception details, connection strings, or dependency diagnostics.

### Detailed diagnostics

Super-admin-only diagnostics should surface:

- app/version/expected vs actual SchemaVersion and target/current Hangfire storage compatibility;
- incomplete/operator-required hold state and scoped queue progress, with authorized Reconcile/resolution actions and no raw provider payloads;
- distinct completed/imported hold-tracking diagnostics for missing/ambiguous final HoldRequestID or provider-read failure (`01-PORTING-SPEC.md` section 9.2); historical BIB protection is not terminal correlation and missing identity alone is not an incomplete operation;
- SQL connectivity;
- Hangfire/config initialization and current-policy usable-super-admin gate;
- active Polaris connectivity/auth test;
- active Postmark/config test as designed;
- relevant cache/config state without exposing credentials.

External Polaris/Postmark outage does not itself make readiness fail.

### PRTG

Ship a small PowerShell sensor in the repo that calls `/health/ready` and emits valid PRTG XML, including explicit error XML when HTTP/request/parsing fails. Keep it simple and environment-parameterized.

## 12. Production staging and hostname cutover

PocketBase remains authoritative production during the interval after the .NET merge and before cutover. A critical PocketBase fix in that interval uses a temporary branch and/or tag from the exact last deployed PocketBase commit, follows the normal emergency test/deploy process, and is immediately ported into .NET `main`; no permanent PocketBase branch is maintained. Build and rehearse the replacement exact .NET artifact. If the fix affects PocketBase schema, stored-data semantics, migration input, or an export/import/reconciliation assumption, update the migration tooling/contract and repeat the full exact-artifact rehearsal before production cutover. After successful cutover, remove temporary branches and make the permanent final-PocketBase tag point to the exact commit that was actually frozen.

Before the final production migration:

- create a disposable preflight SQL database and deploy the exact release DACPAC to it;
- deploy/stage the intended .NET artifact on the new production server using a temporary preflight external config that points only to that disposable database;
- validate through a temporary/internal path as useful;
- from an administrator/test workstation, add a hosts-file override resolving the **real production ASAP hostname** to the new server;
- validate real hostname HTTPS binding/certificate, Entra redirect/issuer/tenant+object-ID authorization, cookies, antiforgery, patron CSP/embedding, static assets, deep links, staff login, and health without changing public DNS;
- stop the app pool, restore the real production config, destroy the disposable preflight database, and recreate/reset the final SQL database to the documented fresh migration-target state;
- do not start `Asap.Web` against that final target until the maintenance-window import/reconciliation has completed.

During the maintenance window, prepare the exact DACPAC/files with the app pool stopped, import the frozen PocketBase data, run the explicit active-bound-super-admin cutover gate (including migration-time provisioning/promotion of the configured bootstrap identity if needed), then complete reconciliation before starting the app. Never rely on normal startup bootstrap after StaffUser rows have been imported. After new-server checks pass, switch production DNS/hostname traffic to the new server.

## 13. Availability model

There is no general patron maintenance-mode feature in the initial .NET app. Ordinary upgrades are performed off-hours. During the one-time PocketBase migration, the old system is explicitly stopped so the dataset is frozen; this is accepted maintenance-window downtime.

Once a normal production deployment completes and readiness is healthy, the application may serve patrons while staff perform smoke validation. Do not add a separate staged "staff-only then patron-open" lifecycle.

After .NET accepts its first production write, never start the retired production PocketBase deployment as-is. Retained executable/data/config/backup material is kept for approximately 30 days as forensic/reference material only, and direct database/file inspection is preferred. A genuinely necessary executable investigation must use a separate isolated copy with outbound Polaris/email access blocked and all recurring production jobs disabled before startup. It can never serve as parallel read/write or fallback production.

## 14. Permanent nonproduction operations

- Internet-accessible intentionally for testing/demos.
- Uses its own Windows server and SQL infrastructure.
- No production data copied into it.
- Persistent SQL database upgraded release-over-release.
- Scheduled Hangfire jobs run normally using environment-specific schedules.
- The environment JSON must expose the complete schedule key set defined by the authoritative matrix in `01-PORTING-SPEC.md`: `WorkflowProcessing`, `IdentifierProcessing`, `OrganizationRefresh`, `WeeklyStaffSummary`, `EmailOutboxSweep`, `PatronSessionCleanup`, and `EmailPayloadCleanup`, plus the complete processing-limit shape. Startup/diagnostics should report missing/invalid schedules/limits and effective queue limits; all cron expressions use the configured business timezone.
- Clear persistent nonproduction/test visual banner in both patron and staff UI, controlled by external config and not an admin toggle.
- Email recipient domain allowlist enforced from external config.
- May initially share the Entra app registration and Postmark server/token with production, but uses its **own domain runtime service account and Data Protection key ring**.
- Deployment remains manual initially; automatic main->nonprod CD is deferred.

## 15. Repository/documentation transition

After first .NET production deployment is validated:

- rename repository `clcdpc/asap-pocketbase` -> `clcdpc/asap`;
- canonical docs describe .NET only;
- old PocketBase operational/setup docs are removed rather than maintained under a permanent legacy folder;
- the permanent final-PocketBase tag points to the exact PocketBase commit frozen at the successful production cutover, including any post-merge emergency hotfix, and Git history remains the archival reference.
