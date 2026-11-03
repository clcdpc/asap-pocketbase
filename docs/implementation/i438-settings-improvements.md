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
| 5 | #409 | Accepted credential help and local outcomes; Node, SQL 3/3 and final browser 1/1 PASS | Luna MAX / Astra HIGH clear after P2 correction | `2b4068a` |
| 6 | #437 | Flag-only masks; Node 13 files, initial SQL/browser 4/4 and final affected browser 1/1 PASS | Luna MAX / Astra HIGH clear after P3 wording correction | This issue commit |
| 7 | #446 | Pending | Pending Luna MAX / Astra HIGH | Pending |
| 8 | #447 | Pending | Pending Luna MAX / Astra HIGH | Pending |
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
