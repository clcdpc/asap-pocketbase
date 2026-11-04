# Settings improvements (#438)

One serial implementation branch, `codex/i438-settings-improvements`, starts at
`7aa0865e1200a1103900897ff3f3672e5d1873fd` on `main`. Merged #420 (`50f1483`),
#421 (`c168c38`) and #453 / Operations #450 (`7aa0865`) are in the base.
Administration audit remains under Operations. The original checkout's unrelated
browser-test edits are preserved in place.

The [pinned Staff access reference](https://github.com/clcdpc/asap-pocketbase/blob/e515d2619ec5887b20acffbb8d514df7ac815ddc/docs/design/staff-roster-mockup.html)
governs #435; its unified save and single email supersede conflicting older #406
requirements. #433 follows approved Decision A: Polaris owns immediate confirmed
participation edits; Getting started has only summary/navigation. #436 covers
focused Requests semantics without absorbing the broader #439 workstream.

## Issue qualification

Counts below describe actual completed runs, not invocation or planned coverage.
The parent session coordinates serial implementation, independent review,
remediation, evidence and issue-specific commits. Requested implementation and
review models are selected explicitly for child agents; orchestration remains in
the parent Codex session.

| Order | Issue | Acceptance and tests | Independent review | Commit |
| --- | --- | --- | --- | --- |
| 1 | #395 | Header compacted; focused DOM/scope checks and SQL/browser presentation PASS | Luna MAX clear after retained-test correction | `78b0943` |
| 2 | #448 | Complete Branding fieldset moved first; focused DOM/logo and SQL/browser presentation PASS | Luna MAX clear, including rendered desktop/mobile | `0c8ccef` |
| 3 | #445 | Redundant paragraph removed; focused DOM/picker/scope and SQL/browser tests PASS | Luna MAX clear, including desktop/mobile gap check | `35de01e` |
| 4 | #449 | Explicit validated URL draft action; focused controller/layout/scope PASS, links 2/2 and SQL/browser 1/1 PASS | Luna MAX clear after wait/identity test corrections and rendered checks | `dde686a` |
| 5 | #409 | Credential help/local outcomes; Node, SQL 3/3, browser 1/1 and checkpoint navigation 271/271 PASS | Luna MAX / Astra HIGH clear after P2 and recovery-title corrections | `2b4068a`, `33288f3` |
| 6 | #437 | Flag-only masks; Node 13 files, initial SQL/browser 4/4 and final affected browser 1/1 PASS | Luna MAX / Astra HIGH clear after P3 wording correction | `8b3e76e` |
| 7 | #446 | Named groups/native gates; Node 13/13, Release build and final SQL/browser 1/1 PASS | Luna MAX / Astra HIGH clear after coverage/wait/width corrections | `1f40c91` |
| 8 | #447 | Effective conditional options; Node 13/13, Release build, checkpoint 4/4 unaffected and final SQL/browser 1/1 PASS | Luna MAX / Astra HIGH clear after focus/test-wrapper corrections | `3ec9a0d` |
| 9 | #444 | Nearby single-token picker; focused Node, Settings 13/13, Release and final SQL/browser 1/1 PASS | Luna MAX clear, including actual open-popup and inherited renders | `d70170e` |
| 10 | #399 | Cards/previews/contextual insertion; Node, eight unique checkpoint owners, final Staff/Campaign 3/3 and native-focus 1/1 PASS | Luna MAX / Astra HIGH / Terra MAX clear after recorded persistence, feedback and focus corrections | `43efa9b` |
| 11 | #435 | SQL 18/18 and complete Staff 74 states/16 roster flags/eight native proofs PASS; shared accessibility corrections verified in #436 | Luna MAX / Astra HIGH clear; Terra actual desktop/mobile/dark/native renders clear | `3054851`, corrections in #406/#436 |
| 12 | #406 | Filter/counts/preserved drafts; complete Staff PASS; final shared notice/pointer/focus checks PASS in #436 | Luna MAX / Astra HIGH clear; Terra actual major and eight native renders clear | `a8c0e7d`, corrections in #436 |
| 13 | #433 | SQL/browser 2/2 PASS, 14 states; Library summary contrast corrected and rechecked in #436 R1 | Luna MAX / Astra HIGH clear; Terra actual groups and both summaries clear | `99469b7`, contrast correction in #436 |
| 14 | #436 | Focused R3 and Node checks PASS; carried 436-6 independently reproduced and corrected as C438-1 in the final integrated audit below | Luna MAX / Astra HIGH / Terra MAX issue reviews clear; fresh Astra XHIGH verifies final keyboard correction | `7d1b247`, final audit correction below |
| 15 | #391 | Actual before/after spacing; seven Node files, Settings/native R2 and final Presentation R3 PASS; supplemental visible-logo/Library-roster results tracked in PR #454 | Luna MAX source and Terra MAX actual matrix clear; integrated results remain separately identified | `406b0ca` |

## Environment and integration

- Initial Release solution build: PASS, zero warnings/errors.
- Real-SQL bootstrap `PolarisSettingsRejectInvalidIdentitiesRetiredContextAndLibraryWrites`:
  PASS, 1/1, no failures/skips. This establishes the local SQL/HTTP harness;
  it is not final implementation qualification.
- Node dependencies and Chromium installed; .NET SDK 10.0.303 / Node 24.19.0.
- Acceptance runtime preflight: WSLC 3.0.3 and disposable `hello-world` PASS.
  This verifies the local container prerequisite; it does not qualify a suite.
- Before backend changes, authoritative native discovery at `33288f3` verifies
  the exact disjoint union: 1,423 non-browser + 46 Browser = 1,469 cases.
  These are discovery counts, not a full execution result. The external
  baseline catalog supports comparison with the final population.
- Real SQL Server 2022 Developer 16.0.1210.3 (metadata explicitly verified) and deterministic provider boundaries are used for
  mutation and browser journeys. No live Polaris/Postmark actions are part of
  ordinary qualification.
- Final combined qualification and its current status are maintained in
  [PR #454](https://github.com/clcdpc/asap-pocketbase/pull/454), bound to the final
  source revision: complete frontend, authoritative .NET/SQL and browser
  populations, sequential acceptance suites, final independent audit and CI.
  Counts below describe focused checkpoints and do not substitute for that gate.
- Human assistive-technology acceptance remains #398; local automation and
  visual review do not claim NVDA/JAWS/VoiceOver acceptance. Live-provider and
  deployment qualification remain separate.

Raw local receipts and requirement snapshots are outside Git under
`C:\Users\mfields\.codex\tmp\asap-i438\`. Only concise final evidence belongs
in this record and the consolidated PR. No issue or dependency PR is merged or
closed by this task.

## #395 — compact Settings header

The redundant subtitle and its controller binding are removed. Status remains
the same polite live region, beside the scope selector and Refresh. Desktop has
one grid row; narrow layouts stack the heading and toolbar. The separate library
context and permission guidance remain available.

- Focused `settings_accessibility_layout.test.js` and `settings_staff_scope.test.js`:
  PASS, including independent reviewer reruns. Browser-script syntax and diff checks PASS.
- Incremental Release build: PASS, zero warnings/errors.
- Retained real-SQL `StaffPresentationBrowserPreservesHeaderNavigationLookupOrderAndTabGeometry`:
  final R2 PASS, 1/1, no failures/skips. Its 56 rendered states include 12 header
  scope/viewport contexts (System/library, 1440/1280/390px, overlay/persistent
  scrollbar variants). Empty desktop height is 40px with one grid row; a long
  error wraps in the same row at 79.8125px. Mobile stacks two rows and preserves
  the complete error. Axe serious/critical, missing images, external requests,
  page errors and page overflow are zero.
- An initial source-only Chromium check also verified native zoom 2 with wrapped
  status/no overflow. Its duplicate standalone test was removed after moving
  geometry and screenshots into the CI-owned presentation journey. That earlier
  invocation is supplementary evidence, not a retained test-file coverage count.
- Parent review caught that the initial standalone regression was not invoked by
  CI. The retained journey now owns the geometry checks and long-error scans,
  restoring injected status in `finally`. Independent Luna MAX final review is
  clear, with no actionable findings. Its separate Chromium comparison against
  base measured the empty header shrinking from 65.47px to 40px and the long-error
  header from 123.81px to 79.81px; following content moved up by the same amounts.

Raw R2 receipt: `results/i395-presentation-r2.trx` under the external evidence
directory. Captures/report are in the ignored browser artifact directory
`staff-presentation-6aefd6831cd9403d95b97e7de2122ffc`.

## #448 — Branding first in Patron experience

The complete existing Branding fieldset is moved unchanged immediately after the
panel introduction. Public patron portal, Patron text, and Publication options
and formats retain their relative order. No JavaScript, CSS or persistence path
changes are needed.

- Focused DOM/layout and multipart-logo tests: PASS, including independent Luna
  reruns. Browser-script syntax and diff checks PASS.
- Incremental Release build: PASS, zero warnings/errors.
- Retained real-SQL presentation browser: PASS, 1/1, no failures/skips. New checks
  verify all eight Branding IDs remain together, visual/DOM section order,
  keyboard entry with visible focus, and desktop/mobile System/library placement.
  Existing axe/image/overflow/traffic/page-error guards remain active.
- Parent and independent source review confirm an exact block move. Independent
  Luna MAX inspected desktop/mobile renders and all 12 placement contexts and
  found no actionable defects. The run contains 68 scanned states; guards report
  zero serious/critical axe findings, missing images, external requests, page
  errors or page overflow. The library keyboard witness enters its Branding
  override control, retaining inherited read-only behavior.
- Receipt: `results/i448-presentation.trx`; actual captures/report are in ignored
  `staff-presentation-56868cd00897467f8d556780bbe04f25`.

## #445 — remove redundant patron-code callout

Only the standalone “displayed as written” paragraph is removed. The existing
message label/textarea, workflow binding, eligibility controls and useful picker
guidance remain intact; no replacement copy or placeholder picker is added.

- Focused layout, patron-code picker and scope Node tests: PASS. Independent Luna
  also reran settings-save-completion successfully. Browser-script syntax and
  diff checks PASS.
- Frontend assets refreshed through the existing MSBuild CopyFrontendToWebRoot
  target; no compiled backend source changed.
- Retained real-SQL presentation browser: PASS, 1/1, no failures/skips. It checks
  callout absence and the retained message label in both scopes while repeating
  the header, Branding, accessibility, image, overflow and traffic safeguards.
- Independent Luna MAX source and rendered desktop/mobile review is clear.
  It verified the paragraph and its reserved space are gone, with the labeled
  editor retained. The final browser receipt has 68 scanned states and zero
  serious/critical axe findings, missing images, overflow, external requests or
  page errors.
- Receipt: `results/i445-presentation.trx`; actual captures/report are in ignored
  `staff-presentation-ec394271bf4747d09a9fa157c8b51abe`.

## #449 — suggest the current Staff URL

The explicit Use current address action displays a candidate built from the
browser origin plus `/staff/`. It strips credentials/query/fragment, preserves
nondefault ports and accepts HTTPS or the existing exact HTTP loopback hosts.
It never promotes HTTP to HTTPS. Choosing it dispatches normal input/change
events; ordinary Save settings persists the draft. Loading never applies it.
System-only, active actor/panel, loading, inert and mutation guards remain in
place. Applying an already-saved match leaves the form clean with truthful
feedback.

- Existing generated-link contracts: exact discovery and PASS 2/2, no failures
  or skips (`results/i449-link-contract.trx`).
- Focused controller/email, layout and scope Node files: PASS, including
  independent Luna reruns. Cases cover configured/blank values, credentials,
  query/hash, ports, HTTPS, exact HTTP loopback including IPv6, unsupported
  origins, inert/library contexts, normal payload/save/reload, and an already
  matching saved value. A final direct actor-replacement regression is also
  green: a valid stale action cannot alter a distinct manual draft or emit
  input/change events/POST before the new accepted load.
- Assets refreshed through the existing MSBuild target. Retained real-SQL
  presentation browser: PASS 1/1, no failures/skips; 76 scanned states, six System
  URL keyboard contexts plus library disabled checks, 12 prior header and 12
  Branding contexts. It exercises keyboard activation, dirty/no-write behavior,
  refresh cancellation/discard and long mobile candidate wrapping. Axe serious/
  critical, missing images, external requests, page errors and overflow are zero.
- Review identified an early refresh wait in the already-matching test branch;
  it now awaits the GET response and refresh completion. Luna confirmed the
  timing gap independently. Luna also requested direct actor-replacement
  coverage; that meaningful regression was added and passed. Final Luna MAX
  verification is clear, including the corrected wait, a fresh actor-regression
  rerun, and actual desktop/mobile/long-candidate renders. No actionable finding
  remains.
- Raw browser receipt: `results/i449-presentation.trx`; actual captures/report
  are in ignored `staff-presentation-861b1ec9f91041a6949e9ae1372923b3`.

## #409 — Polaris credential status and local action feedback

Implementation uses Sol MAX under the task's explicit complexity exception:
diagnostic display shares cancellation and reload paths with committed and
uncertain organization mutations. Functional review remains independent Luna
MAX; security/data-integrity review uses Astra HIGH.

Saved/missing help reads only accepted `hasApiKey` / `hasAdminPassword` booleans.
Replacement values stay blank on load; saved presence does not claim current
connectivity. Actions and local feedback form one right-aligned responsive group.
The form becomes inert during sync, so a separate hidden live region outside
the form announces local pending/results without duplicate visible notices.
Existing receipt, configuration/catalog callbacks and recovery ownership remain.

- Six focused Node scripts PASS: feedback, diagnostic, Staff Access, layout,
  save-completion and mutation-scope race. Browser-script syntax and diff checks PASS.
- Exact focused real-SQL/HTTP discovery and execution: PASS 3/3, no failures or
  skips; covers setup readiness, invalid/retired/library Polaris writes and
  administration/settings HTTP scope (`results/i409-sql.trx`).
- Astra HIGH initial audit was clear. Three independently rerun Node files
  and extra failed-preload, AbortError, late committed and uncertain-owner probes
  pass. It verified blank/library payloads, accepted flags, unchanged protected
  secret/auth contracts, receipts, reload-required/uncertain guards and live
  region retirement.
- Browser R1 failed an existing tab-focus assertion after accepted sync correctly
  invalidated Queue data. Queue review completed late and moved focus during the
  assertion. The test now awaits Queue visibility, refresh completion and quiet
  feedback before the unchanged focus/offset checks. This is a test timing fix;
  callbacks and focus styling are retained. R1 remains a failed receipt, not a pass.
- Browser R2 passed 1/1 after the navigation-wait correction (124 scanned states),
  before the credential-help correction below; it is historical qualification.
- Luna P2: first credential replacement could be confirmed saved, then GET 503
  left the help claiming no secret was saved. The regression reproduced this
  before the fix. Only after exact acknowledgement and current-owner validation,
  submitted replacements now receive explicit saved/reload-needed help; accepted
  presence flags and secret input values are untouched. An authoritative GET
  replaces the hint. Both-credential committed/rejected/uncertain cases and later
  successful refresh pass. Astra independently verified those cases plus single-
  credential, blank/library, later false-flag and retired-owner probes; final
  security re-review is clear.
- Final retained real-SQL browser R3: PASS 1/1, no failures/skips, 130 scanned
  states. Six Polaris System contexts cover saved/missing replacements,
  first-save/failed-refresh recovery, diagnostics, sync pending/success/rejected/
  uncertain/committed-refresh-failed outcomes, keyboard, desktop/mobile and CSS
  200% wrapping. Library disabled checks and previous header/Branding/URL checks
  remain green. Deterministic endpoint responses exercise UI outcomes; the
  separate real-SQL/HTTP tests above verify unchanged server authority. All
  axe/image/overflow/traffic/page-error guards pass.
- R1: `results/i409-presentation.trx` (failed); R2 and final R3 each retain their
  own external log/TRX. Final captures/report are in ignored
  `staff-presentation-d03961c7ddbd4460b97e071d25a91c88`. Luna final independent
  source/TRX/report and desktop/mobile/200% rendered review is clear, including
  the P2 regression and Queue focus wait. No actionable findings remain.

## #437 — saved-secret placeholders

All three password replacement fields use the same fixed `********` placeholder
only for a strict accepted saved-secret flag. Their actual values remain empty
on population. Postmark Clear suppresses its retain-current placeholder, cancel
restores accepted presentation, and typing a replacement cancels Clear. Accessible
help stays separate from the visual placeholder. Confirmed and uncertain Postmark
mutations retain truthful recovery guidance until an authoritative reload.

- Initial focused Node checks and the broader retained Settings sweep PASS all
  13 current Settings frontend files. Browser and source syntax/diff checks PASS.
- Initial real-SQL/HTTP/browser checkpoint PASS 4/4, zero failures/skips:
  full Settings round-trip, cumulative Staff presentation, recording-Postmark
  delivery/Operations same-reference journey, and protected Postmark blank/clear/
  secret-omission contract. Raw receipt: `results/i437-checkpoint.trx`.
- The initial browser reports cover two full-Settings states, six email/Operations
  states and 130 cumulative presentation states, with no serious/critical axe,
  missing images, overflow, external requests or page errors. Captures are in
  ignored `settings-369180b0d32a4362a867520cb565de27`,
  `saved-email-95c2038b0c9f45f082d2c4c53904ceb5` and
  `staff-presentation-f2bf0585eb1c4dc09edd3ab69c51469b`.
- Astra HIGH independent review is clear. Three focused Node files and extra
  strict-flag, clear/replacement, committed/uncertain failed-refresh and retired-
  actor probes PASS. No protected-secret, scope or recovery contract changed.
- Luna MAX found a P3 status-copy defect after the initial checkpoint: Clear with
  an accepted missing token said a stored token was pending removal. Guidance
  now branches on accepted presence and explains that saving a missing-token
  clear keeps it unconfigured. New tests cover missing-token clear/cancel and
  a first typed draft discarded by Clear, retaining the explicit clear payload.
  Focused Node reruns PASS; both independent source re-reviews are clear, and
  Astra independently reran the complete credential-feedback test.
- Final affected real-SQL Settings journey PASS 1/1, zero failures/skips
  (`results/i437-settings-final.trx`), after refreshing the corrected assets.
  It executes the new clear/cancel/first-draft cases and exact unrelated-save
  secret omissions, followed by existing System/library round-trips. Both final
  desktop/mobile scans are clear. Actual final captures/report are in ignored
  `settings-8d532a5cb8604c4f95dd3870d8b46809`. The initial four-case receipt
  remains pre-copy-fix evidence for unchanged credential, Operations and prior
  issue behavior; it is not reported as the corrected wording's final run.
- Final Luna MAX closure independently inspected the corrected source, Node
  results, final TRX/report and representative actual desktop/mobile renders.
  Both functional and security reviews are clear; no actionable finding remains.

## Credential-checkpoint navigation regression (#409)

The [draft checkpoint CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41740465554)
at `8b3e76e` passed its build and 107 SQL-independent native cases, then failed
one of 69 executed frontend files. Three of 271 navigation variants still
expected uncertain synchronization feedback in the global Settings message,
although #409 deliberately routes it to the local operation result and separate
announcer. The application retained its uncertain/reload-required barrier.

#446 implementation was paused with its partial draft preserved. The initial
correction changed only navigation-test expectations: Sync uses its local
feedback/announcer, activation retains the global message, and both explicitly
check the uncertain title and inert editor. Existing no-catalog-refresh,
no-false-success and no-replay assertions remain intact.

The isolated R1 run then exposed a P3 application projection defect: all three
sync variants passed, while the three activation variants found “No changes”
in the save bar despite the correct uncertain/inert interaction barrier.
Activation cleanup now refreshes dirty/recovery presentation only for its still-
current context, matching Sync. Receipts, callbacks, acknowledgement validation
and replay blocking are unchanged. R1 remains FAIL 3/271 in
`results/i409-navigation-correction.log`. Independent source re-reviews are
clear; the parent is rerunning all 271 journeys against a Git archive of exact
committed `8b3e76e` frontend source plus only the one-line production correction
and corrected test, isolating the paused timeout draft. R2 PASS all 271 journeys,
exit 0, in `results/i409-navigation-correction-r2.log`. Astra independently
verified the export contains exactly the one production line and that retired
contexts cannot repaint. The tested production file's Git-normalized blob
matches the staged source (`cc8ad988aa635a321afbd0ff7fcc17c891899e44`);
the Windows export uses CRLF while the index stores LF. Final Luna MAX source
and raw-receipt inspection is clear, including all six malformed-ack cases.
No actionable finding remains. The timeout draft is excluded from this commit.

The correction's [fresh draft run](https://github.com/clcdpc/asap-pocketbase/actions/runs/41744104122)
initially stopped before application tests in the unchanged Linux collector
contract `test_stalled_sql_does_not_stall_host_or_leave_client_after_failure`:
it observed three host samples during its fixed 0.4-second workload where it
expected four. The failed jobs were retried without changing application,
collector, timing assertions, or runner resources. Retry PASS: 141 Python checks
with one pre-build population-discovery class skipped, Release build with zero
warnings/errors, 107 SQL-independent native cases with zero failures/skips,
and all 69 frontend files, including all 271 navigation journeys. Full CI and
test-IIS activation remain skipped for this draft checkpoint. This does not
replace final full-population qualification.

## #446 — timeout groups and native dependencies

Four named rule groups retain their dependent controls; auto-rejection contains
a nested email/template group. Native disabled states compose each rule with
the dependent field's own override permission. Checked inherited parents remain
effective. Turning a rule off preserves its values and subordinate selections.
For timeout fields, removing Override displays the authoritative System value;
re-enabling it restores the local draft. Accepted population retires that cache.
System workflow population no longer falls back to the raw Library row, including
when the actual System value is null. Server timeout policy and schema are unchanged.

- Focused scope/layout Node tests PASS; parent broader Settings sweep PASS 13/13,
  zero failures/skips (`results/i446-settings-frontend.json`). Independent Luna
  reran both focused files successfully. Astra's extra null/inherited/nested-
  email/ownership/rejected-save/cache/pending-state probes PASS.
- Initial build failed on an undefined test-fixture context factory. It now
  resolves a local factory through the existing application services pattern.
  Build R2 PASS, zero warnings/errors (`results/i446-build-r2.log`).
- The retained real-SQL Settings browser uses an owned nonblank rejection
  template and restores initial timeout values/references in its fixture cleanup.
  It checks every dependency, System off/save/reload, nested email false/save/
  reload with the selected template retained, Library false/true/null reset,
  independently owned dependents, draft restoration/retirement and keyboard use.
- Browser R1 failed an early scope-ready wait that accepted the still-visible
  old scope. The waiter now requires the requested accepted scope as well as
  visibility, preserving its error and final scope assertions. No sleep is added.
- Luna's review found missing persisted email-off evidence; the actual SQL
  save/reload assertion above closes it. Browser R2 then PASS 1/1 with four
  System/library desktop/mobile scans and all existing full-journey flags.
- Actual rendered review exposed a P2 select-width defect despite the R2 page
  overflow guard: a long template name pushed its select outside the timeout
  card and clipped it on mobile. Parent and Luna independently confirmed it.
  Scoped control sizing now constrains only non-checkbox inputs/selects. Each
  final scan also checks all five visible controls inside their label, rule and
  viewport, preventing global `overflow-x: clip` from masking this regression.
  The focused layout rerun, browser syntax and diff checks PASS.
- Raw R1/R2 receipts remain distinct under `results/i446-settings-browser*`.
  R2 captures/report are in ignored `settings-c32f1d4079254ca49cc85ab71789b7dc`;
  they document pre-width-fix behavior and are not the final visual qualification.
- Final R3 PASS 1/1, zero failures/skips, in
  `results/i446-settings-browser-r3.trx`/`.log`. Four final states have zero
  serious/critical axe findings, missing images, page overflow, external requests
  or page errors. All five controls fit their labels, rules and viewport in each
  state (20 containment checks); both timeout round-trip flags are true.
  Actual final captures/report: ignored
  `settings-3b50a6125c4c45e2a9fab81eb06ded46`.
- Final independent Luna MAX inspection of the source, raw receipt and all four
  actual renders confirms the P2 width defect is resolved. Astra HIGH inspected
  the final receipt and containment data; prior inheritance/ownership/probe and
  fixture conclusions remain clear. Both reviews have no actionable findings.

## #447 — conditional Common creators options

The heading, feature checkbox and its Override remain visible. A mounted native
fieldset hides and disables the complete dependent editor when the displayed
effective flag is false. Revealing it preserves each descendant's existing
permission, scalar override and list ownership. Turning the Library feature
Override off displays the authoritative System flag; turning it back on restores
the local boolean draft, including false. Accepted population retires that cache.
Focus inside disappearing options returns to the feature checkbox or its editable
Override after the editor becomes interactive. Hidden retained list handlers
cannot add, reorder, delete or change Use system list ownership.

- Focused scope/domain/layout Node files PASS; the broader Settings sweep PASS
  all 13 files (`results/i447-settings-frontend.json`). Production and root Node
  sources stayed frozen through qualification. Release R2 build PASS with zero
  warnings/errors (`results/i447-build-r2.log`).
- Parent source inspection corrected an inverted deferred-focus guard before
  qualification. The accepted-inert-refresh regression exercises the corrected
  focus path. Astra found the owning C# wrapper still expected four scan states;
  it now requires the exact eight states and both creator round-trip flags.
- Workflow checkpoint executed five real-SQL/browser cases. Both Campaign2
  role cases, Library metadata and cumulative presentation PASS (4/4). The
  presentation journey retains 130 scans across prior issue states and viewport
  variants; artifact `staff-presentation-3ca17ae8d41f400992eee12d481d0d13`.
- The checkpoint's full Settings case failed a test API mismatch: Playwright
  reports `isDisabled()` false for a fieldset although its native disabled
  property, `:disabled` selector and child controls correctly report disabled.
  The test now checks native fieldset state plus scalar/Add child interaction
  barriers. No production change or weaker visibility-only assertion was used.
  The failed receipt remains `results/i447-workflow-checkpoint.trx`.
- Final affected Settings R2 PASS 1/1, zero failures/skips, 69 seconds
  (`results/i447-settings-browser-r2.trx`). It persists nonempty System and
  Library lists while off, checks exact payloads/reload, ownership, hidden
  handlers, parent reset/draft restoration and keyboard/focus behavior, then
  restores both initial snapshots through the real editor and SQL APIs.
- All eight final states have zero serious/critical axe findings, missing
  images, horizontal page overflow, external requests or page errors. Mobile
  creator controls fit their region. Timeout round-trip flags remain true.
  Actual final renders/report: `settings-4bc0b8ca537a4f80bca699ed7decf6ec`.
- Astra HIGH final independent review is clear after source and receipt
  inspection, including inherited/owned-empty/nonempty list probes, forced
  hidden Use system events, detached row handlers, authoritative false/null
  values and cache retirement. Luna MAX independently inspected the final
  source, three focused Node results, raw receipts, cumulative presentation
  and all four actual mobile on/off renders. Functional closure is clear;
  no actionable finding remains in either review.

The pushed Workflow checkpoint at `3ec9a0d` also passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41755614480):
Release build with zero warnings/errors, 107 SQL-independent native cases with
zero failures/skips, and all 69 discovered frontend files. Its 141 Python
contract checks include the expected pre-build native-population class skip;
this is distinct from native execution, which has no skips. Full CI and IIS
activation are skipped for this draft checkpoint. `main` was re-fetched and
remains `7aa0865`; a fresh search still found no new #439 implementation PR.

## #444 — suggestion-limit placeholder picker

The message has one nearby Insert placeholder control, offering only
“Next available submission date/time — {{next_available_date}}”. The detached
bottom callout is removed. The textarea retains its Workflow metadata, default/
override and global save path; only presentation controls are excluded from
dirty serialization. Activation rechecks the remembered range, text, scope,
actor, Override, connectedness and editability. Native insertion participates
in browser history; the existing selection fallback still dispatches input.
Runtime date substitution and business-time-zone policy are unchanged.

- Four focused Node files PASS, including independent Luna reruns: scope,
  layout and existing Patron/email placeholder helpers. Parent broader Settings
  sweep PASS 13/13 (`results/i444-settings-frontend.json`). Release build PASS
  with zero warnings/errors (`results/i444-build.log`).
- The retained SQL/browser flow checks clean open/Escape/no POST, exact selected
  replacement, focus, native token Undo/Redo and restored selection, every
  adjacent-typing Undo/Redo step, System and Library save/full reload, temporary
  override reset, hidden stale options and final snapshot restoration.
- Initial browser receipts remain distinct. R1 failed in unconditional cleanup
  saving an already-clean restored value and masked the primary error. Cleanup
  now saves only when dirty and preserves both primary and cleanup failures.
  R2 exposed an overly specific single-step typing Undo expectation; the bounded
  native checks pin every intermediate prefix, token, suffix and selection.
  R3 exposed immediate reopening before the queued resize-close settled; two
  animation frames and explicit enabled/open checks replace that ordering race.
  These corrections did not change production source or weaken preservation.
- R4 PASS 1/1 behaviorally, but actual image review found its full-page capture
  had closed the native popup through resize. The popup scan now uses viewport
  capture and requires it to remain open before and after; other scans retain
  full-page capture. This is recorded separately from visual qualification.
- Final R5 PASS 1/1, zero failures/skips, 81.309 seconds
  (`results/i444-settings-browser-r5.trx`). Nine states have zero serious/critical
  axe findings, missing images, page overflow, external requests or page errors.
  Timeout and Common creators round-trip flags remain true. The mobile picker
  fits x=12..378 within 390px; its option fits x=21..369 inside it.
  Final actual captures/report: `settings-4c2377b869094a3fadae72e82fb63b36`.
- Final Luna MAX closure independently inspected source, contract, corrected
  tests, raw R5 receipt, report, actual open-popup PNG and System/Library scope
  renders. All reviews are clear; no actionable finding remains.

The pushed `d70170e` checkpoint passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41761781830):
zero-warning/error Release build, all 107 SQL-independent native cases with no
failures/skips, and all 69 frontend files. The 141 Python checks retain the
expected pre-build population-discovery class skip. Full CI and IIS activation
remain skipped for this draft checkpoint; final full-population work is pending.

## #399 — email template cards, contextual insertion and example preview

The five built-ins and Additional templates use mounted, initially collapsed
cards with draft summaries. Multiple cards can stay open; new rows open for
editing. Subject/Body retain the global Save/Discard path, with per-field
capability-filtered pickers and safe text-only synthetic previews. Desktop uses
a 62.3% editor share; narrow containers stack the preview. Hold text remains an
editing example with no verified sending-path claim. Invalid collapsed content
opens the correct panel/card and focuses the field.

Stable UI identities preserve surviving rows, expansion, selection and drafts
through add/move/delete. Ordering follows the existing rejection-choice policy:
System owns source positions, Library owns only its custom ranks, and tied ranks
use exact decimal IDs. Unrepresentable Library positions stay disabled. Sparse
operations preserve untouched raw CRLF/whitespace, metadata, identities and
sibling versions; no schema, renderer or delivery policy changes were made.

- All 15 affected frontend files passed after ordering/raw-text remediation
  (13 Settings files plus the email/Patron placeholder helpers). Five files
  affected by the final notice correction passed again; independent Luna reran
  five and Astra reran three, all PASS. Release R3 build has zero warnings/errors
  (`results/i399-build-r3.log`); later frontend-only corrections were copied
  through the owning MSBuild target (`i399-frontend-refresh-r4.log` and
  `i399-frontend-refresh-r5.log`).
- Source review found inherited Move actions could unlock after rerender, fixed
  by composing ownership with position. Page/ancestor scrolling now dismisses
  anchored pickers; popup-internal scrolling remains open. Ordering now survives
  save/full reload through sparse rank patches, preserving fixed inherited
  positions and exact bigint identities. Displayed control baselines detect
  intentional edits without rewriting untouched stored line endings.
- Initial eight-case checkpoint executed 4 PASS / 4 FAIL / 0 skipped
  (`i399-checkpoint.trx`). EmailDefaults, full Settings, Library metadata and
  cumulative presentation passed. Failures exposed a 761px long-error header
  squeeze, duplicate new System keys becoming upserts, a picker-opening scroll
  race, and a native-200 preview breakpoint. The header now reserves intrinsic
  title width; the toolbar wraps. Template columns now require 48rem, preserving
  the wide ratio while stacking both supported native-200 widths.
- New keys are validated against the owning scope, including saved/pending-delete
  keys and unsaved peers. Library custom/System key sharing remains legal. The
  UI duplicate case proves no POST and retained sender/source drafts; a separate
  real HTTP repeated-target case proves 400 with atomic source/sender rollback.
- Campaign R2 passed Library, then System hit an old test sequence that scrolled
  the textarea after opening its picker. The corrected test explicitly verifies
  dismissal/focus/draft preservation, reopens after readiness, and proves insertion
  preserves page and field scroll. Production dismissal was retained.
- Terra found stale global validation text after the Subject was corrected.
  The matching control/message/context now owns the existing generation-guarded
  notice-clear closure. Correction, removal and context retirement clear that
  notice; other fields/cards and newer feedback are preserved. Retained browser
  checks pin the local, Settings and shell messages before/after correction.
- Final Campaign R3 PASS 2/2, zero failures/skips, 109.464 seconds
  (`i399-campaign-r3.trx`). System and Library each require 13 clean states and
  six template contexts, with native Undo/Redo, collapsed validation, stable
  rows, exact custom deletion, durable ordering and raw-text SQL witnesses.
  System additionally passes both duplicate flags and source ordering. All
  scans have zero serious/critical axe findings, missing images, overflow,
  external requests or page errors. Final actual artifacts:
  `settings-campaign2-46f275e2c1cd44b4826e64e29dad0186` and
  `settings-campaign2-2f33d87e36f34b41bfe98dcc5d09e640`.
- Luna independently inspected final source, tests, raw receipts and rendered
  template states; Astra's final integrity review inspected both passing SQL
  cases and raw/order/reset/collision probes. No template/data finding remains.
  Terra inspected all 12 final template images and confirmed the stale-notice
  correction.
- Terra's native full-page images prompted a real viewport check. R3 confirmed
  the sticky Save/Discard bar covered 73.4% of the keyboard-focused Body,
  including its caret. The helper now reveals focused card controls above the
  measured bar, retaining the floating actions. A focus generation and current
  context/connectedness guards retire old frame callbacks; insertion suppresses
  reveal through native/fallback editing and selection/scroll restoration.
  Focused Node checks and independent Luna/Astra re-reviews pass.
- Native R4 PASS 1/1, zero failures/skips, 184.113 seconds
  (`i399-native-r4.trx`). All 22 native states are clean. Actual browser zoom
  and DPR are 2, with CSS/pinch zoom unchanged. At both physical widths
  1280/1440, the full Body field and exact caret hit-test above the visible
  actions with zero overlap, selection `[60,60]` and unchanged textarea scroll.
  Final viewport PNG/JSON: `staff-workflow-c3f402b3c5c54577a41b57251b008572`.
  Terra and Luna personally inspected both focus images and receipts; the P2
  is resolved. The stacked preview was inspected with the same 48rem CSS and
  is retained by the geometry checks; focus captures frame the focused editor.
- The broad Staff retry retained a Chromium `ERR_NO_BUFFER_SPACE` navigation
  failure separately. It did not recur on the next run, which passed the 761px
  header check then exposed an old Staff URL test measuring its inner label
  rather than the approved full-width group containing the action. The corrected
  test retains whole-row width, no unrelated partner, child containment and
  explicit desktop/mobile label/input/action alignment.
- Final Staff/Campaign R4 PASS 3/3, zero failures/skips, 334.318 seconds
  (`i399-staff-campaign-r4.trx`): Staff 210.865s, System 56.794s, Library 37.806s.
  The Staff journey retains all 61 states, including both long-error header
  boundaries and URL group geometry. Both Campaign cases again retain all
  13 states/six contexts, native history, exact insertion scroll, validation,
  stable identity, ordering, deletion and raw-text witnesses after the focus fix.
  Final artifacts: `staff-1e4d4f8341ba4b7a8cbf5e5972d6e62b`,
  `settings-campaign2-e23c84ac58fa4bc28da5d570ed02accc`, and
  `settings-campaign2-3b47cfa135904d24856079e5d14878af`.
- All eight discovered checkpoint owners have passing receipts. The retained
  final source and focused results are independently reviewed; all reported
  implementation findings are resolved with evidence. Full combined-revision
  qualification, human acceptance and the final fresh closure audit remain
  separate from this issue checkpoint.

The pushed `43efa9b` template checkpoint passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41773132351):
zero-warning/error Release build, all 107 SQL-independent native cases with
zero failures/skips, and all 69 unique discovered frontend files. Its 141 Python
checks have the expected pre-build population-discovery class skip; native
execution has no skips. Full CI and test-IIS activation were skipped. Raw job
`126387233567` and the compact count summary are retained outside Git.

## #435 — compact staff roster and coherent account save

- The pinned `e515d2619ec5887b20acffbb8d514df7ac815ddc` roster reference
  governs the compact rows, one email input and bottom action footer. The
  roster keeps mounted account editors and draft handles keyed by account ID,
  initially collapsed disclosures, accepted-value surname ordering and
  separate lifecycle actions. Operations retains Administration audit.
- A versioned PUT uses the existing lifecycle transaction and authorization
  boundary for profile, synchronized primary email and role/library changes.
  Versioned Reactivate keeps accepted profile/access values separate from
  unsaved drafts. Explicit Create synchronizes the primary notification email
  when reactivating an existing inactive account; weekly overrides stay owned
  by their existing contract.
- Release R2 passed with zero warnings/errors. The first build's two
  MSTEST0044 failures were corrected to current TestMethod data-row syntax.
  Focused discovery identifies 18 SQL cases, including both retained POST and
  new PUT contraction behavior. `i435-sql.trx` passed 18/18 with zero
  failures/skips in 40.586s.
- Initial browser receipt `i435-browser.trx`: Administration audit passed;
  Staff failed before the new roster helper because its pre-save input
  assertion compared stored UserPrincipalName with the separately normalized
  uppercase CurrentStaff.AuthenticationEmail. The correction uses the stored
  input value and retains strict post-save identity/version assertions. This
  receipt is retained as 1 passed / 1 failed / 0 skipped,
  202.395s. The roster screenshots and rendered review remain pending.
- Independent Luna cycle 1 identified three corrections, now in one batch:
  **435-1 P2** preserve the distinct Reactivate audit category;
  **435-2 P2** reject Create emails over the SQL 320-character bound before
  mutation; **435-3 P2/P3** retire the previous account's super-admin-assignment
  capability before populating creation choices in a replacement session.
  Immediate SQL audit/failure assertions and account-transition coverage are
  present. Luna and Astra independently verified these corrections and each
  passed the affected Staff Access Node test. Release R3 passed with zero
  warnings/errors; `i435-sql-r2.trx` passed 18/18, zero failures/skips, 31.590s.
- Luna's narrow cycle 2 found **435-4 P2**: Reactivate accepted an already-active
  account with its current version and could append repeat false reactivation
  audit entries without changing the account. The final necessary correction
  adds a locked-state precondition and service/HTTP replay/no-write assertions
  before the qualifying browser run. No comprehensive review was restarted.
- The user's bounded review direction applies: finish necessary fixes after
  two or three cycles, preserve any pending findings/failed receipts, and defer
  further comprehensive review to the final combined audit. This issue has
  not yet reached its qualifying browser or visual result.
- The final locked-state precondition preserves stale-version precedence and
  returns the existing `identity_already_exists`/409 for an already-active
  Reactivate. Luna and Astra independently verified the narrow correction.
  Final Release R4 passed with zero warnings/errors (29.43s), and final SQL R3
  passed 18/18, zero failures/skips, 31.861s (`i435-sql-r3.trx`). Both reviewers
  inspected the actual receipt; all four source/integrity findings are fixed.
- The focused nine-file Node sweep and separate 271-case navigation suite
  passed. Updated Staff Access and inert-focus regressions pass; both reviewers
  independently reran Staff Access after the capability correction.
- Staff browser R2 reached the new desktop roster checks, including explicit
  single-email save, drafts, conflict, reordering and lifecycle, then failed
  on serious dark-mode contrast: success text `#236b42` on `#202c36` is 2.2:1.
  This is **435-5 P2, unresolved**. Receipt `i435-staff-browser-r2.trx` is
  0 passed / 1 failed / 0 skipped, 139.345s (case 128.703s), retained honestly.
  Six actual desktop PNGs are in
  `staff-9d3f6d8998f244fe88e1be278cb6a893`; tablet/mobile/Add captures, the final
  report's ten flags and the complete 70-state pass were not reached.
- Following the bounded direction, commit this implementation checkpoint and
  continue #406. Carry 435-5 and the missing browser/visual states into the
  roster integration checkpoint and final audit; do not describe #435 as fully
  qualified until passing evidence exists. No serious axe finding is waived.
- Terra inspected the six actual desktop states. **435-6 P3, unresolved**:
  the saved/reordered image retains the earlier competing-draft warning in
  both global notices after Cancel and successful Save, while the bottom bar
  says No changes. Carry this owned-feedback recovery defect into the roster
  checkpoint as well. The warning was correct in the earlier competing-drafts
  state; recovery must clear its own notices without erasing newer feedback.
- Terra also raised **435-7 P2, pending disposition**: two dirty mounted rows
  each block the other's Save, while the bar instructs the user to save each
  editor. The prior source review accepted truthful competing-draft blocking
  under the existing admission contract; actual-render review challenges its
  compatibility with independent draft preservation. The #406 checkpoint must
  assess and resolve this explicitly, preserving Settings/create/lifecycle
  admission, other mounted values, rowversions and actor-change safeguards.
- Discovery intentionally replaces the prior single contraction test UID
  `1881d8b7-150c-80c5-8046-afd3585ce756` with retained legacy-POST false row
  `1e734a89-07a8-8807-8a18-55c622bbbeeb` and new unified-PUT true row
  `17aef5ff-92b9-8837-9fde-aeb02e695aad`. Final population comparison must
  account for this retained-behavior replacement and discover current counts.

The `3054851` checkpoint passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41778850983):
zero-warning/error Release build, 107 native SQL-independent cases with zero
failures/skips and all 69 unique frontend files. Its 141 Python checks include
the expected pre-build population class skip. Raw job `126405509549` and summary
are retained outside Git. Draft CI does not supersede the recorded roster browser
failure or qualify the final combined revision.

## #406 — active-only roster and preservation checkpoint (in progress)

- The unchecked Show inactive accounts control filters the accepted authorized
  roster in place. Counts and distinct empty-active/empty-roster messages use
  that scope; the control is excluded from saved Settings and load snapshots.
  Hidden editors retain their DOM/drafts and become natively disabled, with
  focus returned to the filter before an account disappears.
- The checkpoint includes carried 435-5/6/7 corrections: scoped dark feedback,
  generation-owned blocked-action notice cleanup and explicit preservation of
  registered mounted sibling row drafts for a coherent PUT. Global Settings,
  creation and lifecycle admission keep their defaults; actor-context-changing
  saves cannot retire another row's draft. Confirmed receipt reconciliation and
  header Refresh support failed roster reloads without discarding siblings.
- Initial Release build passed with zero warnings/errors (31.01s). Parent's
  Settings sweep passed all 13 discovered files (`i406-settings-node.log`).
  The implementer passed final Staff Access, postcommit401, draft admission and
  271 navigation journeys; the earlier 42-file Staff sweep preceded the final
  recovery refinements. No final full-frontend claim is made from that sweep.
- The existing Staff browser case is discovered exactly once; its updated
  contract requires 74 major states, 16 roster flags and eight actual native
  200% roster states at physical widths 1280/1440. Those runtime and rendered
  results remain pending.
- Astra cycle 1 independently reproduced **406-1 P2, unresolved**: a PUT
  receipt without `active` passes the old guard, then a failed GET lets the new
  reconciliation classify an inactive account as Active and clear its draft.
  Validate all status/scope evidence needed for reconciliation before recording
  commitment; malformed responses must retain draft/uncertain recovery. Other
  reviewed integrity paths are clear, and three focused Node files passed.
- Luna independently confirmed 406-1, including an opposite boolean `active`
  value: PUT must preserve the target's accepted activation state. Its complete
  source review otherwise clears filtering/counts/empty states, scope, native
  fieldset ownership and preserved sibling/reload recovery.
- **406-2, test evidence correction pending**: the new native helper scrolled
  the focused control into view before measuring/capturing it. Capture the
  actual keyboard focus/hit and viewport before axe or manual scrolling; no
  production focus defect is established until that corrected test runs.
- **406-3 P3, unresolved**: a blocked-action notice can outlive its consumed
  draft handle after confirmed discard/reload because admission then reports
  invalid_owner rather than allowed. Retire that owned notice safely and retain
  newer feedback. All three corrections are in one bounded cycle-1 batch.
- The frozen correction rejects missing/nonboolean/opposite PUT activation,
  verifies consumed nullable profile/role/Int32 scope fields and matches the
  requested PUT result before commitment. Thirteen malformed-receipt cases
  retain accepted state, raw drafts and uncertainty through failed review.
  Four retired-handle/newer-message cases verify owned notice retirement.
  The native helper captures bounds/hit and the viewport PNG before axe, with
  no artificial scroll repair. **406-1 and 406-3 source findings are resolved**;
  406-2's test correction is present, pending its real native execution.
- Luna independently passed Staff Access and draft-scope checks; Astra passed
  those plus postcommit401. Both narrow reviews are clear. Final affected
  navigation checks passed 15/15, retaining substantive assertions and adding
  only missing valid-DTO fixture fields. Pure frontend refresh R2 passed, and
  the parent's final Settings sweep passed 13/13 (`i406-settings-node-r2.log`).
  C# and the 74/16/eight browser contract are unchanged since the initial build.
  The final actual Staff browser receipt is running, not yet qualified.
- Actual Staff browser receipt `i406-staff-browser.trx` **FAILED 1/1**, zero
  skips, 182.894s (case 161.294s). It reached all 13 major roster captures and
  the native 1280 collapsed state, then exposed **406-4 P2, unresolved**: the
  first dirty edit makes the floating global save bar cover the focused Email
  address. Actual browser/CDP zoom and DPR are 2; CSS viewport is 632x402, CSS
  zoom and pinch scale are 1. The active field bounds are y362.81..404.01 and
  its center (316,383.41) hits the bar's action div. Axe, images, overflow and
  horizontal control containment are clean at that checkpoint. Parent viewed
  the actual pre-axe PNG and extracted `i406-native-1280-failure.json`.
- Actual artifacts: `staff-031aae43b96747da91ed739ea18dd39b`. Thirteen major
  roster PNGs cover desktop light/dark, filtering and hidden drafts, selected
  library, preserved review, tablet, mobile and Add staff; only two of eight
  native states were captured. The final report and 74/16/eight assertions
  were not reached, so this is not a passing browser qualification.
- Following the bounded direction, carry 406-4 into #436's planned shared
  focus work, alongside the already recorded first-dirty-bar/template risks.
  Keep the strict pre-scroll native check and recheck all eight states after
  the correction. Do not restore manual scrolling as evidence.
- Terra additionally found **406-5 P3, unresolved**: mobile Add staff retains
  Email address is required after a valid email is entered. Its screenshot
  follows the empty-submit then fill sequence at staff.cjs 3663–3667. Carry
  field-owned validation-message recovery into the shared checkpoint without
  clearing newer server/uncertain feedback.
- Chevron review note is a reference conflict, not an open defect: older #406
  specifies right/down, but newer pinned #435 uses down/up. The task explicitly
  gives #435's compact layout precedence; retain that orientation and the
  correctly exposed expanded/control relationships.
- Terra inspected all 13 major roster PNGs and both actual native images.
  **435-5, 435-6 and 435-7 are resolved in that observed workflow**: readable
  dark success feedback, no obsolete competing warning after recovery/save,
  and one row accepted while the other draft stays mounted and is later saved.
  Tablet/mobile reflow, the filter/counts, selected-library roster and compact
  reference composition are clear. Terra and Luna independently confirm the
  actual 406-4 obstruction; Terra confirms 406-5. Both remain explicitly open.
- Commit this bounded checkpoint and proceed to #433. Full #435/#406 browser
  qualification awaits #436's shared focus/owned-validation corrections and a
  passing complete Staff receipt, including all six unreached native states.

The pushed `a8c0e7d` checkpoint passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41783683365):
zero-warning/error Release, all 107 native SQL-independent cases with no
failures/skips and all 69 unique frontend files. The 141 Python checks have the
expected pre-build population class skip. Raw job `126420844204` and summary are
retained outside Git. Full CI and test-IIS activation were skipped; the known
native browser failure and Add validation finding were carried into #436;
their corrected runtime and focused closure evidence is recorded below.

