# settings.js Refactor Design

## Goal
Structural refactor only — split the 1294-line `settings.js` god module into focused sub-modules under `settings/`. Zero behavior changes, zero consumer import changes, all tests pass.

## Rationale
`settings.js` is the single largest file in the staff JS bundle (1294 lines, 31 exports). It acts as a god module combining:

- Load orchestration and app init
- Library context selection and persistence
- Form population from server data
- Serialization and payload building
- Dirty tracking and save
- Toggle visibility groups
- Pure utility functions
- 17 event listener registrations

The six responsibilities are already cleanly separable with minimal internal coupling.

## Consumer analysis (7 modules import from `./settings.js`)

| Consumer | Imports | Circular? |
|---|---|---|
| `app.js` | `initStaffApp` | No |
| `app/nav.js` | `checkSettingsDirty`, `handleLibraryContextSwitch`, `refreshLibrarySelectorIndicators` | No |
| `app/auth.js` | `loadSettings` | No |
| `settings-labels.js` | `loadLibrarySettings` | **Yes** |
| `settings-polaris.js` | `populateLibrarySelector`, `saveSettings` | **Yes** |
| `grid-data.js` | `showSettingsAccessDenied`, `hideSettingsAccessDenied`, `refreshSettingsView` | No |
| `patron.js` | `populateLibrarySelector` | No |

All imports use `./settings.js` — keeping the barrel at that path means zero consumer file changes. The two circular deps (`settings-labels.js`, `settings-polaris.js`) are fixed by pointing those imports to specific sub-modules instead of the barrel.

## Proposed structure

```
pb_public/staff/js/
  settings.js              ← barrel: re-exports all 31 exports + 17 event listener registrations (~180 lines)
  settings/
    loader.js              ← load orchestration, app init (6 exports, ~155 lines)
    library-context.js     ← library selector, context switching (6 exports, ~165 lines)
    form-population.js     ← applying server data to DOM (3 exports, ~270 lines)
    serialize-save.js      ← serialization, dirty tracking, save (9 exports, ~260 lines)
    toggles.js             ← toggle visibility groups (5 exports, ~50 lines)
    utils.js               ← pure utility functions (2 exports, ~30 lines)
```

## Module details

### 1. `settings/utils.js` (~30 lines, 2 exports)
- `normalizeExternalSearchUrlTemplate(value)` — trims and prepends `https://` if missing
- `sortAuthorsByLastName(authorsListStr)` — sorts newline-separated author names by last name

Imports: none (pure functions)

### 2. `settings/toggles.js` (~50 lines, 5 exports)
- `toggleTimeoutGroup()` — shows/hides `#timeout-config-group` based on `#outstanding-timeout-enabled` checkbox
- `toggleHoldPickupTimeoutGroup()` — shows/hides `#hold-pickup-timeout-group`
- `togglePendingHoldTimeoutGroup()` — shows/hides `#pending-hold-timeout-group`
- `toggleAdditionalCopyTimeoutGroup()` — shows/hides `#additional-copy-timeout-group`
- `toggleCommonAuthorsGroup()` — shows/hides `#common-authors-config-group`

**AGENTS.md Requirement (Frontend DOM Safety):** Audit extracted toggle logic for any `innerHTML` usage. If `innerHTML` exists, refactor it to use safe DOM APIs (`document.createElement`, `replaceChildren`, `classList`, etc.).

Imports: none (pure DOM helpers)

### 3. `settings/form-population.js` (~270 lines, 3 exports)
- `applyLibrarySettingsToForm(settings)` — mega-function (~110 lines): populates system fields, library-override banners, SMTP, Polaris, patron embed, email templates, patron UI, workflow, format rules, publication options
- `populateWorkflowForms(wf)` — populates all workflow fields: suggestion limit, timeouts, common authors, external search, auto-promote, card-login
- `populatePatronUiForms(uiText)` — populates patron-facing text fields: logo, page title, barcode/pin labels, messages, format labels/order, publication options, additional fields, format rules

Internal helpers: `populateSystemSettingsForms`, `populateSmtpSettingsForm`, `populatePolarisSettingsForm`, `patronPortalUrl`, `buildIframeEmbedCode`, `buildLoaderEmbedCode`, `updatePatronEmbedSnippet`

**AGENTS.md Requirement (Frontend DOM Safety):** Audit the 270 lines of form population logic for any `innerHTML` usage. When migrating, refactor any `innerHTML` assignments to safe DOM APIs (`document.createElement`, `replaceChildren`, `textContent`, etc.).

Imports: `state.js`, `api.js`, `./toggles.js`, `settings-formats.js`, `settings-labels.js`, `settings-ui.js`, `settings-templates.js`, `settings-additional-fields.js`, `settings-polaris.js`

