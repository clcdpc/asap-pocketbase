# CI simplification and current-application qualification

## Product decision and supported state

PocketBase was an internal proof of concept, never deployed to production, with
no historical data to import. Its executable, source snapshots, normalization,
mapping IDs, imported hold flag, and development-schema upgrade contracts are
retired. Git history preserves that implementation.

The application remains .NET 10 / SQL Server 2022, schema 13. The DACPAC owns
DDL. Fresh deployment and repeated schema-13 deployment remain supported.
Older/newer versions or an existing application database without schema identity
fail before deployment can alter application data. There is no automatic reset.
Only explicitly owned disposable test databases may be recreated.

Native bigint staff links retain parsing, type, authorization, library scope,
and normal not-found behavior. Current hold/pickup journals, adoption, ambiguous
outcomes, cancellation, leases, duplicate prevention, and operator recovery
remain application contracts. Successful hold evidence blocks another placement,
reopening, and deletion, including evidence arriving during preflight or after
acquisition without changing the request rowversion.

## Coverage and ownership

Use `python -B scripts/ci/selection.py prepare` after a Release build to discover
the current native partitions and frontend file population; `catalog.json` and
`selection.json` under `.artifacts/test-selection` record them. File counts are
distinct from node:test subtest counts. Discovery partitions the entire native
population, rejects overlaps/omissions, and verifies every executed test identity
against TRX, including duplicate, skipped, and same-count substitution failures.

- Removed 98 migration cases (80 historical SQL cases and 18 lightweight cases)
  and 14 tests exclusively exercising obsolete pre-production schema upgrades.
- Rewrote the mixed email redeployment case as current-schema data/evidence
  preservation. Kept current deployment, data-loss, seed, startup and Hangfire
  ownership checks; added eight unsupported-version cases.
- Replaced imported browser/runtime fixtures with native SQL settings, formats,
  custom fields, rule ownership, staff permissions, request/copy, journal and
  outbox fixtures. Preserved their editing, inheritance/reset, retired-option,
  automatic-claim and HTTP/SQL workflow assertions.
- Replaced legacy link fixtures with native bigint links, including values
  beyond JavaScript's safe integer range, desktop/mobile failures, revoked
  identity, deleted and out-of-scope records.
- Replaced the mixed imported hold-flag test with current successful-journal
  coverage and two additional late-evidence race tests. Preserved stale version,
  reopen/delete and current actor revocation assertions.
- Moved 88 methods / 332 unchanged protocol cases into PolarisProtocolTests.
  They run without PatronJourneyTests initialization or an ASP.NET host. Their
  real protected SQL configuration is seeded once in an independently owned
  database; controlled HTTP handlers still exercise production provider code.
  Configuration, authentication, credential loading, DI and cross-service
  journal workflows also retain real integration coverage.
- Browser includes every Chromium consumer plus frontend asset/cache/publish
  contracts. PatronJourneyTests retains DoNotParallelize and per-test host
  ownership. No mutable factory or shared SQL database is parallelized.

The retained acceptance runner uses native fixture keys and successful current
hold journals. Run smoke, s1, d1 and q1 against the desired committed source with
scripts/review/Invoke-AsapReview.ps1. These runtime suites remain separately
invoked; the portable authentication/runner contracts are part of normal npm
and frontend qualification via `tests/frontend-tests.json`.

## Execution and evidence

Full CI uses three concurrent standard GitHub-hosted Ubuntu 24.04 workers after trust/current-head
preflight: frontend/CI safety/package, non-browser .NET/SQL and browser/SQL.
Each independently checks out and builds the exact event SHA in Release with
pinned configuration. Separate compiled outputs are accepted; they need not
be byte-identical or transferred. The frontend job owns current-commit
publish/package/ValidateOnly. Each native job owns its SQL Server 2022 container,
ephemeral password, dynamic loopback port, serial fixture/test commands,
independent databases, diagnostics and unconditional owned cleanup. SQL and
Chromium preparation occur only when the assigned suite needs fresh execution.
The hosted Build, test and package reporting gate requires all three successful
workers, explicit verified coverage of every suite and package validation/upload.
Draft CI result stays distinct. No shared build or extra aggregation runner is used.
Draft validation also uses Ubuntu 24.04. DEVWEB/IIS deployment stays on its local
Windows runner. A new hosted environment epoch and exact runner label/group name
and ID reject local-runner evidence. Existing trust, provenance and retry checks
remain; no new reuse mechanism is introduced. Capacity checkpoints and command
CPU/memory/disk-free samples qualify the advertised 4-vCPU/16-GB/14-GB public tier.

