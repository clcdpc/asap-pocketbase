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
| 1 | #395 | Header compacted; focused DOM/scope checks and SQL/browser presentation PASS | Luna MAX clear after retained-test correction | This issue commit; exact SHA in PR |
| 2 | #448 | Pending | Pending Luna MAX | Pending |
| 3 | #445 | Pending | Pending Luna MAX | Pending |
| 4 | #449 | Pending | Pending Luna MAX | Pending |
| 5 | #409 | Pending | Pending Luna MAX / Astra HIGH | Pending |
| 6 | #437 | Pending | Pending Luna MAX / Astra HIGH | Pending |
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
- Real SQL Server 2022 and deterministic provider boundaries are used for
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
