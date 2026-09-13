# Slice 2 Evidence

Slice 2's local acceptance and full review gate are complete; the dated record
below retains interim failures and corrections. This is not release/rehearsal
clearance. Fresh Sol XHigh Raman implemented the slice. The predecessor is
`1e36761c771db70d0b669087d0a843b66cf5618b` with 120 passing Linux CI tests;
see Slice 1's separate review and evidence records.

Milestone `9f946aed4b091a82407ac929345819d0a0c87b10` passed actual
[remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34744506275)
on 2026-09-13. Release has zero warnings/errors; all 181 .NET/SQL/browser tests
passed with zero skips, followed by legacy/frontend and publication gates.
This closes the milestone's remote gate, not the outstanding release blockers.

## Independent Staff Fixture

Astra copied the stopped, already-known synthetic PocketBase `dated-data`
fixture to `.git/asap-real-pb-source/staff-data`, preserving the original.
Only isolated Record/Collection APIs ran, with an empty hooks directory and
no HTTP server/provider access. Migration
`209901010013_staff_claim_acceptance.js` adds four staff users, two libraries
and eight claim cases to the existing one-admin/twelve-request fixture.

Pinned migrations do not declare `purchase_reminder_default`, despite reads/
writes in staff profile and workflow code. The isolated fixture explicitly
adds that Boolean field to exercise populated true/false values. This is not
proof of the deployed source schema, and no repository legacy file changed.
All original required event dates and the frozen export time remain distinct.

The accepted, frozen native Slice 1 executable exported
`.git/asap-real-pb-source/published-staff-package`: five staff, twenty requests,
four original events and no source mail deliveries or AdditionalCopy data.
The independent ignored harness `.git/asap-staff-migration-acceptance/`
deploys the exact frozen DACPAC to a GUID-owned database and invokes that
native executable, without building or referencing changing app projects.

Run `e9bd512873a042319d2fe45bf48d8e4f` passed the SQL assertions: active staff
use explicit Entra mappings; readable UPN stays separate from operator-selected
primary contact; weekly overrides and all five mixed-value preferences survive;
inactive staff can retain null durable identity; active staff retain inactive
library relationships. Eligible same-library and system-super-admin open claims
remain, inactive/out-of-scope/unmapped open claims clear every effective field,
and closed inactive/unmapped attribution retains its display/time/type. The
outbox starts empty. Standalone reconciliation passes, and changing one purchase
preference without changing row counts is rejected. The owned CLI/database were
cleaned up. This establishes predecessor SQL behavior, not a completed Slice 2
or complete report-parity result.

## S2-A1 Recipient Report

Inspection of that report exposed a required Slice 2 migration gap. Actual
pinned source, not a documentation inference:

- `lib/mail.js:sendAssignmentNotification` selects weekly override, then primary
  email. For fixture staff 1, the old destination is `weekly-only@example.invalid`.
- `lib/staff/title_request_side_effects.js:sendPurchaseReminderIfRequested`
  selects only the weekly override for purchase/additional-copy reminders;
  blank override means no reminder. Its address rule differs from assignment
  when the override is blank.
- `lib/jobs/weekly_summary.js:optedInStaff` requires opted-in, nonblank weekly
  override and verified source account; the send loop uses only that override.
  It does not apply the target primary-email fallback. All five current source
  fixture accounts have `verified=0`, hence none passes the legacy weekly filter.

The current report instead calls staff 1's primary email its old ordinary
recipient, uses primary fallback as an old weekly recipient for blank-override
accounts, and supplies no old-versus-target weekly eligibility/newly-eligible
delta. For dormant staff 3 it reports a changed ordinary recipient from null,
although the old assignment/reminder destinations already used the same weekly
address selected as the target primary. Blank-override staff 4 also requires
distinguishing an unchanged assignment destination from a newly available
reminder destination.

Sol is assigned the explicit old-path versus intentional target-normalization
report correction and focused tests within Slice 2's existing migration scope.
Keep the correct SQL recipient/preference normalization and empty outbox. Do
not add a mail framework or reproduce old delivery policy in target business
behavior. The independent report-parity gate remains open; nominal prior review
clearance does not waive this newly observed discrepancy.

The ignored `recipient-oracle.cjs` executes the actual pinned assignment,
purchase/additional-copy reminder and weekly-recipient functions with a recording
transport and the exported synthetic records. Its result alongside run
`e9bd512873a042319d2fe45bf48d8e4f` confirms five report mismatches, while the
positive address controls pass. This is failing-before executable evidence for
S2-A1, not a failure of the SQL preference/claim assertions above.

