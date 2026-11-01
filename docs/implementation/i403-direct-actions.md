# Issue #403: request row actions

Title request rows use one ordered set of eligible commands and one existing
live-row guarded `onOpen` workflow. Priority is Purchase, Reject, Open details,
Already own, Claim, Assign, Close silently, omitting unavailable commands.
Server capabilities and the existing status/claim rules still decide eligibility.

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
observers and closes/removes the popover. Resize and font notifications remeasure
without replacing a stable partition. Resizing closes an open menu, restores focus
to a connected control and promotes a focused command when space permits.

## First-party menu audit

Searched tracked Frontend HTML/JS/CSS for More, ellipsis, popover, action menu,
details and summary, excluding vendor bundles.

| Location | Finding |
| --- | --- |
| `staff/js/queues.js`, title request Actions | Only comparable action overflow; fixed unconditional More and split quick actions. |
| `staff/js/request-workflows.js`, request detail action bar | Already direct/wrapping per #388; preserved. |
| `staff/js/copy-detail.js`, Additional copies action bar | Already direct; preserved. |
| `staff/js/queues.js`, Additional copies grid | Sole direct Open details; preserved. |
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
`tests/acceptance/direct-actions.test.cjs` checks the shipped component/CSS/fonts
in Chromium, including native zoom, forced colors and complete labels.

`i403` is the disposable acceptance suite. Run with `d1,q1,pr410` as documented in
`tests/acceptance/README.md`. Generated evidence is local and never committed.
Final human accessibility acceptance remains under #398.
