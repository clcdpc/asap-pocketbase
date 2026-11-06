# Polaris and Requests improvements (#439)

Branch: `codex/i439-polaris-requests`; integration base:
`4cdfdf7b9d29869d66685d968ac7a955e891d7fa` (`main`). The base includes
#411, #420, #421, #453 and #454. Work uses a separate checkout to preserve
the original checkout's four unrelated browser-test edits.

Each issue is implemented serially, followed by no more than two independent
review/fix passes: requirements review, then executed adversarial scenarios.
Routine regression checks and final evidence reconciliation are not new reviews.

| Issue | Implementation | Pass 1 | Pass 2 | Technical disposition |
| --- | --- | --- | --- | --- |
| #389 Polaris search and direct actions | `7ebc90b`: compact catalog UI and scoped direct actions | Luna MAX: 2 findings fixed | Astra HIGH + Terra MAX: 1 finding fixed | Technically qualified |
| #413 Already own Bib linking | `acc63cb`: atomic verified association/action; unrelated drafts retained | Luna MAX: 1 finding fixed | Astra HIGH: 2 findings fixed | Technically qualified |
| #423 Purchase/rejection confirmations | `62c4c17`: scoped inline-action toolbar/focus ownership; Confirm purchase | Luna MAX: no findings | Luna MAX browser scenarios: 1 finding fixed | Technically qualified |
| #426 Detail footer Close | `398bb8c`: secondary footer Close reuses guarded host dismissal | Luna MAX: 1 recovery finding fixed | Luna MAX actual-browser comparisons: no findings | Technically qualified |
| #424 Pickup help boundary | `1ad8c4d`: scoped accessible pickup disclosure | Luna MAX: no findings | Luna MAX actual-browser scenarios: 1 held-Escape finding fixed | Technically qualified |
| #425 Patron email link | `a569897`: normalized, safely encoded mailto recipient | Luna MAX: 2 findings fixed | Luna MAX executed browser/DOM matrix: no findings | Technically qualified |
| #396 Initial heading focus | `dbdc1f1`: startup-only queue focus distinction | Luna MAX: 1 test-coverage finding fixed | Luna MAX actual focus matrix: no product findings | Technically qualified |
| #442 Authorized workflow counts | `440606c`: complete scoped totals with explicit unknown states | Luna MAX: no findings | Astra HIGH executed scope/identity matrix: no findings | Technically qualified |
| #428 Requests table sizing | Final issue commit; exact SHA in consolidated PR | Luna MAX: no findings | Terra MAX visual + Luna MAX functional: 1 finding fixed | Technically qualified |

Implementation/reviewer models are selected explicitly per the task: Sol MAX
for #389/#413/#423/#442, Luna MAX for smaller implementations and requirements
reviews, Astra HIGH for workflow/authorization scenarios, and Terra MAX for
visual scenarios. The parent session cannot change its own model; orchestration
uses the current Codex session. No child-model substitutions are currently needed.

## Verification boundaries

- The original #389 reference PNGs are absent from the issue and its comments;
  rendered comparison uses the issue's detailed screenshot-derived specification.
- Human assistive-technology acceptance remains #398. Live Polaris acceptance
  remains #298. Deterministic automated fixtures do not establish those results.
- No issue closure, merge, deployment, host activation or production tag is part
  of this implementation.

## Focused evidence

Generated logs, browser PNGs and machine-readable receipts remain outside tracked
source under `.artifacts/i439` or the acceptance runner's normal evidence root.

### #389

Reuses #411's authoritative, atomic catalog-queue service. Adds compact cards,
all five modes, author shortcut, readable material metadata/icons, bounded
deduplicated holdings, scoped direct actions and persisted identical-command
recovery. Additional-copy confirmation explicitly discloses the automatic-hold
override. Real SQL verifies `AutoHold=true`, verified BIB, exactly one associated
task, reminder choice, audit/outbox and authoritative final status. Ordinary
Use BIB preserves the existing automatic-hold preference.

