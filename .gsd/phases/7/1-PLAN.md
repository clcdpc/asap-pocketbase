---
phase: 7
plan: 1
wave: 1
---

# Plan 7.1: Staff Access List Refinement & Fix SyntaxError

## Objective
Resolve the `SyntaxError` by implementing the missing `handleLibraryContextSwitch` export and remove the Auto-claims column and checkboxes from the staff access list.

## Context
- .gsd/SPEC.md
- pb_public/staff/js/settings.js
- pb_public/staff/index.html
- pb_public/staff/js/settings-users.js

## Tasks

<task type="auto">
  <name>Fix handleLibraryContextSwitch Export</name>
  <files>
    <file>pb_public/staff/js/settings.js</file>
  </files>
  <action>
    1. Implement and export `handleLibraryContextSwitch` in `settings.js`.
    2. The function should take an `orgId`, find the `select-library-context` element, set its value, and dispatch a `change` event.
  </action>
  <verify>Run the app and check the console. The SyntaxError `Uncaught SyntaxError: The requested module './settings.js' does not provide an export named 'handleLibraryContextSwitch'` should be gone.</verify>
  <done>`handleLibraryContextSwitch` is exported and functional.</done>
</task>

<task type="auto">
  <name>Remove Auto-claims from Staff Access UI</name>
  <files>
    <file>pb_public/staff/index.html</file>
    <file>pb_public/staff/js/settings-users.js</file>
  </files>
  <action>
    1. In `index.html`, remove the `<th>Auto-claims</th>` header from the staff access table (around line 427).
    2. In `settings-users.js`, remove the `tdAutoClaim` creation and the call to `renderStaffFormatClaimToggles` in `renderStaffUsers`.
    3. Delete the `renderStaffFormatClaimToggles` function.
    4. Delete the `setFormatAssignment` function.
    5. Remove the click event listener for `.staff-format-claim-check` in the `settings-staff` container.
  </action>
  <verify>Check the Staff Access list in the browser; the Auto-claims column should be gone.</verify>
  <done>Auto-claims column and rendering logic are removed from the staff access list.</done>
</task>

## Success Criteria
- [ ] No SyntaxError regarding `handleLibraryContextSwitch`.
- [ ] Staff access list no longer shows the Auto-claims column.
- [ ] Staff user rendering logic no longer includes auto-claim toggle rendering.
- [ ] Auto-claim save logic is removed from the staff management module.