## #433 — organization placement and one participation editor

- The existing Organizations owner is under Polaris beside synchronization.
  Eligible active/inactive libraries have the canonical confirmed actions;
  read-only reference records follow synchronized parent chains into alphabetic
  library groups, with finite cycle/missing-parent fallback to Other last.
  Getting started now has summary/navigation and retains unrelated drafts.
- The duplicate checkboxes, full-set collection, participation baseline and
  dead Settings-save catalog callback are removed. Read-catalog validation and
  backend APIs/policies remain. No backend or schema behavior changes are made.
- Initial Release passed with zero warnings/errors (40.20s). The worker passed
  all 14 Settings files and 271 navigation journeys. Parent independently passed
  the new participation test's 7/7 scenarios, zero failures/skips (7.095s).
  The new root test file brings current frontend discovery to 70 files; this
  is not yet a full 70-file execution claim.
- Astra cycle 1 source review is clear and independently passed participation,
  catalog and scope-race Node checks. Current discovery selects exactly two
  browser owners: the existing combined System/Library Settings method and
  Administration audit; no new compiled cases. Settings now requires 12 states
  and its participation/grouping/recovery flags plus final real-SQL witnesses.
- Initial `i433-browser.trx`: **1 passed / 1 failed / 0 skipped**, 86.062s.
  Administration audit passed 51.951s. Settings failed 11.252s at a hardcoded
  group-name vector that omitted the legitimate Audit Library retained by the
  preceding fixture. Every real catalog record and eligible library were already
  present; production correctly included its extra group. **433-1 test-only**
  correction must allow authorized catalog additions while retaining explicit
  seeded relationship/sync assertions, ordering and exactly-once coverage.
  Rerun the same combined selection to retain the reproducing fixture order.
