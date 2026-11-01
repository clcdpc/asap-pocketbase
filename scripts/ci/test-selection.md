# Required CI test selection

`selection.py` owns change-aware selection, category filters and native discovery.
`reviewed-test-categories.json` is the reviewed inventory of methods allowed outside
the core partition. It is an audit/completeness check, not a method-name execution
filter. MSTest method-level `TestCategory` attributes select the partitions.

## Coverage and classification

Release discovery currently finds 1,547 cases:

| Partition | Cases | Routine unrelated change | Full qualification |
| --- | ---: | --- | --- |
| Core (unclassified) | 1,435 | Required | Required |
| `HistoricalMigration` | 80 (69 methods) | Omitted by policy | Required |
| `HistoricalUpgrade` | 15 (11 methods) | Omitted by policy | Required |
| `Browser` | 17 (16 methods) | Required | Required |

These are observed counts, not fixed acceptance thresholds. Every run discovers
the current population and derives its counts. All 64 root frontend test files
remain required. No test method or assertion was removed.

The migration category covers SQL import, reconciliation, report recovery,
source transformations, compatibility and the post-import runtime oracles in
`MigrationCliTests`. The 18 lightweight methods remain core: help, contract,
unknown-command handling, duplicate JSON validation, overflow/legacy parsing,
stopped SQLite export and WAL/source identity, missing collections, frozen
configuration, external configuration requirements, branding export, tamper and
path validation, initialization precedence, malformed source configuration and
staff authentication preflight. They require no SQL database initialization.

The upgrade category covers schema 7/8/9/10/11/12 to 13 preservation/remediation,
unsupported schema-6 rejection, old text-column rejection, the pickup-journal
normalization, blocked deployment preflight, and the historical email-default
upgrade. The JSON inventory lists every exact method, including methods with
multiple data rows.

Current SQL coverage remains core, including every unclassified method of
`DatabaseBaselineTests` and `EmailDefaultsDeploymentTests`:

- SQL Server 2022/compatibility-160 and schema-13 deployment, singleton rows and
  DACPAC identity; repeated deployment preserving data and unowned schema;
  recovery-model preservation and explicit compatibility.
- Singleton/native identity constraints, runtime DML and lock permissions with
  no schema-control grant, missing-schema rejection, blocked data loss and exact
  readiness/configuration checks.
- Active staff email uniqueness, authentication eligibility/tenant/participation,
  authorization-sensitive outbox address kinds, email purge retention, claim
  snapshots and ordered configuration.
- Fresh five-template email defaults and idempotent plain-text defaults across
  scopes. All active `PatronJourneyTests`, authorization/isolation tests and
  other runtime integration tests remain mandatory.

Keep new tests **unclassified** by default. Do not place historical/browser
categories on a class or assembly: a new method must stay core until reviewed.
To deliberately classify a method, inspect its dependencies, add its category
attribute and exact fully qualified name to the inventory, update the selector's
dependency rules if needed, and run the selection contracts. Inventory/category
changes force fresh full qualification. Normal unfiltered `dotnet test` still
executes every category.

## Change and event policy

| Change | Additional required history |
| --- | --- |
| Web frontend, unrelated feature implementation, integration/unit tests or browser scripts | None |
| `src/Asap.Migration/`, `tests/Asap.Tests/Migration/`, migration fixtures/contracts | Both, fresh full qualification |
| `PatronConfigurationService*`, `PatronMessageDefaults*`, `Staff/TitleRequestEndpoints*` | Migration (consumed by post-import oracles) |
| `tests/Asap.Tests/Sql/`, `database/` | Both, fresh full qualification |
| `PatronSessionService*` | Upgrade (session identity is consumed by the schema-upgrade oracle) |
| `src/Shared/`, Web `Infrastructure/` | Both (EF/configuration/security/deployment dependencies) |
| Workflow, CI selection/evidence/safety tooling, build/deployment scripts or project/dependency definitions | All, fresh execution |
| Any other/new dependency path | All, fresh execution |

The exact rules and precedence live only in `paths_policy`. Unknown paths are
conservative, including fixtures outside the recognized migration fixture path,
shared test helpers, new serialization code, bootstrap/build definitions and
unreviewed infrastructure. A newly introduced dependency consumed by a historical
test must be added to this audit when that test/consumer changes.

Ready trusted PRs apply this policy to their exact synthetic merge. The checkout
fetches full history. Selection requires event and current server base/head to
match the two merge parents, a unique merge base, and trusted ready PR metadata.
The diff starts at the common ancestor and ends at the tested merge, so integration
base changes since the branch fork are included. Rename detection is disabled:
both the deleted and added dependency paths participate.

`main` pushes use the same path policy over the exact `before..after` integrated
tree, with a proven fast-forward ancestry. An ordinary merge need not repeat
historical tests after an unrelated PR. Other retained branch pushes, empty or
ambiguous diffs, force pushes, missing objects/metadata, stale PR base/head,
unsupported events and selection API failures require all categories afresh.
Discovery/partition verification failures fail the job; they cannot produce a
successful reduced run.

Draft PRs keep the existing build/SQL-independent unit/frontend validation and
distinct **Draft CI result**. The hosted current-head/author/repository trust
check still precedes allocation of any code-executing PR runner. The stable
**Build, test and package** check requires successful ready-policy execution;
its name does not mean that history was selected on every unrelated change.
PR cancellation groups and current-commit publish/package verification remain.

