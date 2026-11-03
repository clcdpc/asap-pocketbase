# Operations and administration audit (#450)

This change implements #430, #431, #432, and #434 serially on
`codex/i450-operations-audit`, starting at `c168c3856e43fa15fd44382ca56a33286c05ced4`.
Merged #420 (`50f1483a30e2b738b0082e91c452081011f502c6`) and #421 are ancestors
of that base. The existing checkout's unrelated browser edits are preserved in
place; implementation uses an isolated managed worktree.

Before final qualification, the branch incorporates current `main` commit
`e3fe8edca4ae872548939663cd7c397f528a906f` (#451). That upstream change adds
deployment tooling and its tests; application and database source are unchanged
by the integration. Final gates use the integrated head. The completed 68-file
frontend run on `4c8e853` remains separate from those final-head receipts.

The authoritative visual reference is
[`operations-audit-mockup.html` at `d85d22f`](https://github.com/clcdpc/asap-pocketbase/blob/d85d22f2373bf38080e80d0a893c8daa49a504ee/docs/design/operations-audit-mockup.html).
Only the production application is changed; reference data and preview tools
are not application inputs.

## Qualification matrix

| Issue | Acceptance evidence | Tests | Independent reviews | Commit |
| --- | --- | --- | --- | --- |
| #430 | Operations-only link removed; Settings link, saved context, exact recovery identity and focusable log retained | Focused email-test and Operations Node files PASS; saved-email real-SQL/Chromium journey 1/1 PASS with six checkpoints | Luna MAX independent review and closure audit PASS; no actionable findings | `ac57372` |
| #431 | One refresh; truthful log/status; guarded Settings handoff; exact identity/recovery; latest current test visible beyond 500 failed rows | SQL 23/23 then 6/6 PASS; final correction SQL 9/9, ten focused Node commands and saved-email browser 1/1 PASS | Luna MAX / Astra HIGH plus final Astra XHIGH code audit cleared after combined remediation below | `c33e327` plus closure correction; exact SHAs in PR |
| #432 | One forced manual action; distinct deliberate identities, original-identity retry and gesture guards | SQL 27/27 and browser 1/1 PASS; warm-fixture correction 6/6 and broader 28/28 PASS | Luna MAX / Astra HIGH and combined Astra XHIGH test-fix review clear | `ee835b6` plus fixture correction; exact SHAs in PR |
| #434 | Three addressable sections; full-history scoped audit; Staff access handoff; approved responsive layout | SQL 11/11, retained browser 4/4, final audit browser 1/1, focused Node PASS; pre-review full frontend 68/68 | Luna MAX / Astra HIGH / Terra MAX final R2 closure PASS; all findings resolved | This change; exact SHA in consolidated PR |

Implementation and functional-review agents use the requested available models:
Luna MAX for #430 and functional reviews; Sol MAX for #431/#432/#434;
Astra HIGH for security/data-integrity reviews; Terra MAX for visual review.
The final combined adversarial audit uses Astra XHIGH. Orchestration remains in
the parent Codex session. Review findings must be resolved and independently
verified before the next issue starts.

### #430 qualification

- `dotnet build Asap.sln -c Release --nologo`: PASS, zero warnings/errors.
- `node tests/staff_email_test.test.js`, `node tests/staff_operations_ui.test.js`,
  `node tests/staff_operations_controller.test.js`: PASS.
- `dotnet test --project tests/Asap.Tests/Asap.Tests.csproj -c Release --no-restore
  --filter FullyQualifiedName~SettingsEmailTestBrowserDispatchesAutomaticallyThroughRecordingPostmarkAndObservesSameReference`:
  PASS, 1/1. Six actual desktop/mobile checkpoints; zero external requests, page
  errors, serious/critical axe findings, missing images or page overflow.
- The first browser wrapper run exposed its old five-checkpoint assertion after
  the added sixth scan. The assertion now reflects the actual six checkpoints;
  the complete focused journey was rerun successfully.
- The SQL fixture's recording Postmark handler recorded one provider request
  from the explicit new send; the deliberately abandoned submission, navigation,
  retirement, reconciliation and reload did not create another provider request.
- Independent Luna MAX reviewed the complete code/test diff, inspected all six
  browser states and their report, and independently reran the Operations UI
  Node file successfully. It found no actionable defects or #430 acceptance gaps.

### #431 qualification and review resolutions

Settings retains saved context, Send, and always-available guarded navigation.
Both screens remove individual status refresh and routine terminal results.
Main refresh reconciles by reference or original operation identity without
writes, independently of table failures. Session recovery, explicit retirement,
late acknowledgement and scope/account ownership remain enforced. The log uses
recorded delivery-mode evidence; provider acceptance never means mailbox receipt.

Independent Luna MAX and Astra HIGH reviewed actual source/tests after each
remediation. Their final R3 closure passes have no actionable findings.

| Finding | Resolution and regression |
| --- | --- |
| Luna P2: retirement focuses disabled Send | Choose enabled Send, main Refresh, or focusable log; test blocked/unavailable context and busy Refresh |
| Astra P2: historical result hides current configuration block | Retain readiness guidance separately from history; test both panels, response orders and unresolved outcomes |
| Astra P2: table failure skips exact reconciliation | Run independent reads with owning-load cancellation; test queue/list/catalog failure, known/unknown reference and zero writes |
| Astra validation: 20ms acknowledgement race | Controlled window clock verifies acknowledgement before expiry and clearing at expiry; the independently observed initial failure is resolved |
| Luna P2: 500 failures hide newly submitted test | Keep bounded failed-first query; prepend current authorized exact test only if absent, without retry authority; SQL tests seed 501 failures |
| Luna P2: delayed table renders older queued observation | Resolve operation-bound latest-observation accessor after tables finish; test newer poll, identity/storage replacement, scope/account/deactivation and cancellation |

Executed qualification (counts describe separate runs, not unique totals):

- Focused real SQL: 23/23 PASS; subsequent targeted SQL: 6/6 PASS, including
  pending/sent/suppressed beyond the list bound, actual-mode evidence, HTTP
  idempotency and the retained failed-first contract. No skips or failures.
- Final `staff_email_test`, `staff_operations_controller`,
  `staff_operations_ui`, selected email-navigation (2 journeys), and
  `staff_operations_postcommit_401_ui` Node invocations: PASS.
- Final saved-email SQL/Chromium journey: 1/1 PASS; six desktop/mobile scans
  with zero serious/critical axe findings, page errors, external requests,
  missing images or page overflow. Two deliberate client submissions include
  one never reaching the server and one committed with a lost acknowledgement;
  five exact by-intent reads preserve the same reference. SQL verifies one
  recording-provider request and one worker job.
- Final reviewers independently reran focused Node checks and adverse probes,
  including post-poll cancellation and storage loss. Astra also completed 262
  navigation/draft journeys during remediation; the final focused navigation
  rerun passed. An accidentally broad Luna navigation invocation was stopped
  and is not counted as a complete run.

Exact commands, TRX, browser reports and images are retained outside Git under
`Documents/Codex/2026-11-02/i450/`; latest receipts use `i431-r3-*`.

### #432 qualification

The single Run weekly summary action uses the existing forced endpoint. It
retains durable command capture/recovery, scope, recipient rules and ordinary
scheduled processing. Native double-click detail and held-key guards distinguish
one activation from a later deliberate run; backend execution is unchanged.

- Operations controller/UI, all 262 navigation/draft journeys, postcommit-401,
  and shared email-controller Node checks: PASS.
- Focused real SQL: 27/27 PASS, no skips/failures. Native discovery verifies all
  27 UIDs across 15 intended method/data-case populations. Coverage includes a
  fresh same-period run after acceptance, retry without another provider call,
  eligibility/opt-out/inactivity/empty/suppression, scope/actor authority, audit,
  ordinary deduplication, cancellation, and all seven ordinary schedules.
- Dedicated SQL/Chromium journey: 1/1 PASS; eight accepted jobs with seven UUIDs
  and original-ID/library retry. Trusted click details `[1,2]`, held Enter,
  subsequent Space/new ID, lost acknowledgement/reload/read-only refresh,
  selected/aggregate/admin scope, keyboard focus and four desktop/mobile scans
  pass. No provider delivery is performed by the browser command.
- The initial SQL command failed its guessed minimum-30 guard despite 27
  successful cases. Discovery confirmed 27 and the correctly guarded rerun
  exited zero. Browser harness failures were corrected using actual trusted
  down/up events and the existing scope-specific feedback contract; final R3
  passed. Initial failure receipts remain retained, not counted as successes.
- Exact commands, discovery, TRX and images: external `i432-*` evidence.
- Independent Luna MAX and Astra HIGH reviewed actual code, SQL/browser tests
  and receipts and found no actionable issues. Luna independently reran all
  five frontend files, browser-script syntax and diff checks; Astra reran the
  controller/postcommit checks and an extra committed-UUID/refresh-401 probe.
  Scope, actor revalidation, recipient rules, audit and scheduling were traced
  through endpoint, worker and SQL mutation/deduplication paths. Both issue
  closure audits passed before #434 implementation began.

### #434 qualification and review resolutions

Operations uses Workflow, Email operations and Administration audit, in order,
with one main refresh and addressable section/filter state. Run, email and audit
scopes remain independent. Settings email navigation and Staff access history
use guarded departures; the global Staff roster opens aggregate staff history,
while a selected library opens that library. System-only history remains a
distinct selection. No mockup preview controls or sample records are shipped.

The audit service filters/counts/pages the full authorized SQL history. Pages
are bounded to 100 (UI 25), with numeric ID tie-breaking, historical actor
choices, and local calendar dates converted to half-open UTC boundaries across
DST changes. Existing transaction/actor locks protect authority. Only safe
action-specific recorded details are projected; raw JSON is neither returned
nor searched. Actor and affected-record fields stay separate.

Luna MAX, Astra HIGH and Terra MAX independently inspected actual source,
tests and receipts. Their final R2 issue closure decisions have no actionable
findings. Terra compared both themes and desktop/mobile captures with the
pinned reference and independently reproduced the corrected focus behavior
using the production controller, markup and stylesheet in Chromium.

| Finding | Resolution and regression |
| --- | --- |
| Astra/Luna P2: stale super-admin ticket exposes library options after demotion | Compare the locked current role/library with the ticket before any projection; real lifecycle demotion verifies stale-ticket rejection and current-admin scope isolation |
| Terra/Luna P2: mobile queue fragments six columns | Readable minimum table width inside the existing scroll region; 320/390px in both themes verify token widths, keyboard scrolling and no page overflow |
| Terra/Luna P2: disabling pager/main Refresh loses focus | Stable loading handoff and current-load/session/user-movement guards; delayed success/failure, boundaries, both directions and background refresh tests |
| Terra/Luna R1 P2: restored pager focus is offscreen | Native focus reveals available buttons; unavailable boundaries reveal the results start. Full 25-row first/last pages verify viewport bounds as well as active-element identity |

Executed qualification (separate runs, not unique-case totals):

- Pre-review full frontend: 68/68 files PASS, no failures/skips. Final remediation
  focused audit, Operations controller/UI and postcommit-401 files PASS; R1 also
  passed shared email and selected email/staff navigation checks.
- Real SQL: final 11/11 PASS, no skips, including full history beyond 200 rows,
  scope/options/details/count isolation, actor narrowing, stable bigint ties,
  DST boundaries, invalid queries and lifecycle revalidation.
- R1 combined SQL/Chromium: 4/4 PASS, covering audit and the retained saved-email,
  weekly-summary and UTC/bigint journeys. Final R2 audit journey: 1/1 PASS,
  26 clean states, 22 visible-focus witnesses, four mobile queue cases and
  48 native PNGs. Zero serious/critical axe findings, external requests, page
  errors, unexpected missing images, page overflow or writes.
- The new visibility assertion deliberately failed against retained R1 built
  assets: Next was at y=3807–3851 in a 1000px viewport. Rebuilt R2 passed with
  Next/Previous at y=913–956 and visible boundary/error targets. This failed
  control run is retained separately and is not counted as qualification.
- Final Release build: zero warnings/errors. All 25 frozen source/test hashes
  were independently verified. Evidence is retained outside Git under the
  existing `i450` root, using `i434-r1-review-*` and `i434-r2-review-*` names.

### Combined adversarial code audit and correction

Astra XHIGH inspected the complete four-issue diff, all five issue descriptions,
the pinned reference and actual source/tests/receipts. Its initial audit found a
stale existing test-email row and inaccurate System-scope documentation. Further
independent Luna MAX, Astra HIGH and Astra XHIGH probes exposed timing, handoff
and storage-loss variants. All actionable findings were fixed on this branch;
the final R3b functional, security and adversarial code reviews are clear.

| Finding | Resolution and observable regression |
| --- | --- |
| Existing listed reference hides newer exact status | Exact reads expose the existing canonical SQL rowversion. Compare decoded eight-byte values and select complete snapshots; never combine newer presentation with stale retry authority |
| Newer list state disagrees with remembered recovery state | The original operation-bound accessor accepts strictly newer same-reference/scope evidence, updates remembered status/polling, and rejects older responses; original UUID and pending Send protection remain |
| Send unlocks before slow unrelated reads finish | Load-owned Send-only guidance covers exact/list reconciliation. Consume that pair before queue/catalog/audit waits or errors; old callbacks cannot unlock a newer refresh |
| Failed lookup or restored Settings/reload permits another send | Shared observation-in-progress and persisted unconfirmed guards survive departure. Failed/malformed/older reads cannot resolve an existing request; current valid evidence or supported explicit retirement clears the guard |
| Storage disappears before or after failed observation | Preserve the in-memory original as unresolved before early return; retain the same UUID before navigation or visibly block departure when persistence fails. Fresh retained evidence restores ordinary permission |
| Older legacy adoption clears current recovery evidence | Reject older same-operation/reference versions, changed acknowledged references and replacement of another unresolved intent; equal/fresh valid evidence remains usable |
| System test guidance says aggregate | Operational documentation now identifies the explicit System-only email view |

Final correction qualification: ten focused Node commands PASS, including all
eight selected actual email-navigation journeys; real SQL 9/9 PASS; Release
builds PASS with zero warnings/errors. Source/generated assets match recorded
hashes. Final R3b saved-email SQL/Chromium journey is 1/1 PASS with six clean
states, original lost-acknowledgement identity and one recording-provider POST
and worker job. R3a audit remains 1/1 PASS (26 clean states, 22 visible-focus
witnesses and four mobile queue cases); R3b changes neither audit logic/CSS nor
backend code, so those are retained receipts, not claimed repeated executions.
No live provider calls occurred. Actual receipts and nine-file freeze manifests
are external `i450-closure-*` evidence. The earlier full-frontend attempt was
interrupted for remediation and is explicitly incomplete, not passing evidence.

### Full-run fixture qualification

The first complete native run on `357bf94` executed all 1,423 discovered
non-browser identities: 1,419 passed, four failed, none skipped. The four new
weekly-recipient cases assumed no competing subscribers, while an earlier
profile test intentionally left the configured System super-admin opted in.
Production correctly includes that recipient in library summaries.

A test-only correction temporarily isolates captured competing opt-in flags,
then restores and verifies their exact SQL values in protected cleanup. It
preserves the full execution/assertion block, including zero/one whole-prefix
counts, suppression, audit and dispatcher checks; production source is unchanged.
The warm profile-plus-five-case reproduction failed four of six before the fix;
the same six UIDs passed afterward. The broader weekly/profile population passed
28/28 with no skips. Both passing TRXs prove profile execution first and exact
System subscription restoration after every case. Luna MAX, Astra HIGH and
Astra XHIGH independently cleared the correction. These focused passes do not
replace the required fresh complete rerun; the failed full receipt remains
explicitly unqualified in external evidence.

## Integration qualification

The consolidated PR records the immutable final head, fresh complete frontend,
non-browser .NET/SQL and browser execution receipts, full CI required gate,
selected WSLC acceptance results and final Astra XHIGH adversarial closure.
Counts come from current discovery and exact executed identities, with no
skipped required checks. Expensive suites run sequentially; acceptance pins one
application SHA/tooling snapshot/image and uses fresh disposable state per suite.

## Boundaries

No deployment, production tag, PR merge, live Polaris mutation, or live Postmark
send is part of this work. Human visual/screen-reader acceptance remains #398;
live Postmark validation remains #293. Broader Settings work (#438), Request
Activity (#429), and extra weekly-summary recipients (#441) remain separate.