- Luna cycle 1 independently passed participation, catalog, scope, layout,
  save-completion and Polaris feedback Node checks. It found **433-2 P2/P3**:
  confirmed participation success or conflict refresh makes the form inert,
  replaces its action controls and has no focus restoration in the normal
  visible-panel path. Include current-owner action/catalog/header fallback
  focus after finish, without stealing newer focus or targeting inert controls.
  Add observable Node and actual browser assertions; this is the same bounded
  cycle-1 remediation batch as the test-only catalog correction.
- The corrected test accepts legitimate shared-catalog groups while retaining
  exact owned parent graphs, alpha ordering, action eligibility and every real
  record once. Its SQL witness compares non-owned library Id/IsActive tuples
  before/after; owned 2/3 remain active and 4–7 inactive. Focus recovery runs
  after inert release, respects newer user focus intent and restores the current
  action/catalog/header only within the accepted owner, panel and scope.
  Nine participation scenarios and both narrow independent reviews pass.
- R2 Release passed with zero warnings/errors (31.59s). Its combined browser
  receipt is retained as **1 passed / 1 failed / 0 skipped**, 136.435s:
  Audit passed 47.954s; the Settings report was written with 12 states, but the
  finally block deleted seeded organizations before their successful audit
  records and hit the real audit FK. This failure can mask an earlier assertion
  and was not treated as qualification. **433-3 fixture cleanup** moves only
  the existing own-audit Id > watermark deletion before the organization delete;
  no boundary or SQL constraint is weakened.
