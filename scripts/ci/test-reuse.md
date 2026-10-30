# Content-fingerprinted CI test reuse

`.github/workflows/dotnet.yml` keeps **Build, test and package** as the stable
full-validation gate. Its **Full CI execution** dependency runs on `clc-linux`
and retains the full .NET/SQL partition and minimum-count guard, frontend tests,
all designated browser journeys, performance telemetry, cleanup and test-IIS
activation barriers. Drafts use a separate **Draft validation** job. There are
no workflow path filters or new schedules. Python 3.9+ and the standard library
implement test reuse.

The evidence configuration now names **Full CI execution**, not the reporting
gate. This workflow/configuration change intentionally invalidates prior
fingerprints; the first ready run needs fresh execution. The evidence schema,
dependency exclusions and provenance/digest verification are unchanged.

## Validation policy and required checks

| Event/current state | Validation | Reuse | Automatic deployment |
| --- | --- | --- | --- |
| PR opened, reopened or synchronized, draft | Draft validation | No original full-suite evidence produced | Never |
| PR opened, reopened or synchronized, ready | Full CI execution and full gate | Verified original evidence allowed | Never |
| `ready_for_review`, unchanged SHA allowed | Full CI execution and full gate | Only full original evidence; draft success is ineligible | Never |
| `converted_to_draft`, unchanged SHA allowed | Draft validation | No full-suite evidence | Never |
| Push to `main` or the two retained configured branches | Full CI execution and full gate | Verified original evidence allowed | Never |
| `v*.*.*-test.*` tag push | Fresh full CI execution and full gate | Bypassed | Only with existing activation/environment gates |
| Manual dispatch, including a draft branch | Full CI execution and full gate | Allowed unless forced or deployment requested | Only explicit `deploy_test_iis` plus existing gates |

Draft validation has a 15-minute job budget. It checks the exact event checkout,
validates all workflow YAML/expressions with checksum-pinned actionlint 1.7.12,
runs the SQL-independent reuse regressions, restores/builds the Release solution,
runs the `Asap.Tests.Unit`, `PolicyContractTests` and `AnalyticsContractTests`
selection (107 currently discovered cases, minimum guard 100), and runs the
complete retained `npm test` suite. It starts no SQL container, installs no
Chromium/native browser dependencies, runs no browser suite and publishes or
uploads no deployment package. Runner queuing is outside that execution budget.

Ready CI does not rerun a separate draft job. Its frontend checks run after
reuse lookup and before SQL startup, so a frontend failure can stop an expensive
cold run early. SQL/browser setup is conditional only on the existing verified
reuse decisions. Every full run still builds and publishes the current SHA.

The reporting-only **Build, test and package** job runs with `always()` after
both execution jobs. It succeeds only for a non-draft/full event with a successful
**Full CI execution** result. Failure, cancellation, a skipped full job or an
absent result cannot pass. On a draft it **intentionally fails**, even when
**Draft validation** passes: the red full gate means full validation has not
been established. The gate has no GitHub API permissions and runs on a GitHub-hosted
runner without checking out or executing PR code. Deployment depends on both the
full execution and this gate.

This is necessary because GitHub accepts conditional job skips as successful
required checks. See [required-check troubleshooting](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks).
Require **Build, test and package**, from the **GitHub Actions** app, on `main`;
do not substitute **Draft validation** or the conditional **Full CI execution**
job. Keep any additional existing required checks. Enable **Require branches to
be up to date before merging** when requiring validation of the current merge
content. A ready transition creates a new run/check even without a new SHA;
a successful dispatch is an operator check, not a replacement for PR checks.

Read-only inspection on October 30, 2026 returned `protected: false` for `main`,
no branch rules and no required checks on PR #414. The detailed protection,
runner-administration and contributor-approval endpoints returned HTTP 403.
**The workflow cannot enforce merge protection on an unprotected branch.** An
administrator must configure the required check above; this task does not
change repository settings or claim that protection is already enabled.

## PR transitions and concurrency

The explicit PR activity types are `opened`, `reopened`, `synchronize`,
`ready_for_review` and `converted_to_draft`. Validation depth uses the event's
current draft flag, not whether the SHA previously received a green check.
Marking ready therefore starts full CI immediately; converting back to draft
starts fast checks and cancels queued/running obsolete PR work.

