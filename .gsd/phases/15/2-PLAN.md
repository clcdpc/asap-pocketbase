---
phase: 15
plan: 2
wave: 1
---

# Plan 15.2: Frontend Analytics for Additional Copies

## Objective
Update the staff analytics dashboard to display additional copy metrics alongside other request stages.

## Context
- pb_public/staff/js/analytics.js

## Tasks

<task type="auto">
  <name>Update stage labels and rendering</name>
  <files>pb_public/staff/js/analytics.js</files>
  <action>
    Update `stageLabels` to include `additional_copies`.
    - Add `"additional_copies": "Additional copies"` to the `stageLabels` object.
    - This will automatically enable it in "Requests by stage" and "Open request aging" panels.
  </action>
  <verify>grep "additional_copies" pb_public/staff/js/analytics.js</verify>
  <done>Frontend labels are updated.</done>
</task>

<task type="auto">
  <name>Verify summary hints</name>
  <files>pb_public/staff/js/analytics.js</files>
  <action>
    Review `renderSummaryCards` and `renderSummaryCard` hints to ensure they are still accurate (e.g. "Current non-closed requests" is still true if it includes additional copies).
  </action>
  <verify>grep -A 10 "function renderSummaryCards" pb_public/staff/js/analytics.js</verify>
  <done>Summary hints are accurate.</done>
</task>

## Success Criteria
- [ ] The "Requests by stage" panel shows an "Additional copies" row.
- [ ] The "Open request aging" panel shows an "Additional copies" row.
- [ ] Totals in the summary cards match the sum of all stages.
