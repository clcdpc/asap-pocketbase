# Slice 3 Implementation And Acceptance Evidence

Slice 2's milestone `9f946ae` and exact-commit Linux CI `34744506275` passed;
fresh Sol XHigh is implementing the slice. The fixture below was prepared
independently before dispatch. Interim migration checks now pass; the complete
slice, final publication, Terra review and milestone are not yet accepted.

Fresh Sol XHigh Lorentz (`01a0999f-6fe2-76a2-8fb1-4e1be19408fa`) owns the
complete implementation. Documentation checkpoint `3e37a48` commits the
previously prepared packets/source notes and Slice 2 CI closure; its actual
Linux CI `34746461393` also passed. It changes no application code and is not
a Slice 3 milestone. The code predecessor remains the accepted Slice 2 code.

## Isolated Source Fixture

Astra copied the stopped `staff-expanded-v2-data` fixture to a new ignored
`.git/asap-real-pb-source/additional-copy-data`, retaining every previous source
and evidence directory. Only Record/Collection API migration
`additional-copy-migrations/209901010015_additional_copy_acceptance.js` ran,
with empty hooks and no HTTP server or external calls. Actual pinned
`lib/additional_copies.js` and the three additional-copy schema migrations were
inspected first; no repository PocketBase implementation was edited.

Twelve `copycase0000000` through `copycase0000011` tasks have independent
bibliographic/library snapshots, original Notes, creator/closer snapshots and
distinct frozen creation/update/claim timestamps. Cases intentionally include
task snapshots different from the linked source; migration must not refresh
them from TitleRequest or current Organization data.

| Case | Claim / State | Required Import Result |
| --- | --- | --- |
| 0 | Active same-library staff, open | Preserve original effective attribution |
| 1 | Inactive mapped staff, open | Clear and retain history; claimant_inactive |
| 2 | Active foreign-library staff, open | Clear and retain history; claimant_out_of_scope |
| 3 | Active staff in its inactive library, open | Preserve original effective attribution |
| 4 | Active system super-admin, other library open | Preserve original effective attribution |
| 5 | Inactive mapped staff, closed | Preserve mapped historical claimant |
| 6 | Unmapped claimant, open | Clear and retain history; claimant_unmapped |
| 7 | Unmapped claimant, closed | Null FK, retain display/time and report |
| 8 | Attribution with blank claimant ID, open | Clear and retain history; claimant_unmapped |
| 9 | Attribution with blank claimant ID, closed | Null FK, retain display/time and report |
| 10 | No claim, closed, absent source | Preserve task; no claim conversion; null source |
| 11 | Foreign-library mapped staff, closed | Preserve mapped historical claimant |

Every open conversion appends exactly one deterministic annotation after the
existing Notes and retains all original attribution in the restricted report.
Use exported time `2030-01-04T00:00:00Z`, not import wall-clock time. Source
creator/closer display snapshots remain unchanged even when the current staff
name/activity differs. Task-source independence and source deletion also need
real-SQL runtime acceptance, not only migration counts.

The frozen Slice 2 native executable exported all twelve tasks successfully to
`slice02-additional-copy-preparation-package`. That package is a preparatory
`slice-02` contract, not a Slice 3 import result. Re-export the stopped database
with Slice 3's actual new native executable before final import acceptance.

Current-slice negative boundary check
`58e436f000a84635a46565ab20c2ebb0` ran the frozen native executable against a
new DACPAC-deployed SQL target using an already-compiled isolated harness. It
returned `source_domain_not_supported` for the twelve task rows, with zero
TitleRequest/StaffUser/outbox rows and complete owned-database cleanup. This is
an expected current-slice refusal, not a Slice 3 migration implementation test.

## Independent Acceptance Harness

Astra extended its ignored, app-reference-free staff migration harness with a
`copies` mode. It retains expanded staff/rule/title-request assertions and adds
all twelve task mappings, independent bibliography/library snapshots, original
creator/closer/time/Notes, effective claim/history/conversion reasons, current
eligibility SQL and same-count task snapshot drift detection. This harness
builds independently with zero warnings/errors. The interim executions below
now pass; final source-built native export/import remains the acceptance boundary.

A second stopped copy, `additional-copy-missing-updated-data`, applies only
Record API migration `209901010016_additional_copy_missing_updated.js` to clear
case 10's `updated` field. Read-back verifies it remains absent. Original data
and evidence are retained; no HTTP server/provider or raw legacy SQL ran. The
`copies-missing-updated` acceptance mode requires that task's target UpdatedUtc
to use original CreatedUtc, as specified for AdditionalCopy timeout age. Export
this separate source with the new native artifact without rewriting a manifest.

The accepted Slice 2 native exporter separately produced
`slice02-additional-copy-missing-updated-preparation-package`; direct JSON
inspection confirms case 10 retains its original creation/closure times and
an empty `updated` value. This proves the source fixture, not target fallback.