- Added guarded Getting started summary/Manage captures for System desktop
  and Library mobile. The final contract requires 14 exact states, no new MTP
  cases. Final Release R3 passed with zero warnings/errors (15.55s).
  **Final `i433-browser-r3.trx` PASS 2/2**, zero failures/skips, 122.404s:
  Administration audit 44.968s; combined System/Library Settings 61.099s.
  Thus all required flags, real-SQL witnesses and finally cleanup complete.
  All 14 states have zero serious/critical axe findings, external requests,
  page errors, missing images or horizontal overflow. Final Settings artifacts:
  `settings-28aedbd994264e4caae5d6156367c775`.
- Luna and Astra independently inspected the actual final receipt/report and
  closed functional/integrity qualification. Terra inspected the three Polaris
  views and two summary images: grouping, literal data text, inactive-parent
  associations, read-only controls and System summary are clear.
- **433-4 P2, initial visual contrast finding (resolved in #436):** Library summary information is dimmed
  by disabled-fieldset opacity. Screenshot pixels `#8a929b` on `#eff3f5` give
  approximately 2.82:1, despite the clean axe result. Manage must stay disabled;
  the read-only status must keep normal readable contrast. Carry this shared
  style correction and direct rendered contrast assertion into #436; do not
  describe #433 visual qualification as complete until it is rechecked.

The pushed `99469b7` checkpoint passed
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41789141510):
zero-warning/error Release, 107 native SQL-independent cases with zero
failures/skips and all **70** current frontend files. The 141 Python checks
include the expected pre-build population class skip. Raw job `126438220162`
and summary are retained outside Git. Full CI and test-IIS activation were
skipped; the visual contrast finding remains assigned to #436.