## Expanded Claim And Rule Fixture

A separate stopped copy, `staff-expanded-v2-data`, applies isolated migration
`209901010014_staff_rules_acceptance.js` and exports
`published-staff-expanded-v2-package`. It retains the prior fixture and adds
seven source rules plus three requests. Two source staff are now verified,
including active staff in the inactive library; a placeholder-only contact
normalizes to a null target destination. The source migration initially rejected
an invalid fixture claim-type spelling; the corrected source uses the pinned
schema's `automatic_format_rule`. No invalid package was accepted as evidence.

Independent native run `4818d7f42e7249c68aead06e111ccb13` imports 83 records and
nominal standalone reconciliation passes. Seven rules retain their expected
active/inactive state: eligible same-library, dormant-library and super-admin
rules remain active; inactive/out-of-scope/unmapped assignees normalize inactive;
an already inactive rule remains inactive. Placeholder-only target contact is
null. Same-count preference drift is still rejected and the owned database is
removed. The strengthened behavioral assertions fail in fourteen places,
representing two further required corrections, not fourteen independent bugs.

### S2-A2 Claim History And Missing Source Identity

Cleared open claims 1, 2 and 6 have no legacy event/note preserving the original
attribution. Section 6.3 requires both restricted report and existing history
annotation, timestamped from the frozen export. Attribution-only open case 9
with a blank source claimant ID is silently discarded without report/history;
closed case 10 loses its original display/time/type and has no report entry.
The original PocketBase Record API validates both source shapes. Open case 9
must be reported/annotated as unmapped, while closed case 10 preserves historical
fields with null FK. Required annotations occur exactly once and deterministically.

### S2-A3 Historical Rule Is Not Current Claim Eligibility

Open case 8 belongs to an active, correctly bound, same-library staff user and
references a mapped but inactive historical rule. The importer clears all claim
fields because its rule matcher requires the old rule to be active. This adds a
condition not present in section 6.3's current claimant predicate. Porting-spec
sections 7.6 and 18 preserve stored eligible relationships and disallow a
retroactive sweep when rule configuration changes. Pinned
`lib/staff/settings_save.js:saveFormatClaimRulesInApp` changes/deletes rules
without sweeping existing requests. Preserve the eligible claim and historical
rule reference; migration must not execute a rule or substitute an assignee.

Sol has all three S2-A findings and their exact source, package and executable
evidence. None is waived by the predecessor's nominal reconciliation result.

Repeat run `c02a9ddfdd624e408f67bb55664954e3` uses another fresh GUID database
and produces byte-identical staff, claims, claim-events, rules, reconciliation
and failures JSON. The reconciliation report SHA-256 is
`bb389ad5a9af19ccd5faffd3dc90c5cd4ae475ba3a868b219f6e9beadb571620`.
This proves deterministic reproduction, not correct migration. The expanded
pinned-function recipient oracle selects staff 1 and dormant-library staff 3
as legacy weekly recipients; the target must apply its explicitly corrected
current participation policy and report that eligibility change separately.

Native refusal run `d63d3eb489504b41b6ede5fed1c07a41` first omits an active
staff mapping, receiving `active_staff_identity_missing`, then duplicates a
durable tuple, receiving `staff_identity_map_invalid`. Both leave zero staff,
request, legacy-mapping and outbox rows. The valid import then succeeds on that
same disposable target and repeats the fourteen known history/claim failures;
the subsequent deliberate same-count drift is rejected. No refusal or failed
assertion is reported as a passing overall migration gate.

## Interim Native Correction Acceptance

Sol's source-built immutable snapshot
`.artifacts/slice-02-migration-checkpoint-report-v3-a22b2e39c3e4` reports contract
`slice-02`, schema 3 and the unchanged PocketBase pin. Astra independently
verified the actual bytes against these hashes before running it:

- Migration DLL: `091e5d6c78a5dfa760850b5ffdaa6b7603d6e6400e8cda8a38644bab215a9c2f`.
- Native EXE: `ba2ae282c3b0c42b402b6ed9919d2cb3c49cb9254351f5f754b9e093c935c598`.
- DACPAC: `1cb4798f4aab79717b3007e45c6513300ce3832c24b50d439b578aef7d78db5d`.

