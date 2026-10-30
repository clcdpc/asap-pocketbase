# Content-fingerprinted CI test reuse

`.github/workflows/dotnet.yml` retains the required **Build, test and package**
job on `clc-linux`, its full .NET/SQL partition and minimum-count guard, frontend
tests, all designated browser journeys, performance telemetry, cleanup and
test-IIS activation barriers. There are no workflow path filters or new schedules.
Python 3.9+ and the standard library implement the optimization.

## Identity and dependencies

`reuse.py checkout` first requires `git rev-parse HEAD` to equal `GITHUB_SHA`
and rejects tracked working-tree changes. It records `HEAD^{tree}`. A PR tests
its actual synthetic merge, including the integration base, rather than its head
branch alone. Commit SHA is provenance, never the test fingerprint.

`fingerprints.py` expands that exact tree with `git ls-tree -r -z HEAD` and hashes
sorted path/mode/type/blob identities per suite. Every tracked file is included
unless an **exact path** has a documented exclusion in `test-reuse.json`. New and
unclassified files, documentation consumed by deployment-contract tests, all
production code, schema, fixtures, workflow/harness code, manifests and locks
therefore invalidate the affected suite by default.

The deliberately small reviewed exclusions are:

| Paths | Excluded from | Reason |
| --- | --- | --- |
| `tests/acceptance/run.cjs`, `tests/acceptance/helpers/reporting.cjs`, `tests/acceptance/helpers/accessibility.cjs` | All three suites | Standalone acceptance launch/report/screenshot implementation; required suites do not load it. |
| `tests/browser/staff-presentation.cjs` | .NET/SQL and frontend | Invoked only by the explicitly designated browser-partition method; browser fingerprint includes it. |
| `scripts/ci/test-reuse.md` | All three suites | Operator prose/validation receipts; no execution contract reads it. |

Do not extend these to whole directories. The test project actually references
`dev/review/src/Asap.ReviewProviders` and embeds
`dev/review/src/Asap.ReviewInit/LocalReviewer.sql`; both remain inputs. Shared
browser helpers, other acceptance/review files and all C# test code also remain
included. Re-audit exclusions when adding consumers or changing test selection.
A suite can conservatively invalidate for more changes than strictly necessary.

Each fingerprint hashes schema version, suite, included source identity,
environment identity and selection. The selection reads the existing workflow
filter/minimum and browser-runner filter/minimum directly. Fresh `--list-tests`
discovery must meet those minimums; its actual count is the expected evidence
count. Frontend selection is the sorted complete set of root `*.test.js` files.
There is no second hard-coded .NET test-count source; #411's count and filter
changes invalidate evidence automatically. This branch adds no backend work.

## Environment versioning

`test-reuse.json` owns schema, the explicit `environment_epoch`, repository/job/
runner identities and a maximum 14-day lifetime. **Bump the epoch whenever the
clc-linux VM image, native libraries, runner tooling/configuration or runtime
policy changes.** Bump schema for incompatible evidence/verification changes.
These values are tracked inputs and invalidate previous evidence.

Observe actual OS/distribution/kernel/architecture, Python, .NET SDK and installed
runtimes, Node/npm, PowerShell, locale and resolved NuGet package content hashes,
including MSTest/SQL build SDK packages. Major/roll-forward setup declarations
alone are insufficient. No ephemeral runner machine name enters the identity.
The npm lockfile and all dependency declarations remain source inputs.

For SQL-dependent suites, resolve the SQL Server 2022 registry manifest, verify
its SHA-256 and select the Linux x64 digest when it is an image index. Start
SQL with that exact `mcr.microsoft.com/mssql/server@sha256:...` reference. Include
the Docker server version. Registry/tooling uncertainty disables SQL-dependent
reuse; normal tests may use the existing tag fallback. Never reuse on tag alone.

Browser identity additionally includes the installed Playwright version and its
locked Chromium/headless-shell revisions and browser versions. Browser installation
happens only on a MISS. Custom Chromium executables/browser paths disable browser
reuse. Ambient Node options and .NET extension hooks disable affected reuse.
Override paths/values, SQL credentials and arbitrary environment variables are
never written to evidence.

## Original execution and trust

The job uses only `contents: read` and `actions: read` on its existing GitHub token.
No PAT, new secret, write permission or `pull_request_target` is introduced. The
lookup starts after runner allocation, restore/build and npm install, so queued
jobs can see a predecessor that completed while they waited. Search is bounded
to three pages of ten successful workflow runs with bounded job/artifact pages,
response sizes, timeouts and a lookup time budget. Older/out-of-bound evidence
is simply a MISS. Concurrent jobs that both start before either succeeds may
both execute; there is no distributed lock or new cancellation policy.

Acceptance requires all of these checks, against GitHub's API rather than JSON
claims alone:

1. Expected repository numeric ID/name, workflow numeric ID/path, completed
   successful latest run and exact successful originating attempt. A run with
   passed tests followed by failed packaging/cleanup/deployment is ineligible.
2. Expected successful job name, runner group and labels; the machine name is
   deliberately ignored. Original suite, verification gate and evidence upload
   steps must each have completed successfully. Skipped steps cannot pass.
