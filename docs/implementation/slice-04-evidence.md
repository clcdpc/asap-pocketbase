# Slice 4 Implementation And Acceptance Evidence

## Corrective Gate - 2026-09-14

Current corrective starting SHA: `42e65cd3773cf57557e908fe79e01d77d58bac2f`.
Local HEAD, fetched implementation branch and draft PR #264 agreed exactly.
Fetched `origin/main` remains behavioral pin
`150b30b776565194260cc327eeeffdfb46475e81`; no intervening source correction
required propagation. The full Slice 4 review base remains `4769a8a8750c315e319824355d4508073bd43546`.

`slice-04-corrective-notes.md` records the retained Luna Max implementation,
focused SQL/frontend tests and bounded additional findings. This pass changes
neither the DACPAC schema nor migration mappings. It preserves the final-only
FileEmailSender substitution and does not implement Slice 5 notification types.

The first fresh isolated candidate, `corrective-20260914a`, used a complete
nonignored source snapshot at `.git/s4-candidate-corrective-20260914a`. Its clean
Release build passed with zero warnings/errors. The complete .NET/real-SQL run
was 225/226, zero skipped; the sole failure was the existing mobile
AdditionalCopy unclaim/Escape focus-return wait in `tests/browser/staff.cjs`.
Logs are retained under
`.artifacts/slice-04-candidate-corrective-20260914a/validation`. Publication did
not proceed. This red run is not counted as final-byte acceptance.

The retained implementer reproduced two focus races using the actual pinned
Grid.js: the opener arriving after the old focus timers expire, and an opener
being replaced after receiving focus. One scoped DOM observer follows the
opener until deliberate focus, navigation, a new modal or authentication state
changes; existing browser assertions remain unchanged. Deterministic red logs
are `TestResults/corrective-browser-focus-node-red.log` and
`corrective-browser-focus-replacement-red.log`. Nine isolated Node scenarios
passed in `corrective-browser-focus-isolated-green.log`; the unchanged SQL
browser journey passed 1/1 in `corrective-browser-focus-final-green.trx`, with
18 accessibility/layout/image states at
`.artifacts/browser/staff-dd3a73b0ed6747a0a78a6bb3fdfb4901`.

The first full Node attempt exposed scheduled Grid.js work sharing replaced
JSDOM globals between the new scenarios; each now runs in a fresh process.
The complete rerun passed all 172 files in
`TestResults/corrective-focus-final-node2.log`. Existing Git `grep`/`true`
utilities were on PATH so the legacy Dao guard executed without its earlier
missing-tool noise. The final candidate harness retains that environment and
requires at least 226 .NET tests plus a machine-readable TRX report.

The replacement isolated candidate `corrective-20260914b` passed the clean
Release build with zero warnings/errors, all 226 .NET cases with zero skips
(138 integration, 88 unit, including 19 migration cases), and all 172 Node files.
It contains fresh Web and self-contained `win-x64` migration publications.
The tested Web assembly equals the published assembly; all 745 source inputs
remained unchanged during the gates. The 590-file candidate inventory includes
validation receipts. All 45 frontend files, 76 compressed payloads, 17 vendor
hashes and both DACPAC copies verified. Historical b hashes remain in its
immutable local receipts; the review packet now identifies candidate c.

All four stopped fixtures were freshly exported by those candidate native
bytes. Original/edge imported and reconciled 115/116 records, passed 161/164
independent checks, and rejected deliberate same-count reconciliation drift.
The populated-unmapped-setting and ambiguous-sender variants each passed three
negative checks without importing business data. Every package passed the
12-module/four-scope pinned configuration oracle and the four-module runtime
oracle (including eight effective queue limits). Durable identity mapping,
trimmed/normalized readable UPN and separate NotificationEmail were explicitly
verified in both positive native imports. No source database changed.

The first final published-browser batch passed staff administration and cookie
recovery, then stopped before patron execution because an empty optional
environment value selected a directory instead of `journey.cjs`. The red batch
is retained at `.git/asap-slice-04-final-browsers-corrective-20260914b`.
The runner now selects the patron script and SQL assertion flag explicitly.
All 12 modes passed as `corrective-20260914b2` against unchanged bytes: 103
desktop/mobile browser states and 14 pinned CSP cases. Applicable serious/
critical axe, overflow and image checks all passed. Astra separately inspected
the final Staff Access, recovery, different-account and patron screenshots.
The complete receipt is
`.git/asap-slice-04-final-browsers-corrective-20260914b2/receipt.json`, SHA-256
`cc632d154926765e8066712cb4b3a48f9b44f9ec12c256420e54d4f9583a8bc6`.

