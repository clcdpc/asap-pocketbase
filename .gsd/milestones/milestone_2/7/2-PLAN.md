---
phase: 7
plan: 2
wave: 1
---

# Plan 7.2: Patron Experience Auto-Claim Scoping & Filtering

## Objective
Restrict auto-claim settings in the Patron Experience panel to library-scoped views and ensure that only staff members from the current library context can be selected as auto-claimants.

## Context
- .gsd/SPEC.md
- pb_public/staff/js/settings-formats.js
- pb_public/staff/js/state.js

## Tasks

<task type="auto">
  <name>Hide Auto-claim settings in System context</name>
  <files>
    <file>pb_public/staff/js/settings-formats.js</file>
  </files>
  <action>
    1. In `renderFormatSettings`, check `currentLibraryContextOrgId` from `state.js`.
    2. If `currentLibraryContextOrgId === 'system'`, hide the "Auto-claim staff" column.
    3. Remove the "Auto-claim staff" table header and the corresponding `claimTd` cells from the table generation when in system context.
    4. Update the help text at the top of the format settings to explicitly state that auto-claims are library-only and must be configured per library.
  </action>
  <verify>Switch to "System Defaults" and check "Patron Experience" -> "Material Formats". The "Auto-claim staff" column should be hidden. Switch to a library, and it should reappear.</verify>
  <done>Auto-claim settings are hidden in the global System context.</done>
</task>

<task type="auto">
  <name>Filter Auto-claim Staff Options by Library Context</name>
  <files>
    <file>pb_public/staff/js/settings-formats.js</file>
  </files>
  <action>
    1. In `renderFormatSettings`, update the loop that populates the `format-claim-staff-select` dropdowns.
    2. Filter `formatClaimStaffOptions` to only include staff members where `staff.libraryOrgId` matches the `currentLibraryContextOrgId`.
    3. Ensure that if no staff members are found for the library, the dropdown only shows "No automatic claimant".
  </action>
  <verify>Switch between libraries and check the "Auto-claim staff" dropdown in "Material Formats". It should only list staff belonging to the selected library.</verify>
  <done>Auto-claimant options are filtered by library context.</done>
</task>

## Success Criteria
- [ ] Auto-claim settings are only visible when a library context is active.
- [ ] Auto-claimant dropdowns only list staff from the active library.
- [ ] Uniqueness (one claimant per format) is maintained by the UI structure.