Evidence schema 6 rejects the old population and single-/two-job topologies.
Fixed job IDs determine suite ownership; no ambient scope can narrow the complete
required population. Every worker discovers and verifies the disjoint native
catalog, then verifies only its assigned actual execution or revalidated original.
Each manifest binds its owning job, successful test/verification/upload steps,
independent checkout, run/attempt, source/dependencies/selection, artifact digest
and lifetime. All originating workers, package steps and the required reporting
gate must succeed. Later incomplete/failed qualification rejects reuse; independent
deployment failure and supported deployment-only retries preserve qualification.
Only successful original executions produce reusable manifests. Explicit
force-full and invalid/missing metadata require fresh execution; trusted PR,
manual, tag and authorized deployment events can reuse matching inputs.
The complete executor/reporting/deployment workflow remains fingerprinted.

Backend fingerprints exclude only the exact audited first-party JS/CSS paths
listed in scripts/ci/test-reuse.json. Browser/frontend still invalidate on those
changes. HTML, vendor manifests, build targets, backend/shared/schema/test/CI
changes and unknown paths remain conservative. Build, publish, asset verification,
package identity/digests and ValidateOnly always use the current commit.

The frontend runner executes files serially, with the two security/workflow
files first. A two-worker trial reduced local wall time but encountered timing-sensitive
failures and a child-process timeout on the 2-vCPU CI runner. The worker option
and audited concurrency list were removed. Each file owns a process, uses safe
argument arrays, and gets a deterministic result/timing receipt.
Cancellation terminates owned process groups, with escalation for uncooperative
children; incomplete/cancelled populations cannot become passing evidence.

Deployment still waits for the required gate and existing explicit authorization.
Build/test/package remains isolated from self-hosted activation. No IIS
activation, production tag, production deployment, runner provisioning
or merge is part of this work. Initial test-IIS activation already succeeded
November 5, 2026 for `v1.0.0-test.32` at `46c60a8cdb1218f34622620cbf0b31cc92e6bd07`;
[that receipt](test-iis-activation.md) does not establish current deployment or health.

## Reproducible measurements

