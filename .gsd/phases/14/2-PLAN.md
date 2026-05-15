---
phase: 14
plan: 2
wave: 1
---

# Plan 14.2: Add Status Toggle to Additional Copy Tab

## Objective
Provide a way for staff to view "Closed" additional copy requests without leaving the "Additional Copies" tab.

## Context
- pb_public/staff/index.html
- pb_public/staff/js/state.js
- pb_public/staff/js/grid.js

## Tasks

<task type="auto">
  <name>Add status filter to UI</name>
  <files>
    pb_public/staff/index.html
    pb_public/staff/js/state.js
  </files>
  <action>
    1. In `index.html`, add a new select `#additional-copy-status-filter` to the `staff-grid-filter-bar`.
       - Options: Open (default), Closed.
       - Add it after the `similar-request-filter`.
    2. In `state.js`:
       - Export `additionalCopyStatusFilter` and `setAdditionalCopyStatusFilter`.
       - Add the new select to the imports/exports list.
  </action>
  <verify>grep "additional-copy-status-filter" pb_public/staff/index.html</verify>
  <done>UI element and state management are added.</done>
</task>

<task type="auto">
  <name>Implement status filtering logic</name>
  <files>pb_public/staff/js/grid.js</files>
  <action>
    1. Update `loadTab` for `additional_copies` to use the value of `#additional-copy-status-filter`.
    2. Update `renderAdditionalCopiesGrid` to show/hide the status filter based on the current tab.
    3. Add a change listener to `#additional-copy-status-filter` that reloads the tab.
    4. Ensure that when switching *to* the Additional Copies tab, the filter defaults to "Open".
  </action>
  <verify>grep -n "additional-copy-status-filter" pb_public/staff/js/grid.js</verify>
  <done>Staff can toggle between Open and Closed requests on the Additional Copies tab.</done>
</task>

## Success Criteria
- [ ] A new "Status" dropdown appears on the "Additional Copies" tab.
- [ ] Changing the status to "Closed" reloads the grid with closed additional copy requests.
- [ ] The status filter is hidden on other tabs.