Harness baseline regression `661b31ca6b2e4a64be9674367d176bb5` passed against
the unchanged accepted Slice 2 native artifact and expanded staff package:
both identity-map refusals were clean, valid import/reconciliation succeeded,
all existing staff/title-claim/rule assertions passed, same-count preference
drift was rejected, and the owned database was removed. The added task checks
were not exercised in that baseline regression. See the separate interim Slice 3
executions below; no final task acceptance is claimed.

## Historical Closure Boundary

Pinned `lib/jobs/timeouts.js` calls `additionalCopies.closeTask(app, record)`
without staff. Pinned `lib/additional_copies.js` therefore records a valid
closed time with empty closing staff/display attribution. The canonical nullable
closer fields must preserve this history without inventing an actor. Astra
flagged the initial schema's non-null closed-display constraint for Sol during
implementation, along with inherited TitleRequest claim-type preservation and
the actual source task's 128-character publication/BIB storage boundaries.

Separate stopped `additional-copy-history-boundary-data` applies only migration
`209901010018_additional_copy_history_boundary.js`: case 10 has no closer FK or
display and source-valid 128-character publication/BIB values. Record API
read-back confirms those exact values. Original twelve-task data is unchanged.
Harness mode `copies-history-boundary` preserves all twelve cases and additionally
requires those historical values. Its interim native/SQL execution now passes
as recorded below; final artifact acceptance remains pending.

The parent published-Web harness also has an opt-in, isolated two-request/staff
seed for desktop/mobile AdditionalCopy acceptance. It uses only synthetic SQL
rows after the normal ready/bootstrap gate and keeps the usual process/database/
certificate cleanup. The fixture helper builds without app references; the
actual browser journey will run only against a frozen Slice 3 publication.

## Interim Native Checkpoint

Sol source-built and froze
`.artifacts/slice-03-native-prefrontend-3e37a48-20260913a` and copied its matching
DACPAC into that directory. Astra independently verified SHA-256 values:

| Payload | SHA-256 |
| --- | --- |
| Asap.Migration.exe | 676771b04e3f42e57b3c49f628798ddbdcc918ebc71300c80a2e80fcba6f0cea |
| Asap.Migration.dll | d0341ffc6bc89f50af0aba2f77485a06fffa7567ef07045c5175fd4e50e69c9b |
| Asap.Database.dacpac | 93da6896a1892aecf56c1e0cafa8acb18712394037ae91fe918fa5e4f5e8d502 |

This is schema 4 / package slice-03 / report v4. It predates the subsequent
runtime legacy-plus-mapped-rule constraint correction and reduced-deletion-audit
BIB widening. It is immutable interim migration evidence, not the final slice
publication; those later changes require a new source-built artifact and rerun.

Astra used that native executable to re-export all three stopped source copies
into new `slice03-parent-copies*-20300104-midnight-a` directories, with the
packet's frozen export time `2030-01-04T00:00:00Z`. No manifest was rewritten.
Each package contains 97 source records across 21 files; the import/reconciliation
reports 103 records including target normalization output.

| Fixture | Manifest SHA-256 | Passing SQL Run |
| --- | --- | --- |
| Original twelve tasks | d4303a13a047f59956eceb3c5eb0fb5f699212838e09f47161991fa741500317 | 9b8b59f43e0e4148bf7c9b0d92c5af71 |
| Missing updated | 3e8a3415c5c3830dbd1aad0fc63057ab07d842344566b9143a2a507fe7d56f92 | 8dbe40de9b564ed49b5422741de9e5ed |
| Historical closure boundary | 29cac215a69dcb7275764bdf414a50933f87c7d25bc2cd31c6451d99086bf3d2 | 5356c7cd606d4246925a5f198e42464b |

The independently compiled, application-reference-free harness deployed the
frozen DACPAC to fresh owned SQL databases and invoked the actual native CLI.
All three runs passed strict missing/duplicate Entra binding refusals without
business rows; all existing staff preferences/contact/scope/rule/title-history
assertions; all twelve independent task snapshots, claim conversion reasons,
retained Notes/attribution and seven reconciliation groups; zero invalid effective
open claims; and an empty outbox. Missing UpdatedUtc uses original CreatedUtc;
nullable closer and 128-character BIB/publication history survive intact.

Same-count task-title drift failed reconciliation, restoring it passed, and
same-count staff-preference drift failed as expected. A transaction changed the
source snapshots and deleted it: all twelve tasks survived with null source FKs
and otherwise identical task snapshots/history before rollback. All owned
databases were removed. Restricted evidence remains ignored under
`.git/asap-staff-migration-acceptance/runs/<run-id>`.

Initial run `d0c2ca5290d64dcf8307e549c7c412ce` retained all passing task checks
but failed four preexisting title-event time assertions: Sol's export used
`05:06:07Z`, while the harness/packet expected midnight. The fresh exports above
correct the test setup; this was not an application defect or a weakened check.

The parent browser script is now prepared against actual frontend selectors and
DTOs. It checks desktop/mobile keyboard preview/cancel/create, manual and legacy
historical-rule claim inheritance, valid reopen, closed history during claimant
deactivation, invalid-claim clearing once on reopen, repeated/no-op and stale
versions, Mine/unclaimed, screenshots/axe, and direct persisted SQL/audit state.
It has not yet run against a final frozen Web publication.

