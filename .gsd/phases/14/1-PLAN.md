---
phase: 14
plan: 1
wave: 1
---

# Plan 14.1: Enable Existing Filters for Additional Copies

## Objective
Enable Search, Claim, and Workflow Tag filters for the "Additional Copies" grid tab to provide consistency with other workflow stages.

## Context
- .gsd/SPEC.md
- pb_public/staff/js/grid.js
- pb_public/staff/js/state.js

## Tasks

<task type="auto">
  <name>Enable filters in UI</name>
  <files>pb_public/staff/js/grid.js</files>
  <action>
    Modify `renderAdditionalCopiesGrid` to stop explicitly hiding filters.
    - Remove calls to `hideTagFilter()` and `hideClaimFilter()`.
    - Call `updateTagFilter(records)` and `updateClaimFilter()` instead.
    - Ensure `staffGridFilterBar.classList.remove('hidden')` is called (usually via the update functions).
  </action>
  <verify>grep -A 5 "function renderAdditionalCopiesGrid" pb_public/staff/js/grid.js</verify>
  <done>Filter update functions are called instead of hide functions.</done>
</task>

<task type="auto">
  <name>Apply filters to record set</name>
  <files>pb_public/staff/js/grid.js</files>
  <action>
    Update `renderCurrentGrid` to apply filters even for the `additional_copies` status.
    - Modify the ternary at the start of the function to include `additional_copies` in the filtering pipeline.
    - Ensure `gridjs` search keyword is correctly passed (it already is, but verify it works with the filtered records).
  </action>
  <verify>grep -A 10 "export function renderCurrentGrid" pb_public/staff/js/grid.js</verify>
  <done>`visibleRecords` for `additional_copies` are passed through the filter pipeline.</done>
</task>

## Success Criteria
- [ ] Search, Claim, and Flag filters are visible on the "Additional Copies" tab.
- [ ] Selecting a claim filter (e.g., "Mine") correctly filters the list of additional copies.
- [ ] Searching in the grid search box filters the additional copies list.