## #436 — focused accessibility and native editing

- Shared unfocused input borders use `#7b8791`, measured 3.673:1 against white
  and 3.389:1 against the page. Disabled system-only fieldsets retain native
  disabling with a soft background rather than dimming their informational text.
  Source-component Library summary/credential help measures 5.053:1 including
  ancestor opacity; actual application confirmation remains pending.
- Seven sortable Requests headers expose current `aria-sort` and column-specific
  next actions through class changes/redraw. Settings tab orientation follows
  the actual 760px layout breakpoint; concise labels retain separate help.
- Three placeholder mechanisms share native insertion, observed input events,
  selected-range/caret and exact field/page scroll preservation. Native history
  checks include surrounding ordinary edits, selection replacement, Undo/Redo,
  dirty-state and global Save/Discard pointer geometry.
- Seven focused Node files and the initial Release build pass (zero warnings or
  errors, 43.62s). The retained source-component v14 probe passes desktop,
  mobile, native 200% and native 400%: 48 natural focus center/hit proofs,
  32 native history cases, eight nonzero-scroll checks and 20 clean axe scans.
  Earlier v9–v13 failed probes remain outside Git; fixes include first-dirty
  template preview growth, queued-scroll/picker dismissal and staff focus return
  after collapsing/hiding DOM. These component results are not full-app evidence.