Historical reference: [successful run 41352020578](https://github.com/clcdpc/asap-pocketbase/actions/runs/41352020578),
head 9a1a267, tested merge 6fd821b. Full execution was 71m25s;
non-browser command 3,162.22s (1,530 cases), browser about 9m40s (17 cases),
frontend 67 files, native test concurrency one. Guest sampled CPU averaged
48.73%, peaked at 82.64%; guest used memory averaged 3,326.17 MiB and peaked at
6,482.22 MiB. These describe that run, not a capacity guarantee.

Its TRX attributes 1,234.552 summed seconds to the 98 migration cases and
237.478 seconds to the 14 removed upgrade cases: 1,472.030 seconds of obsolete
case duration. This is not a measured wall-time saving; test setup and fixture
overlap must be accounted for separately.

Controlled local comparison started at aa6031f on Windows, .NET 10.0.303 /
Node 24.19, with the same owned SQL Server 2022 container limited to 2 CPUs /
6 GiB. Comparing exactly the same 332 protocol display names and inputs:
summed duration fell from 180.771s to 7.594s, median 0.5015s to 0.0167s.
The independently executed revised command passed all 332 cases in 22.908s
including provisioning/teardown. The original broad comparison command also
contained other cases, so its 11m51s wall time is not the protocol baseline.

The same three real SQL heartbeat-failure cases fell from 98.287 summed seconds
to 8.634s. A test-owned TimeProvider fires the heartbeat after the SQL trigger is
installed; production retains its 30-second interval. The post-commit SQL timeout
case uses a test-only connection command timeout; production defaults are
unchanged, and its durable commit/status assertions still run against SQL.

A local complete non-browser diagnostic run passed 1,399 exact identities with
no skips in 1,736.566 command seconds. It preceded the three added journal cases,
which separately passed. It is not an exact-final-commit CI receipt and is not
compared to Linux CI as a controlled wall-time improvement.

Its 924 PatronJourneyTests case bodies/framework spans totaled 1,561.468s;
1,330 actual host build/start spans totaled 694.386s (median 0.502s, p95 0.801s).
Factory construction itself totaled 0.011s, TestInitialize 0.544s, cleanup 8.775s,
pickup cleanup 3.473s and shared configuration cleanup 4.002s. These inclusive
spans overlap: do not add them together or to suite wall time. This supports the
protocol extraction and leaves host-heavy workflow fixture isolation as a future
measured opportunity; global cleanup is not the dominant observed cost.

Opt in with ASAP_CI_FIXTURE_TIMINGS. JSONL records monotonic ticks, unique spans,
parent spans, process IDs and hashed test identity without test input data.
Reports retain inclusive per-operation totals/percentiles plus interval unions
for overlap, original TRX, resource samples and SQL telemetry. Disabled timing
does not inspect inputs; telemetry write failures do not replace test failures.

The frontend concurrency experiment ran the identical complete 67-file
population with one and two workers, retaining results, per-file timing and
command wall time: 455.606s versus 259.995s locally, both passing. Subsequent
2-vCPU CI failures made the concurrent default unreliable; this measured trial
is rejected rather than claimed as a retained speedup. Serial execution remains.
Compare cold CI to cold CI and reuse to reuse separately. Record queue/start,
restore/build, SQL startup, test command, browser, frontend, publish/package and
required-gate wall times. Job overlap and runner memory must come from actual
Actions timestamps/telemetry, not subtracting independent stage estimates.
Final cold/reuse, frontend benchmark, acceptance and exact-commit receipts are
maintained in [PR #422](https://github.com/clcdpc/asap-pocketbase/pull/422).

## Adversarial review record

A separate closure review covers functional behavior, selection/provenance,
concurrency/resources, deployment, and maintenance/performance. Findings fixed:

- Preserve the mixed hold-flag test's useful current assertions. Add successful
  journal guards at acquisition, acquired-source validation and dispatch/adoption
  SQL fences; regression-test late evidence before acquisition and dispatch.
- Acceptance consumers still selected removed legacy DTO fields. Convert their
  selectors and evidence maps to native fixture keys/BibIDs/current journals.
- Split-job executor environment leaked into safety-test fixtures. Scope it to
  plan/verify steps and validate the immutable GITHUB_JOB-to-suite assignment.
- Remaining verifier/report loops referenced the other executor's suites.
  Exercise scoped lookup, plan, summary and successful/missing-result gates.
- The removed port specification contained a current bootstrap example still
  consumed by deployment tests. Retain it at scripts/deployment/application.example.json.
- Manual timer delivery initially selected an already cancelled provider timer.
  Skip disposed timers and retain the real SQL failure synchronization/assertions.

- Repeated 2-vCPU frontend execution exposed timing-sensitive failures under
  two workers. Restore serial scheduling and remove the unused concurrency list
  and option; preserve per-file timings, cancellation and exact population checks.
- The staff table transition probe treated a container busy indicator as an empty
  loading body even while retained rows were ready. Target the actual tbody
  loading placeholder and keep busy state as a separate diagnostic.

Final qualification and any subsequent fixes are recorded on the existing PR,
which was merged as PR #422. The subsequent sequential-runner architecture,
cold/reuse measurements and closure review are maintained in
[the current qualification record](../../scripts/ci/test-selection.md#sequential-runner-qualification-record).
