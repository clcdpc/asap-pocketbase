---
phase: 6
plan: 1
wave: 1
---

# Plan 6.1: Backend API Support for System-wide User Visibility

## Objective
Update the staff users list API to return the total count of staff users in the system when a Super Admin is viewing a specific library context. This information will be used by the frontend to help Super Admins understand where other users are located.

## Context
- .gsd/ROADMAP.md
- lib/staff_routes.js

## Tasks

<task type="auto">
  <name>Update staffUsersList API response</name>
  <files>lib/staff_routes.js</files>
  <action>
    Modify `staffUsersList(e)` in `lib/staff_routes.js`:
    - After calculating the filtered `users` list, check if the current user is a Super Admin.
    - If they are a Super Admin and a `targetOrgId` (other than "system") was provided, calculate the total count of all staff users in the system using `records.listStaffUsers(e.app).length`.
    - Include `totalAcrossSystem: count` in the JSON response payload.
  </action>
  <verify>
    Check the API response manually or via a script.
  </verify>
  <done>
    The `/api/asap/staff/users?orgId=...` endpoint returns `totalAcrossSystem` for Super Admins when a library orgId is specified.
  </done>
</task>

## Success Criteria
- [ ] Super Admins receive `totalAcrossSystem` in the staff users list API response when filtered by library.
