# Current application qualification

PocketBase was an internal proof of concept with no production deployment or
historical data. Its migration executable, frozen source contracts, mapping
table and pre-production schema upgrades are retired. Git history is the archive.

## Mandatory populations

The built MTP application discovers the complete population, non-browser and
Browser partitions. Their disjoint union must exactly equal the complete
catalog. The reviewed browser methods are in reviewed-test-categories.json.
New unclassified tests default to non-browser. Exact native UIDs, execution
identities, counts, outcomes and counters must match; omissions, duplicates,
skips and equal-count substitutions fail qualification.

The Browser partition includes Chromium journeys and frontend asset,
publication and cache contracts. Every Chromium consumer belongs there.
PolarisProtocolTests retains real SQL configuration/protected-credential reads
while constructing providers directly, without a web host or Hangfire.
Shared-database fixtures remain serial. Every root frontend test file remains
mandatory; counts are derived from current discovery.

## Execution and required gate

Hosted validation-policy resolves current trusted PR state before runner
allocation. Draft PRs run bounded configuration/unit contracts and the complete
frontend suite. Ready PRs, main pushes and manual qualification use one Full CI
execution job on one ephemeral clc-linux runner, retaining 2 vCPUs, existing VM
memory and the 120-minute timeout. Hosted policy/reporting jobs use no clc-linux
slot. No infrastructure, runner labels, acquisition or scaling settings change.

The executor checks out the exact event SHA, sets up tools, builds Release once,
checks current safety contracts and discovers the complete population. It plans
reuse independently for all three suites, then runs fresh frontend, non-browser
.NET/SQL and browser tests in that order, with no overlapping test commands.
When either native suite needs execution, the job creates one ephemeral SQL
password, starts one digest-resolved SQL Server 2022 container and waits once.
The container name is unique to the run/attempt and binds a dynamic loopback
port. Test databases remain independently owned; their fixture lifecycle is
unchanged. An always-run cleanup removes only the owned container. Existing
process-group cancellation stops testhost/Chromium descendants before teardown.

One mandatory verifier checks every suite and publishes only original successful
evidence. The reporting-only Build, test and package gate requires the execution
job to succeed. Missing, failed, cancelled or skipped full execution fails the
gate. Draft CI result remains distinct. Publication reuses the current build
with --no-build/--no-restore; asset validation, DACPAC/ZIP identity and digest
checks and ValidateOnly remain mandatory even on complete evidence reuse.
Test-IIS activation still requires existing explicit authorization and successful
final qualification. There is no aggregation test job or additional runner.

Browser and frontend source changes invalidate their evidence. The directory/file
input policy in test-reuse.json assigns authored frontend JS/CSS and Node/browser
runtime to their owning suites, preserving backend evidence when its inputs match.
HTML entry points, vendor assets, manifests, new paths, backend/schema/test
changes and execution/security/build/deployment tooling remain conservative.
Current build/publication checks and frontend-serving/browser contracts still
validate changed asset bytes.

## Events, provenance and evidence

PR selection verifies the trusted current head, actual synthetic-merge parents,
the first parent's membership in fetched main history, a unique
merge base and both sides of renames. Every valid changed path selects the full
required population, including documentation, build/CI tooling and unknown paths.
The cumulative PR diff does not demand fresh execution: reuse independently
compares the effective tested inputs, including CI/test tooling by default.
Advertised base SHAs may lag or advance beyond the runner's fetched objects
without changing an unchanged tested merge. They do not authorize its actual
parents. Ambiguous or invalid source/trust metadata still forces fresh execution. Only explicit
force-full bypasses otherwise valid matching evidence. Triggering and originating
events do not affect eligibility: PR, manual, branch, tag, release and deployment
runs share the same fingerprint/evidence contract. Tag refs (including annotated
tags) must peel to the exact tested commit; release and deployment metadata must
agree with it. Verified empty diffs still require the complete population.

Evidence schema 5 rejects prior populations and the two-job contract. It binds suite, exact
identities, complete classification, source/resolved dependencies, originating
run/attempt/job, checkout logs, Git blob/tree identity, artifact digest, age and
successful original steps. Lookup queries artifacts by their exact suite
fingerprint, remains bounded/read-only, and is unaffected by unrelated run
volume. Deployment-only retries preserve the independently successful original
qualification, including active downstream deployment; later active/failed
qualification and empty/ambiguous retry state still reject it. The final suite
verifier re-fetches original evidence; a cached plan cannot authorize success.
The full workflow, including browser execution and the required gate, is part
of the execution contract. Reused evidence never becomes new original evidence.

Throwaway VM differences alone do not invalidate matching evidence. Observed OS,
kernel, locale, installed tool/native-package versions, Docker and SQL image
digests remain recorded provenance. The environment fingerprint binds repository
policy (the configured epoch), resolved NuGet package contents and Playwright
browser definitions. Repository tool/lock files, all other included source and
the exact discovered population remain fingerprinted. Runner trust checks,
unavailable identity and untracked runtime-extension guards still fail closed.
Fresh SQL execution still uses the currently resolved image digest.

