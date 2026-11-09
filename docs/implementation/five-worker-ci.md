# Five-worker full CI qualification

The five independent GitHub-hosted Ubuntu 24.04 workers are frontend/CI
safety/build/package, non-browser .NET/SQL A and B, and browser/SQL A and B.
All depend only on validation-policy and qualify the exact event SHA with
independent Release builds. The required `Build, test and package` reporter
requires all five successes, five explicit suite-verification outputs, and
validated/uploaded packaging. Draft policy, trusted/current PR checks,
cancellation, and test-IIS authorization remain in place. PRs cannot deploy.

Each native worker retains its own digest-resolved SQL Server 2022 container,
ephemeral masked password, random container name, dynamic loopback port,
independent fixture databases and always-run cleanup. Browser workers each
install Chromium for fresh execution. No shared build, database or browser
state is introduced.

## Partition choice

Authoritative MTP discovery and the reviewed category contract first establish
the complete disjoint non-browser/browser populations. Within each category,
`SHA256("asap-method-v1-116\n" + fullyQualifiedMethod)[0] % 2` assigns A/B.
This frozen seed was selected using summed method durations from PR #504 and
the first full partition execution, rather than counts. No historical artifacts or
timings are runtime inputs. New methods and parameter rows are discovered and
assigned automatically; all rows of a method stay together. The large partial
PatronJourneyTests class is split by method, with fixture initialization repeated
inside each independent process/database. Internal browser scenario order is
unchanged. Two existing state dependencies must remain together. First, the dispatch-failure
weekly summary method hashes using the preceding NormalWeeklySummary method
as its anchor. The ordinary summary establishes global-recipient dedupe that
the following method consumes. Both retain native ordering in one process; no
test behavior/assertions are changed. Second, the malformed patron-hold BIB
lookup uses the preceding holdings-failure BIB lookup as its anchor. The first
lookup establishes the authenticated Polaris session; the second expects it
to be cached when asserting transport request counts. Missing or cross-category anchors fail
discovery, and the anchor definitions are fingerprinted.

| Population | A cases | B cases | Baseline A test time | Baseline B test time |
| --- | ---: | ---: | ---: | ---: |
| Non-browser | 792 | 845 | 678.61 s | 675.82 s |
| Browser | 16 | 43 | 679.32 s | 655.80 s |

These counts describe the discovered baseline, not expected-count constants.
The estimates omit repeated setup, runner variation and queuing; they do not
prove a 15–18 minute completion time. The first partition execution exposed
the weekly fixture dependency and uneven timing; one focused rebalance also
uses that run's TRX durations, projecting 498.87/484.39 seconds non-browser and
584.93/618.63 seconds browser. Both evidence sets stay balanced. Final hosted
measurements are required to confirm the revised split.

Execution selects exact native UIDs with MTP `--filter-uid`. The .NET selector
uses a token-per-line SDK response file to avoid command length limits.
Verification rediscovers the complete category population and checks exact
TRX identities, execution identities, counters and passing outcomes. Missing,
overlapping, changed, interrupted, skipped or substituted populations fail.

## Reuse and diagnostics

Evidence schema 7 has fixed `dotnet_a`, `dotnet_b`, `browser_a`, `browser_b` and
`frontend` suite/executor identities. Native selection schema 3 binds each
partition's sorted UIDs, hashed identities, population digest, algorithm, seed,
anchor-definition digest and bucket. Old whole-suite evidence and opposite-partition evidence cannot
qualify a partition. Source inputs and the full workflow execution contract
continue to invalidate incompatible originals. Every reused partition is
independently revalidated at its worker's gate; all 32 fresh/reused combinations
are covered by focused contracts. Originating qualification still requires all
workers, package steps and the reporting gate.

TRX, CPU/memory/disk samples, SQL activity/waits/I/O and fixture timelines use
`ci-performance-a/b` and `browser-performance-a/b` directories/artifacts;
frontend retains its existing artifact. Verification artifacts include job,
run and attempt. Reuse reports link the original execution and saved step time.
Job wall times are obtained from Actions job timestamps, including setup and
cleanup rather than only command sampling.

## Validation and measurements

Baseline: [PR #504 full CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/42678052437).
Its qualification workers took 8m14s frontend/package, 25m11s non-browser and
25m14s browser (58.65 cumulative qualification runner-minutes). Trigger to the
required gate was 25m57s. These Actions timestamps include setup/cleanup.

Local checks cover Release build, workflow lint, workflow/deployment contracts,
selection/reuse/performance/SQL diagnostic tests, complete UID discovery and
small real SDK executions with TRX verification from all four partitions.
The SQL-independent draft path retains its discovered 130-test execution.
POSIX lifecycle cases require Ubuntu. Independent Astra review found no
actionable issues; targeted Sol review identified and corrected a Linux-only
SQL bootstrap test that referenced the retired worker IDs.

The associated pull request records the hosted full qualification run and its
measured worker durations, trigger-to-required-gate wall time, approximate
cumulative runner-minutes and any remaining bottleneck. Compare its fresh
execution with the fresh baseline; reused execution measures saved work rather
than native test throughput. No test-IIS deployment is authorized by this PR.
