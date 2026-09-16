# Slice 8: Deployment, Health And Release Artifacts

## Current Authorized Scope - 2026-09-16

This packet is being reduced to repository-side **test-environment CI/CD to IIS**.
The prior production-grade specification below is preserved reference only and
is superseded for current execution by the user-authorized reduced Slice 8 and
[development-completion policy](deferred-production-readiness.md).

Start/technical review base: `88fd92cf1fdd856dccba6ef3538182d80325e98a`.
Branch: `codex/slice-08-test-deployment` into `codex/csharp-port`.
One cohesive Luna Max implementation; complete integrated validation, then one
fresh Terra High holistic review. No package reviews absent recorded exception.

Implement `v*.*.*-test.*` and `workflow_dispatch` exact-ref hosted build/test/package,
then a deployment job gated by repository variable
`ASAP_TEST_DEPLOYMENT_ENABLED == 'true'`. Unset/empty/false skips deployment.
PR and ordinary branch CI must never require self-hosted runners. Future labels:
`self-hosted`, `windows`, `x64`, `asap-test-iis`. No IIS-host checkout/build/npm.

The small ZIP contains Web publish, DACPAC, deployment PowerShell and a manifest
with label, exact commit, UTC build time, DACPAC SHA-256 and web file identity.
Verify final ZIP SHA-256 before mutation and exact expected manifest identity.
Exclude source, secrets, external config, keys, Git, Node, PocketBase and migration
executables/packages. Load host settings outside the site, preferably
`C:\ProgramData\ASAP\test-deployment.json`; successful state lives beside it.

Preflight staging, identity, payload, IIS/pool/path, external configuration,
SqlPackage and prior state before mutation. Stop/verify pool before changed
DACPAC publish with data-loss blocking; unchanged DACPAC skips publish. Back up
old web files without pruning, replace the complete payload from staging, start
the pool and require bounded HTTPS `/health/ready` HTTP 200 healthy polling with
normal TLS validation. Write state only after readiness. Preflight failure leaves
the running site untouched; DACPAC failure leaves old files and stopped pool;
file failure leaves pool stopped; readiness failure retains new files/diagnostics.
No automatic SQL backup/rollback, production classifier or SQL DeploymentState use.
Preserve existing `/health/live` and `/health/ready`, including Hangfire compatibility.

Runner installation/registration, contacting the IIS host, host-local setup and
the first real deployment are explicitly excluded. Document later activation.
Acceptance requires repository tests/review/exact-milestone CI, never a runner.
Record `test_cd_activation: pending_runner_setup`; do not claim live deployment.
Do not merge PR #264 or implement Slices 9, 10 or 11.

## Historical Production-Readiness Specification

## Preparation Only

Not dispatched. Start only after Slices 1-7 pass their complete tests,
independent review/fix/re-review gates and milestone commits. Refresh the
actual schema versions, dependency assets, configuration/eligibility APIs,
migration artifact contract and installed-state model before dispatch.
Follow [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) for the
staged-PR lifecycle, optional package boundaries, PR-backed durable state,
review modes, context firewall and exact-milestone CI. GPT-6 Astra High is the
default thin supervisor, dispatching bounded Luna Max and independent Terra
High workers. GPT-6 Astra Max is bounded escalation only, returning control to
High. The slice PR holds the central state/event journal under document 10;
package PRs do not duplicate it. Manual/direct execution remains fallback. Reading lists
below identify full-slice authority; workers start with directly relevant
excerpts and expand for concrete concerns. Prior-slice acceptance and its
exact-SHA CI must be complete before this slice starts.
Package guidance in document 10 is provisional; choose actual boundaries at
slice start.

## Objective And References

Deliver the explicit local-operator deployment and recovery workflow, immutable
correlated release artifacts, safe health/diagnostics and PRTG readiness sensor
defined by document 05. Extend the accepted engineering baseline rather than
adding deployment infrastructure, automatic CD or another service framework.