Astra then performed the required fresh corrective reread over lifecycle,
locked authority, identity metadata, recovery, readiness, settings versions,
template hiding, related guards and migration compatibility. No substantive
issue remained; detailed scope and ruled-out concerns are in `slice-04-review.md`.
Terra full Pass 1 subsequently found one substantive TitleRequest cleanup race
for a global super-admin claimant and a distinct administrator in another
library. Astra confirmed that narrower case and returned it to the retained
Luna. Held request locks and deterministic real-SQL coverage for both orderings
are required before a fresh candidate and the same Terra's full Pass 2.
The existing settings-rule cleanup shares Organization 1 serialization and
does not require a speculative parallel correction. Candidate b is historical
green evidence, not acceptance of the upcoming fix. Earlier entries below
retain their original historical scope; they do not claim these corrections
were present at the Step-4 checkpoint.

The retained Luna reproduced the review race in four of eight SQL cases;
all four opposite orderings passed. The two title cleanup queries now retain
update/hold locks in Id order, matching existing copy cleanup. All eight cases
then passed, including exact cleanup, event and audit counts and preservation
of closed history. `slice-04-review.md` records Astra's adjudication and the
red/green receipts. The complete suite now has 234 cases. The same focused
validation also found and corrected one HTTP test's bootstrap-before-seed
ordering, with unchanged assertions and a standalone passing rerun.

### Fresh Post-Review Candidate C

`corrective-20260914c` freshly passed every complete local gate after the Terra
fix and test-fixture corrections: clean Release with zero warnings/errors,
234/234 .NET tests with zero skips (146 integration, 88 unit, including all 19
migration cases), and all 172 Node files. All 746 snapshot inputs remained
unchanged through the run. Fresh Web and self-contained `win-x64` migration
publications have matching DACPACs; the tested Web assembly matches publication.
All 45 frontend files, 76 compressed variants and 17 vendor hashes passed.

All four stopped fixtures were freshly re-exported using candidate c. Every
pinned configuration/runtime oracle passed; original/edge imported 115/116
records and passed 161/164 checks including deliberate drift rejection.
Unmapped-setting and sender-conflict variants each passed three rejection
checks. Source databases were unchanged, and fresh manifest hashes match the
same logical packages from b. No business configuration mapping changed.