Every manual dispatch selects **all** categories. `force_full_ci: true` also
bypasses evidence reuse for all three suites. All `v*.*.*` tag pushes and requested
test-IIS qualification require all tests afresh. Only existing `-test.` tags or
an explicit deployment request can reach the existing test-IIS activation gates;
ordinary release tags do not activate IIS. This task does not enable deployment,
create a release tag or alter `test_cd_activation: pending_runner_setup`.
There is no scheduled full run.

The hosted preflight computes test-tag eligibility with a case-sensitive match
of the former `v*.*.*-test.*` filter. This preserves its exact spelling and slash
rules: Actions string [functions are case-insensitive](https://docs.github.com/en/actions/reference/workflows-and-actions/expressions#functions),
so an expression substring check alone would broaden deployment eligibility.

## Population and result verification

After build and mandatory safety checks, `selection.py prepare` invokes the built
MSTest 4.4.0 / MTP 2.4.0 application with `--list-tests json --no-ansi`. SDK 10.0.303's
`dotnet test --list-tests json` consumes the JSON argument and returns text, so
structured discovery intentionally invokes the built DLL directly. Execution
still uses `dotnet test --project ... --configuration Release --no-build`.

Preparation discovers the unfiltered assembly and each of the four partitions.
It rejects empty partitions, duplicate UIDs, overlapping partitions, missing
union members and identities inconsistent with the independent reviewed method
inventory. Core must equal the unfiltered population minus the reviewed methods;
a new unclassified test therefore cannot disappear. It also independently
discovers the effective execution filter and compares that set to the required
union of partitions. Expected counts and native UID hashes are saved in
`.artifacts/test-selection/selection.json` before lookup or execution.
The adjacent `catalog.json` lists every exact case UID, fully qualified method
and data-row display name by partition for review; it does not authorize success.

The gate repeats the decision and full discovery, checks the current commit and
unchanged population, and validates native TRX counters, outcomes, unique execution
IDs and exact test IDs (MTP UIDs). Equal counts with different tests fail. Missing,
skipped, failed, duplicated or unexpectedly omitted tests fail. Full qualification
must cover the complete non-browser union plus the independently verified browser
partition. A reduced result cannot satisfy that gate. Frontend discovery verifies
the complete sorted root test-file list and complete passing results.

The two Node safety files execute once before lookup via `--ci-safety`. A cold
`npm test -- --ci-remaining` executes the other files and verifies/combines the
fresh safety receipt into the complete frontend report. Standalone `npm test`
still executes all files. Mandatory Python discovery runs all `test_*.py` once
before reuse; the former second, optional overlapping discovery is removed.

## Evidence and diagnosis

[Test reuse](test-reuse.md) retains its original provenance, digest, lifetime,
environment and trust checks. Schema 2 intentionally rejects schema-1 evidence.
Fingerprints include category selection, exact selected UID hashes/counts, the
full discovered population/classification identity and all selection tooling.
Different non-browser category selections cannot share .NET/SQL evidence, even
if their counts match. Browser/frontend evidence remains compatible when its own
exact required population and all other inputs match.
A force-full, release, high-risk or conservative fallback run cannot reuse.
Lookup failures still execute the selected required tests; a failed selection
never becomes a reuse hit.

The summary distinguishes **EXECUTED**, **REUSED** and **OMITTED BY POLICY**.
Omission is not a passing test and emits no evidence for that category. Evidence
always identifies the exact selected population. The verification artifact also
contains `selection.json`; inspect its `decision.reason`, `base_paths`, category
counts and identities when a run unexpectedly selects all tests. Compare event
and current base/head, Git ancestry, changed dependencies and inventory changes.
Do not override a fallback by weakening a filter or minimum.

## Controlled measurement

For a fresh all-category run, dispatch `.NET baseline` on the task branch with
`force_full_ci: true`, `deploy_test_iis: false`. Optionally set
`benchmark_core: true`. After successful full execution, the independent **Controlled core
benchmark (not qualification)** job builds the same SHA in another job, exercises
the selector with a controlled unrelated frontend path, and runs the cold core
population against a new SQL container using the qualification job's SQL image.
It requires a clean tracked checkout at the recorded commit before execution and
again before recording results.
It verifies native results and uploads `core-benchmark-<run>-<attempt>` with
performance, selection and benchmark-only results. It emits no reusable evidence,
publication/deployment artifact or required-validation result, and cannot replace
the full job. Full frontend/browser and publish/package results belong to the
qualification job. Compare SQL step wall time separately from job/setup durations;
this SQL-only benchmark is not a measured routine end-to-end CI job.

October 30 baseline `41102402688` has 1,530 passing non-browser cases, no skips,
peak measured concurrency one and 58m55s approximate SQL wall time. Matching the
reviewed categories against that native TRX gives 80 migration cases / 24.073
minutes, 15 upgrade cases / 3.971 minutes and 1,435 retained core cases / 30.295
minutes of **summed test durations**. The 28.044-minute historical sum is an
optimization estimate, not a new timing result. Migration fixture telemetry
records 104 isolated DACPAC deployments totalling 440.166 seconds, all successful.
Selection removes that setup from unrelated runs without sharing mutable
databases or changing cleanup. Further fixture reuse is deliberately deferred:
fresh-target migration tests and destructive upgrade tests require isolation.
The task PR records fresh run receipts and limitations. These baseline sums do
not establish achieved savings for the revised workflow.