## Reduced Deletion Audit Check

Astra inspected exact pinned `lib/records/suggestions.js` audit creation and
both deletion callers, plus the PocketBase audit collection migration. A fourth
stopped source copy, `additional-copy-audit-data`, adds two audit rows only via
Record API migration `209901010019_additional_copy_audit_acceptance.js`. Their
source shapes cover a deleted additional-copy task and deleted TitleRequest,
original library/bibliography/creator/deletion snapshots, a source-valid
64-character audit BIB, full title-request patron details and freeform Notes.
No legacy data-access SQL, HTTP server or provider call was used.

The interim native exporter created
`slice03-parent-copies-audit-20300104-midnight-a` with 99 source records and
manifest SHA-256
`f2486f4db767733ecd5d1f3207d5877ea0aa87e09c0a94c8f98d614389994362`.
Independent SQL run `c5f9121e4edc48e196a49139a5fddfbe` passed all original
twelve-task/expanded-staff checks and imported/reconciled 105 records. It also
verified both reduced audit request types, original request keys, library,
bibliography, original creation/deletion times and deleting-staff attribution;
the title barcode is only `***9012`, and tasks have no barcode. Direct SQL
inspection of every persisted audit column contains no full barcode, patron
names/email/code, or Notes sentinels from the source snapshots. A same-count
audit author change failed reconciliation; restoration passed. The owned target
database was removed. This fourth fixture must also run against the final native
publication; the interim pass is not a final slice/release gate.

Equivalent fresh-target rerun `f4365b17e85044d383dfab2d6145d363` passed the same
fourth-fixture checks. Astra compared all thirteen JSON evidence files by SHA-256:
every file was byte-identical, including the restricted reconciliation report
(`81a149fe2436495ca0d58e1621e8016fd7b90b92a6447840e6a91557292ab2dd`),
task snapshots/conversion counts, source-independence result, reduced audit,
staff/claims/events/rules and empty failures. Both disposable databases were
cleaned up; no SQL or filesystem output was reused as application state.

A fresh `origin/main` fetch during these checks still resolves to the pinned
`150b30b776565194260cc327eeeffdfb46475e81`. PR #264 remains open/draft on
`codex/csharp-port`, head documentation checkpoint `3e37a48`. The actual deployed
PocketBase commit remains unverified; no final PocketBase tag was created.

## Interim Web And Finding S3-A1

Sol's source-built Web checkpoint
`.artifacts/slice-03-web-prebrowser-3e37a48-20260913a` includes the later schema
corrections. Astra independently verified Web DLL SHA-256
`c31cf83b6e59c87458306bc5f910d2c54ae0f88cf7b84ef67d45b870d8283df3`
and embedded DACPAC
`749c875fc30a30cffabe75f8bdc710701ac6aca468ed54813d58ae2a4cf8e6f1`.
The complete slice is still in implementation/testing; this is not a Terra
review freeze or accepted publication.

Independent published-browser run `d8850c97a1d14ebc9784526b7c06dd50` failed
claim timestamp preservation. Reproduction `570b19237f864353ad849d958ffbcb1d`
captured the exact response in `desktop-copy-creation.json`: SQL
`ClaimedAtUtc = 2026-09-01T12:13:14` was serialized as zone-less
`"claimedAt":"2026-09-01T12:13:14"`, not an explicit UTC instant. Created and
Updated timestamps likewise omitted the UTC designator. Eastern-time JavaScript
therefore parsed the retained claim as `16:13:14Z`, four hours after the stored
instant. Pinned PocketBase emits ISO UTC values; the new AdditionalCopy DTO and
surrounding source-request timestamp projections must preserve that meaning.

Astra sent **S3-A1** to Sol with the exact response, publication and reproduction.
The correction must mark known SQL UTC ticks as UTC, not reinterpret/convert
them from the server's local time, and must not change DateOnly publication
semantics. Focused SQL-backed JSON and non-UTC browser regressions are required.
This is a concrete acceptance defect, not an architecture change or release-only
deferral.

Diagnostic run `74ea68ee29e54bb38f9c779d053b78bf` retained the UTC assertion but
collected its failures until the end so the remaining browser workflow could be
checked. Both viewports completed preview/cancel keyboard focus, creation,
manual and legacy-plus-historical-rule inheritance, valid reopen, closed-claim
history during claimant deactivation, clear-on-reopen without substitution,
exactly-once Notes, already-open no-op, stale close rejection and Mine/unclaimed
refresh. Eight scans had zero serious/critical axe violations, document-width
overflow or missing visible images. Astra inspected desktop and mobile preview
and reopened-task screenshots. The run still **failed**, and post-browser SQL
assertions did not execute; no browser acceptance is claimed. All three owned
databases, application processes and temporary certificates were cleaned up.

The parent harness now also parses the claim timestamp inside an explicit
America/New_York browser context and captures the scrolled retained Notes.
It awaits the next immutable source-built Web publication containing S3-A1's fix.

## S3-A1 Fix Verification

