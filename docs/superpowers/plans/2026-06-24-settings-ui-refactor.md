# settings-ui.js Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** split `pb_public/staff/js/settings-ui.js` (917 lines) into focused sub-modules under `pb_public/staff/js/settings/`, keeping `settings-ui.js` as a barrel file so existing importers do not break.

**Architecture:** follow the existing pattern where `settings.js` re-exports from `settings/*.js` sub-modules. Each sub-module owns one settings-area concern and exposes only the functions its consumers need. The barrel re-exports everything so existing import paths (`'./settings-ui.js'`, `'../settings-ui.js'`) continue to work without editing consumer files.

**Tech Stack:** ES modules in `pb_public/staff/js/`, existing `settings/` sub-directory pattern.

---

## File Structure

### Create
- `pb_public/staff/js/settings/option-list.js` — option list normalization, rendering, editing, drag-and-drop (replaces lines 7-112, 535-709)
- `pb_public/staff/js/settings/format-rules.js` — patron format rules normalisation, rendering, collecting, accordion wiring (replaces lines 116-533)
- `pb_public/staff/js/settings/bib-lookup.js` — BIB lookup API call, Polaris result application to edit form, hidden-field helpers (replaces lines 711-888)
- `pb_public/staff/js/settings/smtp-wiring.js` — SMTP host validation wiring, `dateOnly`, `syncInputPair` (replaces lines 890-917)

### Modify
- `pb_public/staff/js/settings-ui.js` — convert from implementation to barrel file that re-exports from the four sub-modules

