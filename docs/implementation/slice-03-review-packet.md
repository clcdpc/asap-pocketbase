# Slice 3 Independent Review Packet

## Pass 5 Candidate

Sol corrected S3-T4 by mirroring the existing mutation-error pattern: current
409 refresh feedback remains intact, while other recovery errors require the
current mutation token/version and ignore superseded/auth/abort completions.
The focused real-browser regression first fails when an older 502 replaces the
newer current error, then passes with the correction. Full Node tests, Release
build with zero warnings, complete staff browser journey and exact 190 tests pass.

Final immutable root: `.artifacts/slice-03-final-s3-t4-3e37a48-20260913a`.
666-file inventory `.git/asap-slice-03-artifact-inventory-pass5.json`:
`57b8fc3c6656889ac6e498cfc67bea6465ea292092ebdf5922107a0f2c1b0388`.
25-path source receipt `.git/asap-slice-03-review-state-pass5.json`:
`62ab4acb40a9e1cc8fd9ee01509d336f2da43c32f0c97769f5a55c4701cc4684`.
Workflow JS: `93ad87b7d78172d5e3332c42180d4e5d1d546bcc7cf48f47c976189094371163`.
Web DLL and both matching 2eac1f4e DACPACs remain unchanged. Astra verifies all
340 native/package files identical to the final four native acceptance runs,
43 source assets, 72 compressed variants and 17 pinned vendors. Parent's final
190-test gate and both published race suites now pass on this freeze; exact
results are in evidence. Terra completed the full slice review, not only S3-T4,
on 2026-09-13: **CLEAR**, with all prior findings resolved and both receipts
independently verified. Sol remained paused and Terra made no shared builds or
tracked edits. The review outcome is recorded in `slice-03-review.md`.

## Original Pass 4 Candidate

Sol fixed S3-T3 and S3-A2. Review the full slice again, including all prior
findings and the existing mutation/create/recovery completion callers. Accepted
server writes are not cancelled or represented as rolled back; stale completion
effects are fenced from newer dialog/authentication contexts. Ordinary own-library
title-request Assign is restored to the pinned behavior without widening the
management API or changing backend authority.

Final immutable candidate:
`.artifacts/slice-03-accepted-candidate-3e37a48-20260913a`.
666-file inventory `.git/asap-slice-03-artifact-inventory-pass4-final.json`:
`fef86a321baf8a5418fb9195170f81622dbaffe1403829f509e8396266aa4e01`.
25-path source receipt `.git/asap-slice-03-review-state-pass4-final.json`:
`6369c9c8ce93754379f399212245dff9440884f5560328fa75333588c2033b83`.
Web DLL remains `b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`;
published workflow JS is
`a0898454bb8e594c971eee174f08e50763ece68605357a6fbc8977a142fa1047`.
Both DACPAC copies are now
`2eac1f4e3e3e1a18b5e2b745fcb3f924c31df0280708b088fd08016b257b7306`.

This stages Sol's source-built Web unchanged. The copied earlier native DACPAC
initially differed only in Origin.xml build metadata; Astra independently proved
all four substantive archive members identical and put the actual clean-build
DACPAC in both locations in a new candidate directory. Earlier frozen artifacts
are untouched; no application code or migration package changed. Native EXE/DLL
and all package bytes remain unchanged. All 43/72/17 asset checks pass.

Sol's full Node suite, clean Release build and exact 190-test gate pass. Parent
published mutation-race run `93137040c532476faced9294f514424c` passes close,
delete and title assignment on both viewports with accepted A persistence and
unaffected B/candidate work. Candidate-race run `d9d7ab0aa944426488b88acdf68e8932`
passes both request types/viewports using ordinary staff, confirming restored
title assignment. Parent final 190-test, all four native and all published
baseline probes now pass; exact results and cleanup are recorded in evidence.
Sol is paused; no shared builds or application edits during review. No blocking
defect may be waived by pass count.

## Original Pass 3 Candidate

Sol fixed confirmed S3-T2 using the existing latest-load mechanism, one shared
assignment-candidate slot, cancellation, current dialog/type/ID/version guards
and connected-form submit guards. Open/rerender/close/sign-out invalidate pending
work. Review the full slice and surrounding effects, not only this final fix.