Sol corrected the affected AdditionalCopy and adjacent TitleRequest/hold-summary
DTO projections with explicit UTC kind on unchanged SQL ticks. Retained-claim
Notes use explicit UTC formatting; DateOnly fields are unchanged. The focused
real-SQL `StaffWorkflowTimestampsSerializeSqlUtcTicksWithZuluOffsets` test passes
and checks both exact serialized values and unchanged SQL clock ticks.

The superseding source-built checkpoint is
`.artifacts/slice-03-web-utcfix-3e37a48-20260913b`. Astra verified Web DLL
SHA-256 `800b7c3a83a636414456bd31cbe947c391d27d1934559fe4d6b340712da39c37`
and unchanged embedded DACPAC
`749c875fc30a30cffabe75f8bdc710701ac6aca468ed54813d58ae2a4cf8e6f1`.
All 17 pinned vendor payloads match and the 326-file publication contains no
forbidden PocketBase/Node/dev-email payload. This remains an interim checkpoint,
not the final full-test/Terra freeze.

Independent browser/SQL run `7e3ada1700ae4c64b123c3181fb8b07e` **passed** the
entire journey at 1280x900 and 390x844. Both Node and the actual
America/New_York browser parse the retained claim as its original UTC instant;
the timestamp finding list is empty. Ten screenshots/scans, including scrolled
Notes, pass serious/critical axe, document-width layout and visible-image checks.
Astra inspected the desktop and mobile Notes screenshots: history is readable,
retains original attribution/time, and is separate from the now-unclaimed state.

Direct post-browser SQL assertions also pass: two open tasks with all effective
claim and closure fields null; original independent library/bibliographic/creation
snapshots and exactly-once Notes preserved; both original claimants inactive;
two administrative deactivation audits each showing one TitleRequest cleanup
and zero closed AdditionalCopy cleanups; no outbox rows or file-email output.
Owned SQL database, app process and certificate cleanup completed.

Browser result SHA-256:
`d38ab407af312e365f36ba69cf7b0c4b16c884ff1fa64339bafec31da0240ce7`.
Direct SQL-state SHA-256:
`97c0d81b022d4b9c4dc51b60c892f2b869e791643b6655a85e3c7b3ffb98f2dd`.
S3-A1 is fixed and verified at this checkpoint; final publication and full
independent Terra passes are still required before the Slice 3 milestone.

The unchanged patron published-browser/SQL regression also passed against this
UTC-fix checkpoint in run `14d04399298a45b680bc2d72f372915d`: both viewports
cover login/error/focus, session restore, form/submission, duplicate prevention,
logout and server session revocation, with two SQL submissions and two terminal
recipient-domain suppressions and no file-email output. Cleanup completed.
Astra additionally verified all 43 frontend source files against this Web
payload and decompressed all 72 Brotli/gzip variants to the exact payload bytes.

The user's latest direction is to stop after Slice 3 is complete and leave a
clean working tree. Full mandatory runtime coverage, complete tests, fresh Terra
Pass 1 plus the same reviewer's full re-review, final artifacts and milestone/CI
remain in scope. Slice 4 will not be dispatched during this execution.

## Final Candidate Acceptance Before Terra

Sol completed the full slice and paused for independent review. The immutable,
source-built candidate is `.artifacts/slice-03-final-3e37a48-20260913a`.
The review packet records the Web/native/DACPAC hashes and independently hashed
666-file artifact inventory plus 25-file code receipt. All 43 frontend sources,
72 decompressed variants and 17 vendor hashes match; no forbidden payload exists.
The native contract reports schema 4, slice-03 and the pinned PocketBase SHA.

Sol passed a clean Release build (zero warnings/errors), all Node test files,
two full 189-test runs, and the expanded 18-state desktop/mobile staff browser
integration. The complete R3 coverage includes both serialization orders for
deactivation, library move and cross-library super-admin demotion; valid retained
claims and claimless reopen; changed candidates, stale/no-op requests and rollback.

Astra independently reran the exact CI command against the final build:

```powershell
$env:ASAP_TEST_SQL_CONNECTION_STRING='Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --minimum-expected-tests 189
```

Result: **189 passed, zero failed, zero skipped**, 3m18.717s.

All four independent app-reference-free native CLI probes passed using the final
EXE, final DACPAC and newly exported stopped-source packages:

| Fixture | Acceptance run |
| --- | --- |
| Original 12-task claim matrix | `0e5caf5a3c1b49629abc0104575d9d27` |
| Missing-updated creation fallback | `6102fc50b9df42ddaa7a0b79c84908fb` |
| Historical closer and field boundaries | `eb6726ecf4ac43a2a0cf0819f8625dac` |
| Reduced mixed deletion audit | `b84b4cdc74c24f878a6cb0f6c37d83ba` |

Each probe verifies clean strict-identity refusals, actual SQL import values,
preserved independent task snapshots and closed history, operational-claim
conversion reasons/counts, source edit/delete independence and nullable links,
empty outbox, reconciliation success and same-count semantic drift failure.
The audit probe additionally checks reduced patron data, request-type-specific
keys, attribution/times and audit drift detection. All owned databases were removed.