The new native exporter reads the same stopped source databases into separate
`slice02-staff-package-a22b` and `slice02-staff-expanded-package-a22b` packages.
Old artifacts/packages and failing-before evidence remain untouched. No shared
application build occurs while Sol continues the remaining slice work.

Expanded native run `433bbd14494f49ff8296894dbdd326ec` passes all eleven claim
cases, seven rule cases, preferences/identity/contact/dormant-library controls,
empty outbox, missing/duplicate-binding refusal followed by a valid same-target
retry, standalone reconciliation and intentional same-count preference drift.
Cleared claims now receive exactly one local legacy event with original
attribution and frozen export time. Closed attribution without a source ID and
the valid claim with an inactive historical rule survive correctly.

The pinned-function recipient oracle also passes all five per-user reports:
actual old assignment/purchase/additional-copy/weekly destinations, final SQL
target recipients, each kind's change flag, current weekly eligibility, newly
eligible fallback and null-target results. This includes the source placeholder
ordinary destination becoming null and the verified dormant-library source
recipient losing target weekly participation. Source normalization is distinct
from intentional target address validation. Sol additionally reports passing
focused bootstrap-promotion tests ensuring deltas use final imported staff
state rather than pre-bootstrap values; these are not independently exercised
by this particular five-user fixture.

Original native run `0268522df163444da9049d1007ec353b` and its recipient oracle
pass as well. Expanded repeat `44a4e227c1f54730b9b526edcaa1b9f6` passes on a new
SQL target with byte-identical staff, claims, history, rules, report and failures
JSON. All owned CLI/database resources are cleaned up.

S2-A1/A2/A3 are resolved on this interim snapshot. Full Slice 2 tests, final
source-built artifacts, independent Terra passes and the milestone gate remain
pending; these native results are not release or whole-slice completion.

## S2-A4 Unsupported Hold Correlation

Parent acceptance inspection found that the in-progress ambiguity observer
selected the sole new active same-patron/BIB hold absent from a pre-dispatch
list and called it `correlated_new_hold_observation`. The completed-operation
null-ID enrichment path likewise selected the sole active same-BIB hold.
`PolarisHoldSnapshot` contains no operation GUID or other proven causal mapping.
An unrelated patron/librarian hold created during the uncertain call can satisfy
both tests. A list difference or single candidate is not authoritative proof
that this exact operation succeeded or owns that final ID.

Porting-spec sections 9.1/9.2 prohibit this inference. Preserve the ambiguous
operation/barrier and operator-required path without supported correlated
evidence; retain null identity for an otherwise proven successful operation.
Pre-dispatch existing-hold adoption remains the explicitly permitted separate
case. Sol is assigned the correction and adversarial unrelated-hold controls;
passing tests that asserted the inferred correlation must be corrected as well.
This finding is open and is not waived by migration acceptance or compilation.

On 2026-09-13 Sol reports red/green adversarial tests for both prohibited
inferences, a clean Release build and seven passing focused hold tests. The
unsupported observation paths now retain the barrier or completed null-ID
diagnostic as appropriate; legitimate pre-dispatch adoption is unchanged.
Final independent runtime verification remains pending. Astra requested a
fresh bounded advisor consultation on the remaining section 9.2/F2 safe
identity-only enrichment requirement when no selected-provider correlated
lookup has been proved. That consultation does not reopen the no-inference
rule or pause the remaining browser/auth/lifecycle work.

Fresh Astra Max Dirac (`01a098f9-1c8a-7ca3-b859-3ec26e87bc75`) completed the
read-only consultation and was closed. Decision: retain one internal identity-only
write in the existing service plus F2 real-SQL fencing tests using explicitly
synthetic operation-specific authoritative evidence. The current adapter must
still report correlation unavailable; no guessed GUID lookup, provider framework
or public synthetic-evidence endpoint is introduced. Enrichment checks active
Organization, request/operation versions, latest succeeded association, patron/BIB
and current-null ID, changing only identity/evidence/diagnostic fields. It never
re-completes placement or reuses an expired executor lease. Actual provider
correlation capability remains unproved; the selected SDK's hold-list API/models
do not establish GUID-to-final-ID mapping. Synthetic tests prove local fencing,
not that external capability. Sol has this decision and the additional reminder
that a numeric operator evidence reference alone is not operation-specific proof.

## S2-A5 Staff Frontend Acceptance