Read root AGENTS, document 10, document 02 Slice 8, all of document 05,
document 04's first-cutover/fresh-target/recovery boundaries, document 08's
release validation, document 06's failure/recovery/permissions tests, and the
existing release/configuration examples. Inspect the accepted DeploymentState,
SchemaVersion, readiness, Data Protection, operator Hangfire assets and runtime
storage setup. The exact dependency package source/assets resolve storage
compatibility; a package-version string alone does not prove DDL requirements.

This is an intentional platform/deployment replacement. Do not invent legacy
deploy-script behavior: the pinned PocketBase tree has no deployment scripts
to preserve. Keep exact deployed PocketBase SHA tracking and the emergency
hotfix/final-tag procedure from document 10 intact.

## Artifact And Operator Contract

- Build the framework-dependent Web/DACPAC/deployment ZIP and separate
  self-contained win-x64 migration ZIP from one exact version tag. Correlate
  version/tag/commit, expected SchemaVersion, build time, runtime mode, file
  hashes, DACPAC hash and exact dependency package/schema/asset compatibility
  metadata. Local development verification is not a tagged release claim.
- Include deployment scripts, operator-owned Hangfire assets, PRTG sensor and
  safe templates/docs in the web artifact. Exclude real external config,
  secrets, key rings, local email previews, migration executable, PocketBase
  runtime/data and Node/npm. Validate the separate migration artifact's native
  SQLite dependencies and self-contained mode without installing it in IIS.
- Deployment runs on the web host against an already-present local ZIP under
  the interactive Windows operator identity. Infrastructure, SQL databases/
  logins, IIS site/pool, accounts, Hosting Bundle and permissions are provisioned
  separately. Do not add downloading, server provisioning or account management
  to the normal deploy path.
- Stage and validate artifact hashes/structure, external configuration,
  current usable-admin policy and local prerequisites without mutating live
  SQL/files. External JSON is outside the deployment and is never overwritten
  by it. Data Protection keys/certificates and logs also survive file replacement.

## Database Classification And Failure Contract

1. Read actual application SchemaVersion/DeploymentState, installed file
   identity and actual Hangfire schema/shape through read-only checks. Missing
   or unknown state is not evidence of file-only compatibility. Reject
   inconsistent manifests/downgrades, including changed expected SchemaVersion
   with an identical recorded DACPAC hash.
2. Independently derive DACPAC change, actual dependency DDL requirement and
   explicit database-change requirement. DatabaseChanging is their OR. A
   dependency package upgrade can be no-DDL, but that must be proved from the
   actual storage and version-matched requirements. Unknown classification
   stops preflight; it must not trigger initialization as a side effect.
3. For database-changing work, stop and verify all relevant web/Hangfire workers
   before DDL or deployed-file replacement, then re-read state. Verify a backup
   of every existing database that will change before the first DDL, including
   separate application/Hangfire databases. Verify database/recovery identity,
   discoverability and restore verification. The backup destination is local
   to SQL Server and writable by its service identity, not the IIS identity.
4. Only a genuinely fresh empty first-cutover target can use the documented
   no-prior-dataset exception. Unchanged application DACPAC does not exempt an
   existing Hangfire database from backup/quiescence when dependency DDL is
   needed. Backup failure leaves SQL/schema/deployed files unchanged.
5. Apply changed DACPAC with data-loss blocking, explicit release SQL if any,
   and required exact dependency assets in the tested order. Verify actual
   application and dependency compatibility before updating DeploymentState
   database-component metadata. Never record complete success after a partial
   database failure. Runtime schema/backup privileges remain forbidden.
6. Proven file-only deployments create no SQL backup, publish no DACPAC, run
   no dependency DDL/backfill and write no DeploymentState. Stop workers before
   file replacement, record new installed file identity separately and retain
   a timestamped app-file backup. SQL state continues to identify the last
   database deployment. Ordinary later business writes are distinct.
7. Normal release startup requires configuration/current usable-admin and both
   schema gates, then sparse liveness/readiness and least-privilege checks.
   PrepareMigrationCutover leaves the pool/workers stopped through migration
   bootstrap, reconciliation and explicit later activation.