Focused staff frontend sweep: 43/43 files, zero failures/skips. Pass 1 fixed
root-relative icons and recovery Escape/focus; its focused Node/build/browser
checks pass. Pass 2 independently executed 86/86 SQL cases, three Node files,
eight integrity probes and actual Chromium mode/ownership/failure/stale/keyboard
scenarios. Terra inspected the eight rendered owner states and found one stale
count after rejected-action recovery; search now owns clearing and truthful
feedback. Final targeted Node and Release build pass (zero warnings/errors), and
the real-SQL browser owner passes 1/1 with nine captured states, zero serious or
critical axe findings, overflow, missing images, page errors or external requests.
Final receipt: `pass2-polaris-browser/polaris-search.trx`; visual/state evidence:
`polaris-search-25fced7c28714d9fa610243ab5ad5575/browser-results.json` under the local
evidence root. Failed initial fixture/focus receipts are retained. Exact role/
claim and persisted pending-operation variants received targeted lock-path
inspection in Pass 2; the final complete SQL/browser gate remains required.

### #413

Missing/invalid/unverified BIBs expose local linking guidance, existing lookup
controls and explicit completion inside the same request detail flow. Selection
alone does not mutate. The existing action saves association and workflow in one
transaction; unrelated drafts remain unsaved. Saved automatic-hold opt-out still
closes without a hold and uses truthful conditional wording.

Focused Node checks and Release build pass (zero warnings/errors); fresh SQL
passes 10/10. Pass 1 fixed a reload control that could become an action after a
lookup error; the red/green regression and actual browser prove recovery stays
read-only until authoritative refresh. Pass 2 found duplicate-recovery draft loss
and overwritten typing during delayed reload. Both are fixed, including failed
reads and Cancel during reload. The unchanged independent probe improves from
8/10 to 10/10; failed results remain retained. The expanded #413 browser owner
passes with eight clean accessibility/layout states (desktop, mobile and native
200%), including all recovery flags. Its combined checkpoint is retained as
1/2: #389 had an ownership-enrichment timing race in a test assertion. An explicit
ownership-complete setup wait fixes it; the focused #389 rerun passes 1/1.
Evidence: `i413-pass2/remediation-browser/i413-pass2-remediation-browser.trx`,
`i413-pass2/389-checkpoint/i413-pass2-389-checkpoint.trx`, and
`already-own-link-37f16f34c9a44668abff21e64c2b1fa6/browser-results.json` under the
local evidence root. Native actor/version races and UI role/scope/identity races
were executed; separate SQL role/library/claim changes were inspected only.

### #423

Purchase/Reject hide the competing toolbar while retaining title, status and X;
the final purchase action is Confirm purchase. Reminder/default, rejection
selection/delivery and backend policies remain intact. Assign, Pickup, Bib
linking and duplicate recovery share the title-detail visibility/focus owner;
persistent hold resolution stays unchanged. Cancellation restores toolbar/focus.

Pass 1 found no defect. Pass 2 used trusted Chromium gestures in the disposable
SQL/Kestrel fixture and found that held Enter could submit immediately after
opening Purchase. Scoped carry-over suppression fixes it while retaining fresh
Enter confirmation and textarea repeats. Final seven affected Node files,
Release build (zero warnings/errors), and #423 plus #413 browser owners pass
(2/2, zero failures/skips). Six trusted held-Enter cases make zero premature
action POSTs; deliberate confirmation and SQL status/history/outbox checks pass.
Eight desktop/mobile/native-200% scans have zero serious/critical axe findings,
overflow, missing images, page errors or external requests. Pass 2 also executed
real double-clicks, tab exclusion, dirty Reject discard/keep/cancel and pending
response close guards. A committed-success response loss was not separately
injected here; existing recovery contracts remain.

Final evidence: `i423-pass2/remediation/i423-pass2-remediation.trx` and
`request-confirmations-dfb3865ae3bb41c0abf8ca6dea20b47a/browser-results.json` under
the local root. Failed focused runs and the independent pre-fix probe are
retained. Temporary test instrumentation was restored byte-for-byte before the
implementation fix. Registered d1/i403/pr410 assertions are updated; their full
runner executions remain final integrated gates.

### #426

Secondary footer Close uses the same host dismissal as X and preserves Save,
Revert, pending-operation and unsaved-edit protections. It stays available on
closed requests and across detail tabs. Pass 1 found that unavailable-detail
fallbacks lost the footer; both now retain a guarded Close. Focused Node checks,
Web build (zero warnings/errors) and the browser/SQL owner pass 1/1 with thirteen
clean states, including both fallback branches.

