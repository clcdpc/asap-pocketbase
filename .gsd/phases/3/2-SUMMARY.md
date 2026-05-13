---
phase: 3
plan: 2
wave: 2
status: complete
---

# Plan 3.2 Summary: Audit and Harden Save Payload Scope Safety

## Tasks Completed

### Task 1: Audit and fix frontend payload assembly for library saves
- Refactored `libraryPayload` in `saveSettings()` to remove `staffUrl`, `leapBibUrlPattern`, `smtp`, and `polaris` from the initial object literal
- These system-only fields are now only added to `libraryPayload` inside an `if (isSystemSave)` block
- Added code comments documenting the scope safety rule
- Backend guard in `staff_routes.js:1853-1863` remains intact as defense-in-depth

### Task 2: Manual verification (checkpoint)
- Browser subagent skipped by user — manual verification deferred to user testing
- Code audit confirms:
  - `_serializeSettingsState()` gates SMTP/Polaris collection behind `isSystemContext` (line 545)
  - `saveSettings()` now only sends `smtp`/`polaris`/`staffUrl`/`leapBibUrlPattern` when `currentLibraryContextOrgId === 'system'`
  - Backend `updateLibrarySettings()` strips these fields for non-super-admins (line 1853-1863)
  - `saveLibraryScopedSettings()` only saves workflow, ui, emails, and formatClaimRules — never SMTP/Polaris

## Files Modified
- `pb_public/staff/js/settings.js` — payload assembly hardening