8. On validation/backup failure, only restart untouched old code after proving
   its configuration and both schemas compatible. On any database-step failure,
   leave old files untouched and workers stopped by default. Inspect actual
   partial schema state; unchanged version metadata is not compatibility proof.
9. Readiness failure after deployment leaves business workers stopped and
   new/staged files plus restricted diagnostics available for repair-forward.
   No automatic code/database rollback. Explicit file rollback needs both the
   old artifact's application and dependency compatibility against actual SQL;
   it never publishes an old DACPAC or downgrades Hangfire implicitly.
10. Report retained app/SQL backup locations or precise exemptions, actual/
    expected versions, recorded hashes, installed file version and final pool
    state without secrets. Do not automatically delete backups.

## Health, Isolation And Runbooks

Keep anonymous live/ready responses sparse and external provider outages out
of readiness failure by themselves. Super-admin diagnostics use current
authorization and expose safe version/storage/initialization/queue/hold-tracking
state plus deliberate provider tests, without sensitive raw payloads. Missing
final hold identity is distinct from an incomplete placement operation.
The small parameterized PRTG script always emits valid XML, including failure
XML for HTTP, timeout and parsing errors.

Document distinct production/nonproduction traditional domain runtime accounts
from day one, Windows-integrated SQL, minimum asap/Hangfire DML/EXECUTE roles
and operator-only backup/DDL. Validate that each real principal can access only
its own environment and cannot perform schema/backup/deployment operations.
Document separate persistent key rings and X.509 certificates/private-key ACLs,
protected backups and replacement-host recovery for both cookies and encrypted
SQL integration credentials. SQL backup alone is insufficient recovery material.

Production-hostname/TLS/Entra preflight uses only a disposable SQL database and
temporary config. Stop the pool, restore final external config and destroy the
preflight database before resetting/preparing the fresh migration target.
Do not let staging bootstrap dirty the final import target. No production
write occurs until all explicit migration/eligibility/reconciliation gates pass.

Preserve the configured nonproduction banner in patron/staff UI independently
of Testing authentication. Permanent nonproduction remains PocketBase until the
complete reviewed port is merged/tagged; it is never seeded with production
data. Post-write recovery never restarts retired PocketBase as a fallback.

## Acceptance And Boundaries

Test the real SQL classifier and failure ordering across file-only, DACPAC,
Hangfire-only, combined and separate-database changes. Cover unknown/partial
schema state, failed backups/DDL/readiness, unchanged versions with changed
shape, compatible/incompatible explicit rollback and stopped first-cutover
preparation. Prove file-only produces no deployment SQL writes and that all
required backups precede the first mutation. Preserve strict runtime rights.

Incorporate `hold-resolution-operator-evidence.md` from Slice 2 into the actual
IIS/worker operations runbook. Rehearse verification that all affected current
and overlapping/superseded interactive/background processes have ended before
attesting executor exclusion, while separately accounting for already-sent
provider work. A stopped schedule, cancellation request or expired SQL lease
does not certify termination or no effect. Preserve operator evidence references
and refresh the operation version after the validated restart; no force retry.

Validate release content/hash failures, retained external config/keys/backups,
sensor error XML, sparse versus privileged diagnostics and safe startup failure.
Use disposable local fixtures for destructive tests. Real IIS principals,
replacement hosts, exact tagged rehearsal and production source identity must
have actual evidence; absent external access is not a passed gate.

The authorized FileEmailSender remains temporary. Real cancellable Rest 3
Postmark integration, provider/webhook tests and final transport/rehearsal
validation remain explicit release blockers, not simulated successes.
No production tag/version, final-port merge, permanent-nonproduction deployment
or cutover is authorized merely by completing this implementation slice.

Run the complete integrated slice validation before holistic Terra review.
Follow document 10 for package/fix validation, direct tasks and staged
integration. Return exact changes, compact receipts and remaining evidence
gaps at the authorized branch/PR checkpoint. Short Astra acceptance precedes
slice integration; the exact resulting milestone's remote CI gates progression.