Superseding source-built Web plus unchanged byte-verified native/package root:
`.artifacts/slice-03-final-s3-t2-3e37a48-20260913a`.
Full 666-file inventory `.git/asap-slice-03-artifact-inventory-pass3.json`:
`4b1884a519f6a5481c344a5bc4583f71c7a24f32e77c3654f389fadd12d33819`.
Full 25-path code receipt `.git/asap-slice-03-review-state-pass3.json`:
`2099a4718ad4a471c48185f9132266a5ecf86451f8b8f8c1957555f670c60329`.
Web DLL remains `b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`;
published `staff/js/workflow.js` is
`e65ea89cc371d565f09f0248b9f50d939187d8fc1c73701ef58aed90b6f798ed`.
Astra independently confirms all 340 native/package files unchanged, all 43
frontend source matches, all 72 compressed variants and all 17 vendor pins.

Sol's focused delayed-response browser regression passes for both request types,
followed by full Node tests, clean Release build and exact 190/190 CI-floor
.NET run with zero skips. Parent independently reproduced S3-T2 against Pass 2
and is now rerunning the same published-browser regression and full 190 tests
against Pass 3. Results will be recorded, not assumed. Sol is paused for review;
no application/shared-build changes are permitted. Any remaining blocking issue
must still be fixed, regardless of nominal pass count.

## Original Pass 2 Candidate

S3-T1's confirmed assignment-picker defect is fixed by Sol. Review the entire
slice again, including the minimal scoped candidate endpoint, both affected
pickers, their callers and new ordinary-staff API/browser regressions. Do not
limit Pass 2 to the patch. Staff Access management permissions remain unchanged;
assignment mutations remain the eligibility/version authority.

Superseding immutable root:
`.artifacts/slice-03-final-s3-t1-3e37a48-20260913a`.
Web DLL SHA-256:
`b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`.
Native EXE/DLL, both DACPACs and all four source packages remain unchanged from
Pass 1. Astra independently compared all 340 native/package files byte hashes.
All 43 frontend sources, 72 compressed variants and 17 vendor hashes pass again.

New complete 666-file receipt:
`.git/asap-slice-03-artifact-inventory-pass2.json`, SHA-256
`6e1334fe615a7562a9ff5de67ec01d3eec99510f6f8a12af350a7cfadb637317`.
New 25-file full-slice code receipt:
`.git/asap-slice-03-review-state-pass2.json`, SHA-256
`665140ca0858b4d9e9cf5491b476ce068e7df4100f02a0507b79f87833f83559`.

Sol reports full Node tests, Release build without warnings/errors, two complete
190-test runs including the raised exact CI floor, focused real-SQL API and
expanded Playwright regressions all passing. The four newly exported packages
validate. Sol is paused. Parent independent 190-test, new published-assignment
browser/SQL, full AdditionalCopy lifecycle browser/SQL and patron browser/SQL
regressions all pass against this freeze; exact runs are recorded in evidence.
No shared builds or source changes are allowed during review; isolated no-build
probes are separate. The independent full Pass 2 outcome is still required.

## Original Pass 1 Candidate

Fresh Terra High must review the complete Slice 3 implementation against
`3e37a48926833a8a92045260fb92b42ef1a090ca`, whose application code is identical
to accepted Slice 2 milestone `9f946ae`. Include all 25 changed/new code, schema,
frontend, test and CI paths, not only the parent acceptance fix. Sol XHigh
Lorentz is paused for acceptance/review. Do not edit application/tests or run
shared builds during review; report confirmed findings for Sol to fix.

Read root AGENTS, `slice-03.md`, `slice-03-evidence.md`, the relevant authoritative
pack sections linked by the slice packet, and actual pinned PocketBase source
at `150b30b776565194260cc327eeeffdfb46475e81` whenever it resolves behavior.
Keep existing scope and architecture decisions. This is implementation review,
not permission to redesign the email subsystem or reopen the port architecture.

The final source-built candidate is
`.artifacts/slice-03-final-3e37a48-20260913a`:

- Web DLL: `800b7c3a83a636414456bd31cbe947c391d27d1934559fe4d6b340712da39c37`.
- Native EXE: `676771b04e3f42e57b3c49f628798ddbdcc918ebc71300c80a2e80fcba6f0cea`.
- Native DLL: `d0341ffc6bc89f50af0aba2f77485a06fffa7567ef07045c5175fd4e50e69c9b`.
- Matching native/embedded Web DACPAC: `749c875fc30a30cffabe75f8bdc710701ac6aca468ed54813d58ae2a4cf8e6f1`.