Final published browser/SQL run `e388465510e24905845e64084014c856` passed at
1280x900 and 390x844 with America/New_York browser time. Ten screenshots/scans
cover preview keyboard/cancel focus, manual and legacy historical-rule claim
inheritance, source/task links, valid reopen, closed-history preservation during
claimant deactivation, clear-on-reopen without substitution, exactly-once Notes,
stale/no-op handling and Mine/unclaimed refresh. Both browser and Node preserve
the exact UTC instant. Serious/critical axe, document overflow and missing-image
counts are zero. Direct SQL confirms two unclaimed open tasks with all original
snapshots/Notes, inactive claimants, correct separate lifecycle cleanup counts,
zero outbox rows and no generated email files. Owned SQL/app/certificate cleanup
completed. The final frontend includes the verified sign-out formatter and
replacement-row focus fixes, so this rerun supersedes the earlier publication.

These are slice acceptance results, not independent review or production
readiness. Fresh Terra Pass 1 and the same reviewer's full subsequent pass remain
mandatory before the coherent Slice 3 milestone.

Astra inspected the final desktop/mobile retained-Notes screenshots and confirmed
readable wrapping, preserved original attribution/UTC time and separation from
the current unclaimed state. Final browser-result SHA-256 is
`76f85a3c20bb6462f2978f561fbf7707a6f830193ecd14e5e7e9053c1f341797`;
final SQL-state SHA-256 is
`488e774f6907687a0f343cbaf76ce4294b204d72f0ce345ef11cfc57ab2cc768`.

The unchanged patron journey also passes against the final Web publication,
run `a75184e164de4b2c92a23db37d40f3b9`: desktop/mobile login/error/focus,
session restoration, submission, duplicate protection, logout/revocation and
direct SQL/domain-suppression checks. No email files were generated; owned SQL,
app and certificate cleanup completed.

All 13 final native audit JSON outputs are byte-identical to the equivalent
earlier fresh target `c5f9121e4edc48e196a49139a5fddfbe`, including reconciliation
SHA-256 `81a149fe2436495ca0d58e1621e8016fd7b90b92a6447840e6a91557292ab2dd`.
All four final native failure arrays are empty. The frozen 666-file artifact
inventory and 25-file code receipt still match after these acceptance runs.
Fresh fetch confirms `origin/main` remains the exact pinned source; all 19
authoritative pack payload hashes remain unchanged. PR 264 is open and draft.

Fresh Terra High Kuhn (`01a09a1d-5c47-7293-9f91-4e7360a88d61`) now owns the
independent full-slice review cycle; Sol XHigh Lorentz remains paused and
available for confirmed fixes. No Slice 4 implementation has been dispatched.

## S3-T1 Published Reproduction

Terra Pass 1 found that the ordinary-staff assignment picker calls the restricted
Staff Access list and cannot expose valid system super-admin candidates. Astra
confirmed the route mismatch and pinned ordinary-staff transfer contract, then
sent S3-T1 to Sol. Staff Access must remain restricted; this needs a minimal
workflow-specific eligible-candidate boundary, not broader management access.

Independent published-build run `db46b5f6d13642e7a6f3b2e3a2a022e5` reproduces
the failure against the frozen Pass 1 Web artifact. An ordinary own-library
staff session creates a task successfully, opens the actual UI and clicks Assign,
but the picker never appears because its management-list request is forbidden.
The browser test fails and does not run its post-browser SQL acceptance; owned
SQL/app/certificate cleanup completed. The parent harness will verify the fix
using desktop/mobile colleague and system super-admin assignment, foreign-library
candidate exclusion and direct mutation rejection, unchanged management access,
persisted SQL claims and safe outbox suppression. No fixture weakens permissions.

## S3-T1 Fix And Superseding Acceptance

Sol added `/api/asap/staff/assignment-candidates?libraryOrgId=...` in the existing
staff feature with only `id`/`displayName` output. Ordinary callers are restricted
to their own library; eligible same-library staff/admin and system super-admins
are available. Both affected pickers use this endpoint without reconstructing
eligibility client-side. Staff Access permissions and authoritative assignment
mutation checks remain unchanged. No schema or migration behavior changed.

Sol's full Node suite passes (28.2s), Release build passes with zero warnings or
errors (3.9s), and two full .NET runs pass **190/190**, zero skipped (3m04s and
3m01s). The exact CI floor is now 190. Focused SQL/API and browser tests pass.
Browser diagnostics exposed an immediate empty-option snapshot and a hard-coded
historical administrator label; synchronization and fixture-derived expectations
were corrected while retaining exact candidate-ID, response-shape and scope gates.

Superseding source-built immutable publication:
`.artifacts/slice-03-final-s3-t1-3e37a48-20260913a`. Web DLL SHA-256 is
`b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`.
Astra's complete 666-file inventory SHA-256 is
`6e1334fe615a7562a9ff5de67ec01d3eec99510f6f8a12af350a7cfadb637317`;
25-file source receipt SHA-256 is
`665140ca0858b4d9e9cf5491b476ce068e7df4100f02a0507b79f87833f83559`.
All 340 native/package files are exactly identical to previously independently
accepted bytes, including both native binaries, DACPAC and all four freshly
exported packages. The four native import/reconciliation acceptances therefore
cover the exact unchanged migration artifact. All 43 frontend sources, 72
compressed variants and 17 vendors pass, with zero forbidden payloads.

