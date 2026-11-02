# Issue #403: request row actions

Title request rows use one ordered set of eligible commands and one existing
live-row guarded `onOpen` workflow. The November 2 approved priorities are:

| Tab/status | Preferred action and authority |
| --- | --- |
| Suggestions / `suggestion` | Purchase, then Reject, from `allowedActions` |
| Purchases / `outstanding_purchase` | `catalogFound`: Ready for hold when AutoHold is on; Close without hold otherwise |
| Pending holds / `pending_hold` | Place hold only with `canPlaceHold` and `canChangeWorkflowState` |
| Placed holds / `hold_placed` | Close request only with the `close` capability |
| Closed / `closed` | Reopen only with the `reopen` capability; successful hold history protects closure |

Already own, Claim, Assign and Close silently follow the primary action(s), only
where status, capabilities, claim state and actor/library authorization allow.
Claim is available to eligible staff on unclaimed open requests; Assign opens the
existing eligible-candidate form for open requests, including claimed requests.
Open details is absent from the command list. The row background and accessible
title link still open details, including rows with zero workflow commands.

`eligibleRowActions` is shared by row construction and detail `startAction`.
After the authoritative detail GET, it rechecks availability before focusing and
clicking the corresponding existing toolbar control. This preserves confirmation,
Purchase/Reject choices, assignment candidates, drafts, cancellation, request
version, current session/scope and pending-operation guards. Row handlers perform
no API mutation. Cancel returns focus to the workflow button; detail closure uses
the existing connected row/refresh fallback. Backend permissions are unchanged.

Each cell measures its actual rendered buttons, gap and available container width.
All fitting commands stay direct. Otherwise the longest fitting priority prefix
stays direct and the remainder goes into More. At least one command stays direct;
an exceptionally narrow area can wrap that command and More to retain full labels.
A sole command can wrap and never creates a menu. The same DOM buttons move between
the two areas, preserving their handler and avoiding hidden duplicates.

The client-array Grid.js adapter retains accepted rows/status during deferred
local Sort/Pagination recalculation, preserving the existing no-loading-flash
contract even when action measurement increases rendering work.

Grid.js ref retirement aborts listeners, cancels deferred layout, disconnects
observers and closes/removes the popover. Changed descriptor sets also retire the
old cell lifetime even when the record object is reused. A zero-width container
stays unpainted until it can be measured, including after a visible layout; its
open popup closes and focus returns to the connected queue control. Resize and font notifications remeasure
without replacing a stable partition. Resizing closes an open menu, restores focus
to a connected control and promotes a focused command when space permits.
Scroll dismissal compares the current More anchor with its opening position:
keyboard focus may scroll an offscreen cell before activation and deliver that
scroll event after the popup opens. An unchanged anchor keeps the popup open;
actual anchor movement closes it and returns focus to More.

## First-party menu audit

Searched tracked Frontend HTML/JS/CSS for More, ellipsis, popover, action menu,
details and summary, excluding vendor bundles.

| Location | Finding |
| --- | --- |
| `staff/js/queues.js`, title request Actions | Only comparable action overflow; fixed unconditional More and split quick actions. |
| `staff/js/request-workflows.js`, request detail action bar | Already direct/wrapping per #388; preserved. |
| `staff/js/copy-detail.js`, Additional copies action bar | Already direct; preserved. |
| `staff/js/queues.js`, Additional copies grid | Sole direct Open; preserved. |
| `staff/js/settings-domains.js`, Settings row actions | Direct move/delete controls; preserved. |
| `staff/js/patron-text-placeholders.js` | Placeholder insertion picker; distinct input workflow, preserved. |
| `staff/index.html`, Recent requests | Navigation history disclosure, preserved. |
| `staff/index.html`, Retirement review references | Informational references disclosure, preserved. |
| `staff/index.html`, What each placeholder means | Informational help disclosure, preserved. |
| `staff/styles.css`, staff identity ellipsis | Text truncation only; preserved. |

## Validation