## Diagnostics and performance

ASAP_CI_FIXTURE_TIMINGS enables monotonic JSONL spans. Observations contain
operation identifiers, hashed test identity, process/span/parent identity,
timestamps and elapsed duration. Host build/start includes DI startup.
body_and_framework excludes TestInitialize and TestCleanup but includes any
nested host/provider work. Inclusive spans can overlap and must not be added
to wall time. Failed telemetry writes preserve test exceptions.

The normal execution uploads native TRX, resource/SQL telemetry, fixture spans,
discovered catalogs, per-file frontend receipts and verification decisions.
Non-browser TRX, fixture spans and telemetry stay in .artifacts/ci-performance;
browser writes its own test-results.trx, spans and telemetry directly to
.artifacts/browser-performance. Frontend telemetry uses
.artifacts/frontend-performance and receipts use .artifacts/test-reuse.
Discovery catalogs use .artifacts/test-selection; the single plan, current
fingerprints and complete verification manifest use .artifacts/test-reuse.
No TRX copying or combined fixture timeline is needed. The existing
ci-performance, browser-performance and frontend-performance artifact names
retain seven-day retention; verification/original evidence retains 14 days.
Failure uploads preserve partial diagnostics. Reused suites produce no new
original evidence or empty diagnostic report.
Compare equivalent cold populations separately from evidence reuse, and report
runner queueing/startup separately from test-command wall time. See
[the implementation and validation record](../../docs/implementation/ci-simplification.md).

## Sequential-runner qualification record

The intended trade-off is runner availability: one occupied clc-linux slot
instead of two, with the same 2-vCPU VM resources. PR #422's parallel reference
was 35m39s for Full CI execution and 37m08s for the required gate; test commands
were 26m28s non-browser, 13m23s browser and 4m57s frontend. Those stages used two
independent runners. They are not a controlled comparison with a sequential
runner. Roughly 49–51 minutes was the pre-run estimate, subject to actual data.

Local validation on November 2, 2026: Release build passed with zero warnings or
errors; actionlint 1.7.12 passed; native discovery verified 1,402 non-browser and
43 browser cases with an exact disjoint union. Python discovered 147 checks:
135 passed on Windows and 12 Linux-only checks were skipped locally. Linux CI
must execute those process/signal/bootstrap checks. Publication using the shared
build verified all 74 frontend assets; the extracted workflow ZIP/ValidateOnly
steps passed without host mutation. These local package checks preceded the
implementation commit and do not replace exact-commit CI qualification.
The complete retained frontend suite passed all 67 files with zero failures or
skips using one worker (579.127 summed command seconds locally on Windows;
this is not a controlled comparison with Linux CI).
All 47 Linux performance/SQL collector checks passed with no skips, including
signal escalation, descendant cleanup, secret-safe bootstrap and one-container
teardown. The separate Linux pass found and fixed a shell-contract extractor
that consumed the next job after the final cleanup step. Running directly from
the Windows bind mount also exceeded pre-existing subsecond timing assumptions;
the unchanged checks passed from the container's native filesystem in 11.902s.

A separate closure review examined the completed implementation:

- A, workflow correctness: full/draft routing selects one clc-linux job; checked
  every job dependency, required-gate outcome and default step-success condition.
  Test steps are blocking and have no continue-on-error. An earlier failure
  cannot reach publication; verification and owned-container cleanup always run.
- B, evidence integrity: rejected the old schema/job, swapped suite archives,
  missing/failed/cancelled/skipped original steps, changed native identities at
  equal counts, incomplete populations and lost/corrupt evidence. All eight
  fresh/reused combinations are covered. The full verifier ignores obsolete
  ambient split scopes and revalidates every skipped suite against its origin.
- C, runtime isolation: retained GUID-owned databases and existing fixture
  teardown. Reviewed process-group signal/escalation behavior and one SQL
  lifecycle. Removed browser TRX copying; separated TRX, fixture spans and
  resource directories. Telemetry requires the explicit owned container and
  never probes a fixed fallback. Reused suites produce no diagnostic summaries.
- D, deployment safety: trust, permissions, cancellation, exact SHA/ZIP digest,
  DACPAC/asset checks and ValidateOnly are retained. Contract scenarios cover
  manual/tag authorization and failed/cancelled/skipped prerequisites. No IIS
  activation or release tag is part of qualification.
- E, simplicity: removed the browser job, duplicate setup/build/discovery/SQL,
  split executor configuration and unused SQL-image job output. Review found
  that publication still invoked a build; --no-build/--no-restore now reuses the
  validated Release output, and local publication/package validation passed.

Exact-commit cold and evidence-reuse Actions receipts, per-stage timings,
observed CPU/memory, runner-slot accounting and final closure findings are
maintained in [PR #427](https://github.com/clcdpc/asap-pocketbase/pull/427).
That measured record distinguishes runner allocation from execution and the
parallel reference from the sequential run. The PR remains open for review and
must not be merged by this task.