Independent published assignment run `df46d9f8e1ff4e78822cbe05e1cb30a9` passes
at both 1280x900 and 390x844. Each ordinary-staff session sees the exact eligible
colleague/system-super-admin option IDs, assigns first to the colleague and then
to the super-admin through the real UI, and observes the selected claimant.
Foreign-library staff are absent; a forged assignment is rejected with 400
`assignee_ineligible` without changing the rowversion. Foreign-scope candidate
lookup and Staff Access remain 403. Candidate JSON has only `id`/`displayName`.
Six screenshot/axe/layout scans pass, and Astra inspected desktop picker and
mobile assigned-state screenshots. Direct SQL confirms two open manual claims
owned by the selected system super-admin, unchanged pending-hold source stages,
four terminal domain suppressions and no generated email files. Cleanup completed.

Assignment browser report SHA-256:
`75811a2b86008779f14decd0c86efa5bf76b73746f67e92b798d7d98ada61000`.
Assignment SQL report SHA-256:
`b12f5e784941a8a6a1db63dda1014dea9a6653f328dd536c100cfeac6ec8ca33`.

Both independent published regressions also pass on the superseding build:
AdditionalCopy lifecycle/UTC/Notes/source-history plus direct SQL and ten scans,
run `184356dcc00247e0abae3dc419a5c4c8`; patron login/submission/duplicate/logout,
domain safety and SQL, run `3ed5859066d5401191946e7fd432df94`. Each completed
its owned database/app/certificate cleanup. The same Terra High context is
performing full Pass 2; its outcome remains a separate required gate.

Astra's independent exact-floor rerun on the superseding frozen source/build
passes **190/190**, zero failed and zero skipped, in 3m00.934s. Command is the
earlier recorded Release no-build invocation with `--minimum-expected-tests 190`.
All parent test/probe sessions have completed. There are no pending acceptance
failures. The remaining local gate is Terra's full Pass 2 outcome, followed by
the coherent milestone, remote CI and clean-tree stop.

## S3-T2 Delayed-Response Reproduction

Terra's full Pass 2 confirms S3-T1 fixed but finds a new blocking stale-response
race in both assignment pickers. Their delayed candidate result can append an
A-bound form after the user closes A and opens B. The mutation remains valid for
A, so backend version/authorization checks cannot distinguish this incorrect UI
intent. Sol is correcting the existing latest-load/dialog lifecycle, not adding
a new request framework. Full same-reviewer Pass 3 is required afterward.

Astra independently reproduces the defect on the exact Pass 2 publication in
run `2b4512d420ce43feae04dfaf63236c9f`. The real browser holds A's candidate
response, closes A, opens B in the same page, then releases the response. One
A-bound picker appears inside B, failing the required zero-picker assertion.
The failure screenshot/report are retained; owned SQL/app/certificate cleanup
completed. No successful race acceptance is claimed. The independent regression
will require both request types and viewports to discard A's response, permit
only B's visible assignment and preserve A's rowversion.

## Pass 3 Acceptance And Further Findings

Sol fixed S3-T2 through the existing latest-load slot, cancellation and current
dialog/type/id/version checks, including connected-form submit checks and
open/rerender/close/sign-out invalidation. Full Node tests, clean Release build
and 190 tests pass. The immutable candidate is
`.artifacts/slice-03-final-s3-t2-3e37a48-20260913a`. Parent inventory SHA-256:
`4b1884a519f6a5481c344a5bc4583f71c7a24f32e77c3654f389fadd12d33819`;
25-path code receipt SHA-256:
`2099a4718ad4a471c48185f9132266a5ecf86451f8b8f8c1957555f670c60329`.
All native/package bytes remain unchanged, and the 43/72/17 frontend/compression/
vendor checks pass. Parent exact-floor rerun passes **190/190**, zero failures
or skips, in 2m47.975s. This does not waive untested UI defects.

Published run `f1eac83e586f40659a9baafd3f10b802` confirms the desktop
AdditionalCopy delayed-candidate fix: no A picker, only B's assignment, unchanged
A version and clean axe/layout. Its ordinary title-request companion fails to
find Assign. Parent and Terra inspect the pinned `grid-row-actions.mjs:22-24`,
assignment policy and target mutation, confirming **S3-A2**: the target's
admin-only title-request button contradicts preserved ordinary-staff behavior.
The harness retains that ordinary-user expectation; it is not changed to use a
super-admin merely to make the test pass. Cleanup completed.

Terra's full Pass 3 also confirms **S3-T3**: a delayed accepted mutation response
can overwrite/close a newer dialog or reopen its old task on conflict. Parent
published run `a66fc98f0de14896af198337cc2c9f89` independently reproduces it.
A's close POST succeeds server-side and its response is held. The user closes
A, opens B and starts B's pending candidate lookup. Releasing A's response
replaces B's title/body with A. The explicit expected-B assertion fails; the
screenshot/report and accepted response are retained, and owned resources are
cleaned up. Sol must preserve A's accepted server outcome while fencing stale
UI completion, including non-delete/delete and equivalent title-request paths.

