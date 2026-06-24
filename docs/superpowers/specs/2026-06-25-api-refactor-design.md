# api.js Refactor Design

## Summary

Split `pb_public/staff/js/api.js` (919 lines, 47 exports) into 6 focused modules
under `app/`, with `api.js` becoming a thin barrel + side-effect event boot.
Two source-inspection tests need to read from new target files.

This is a structural refactor only. It must not change auth, settings, filter,
grid, bootstrap, or profile behavior. All 47 public exports are preserved.

## Motivation

`api.js` is a catch-all mixing 6+ unrelated concerns:

- DOM field helpers (`setFieldValue`, `getFieldChecked`, etc.)
- URL normalization and query parsing (`leapBibUrl`, `requestedStatusFromUrl`)
- Auth checks and role helpers (`isSuperAdminStaff`, `staffRole`)
- Bootstrap/setup orchestration (`checkAuth`, `loadSetupStatus`)
- Settings navigation and save-bar state management (`activateSettingsSection`,
  `markSettingsDirty`, `updateSaveBarState`)
- App-wide event listeners (login form, status tabs, filter selects, profile
  dialog, grid search input)

47 exports make it the most scattered module in the codebase. The modals.js and
grid.js refactors establish the barrel + sub-module pattern that applies here.

## Target Module Boundaries

```
pb_public/staff/js/
├── app/
│   ├── dom.js           # DOM field helpers (10 exports, 0 project deps)
│   ├── url-utils.js     # URL normalization + query parsing (9 exports)
│   ├── auth.js          # Auth checks, bootstrap, profile, email status
│   ├── settings-nav.js  # Settings section mgmt, save bar, dirty state
│   ├── misc.js          # Straggler utilities (6 exports)
│   └── events.js        # App event listeners (import-time side effects)
├── api.js               # Barrel re-exporting from app/* + app/events.js
├── ...
```

## Module Details

### 1. `app/dom.js` — DOM Field Helpers (~30 lines)

Pure DOM helpers. No project imports. Called by nearly every settings sub-module
and by event handlers.

Exports:

```
setFieldValue(id, value)
setFieldChecked(id, checked)
getFieldValue(id, fallback)
getFieldChecked(id, fallback)
setVisible(id, visible)
setText(id, value)
setDisabled(id, disabled)
setInlineStatus(id, message, type)
setInlineResult(el, message, className)
formDataObject(form)
```

### 2. `app/url-utils.js` — URL Utilities (~75 lines)

Imports `leapBibUrlPattern`, `leapPatronUrlPattern`, `statusStages`,
`stageQueryMap` from `state.js`.

Exports:

```
validateStaffUrl(value)
normalizeStaffUrl(value)
normalizeLeapBibUrlPattern(value)
normalizeLeapPatronUrlPattern(value)
leapBibUrl(bibId)
leapPatronUrl(patronId)
requestedStatusFromUrl()
requestedRequestIdFromUrl()
updateStageQuery(status)
```

### 3. `app/auth.js` — Auth, Bootstrap, Profile (~160 lines)

Imports `pb`, auth-related DOM refs, `bootstrapAdminMessage`, `setupRequired`,
`currentEmailStatus`, etc. from `state.js`. Imports `loadTab`,
`renderCurrentGrid`, `closeActionMenu` from `grid.js`. Imports `closeOpenDialogs`
from `dialogs.js`. Imports `authorizedJson` from `http.js`.

Exports:

```
staffRole()
isSuperAdminStaff()
isAdminStaff()
checkAuth()
showBootstrapAdminMessage()
loadSetupStatus()
loadEmailStatus(orgId)
updateEmailStatusBanner(status)
openProfileDialog()
```

Internal: `appliedProfileClaimFilterDefaultForStaffId`,
`profileDefaultClaimFilter()`, `applyProfileClaimFilterDefault()`,
`clearAppliedProfileClaimFilterDefault()`.

### 4. `app/settings-nav.js` — Settings Navigation & State (~240 lines)

Imports settings-related state from `state.js` (`settingsSectionIds`,
`currentSettingsSection`, `settingsDirty`, `settingsSaving`, `settingsLoading`,
`currentLibraryContextOrgId`, etc.). Imports `checkSettingsDirty`,
`handleLibraryContextSwitch`, `refreshLibrarySelectorIndicators` from
`settings.js`.

