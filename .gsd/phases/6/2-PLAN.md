---
phase: 6
plan: 2
wave: 1
---

# Plan 6.2: Frontend Library Switcher and User Visibility

## Objective
Enable the library context switcher (blue bar) for the Staff Access section and update the UI to show Super Admins how many users are in other libraries when a specific library context is active.

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

## Success Criteria
- [ ] Super Admins can switch library context while in the Staff Access section.
- [ ] The staff list message includes the count of users in other libraries when a library context is active.