S3-T1/T2 are verified fixed, but Pass 3 is blocked by S3-T3 and S3-A2. Sol owns
both corrections and a full same-reviewer Pass 4 follows. No nominal review cap
can waive a known blocking defect. No Slice 4 work has begun.

## Final Pass 4 Acceptance

Sol completed S3-T3/S3-A2 within the existing frontend. Accepted mutations remain
server-side facts; current dialog/authentication/version/load-token guards fence
their completion effects, including conflict, close/delete, create, title
mutation and recovery paths. Ordinary title assignment follows the pinned UI
contract, while backend authority and Staff Access restrictions are unchanged.
The expanded integrated browser test passes in 39.8s; full Node tests pass in
19.1s; clean Release build passes with zero warnings/errors in 8.5s; the exact
190-test floor passes with zero skips in 2m51s. A worker tool error interrupted
one earlier verification turn; the same Sol resumed from preserved edits/evidence
and completed the gates. That interruption is not counted as a test result.

Final immutable candidate:
`.artifacts/slice-03-accepted-candidate-3e37a48-20260913a`.
Full inventory `.git/asap-slice-03-artifact-inventory-pass4-final.json`, SHA-256:
`fef86a321baf8a5418fb9195170f81622dbaffe1403829f509e8396266aa4e01`.
Full 25-file source receipt `.git/asap-slice-03-review-state-pass4-final.json`:
`6369c9c8ce93754379f399212245dff9440884f5560328fa75333588c2033b83`.
Web DLL: `b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`.
Workflow JS: `a0898454bb8e594c971eee174f08e50763ece68605357a6fbc8977a142fa1047`.
Both DACPAC copies: `2eac1f4e3e3e1a18b5e2b745fcb3f924c31df0280708b088fd08016b257b7306`.

The clean-build DACPAC changed only Origin.xml build metadata from the earlier
copied native artifact. Astra independently compared the five archive entries:
model.xml, postdeploy.sql, DacMetadata.xml and Content_Types bytes are identical.
The final candidate copies Sol's source-built Web and all remaining payloads
unchanged, with the same actual clean-build DACPAC in both locations. Exactly
one of 666 staged files differs from Sol's publication: that native DACPAC copy.
No source file or migration package was edited; previous freezes are untouched.
All 43 frontend sources, 72 compressed variants and 17 pinned vendors pass again.

Astra independently passed the final exact-floor command: **190 passed, zero
failed, zero skipped**, 2m54.310s. Independent published-browser checks pass:

| Check | Run |
| --- | --- |
| Accepted mutation completion, desktop/mobile close/delete/title assign | `93137040c532476faced9294f514424c` |
| Delayed candidates, both request types/viewports, ordinary title assignment | `d9d7ab0aa944426488b88acdf68e8932` |
| Full AdditionalCopy lifecycle, UTC, Notes and direct SQL | `935647a77e664f488cbc90e04cfdf220` |
| Ordinary peer/super-admin assignment, scope/privacy and direct SQL | `504625c3e4464ac88c340d0db6003876` |
| Patron login/submission/duplicate/logout and direct SQL | `ed2b6648967d4fa2aa8ada361408694c` |

The mutation tests prove A's accepted persisted outcome, B's unchanged current
dialog/selection/actions and surviving pending candidate lookup, followed by
successful B assignment. Candidate tests prove no A picker or mutation appears
in B, A's version stays unchanged and ordinary staff can assign title requests.
All applicable axe/layout gates pass. Astra inspected final desktop title and
mobile deletion-race screenshots, verifying B remains the displayed item.
All owned browser databases/processes/certificates were cleaned up; no email
files were generated. Prior complete lifecycle and outbox assertions remain green.

All four native acceptance fixtures were rerun against the final matching
DACPAC, unchanged native executable and frozen source packages:

| Fixture | Final run |
| --- | --- |
| Original | `cccfed45e2454b88ab7c7b6385f18869` |
| Missing updated | `afd82b680f1d45908296a730e23f8808` |
| Historical boundaries | `750b04fba28e4309892016214cac6e49` |
| Mixed reduced deletion audit | `bbbec4de9278440287232eebd7002b84` |

Every import/reconciliation/strict-identity refusal, snapshot/claim/history,
same-count drift, source deletion, reduced audit and empty-outbox assertion
passes. Each disposable database was removed. All 13 audit JSON outputs are
byte-identical to the prior equivalent fresh target, including reconciliation
SHA-256 `81a149fe2436495ca0d58e1621e8016fd7b90b92a6447840e6a91557292ab2dd`.
All parent acceptance sessions are complete. Full same-Terra Pass 4 remains a
separate gate before milestone/remote CI and the clean-tree stop.

## S3-T4 And Final Pass 5 Acceptance