Workflow concurrency is `<workflow>-pr-<number>` for all revisions and states
of one PR, with `cancel-in-progress` enabled only for PR events. Different PRs
have independent groups. Push, tag and manual groups include the unique run ID
and attempt, so even runs at an identical SHA cannot cancel one another or a
release deployment. The previous SHA-based group failed to cancel successive
PR commits and could cross-cancel manual/release validation of the same SHA.
There is no global PR queue. GitHub does not guarantee concurrency ordering;
rerun the latest revision, not a superseded historical run. See
[GitHub concurrency behavior](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

Use **Actions -> .NET baseline -> Run workflow**, select the draft branch and
leave `deploy_test_iis` false to request full validation while keeping the PR
draft. Select `force_full_ci: true` to execute every suite afresh. Otherwise
trusted equivalent evidence can be reused. Merges into `main` still validate
the integrated tree and produce current-commit packages; release tags and
deployment requests always execute fresh tests before any eligible activation.

## Public PR runner boundary

Both code-executing jobs require a same-repository PR authored by an `OWNER`,
`MEMBER` or `COLLABORATOR` before allocating `clc-linux`. Forks and untrusted
authors receive no trusted runner; the full gate fails rather than treating
skipped execution as validation. This is an explicit restriction compared with
the earlier workflow's reliance on repository-level approval and isolation.
External-contributor approval settings remain unchanged; their exact current
configuration could not be read. Approval alone does not make fork code eligible
for these trusted runners. Maintainers must review external changes before
importing them to an authorized repository branch for validation.

No `pull_request_target`, privileged token, new secret or write permission is
introduced. Draft execution has only `contents: read`; full execution retains
the read scopes needed for evidence provenance. Ephemeral runner isolation and
review of workflow/harness changes remain necessary: a writer who can replace
the workflow itself can replace its controls. More generally, `pull_request`
loads PR-authored workflow code: a fork can propose removing these predicates.
The predicates are not an independent sandbox or a replacement for approvals
of untrusted workflow changes, runner-group restrictions and disposable host
isolation. See [GitHub's self-hosted runner guidance](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/manage-access).
The pre-existing unprotected integration branch and unverifiable approval settings are operational
limitations, not evidence that approvals or host isolation are configured.
The separate WSLC acceptance runner and its explicit mutation authorization are
unchanged; it does not validate Actions scheduling or concurrency.

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

Resolve Chromium's native OS libraries before build/lookup, then include a digest
of the actual installed dpkg package versions in every suite's environment.
`install --with-deps` can otherwise silently upgrade native libraries *after*
fingerprinting. Native package resolution remains necessary on a HIT; it installs
no browser binary. Browser download and startup remain conditional on a MISS.

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

The build job uses only `contents: read`, `actions: read` and `pull-requests: read`
on its existing GitHub token. The PR read scope is restricted to that job and is
required by GitHub's [commit-to-PR endpoint](https://docs.github.com/en/rest/commits/commits#list-pull-requests-associated-with-a-commit)
for historical author/repository provenance.
No PAT, new secret, write permission or `pull_request_target` is introduced. The
lookup starts after runner allocation, restore/build and npm install, so queued
jobs can see a predecessor that completed while they waited. Search is bounded
to three pages of ten successful workflow runs with bounded job/artifact pages,
response sizes, timeouts and a lookup time budget. Older/out-of-bound evidence
is simply a MISS. Independent jobs that both start before either succeeds may
both execute; there is no distributed evidence lock. Superseded PR runs use
the cancellation policy above.

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

`node tests/ci_workflow_contract.test.js` parses YAML with unique-key checking,
evaluates the workflow's actual routing/concurrency expressions with GitHub's
`@actions/expressions` library, and executes the actual reporting-gate shell
against successful, failed, cancelled, skipped and missing dependency results.
Its matrix covers state transitions at an unchanged SHA, per-PR isolation,
independent manual/push/tag attempts, fork/author exclusion, draft setup limits
and preserved deployment dependencies. It runs in the complete `npm test` suite.
These are executable policy and gate contracts, not a simulation of GitHub's
event delivery or cancellation engine. Those require separate live run receipts.

Live trigger receipts on October 30, 2026, in [PR #418](https://github.com/clcdpc/asap-pocketbase/pull/418):

- [Opened draft](https://github.com/clcdpc/asap-pocketbase/actions/runs/41075824409)
  was automatically cancelled after a new commit, including its running clc-linux job.
- [Draft synchronize](https://github.com/clcdpc/asap-pocketbase/actions/runs/41075985310)
  passed fast validation in 7m 28s: 107 .NET cases, 54 reuse checks and all 64
  frontend files. Full CI and IIS had no runner allocation, no artifacts were
  uploaded, and the separate full gate intentionally failed.
- [Ready transition](https://github.com/clcdpc/asap-pocketbase/actions/runs/41077529037)
  selected full CI without changing head `25975c1be940bc29e2b7e80f10be6e8df399b341`.
  [Conversion back to draft](https://github.com/clcdpc/asap-pocketbase/actions/runs/41078346929)
  at that same head selected fast validation and cancelled the running full job
  before SQL startup. No original evidence was uploaded and the full gate failed.

The PR records final full-suite/reuse results separately. No release tag,
deployment or second PR is needed to test these state transitions. Cross-PR
isolation and release/forced-deployment policy have automated contract coverage;
live deployment and a second independent PR were deliberately not exercised.

`bash scripts/ci/check-workflows.sh` validates all workflows on Linux x64 with
checksum-pinned actionlint. `.github/actionlint.yml` declares the existing custom
runner labels; it does not grant access to runners. ShellCheck and pyflakes
integrations are explicitly disabled because those tools are not consistently
installed on the controlled images. YAML, expression, context and dependency
validation remain enabled.

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
ephemeral clc-linux images, package integrity, contributor approval and
maintainer review of workflow/harness changes. Repository code cannot prevent a
write-authorized maintainer from replacing the workflow/gate itself; enforce that
boundary through repository review/rules and runner isolation. The PR runner
guard above does not grant permissions or replace those controls. Undeclared native/environment
drift requires an epoch bump. Conservative misses are preferred to inferred hits.

## Adversarial closure review

| Attempt / failure | Enforced boundary |
| --- | --- |
| Draft checks substituted for full CI | Distinct jobs, explicit original-executor identity and an always-run required gate that fails for drafts/skipped/failed/cancelled full execution. |
| Ready/draft transition without a commit | Explicit activity types and current draft flag; every transition creates the appropriate run, without treating draft success as full evidence. |
| New PR revision leaves old SQL job running | Workflow-wide stable PR-number group and PR-only cancellation; unrelated PR, push, manual and tag groups stay independent. |
| Fork reaches trusted runner after skipped security job | Trust predicate is on both execution jobs before allocation; hosted required gate rejects skipped full execution. |
| Passed tests, later failed/cancelled workflow | Only a completed successful latest workflow and exact successful attempt can supply evidence. |
| Skipped test presented as PASS | Original successful test step, complete native outcomes and current always-run gate are all required. |
| Contributor/fork forged manifest | Numeric repository/head IDs, trusted same-repository PR association, original checkout logs, Git tree and immutable artifact digest must agree. |
| Omitted dependency | Tracked-file inclusion by default; only the audited exact-path exclusions above. Shared review providers and SQL fixtures stay included. |
| Same PR head, changed integration base | Fingerprint actual checked-out merge tree, independently recover its checkout and verify its head parent. |
| API/ZIP outage suppressing tests | Lookup exceptions restore normal execution, including after a partial HIT; late evidence loss fails the final gate. |
| Different filter/count | Match complete selection, retained minimum, fresh discovery and exact executed/passed count. |
| Reuse chain | Only executed manifests with an actual original successful test step are eligible; reused suites emit decisions only. |
| Old deployment payload | Current SHA build/publishes/manifest/ZIP/digest/ValidateOnly remain unconditional on reuse. |
| Concurrent equivalent runs | Lookup after runner allocation; stable per-PR cancellation. Independent cold jobs may both execute, but cannot accept in-progress evidence. |
| Workflow edit skipping tests | Workflow/harness changes invalidate fingerprints; skipped tests cannot pass the unchanged gate. Deliberate replacement of the gate itself remains subject to existing repository review/runner controls. |
| Environment drift | Actual tool/package versions, immutable SQL digest, browser revisions and controlled image epoch; unidentified supported override paths disable affected reuse. |

Live metadata review found and corrected a conservative false miss after a trusted
PR advanced: GitHub's commit-specific PR association verifies the historical head,
so the PR's *current* head need not still equal the executed head. Original merge
parent, checkout, repository and author checks remain required. Another regression
proves that an API failure after one accepted candidate restores execution for all
suites. The retained Linux SQL bootstrap regression was updated for conditional
steps/image variables while preserving its password-argument assertions.

Validation before hosted cold execution:

- Release build: zero warnings/errors; two actual MTP/native-report cases passed.
- All 63 retained frontend files passed, including deployment/preflight contracts.
- All 52 reuse checks passed on Windows and Linux. The complete 104 Python checks
  passed on Windows with 11 existing Linux-only skips. The earlier full local Linux
  container run passed 102/103; the unchanged optional-command descendant timing
  fixture had no PID file after its 0.4-second timeout. Its source is unchanged from main.
- Read-only real API validation recovered checkout
  `92ef3e3b20b8e163eebd8f57874e1cc3612d64f4` and tree
  `753bdbaad26a93fccad091e5222719e1c66c788a` from
  [successful run 40988487235](https://github.com/clcdpc/asap-pocketbase/actions/runs/40988487235).
  Artifact `12950811465` passed its API SHA-256 check; its native TRX contains
  exactly 1,444 unique passing outcomes. This establishes parser/provenance
  compatibility, not a cache HIT for the new schema.

The first hosted attempt, [41008743691](https://github.com/clcdpc/asap-pocketbase/actions/runs/41008743691),
passed all 1,530 non-browser tests and 63 frontend files, then hit the inherited
90-minute timeout during browser execution. Its failed verification gate emitted
no original evidence, and the cancelled workflow is ineligible for reuse. The
budget is now 120 minutes based on its measured SQL (76m 52s) and frontend (5m 25s)
times, with all test guards retained. Its logs also exposed MTP's ANSI-colored
discovery output under `GITHUB_ACTIONS=true`; discovery now strips only those
formatting codes before enforcing the same exact count check, covered by a new
regression and real SDK discovery with the Actions environment enabled.

The new-schema cold/repeated workflow and live HIT/invalidation/fallback receipts
are recorded below and in [PR #414](https://github.com/clcdpc/asap-pocketbase/pull/414)
after those runs complete.
