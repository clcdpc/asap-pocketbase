# Slice 4 Corrective And Independent Review

## Reviewed Candidate

Corrective start: `42e65cd3773cf57557e908fe79e01d77d58bac2f`.
Full Slice 4 base: `4769a8a8750c315e319824355d4508073bd43546`.
Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
Pass 1 candidate: `corrective-20260914b`; full Pass 2 candidate:
`corrective-20260914c`. Both have 51 changed/new code paths from the full-slice
base. `slice-04-review-packet.md` identifies current candidate-c source,
artifact, native fixture and browser receipts. Documentation is completed
after publication; no candidate-c code changed after its complete gates.

## Astra Corrective Review

After all complete candidate-b/native/browser gates passed on 2026-09-14, Astra
performed a fresh corrective review against the requested findings and the
authoritative contract. This is separate from earlier implementation diagnosis
and does not substitute for Terra's required full Slice 4 review sequence.

| Finding | Disposition and implementation evidence |
| --- | --- |
| 1. Staff lifecycle | Confirmed and fixed. `ChangeRoleAsync` preserves locked activity; `CanManage` checks locked actor, old relationship and resulting scope. Explicit re-add enforces active destination. Existing invariant, rowversion, atomic cleanup and history are preserved. |
| 2. Stale workflow privilege | Confirmed and fixed. Title/copy unclaim and closed deletion use locked actor roles. Assignment/transfer eligibility already used locked rows and remains enforced. Four SQL cases cover 24 demotion/deactivation/move interleavings across both serialization orders. |
| 3. Durable identity and label | Confirmed and fixed. Own-library administrator rebind, duplicate detection including inactive owners, allowed nonempty tuple and required normalized readable label. Tuple/label update is atomic; notification email is untouched. Original-tuple sign-in metadata fencing prevents a stale sign-in overwriting a rebind. |
| 4. Existing-cookie recovery | Confirmed and fixed. Valid forbidden identities retain only session/antiforgery, sign-out and Microsoft challenge recovery. Protected access remains 403, invalid binding remains 401, and sign-out retains antiforgery. UI recovery and explicit account selection are covered. |
| 5. Mail readiness organization | Confirmed and fixed in title/copy intent paths and hold completion. Actual resource library is checked outside SQL; locked resource/version/operation ownership is revalidated before commit. Patron submission, outbox delivery and operator-resolution readiness already used owning scope. |
| 6. Aggregate settings version | Confirmed and fixed. Canonical ordering covers every included multi-row domain; origins, all system participation rows and inherited branding are included. System version mutations acquire every Organization before Staff. Existing editable/effective scalar, set, provider, format, field/rule, template and branding domains are included. |
| 7. Hidden templates | Confirmed and fixed. Hidden system source or library lineage makes submission template unavailable; sparse enabled content inherits. No other implemented built-in-template delivery consumer required correction. Existing hold confirmation text is not a template-table consumer. |
| 8. Related authorization | Bounded sweep additionally fixed profile writes committing before participation validation, unusable empty/disallowed identities in invariant/assignment paths, and auto-claim staff locking/current eligibility. No other concrete post-lock stale-role mutation remained. |
| 9. Migration compatibility | No mapping/schema correction was required. Explicit allowed durable identity, metadata/notification separation, open-claim/rule normalization, deterministic reconciliation and source provenance remain intact; 19 migration tests and both fresh positive native imports verify them. |

The inactive-historical-claim concern was checked against migration contract
sections 6.3 and the auto-claim normalization rules: inactive effective open
claims are cleared and active rules normalized inactive before runtime use.
Staff deactivation also clears them; reactivation does not restore them.
Closed history remains intentionally preserved. No speculative repair path or
second lifecycle engine was introduced for unsupported inconsistent rows.

The settings aggregate intentionally does not acquire unrelated operational
StaffUser/audit data merely because pickers/history accompany the editor.
Staff records have their own versions and current locked eligibility checks;
configuration rules themselves participate in the aggregate.

The final validation also exposed and corrected two actual Grid.js dialog
focus races. Nine isolated behavioral cases cover absent/replaced openers and
new navigation/modal/auth/deliberate-focus boundaries; the original browser
assertions remain unchanged. Red and corrected evidence is retained in
`slice-04-corrective-notes.md` and `slice-04-evidence.md`.

Result: no remaining confirmed substantive issue in the corrective pass.
Source/artifact receipts and the 103 final browser states were independently
checked; representative desktop/mobile screenshots were visually inspected.
The post-documentation hash audit again matched all 659 non-document input
files, all 590 candidate files, four native receipts and all 12 browser runs'
reports/screenshots/SQL evidence. PR #264 remained open/draft at the original
checkpoint while review was dispatched; no premature milestone was created.

## Terra Full Review Sequence

Fresh GPT-5.6 Terra High Laplace (`01a0a00a-8fac-74a0-a536-480f5bde0852`) was
dispatched for full Pass 1 after all gates above. Retain that reviewer for
mandatory full Pass 2 and any further required re-review. Return
all confirmed substantive findings to the retained Luna Max implementation
context `01a09b0c-a918-7640-ac4a-6ed90d644f76` for fixes and retesting.
No clean Terra sequence, accepted milestone or exact-SHA remote CI is claimed
by this review preparation. Slice 5 has not started.

### Pass 1 - Substantive Finding