Pass 2 independently compared footer/X/Escape for clean, dirty, pending, closed
and recovery states; checked safe connected heading focus, stale detached
callbacks, 390px and native 200% geometry. The corrected objective run passes
1/1 with 32 clean states and no app finding. Temporary probe setup failures
(navigation zoom reset, callback argument and overly narrow focus expectation)
are retained and are not app failures or extra review cycles. Source was restored
exactly to SHA256 `528aba548eb0cafebe2b3f5a788f8cb84996793f3bcd57c623f1f4a155967d30`.
Evidence: `i426/i426-request-confirmations-test-run-7.trx`,
`i426-pass2/native/i426-pass2.trx` and `i426-pass2/pass2-report.md` under the local
root. No implicit save, revert, workflow action or added notification occurred.

### #424

The full explanation belongs to the pickup cell's native disclosure; other patron
cells no longer trigger it. The required short text remains visible. Pass 1 had
no findings. Pass 2 independently exercised pointer boundaries, Enter/Space,
touch, SPA replacement and real wheel access to every clause at 390px and native
200% zoom. It found that repeated Escape could cancel an underlying Purchase
panel after closing help. The local keydown/keyup fence now owns that held press;
a fresh Escape retains normal workflow cancellation.

Final Node checks, zero-warning/error Web build and browser owner pass 1/1 with
17 clean states. Trusted repeated Escape closes only help, retains panel/modal
and focus, and posts no action; a fresh Escape cancels the panel normally.
Evidence: `i424/i424-request-confirmations-test-run-7.trx`,
`request-confirmations-83ce104702854834ae8559e873565d59/browser-results.json` and
`i424-pass2/pass2-report.md`. Failed receipts from redundant wheel-test geometry
are retained; that brittle extra assertion was removed from the keyboard-only
remediation after independent Pass 2 had already proved scrolling. Temporary
instrumentation was restored before remediation. All final states have zero
serious/critical axe findings, overflow, page errors or external requests.

### #425

Present summary email is a standard, underlined mailto link with safely encoded
recipient text; missing values retain Not recorded. Pass 1 fixed an encoded
structural address separator and blank links for whitespace-only values. Literal
regressions cover ordinary addresses, quoted local parts containing @, and URI
encoding boundaries. Node controller/details checks, browser syntax, Release
builds (zero warnings/errors) and the real-SQL browser owner pass 1/1.

Pass 2 independently executed null/empty/whitespace/omitted values, valid unusual
characters, quoted-local @, literal percent sequences, query/header delimiters
and markup-as-text. Actual pointer/Enter activation was intercepted without
launching a mail handler or posting an action. At 390px and native Chromium 200%,
long addresses wrap within their cells and keyboard focus has a visible 3px
outline. The instrumented owner passes 1/1 with zero serious/critical axe findings,
overflow, page errors or external requests; viewed screenshots are retained.
There were no findings. The harness was restored exactly to SHA256
`C8AC9BE49168CB3F4174B569A2F5AEB09C1B9EC218ECBDAE89A32B64F264A9DA`.
Evidence: `i425/i425-request-confirmations-test-run-2.trx`,
`i425-pass2/native/i425-pass2.trx` and `i425-pass2/browser/browser-results.json`.
The owner also retains the prior confirmation, Close and pickup-help checkpoints.

### #396

The exact pre-fix local build (`a569897`, Chromium 151.0.7922.34) reproduced the
orange 3px heading outline. At DOMContentLoaded focus was on body; the captured
stack then showed session startup navigating and focusing the queue heading.
Initial queue presentation now skips that focus call. Deliberate navigation,
other headings, skip links and dialog return keep their existing behavior and
global focus styles. The reported deployed DEV build was not contacted.

Pass 1 found one coverage gap: direct/reload measured final focus but did not
assert their recorded focus calls. Both now assert zero app heading-focus calls.
Web build (zero warnings/errors), shell Node and the strengthened browser owner
pass 1/1 with 21 clean states. Pass 2 independently passed delayed-session Tab
preservation, pointer/keyboard view entry, real browser Back/Forward, deliberate
heading focus/reload, deep-link dialog focus, Escape return and native 200% zoom.
Viewed screenshots confirm focus appearance; the final owner passes 1/1 with
zero skips, serious/critical axe findings, overflow, page errors or external
requests. Browser-history API calls followed pointer input; this does not prove
keyboard-history shortcuts. Keyboard view entry and dialog return were exercised.