### 4. `settings/serialize-save.js` (~260 lines, 9 exports)
- `serializeSettingsState()` — public wrapper for `_serializeSettingsState(false)` (no validation)
- `buildSettingsPayload()` — public wrapper for `_serializeSettingsState(true)` (with validation)
- `saveSettings(options)` — validates SMTP, builds payload, POSTs, refreshes view
- `updateSaveButtonText()` — sets "Save System Defaults" vs "Save Library Settings"
- `captureSettingsBaseline()` — snapshots current form state to detect changes
- `checkSettingsDirty()` — compares current form state against baseline
- `cloneLibrarySettingsSnapshot(settings)` — deep-clone via JSON round-trip
- `rememberLastSavedLibrarySettings(settings)` — stores snapshot + current orgId
- `discardLibrarySettingsChanges()` — restores last saved snapshot to form

Internal: `_serializeSettingsState(validate)` (160-line real serialization engine), `positiveInt` (inline helper)

**AGENTS.md Requirements:**
1. **System-Only Payload Safety**: When extracting `_serializeSettingsState`, strictly preserve the logic distinguishing system saves from library saves to verify that library-context saves never include system-only payload keys (e.g. SMTP, Polaris) while system-context saves do.
2. **JSON Request Serialization**: For the `saveSettings` POST logic, ensure you pass plain object bodies instead of pre-stringifying them, letting the shared request helper handle the `Content-Type` header natively.

Imports: `state.js`, `api.js`, `http.js`, `dialogs.js`, `./utils.js`, `settings-formats.js`, `settings-labels.js`, `settings-polaris.js`, `settings-ui.js`, `settings-additional-fields.js`, `./loader.js`

### 5. `settings/library-context.js` (~165 lines, 6 exports)
- `populateLibrarySelector()` — fills `<select>` with Polaris organizations, restores saved context, binds change listener
- `switchLibraryContext(orgId, select?)` — core context switch: dirty-check, update state, reload library settings
- `handleLibraryContextSwitch(orgId)` — public wrapper, finds the `<select>` and delegates
- `loadLibrarySettings(orgId)` — fetches `/api/asap/staff/settings/library?orgId=...`, applies to form, captures baseline
- `fetchLibraryOverridesSummary()` — fetches overrides-summary endpoint, stores in state
- `refreshLibrarySelectorIndicators()` — adds `●` dots to selector options that have overrides in active section

Internal: `readSavedSuperAdminLibraryContext`, `saveSuperAdminLibraryContext`, `maybeSyncPolarisOrganizations`, `updateWorkflowSettingsSummary`

**AGENTS.md Requirement (Frontend Request Guards):** `loadLibrarySettings(orgId)` triggers a network request when the library context changes. Protect this screen-level staff load with abort-plus-stale-result guards (implementing an `AbortController` pattern) to prevent stale data from rendering during rapid context switches.

Imports: `state.js`, `api.js`, `http.js`, `./form-population.js`

**This module breaks both circular deps.**
- `settings-labels.js` → imports `loadLibrarySettings` from `./settings/library-context.js` (no cycle)
- `settings-polaris.js` → imports `populateLibrarySelector` and `saveSettings` from `./settings/library-context.js` and `./settings/serialize-save.js` (no cycle)

### 6. `settings/loader.js` (~155 lines, 6 exports)
- `loadSettings(options)` — main entry: loads library context, library settings, syncs Polaris, populates forms, loads staff access
- `refreshSettingsView(options)` — thin wrapper delegating to `loadSettings`
- `loadStaffConfig()` — fetches `/api/asap/config`, updates logo, title, publication options, additional fields
- `initStaffApp()` — app bootstrap: closes dialogs/menus, inits nav, loads config, loads setup status, checks auth
- `showSettingsAccessDenied()` — shows settings-error block, hides settings-form
- `hideSettingsAccessDenied()` — hides settings-error block

Internal: `updateSettingsSidebar`, `ensureAllowedSettingsSection`, `loadLibraryContext`, `loadLibraryAdminSettings`, `loadStaffAccessSettings`, `handleLoadSettingsError`, `showSettingsForm`

Imports: `state.js`, `api.js`, `http.js`, `./library-context.js`, `./form-population.js`, `./toggles.js`, `settings-ui.js`

### 7. `settings.js` (barrel, ~180 lines)

