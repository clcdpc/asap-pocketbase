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
| 8 | #447 | Effective conditional options; Node 13/13, Release build, checkpoint 4/4 unaffected and final SQL/browser 1/1 PASS | Luna MAX / Astra HIGH clear after focus/test-wrapper corrections | This commit |
| 9 | #444 | Pending | Pending Luna MAX | Pending |
| 10 | #399 | Pending | Pending Luna MAX / Terra MAX | Pending |
| 11 | #435 | Pending | Pending Luna MAX / Astra HIGH / Terra MAX | Pending |
| 12 | #406 | Pending | Pending Luna MAX / Astra HIGH / Terra MAX | Pending |
| 13 | #433 | Pending | Pending Luna MAX / Astra HIGH / Terra MAX | Pending |
| 14 | #436 | Pending | Pending Luna MAX / Terra MAX | Pending |
| 15 | #391 | Pending | Pending Luna MAX / Terra MAX | Pending |

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
- Full frontend, exact discovered .NET/SQL and browser populations, sequential
  acceptance suites and final CI remain pending until the combined implementation.
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