- **436-1 P3, independent visual finding:** v14's first-dirty roster Email has
  a visible caret/center but its lower edge/focus outline is clipped on mobile
  (center y=833.48 in height 844) and slightly at native 200%. The existing proof
  checks vertical center and horizontal containment, not full vertical bounds.
  Root inspected the actual screenshot and confirmed the limitation. Correction
  and stronger natural-focus coverage are pending after the frozen runtime run.
- Parent structured discovery selects exactly four existing application owners:
  Staff roster, combined System/Library Settings, cumulative presentation and
  queue/detail/native workflow. `i436-browser-r1` is in progress. Its Staff case
  failed after 230.718s in a later operational-catalog journey: **433-5 test-only
  integration defect**, the retained consumer still opens Staff access before
  clicking the relocated, hidden participation action. Both deactivate/activate
  paths must open Polaris and await its catalog. The eight native roster images
  were captured earlier, but no complete Staff pass/report is claimed.
- **436-2 P3, owned validation lifecycle:** a nonempty invalid partial Email still
  leaves the old required-field message, and abandoning the empty Add form leaves
  it outside the hidden form. Retire only this owned notice on nonblank input or
  cancellation/reset, preserving newer server/uncertain feedback and native
  format validation. These corrections are queued in the first review batch.
- Completed `i436-browser-r1.trx`: **3 passed / 1 failed / 0 skipped**, 589.019s.
  Settings passed 62.634s (14 states, eight direct rendered contrast checks),
  cumulative presentation passed 168.844s (including all four component/native
  configurations), queue/detail/native workflow passed 106.502s (22 native
  states). Staff failed 230.718s at 433-5; its complete report is absent.
  Actual Library summary measures 5.052771:1 and remains natively read-only;
  Terra inspected its fresh mobile image and the System summary/organization
  views, closing **433-4**. Its old failure remains recorded above.