Terra completed full Pass 1 with one P1 finding: TitleRequest lifecycle cleanup
used an ordinary EF read before its rowversion-guarded save, unlike the held
update locks in AdditionalCopy cleanup. A distinct administrator can mutate a
global super-admin target's title claim in a library outside the lifecycle's
organization lock set, making the later lifecycle save throw and roll back.
The existing actor-demotion tests do not cover this distinct claimant race.

Astra confirmed the cross-library global-super-admin case and narrowed the
description: an ordinary target's own-library case is already serialized by the
common Organization update lock. The retained Luna received the confirmed
case, required TitleRequest update/hold locking and real-SQL unclaim/assignment
tests for both orderings on deactivation/demotion. Candidate b remains accurate
historical green evidence, not final acceptance of the upcoming correction.

Terra reported no other substantive finding and verified all 51 code hashes
and 164 referenced browser evidence files. It read the four fixture receipt
entries but did not replay the completed suites/providers. Same-reviewer full
Pass 2 is required after the fix and fresh complete candidate gates.

### Pass 1 Correction And Reproduction

The retained Luna reproduced four real-SQL failures and four passing opposite
orderings in `TestResults/corrective-terra1-cleanup-red2.trx`. Every
lifecycle-first combination of deactivation/demotion and unclaim/reassignment
threw `DbUpdateConcurrencyException`; workflow-first cases passed. An earlier
zero-selected filter run is explicitly not evidence of reproduction.

The existing title cleanup now uses parameterized `UPDLOCK,HOLDLOCK` queries
ordered by Id, matching AdditionalCopy cleanup. No new lifecycle engine,
retry layer, schema or migration change was introduced. The eight-case green
run is `TestResults/corrective-terra1-cleanup-green.trx`; clean Release is zero
warnings/errors. Tests use a held copy row or event-table barrier and SQL DMV
wait chains, drain both real operations, verify controlled stale-version
results, exact events/cleanup/audit counts and unchanged closed history.
Astra inspected the query change and all eight test paths. Fresh complete
candidate-c gates and Terra full Pass 2 remain required before acceptance.

A broader focused run passed 26/27 but exposed a Slice 4 HTTP test seeding an
administrator before bootstrap when selected first on a fresh database.
Moving that test's existing `CreateClient` ahead of seeding makes its standalone
run pass (1/1); no product behavior or assertion was changed. The original
failed run is retained in `corrective-terra1-lifecycle-regressions.trx`.
The lifecycle-only 26-case rerun also passed with zero skips. Final candidate
gates include this fixture correction as well as the product fix.

The final Node rerun exposed an intermittent setup race in the new focus
fixture (171/172 files passed). Pinned Grid.js diagnostics showed that the
test began its held unclaim render before earlier filter/claim pipelines had
finished. The fixture now waits for rendered membership, current claim label
and replacement row identity, and asserts the held grid/data belong to the
unclaim. No product focus code or final focus/error assertion changed.
All nine scenarios passed, then 12 complete repetitions passed (108/108),
followed by all 172 Node files in `corrective-terra1-final-node2.log`.
The failed `corrective-terra1-final-node.log` and diagnostic receipts remain.

### Full Pass 2 Candidate Ready

Fresh candidate c passed clean Release, 234/234 .NET/real-SQL cases with zero
skips, all 172 Node files, four fresh native fixture/oracle runs and all 12
published browser modes (103 states and 14 CSP cases). Astra checked the new
fixture synchronization, SQL correction, all 659 non-document source inputs,
590 candidate files, four native receipts and 164 browser evidence hashes.
Representative final screenshots were inspected. Same-reviewer full Pass 2
is now required; candidate b evidence remains historical, not substituted.

### Pass 2 - Clean

The same Terra High completed full Pass 2 against the entire Slice 4 delta
from `4769a8a8750c315e319824355d4508073bd43546` and surrounding callers.
No substantive findings remain. P1 is closed: both lifecycle cleanup branches
retain ordered request locks, and the eight real-SQL cases prove both orders
and exact atomic outcomes. The two fixture corrections preserve meaningful
coverage and all original assertions. No third pass is required under the
slice review contract after a clean full Pass 2.

Terra independently matched all 51 source receipt hashes, all 164 browser
evidence hashes across 12 runs, the 234/234 zero-failure/skip TRX, zero-warning/
error Release log, and candidate/artifact/fixture/browser receipt hashes.
It did not rerun suites/providers or modify any source, output or document.
Its full reread covered locked authority, lifecycle/invariant ordering,
identity/recovery, readiness/outbox scope, aggregate versions/inheritance,
hidden templates, migration, frontend safety and focus-race behavior.

## Astra Acceptance And Final Gate

Astra independently verified the complete candidate-c evidence and reviewed
the confirmed fix and test-fixture changes. All requested findings are resolved
as in the disposition table; there is no remaining known substantive defect.
All 19 authoritative pack payload hashes match. A fresh fetch still shows
main exactly at the pin and local/remote/PR head at the starting checkpoint;
PR #264 is open/draft. No source change needs propagation.

The local Slice 4 acceptance gate is satisfied. Commit/push one coherent
milestone, then require actual remote CI success for that exact SHA. Record
the final SHA and run in the
[Slice 4 acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639)
without creating a replacement documentation SHA. An earlier checkpoint run
does not substitute for that gate. STOP after success; Slice 5 has not started.
No merge, tag, deployment or cutover follows. Protected-ticket/real-handler
browser coverage is not live Entra login; FileEmailSender is not real-provider
email certification, and the documented release/rehearsal blockers remain.