Contains:
- Re-exports all 31 exports from the 6 sub-modules (`export { ... } from './settings/...'`)
- The two constants (`adminSettingsSections`, `SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY`)
- The two `createLatestLoad()` instances (`settingsLoads`, `librarySettingsLoads`)
- **All 17 event listener registrations** at lines 1048-1294:
  - `settingsForm` submit → `saveSettings`
  - `#settings-discard-btn` click → `discardLibrarySettingsChanges`
  - `settingsForm` input/change → `markSettingsDirty`
  - `#ui-publication-options-editor` click → `handleOptionListClick`
  - `#btn-add-publication-option` click → `addOptionListRow`
  - `.patron-copy-btn` click → clipboard copy
  - `#outstanding-timeout-enabled` change → `toggleTimeoutGroup` + `updateAutoRejectEmailControls`
  - `#outstanding-timeout-send-email` change → `updateAutoRejectEmailControls`
  - `#hold-pickup-timeout-enabled` change → `toggleHoldPickupTimeoutGroup`
  - `#pending-hold-timeout-enabled` change → `togglePendingHoldTimeoutGroup`
  - `#additional-copy-timeout-enabled` change → `toggleAdditionalCopyTimeoutGroup`
  - `#wf-common-authors-enabled` change → `toggleCommonAuthorsGroup`
  - `#edit-bibid` keydown → trigger BIB lookup
  - `#edit-bibid` input → update LEAP link, clear verified BIB
  - `#ui-logo-file` change → file preview
  - `#btn-upload-logo` click → logo upload
  - `#btn-reset-logo` click → logo reset

## `loadStaffConfig` — raw fetch to `authorizedJson`
Currently uses raw `fetch` (line 1077). Since this function moves to `settings/loader.js`, switch to `authorizedJson` as part of the refactor to match the pattern used in the rest of the codebase.

**AGENTS.md Requirement (JSON Request Serialization):** When migrating raw `fetch` calls to `authorizedJson`, ensure that if there are request bodies (like in `saveSettings`), you pass them as plain objects instead of pre-stringifying them. Let the shared request helper handle the `Content-Type` header natively.


## Test impact

| Test | Impact | Required change |
|---|---|---|
| `tests/external_search_provider4.test.js` | **Direct source read** of `settings.js` to extract `normalizeExternalSearchUrlTemplate` via string op (line 83-84) | Update to read from `settings/utils.js` |
| `tests/settings_serialization.test.js` | Inline-mocks `normalizeExternalSearchUrlTemplate` and `sortAuthorsByLastName` at test level (line 57, 66) | No change needed (mocks are inline definitions, not imports) |
| `tests/settings_system_level_guard.test.js` | Already reads exports from `app/nav.js` | No change |
| `tests/settings_staff_scope_banner.test.js` | Same | No change |
| Remaining 11 settings tests | Mock at module level | No change expected |
| `tests/module_import_paths.test.js` | Should validate new sub-module paths after refactor | Add coverage (already exists as general validator) |

## Circular dependency fix

| Current | New path | Effect |
|---|---|---|
| `settings-labels.js` → `import { loadLibrarySettings } from './settings.js'` | → `import { loadLibrarySettings } from './settings/library-context.js'` | ✅ Cycle broken |
| `settings-polaris.js` → `import { populateLibrarySelector, saveSettings } from './settings.js'` | → import from `./settings/library-context.js` and `./settings/serialize-save.js` | ✅ Cycle broken |

## Execution order

1. Create `settings/` directory
2. Create `settings/utils.js` — extract `normalizeExternalSearchUrlTemplate`, `sortAuthorsByLastName`
3. Create `settings/toggles.js` — extract all 5 toggle functions
4. Create `settings/form-population.js` — extract `applyLibrarySettingsToForm`, `populateWorkflowForms`, `populatePatronUiForms` + 7 internal helpers
5. Create `settings/serialize-save.js` — extract serialization, dirty tracking, save functions + `_serializeSettingsState`
6. Create `settings/library-context.js` — extract library context functions + 4 internal helpers
7. Create `settings/loader.js` — extract load orchestration + app init
8. Rewrite `settings.js` as barrel: re-exports + constants + event listeners
9. Update `settings-labels.js` — change import from `./settings.js` to `./settings/library-context.js`
10. Update `settings-polaris.js` — change imports from `./settings.js` to sub-module paths
11. Update `tests/external_search_provider4.test.js` — read from `settings/utils.js` instead of `settings.js`
12. Run `npm test` — verify all pass
13. Run import path validator — verify all new paths resolve

## Manual verification checklist

- Save at system level persists correctly
- Save at library level persists correctly
- Library value overrides system value only where intended
- Removing or disabling an override falls back to system value
- Switching between libraries does not leak values from another library
- Reopening Settings shows the correct value for the selected scope
- Logo upload and reset still work
- Library context persistence across page reloads
- Dirty-tracking indicator on field change
- Settings dirty-check blocks library context switch when unsaved changes exist