Terra full Pass 4 corroborated every earlier fix and acceptance result, but found
S3-T4: non-409 hold-recovery errors checked only same-ID selection after the
dialog/mutation token had been superseded. Sol's focused browser test reproduces
an older delayed 502 replacing a newer current error, then passes after applying
the sibling mutation-error pattern. Current non-409 feedback still appears;
current 409 refresh feedback remains intact; stale/auth/abort completions are
ignored. No backend, permission, schema, migration or provider behavior changes.

Sol passes full Node tests (20.0s), Release build with zero warnings/errors
(7.2s), the complete staff browser journey including the new case (42.6s), and
the exact 190-test floor with zero skips (2m54.9s).

The final immutable Slice 3 candidate is now
`.artifacts/slice-03-final-s3-t4-3e37a48-20260913a`.
Inventory `.git/asap-slice-03-artifact-inventory-pass5.json`, SHA-256:
`57b8fc3c6656889ac6e498cfc67bea6465ea292092ebdf5922107a0f2c1b0388`.
Source `.git/asap-slice-03-review-state-pass5.json`, SHA-256:
`62ab4acb40a9e1cc8fd9ee01509d336f2da43c32f0c97769f5a55c4701cc4684`.
Workflow JS: `93ad87b7d78172d5e3332c42180d4e5d1d546bcc7cf48f47c976189094371163`.
Web DLL remains `b8ddcaa3b300003eb361b90f0df19e16b96aa5227107447c54d3fa06813e763f`.
Both DACPACs remain `2eac1f4e3e3e1a18b5e2b745fcb3f924c31df0280708b088fd08016b257b7306`.
All 340 native/package files are byte-identical to the final four native runs;
the same exact migration evidence therefore applies. All 666 entries, 43 source
assets, 72 compressed variants and 17 vendor pins pass independent verification.

Astra independently reruns the final exact command: **190 passed, zero failed,
zero skipped**, 2m53.116s. This includes the tracked real-Kestrel browser
regression for S3-T4. The tested frontend source bytes match publication.
Published mutation-race regression `cbecdfc023d34e5391e887475c31bf67` passes
all six desktop/mobile close/delete/title-assignment cases, preserving accepted
A state and current B/candidate/action context. Published candidate-race
regression `be36105197b14e9b820fbe2b4a118e79` passes all four request-type/
viewport cases with ordinary staff, unchanged A versions and only B assignment.
All applicable axe/layout checks pass and all owned resources are cleaned up,
with no generated email files. `git diff --check` passes.

## Review Gate And Milestone Readiness

Terra completed full Pass 5 on 2026-09-13 with **CLEAR** outcome. S3-T4 and every
earlier finding are resolved; no additional confirmed or nonblocking contract
concern remains. Terra independently verified all 25 source paths and all 666
frozen artifact entries, and reviewed surrounding authorization, concurrency,
lifecycle, migration/reconciliation, audit/outbox, UTC and frontend contracts.
The reviewer made no tracked edits or shared builds. Its completion preceded
the parent's final 190-test result; both independent gates now pass.

Immediately before milestone staging, Astra repeated the source/artifact checks:
Pass 5 receipt digests remain unchanged, with 43 frontend assets, 72 compressed
variants and 17 vendor pins verified. A fresh `git fetch origin main` still
resolves to `150b30b776565194260cc327eeeffdfb46475e81`; all 19 authoritative
pack payload hashes match and `git diff --check` passes. No urgent source delta
needs propagation. The actual deployed PocketBase commit remains unverified.

Local Slice 3 acceptance and independent review are satisfied. Commit the
coherent milestone and verify its actual remote CI before marking Slice 3
complete. Stop afterward with all implementation documentation committed and a
clean tree; do not start Slice 4. The overall port and real Postmark integration,
provider/webhook tests, exact-artifact rehearsal and release gates remain open.

## Committed Milestone And Remote CI

Milestone `85539b0ba34937e31396181adfc87c383b8a0cd8`,
`Port additional-copy workflow (slice 3)`, commits the 25 reviewed implementation
paths and all four Slice 3 evidence/review documents: 29 files, 5,989 insertions
and 195 deletions. It is pushed on `codex/csharp-port` to the existing draft
PR #264. The earlier prepared implementation documents are already committed in
`3e37a48926833a8a92045260fb92b42ef1a090ca`; no prepared packet is left untracked.

The exact milestone passed [remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34755432713)
on 2026-09-13, completed at 11:55:18 UTC. The build-test job completed in 5m18s:

- Release build: zero warnings and zero errors.
- .NET/real-SQL/browser suite: **190 passed, zero failed, zero skipped**,
  3m06.610s, enforcing `--minimum-expected-tests 190`.
- Legacy/frontend unit tests: passed.
- Web and self-contained win-x64 migration publication checks: passed,
  including DACPAC/vendor presence and forbidden runtime/dev-email exclusions.

The CI watcher exited successfully; no local acceptance process remains pending.
Git was clean immediately after the milestone push, with local HEAD equal to
the remote branch. The actual `.artifacts/dev-email/` output paths are ignored,
and no local acceptance or generated email output is tracked. This final
documentation-only receipt will be committed and pushed before stopping; it
does not modify the accepted source/artifacts. Slice 3 is complete, Slice 4 is
not started, and the overall production/rehearsal blockers remain unchanged.
