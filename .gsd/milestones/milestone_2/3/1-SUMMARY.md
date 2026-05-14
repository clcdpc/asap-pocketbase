---
phase: 3
plan: 1
wave: 1
status: complete
---

# Plan 3.1 Summary: System-Only Section Guard UI

## Tasks Completed

### Task 1: Add system-only section guard logic to activateSettingsSection
- Added `systemOnlySections` constant: `['start', 'polaris', 'smtp', 'staff']`
- Extended `activateSettingsSection` to toggle `settings-panel-locked` class on system-only panels when `currentLibraryContextOrgId` is not `'system'`
- Dynamically creates/removes a `.system-only-guard-banner` div using safe DOM APIs (createElement, textContent, appendChild)
- Disables all `input`, `select`, `textarea`, and `.btn` elements inside locked panels
- Re-enables controls when switching back to system context

### Task 2: Re-apply guard when library context changes
- Added `activateSettingsSection(currentSettingsSection, { updateHash: false })` after `loadLibrarySettings` in the library selector change handler
- Added the same call at the end of `applyLibrarySettingsToForm`, guarded by `!settingsLoading` to avoid conflicts during initial load

### Task 3: Add CSS for system-only guard styling
- `.settings-panel-locked` reduces opacity to 0.5 and sets `pointer-events: none` on form controls
- `.system-only-guard-banner` uses a warning color scheme (#fff3cd background, #664d03 text) with a left accent border
- Panel itself remains scrollable/readable

## Files Modified
- `pb_public/staff/js/api.js` — guard logic + `systemOnlySections` export
- `pb_public/staff/js/settings.js` — re-evaluation hooks on context change
- `pb_public/staff/styles.css` — locked state + banner styles