All 12 freshly published browser modes passed: 103 desktop/mobile states and
14 pinned CSP cases, with no applicable serious/critical axe, overflow or image
failure. Astra inspected representative Staff Access, forbidden-cookie recovery,
different-account and patron form screenshots and verified all 164 referenced
browser evidence hashes. The independent source/artifact audit matched all
659 non-document inputs, all 590 candidate files and four native receipts.
Exact candidate-c paths/hashes and coverage remain in the
[candidate-c packet](https://github.com/clcdpc/asap-pocketbase/blob/c147c3501db2c852781b21af4826ce589dab622c/docs/implementation/slice-04-review-packet.md)
and its immutable local receipts. The current packet now identifies d.

The retained Terra subsequently performed full Pass 2 over the complete
Slice 4 base, not only the cleanup correction. Pass 2 is clean and the P1 is
closed. It independently matched all 51 source receipt hashes, 164 browser
evidence hashes, final TRX counts and packet receipt hashes without rerunning
providers or changing files. Astra verified the complete acceptance evidence,
including all 19 unchanged authoritative pack payload hashes and a fresh
fetch showing no main change beyond the pin. The local review gate is satisfied.

Final certification still requires the coherent milestone's actual remote CI.
The exact SHA and run result are recorded in the
[Slice 4 acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639)
after push, avoiding a second documentation commit that would change the SHA
being certified. No earlier WIP CI is substituted; Slice 5 remains unstarted.

### Exact-Milestone CI Failure

Attempted milestone `c147c3501db2c852781b21af4826ce589dab622c` was pushed after
the complete candidate-c gates and clean full Pass 2. Exact remote run
34855842335 failed: 233 passed, one failed, zero skipped. The sole failure was
`ForbiddenStaffCookieCanRecoverSessionSignOutAndChallengeInDevelopment` during
host construction, before its cookie assertions. Linux CI uses SQL
authentication; changing that test host to Development correctly invalidated
configuration, then the fixture's strict worker lookup threw. Windows local
and published Development checks used integrated authentication and passed.

The failed log and run metadata are retained in
`.git/asap-slice-04-ci-c147c35-failed.log` and
`.git/asap-slice-04-ci-c147c35-result.json`. No product policy or assertion may
be weakened to pass. The retained Luna will select the already-registered
genuine cookie/OIDC handlers in the trusted Testing host, retaining secure
antiforgery options and all recovery checks. Fresh complete gates and retained
Terra full review precede a replacement milestone and exact-SHA CI. Candidate
c remains accurate local evidence, not final Slice 4 acceptance.

The fixture correction is now implemented only in
`StaffCorrectiveJourneyTests.cs` and `StaffAuthenticationTests.cs`.
`ForbiddenStaffCookieCanRecoverSessionSignOutAndChallengeWithRealCookieAuthentication`
asserts the trusted Testing host, genuine Cookie/OIDC handler types, no testing
identity headers, and actual secure/path/httponly antiforgery cookie flags.
Every original recovery assertion remains. The environment-loop unit also
verifies the genuine handlers and default sign-in/challenge in Development
and Production. No application, schema, CI policy or SQL-auth guard changed.

Local `TestResults/corrective-ci-cookie-*` receipts record clean Release with
zero warnings/errors, 17/17 focused auth/config/cookie tests, all 234 .NET/SQL
cases with zero skips (including 19 migration cases), and all 172 Node files.
Owned sessions ended and source was refrozen for candidate d. Local SQL used
Windows integrated authentication; replacement Linux SQL-auth CI is still
required. No test login or server authentication policy was changed.

### Fresh CI-Corrected Candidate D

`corrective-20260914d` freshly repeated every final-byte gate from attempted
milestone c147c35 plus the two test-only corrections. Clean isolated Release
passed with zero warnings/errors; complete .NET passed 234/234, zero skipped
(146 integration, 88 unit, including 19 migration cases); Node passed all 172
files. All 746 source inputs stayed unchanged during build/test/publication.
Fresh Web and self-contained `win-x64` migration publications passed the
590-file inventory, tested/published assembly equality, 45 frontend copies,
76 compressed payloads, 17 vendor hashes and matching DACPAC checks.

Four fresh stopped exports passed both pinned oracles. Original and edge
again imported/reconciled 115/116 records with 161/164 checks and intentional
drift rejection; unmapped-setting and sender-conflict variants passed three
controlled-rejection checks each. Source database and manifest hashes match
the earlier logical packages. No source database was modified. Both positive
imports verify durable identity, readable metadata and separate NotificationEmail.
The DACPAC differs from c only in Origin.xml build metadata, not schema or SQL.

Every fresh published mode passed: 12 modes, 103 desktop/mobile states and
14 pinned CSP cases. Applicable serious/critical axe, overflow and image
checks passed, with no development email output or diagnostic interception.
Astra inspected the final Staff Access, recovery, different-account and patron
screenshots; the independent audit matched 659 non-document inputs, all 590
candidate files, 52 full-slice code paths, all four native receipts and all
164 browser evidence files. All 19 authoritative pack hashes still match.
Current exact paths and hashes are in `slice-04-review-packet.md`.

The published Development cookie journey remains unchanged and green. The
CI-compatible HTTP test still exercises genuine cookie/antiforgery/OIDC
handlers, not a testing identity header. Neither test claims live Entra login.
The retained Terra subsequently completed additional full Pass 3 clean over
the entire Slice 4 delta and surrounding callers. It independently verified
52 code hashes, the candidate/artifact/native/browser receipt hashes, all 164
browser evidence files, the 234-case zero-failure/skip TRX and clean Release
log. It confirmed the CI fixture correction preserves the real handlers and
all assertions, with unchanged product policy and the prior P1 still closed.
Astra verified the complete candidate-d acceptance evidence and no code/test
changes after its freeze. See `slice-04-review.md` for the final local gate.

The replacement milestone must still have its own exact-SHA successful remote
CI. Its actual SHA and outcome are maintained in the linked acceptance record,
without generating another documentation SHA after certification. The failed
c147c35 milestone and clean candidate-c review remain historical, not final
certification. Stop after the replacement exact-SHA green gate; no Slice 5.

## State And Ownership

Implementation resumed 2026-09-13 from clean PR #264 head
`4769a8a8750c315e319824355d4508073bd43546` on `codex/csharp-port`.
Astra refreshed `slice-04.md` after reading the complete authoritative pack and
recovering branch, policy, main, PR and CI state. Accepted Slices 0-3 remain
unchanged historical milestones. The behavioral source remains
`150b30b776565194260cc327eeeffdfb46475e81`; deployed source is unverified.

Fresh primary implementer: GPT-5.6 Luna Max Jason,
`01a09b0c-a918-7640-ac4a-6ed90d644f76`. This same context owns implementation,
tests, diagnosis and confirmed review fixes. Astra owns acceptance and the
milestone. Independent Terra High review has not started. No completed Slice 4
test, review, milestone or exact-SHA remote CI is claimed by this preparation.

## Conversation Handoff Checkpoint - 2026-09-13

The user explicitly requested committing and pushing the incomplete work for
continuation in a new conversation. Luna confirmed it is paused with no known
running command/session. See `slice-04-handoff.md` for the current work list and
the existing-checkout paths required to retain local acceptance preparation.
Historical entries below retain their original scope and attribution.

Fresh checkpoint verification on the current implementation:

- `dotnet build Asap.sln --configuration Release --no-restore`: passed,
  zero warnings/errors.
- `node tests/run_all.js`: all 168 discovered test files passed.
- No full .NET/real-SQL test rerun, native migration rerun, new published-browser
  acceptance, final source/artifact freeze or Terra review was performed for
  this checkpoint. Earlier green results do not certify the checkpoint bytes.

The last uninstrumented published-Web run,
`ba0332479ebd420f870712fc58e477a1`, exercised snapshot
`asap-slice-04-web-precheck-20260913b`. All 17 functional scenarios passed:
the ten HTTP scenarios, desktop/mobile scoped saves and editor journeys,
delayed GET and committed old-scope POST fencing, and owned-template-preserving
reset. The separate aggregate accessibility/layout gate failed with twelve
reported entries across desktop/mobile system and patron views. These cover
24 unlabelled format inputs, 24 unlabelled mode selects, invalid organization
list-item parents, and desktop horizontal overflow. All inspected images
rendered. These known failures and the missing staff/audit UI remain open;
passing functional cases are not a passing browser acceptance result.

## Step 1-4 Pause Checkpoint - 2026-09-14

Implementation intentionally paused after completing only steps 1 through 4
from `slice-04-handoff.md`. This is a WIP/checkpoint state, not the accepted
Slice 4 milestone. The final Slice 4 acceptance/freeze, native migration reruns,
Terra review, exact-milestone remote CI and Slice 5 were not started.

Completed in this checkpoint:

- Staff Access UI in the existing settings frontend: roster load, add staff,
  per-record profile save, role/library change, deactivation/reactivation,
  super-admin-only confirmed/reasoned rebind, actual cleanup-result display,
  scoped audit-history display, ordinary-admin own-library scope and stale
  staff/scope load fencing. Staff records remain global records, not settings
  override/inheritance state.
- Published-browser defects from the prior run: only the selected settings
  panel is initially visible, all 24 dynamic format-label inputs and 24 dynamic
  mode selects have accessible names, organization rows have a semantic `ul`
  parent, and the desktop initial/patron overflow is removed.
- Real-SQL/API coverage for every inheritable scalar in Workflow, Patron text
  and Email sender settings: system save, library override save, effective
  library resolution, and single-field reset while a peer override in the same
  row remains. The write-only Postmark token path is covered separately for
  library override preserve/clear behavior without reading the secret.
- Additional scoped HTTP coverage for audit authorization and system-only
  settings authorization using a real ordinary-admin staff row.
- The retained local browser harness was extended under `.git` with Staff
  Access HTTP/UI journeys, organization activation/version checks, scoped audit,
  stale Staff Access load behavior and real lifecycle cleanup seed data. These
  harness edits are local-only evidence tooling, not tracked source.

Focused validation:

- `node --check src\Asap.Web\Frontend\staff\js\settings.js`: passed.
- `node --check src\Asap.Web\Frontend\staff\js\settings-domains.js`: passed.
- `node --check .git\asap-patron-browser-probe\admin-settings.cjs`: passed.
- `dotnet build .git\asap-patron-browser-probe\Probe.csproj --configuration Release --no-restore`: passed, zero warnings/errors.
- `dotnet build Asap.sln --configuration Release --no-restore`: passed, zero warnings/errors.
- `node tests\run_all.js`: all 170 discovered Node regression files passed.
  The existing Windows `grep`/`true` noise inside `no_dao_usage.test.js` still
  appears, and that test still reports `PASS: No "Dao" usage found.`
- `dotnet test tests\Asap.Tests\Asap.Tests.csproj --no-restore --filter "FullyQualifiedName~AdministrationInheritableScalarsSaveResolveAndResetPerField|FullyQualifiedName~AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole"`:
  passed, 2/2.
- `git diff --check`: no whitespace errors; only line-ending normalization
  warnings for edited text files.

Fresh isolated published-Web evidence:

- Snapshot name: `slice-04-step4-20260914c`.
- Published root:
  `.artifacts/asap-slice-04-web-precheck-slice-04-step4-20260914c`.
- Receipt:
  `.git/asap-slice-04-web-precheck-slice-04-step4-20260914c-receipt.json`.
- Source files: 144. Published files: 256.
- Published DACPAC SHA-256:
  `8ad550e2b75a5f24d24a4af086207e01a64daaa5c80727491b2ef7edce454034`.
- Parent browser run:
  `.artifacts/acceptance/patron-browser/f6e7cf022b314288a53c0a0e4c16de06/admin-settings-results.json`.
- Probe script diagnostic mode: `false`.
- External browser requests: `0`.
- Result: 23/23 scenarios passed. The run includes the previous 17 functional
  scenarios plus Staff Access HTTP lifecycle/audit/rebind, organization
  activation/version/scope HTTP contracts, desktop/mobile Staff Access UI and
  stale Staff Access roster fencing.
- Browser states captured: 17. The aggregate gate passed with no serious or
  critical accessibility violations, no horizontal overflow and no visible
  image-rendering failures.

Earlier same-day local probe attempts
`e1e1186ec5704aa6872ae74ca6e0a565` and
`525f5e4fa6564fdfa71272a620d7434e` failed on harness assertion mismatches
while extending the local probe (`active` vs `isActive`, and a visibility wait
for already-rendered dynamic controls). They are not passing evidence and did
not require weakening application behavior.

## Independent Migration Inputs

Existing parent preparation is retained without modification:

- stopped synthetic source: `.git/asap-real-pb-source/admin-settings-data`;
- Record/Collection-API fixture migration:
  `admin-settings-migrations/209901010017_admin_settings_acceptance.js`;
- source oracle: `.git/asap-admin-migration-acceptance/config-oracle.cjs`;
- earlier oracle: `pinned-config-oracle.json` in that preparation directory.

The oracle executes twelve exact pinned config modules using narrow read-only
record adapters, checking text/Goja-byte JSON equivalence at four scopes. It is
source-behavior evidence, not an actual PocketBase route-runtime smoke test or
a target migration result. Parent will re-export the stopped source using the
final native Slice 4 executable and rerun the oracle on that hashed package.
Existing Slice 2/3 package manifests and acceptance artifacts stay unchanged.

Inventory 13 explicitly corrects partial patron-text record fallback to use
configured system values, and empty library publication sets to inherit the
complete system set including stable IDs, disabled values and order. The
fixture's attempted `misconfiguredMessage` was not persisted by its actual
source schema. Migration must preserve the actual source fallback; target
administration must support the typed field normally. The attempted text is
not an imported production value.

## Baseline Gap Probe

Parent compiled an isolated acceptance probe without application project
references (zero warnings/errors). No PocketBase process was running. The
accepted Slice 3 native executable exported the stopped administration fixture
to `.git/asap-slice-04-baseline-admin-export-20260913a`; the twelve-module
oracle passed all four scopes and both JSON boundary representations.

Native import/reconciliation succeeded for 115 records. The independent
108-assertion run at
`.git/asap-admin-migration-acceptance/runs/c263fd21c5a34ff1afaa4191c1c56f02`
identified eight assertions in five Slice 4 extension domains: ordinary blank
library strings stored as empty rather than NULL, an empty library publication
set retained rather than removed, built-in format resolver precedence lost,
custom-field rule label lost, and scoped sender not moved to EmailSettings.
Existing text runtime fallback can still handle some empty strings; those
assertions identify sparse-persistence/editor-contract gaps, not proven current
runtime fallback failures. Astra inspected the saved SQL rows and source oracle
and returned these scoped corrections to the same Luna context.

The exact package/target reconciled before mutation; a same-count scoped eBook
text change then correctly failed reconciliation. All probe-owned SQL targets
were removed. Harness setup mistakes (an initial DACPAC path and source/target
icon-field name mismatch) were corrected before the reported run. This is
baseline analysis only; final Slice 4 native acceptance is still pending.

Parent also copied the stopped source to a separate
`admin-settings-unmapped-data` fixture and added a populated
`workflow_settings.unmappedAdminSetting` through PocketBase Record/Collection
APIs only, with empty hooks and no HTTP server. Accepted native export and
import passed all three rejection checks: explicit `source_field_unaccounted`,
zero imported staff/requests/mappings and no successful reconciliation report.
Evidence: `.git/asap-admin-migration-acceptance/runs/70c5f33209644af3ad1da2868281f9c7`.
The probe-owned database was removed; the original prepared fixture was not
changed. This existing protection must remain green on the final native build.

The separate PB-API-only `admin-settings-edge-data` fixture adds system template
sender precedence, a library with built-in format JSON but no custom-field or
material-format row, message-only format JSON, and sparse built-in email content.
The fresh twelve-module oracle confirms those exact source outcomes. The
expanded 161-assertion baseline run imported/reconciled 116 records and reported
18 assertions, including the original gaps, six blank provider override fields,
and the added edge cases. These are pending Slice 4 extension checks, not
acceptance failures against a completed Slice 4 implementation.
Evidence: `.git/asap-admin-migration-acceptance/runs/6111b329a17d4bbe97b8dfabb2e76a97`.

Astra inspected exact pinned `lib/config/emails.js` and ruled that both system
and library template sender fields must be considered after SMTP defaults.
Its source query has no explicit order; competing meaningful sender values
must not be silently resolved by exported-ID sorting. Preserve determined
per-scope values or report a blocking source ambiguity under the existing
migration contract. Astra also confirmed that built-in format rules cannot be
conditioned on custom-field definitions or prior override-row existence. Luna
retains implementation and tracked regression ownership for both rulings.

A fourth stopped fixture, `admin-settings-sender-conflict-data`, copies the
edge source and gives a different existing system template a competing sender
through PocketBase Record APIs. Parent's native probe now includes an explicit
`email_sender_ambiguous` rejection/empty-target/no-success-report mode. Fixture
preparation and probe compilation passed; final native export/import of this
negative case remains pending alongside the other three Slice 4 fixtures.

## Provisional Native Checkpoint

While Luna completed administration UI/tests, Astra published only the migration
project with isolated MSBuild intermediates and output. All 19 migration/shared/
build inputs remained unchanged across this Release self-contained build. SQL
source had no changes; the accepted schema-4 DACPAC was used explicitly. This
does not freeze the complete slice or replace its final native publication.

Native root: `.artifacts/slice-04-native-precheck-4769a8a-20260913a`.
EXE SHA-256: `d2a9c25d70f31d28b41087081e2bb3cce4cd98863b58d5a0f4f7da6082c6ecab`.
DLL SHA-256: `5466702a56ea79aa2b2865e808f35877a414a0b3363b5b1ca0d25ce9cee9f6a9`.
DACPAC SHA-256: `2eac1f4e3e3e1a18b5e2b745fcb3f924c31df0280708b088fd08016b257b7306`.
Input/build/result receipt:
`.git/asap-slice-04-native-precheck-receipt-20260913a.json`.

All four stopped fixtures were freshly exported into separate
`.git/asap-slice-04-native-precheck-*-export-20260913a` packages. Both positive
packages passed the exact twelve-module source oracle at four scopes and both
JSON representations. Actual native executable results:

- Original: 158 checks passed, 115 records imported/reconciled;
  run `6f14fd1b66c4471ca85521d94365deec`.
- Expanded edge: 161 checks passed, 116 records imported/reconciled;
  run `21fb1b964a9d42f7a3b48be6a3cd7de1`.
- Unmapped setting: 3 rejection checks passed (`source_field_unaccounted`);
  run `42d55d8706964a7b940a5d5f97539c44`.
- Competing sender: 3 rejection checks passed (`email_sender_ambiguous`);
  run `f8818b94437049759a5e5cadf0721b06`.

All runs are under `.git/asap-admin-migration-acceptance/runs/`. Positive cases
also rejected same-count scoped eBook drift after reconciliation. Negative
cases left no imported staff/requests/mappings or successful report. Every
probe-owned SQL database was removed. The first original-fixture run compared
PocketBase's absent sender empty string to normalized SQL NULL; Astra corrected
that probe assertion to require the intended NULL and reran successfully. Its
retained run `02da6ccb70f84e75811acd9f4f4bae76` is a harness mismatch, not an
application defect. These remain provisional results pending final slice bytes.

## Pre-Freeze Integration Guidance

Astra inspected the in-progress, buildable administration boundary and returned
the following requirements to the same Luna context before frontend completion
and the final test/review freeze. This is not a Terra pass or acceptance claim:

- Do not represent an absent library Workflow/Patron/Email row as a stored
  system row. Inventory 13 requires unambiguous configured-system, effective
  and raw-library override information for each field, including libraries
  whose only existing override is in another domain.
- Editable mutations require the version contract; missing versions cannot
  bypass concurrency checks. A GET must not pair old field values with a new
  independently queried version after a concurrent write. Preserve coherent
  snapshot/version pairing and test the read/write interleaving.
- New administration commits must revalidate the actor's current durable
  identity/activity/role/scope under the accepted lock order, not rely only on
  the earlier cookie check. The same current eligibility governs rule targets.
- A deletable custom format may disappear during a patron's provider wait.
  Revalidate the selected format at the protected local insert boundary and
  return a controlled validation/conflict result, without extending a SQL
  transaction across Polaris calls or deleting referenced history.
- Structured settings domains must retain ordinary form-based editors. Raw
  JSON authoring areas do not satisfy the requested existing vanilla settings
  UX. Email token controls must support library overrides, and SQL entity IDs
  must remain decimal strings through selects, payloads and returned values.
- Both branding write paths must apply the binding PNG/JPEG/GIF signature,
  content-type, 2 MB and 4096x4096 limits and preserve original accepted bytes.
  The initial draft's image/* and 5 MB checks were not sufficient.
- Numeric-ID and legacy-key timeout-template inputs must enforce identical
  template scope/eligibility rules. Hiding/deleting a template must preserve
  the effective timeout-template reference invariant across inheritance.
- SQL read locks inside a serializable transaction also count toward lock
  ordering. The actor-routing lookup must not retain a StaffUser lock before
  Organization, and new format revalidation must follow the accepted staff
  lock stage. Astra returned concrete deadlock interleavings to the same Luna.

The authoritative implementation plan assigns scheduled/manual timeout
execution and full F1/F2 execution cases to Slice 5. Slice 4 still delivers
complete timeout configuration, effective resolution and migration fixtures.
No recurring job was brought forward merely to report those later tests green.

## Independent Published-App Preparation

The isolated parent browser harness has an administration-only fixture mode
with a system super administrator, two library administrators, ordinary staff,
an inactive library, and a library-owned rejection template whose SQL ID is
`9007199254740993`. This intentionally exceeds JavaScript's exact integer
range. Default earlier-slice harness modes remain unchanged.

Parent prepared `.git/asap-patron-browser-probe/admin-settings.cjs` to exercise
the actual published application's HTTP authorization, antiforgery, sparse
override/secret/whole-set/version behavior, large template IDs, branding input
validation and original-byte serving, scoped audit, and desktop/mobile settings
flows. Browser assertions include dirty-switch cancellation, system-only payload
exclusion, delayed old-response suppression, visible images, horizontal overflow
and serious/critical axe findings. Network interception blocks nonlocal browser
requests; deterministic testing adapters and email safety prevent provider work.

The independent harness builds cleanly and the browser script passes syntax
checking. They have not run against a completed Slice 4 candidate yet, and are
not substitutes for Luna's tracked tests or the required independent reviews.

### Provisional Published-Web Run

Astra captured 144 source/build files with copy/hash consistency checks, then
built and published Web only inside an isolated source snapshot. No shared
`bin`, `obj` or `wwwroot` outputs were changed. The provisional 256-file output
is `.artifacts/asap-slice-04-web-precheck-20260913a`; full input/publication hashes
are in `.git/asap-slice-04-web-precheck-20260913a-receipt.json`.
The fresh DACPAC differs from the accepted artifact only in `Origin.xml` build
metadata: model, post-deployment SQL, DAC metadata and content-type entries are
byte-identical. Entry hashes are recorded in
`.git/asap-slice-04-web-precheck-dacpac-20260913a.json`.

Published run `b0ab75e9f4634453b851c88e877a7ce6` under
`.artifacts/acceptance/patron-browser/` passes the ten HTTP scenarios: role/scope/
antiforgery boundaries, sparse values and protected-secret semantics, whole-set
replacement/inheritance, mandatory/stale versions, live patron-code validation,
named rejection lineage/partial content/cross-scope references, large template
IDs, independent branding bytes/alt, both invalid-branding paths and scoped audit.

Its first ordinary-library-admin UI save exposes a confirmed completion defect:
POST succeeds, the displayed version equals the returned version and refresh
finishes, but the form remains dirty with Save enabled and "Unsaved changes"
after fifteen seconds without further input. The run retains exact diagnostics
and `save-completion-failure.png`. Astra returned the reproduction to the same
Luna context; it remains a pre-freeze integration finding, not a Terra pass.
The full desktop/mobile browser suite is not yet green.

The diagnostic-only follow-up `fe4bc9aca94541d3b6523ddc11581d90` confirmed that
the form still compared against its complete pre-save baseline, including the
old version, text and override toggle. Luna traced a repeated-populate exception
in override-toggle reuse, fixed the cause, and added the tracked
`settings_save_completion.test.js` regression. No diagnostic instrumentation
was included in the published files or counted as acceptance evidence.

Fresh isolated publication `asap-slice-04-web-precheck-20260913b` has its own
144-input/256-output hash receipt. Its uninstrumented published run
`2ad4b1b15bd142e09e86da3766af79e7` passes all ten HTTP scenarios and both
desktop/mobile own-library-admin saves, secret save/clear and clean-state checks.
Both library-email states have no serious/critical axe violations or overflow.
The initial super-admin view exposes the remaining pre-freeze issues: all
settings panels are initially visible, 24 format-label inputs and 24 mode selects
lack accessible names, organization list items lack a semantic list parent, and
desktop content overflows by 14 pixels. Exact reports/screenshots were returned
to the same Luna. Later probe scans aggregate accessibility/layout failures
across all reached panels and still fail if any gate is unmet.

Astra also verified that the current Staff Access panel only manages
organizations, without the required staff-record editor or scoped audit-history
screen. Plan 02's Slice 4 explicitly requires both. Luna was directed to adapt
the pinned roster/add/per-record-action workflow to the accepted versioned
StaffLifecycle endpoints and durable Entra contracts, with no inheritance
banner/reset semantics for these global records. This remains implementation
work, not a reason to reopen the accepted lifecycle architecture.

Earlier probe attempts used an outdated three-code deterministic fixture
expectation and an insufficient Save-disabled completion wait. Those harness
expectations were corrected before the confirmed version-aware reproduction.
No target contract or application code was weakened to pass a probe. Every
run's application, database and temporary certificate were cleaned up.

## Implementation Checkpoint

Luna reports `node tests/run_all.js` green across 116 discovered test files,
with syntax checks passing for `settings.js` and `settings-domains.js`. Web and
migration Debug builds were clean with zero warnings/errors before the latest
alt-only multipart branding correction; reruns and the new focused SQL tests
remain in progress. These are intermediate results, not the final Release,
published-artifact, independent-review or exact-milestone CI gates.

The first focused real-SQL/HTTP test,
`AdministrationSettingsSaveReadAndClearUsesLivePatronCodesAndSparseOverrides`,
now passes (1/1). It exposed EF principal/member insert ordering when creating
whole-set configuration; Luna added the corresponding explicit relationships
to the existing EF model without transferring schema ownership from DACPAC.
The journey covers live code choices, save/read, rejected unknown IDs, sparse
scalar clearing, empty-set inheritance and direct SQL absence of removed
set/member rows. Broader inventory, race, migration, browser and full-suite
verification remains pending.

The first full Debug run reported 191 tests (180 passed, 11 failed). Luna
diagnosed the integration regressions, verified targeted corrections, and
fixed mobile staff navigation overflow introduced by the additional Settings
view. The next full Debug run passed 192/192 with zero failures or skips.
Provider-failure atomicity and frontend domain-control/mutation-completion
regressions also pass. These results do not replace remaining complete
inventory coverage, the final Release suite, or published/native acceptance.

Parent additionally returned the actual branding form's scope binding and
disabled-alt serialization gaps: a super-admin library image action must reach
the selected library, and must not turn inherited alt text into an override.
Published desktop/mobile checks now exercise image upload and removal through
those controls, in addition to the direct API validation cases.

The four new tracked Node regressions also pass at the intermediate checkpoint:
domain controls, stale mutation completion, multipart branding payloads and
library-admin initial scope/save. The 192-test Debug result predates the expanded
effective WorkflowSettings projection and subsequent administration changes;
it is not evidence for those newer bytes. The interrupted implementation turn
was resumed in the same Luna context. No replacement implementer or review
context was created.

Astra verified a remaining named rejection-template editor gap against
inventory 13 and pinned `lib/staff/settings_email.js`: the draft only rendered
library-owned custom rows, hid system creation and generated keys outside the
rejection namespace. Luna must complete system named templates, library lineage
override/hide/reset and selectable library-owned rejection templates, preserving
independent content inheritance and reference safety. This is pre-freeze
implementation guidance, not a completed Terra review pass.

## Acceptance Ledger

Completed by the step-1-through-4 pause checkpoint, with evidence above:

- Staff Access frontend flows, scoped audit UI, accepted versioned lifecycle
  mutations, durable Entra identity, actual cleanup-result display and stale
  staff/scope load protection.
- Confirmed published-browser defects: initial panel visibility, dynamic format
  control labels, semantic organization list parent and horizontal overflow.
- Focused real-SQL scalar save/override/effective/reset coverage plus
  write-only secret preserve/clear and scoped audit/system-only authorization.
- Published vanilla settings browser/API journeys: role/library boundaries,
  dirty/cancel/scope switching, stale settings and Staff Access loads, mutation
  completion fencing, keyboard/focus, mobile/desktop layout, visible images and
  serious/critical axe gate.

Pending final Slice 4 step-5 gates, still to be replaced with actual final
commands, results and artifact hashes:

- Run the complete required Release/.NET/real-SQL/Node suites on final bytes,
  not only focused tests and the checkpoint Node/build passes.
- Fresh native export/import/reconcile of the prepared administration fixture,
  effective source comparison, declared corrections, provenance, every-domain
  reconciliation and rejection of unexplained populated source values.
- Fresh self-contained migration package, source/artifact receipts, final
  matching DACPAC/artifact/source hashes and final freeze checks.
- Same-context Terra full Pass 1 and Pass 2, conditional additional full passes
  and blocking-finding fixes; independent Astra acceptance.
- One coherent Slice 4 milestone commit, push to the existing branch and actual
  successful remote CI for that exact SHA before dispatching Slice 5.

The final Postmark provider/webhook boundary remains a release/rehearsal blocker
under `temporary-email-transport.md`. File transport does not waive protected
configuration, durable outbox, idempotency, snapshots, recipient authorization,
domain safety, cancellation, leases/fencing, retry or retention requirements.