Astra inspected the actual desktop detail and mobile blocked-recovery PNGs from
`.artifacts/browser/staff-be934d15bbf34270814da23687b1b237`. Its six-state report
has no serious/critical axe findings and no document-width overflow, but visual
and source inspection still exposes material frontend gaps:

- `buildEditForm` replaces configured material-format and publication timing
  selectors with plain text, including an internal `Format code` input. It does
  not offer editing of the configured request custom fields, passing original
  JSON back unchanged. Pinned staff HTML has `edit-format`, `edit-publication`
  and `edit-custom-fields`; preserve effective library options and historical
  edit values using the already-owned configuration model, not a new admin UI.
- The incomplete-operation state still offers enabled Purchase, Already own,
  Reject and Close silently commands and an editable AutoHold checkbox. Use the
  backend `canChangeWorkflowState`/operation capabilities for guarded actions
  and fields while retaining the edits/claim operations genuinely permitted.
- Mobile Grid.js claimant/date cell text overlaps adjacent columns behind the
  modal. A passing document scrollWidth check does not prove readable cells.
  Preserve readable column sizing with bounded table scrolling or the source's
  equivalent responsive behavior, and inspect/test the unobscured queue state.

Sol has the actual image paths, source anchors and required focused regressions.
This finding remains open; zero axe findings alone do not close the frontend
gate. No parent edits to application/frontend/test files were made.

Sol's corrected seven-state browser run is
`.artifacts/browser/staff-c08da3c7866344c89790b81c6b36752d`. Astra independently
read its report and inspected the desktop editor, mobile blocked-operation
dialog and unobscured mobile queue PNGs. Configured/historical selectors and
custom fields are present; guarded commands are visibly disabled while Claim
remains usable; the mobile queue has readable stable columns within an internal
horizontal scroller, without neighboring text overlap. The focused hosted
browser test passes its final SQL assertions, axe, keyboard/focus and layout
checks. S2-A5 is resolved on this interim frontend; final frozen full-slice
verification and independent Terra passes still apply.

## Pass 1 Fix Artifact

The corrected source-built publication is
`.artifacts/slice-02-final-c3782c487ad1`; the `62a723e7e179` and
`d7f4c9a182be` Web publications are retained superseded evidence. Astra verified
all 579 payload lengths/hashes and the exact inventory from disk. The inventory
SHA-256 is `58f6b58f0ddc5e6d2451a12d50ebe22563d4663a15f2d3fd50d8013c5b7258e7`;
verification record is
`58a8f8143681d9adb250d03dae4f498e4bd54c9846ecb5cbf5e3d0fbeab5e1db`;
Web DLL is `c6b8b9b32ee5da9f99dbede62ecd2bda6cefbeeac6f535964525285c556c4d70`.

Current full code receipt `.git/asap-slice-02-review-state-pass2.json` contains
48 files, SHA-256
`ed0e43ead2d93a3aa71c7cbfe9b56fca8ab14c175211c9f4ccc9654715cb352f`.
Sol reports the final post-extension 181/181 suite with zero skips; Astra's
independent final suite/native reruns and same-reviewer full Pass 2 remain
open. Direct generated-file hashes are authoritative; a malformed hash string
in Sol's final prose was not used as verification evidence.

Astra independently passed the corrected exact-floor full suite: 181 total,
181 passed, zero failures/skips, 2m31s. All 43 current frontend source files
match the frozen Web output, and the actual native `describe-contract` confirms
the expected Slice 2/schema 3/PocketBase pin. Final native SQL acceptance and
the retained reviewer's full Pass 2 are now underway.

Final native expanded run `60b225cb742b43af92785d3b7ae4d389` and original run
`667464a918b44d8ab3011bb3252f22db` passed all clean-target refusals, staff
identity/preference/contact and claim/history/rule assertions, empty-outbox
checks, reconciliation and deliberate preference-drift rejection. Both
five-user pinned-source recipient oracles passed. Expanded staff, claims,
claim-events, rules, reconciliation and failures JSON remain byte-identical
to the earlier equivalent fresh-target run `762148216d8c45ab836978cf94049c9d`.
All owned SQL/CLI resources were cleaned up.

Frozen Web `wwwroot` contains 43 current source files plus 72 SDK-generated
Brotli/gzip variants. Astra decompressed every variant and verified its hash
against the exact current source payload. An initial raw count equality check
omitted those standard generated variants; this was an acceptance-command
assumption, not stale application content or a product defect.

