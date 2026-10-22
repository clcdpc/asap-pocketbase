# Slice 2 Independent Review Packet

## Current Pass 2 Freeze

Retain Terra High Ohm for full Pass 2 after the S2-T1/S2-T2 fixes. Do not review
only those patches. Read the complete finding/advisor/operator-procedure record
in `slice-02-review.md` and `hold-resolution-operator-evidence.md`, then the full
current slice against predecessor `1e36761c771db70d0b669087d0a843b66cf5618b`.

Current artifact: `.artifacts/slice-02-final-c3782c487ad1`.
Astra directly verified all 579 payload sizes/hashes and exact disk inventory.
Inventory SHA-256:
`58f6b58f0ddc5e6d2451a12d50ebe22563d4663a15f2d3fd50d8013c5b7258e7`.
Verification record SHA-256:
`58a8f8143681d9adb250d03dae4f498e4bd54c9846ecb5cbf5e3d0fbeab5e1db`.
Web DLL SHA-256:
`c6b8b9b32ee5da9f99dbede62ecd2bda6cefbeeac6f535964525285c556c4d70`.
Native migration/DACPAC/contract bytes match the recorded prior accepted
native checkpoint. Do not substitute the superseded `62a723e7e179` or
`d7f4c9a182be` Web publication for this freeze.

The full 48-file code receipt is `.git/asap-slice-02-review-state-pass2.json`,
SHA-256 `ed0e43ead2d93a3aa71c7cbfe9b56fca8ab14c175211c9f4ccc9654715cb352f`.
This adds the concrete workflow guard and CI floor to the earlier 46-file
scope. Sol is paused. No shared builds or app/test edits during review.

Sol's final post-browser-extension run passed 181/181 tests with zero skips,
clean Release build and frontend/npm tests. The real staff browser now covers
twelve states and an actual resolution submission with exact SQL proof/ID/
epoch/state and once-only event/audit/outbox assertions. Astra inspected the
upper/lower desktop/mobile form and report. Astra's independent exact-floor
181-test rerun passed 181/181, zero failures/skips, in 2m31s. All 43 current
frontend source files match the frozen Web payload. Actual native contract
inspection passes. Final native original/expanded SQL acceptance and both
pinned-source recipient oracles pass; expanded results are byte-identical to
the preceding equivalent fresh target. All 72 generated compressed frontend
variants also decode to exact current source payloads.

## Pass 1 Freeze History

Prepared for the completed Slice 2 full independent review, not a review result.
Astra records the completed handoff, exact test results, final source-built
artifact directory/hashes and full code receipt here. Do not review an
interim snapshot as if it were the completed slice. The accepted predecessor is
`1e36761c771db70d0b669087d0a843b66cf5618b`; include all Slice 2 tracked and
untracked code, schema, frontend, tests, dependencies, configuration and CI.

Sol's completed local handoff is now frozen. Final artifact:
`.artifacts/slice-02-final-d7f4c9a182be`, 326 Web and 253 migration payload
files. Astra verified all 579 lengths/hashes and the exact file inventory.
Inventory SHA-256:
`df0f749a7067ad1721511dc1a4ac74b693d3c5b3d6c22c150dedcb392f286e3f`.
Code receipt `.git/asap-slice-02-review-state-pass1.json` contains 46 changed
implementation/test/schema files, SHA-256
`9f7890b6ac18faa6e23f9b05e23bf80af30dd37aa943d80eaed0ee841061bec6`.
Documents are excluded from that receipt, not from contextual review. No
application changes or shared builds are permitted during the frozen review
unless Astra explicitly coordinates them. Sol reports 174/174 tests, zero
skips, clean Release build, frontend tests and native publication checks;
Astra independently passed 174/174 tests, zero skips, in 2m38s with the minimum
set to 174. Final native expanded acceptance run
`762148216d8c45ab836978cf94049c9d` passed identities, preferences, contact,
claim/history/rule semantics, clean-target refusals, empty outbox, reconciliation
and deliberate drift detection. Its five-user pinned-source recipient oracle
also passed. The earlier shared-fixture failures and fixes remain in the
progress/evidence history, not waived as expected failures.

