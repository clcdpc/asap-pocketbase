---
phase: 6
plan: 2
wave: 1
---

# Plan 6.2: Frontend Library Switcher and User Visibility

## Objective
Enable the library context switcher (blue bar) for the Staff Access section and update the UI to show Super Admins how many users are in other libraries. Also, ensure Super Admins can add staff to any library regardless of the current context.

## Context
- .gsd/ROADMAP.md
- pb_public/staff/js/api.js
- pb_public/staff/js/settings-users.js

## Tasks

<task type="auto">
  <name>Enable library context switcher for Staff section</name>
  <files>pb_public/staff/js/api.js</files>
  <action>
    Modify `activateSettingsSection` in `pb_public/staff/js/api.js`:
    - Add `'staff'` to the `overridableSections` array. This will cause the `library-context-wrapper` (the blue bar with the dropdown) to be visible when the "Staff Access" section is active.
  </action>
  <verify>
    Navigate to Settings > Staff Access as a Super Admin and confirm the blue context bar is visible.
  </verify>
  <done>
    The library context switcher is visible in the Staff Access section.
  </done>
</task>

<task type="auto">
  <name>Display "other users" count for Super Admins</name>
  <files>pb_public/staff/js/settings-users.js</files>
  <action>
    Modify `loadStaffUsers()` in `pb_public/staff/js/settings-users.js`:
    - Capture the `totalAcrossSystem` field from the API response.
    - If `currentLibraryContextOrgId` is not 'system' and `totalAcrossSystem` is provided, calculate the number of users in other libraries: `totalAcrossSystem - users.length`.
    - If there are users in other libraries, append a helpful message to `msgEl.textContent`, e.g., "(X other users are in different libraries)".
  </action>
  <verify>
    Switch to a specific library context in Staff Access and confirm the message shows the count of users in other libraries.
  </verify>
  <done>
    Super Admins see a count of users in other libraries when viewing a specific library's staff.
  </done>
</task>

<task type="auto">
  <name>Unlock library selection for Super Admins in Add Staff form</name>
  <files>pb_public/staff/js/settings-users.js</files>
  <action>
    Modify `populateStaffLibraryOptions()` in `pb_public/staff/js/settings-users.js`:
    - For Super Admins, do NOT hide the `select` element even if a library context is active.
    - Instead, always show the full list of organizations in the `select`.
    - If `currentLibraryContextOrgId` is a specific library, pre-select that library in the dropdown as a convenience, but keep it editable.
    - Ensure the `context` display (the static text) is hidden for Super Admins so they see the interactive dropdown.
  </action>
  <verify>
    As a Super Admin in a specific library context, confirm the "Library" dropdown in the "Add staff member" form is visible and allows selecting any library.
  </verify>
  <done>
    Super Admins can add staff to any library regardless of the current context.
  </done>
</task>

## Success Criteria
- [ ] Super Admins can switch library context while in the Staff Access section.
- [ ] The staff list message includes the count of users in other libraries when a library context is active.
- [ ] The "Add staff member" form allows Super Admins to select any library, even when scoped to a specific library.