Three temporary probe failures (retained separately) concerned keyboard starting
focus, an ignored headless Alt+Left shortcut and a missing probe in the separate
zoom context. The corrected final run passed; no product defect was found.
The harness was restored exactly to SHA256
`B9BA6F1AE8E07BC0C77B9262756E90307EA073E093C4BD3409969D68E09A6B6F`.
Evidence: `i396/final/i396-focus-final.trx`,
`i396-pass2/native-final2/i396-pass2-final2.trx` and
`request-confirmations-b7752c26a4eb459987d99893b91d1bee/browser-results.json`.

The bounded navigation checkpoint exposed five retained expectations for #413's
former global notice and #423's former competing panels. Test-only follow-up
`9b70388` checks inline validation, one active panel, explicit Cancel, retained
drafts/focus and no premature mutations. All 271 navigation journeys and the
separate dialog-focus file pass. The earlier 266/271 result is retained in tool
output and `i396/navigation-checkpoint-before.json`; no product behavior was
changed for that alignment.

### #442

Every title workflow tab includes its complete accepted dataset total, including
zero, independent of table filters and pagination. Loading/unavailable values
stay distinct from zero; scope, identity and stale-result guards own publication.
Existing server-normalized super-admin scopes remain authoritative, while ordinary
users resolve to their own library. The existing complete list query is reused;
there is no new endpoint, service or schema. Count help remains visible, names
include totals and an already-focused tab stays fully visible after names widen.

Initial complete frontend discovery ran 73 files: 68 passed and five failed.
The overly strict new scope check was corrected, and all five affected files pass
targeted reruns, including 271 navigation journeys. Separate follow-up `f5bfa6d`
renames a private catalog function without changing its API and supplies current
quick-create fixture owner hooks; the native-dialog guard remains intact. This
does not claim a fresh complete 73/73 run; final Full CI supplies that gate.
Release builds have zero warnings/errors, focused controller/Grid.js checks pass,
real-SQL scope passes 1/1 and the final browser owner passes 1/1 with eight clean
states. Actual Purchase persistence, status_changed audit, provider refresh and
authoritative count transition pass. Visible help, dialog focus, mobile and native
200% focused-tab geometry were measured before axe; screenshots were inspected.
Earlier fixture/probe failures and the real help/tab-visibility corrections remain
recorded. Final initial receipt: `i442/i442-browser-counts-retry6.trx`.

Pass 1 had no findings. Pass 2 independently executed ordinary API attempts for
All, another library, System and nonexistent scopes: all returned only the user's
own library. A held old All response could not replace newer library totals.
Failed/malformed targets recovered, same-ID email/role/library changes canceled
obsolete refresh and preparation, and an older refresh could not replace an
accepted prepared target. Native SQL/browser passes 1/1 with zero skips; augmented
Node probes pass. Eight scans have zero serious/critical axe findings, overflow,
missing images, page errors or external requests. No findings remained. Identity
replacement was exercised at controller ownership boundaries, not live Entra.
Both instrumented files were restored exactly before commit. Evidence:
`i442/i442-sql-scope-initial.trx`, `i442/frontend-initial.json`,
`i442-pass2/native/i442-pass2.trx`, `i442-pass2/node-pass2.txt` and
`i442-pass2/browser-evidence/browser-results.json`.

### #428

The current local Release build at `440606c` reproduced the reported small-table
problem: Closed with two rows and Placed with one had a 1421px table in a 1420px
region at a 3000px viewport. Grid.js converted each percentage independently to
integer inline widths. A fractional-input tweak was rejected because its parser
truncated it. Title columns now retain the original 14/23/8/10/10/9/9/17 percent
proportions in scoped native CSS; Grid.js receives static column identifiers and
keeps autoWidth disabled. All eight columns, sort and action definitions remain.
No vendor, global-grid, text-size or overflow-masking change was needed.