- **436-3 P3, independently confirmed by Luna and a real controller probe:** a
  long Subject's picker blurs/resets horizontal scroll before the shared helper
  captures it. The real picker probe resets scrollLeft 5112 to 0 and leaves the
  inserted caret at x=5719.72 outside field bounds 283..859.56. Value/selection
  and page scroll remain correct. The existing nonzero-scroll test calls the
  helper while focused, missing this path. Preserve the owning editor's actual
  pre-blur scroll through the picker and cover visible caret/native history.
- Luna MAX functional review is in progress. Astra HIGH independently passed
  three affected Node files and five insertion event/fallback cases and found
  no integrity defect in the initial frozen source. Terra MAX inspected component
  and actual application images and identified 436-1/2. Runtime/review closure is
  pending; human screen-reader/deployed acceptance remains #398.
- The bounded correction batch is frozen. Staff reveal uses current context,
  sequence, actual focus and visible/editable ownership; full bounds include the
  focus outline after natural layout growth. Required-email feedback retires
  only its owned message on nonblank input or closing/resetting its Add form;
  Escape still retains the draft and Cancel resets it. Later server feedback
  survives. The operational-catalog journey now opens Polaris, awaits its real
  catalog/form readiness and observes current Title links when checking retired
  caches; all real confirmations/mutation/cache assertions remain.
- Long Subject insertion retains its pre-blur view under the same context,
  value and selection. Replacement can reduce the maximum scroll: the original
  desktop example's scrollWidth 5687 to 5673, clientWidth 575, clamps 5112 to
  5098. A separate end-caret insertion actually needs more horizontal scroll;
  the helper restores the prior view, then makes only the additional single-line
  adjustment needed to expose that caret. Page/textarea scroll remains exact.
  At native zoom the actual scroll maximum can differ by 0.5px from rounded DOM
  dimensions. Tests measure that diagnostic maximum only after the unassisted
  caret assertion/screenshot, then restore the recorded position, so it cannot
  repair a failure.
- Final source-component R4 passes desktop/mobile/native 200%/400%: **64 natural
  focus proofs, 56 full small-control/outline checks, 32 standard native history
  paths, four long selected replacements and four long end-caret paths, 20 axe
  scans**, with zero failures or serious/critical findings. Focused Email,
  Staff Access and Patron Node files pass again; diff checks pass. The seven-file
  frontend witness is `f0e2f8d2f7148e98b031cb953ad4764079121f9afca4dfaff026b9afa0107354`.
  Tall native-400 Body direct insertion is explicitly a visible-caret/center
  and exact-scroll claim, not full vertical field containment.
- Parent frontend refresh passes. Final Staff and presentation application
  qualification ran as `i436-browser-r2` (exactly two existing owners).
  **R2: 1 passed / 1 failed / 0 skipped**, 693.851s. Staff passed 449.677s:
  all 74 states, all 16 roster flags and all eight actual native-200 roster
  proofs, including full field/outline containment, complete. Its real-SQL
  witnesses and cleanup pass. Terra inspected all eight actual roster images
  and closes 436-1's rendered finding. Presentation failed 204.060s during the
  new rapid long-Subject end-caret picker sequence: the value remains unchanged
  and focus ends on the picker's first option. Its preceding application renders
  complete, but the component's first desktop case fails; no final presentation
  or #436 pass is claimed. Queued scroll/picker ownership is being investigated,
  rather than weakening the insertion assertion or repairing test focus.
- Narrow cycle 2 independently clears the scroll/history/context corrections
  (Luna source, Astra source plus focused Node/four measured-input probes, Terra
  actual replacement/end-caret and staff-outline renders). **436-4 P3** remains:
  an existing row's empty Email validation notice survives its Cancel/reset or
  Escape collapse. Retire only that row control's owned notice, with the same
  newer-generation safeguards. A final focused correction follows the running
  frozen qualification, then implementation moves to #391 under the user cycle
  cap. The bounded probes subsequently confirm both remaining interaction paths:
  the Subject's already-applied blur scroll (5112 to 0) arrives after opening at
  unchanged input position 0 and dismisses its picker while the page stays at 0;
  **436-5 P3** moves mobile Cancel's bottom from 844.328 to 837.328 during
  pointer-down. Pointer-up at 843.328 misses and leaves the draft dirty. Preserve
  dismissal for genuine later scrolling while ignoring an owned queued event
  at its opening position; button reveal must respect native keyboard modality.
- Final correction preserves matching-row notice ownership on reset, collapse
  and inactive-filter hide, retaining Escape/hidden drafts and newer/other-row
  feedback. Opening field coordinates distinguish an already-applied queued
  scroll from later movement. Native `:focus-visible` gates roster button reveal
  so pointer-down cannot move its target; keyboard and input reveal remain.
- **Final source-component R5:** all four configurations PASS, with 72 natural
  focus proofs, 64 full small-staff-control/outline checks, 32 standard native
  history paths, eight long-Subject paths, eight scroll cases, four successful
  stationary edge clicks and 20 clean axe scans (four intermediate plus one
  final scan per profile). Seven focused Node files pass. A separate real-native
  immediate End/Shift+Tab/Enter probe passes 30/30, versus the retained baseline
  failure on iteration 1. The final seven-file local frontend witness is
  `40db7aa8fed5ac23948334e46a43f7546a23d713ff7a33f9a1d7ba448d41779d`.
- After frontend refresh, **`i436-presentation-r3.trx` PASS 1/1**, zero failures
  or skips, 289.645s wall time / 258.490s case time. It completes all 130
  cumulative application states plus all four final component profiles, with
  no external requests or page errors. Actual artifacts:
  `staff-presentation-10b07fff5bd5414a95188d0b5d0b1fec`.
  R2's complete Staff pass precedes the last notice/scroll/pointer corrections;
  final focused Node/component checks and this final presentation pass verify
  those corrections. The final combined suite still runs after #391.
- Luna MAX independently verifies the final small source/test/receipt delta;
  Astra HIGH closes notice and scroll/context integrity; Terra MAX personally
  compares actual pointer baseline/fix and keyboard/native images. No actionable
  finding remained in this issue's reviewed delta at its commit. R1/R2 failures remain recorded,
  and component passes have not been substituted for either failed journey.
  The user's review-cycle cap is honored: finish this in-flight qualification,
  then move to #391, reserving the comprehensive review for the combined audit.
  Human assistive technology, live providers and deployed-SHA verification stay
  separately tracked and are not claimed by these local automated results.

## #391 — measured Settings spacing

- Phase 1 changes only retained browser instrumentation. Production source is
  unchanged from `7d1b247`; the baseline Release build passes with zero warnings
  or errors (71.39s). Structured discovery selects exactly the existing Settings,
  presentation and queue/native owners; no compiled cases are added or removed.
- The actual local Settings baseline serves `/staff/styles.css` with HTTP 200.
  Its SHA-256 `cc6600d5f0f5bc5e26968d9123d61bcd78bbce4645af036834e6ea6617e66d11`
  matches tracked source. The versioned stylesheet response and geometry are
  recorded together; this is local Kestrel evidence, not a deployed-host claim.
- System Common creators heading to populated editor measures **0px**; Library
  heading to Use system list and inheritance control to editor also measure
  **0px** in the actual mobile-on states. Implementer and independent Terra
  inspect the same raw geometry; Terra confirms touching borders in both PNGs.
  The existing patron-code picker normally has 10px; one System empty live-status
  track yields 20px to chips, which needs judgment rather than blind restyling.
- Baseline `i391-baseline.trx`: **2 passed / 1 failed / 0 skipped**, 409.710s.
  Settings passes 82.125s; queue/native passes 116.125s. Presentation fails
  186.775s in the unchanged #436 component, after recording the 84 actual
  spacing audit states. **436-6 P3, open for final audit:** after pointer use and
  summary Enter, native-200 summary bounds end at y=402.640625 against innerHeight
  402 / visualViewport height 402.5. Focus, hit, accessible content and the inset
  outline remain visible; this fractional full-bounds proof fails. Retain the
  exact failure in `staff-presentation-c906fa16563d4ed593b5ac1b52983dd7` and do
  not substitute prior component successes for the failed application journey.
  Under the user's explicit cycle cap, continue #391 and revisit this P3 in the
  final integrated audit; no new dedicated #436 review/fix cycle is started.
- Actual CSS-200 Workflow captures show a separate **P2 child-containment
  defect** for #391: timeout controls overlap narrow card labels even though
  document scrollWidth equals viewport width. Outstanding days ends at x=903.66
  while its owner ends at 830; additional-copy days ends at 1209.94 while its
  owner ends at 1144. Native-200 widths 632/712 confirm the zero vertical section
  gaps with CSS zoom 1, separate from that CSS-zoom collision.
- Measured 0px boundaries also include Branding source to first grid, Patron
  main grid to status-label grid, all five format/domain heading/editor and
  section transitions, and Additional templates heading to empty editor.
  Standalone provider help retains 12px; approved roster and organization
  spacing remain intentional. Empty patron-code availability/chips tracks
  reserve 30px from picker to zero count, requiring a presentation correction
  that retains the live status node and normal selection behavior.
- Both scopes' initial Public links blocks are hidden, so those screenshots do
  not establish the available-link/copy case. Production CSS stays frozen while
  an owned valid-Staff-URL fixture supplies authoritative scoped portal links
  for a test-only before-state supplement. No hidden case is counted as proof
  of the available controls.