`tests/staff_request_table.test.js` covers zero/one/two/many commands, order,
measured fit transitions, common dispatch, stale ownership/capabilities/status,
sign-out and cleanup using the real Grid.js lifecycle. The retained SQL-backed
table and native-zoom journeys check real geometry and direct/overflow dispatch.
The broader `tests/browser/staff.cjs` journey scopes detail workflow lookups to
the open modal and verifies that the new row command and its detail action coexist;
page-wide partial-name selectors must not confuse those two intentional controls.
`tests/acceptance/direct-actions.test.cjs` checks the shipped component/CSS/fonts
in Chromium, including native zoom, forced colors and complete labels. Its native
keyboard regression first reproduces the delayed focus-scroll dismissal, then
checks unchanged-position events, genuine scroll dismissal and connected focus.

`i403` is the disposable acceptance suite. Run with `d1,q1,pr410` as documented in
`tests/acceptance/README.md`. Generated evidence is local and never committed.
Final human accessibility acceptance remains under #398.

## Requirement → implementation → test matrix

| Current #403 criterion | Implementation | Observable evidence |
| --- | --- | --- |
| Closed reproduction, no redundant details command | Shared eligibility list; protected rows have no actions | Table/controller tests; i403 Closed/protected SQL fixtures; native 200% Closed scans at 1280/1440 |
| Zero/one commands; sole command never More | Empty cell or sole direct button, even at tiny widths | Table/component tests; real Chromium cardinality/zero-width/native zoom; i403 protected/Reopen/sole Assign |
| Every fitting command direct | Actual cell/button/gap measurements | Chromium fit/resize tests; i403 expanded action column |
| Constrained multiple commands: priority prefix + exact remainder | Same DOM controls reparented into More | Ordered keys/uniqueness/geometry assertions; i403 two/six-command cases |
| All five tabs, AutoHold variants and operation protection | Server allowedActions; hold capability plus workflow block; actor scope | Table inventory matrix; detail capability-revocation cases; real SQL i403 primary confirmations/Cancel |
| Resize, real column changes, zoom, fonts, sorting/paging/filter/refetch | ResizeObserver, font events, ref disposal, grid revision ownership | Table lifecycle tests; Chromium removal/reinsertion and font/zoom; i403 resize/replacement; SQL browser journey |
| Correct direct/menu dispatch; distinct row/title/+ and catalog searches | Shared guarded handler and authoritative `startAction` | Table exact dispatch counts; detail review/confirmation/Cancel/approval/version checks; i403 Purchase/Reject/Assign; SQL browser distinct controls |
| Escape/Enter/Space, ARIA, popup position and connected focus | Popover lifecycle, actual anchor-movement dismissal and workflow-button focus before click | Chromium native-200% offscreen focus/Enter/Space/delayed scroll/actual movement/Escape; viewport resize; i403 Escape/promotion/focus; SQL native zoom scoped axe |
| Session/role/scope/stale controls invalidated | Session identity + live row + detail context guards | Table stale actor/scope/sign-out/ref retirement; detail revocation; retained workflow/navigation authority regressions and SQL mutation tests |
| Additional-copy and #388 modal preserved | Existing direct Open and all-direct wrapping toolbar | Q1/D1 suites; retained detail/copy controller/browser tests |
| First-party overflow audit | Tracked frontend search, excluding vendor | Audit above; placeholder/combobox/informational disclosures preserved |
| Accessibility, complete labels, touch targets, high contrast, no overflow/leaks | Existing compact styles and measured lifecycle retained | Chromium forced-colors/axe/geometry/removal; SQL/native/browser and i403 checkpoints reject errors, external requests, broken images and page overflow |

The new five workflow fixtures are read-only and outside the mutation allowlist.
I403's sole writable scenario remains its existing approved synthetic Purchase.
Real provider mutations are not authorized or used. Human assistive-technology
acceptance is #398. PR #420 integration remains deferred until its normal merge;
then retain both suites/fixtures/native scans/CSS and run the combined registry
and `i390,i403,s1,d1,q1,pr410` qualification described in the acceptance README.
