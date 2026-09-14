# Slice 5 Interrupted WIP Handoff

## Checkpoint

Recorded 2026-09-14 after the user stopped implementation for time/usage reasons.
**Slice 5 is IN PROGRESS, not review-ready and not an accepted milestone.**
Terra review has not started. Any automatically triggered WIP CI is diagnostic
only. Slice 6 has not started. PR #264 remains draft.

- Repository/branch: `clcdpc/asap-pocketbase`, `codex/csharp-port`.
- Accepted Slice 4: `417c72430652a35bc8fc1da549ae270eabc86429`.
- Pre-Slice-5 documentation HEAD and this checkpoint's parent:
  `ae706931da09d2781add4b96bbca9cfc4f768011`.
- The commit containing this handoff is the interrupted WIP checkpoint; its
  exact SHA is recorded in PR #264's current-state section, not embedded in
  its own contents. No prior milestone was amended or squashed.
- Last fetched main matched `150b30b776565194260cc327eeeffdfb46475e81`;
  deployed production SHA remains unverified.
- All implementation workers were stopped. They reported no active build/test
  sessions. No implementation fixes or acceptance runs were made for checkpointing.

## Implemented In The WIP

These are source-level implementations with partial focused evidence, not
end-to-end acceptance claims. No major Slice 5 area is fully certified.

- Seven timezone-aware schedules, background wrappers/common job guards,
  hourly phase ordering, scoped Run Now and protected Hangfire dashboard.
- Scalar QueueProgress and nine finite scans, caps/keysets/watermarks,
  checkpoint fencing, atomic local outcomes and independent recovery budget.
- Canonical identifier classifications/normalization, historical BIB and
  incomplete-operation protection; existing hold journal extended for automatic
  acquisition/recovery, inactive acquired recovery and final queue fencing.
- Four timeout families and later-phase guards; exact tracked-hold terminal
  fulfillment plus positive checkout and provider-failure diagnostics.
- Weekly scoped open-action counts/samples/links, durable forced-run identity,
  recipient checks; admin Test email, failed-only inspection/retry, sparse sender
  inheritance, session cleanup and terminal email-payload cleanup.
- Reference refresh preserves configured patron-code allowlists and creates
  inactive organizations. Operations UI uses existing requests/safe DOM and
  scope guards; Node coverage was added.

## Partial And Not Started

- R6 paging tests pass, but unresolved-oldest/actionable-later behavior across
  all nine queues still needs explicit oracles. Two newly added tests,
  `QueueCursorRowDeletionDoesNotResetThePersistedCheckpoint` and
  `LateCommittedRowBelowWatermarkIsVisitedOnTheSameFiniteCycle`, are uncompiled
  and unrun. Inspect their unfinished fixture setup before resuming execution.
- `NormalWeeklySummaryUsesBusinessPeriodAndQueuesOnlyOnceForCurrentRecipient`
  was also added after the last build; no execution receipt exists.
- Remaining matrix work includes phase-order/low-cap later-timeout guards,
  both participation lock orders, manual actor rebind/move/demotion, F1 settings/
  state/barrier/DST runtime cases, F2 stale identity/state/actor/provider cases,
  weekly recipient races and first processor cycle from imported business rows.
  Reconcile named tests against the local matrix; older status labels are stale.
- Browser harness now applies protected role cookies, seeds failed/sent/
  suppressed mail, clicks Retry and checks refresh, denies terminal/forged-scope
  actions, and delays an old-scope response. C# probe compilation and all actual
  published-browser/desktop/mobile/axe execution remain unperformed.
- Final clean candidate suites, Web/native publications, native fixtures,
  source/artifact verification, readiness audit, Terra review and acceptance
  have not started. No final discovery floor is established; CI still uses 190.
- No Slice 6/analytics, merge, tag, release, deployment, rehearsal or cutover.

## Schema And Migration

Schema changed from 4 to 5; migration contract is `slice-05`. Added the prescribed
ten-field QueueProgress table with composite queue/scope key and rowversion.
Importer rejects nonempty cursors; reconciliation measures/checks runtime cursor
counts. Existing export, identifier/placement evidence and configuration mapping
remain in place. The first actual post-import processing cycle is still unproved.