The independent published-Web patron regression
`2afebd338df74d6787170c692aa8477f` passed desktop/mobile login, focus,
submission, duplicate protection, logout, real SQL state and domain suppression
without file-email output. Its isolated process, database and certificate were
cleaned up. Current 48-file code receipt and all 19 authoritative pack payloads
remain unchanged while Terra performs full Pass 2.

Terra completed full Pass 2 with no actionable finding, approving the slice
and closing S2-T1/S2-T2 after Sol's fixes. Astra fetched `main` again; it still
matches the PocketBase pin. The local milestone gate is satisfied. Actual
remote CI for the forthcoming Slice 2 commit remains required before Slice 3.

Reviewed milestone `9f946aed4b091a82407ac929345819d0a0c87b10` was committed
and pushed to `codex/csharp-port` on the same draft PR 264. It contains exactly
the reviewed 48-file implementation/CI set plus nine current evidence/docs
files. Staged whitespace checks passed. Future-slice packets/source notes and
all ignored fixtures/artifacts remain outside the commit. Its actual remote
CI result was pending at commit time; the passing exact-commit run is recorded
at the top of this document.

## Final Freeze And Parent Verification

Sol completed the local slice and paused application/schema/frontend/test edits.
The source-built artifact `.artifacts/slice-02-final-d7f4c9a182be` contains 326
Web and 253 native migration payload files. Astra checked every size/hash and
the exact inventory; `sha256-files.json` SHA-256 is
`df0f749a7067ad1721511dc1a4ac74b693d3c5b3d6c22c150dedcb392f286e3f`.
The 46-file code freeze receipt is
`.git/asap-slice-02-review-state-pass1.json`, SHA-256
`9f7890b6ac18faa6e23f9b05e23bf80af30dd37aa943d80eaed0ee841061bec6`.

Astra independently ran the complete Release suite with minimum count 174:
174 passed, zero failed/skipped, 2m38s. This includes real SQL, hosted staff and
patron browser journeys, and the corrected hold-correlation/F2 regressions.
These tests do not claim live Entra/Polaris or real Postmark validation.

Final native expanded fixture run `762148216d8c45ab836978cf94049c9d` passed
missing/duplicate active binding refusals without partial rows, then valid
import, all identity/preference/contact/claim/history/rule assertions, empty
outbox, standalone reconciliation and detection of intentional preference
drift. Its five-user executable pinned-source recipient oracle passed with no
delta mismatch. The native tool and schema are the same bytes accepted at the
interim checkpoint, now executed from the final packaged output. Owned SQL/CLI
resources were cleaned up. Full Terra review remains required before commit.

Final original fixture run `94b9cf3786764f899b9b7141cb3caf52` and its five-user
recipient oracle also passed all checks. Published Web patron regression
`9f9ce7af924545aeada9837df450e146` passed both desktop/mobile journeys,
accessibility/layout checks and real SQL duplicate/submission/domain-suppression
assertions, without file-email output. Its isolated app process, SQL database
and certificate were cleaned up.

Fresh Terra High Ohm (`01a0994e-4e33-7173-8916-dcb64ccab32d`) received the
full-slice Pass 1 packet. Sol remains paused for confirmed review fixes. S2-A4
is locally verified by the independent full suite; unsupported live-provider
correlation remains unavailable exactly as documented, not simulated. The same
reviewer must independently verify it and the complete surrounding slice.

The final expanded import's staff, claims, claim-events, rules, reconciliation
and failures JSON are byte-identical to the earlier equivalent fresh-target
run `44a4e227c1f54730b9b526edcaa1b9f6`. Final reconciliation SHA-256 remains
`8ab5655346b2093dd99ef99da516e3f1eb14d31a0bb156de01b2e30fc13ae795`.
A new fetch confirms `origin/main` still equals the exact PocketBase pin; all
19 pack payload hashes and all 46 frozen code files still match their receipts.

Astra also inspected the actual desktop editor and mobile queue PNGs from the
independent 174-test run, `staff-c4abe62db9bb4ab3ab37c6b70105237e`, and read its
seven-state report. Scoped/historical selectors, custom fields and readable
internally scrolling mobile columns retain the S2-A5 correction; report has no
serious/critical axe findings or document overflow. Later review fixes require
fresh tests/artifacts rather than treating this earlier freeze as current.
