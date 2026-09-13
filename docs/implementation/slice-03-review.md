# Slice 3 Independent Review

## Scope And Freeze

Reviewer: fresh GPT-5.6 Terra High Kuhn,
`01a09a1d-5c47-7293-9f91-4e7360a88d61`. GPT-5.6 Sol XHigh Lorentz retains
implementation ownership and fixes confirmed findings; Terra does not implement
its own feedback. The same Terra context must review the full slice again.

Accepted application predecessor: Slice 2 milestone `9f946ae`; review base is
documentation checkpoint `3e37a48926833a8a92045260fb92b42ef1a090ca`.
Pass 1 includes all 25 source/schema/frontend/test/CI paths in
`.git/asap-slice-03-review-state-pass1.json`, the final 666-file published
candidate `.artifacts/slice-03-final-3e37a48-20260913a`, and surrounding callers,
authorization, lock order, lifecycle cleanup, migration and outbox contracts.
See `slice-03-review-packet.md` and `slice-03-evidence.md` for the exact source
pin, hashes, final 189-test/native/browser acceptance and S3-A1 reproduction/fix.

## Pass 1

Completed 2026-09-13. Terra reviewed all 25 receipt paths and surrounding code,
verified the source receipt and artifact inventory checksum, and inspected the
pinned assignment/additional-copy/deletion behavior. It did not rerun shared
builds or alter application/tests. Pass 1 is blocked by the confirmed finding
below; Sol has resumed implementation for the fix.

### S3-T1: Assignment Candidate Authorization

P1 confirmed. The AdditionalCopy DTO advertises assignment to ordinary in-scope
staff, but `showAdditionalCopyAssignment` loads `/api/asap/staff/users`, whose
Staff Access endpoint only permits admin/super-admin callers. Ordinary staff
therefore receives 403 before choosing an assignee. That management list also
filters out valid system super-admin candidates for a library task.

The pinned `lib/staff/assignment_policy.js` permits ordinary active staff to
transfer an open own-library item; the target common eligibility predicate also
permits valid super-admin claimants. Assignment availability must agree across
the UI, candidate API and authoritative mutation service.

Fix only the scoped assignment-candidate boundary and affected pickers. Return
minimal eligible candidate data, not Staff Access management data; do not widen
the management endpoint. Include ordinary-staff browser/API assignment, eligible
system super-admin availability and foreign-library exclusion regressions.
Inspect the adjacent TitleRequest picker with the same dependency so the prior
implemented journey remains functional. Preserve commit-time eligibility checks.

Sol must rerun tests and publish a new immutable candidate before the same Terra
context performs full Pass 2. No Slice 3 milestone approval is claimed yet.

## Pass 2

Sol completed the scoped candidate fix, both picker updates and focused API/
browser regressions. Release build, full Node suite and two 190-test runs pass.
The superseding immutable publication and independently verified full-slice
receipts are listed at the top of `slice-03-review-packet.md`. All native/package
files are identical to the independently accepted bytes. Parent independently
passed all 190 tests and the exact published assignment, lifecycle and patron
browser/SQL probes. Terra completed full Pass 2 and verified all 25 source hashes
and the 666-file receipt. S3-T1 is confirmed fixed, but Pass 2 is blocked by S3-T2.

### S3-T2: Delayed Assignment Picker Targets The Wrong Task

P1 confirmed. Both candidate consumers await the GET without guarding the
current dialog. After opening A's picker, closing A and opening B, the late A
response can prepend a form into B whose submit closure still targets A. A's
valid rowversion and authorization do not prevent this wrong-task mutation.

Use the existing latest-load/dialog-state mechanisms to cancel or fence stale
candidate loads and submissions for both request types. Verify delayed A-to-B
responses, closure/session changes and valid B assignment without any mutation
of A. Sol owns the fix; the same Terra context must perform full **Pass 3** on
the new frozen candidate. No pass count can waive an unresolved blocking defect.

## Pass 3

Sol completed S3-T2 within the existing latest-load and dialog lifecycle. Both
request types have delayed-response browser regressions that fail before the
fix and pass afterward, with unchanged A state and only the visible B mutation.
Full Node tests, clean Release build and the exact 190-test floor pass. The new
immutable candidate and independently checked full-slice receipts are listed at
the top of the review packet. Terra completed full Pass 3 and verified all 25
source hashes and the 666-file receipt. S3-T2 is confirmed fixed. Parent's exact
190-test rerun passes with zero skips, and the published AdditionalCopy delayed
candidate case passes. Pass 3 remains blocked by the following two findings.