Last local build DACPAC: `database/Asap.Database/bin/Release/Asap.Database.dacpac`,
SHA256 `d0b47057e6e5bf8ff8b84f2dce8afa4202f1755fd4bc88738068e06c428bba35`.
This is an ignored intermediate build, not a certified publication. The prepared
`slice-05.md` currently mislabels the starting schema as 5 and duplicates a
DACPAC sentence; starting schema was 4. Left unchanged at the user's stop.

## Existing Results And Failures

All following results predate checkpointing and do not certify current bytes.
Logs below are under `.artifacts/slice-05-luna-integrated-20260914a/` unless noted.
Focused counts overlap and must not be summed into a full-suite total.

| Receipt | Actual result |
|---|---|
| `test-full-integrated-253.log` | 251 passed, 2 failed, 0 skipped |
| `test-current-intermediate.log` (latest full run) | 260 passed, 13 failed, 0 skipped |
| `build-timeout-fulfillment-rerun-2.log` (last build) | Success, 0 warnings/errors; newer tests not compiled |
| `test-timeout-fulfillment-rerun-2.log` (last test run) | 3 passed, 0 failed/skipped |
| `test-migration-slice5.log` | 19 passed, 0 failed/skipped |
| `test-r6-fairness-5.log`; `test-coupled-r6-3.log` | 9/9; 2/2, zero skips |
| `test-queue-edge-green.log`; `test-queue-failure-tests.log`; `test-queue-edge-3-green.log` | 2/2; 4/4; 2/2, zero skips |
| `test-f2-negative-green.log`; `test-fulfillment-runtime-green-2.log` | 2/2; 4/4, zero skips |
| `test-email-schedule-green-final.log`; `test-lock-order-regressions.log`; `test-api-scope-proof.log` | 5/5; 4/4; 2/2, zero skips |
| Max kernel/shared logs under `.artifacts/slice-05-luna-max-20260914c/` | 10/10 kernel; 31/31 shared, zero skips; intermediate only |
| `.artifacts/slice-05-luna-frontend-20260914a-node-suite-final.log` | 173 Node test files, all passed |

The 13 full-run failures concern session cleanup, empty identifier normalization,
eight queue DataRows (not HoldRecovery), timeout behavior, forced weekly summary,
and coupled recovery/placement. Later focused successes do not establish that the
full-suite interaction failures are resolved. Claims about stale binaries or
fixture isolation remain unverified by a fresh full run. No green final suite.
Some earlier filters discovered zero tests; those are not evidence. Use actual
method filters on the partial `PatronJourneyTests` class and a discovery floor.

Browser sidecar only ran `node --check operations.cjs`, PowerShell parsing and
diff checks successfully. No browser gate ran. Checkpoint `git diff --check`
passes with the repository's normal line-ending configuration.

## Preserve Local Evidence

Keep this checkout; do not clean ignored files or create a replacement worktree.
Paths below are repository-relative and are intentionally NOT committed:

- `.git/asap-slice-05-coverage-matrix.md`, `asap-slice-05-astra-test-map.md`,
  `asap-slice-05-astra-ordinary-gaps.md`, `asap-slice-05-luna-max-kernel-handoff.md`.
  Older Astra/Luna handoffs remain in `.git/` but contain superseded status text.
- `.git/asap-slice-05-{candidate,final-fixtures,final-browsers}.ps1`,
  `asap-slice-05-{verification,runtime-oracle}.cjs`, harness-preparation receipt,
  and prepared Terra instructions (preparation only; no review occurred).
- `.git/asap-slice-05-admin-migration-acceptance/` and
  `.git/asap-slice-05-patron-browser-probe/`, including the interrupted sidecar's
  `operations.cjs` and `Program.cs` edits; these have no published-browser receipt.
- `.artifacts/slice-05-luna-{work-20260914a,max-20260914b,max-20260914c,integrated-20260914a}/`
  and `.artifacts/slice-05-luna-frontend-20260914a-*` logs.
- Frozen `.git/asap-real-pb-source/` fixtures and all existing Slice 4 probe,
  fixture, publication and receipt paths. Preserve source snapshots unchanged.

## Next Action After Explicit Resume

Use a fresh Luna High implementation context in this same checkout. Inspect the
two uncompiled R6 tests, the new normal-weekly test and partial browser probe
first; then reconcile actual methods/receipts with the local coverage matrix and
test map. Resume ordinary implementation/focused diagnosis from those gaps, not
from a clean-slate port analysis. Only after complete current validation may
Astra dispatch fresh Terra. Do not resume work merely because this WIP CI runs.