Five focused frontend files and zero-warning/error Release builds pass. Initial
native layout passes 1/1: all stages fit 1420/1420 at 3000px and 1360/1360 at
1440px. One mounted table retains its identity through desktop/mobile/down/up
resizes. At 900/390px the 1080px minimum remains within 842/366px scroll regions;
true native 200% gives a 712px CSS viewport and a 688px region with no document
overflow. Screenshots and before-axe geometry are retained; native captures use
CDP and recorded zoom metrics. The original deployed DEV build was not contacted,
so its SHA and original screenshot comparison remain unavailable.

Pass 1 had no findings. Within the single Pass 2, Terra independently passed all
five stages plus combined catalog/confirmation/help/BIB/mailto/Close/count/focus
views at desktop, mobile and native zoom: 12 base plus 15 combined scans had zero
serious/critical axe findings. Luna found that new CSS priority blocked the
existing real column-resize helper. Removing the unnecessary priority restores
normal inline overrides while preserving fractional defaults. A permanent test
now proves Actions at 231.234px normally/reset, 952.031px at 70% and 163.219px at
12%, with six direct actions at the wide setting and one direct/five overflow at
the narrow setting. More Enter/Space/Escape, Assign open/cancel, clean footer
Close to the connected title link and zero mutations pass. The final native
owner passes 1/1 with zero skips, all layout/axe/image/error/external guards clean.
No new independent review followed remediation.

Probe failures concerning committed-frame waits, expected overflow order, native
screenshot framing and the established title-link focus port are retained. The
pre-axe BIB-search measurement showed an unobscured center and only a 0.234px
fractional border edge; no product focus change was made. An existing duplicate
empty-stage message was observed and left outside this sizing change.
Evidence: `i428/frontend-tests-20261105/focused-tables.log` (Node text log),
`i428/test-final-20261105-04/i428-layout-final.trx`,
`i428-pass2/visual/native/i428-pass2-visual-r02.trx`, the functional failed
receipts, and `i428-pass2/remediation-20261105-05/i428-layout-remediation.trx`.
Final geometry/screens: `i428/title-layout-94e06b0647ff48988df765ba496d9786/`.

## Integrated qualification

The first exact-revision acceptance invocation at `42b3244` passed i390, i403,
S1, Q1 and PR410. D1 passed five scenarios and failed its toolbar check because
the old dialog-wide `summary` selector counted #424's pickup-help disclosure as
an action overflow menu. The selector now checks toolbar disclosures and retains
the dialog-wide `.request-more` guard, all six direct-action checks, keyboard
order and responsive geometry. No application behavior changed. The failed
invocation remains retained outside the worktree. Final qualification reruns all
six selected suites against one clean updated application/tooling snapshot.

The first forced Full CI at `9296c60` passed frontend and all 1,448 non-browser
.NET/SQL tests; browser passed 50/52 with zero skips. Its two retained-test
assumptions were corrected through objective failure reproduction. Follow-up
`38c5d4f` keeps the BIB detail fixture stable across #389's holdings prefetch and
explicit selection; the complete staff owner then passes 1/1 with zero skips.
The native-zoom More assertion passed unchanged on Windows but failed in Linux
with a 0.1796875 CSS-pixel right-edge difference, restored focus and a matching
center hit. Its focus-visibility bounds now use the existing `visibleEvidence`
one-pixel tolerance and retain clipping, focus and hit-test guards. Strict #428
wide-table sizing remains intact. A temporary Linux I403 probe passed all six
scenarios, including native 200% at both 1280 and 1440 widths; its source was
restored byte-for-byte before final qualification. The original failed CI and
probe receipts are retained. The final permanent native-zoom owner passes 1/1
with zero skips. This is integration remediation, not another
independent review pass. Evidence: `ci42088055100/remediation-staff-r01/`,
`ci42088055100/remediation-zoom-r01/`, `ci42088055100/remediation-zoom-r02/` and external acceptance invocations
`7fc4aa2437204be6bb6479fdf57588c1` (failed strict Linux probe) and
`3dc9de87e70d47898ce2aa040f586d0c` (passing corrected probe).

All nine focused two-pass qualifications are complete. Exact final revision,
registered acceptance-suite results, full frontend/.NET/SQL/browser discovery and
Full CI/package gates are recorded in the consolidated PR after execution. Those
results use current discovered identities and manifests, not guessed totals.
Human assistive-technology and live-provider boundaries above remain separate.