Astra hashed all 666 files, including 326 Web, 252 native and four 22-file
synthetic source packages. The independently recorded complete JSON inventory is
`.git/asap-slice-03-artifact-inventory-pass1.json`, SHA-256
`bf9218d16211d97280b26b6369de1567b1dbee8af0c3edd4adfb7dcff280df3c`.
The 25-file code receipt is `.git/asap-slice-03-review-state-pass1.json`, SHA-256
`b5c703585150d4b4b3f7be480df57dd32748736b0101b783e2aa2204777ccc9f`.
Documents are excluded from that code receipt, not from contextual review.
All 43 frontend source files match the candidate, 72 compressed variants decode
to exact base bytes, and all 17 pinned vendor hashes match. Forbidden legacy,
Node runtime and dev-email payload count is zero. Synthetic packages are local
acceptance inputs, not application deployment payloads.

Sol reports clean Release builds, all Node files passing and two full .NET runs
passing 189/189, including the exact updated CI minimum. The expanded staff
browser integration covers 18 desktop/mobile states and direct SQL outcomes.
Astra independently passed the exact 189-test floor with zero failures/skips,
all four final native import/reconciliation/SQL probes, and the final published
desktop/mobile browser plus direct SQL probe. All owned probe resources were
cleaned up. Final run identifiers and results are recorded in the evidence file.
This frozen candidate is ready for independent Pass 1, not yet an accepted slice.

## Full-Slice Focus

- Complete source preview/create and independent task grid, scoped lookup/deep
  links, claim/unclaim/assignment, close/reopen and reduced closed-task deletion.
  Preserve the original request's hold stage and task-owned library/bibliographic
  snapshots; source edits/deletion must not rewrite or cascade-delete the task.
- Organization -> stable StaffUser IDs -> source/task -> dependents lock order.
  Re-read the preselected claimant and rowversion after locking; changed candidate
  returns conflict or restarts in order, never discovers/locks StaffUser behind
  the task lock. Current actor authorization is separate from stored claimant
  eligibility, which does not require library participation.
- Inherited create and reopen retain valid original attribution/type/rule without
  substituting current actor/rule owner. `legacy` with a mapped historical rule
  is legal even when that inactive rule references another historical staff row.
  Closed history remains untouched until reopen; invalid open claims clear every
  effective field and preserve attribution/time/reason in Notes exactly once.
- Lifecycle deactivation, library move and cross-library super-admin demotion
  share the serialization point, clear invalid open task claims, preserve closed
  history, and record separate AdditionalCopy cleanup counts. Review both race
  orderings for each required R3 mutation, valid staff/admin/super-admin and
  claimless reopen, stale/repeated requests, rollback and no auto-assignment.
- Current scope/version/antiforgery enforcement and atomic source note/task/mail
  intent. Existing outbox states, deterministic keys, recipient tuple and
  RecipientAddressKind, current contact/identity/scope revalidation, domain safety,
  leases/deadlines/fencing/retries and cancellation remain intact. Temporary
  FileEmailSender stays only the final transport; no fake webhooks or release
  completeness claim.
- Actual stopped PocketBase export through the source-built native CLI, schema 4
  / slice-03 / report v4 mapping and semantic reconciliation. Verify nullable
  deleted-source links, all retained snapshots, missing-updated creation fallback,
  source-valid 128-character task fields and nullable historical closer, all
  operational-claim conversions and closed history, current-eligibility SQL,
  deterministic reports, clean strict-identity refusals and empty imported outbox.
- Reduced deletion-audit migration retains request type/key/library/bibliography
  and attribution/times while excluding full patron details and Notes. Same-count
  task, audit and staff-preference drift must fail reconciliation.
- Browser safe DOM, stale loads/mutations, live Grid replacement-row focus,
  sign-out formatter null safety, Mine/unclaimed refresh and claim-clear feedback.
  Inspect actual desktop/mobile screenshots, keyboard/focus, layout and axe, not
  only selector assertions. Source/task/hold-summary UTC wire values and retained
  Notes must preserve SQL instants for non-UTC clients without changing DateOnly.

## Review Cycle And Stop Boundary

Parent acceptance finding S3-A1 (zone-less UTC serialization) is reproduced,
fixed by Sol and independently verified in the evidence file. Review its full
surrounding effects along with the rest of the slice; do not limit Pass 1 to it.

Record findings with severity, concrete file/line/caller/effect, violated
contract and focused regression requirement. Distinguish confirmed defects from
questions and nonblocking follow-ups. Sol implements fixes; the same Terra
context performs a full Pass 2, and further full review if substantive findings
remain. No blocking issue is waived by a nominal pass limit.

The user's latest instruction is to finish Slice 3, commit/push after its full
review gate, verify actual CI, and stop with a clean working tree. Do not start
Slice 4. The draft PR and real Postmark/rehearsal/release blockers remain open;
Slice 3 completion is not production readiness.