3. Independently read actions/checkout's `git log -1 --format=%H` output in GitHub
   job logs. Verify that commit/tree through the Git API. Push/manual checkout
   must equal the run head; a PR merge must have its tested head as second parent.
   PR evidence additionally requires a same-repository PR to main authored by an
   OWNER, MEMBER or COLLABORATOR. External forks and untrusted authors are rejected.
4. Recompute per-suite included source identity from the originating Git tree;
   reject truncated trees. Match selection, discovered/executed counts, environment,
   schema and fingerprints. A manifest cannot substitute a different source tree.
5. Exact attempt-specific artifact name and run/repository/head metadata, nonexpired
   immutable artifact, bounded ZIP and required API SHA-256 digest verified against
   downloaded bytes. Signed storage redirects drop the GitHub Authorization header.
   Read exactly two JSON files in memory; never extract or execute archive contents.
6. `kind: executed`, passed outcome and complete unique passing test identities
   with zero failures/skips. The manifest's result digest/counters must agree with
   the sanitized native outcomes. Native TRX outputs and the full frontend file
   report are verified at original execution before recording these counts.
7. Execution time within the originating job and the configured lifetime, with
   a matching expiration policy. Missing logs/artifacts/metadata are misses.

Artifacts are named `asap-test-evidence-v1-<suite>-<run-id>-<attempt>` and retained
14 days. They contain a manifest and sanitized per-test outcome identities/counts;
no TRX console output, machine name, connection strings or credentials. Original
TRX is associated with that run's existing performance/native test reporting.
The separate `asap-test-verification-<run-id>-<attempt>` artifact records current
fingerprints and execution/reuse decisions. **A reused suite emits no new original
evidence.** Lookup resolves directly to an executed source, never follows reuse chains.

The always-run required gate checks all three suites against Actions step outcomes
and native/current result counts. Reused suites must be skipped and pass a fresh
API/artifact revalidation at the gate. A skipped test with no evidence fails the
check. API/identity failures during lookup schedule normal tests. If already-used
evidence disappears between lookup and the gate, the gate fails safely; retry
with full CI. Workflow success is still required before uploaded evidence is reusable.

## Operating CI

Manual **force_full_ci: true** bypasses lookup and executes all suites, recording
new evidence. Tags, release events and requested test-IIS deployment also require
fresh verification. The existing activation variable, environment approval and
deployment-job isolation remain unchanged. An ordinary dispatch defaults to no
deployment and allows reuse. No scheduled expensive refresh is needed: the next
eligible job after expiration executes naturally.

A typical job summary is:

| Suite | Current status | Original source |
| --- | --- | --- |
| .NET/SQL | REUSED | Linked original successful run |
| Browser | EXECUTED | Current run |
| Frontend | REUSED | Linked original successful run |

Each row also gets the full fingerprint, HIT/MISS reason, original artifact
ID/digest, evidence age and estimated minutes saved from the original Actions
test-step wall time. This is avoided suite execution time, not a fabricated
end-to-end speedup. Performance telemetry only measures actual fresh SQL execution.

For a false MISS, inspect `plan.json` and `current.json` in the verification artifact,
compare included source/selection/environment fingerprints with the original
manifest, then check run/attempt completion, trusted PR association, runner identity,
artifact/log availability, digest and expiration. Registry/SDK/runtime drift,
unavailable synthetic merge objects, a changed workflow, an
epoch bump or the bounded history window can all produce intentional false misses.

Web/DACPAC and self-contained win-x64 migration publishes **always** run for the
current commit. So do frontend asset checks, exact deployment manifest/ZIP creation,
ZIP SHA-256 verification, `-ValidateOnly` and artifact upload. Reused tests never
mean reusing another commit's deployment artifact. If both SQL-dependent suites
hit, credential generation, SQL start/readiness/database initialization are skipped.
The existing always-run container cleanup remains.

## Validation and limitations

Run `python3 -B -m unittest discover -s scripts/ci -p 'test_reuse*.py' -v` for
temporary-Git, mocked Actions, hostile archive, native-report and workflow guards.
The existing full Python telemetry checks and retained frontend suite also remain.

After a successful cold run, download its verification artifact and use
`probe_reuse.py --run <id> --current <current.json> --output <report.json>` with
an existing read-only `GH_TOKEN`. This read-only integration harness uses real
run/job/log/tree/artifact metadata, emulates a changed browser count (only browser
must MISS), and corrupts downloaded ZIP bytes (all must MISS). It does not execute
tests for simulated inputs or claim those inputs passed. A second real workflow
run must separately demonstrate actual reuse, skipped SQL/browser setup and
current-commit packaging.

Trust assumptions remain GitHub's API/immutable artifact integrity, controlled
ephemeral clc-linux images, package integrity, existing contributor approval and
maintainer review of workflow/harness changes. Repository code cannot prevent a
write-authorized maintainer from replacing the workflow/gate itself; enforce that
boundary through repository review/rules and runner isolation. This optimization
does not grant permissions or change those controls. Undeclared native/environment
drift requires an epoch bump. Conservative misses are preferred to inferred hits.

Live validation receipts are recorded here after completion and in the PR.