### S3-T3: Stale Mutation Completion Changes The New Dialog

P1 confirmed. AdditionalCopy mutation completions can unconditionally render A,
close the current dialog after deleting A, or reopen A on conflict after the user
has already opened B. A's accepted server mutation is legitimate, but the stale
UI response must not replace/close B or cancel B's candidate request. Guard
completion, refresh/conflict navigation and equivalent captured-request paths,
using existing dialog/load mechanisms. Do not treat client cancellation as
rollback of an accepted server mutation. Verify delayed non-delete and delete
responses with persisted A outcomes and unchanged B context/action targets.

### S3-A2: Ordinary Title-Request Assignment Hidden

P1 independently confirmed by parent and Terra. The existing title-request
action bar still checks admin/super-admin before rendering Assign. Pinned
`pb_public/staff/js/grid-row-actions.mjs:22-24` and
`lib/staff/assignment_policy.js` permit every authenticated active own-library
staff member to assign an open item; the target mutation already permits it.
The parent published ordinary-staff companion race test exposed this mismatch.
Remove the client-only restriction and verify ordinary title-request transfer,
eligible peer/system-super-admin choices and unchanged foreign/management
restrictions. This restores source behavior, not a permission redesign.

Sol owns both corrections. A full same-reviewer Pass 4 is required on the new
freeze because the nominal pass count cannot waive these blocking defects.

## Pass 4

Sol completed S3-T3/S3-A2 with focused real-browser regression coverage for
delayed accepted mutations, conflict responses, delete/create, same-ID reopening,
sign-out and ordinary title assignment. Full Node tests, clean Release build
and exact 190 tests pass. Parent independently passes both published race suites
on desktop/mobile. The final artifact/source receipts and DACPAC metadata-only
alignment are recorded in the review packet. Terra completed full Pass 4,
verified the 25-file source receipt and 666-file inventory, and corroborated all
final parent acceptance. S3-T3/S3-A2 are fixed. One narrow remaining finding
blocks this pass.

### S3-T4: Superseded Recovery Error Uses Only The Request ID

P2 confirmed. `mutateOperation` fences success and 409 recovery paths, but the
other error branch checks only current selection. After closing and reopening
the same request, an older non-409 failure can overwrite a newer status message
despite its invalidated mutation token/version. Apply the existing sibling
mutation-error pattern without changing backend behavior. Verify delayed old
errors are suppressed after same-ID/context changes while current errors and
current 409 refresh feedback still work. Sol owns the narrow fix and regression;
the same Terra context will perform full Pass 5. Prior passing tests do not cover
this specific completion ordering, so it is not waived by the pass count.

## Pass 5

Sol completed the narrow S3-T4 guard and real-browser red/green regression.
Current errors remain visible; an older delayed error cannot replace the newer
same-ID dialog's status. Full Node tests, zero-warning Release build, complete
staff browser journey and all 190 tests pass. The new immutable source/artifact
receipts are at the top of the review packet, with unchanged accepted native
payloads. Astra independently passed the exact 190-test floor with zero failures
or skips and both published race suites on the final candidate.

Completed 2026-09-13: **CLEAR**. The same Terra reviewed the full slice against
the original baseline, normative pack and pinned executable source. S3-T4 is
fixed; all prior S3-T1/T2/T3/A2 corrections remain sound. No additional confirmed
finding or nonblocking contract concern resulted. All 25 source paths and 666
artifact files match the Pass 5 receipts. Terra made no application, test,
tracked-document or build-output changes and did not rerun shared tests.

The independent full-slice review gate is satisfied. S3-A1's UTC correction and
all later findings are closed with recorded regression evidence. Local acceptance
and review authorize the coherent Slice 3 milestone; actual milestone CI remains
required before recording complete closure and the clean-tree stop.

## Stop Boundary

After mandatory fixes, full re-review, tests and milestone/CI, stop at Slice 3
with a clean working tree. Do not start Slice 4 or declare production readiness.
Real Postmark and the remaining implementation/rehearsal/release gates stay open.