Exports:

```
getSettingsSectionFromHash()
updateSettingsSaveBarVisibility()
activateStatusTab(status)
updateSaveBarState(state)
markSettingsDirty()
markSettingsClean(state)
systemOnlySections                     // const array
libraryOverrideStatusSections          // const array
libraryContextSections                 // const array
updateLibraryOverrideStatusVisibility(section, contextOrgId)
activateSettingsSection(section, options)
initSettingsNavigation()
```

### 5. `app/misc.js` — Straggler Utilities (~140 lines)

Miscellaneous settings-related utilities that don't fit the other categories.
Imports from `state.js` (`currentRejectionTemplates`, `workflowSettings`,
`organizationsStatus`, etc.) and from `app/dom.js`.

Exports:

```
isPocketBaseAutoCancelError(err)
isValidSmtpHost(host)
validateSmtpHostField(showMessage)
updateAutoRejectEmailControls()
updateOrganizationsStatusUi(status, message)
postPolarisTest(url, resultEl, payload, options)
```

### 6. `app/events.js` — App Event Listeners (~270 lines)

All existing `addEventListener` calls and extraction helpers moved verbatim
from current api.js lines 647–919. Includes the login form submit handler,
setup form submit, setup test Polaris button, logout button, profile dialog
event listeners, status tabs click handlers, filter select change handlers,
grid search input handler, and `initRecentSuggestionsDropdown()`.

Imports from sibling `app/*` modules for utility functions, and from external
modules (`settings.js`, `grid.js`, `dialogs.js`, `http.js`, `state.js`,
`settings-polaris.js`, `settings-templates.js`, `recent-suggestions.js`) for
their specific exports.

No exports. Runs at import time when `api.js` imports it.

### 7. `api.js` — Barrel (~25 lines)

```
export * from './app/dom.js';
export * from './app/url-utils.js';
export * from './app/auth.js';
export * from './app/settings-nav.js';
export * from './app/misc.js';
export { authorizedJson, showToast, showAlert, showConfirm, closeOpenDialogs } from './http.js';
export { authorizedJson, showToast, showAlert, showConfirm, closeOpenDialogs } from './dialogs.js';
import './app/events.js';
```

All 47 exports preserved. Zero consumer import changes.

## Test Updates

Two source-inspection tests read from `api.js` directly and must be updated to
read from `app/settings-nav.js`:

1. `tests/settings_system_level_guard.test.js` — reads api.js source to check
   strings in `activateSettingsSection` (switch button text, context switch
   call). Extract from `app/settings-nav.js` instead.

2. `tests/settings_staff_scope_banner.test.js` — reads api.js source to
   extract `updateLibraryOverrideStatusVisibility`. Extract from
   `app/settings-nav.js` instead. Also checks for the string
   `"export const libraryContextSections = libraryOverrideStatusSections.concat(['staff']);"`
   which moves to the new file.

## Execution Phases

| Phase | What | Files |
|-------|------|-------|
| 0 | Baseline: capture all exports, verify test output | (inspection only) |
| 1 | Create `app/dom.js` + `app/url-utils.js` | 2 new files |
| 2 | Create `app/auth.js` | 1 new file |
| 3 | Create `app/settings-nav.js` | 1 new file |
| 4 | Create `app/misc.js` + `app/events.js` | 2 new files |
| 5 | Convert `api.js` to barrel | 1 modified file |
| 6 | Update 2 source-inspection tests | 2 modified files |
| 7 | `npm test`, commit | — |

## Risk Notes

- **Circular deps**: None. `app/*` only imports from `state.js`, `grid.js`,
  `dialogs.js`, `http.js`, `settings.js` — never from `api.js`. `api.js`
  barrel imports from `app/*` unidirectionally.
- **Event timing**: `app/events.js` runs when `api.js` is first imported,
  same as today. No change to boot sequence.
- **The re-export of `authorizedJson`, `showToast` etc.** from both `http.js`
  and `dialogs.js` is preserved exactly as current api.js line 9 does it.
- **Zero consumer import changes**: All 13 existing consumers continue
  importing from `./api.js` as before.
