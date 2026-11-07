# Search ASAP acceptance evidence

Application identity and acceptance-tooling identity are independent. Pin the
full application SHA when invoking `i467`; the launcher records tooling SHA,
dirty state, digest, image, normal reviewer identity, SQL version and disposable
container. Keep raw reports/screenshots outside Git. No IIS/dev deployment is
needed for these scenarios.

| Scenario | Observable proof |
| --- | --- |
| `i467-sql-matching-counts-and-paging` | Seven nonempty status/type facets; no duplicates/gaps across 12-row pages; deterministic case/whitespace; full patron names; 26 all-library versus 25 library-2 Q1 copies; closed facet; barcode/identifier/BIB/request ID; literal special characters and invalid inputs |
| `i467-header-and-keyboard` | Requests, copies, Analytics, Operations, Settings; Ctrl+K, immediate focus, arrows, composition Enter, Tab, Escape/focus return; no term in URL/localStorage |
| `i467-full-results-scope-and-filter` | Bounded 12-row preview, 25+1 full pages, retained query/all-library scope, closed-copy filter/count, idle reload |
| `i467-cross-status-details` | Off-tab closed title and closed copy open via two distinct fresh search-authorized detail GETs |
| `i467-loading-failure-retry-and-stale` | Injected 503 is distinct from no matches; retry; held older success loses to replacement query; loading/empty and fresh reopening |
| `i467-settings-draft-cancel` | Search selection reaches existing unsaved-draft confirmation; Keep editing retains draft with no opened detail and useful focus |
| `i467-responsive-native-zoom` | Desktop, 390px mobile, 390x360 short viewport, real 200% Chromium zoom; visible selected title/search/close/View all controls and retained PNG/geometry |

All seven scenarios assert no workflow mutation and unchanged SQL fixture
title/copy/outbox evidence. Every checkpoint retains normal runtime and reviewer
verification plus axe, image and overflow guards. Search stays within the exact
read-only POST route exception; unexpected outbound requests remain blocked.

## Complementary retained evidence

`tests/Asap.Tests/Integration/PatronJourneyTests.SubmissionSearch.cs` owns the
real SQL HTTP matrix including staff/admin attempts to broaden access, current
identity/role/library changes, inaccessible details, exact rank, representative
volume/performance, stored-state/provider proof and cancellation. Its browser
companion and `tests/browser/submission-search.cjs` retain changing/deleted
details, title/copy drafts, scope races and native zoom. Controller regressions
in `tests/staff_submission_search.test.js` cover debounce, aborted and
out-of-order requests, old failures, session replacement and safe text rendering.
The runner's normal super-admin cookie must not be replaced with test headers.

The application at `ac878bab5007fb2dc9bd1bcd0a7388a41b3ab110` has complete
required [Full CI attempt 2](https://github.com/clcdpc/asap-pocketbase/actions/runs/42355422611/attempts/2)
evidence: 78 retained frontend files, 1,496 non-browser native tests and 54
browser tests, all freshly executed with zero failures/skips. The
[ready-event run](https://github.com/clcdpc/asap-pocketbase/actions/runs/42372829609)
reused those verified originals on an identical source tree and rebuilt the
package. A later tooling-only commit is not a fresh full application suite run.

The recorded 12,038-row fixture has 12,000 matches: first-context All 2.410s
misses the 2s engineering target; warm classes 211-757ms meet the 1s target.
The new compact acceptance suite does not supersede or hide that result.

Human visual and actual screen-reader review remains pending under #398. The
separate unmerged #443/#472/#479 integration checkpoints and later #456 sweep
are not established by this single-PR run. Keep those requirements open and do
not modify closed issues or claim deployment/merge readiness from automation.
