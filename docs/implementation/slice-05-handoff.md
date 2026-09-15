# Slice 5 Review-Candidate Handoff

## Checkpoint

Recorded 2026-09-14 after the Slice 5 review-fix validation.
**Slice 5 remains not accepted.** Terra Pass 1 found exactly one substantive
P1 issue: the weekly-summary Run Now endpoint ignored `force=false` and always
queued a forced summary with a new `ManualRunId`. The endpoint fix is complete;
focused Terra re-review is still required. Slice 6 has not started. PR #264
remains draft. Any CI triggered by this WIP candidate is diagnostic only.

- Repository/branch: `clcdpc/asap-pocketbase`, `codex/csharp-port`.
- Resume starting SHA: `7a3919994d182ec1de3552b2861e657817933f69`.
- Accepted Slice 4: `417c72430652a35bc8fc1da549ae270eabc86429`.
- Review-candidate SHA is recorded in PR #264's current-state section after the
  candidate commit; it is not embedded in this commit's own contents.
- Behavioral PocketBase pin and fetched `origin/main` remain
  `150b30b776565194260cc327eeeffdfb46475e81`; the deployed production SHA is
  still unverified.
- No merge, tag, deployment, rehearsal, cutover, or final milestone
  acceptance was performed.

## Review-Fix Validation

- Starting review candidate: `eb74e00477f28525359fbd851026199a733b4413`.
- Ordinary `force=false` Run Now now queues the existing ordinary weekly-summary
  path with no manual run ID; explicit `force=true` retains one generated ID in
  the enqueued forced invocation.
- Endpoint dispatch tests: 2/2 passed; weekly-route ordinary-staff authorization
  regression: 1/1 passed. Directly affected weekly-summary tests: 5/5 passed.
- Complete .NET/real-SQL suite: 297/297 passed, 0 failed, 0 skipped. Clean
  Release solution build: 0 warnings, 0 errors. Focused/full receipt:
  `.artifacts/slice-05-run-now-fix-20260914/full-dotnet-real-sql.log`.
- The finding is fixed locally. Focused Terra re-review has not been performed;
  Slice 5 remains not accepted and Slice 6 has not started.

## Completed Slice 5 Work

- Seven timezone-aware schedules, common workflow guards, hourly phase ordering,
  scoped Run Now, and protected Hangfire operations access.
- Scalar QueueProgress with the nine logical scans, composite queue/scope key,
  keyset ordering, finite cycle watermarks, low-cap fairness, checkpoint fences,
  deletion/backdated/late-commit behavior, and independent recovery budget.
- Canonical identifier normalization and classifications, historical BIB and
  incomplete-operation protection, automatic hold acquisition/recovery,
  inactive acquired recovery, and final queue fencing.
- Four timeout families with business-calendar cutoffs, later-phase deferral,
  exact boundary/DST behavior, disabled/inactive handling, durable rejection
  email, and scoped sender inheritance.
- Exact tracked-hold fulfillment with positive checkout evidence, provider
  failure diagnostics, stale request identity protection, and actor
  participation checks at the final boundary.
- Weekly scoped counts/samples/links, durable forced-run identity, dedupe,
  current-recipient locking, and recipient race coverage.
- Admin Test email, failed-only email inspection and Retry, terminal
  sent/suppressed behavior, session and email-payload cleanup, and safe scoped
  operations UI.
- Reference refresh allowlist preservation and inactive organization creation.
- Migration/reconciliation proof for schema 5, empty QueueProgress, imported
  business rows, non-imported legacy cursor/runtime state, identifier and
  placement protection, and operational configuration reconciliation.

## Pre-Review Validation

Fresh final evidence is retained under the candidate, native-fixture, and
published-browser paths below. The candidate receipt is the attribution point
for the Release build, complete .NET/real-SQL suite, Node suite, Web and
self-contained win-x64 publications, and publication hashes.

- Candidate: `.artifacts/slice-05-candidate-review-candidate-20260914l/`.
- Candidate receipt: `.git/asap-slice-05-candidate-review-candidate-20260914l.json`.
- Native migration fixtures: `.git/asap-slice-05-final-fixtures-review-candidate-20260914l/`.
- Published browser journeys: `.git/asap-slice-05-final-browsers-review-candidate-20260914l/`.
- Artifact/source verification receipts are named
  `.git/asap-slice-05-artifact-inventory-review-candidate-20260914l.json` and
  `.git/asap-slice-05-review-state-review-candidate-20260914l.json`.
- Focused and current-byte full-suite logs remain under
  `.artifacts/slice-05-luna-resume-20260914a/`; the final candidate log is the
  authoritative suite receipt.
- Final validation requires and records zero Release warnings/errors, zero
  unexplained skips, fresh publications, native export/import/reconciliation,
  all published browser journeys, desktop/mobile checks, serious/critical axe,
  artifact exclusions, DACPAC/source/artifact hashes, and `git diff --check`.

## Schema And Migration

Starting schema version was `4`; Slice 5 advances it to `5`, with migration
contract `slice-05`. QueueProgress is the prescribed scalar table keyed by
`(QueueName, ScopeOrganizationId)`, with nullable `CycleMaxId` and rowversion
checkpoint fencing. The native fixtures prove a fresh target starts with no
QueueProgress rows, the first processor cycle visits imported business rows,
legacy cursor state is not imported, runtime sessions/operations/outbox state
is not fabricated, and identifier/placement/configuration reconciliation stays
protected.

## Preserved Local Evidence

Ignored harnesses, fixtures, logs, and receipts were retained. In particular:

- `.git/asap-slice-05-coverage-matrix.md`,
  `asap-slice-05-astra-test-map.md`,
  `asap-slice-05-astra-ordinary-gaps.md`, and
  `asap-slice-05-luna-max-kernel-handoff.md`;
- `.git/asap-slice-05-{candidate,final-fixtures,final-browsers}.ps1`,
  `asap-slice-05-{verification,runtime-oracle}.cjs`;
- `.git/asap-slice-05-admin-migration-acceptance/` and
  `.git/asap-slice-05-patron-browser-probe/`;
- frozen `.git/asap-real-pb-source/` fixtures and all prior
  `.artifacts/slice-05-*` evidence.

The retained Testing authentication boundary and deterministic providers are
used for browser/native verification. No live Entra or live Postmark
requirement was added.

## Next Step

The next authorized activity is focused Terra re-review of this fix candidate.
This handoff intentionally does not perform re-review, Slice 5 acceptance,
Slice 6, or any release, deployment, rehearsal, cutover, merge, or tag operation.
