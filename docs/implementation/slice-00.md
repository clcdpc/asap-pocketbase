# Slice 0: Engineering Baseline

## Ownership And Scope

Astra orchestrates; a fresh GPT-5.6 Sol High context implements the complete
slice; a fresh GPT-5.6 Terra High context performs full-slice Pass 1 and Pass 2
(and further required re-review after substantive fixes). No milestone commit
until tests and review pass. Prior application contracts: none implemented.

Build a runnable, green foundation for the four canonical projects and local
F5. Business features scheduled for later slices must remain absent/disabled.
Do not expose a copied patron/staff screen with nonfunctional backend calls.

## Authoritative References

Read root `AGENTS.md` and the relevant pack sections in full:

- `02-IMPLEMENTATION-PLAN.md`: Slice 0 and slice/review completion rules.
- `01-PORTING-SPEC.md`: sections 2-6, 12.1-14 (configuration/security boundaries),
  24-25, and the exact external operational shape in section 23.
- `03-DATABASE-DESIGN.md`: schema/version/deployment foundation, section 2.
- `05-DEPLOYMENT-OPERATIONS.md`: external config, Data Protection, logging,
  runtime/operator separation, dependency compatibility and failure boundaries.
- `06-TESTING-CI.md`: baseline CI, SQL 2022, local F5, safe recipient-domain
  configuration validation. Full outbox integration tests belong to Slice 1.
- `07-API-FRONTEND-COMPATIBILITY.md`: exact asset preservation/copy strategy.
- `10-CODEX-MULTI-MODEL-TASK.md`: model ownership and milestone gates.
- `13-SETTINGS-SCOPE-INVENTORY.md`: external versus SQL configuration boundary.
- `scripts/deployment/Initialize-AsapTestHost.ps1`: embedded operational configuration shape, not usable credentials.

## Exact Legacy Anchors

Use `git show 150b30b776565194260cc327eeeffdfb46475e81:<path>` when preserving
behavior. `main` matches this pin at packet creation; working branch contains
only instruction/documentation changes above it.

- `pb_public/patron/index.html`: Bootstrap 4.1.3, Font Awesome 4.7.0.
- `pb_public/staff/index.html`: the same CSS plus Grid.js 6.2.0 CSS/UMD.
- Inspect other actual browser asset references for transitive fonts/assets
  before vendoring; retain package licenses and record exact versions/hashes.
- `package.json`, `package-lock.json`, `pb_public/package.json`, and
  `tests/run_all.js`: Node is only a development/test dependency.
- `clc-carousel-manual-import-example/` is unrelated; remove it now, verifying
  the resolved deletion path stays within this repository.

## Implementation And Acceptance

1. Add `Asap.sln`, `src/Asap.Web`, `database/Asap.Database`,
   `src/Asap.Migration`, and `tests/Asap.Tests`; target .NET 10. Installed SDK
   is 10.0.303. Use feature-oriented web ownership, direct EF Core where needed,
   no repositories/MediatR/new layered projects, no EF schema migrations.
2. SDK-style Microsoft.Build.Sql DACPAC, SQL Server 2022/compatibility 160,
   `[asap]`, singleton SchemaVersion and DeploymentState. Hangfire remains
   outside DACPAC; runtime never gets automatic DDL/schema provisioning.
   A version mismatch or unavailable SQL keeps liveness healthy, readiness
   unhealthy, and normal app functions blocked. Deployment hashes are distinct
   from the monotonic application compatibility version.
3. Explicit startup-only external JSON via `Asap:ConfigFile`; ignored local
   `Development.local.json`. No committed real credentials or operational
   settings copied into SQL. Invalid configuration must fail safely and yield
   useful restricted diagnostics. Minimal anonymous health exposes no details.
4. Validate the independent `Environment.IsNonProduction` switch and exact
   case-insensitive recipient domains. Missing/empty list permits nobody in
   nonproduction; explicit subdomains only; malformed domains fail validation.
   This switch never enables test authentication. Preserve the seven schedule
   and eight processing-queue key shapes without starting unimplemented jobs.
5. Persistent environment-specific Data Protection with portable X.509 key
   encryption for durable environment rings and a stable purpose/application
   name shared by future migration and web secret protection. No machine-bound
   DPAPI for durable production/nonproduction rings. NLog local rolling logs,
   30-day retention, size rollover, console diagnostics, safe correlation IDs.
6. `Frontend/` tracked source, ignored generated `wwwroot`, incremental MSBuild
   copy for build/publish/F5 without invoking Node/npm. Vendor exact source
   browser dependencies locally with fonts/licenses. Features not implemented
   yet are absent, not knowingly broken. No UI redesign.
7. Local Development/F5 may create a missing dedicated local database and
   deploy DACPAC using the developer's configured SQL Developer connection.
   No fixed instance name, no silent destructive reset. A data-loss publish
   fails and retains data. Do not initialize real operational credentials or
   add a Development auth bypass. SQL Server is running locally; Astra is
   checking its version and access separately.
8. Add compile/unit/real-SQL CI foundation. Real persistence tests deploy the
   DACPAC into isolated SQL Server 2022 databases. No InMemory/SQLite stand-in
   and no silent skip counted as pass. Cover singleton/version constraints,
   health/config failure boundaries, safe credential round-trip where provided,
   and build/publish asset behavior. Keep legacy relevant tests green.

## Migration Impact

Establish the permanent, separately publishable `Asap.Migration` console
project with an honest baseline/help/version surface, exact source/contract
identity, and a focused tested structural foundation if useful. It must not
claim export/import of entities not implemented yet. No runtime/business
entities are owned by this slice; their transforms arrive with their slices.
Self-contained win-x64 packaging remains a release requirement. Preserve the
source pack's checksum manifest; implementation evidence stays outside it.

## Evidence To Return

List changed paths, actual commands/results, SQL edition/version and isolated
database evidence, frontend/vendor source provenance, migration scope, and
remaining risks. Do not commit or push; Astra handles milestone/PR progression.
Do not write in `docs/implementation/PORT-STATUS.md` or this packet while Astra
maintains them. Use `apply_patch` for manual edits. No production operations,
deployment, branch merge, or legacy feature changes belong to this slice.
