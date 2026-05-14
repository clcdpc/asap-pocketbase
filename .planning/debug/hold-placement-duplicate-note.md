---
status: resolved
trigger: "the system still seems to treat when it places the hold as creating or attempting to create a duplicate. as least when i manually run the hold check. this is still a bug. it places the hold for me which is good but it shouldn't tell me there is a duplicate when it was the only one that placed the hold."
created: 2026-05-14
updated: 2026-05-14
---

# Debug Session: hold-placement-duplicate-note

## Symptoms

- expected_behavior: When ASAP places the hold itself, later/manual hold checks should not report that as an external duplicate or add "Polaris reported an existing duplicate hold request for this patron."
- actual_behavior: Hold placement succeeds, but notes/activity reports a duplicate hold and Hold placed tab shows "Patron already has hold" for that same request.
- error_messages: No thrown error reported. UI/system note says "Polaris reported an existing duplicate hold request for this patron."
- timeline: Observed on 2026-05-14 after manual hold check.
- reproduction: Move/request item through Pending Purchase -> Pending Hold with BIB 4230422; run manual hold check; inspect Notes & activity and Hold placed tab.

## Current Focus

- hypothesis: Staff-side duplicate hold probes were creating holds, then the pending-hold processor detected those ASAP-created holds as pre-existing duplicates.
- test: Replace staff duplicate probes with read-only patron hold lookup and run targeted regression tests.
- expecting: Staff BIB lookup and pending-hold transition duplicate checks do not call hold placement APIs; pending-hold processor still detects true pre-existing holds.
- next_action: resolved
- reasoning_checkpoint:
- tdd_checkpoint:

## Evidence

- timestamp: 2026-05-14T00:00:00-04:00
  observation: `lib/staff_routes.js` used `polaris.placeHold(staffAuth, bibid, pPatron.PatronID, true)` in `maybePromoteExistingPolarisHold`.
  implication: The supposed duplicate check was not read-only. `lib/polaris.js` has no test-mode branch for boolean `true`, so this could create a Polaris hold before the pending-hold job ran.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: `lib/staff_routes.js` also used `polaris.placeHold(staffAuth, bibId, patron.PatronID, true)` while populating `patronHoldCheck` for BIB lookup UI.
  implication: Manually checking a BIB could create the hold and later cause the system to report the same hold as an existing duplicate.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: `lib/jobs.js` correctly skips pending hold placement when `polaris.patronHasHoldForBib` returns true and adds the "Hold exists (same patron)" workflow tag.
  implication: The job behavior is correct for true external duplicates, but staff-side write probes could manufacture that condition.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: Replaced both staff-side duplicate probes with `polaris.patronHasHoldForBib`, preserving the UI's `patronHoldCheck.statusValue === 29` contract with a read-only synthetic result.
  implication: Duplicate detection remains visible to staff without creating a Polaris hold during the check.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: `node tests/staff_hold_duplicate_check_readonly.test.js`, `node tests/pending_hold_placement.test.js`, and `node tests/hold_result_classification.test.js` all pass.
  implication: Regression coverage confirms the staff duplicate checks stay read-only and existing hold placement classification behavior still passes.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: A later UI attempt still showed a generic "Error updating suggestion" on first submit, while a direct browser view of the POST-only action URL showed PocketBase's 404 "File not found" response.
  implication: The UI was hiding the actual response details, making first-submit failures hard to distinguish from a route/method/debug-tab artifact.
- timestamp: 2026-05-14T00:00:00-04:00
  observation: Added a hard guard so legacy `polaris.placeHold(..., true)` throws before any HTTP request.
  implication: Any stale or future boolean test-mode caller cannot create a real Polaris hold as a side effect.

## Eliminated

- `classifyPolarisHoldResult` alone: status 29 is still a valid duplicate classification for Polaris responses, but it was not the initiating defect.
- `processPendingHolds` pre-check alone: it should continue to skip true pre-existing patron holds; the bug was the earlier staff probe creating the hold.

## Resolution

- root_cause: Staff duplicate-hold checks called `polaris.placeHold(..., true)` even though `placeHold` has no test-mode behavior, so checking for duplicates could create a hold and make the later/manual hold check report ASAP's own hold as a duplicate.
- fix: Changed staff duplicate detection to use read-only `polaris.patronHasHoldForBib` in both pending-hold transition handling and BIB lookup, preserved the existing UI duplicate-warning response shape, blocked legacy boolean `placeHold(..., true)` before it can send a hold request, and improved edit-form errors to include HTTP status/body.
- verification: `node tests/staff_hold_duplicate_check_readonly.test.js`; `node tests/pending_hold_placement.test.js`; `node tests/hold_result_classification.test.js`; `node tests/polaris.test.js`; `node --check pb_public/staff/js/modals.js`; `node --check lib/polaris.js`; `node --check lib/staff_routes.js`
- files_changed: `lib/staff_routes.js`; `lib/polaris.js`; `pb_public/staff/js/modals.js`; `tests/polaris.test.js`; `tests/staff_hold_duplicate_check_readonly.test.js`; `.planning/debug/hold-placement-duplicate-note.md`