- The available-links supplement `i391-links-before.trx` remains **failed 1/1**,
  zero skips, 66.188s wall / 50.094s case. It captures the actual Library desktop
  links before a Windows CRLF-versus-LF clipboard comparison fails for loader
  markup. All three field/textarea-to-Copy-button gaps measure **0px**; the buttons
  remain content-sized (100.91/117.55/117.56px). Terra inspects the same actual
  image/geometry. Only desktop available links are reached; no full supplement
  success is claimed. The fixture restores the original persisted Staff URL.
  Normalize only platform clipboard line endings in the assertion, retain the
  failed receipt, and proceed with these measured production spacing fixes.

The `7d1b247` checkpoint passes
[draft CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/41800619652):
clean Release, 107 native cases with zero failures/skips and all 70 frontend
files. The 141 Python checks include the expected pre-build native-discovery
class skip. Full CI and test-IIS activation are skipped; this draft result does
not close 436-6 or replace final combined qualification. Raw job `126475231654`
and its summary remain outside Git.

- The first after-build passes with zero warnings/errors (6.61s), but
  **`i391-after-r1.trx` fails 3/3**, zero skips, 129.460s. Settings fails 19.765s:
  the Library scope explanation still has 0px before System links because a
  later `.settings-callout { margin:0 }` overrides its earlier bottom margin.
  Presentation fails 11.834s because the new flow-gap assertion treats a sticky
  attention footer's viewport rectangle as a static sibling (-373.36px).
  Queue/native fails 79.726s because it requires every unfocused item in a
  normally scrollable popover to fit its visible scrollport. These failed runs
  are not qualification. Correct the actual explanation gap and make measures
  position/scrollport/focus-aware, retaining static gaps, horizontal containment,
  popup bounds and strict focused-control visibility; do not change the footer
  or picker policy to satisfy an overbroad assertion.
- Independent Terra sees the same real explanation collision in actual images
  and confirms the two intentional layout cases. Partial R1 native captures
  show readable stacked timeout/picker controls, expanded format rules and
  template Body; both native widths and the full matrix remain uncompleted.
- Cycle 1 corrects the scope-explanation selector's specificity and records
  positioned sibling rectangles separately from normal flow. Sticky raw gaps
  remain in receipts; static footers still require 18px. Popover checks use real
  scrollport geometry: unfocused off-port options may scroll, while horizontal,
  popup-viewport and fully visible/unobscured focused-option checks remain.
  Native checks traverse first/last/first without inserting or removing states.
- R2 Release passes with zero warnings/errors (4.99s).
  **`i391-after-r2.trx`: 2 passed / 1 failed / 0 skipped**, 302.177s. Settings
  passes 67.156s with all 14 states/eight contrast checks; queue/native passes
  112.253s with all 22 native states, both widths and keyboard picker traversal.
  Actual Library callout gaps are 10px, timeout controls are contained at CSS
  zoom, and common-creator/picker states have separate visible groups.
- Presentation fails 105.856s at the existing mandatory Copy-notice gap check:
  the embedding-policy note touches `Patron URL copied.` (0px). The field/Copy
  gaps are already 10px and copying succeeds. The ID paragraph reset outranks
  the class-only notice margin. R2 completes all 42 System base panel/config
  states but only the first Library desktop panels before that failure; do not
  count the full matrix as passed.
- The final small selector correction gives nonempty copied status 10px while
  retaining empty/cleared live-region normal flow at 0px. The mandatory browser
  assertion is unchanged; focused layout and actual-markup cascade checks pass.
  Final stylesheet SHA-256 is
  `3250025e2ddc5765234166f6a06a719268b84c602afd9a2b1a6710de134f6589`.
  After frontend refresh the existing Presentation owner alone is running as
  `i391-presentation-r3`; previously passing Settings/native paths are unaffected
  by this Copy-only margin. Actual full completion and final visual closure are
  pending, and 436-6 remains explicitly carried for the final integrated audit.
- **Final `i391-presentation-r3.trx` PASS 1/1**, zero failures/skips,
  372.095s wall / 353.692s case. The actual run completes 130 existing presentation
  states, 187 spacing-audit entries (including all 84 main panel/scope/config
  combinations) and four existing accessibility component profiles. All audited
  states have zero serious/critical axe findings, missing visible images or
  unexpected external requests/page errors, with required owner/viewport checks.
  Final actual folder: `staff-presentation-654a0e51aea3430298ad02d567a31ed4`.
- Luna MAX clears the final source/cascade correction and retained guard semantics.
  Terra MAX compares actual before/after System/Library, narrow, CSS/native zoom,
  dark, picker, Copy, editor and joined-roster views; no further actionable #391
  spacing finding remains in the reviewed captures. R2 Settings/native passes
  precede the last Copy-notice-only selector correction; final R3 verifies that
  correction across the full matrix. The earlier 436-6 fractional edge is not
  declared fixed merely because this invocation passes; its final disposition
  remains part of the fresh integrated audit.

| Inspected area | Before | Verified spacing or behavior after |
| --- | --- | --- |
| Patron-code empty availability/chips/count | 30px System / 20px Library phantom tracks | 10px meaningful group gap; live nodes retained in normal flow |
| Common creators | 0px heading/editor; Library 0px around inheritance | 10px internal groups; hidden controls reserve no tracks |
| Five Patron format/domain sections | 0px internal and between sections | 10px internal / 16px direct section boundaries; parent grids retain 14px |
| Patron text grids | 0px | 14px |
| Provider definitions/help/editor | 12px / 12px | Retained |
| Additional templates | 0px before heading and before editor | 16px preceding boundary / 10px internal |
| Branding, hidden preview | 0px source/grid | 12px; image-visible margins remain 12px, capture pending combined run |
| Public links and Copy feedback | 0px field/Copy; R2 copied-note 0px | 10px field/action/notes; content-sized buttons; 12px status/block |
| Library-only explanations | 0px | 10px |
| CSS-200 Timeout and override controls | Controls spill 56–104px beyond owners | Container-width stacking and contained controls in both scopes |
| Staff feedback and controls | Message/table 0px; Add Role spills 9px | 10px message gap / contained fields; joined table boundary stays 0px |
| Polaris/organization groups | 10px / 20px; action gaps 8px | Retained |
| Heading/actions and footer | 14px headings / 8px actions; 18px static footer/mobile stack | Retained, including wrapped actions and focus/scroll guards |

Visible-logo preview and expanded Library roster are explicitly pending rendered
spacing checks for the final integrated Staff journey. Its existing real GIF and
authorized Library-row flows receive supplemental capture/spacing hooks, without
changing 74 main states, 16 roster flags, eight native states or discovered MTP
methods. No success is claimed for those future captures. Human screen-reader,
live-provider and deployed-host validation remain separately owned.

The two final test-only Staff hooks pass syntax/layout checks and independent
Luna MAX read-only verification: stylesheet listeners precede navigation,
loaded-image/selection and mutation guards remain, and the Library 82 disclosure
adds only supplemental artifacts. Existing axe/image/overflow/page-error/external
guards and the 74/16/eight-state contracts remain. These checks verify the hooks,
not the future rendered results. The final combined evidence belongs in PR #454
at its tested source revision.

## Final integrated audit — keyboard focus after pointer use

The fresh Astra XHIGH reviewer independently read all 15 requirements, approved
decisions/reference, the complete implementation diff and focused evidence at
`406b0ca`. Five independently run frontend files passed. Its only actionable
source finding, **C438-1 P3**, reproduces the carried 436-6 beyond fractional
rounding: clicking a partly visible roster summary and then pressing Enter on
that same focused button leaves 24px or 64px below the native-200 viewport.
Pointer focus intentionally skips reveal to preserve stationary clicks; switching
to keyboard did not produce a second `focusin` event.

The correction queues the existing guarded reveal on roster-button keyboard use.
It retains context/generation, active-element, panel, loading/mutation,
disabled/hidden and `:focus-visible` checks. Native activation and text editing
remain unchanged. The retained component regression starts with genuine 24px and
64px clipping, holds a real pointer through two animation frames without moving
the row, verifies one toggle on release, then requires strict full outline bounds
after Enter without test focus/scroll repair.

- Four focused frontend files PASS: Staff access, Staff scope, accessibility
  layout and template editor. Four isolated profiles PASS with 18/18/20/18 focus
  proofs, eight native-history cases each, zero serious/critical axe findings,
  no overflow and valid images. The exact final held-pointer regression also
  passes; the full profile run preceded only its extra two-frame pointer wait.
- Luna MAX independently clears the small source/test delta. Astra XHIGH reruns
  its original independent native-200 probe against the exact final source:
  original/further-clipped bottoms 402.640625 / 426.140625 / 466.140625 all become
  395.640625 in the 402px viewport after Enter, with full bounds and keyboard
  focus visible. Unexpected requests, page errors and writes remain zero.
  Its final 64px screenshot independently confirms the complete row and outline.
- Independent after evidence is outside Git in
  `closure-native-focus-independent-after/closure-focus-probe.json`; focused
  profile evidence is `closure-native-focus-component-after`, and the exact final
  held-pointer receipt is `closure-native-focus-targeted-final/native-200.json`.
  Failed before receipts remain retained. **C438-1 is resolved**; no other
  actionable source/focused-evidence finding remains in this fresh audit.

This closes the concrete correction, not the final integrated gate. Exact final
frontend/.NET/SQL/browser populations, both supplemental spacing captures, the
six sequential acceptance suites, full CI and the auditor's final receipt review
are recorded in PR #454 at the tested revision. No component or source pass
substitutes for those complete executions.