### No change (existing test coverage)
- `tests/polaris_grid_search_ui.test.js` — scans source for string values; barrel preserves exact exported names so these stay green
- `tests/staff_public_option_selects.test.js` — extracts functions by name from source text; barrel preserves the extracted function names
- `pb_public/staff/js/settings.js` — already imports `handleOptionListClick` and `addOptionListRow` from `settings-ui.js` which becomes a barrel, so no change needed (but it's a good candidate for a follow-up direct-import cleanup)

---

### Task 1: Extract Option-List Module

**Files:**
- Create: `pb_public/staff/js/settings/option-list.js`

- [ ] **Step 1: Write the new module**

Create `settings/option-list.js` with these exports. Move the exact code from `settings-ui.js`:

```js
import { publicationOptions, defaultPublicationOptions } from '../state.js';
import { markSettingsDirty } from '../api.js';
import { showToast } from '../dialogs.js';
import { escapeAttr } from '../grid.js';

export function optionIdFromLabel(label, fallback = 'option') { /* ... */ }
export function normalizeOptionList(options, fallbackLabels) { /* ... */ }
export function enabledOptionLabels(options, fallbackLabels) { /* ... */ }
export function updatePublicationOptionsUi(options) { /* ... */ }
export function setSelectValue(select, value) { /* ... */ }
export function renderOptionListEditor(editorId, options, fallbackLabels) { /* ... */ }
export function collectOptionList(editorId, fallbackLabels) { /* ... */ }
export function addOptionListRow(editorId, fallbackLabels) { /* ... */ }
export function handleOptionListClick(event) { /* ... */ }

let optionDraggingRow = null;
// dragstart / dragend / dragover / drop document listeners
```

Keep the `isByteArray`, `decodeByteArray` helpers as private module functions (they are only used by `normalizeOptionList` and `updatePublicationOptionsUi`).

- [ ] **Step 2: Verify the exports match the originals**

Compare the exported symbols against the current `settings-ui.js` exports:
- `optionIdFromLabel` ✓
- `normalizeOptionList` ✓
- `enabledOptionLabels` ✓
- `updatePublicationOptionsUi` ✓
- `setSelectValue` ✓
- `renderOptionListEditor` ✓
- `collectOptionList` ✓
- `addOptionListRow` ✓
- `handleOptionListClick` ✓

- [ ] **Step 3: Remove extracted code from `settings-ui.js`**

Delete lines 7-112 (option helpers + byte array decode) and lines 535-709 (option list editor, drag-and-drop) from `settings-ui.js`. Leave a comment: `// moved to settings/option-list.js`.

---

### Task 2: Extract Format-Rules Module

**Files:**
- Create: `pb_public/staff/js/settings/format-rules.js`

- [ ] **Step 1: Write the new module**

Create `settings/format-rules.js` with these exports:

```js
import { formatMap, availableFormats, patronFormatKeys, patronFormatFields, defaultPatronFormatRules, additionalFieldDefinitions } from '../state.js';

export function normalizePatronFormatRules(rules) { /* ... */ }
export function getPatronFormatRuleSummary(rule) { /* ... */ }
export function renderPatronFormatRulesEditor(rules) { /* ... */ }
export function collectPatronFormatRules() { /* ... */ }
```

Also move the two `document.addEventListener` listeners for `input` and `change` (accordion summary updates and message-behavior toggle):

```js
// Format-rules accordion wiring
document.addEventListener('input', (e) => { /* ... */ });
document.addEventListener('change', (e) => { /* ... */ });
```

- [ ] **Step 2: Verify the exports match**

Expected exports: `normalizePatronFormatRules`, `getPatronFormatRuleSummary`, `renderPatronFormatRulesEditor`, `collectPatronFormatRules`. These are the only names imported by consumers.

- [ ] **Step 3: Remove extracted code from `settings-ui.js`**

Delete lines 116-533 (format rules functions and event listeners). Leave a comment: `// moved to settings/format-rules.js`.

---

### Task 3: Extract BIB-Lookup Module

**Files:**
- Create: `pb_public/staff/js/settings/bib-lookup.js`

- [ ] **Step 1: Write the new module**

Create `settings/bib-lookup.js` with these exports:

```js
import { currentSuggestions, setVerifiedBibId } from '../state.js';
import { authorizedJson } from '../http.js';

export async function lookupEditBibById(options = {}) { /* ... */ }
export function applySelectedPolarisResultToEditForm(result = {}, context = 'edit') { /* ... */ }
```

Keep these as private helpers in the same module:
```js
function mergeCatalogValue(catalogValue, oldValue) { /* ... */ }
function setHiddenEditValue(id, value) { /* ... */ }
```

Also move the `btn-bib-lookup` click listener (with an added null check to ensure safe import):
```js
const btnBibLookup = document.getElementById('btn-bib-lookup');
if (btnBibLookup) {
  btnBibLookup.addEventListener('click', async () => {
    await lookupEditBibById();
  });
}
```

- [ ] **Step 2: Verify the exports match**

Expected exports: `lookupEditBibById`, `applySelectedPolarisResultToEditForm`.

- [ ] **Step 3: Remove extracted code from `settings-ui.js`**

Delete lines 795-888 (BIB lookup function, Polaris result application, mergeCatalogValue, setHiddenEditValue, btn-bib-lookup listener). Leave a comment.

---

### Task 4: Extract SMTP-Wiring Module

**Files:**
- Create: `pb_public/staff/js/settings/smtp-wiring.js`

- [ ] **Step 1: Write the new module**

Create `settings/smtp-wiring.js` with these exports:

```js
import { isValidSmtpHost, validateSmtpHostField } from '../api.js';

export function dateOnly(value) { /* ... */ }
export function syncInputPair(idA, idB) { /* ... */ }

// SMTP wiring
syncInputPair('email-from-address', 'smtp-from');
syncInputPair('email-from-name', 'smtp-from-name');
const smtpHostInput = document.getElementById('smtp-host');
if (smtpHostInput) {
  smtpHostInput.addEventListener('blur', () => validateSmtpHostField(true));
  smtpHostInput.addEventListener('input', () => { /* ... */ });
}
```

- [ ] **Step 2: Verify the exports match**

Expected exports: `dateOnly`, `syncInputPair`.

- [ ] **Step 3: Remove extracted code from `settings-ui.js`**

Delete lines 890-917 (dateOnly, syncInputPair, SMTP host validation). Leave a comment.

---

### Task 5: Convert settings-ui.js To Barrel

**Files:**
- Modify: `pb_public/staff/js/settings-ui.js`

- [ ] **Step 1: Replace the implementation with re-exports**

After removing all the implementation code in Tasks 1-4, convert `settings-ui.js` to a barrel file:

```js
export { optionIdFromLabel, normalizeOptionList, enabledOptionLabels, updatePublicationOptionsUi, setSelectValue, renderOptionListEditor, collectOptionList, addOptionListRow, handleOptionListClick } from './settings/option-list.js';
export { normalizePatronFormatRules, getPatronFormatRuleSummary, renderPatronFormatRulesEditor, collectPatronFormatRules } from './settings/format-rules.js';
export { lookupEditBibById, applySelectedPolarisResultToEditForm } from './settings/bib-lookup.js';
export { dateOnly, syncInputPair } from './settings/smtp-wiring.js';
```

- [ ] **Step 2: Verify the file is clean**

The barrel should be approximately 20 lines — just imports and re-exports. The file path stays `pb_public/staff/js/settings-ui.js` so all existing import paths continue to work.

---

### Task 6: Run Verification

**Files:**
- No additional edits.

- [ ] **Step 1: Run the import path test**

Run:

```bash
node tests/module_import_paths.test.js
```

Expected: All 1100+ import paths resolve correctly.

- [ ] **Step 2: Run the settings-specific tests**

Run:

```bash
node tests/staff_public_option_selects.test.js
node tests/polaris_grid_search_ui.test.js
node tests/settings_accordion_behavior.test.js
node tests/settings_serialization.test.js
```

Expected: All pass.

- [ ] **Step 3: Run the frontend request architecture test**

Run:

```bash
node tests/frontend_request_architecture.test.js
```

Expected: All pass.

- [ ] **Step 4: Run the full suite**

Run:

```bash
npm test
```

Expected: All tests passed.