Use fresh Terra High for full Pass 1, preserving that context for full Pass 2
after Sol fixes, and Pass 3 only if substantive new findings require it. Terra
does not implement feedback. No known correctness/security/data-integrity/
migration/material-regression issue is waived by the nominal pass cap. Astra
owns integration/tests, coherent milestone and actual remote CI acceptance.

Read root AGENTS, `slice-02.md`, the relevant authoritative pack sections linked
there, actual prior-slice implementation, `staff-source-notes.md`,
`slice-02-evidence.md` and the temporary-email decision. Read exact pinned source
when it resolves behavior. This is adversarial implementation review, not an
architecture redesign or permission to relax a difficult contract.

## Full-Slice Focus

- Entra issuer/tenant/tuple validation, local allowlist and real persisted-cookie
  middleware, current row revalidation, same-key restart/tenant removal/rebind,
  secure return URL, explicit sign-in/out, antiforgery and JSON API responses.
  Testing-only auth must never be enabled by the email-safety switch or ordinary
  Development configuration. Bootstrap cannot repair a populated table at startup.
- Usable-admin concurrency and candidate configuration: app lock before ordered
  row locks, both final-admin race outcomes, invalid configuration fail-closed
  workers/business endpoints, sparse repair diagnostics and separate migration
  bootstrap. Null primary contact survives readable Entra metadata refresh.
- All preferences/API/UI defaults; current scope on lists and every mutation;
  complete authorized Grid.js rows, deep links, temporary workflow scope and
  stale-load rejection. Check actual desktop/mobile screenshots, focus/keyboard,
  named action semantics, useful current capabilities and serious/critical axe.
- Organization -> StaffUser -> request -> dependent lock order, current assignee
  revalidation after waiting, lifecycle deactivation/move/demotion cleanup and
  rollback/audit counts, dormant-library relationship preservation and closed
  history. Test both race orderings; no transaction spans provider calls.
- Expected rowversions, status-transition matrix, atomic local events/tags/mail,
  actor versus manual/automatic claim behavior, immutable patron snapshots,
  identifier/BIB starting/proposed-stage checks and all protected reopened known/
  null legacy cases. Dedicated pickup checks the barrier before Polaris and
  respects the specified already-started pickup/hold acquisition interleaving.
- Every hold entry point, one incomplete numbered operation, owner/epoch/lease/
  heartbeat fencing, durable one-way create/reply/result boundaries, provider
  deadline, exact reply context, inactive-library acquired-work continuation,
  crash recovery and late worker writes. No hidden retry or uncertain replay.
- Evidence, not naming, proves a provider outcome. One new or sole same-BIB hold
  does not correlate an already-dispatched operation. Pre-dispatch adoption is
  separate. Completed success without final ID is not an incomplete barrier;
  null-to-proven-ID enrichment uses the narrow internal fenced write, never a
  guessed lookup or repeated completion. Synthetic evidence tests prove local
  fencing only. Operator resolution requires operation-specific authoritative
  evidence and executor exclusion where applicable; a numeric ID is not proof.
- Existing SQL outbox state/business-key/address-kind/recipient-tuple snapshots,
  common current eligibility, domain safety, lease/deadline/retry/fencing and
  immutable business-mail behavior survive staff integration. Temporary file
  transport does not authorize fake webhooks or release-completion claims.
- Real stopped-source -> new native exporter -> fresh SQL import/reconciliation,
  strict Entra maps/bootstrap, per-kind actual-old versus final-target recipient
  deltas, all claim metadata/history/rule cases, deterministic annotations and
  equivalent fresh imports. Verify semantic assertions, not just row counts or
  successful CLI exit. No fabricated operation/final ID from legacy protection.

## Finding Record And Evidence

S2-A1/A2/A3 have independently passing interim and final native expanded
fixtures. S2-A4's no-inference correction and the bounded advisor
decision are recorded in `slice-02-evidence.md`; verify the final implementation
and complete F2 fences, not the earlier tests that certified unsafe inference.

Findings should name concrete behavior/race, reachable caller, contract/source
anchor, affected path/line and focused missing or failing test. Include findings
in surrounding accepted code when this slice exposes them. Distinguish actual
failures from test-fixture mistakes, unsupported external capability and truly
nonblocking cleanup. Return the full reviewed scope, commands/results and
residual limitations even if clean. Do not commit, push, merge, tag or deploy.
