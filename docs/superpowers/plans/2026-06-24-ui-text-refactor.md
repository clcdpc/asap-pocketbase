# ui_text.js Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** split `lib/config/ui_text.js` (347 lines) into focused sub-modules under `lib/config/ui-text/`, keeping `lib/config/ui_text.js` as a barrel so existing consumers (`lib/config.js`, `lib/config/settings.js`) continue to work.

**Architecture:** extract individual setting-type resolvers into leaf sub-modules. Keep the composition function `uiTextFromRecord` and the public entrypoint `uiText` in the barrel since they orchestrate across all resolvers. The barrel re-exports everything.

This is a mechanical refactor only. Do not change scope resolution or fallback behavior for publication options, duplicate status labels, branding, material formats, or patron settings overrides.

**Tech Stack:** CommonJS backend modules, existing `require()` patterns, PocketBase record APIs.

---

## File Structure

### Create
- `lib/config/ui-text/records.js` — `uiRecord`, `patronSettingsOverrideRecord`, `legacyPatronLibrarySettingsRecord`
- `lib/config/ui-text/duplicate-labels.js` — `duplicateStatusLabelsFromUiRecord`, `hasAnyDuplicateStatusLabel`, `duplicateStatusLabelResolution`, `mergeDuplicateStatusLabels`, `duplicateStatusLabels`
- `lib/config/ui-text/formats.js` — `materialFormats`

### Modify
- `lib/config/ui_text.js` — convert from single-file implementation to barrel that imports from sub-modules and keeps `uiTextFromRecord` + `uiText`

### No change
- `lib/config.js` — imports from `./config/ui_text.js`
- `lib/config/settings.js` — imports from `./ui_text.js` (resolves to `lib/config/ui_text.js`)
- Any test that imports through one of those consumers

---

### Task 1: Extract Record-Access Module

**Files:**
- Create: `lib/config/ui-text/records.js`

Move these functions:

```js
const dbHelpers = require("../db_helpers.js");
const systemRecord = dbHelpers.systemRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;
const safeCollection = dbHelpers.safeCollection;

function uiRecord(app, orgId) { /* ... */ }
function patronSettingsOverrideRecord(app, orgId) { /* ... */ }
function legacyPatronLibrarySettingsRecord(app, orgId) { /* ... */ }

module.exports = { uiRecord, patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord };
```

**Current lines:** 26-47, 82-102.

---

### Task 2: Extract Duplicate-Labels Module

**Files:**
- Create: `lib/config/ui-text/duplicate-labels.js`

Move these functions:

```js
const defaults = require("../defaults.js");
const normalization = require("../normalization.js");
const { uiRecord, patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord } = require("./records.js");

const parseJsonObject = normalization.parseJsonObject;
const defaultDuplicateStatusLabels = defaults.defaultDuplicateStatusLabels;

function duplicateStatusLabelsFromUiRecord(record) { /* ... */ }
function hasAnyDuplicateStatusLabel(labels) { /* ... */ }
function duplicateStatusLabelResolution(app, orgId, systemUiRecord) { /* ... */ }
function mergeDuplicateStatusLabels(labels) { /* ... */ }
function duplicateStatusLabels(app, orgId) { /* ... */ }

module.exports = { duplicateStatusLabelsFromUiRecord, hasAnyDuplicateStatusLabel, duplicateStatusLabelResolution, mergeDuplicateStatusLabels, duplicateStatusLabels };
```

Note: `hasAnyDuplicateStatusLabel` is a tiny helper used only by `duplicateStatusLabelResolution`. Keeping it in this module is fine since they're closely related.

**Current lines:** 49-80, 104-149, 236-242.

---

### Task 3: Extract Formats Module

**Files:**
- Create: `lib/config/ui-text/formats.js`

Move this function:

```js
const dbHelpers = require("../db_helpers.js");
const orgIdForSettings = dbHelpers.orgIdForSettings;

function materialFormats(app, orgId) { /* ... */ }

module.exports = { materialFormats };
```

**Current lines:** 151-234.

---

### Task 4: Convert ui_text.js To Barrel + Orchestration

**Files:**
- Modify: `lib/config/ui_text.js`

After extracting Tasks 1-3, what remains in `ui_text.js` is:
- The lazy `getFormatRules()` helper
- `uiTextFromRecord(app, record, orgId)` — the main composition function
- `uiText(app, orgId)` — the public entrypoint

Replace the file with:

```js
const customFields = require("../custom_fields.js");
const normalization = require("./normalization.js");
const workflows = require("./workflows.js");

const {
  uiRecord,
  patronSettingsOverrideRecord,
  legacyPatronLibrarySettingsRecord
} = require("./ui-text/records.js");

const {
  duplicateStatusLabelsFromUiRecord,
  hasAnyDuplicateStatusLabel,
  duplicateStatusLabelResolution,
  mergeDuplicateStatusLabels,
  duplicateStatusLabels
} = require("./ui-text/duplicate-labels.js");

const { materialFormats } = require("./ui-text/formats.js");

const normalizeOptionList = normalization.normalizeOptionList;
const enabledOptionLabels = normalization.enabledOptionLabels;
const parseJsonObject = normalization.parseJsonObject;

const workflowFromRecord = workflows.workflowFromRecord;
const workflowRecord = workflows.workflowRecord;

let _formatRules;
function getFormatRules() {
  if (!_formatRules) _formatRules = require(`${__hooks}/../lib/format_rules.js`);
  return _formatRules;
}

function uiTextFromRecord(app, record, orgId) {
  // ... stays as-is, uses imported resolvers
}

function uiText(app, orgId) {
  app = app || $app;
  return uiTextFromRecord(app, uiRecord(app, orgId), orgId);
}

// Re-export sub-module functions so consumers don't break
module.exports = {
  uiRecord, duplicateStatusLabelsFromUiRecord, hasAnyDuplicateStatusLabel,
  patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord,
  duplicateStatusLabelResolution, materialFormats,
  mergeDuplicateStatusLabels, duplicateStatusLabels,
  uiTextFromRecord, uiText
};
```

Note: `legacyPatronLibrarySettingsRecord` needs to be imported and re-exported even though it's only used internally by `duplicateStatusLabelResolution`. It was originally exported by the module.exports, so it must stay in the barrel.

---

### Task 5: Verify

**Files:**
- No additional edits.

- [ ] **Step 0a: Verify moved-file require paths**

Confirm every `require()` inside the new `lib/config/ui-text/*.js` files is relative to its new location:

- `records.js`
  - `require("../db_helpers.js")`
- `duplicate-labels.js`
  - `require("../defaults.js")`
  - `require("../normalization.js")`
  - `require("./records.js")`
- `formats.js`
  - `require("../db_helpers.js")`

Confirm `lib/config/ui_text.js` imports the moved modules from:

- `require("./ui-text/records.js")`
- `require("./ui-text/duplicate-labels.js")`
- `require("./ui-text/formats.js")`

Do not leave any moved module using the old same-directory paths such as `require("./db_helpers.js")`, `require("./defaults.js")`, or `require("./normalization.js")`.

- [ ] **Step 0b: Smoke-test barrel exports with function check**

```bash
node -e "const ui = require('./lib/config/ui_text.js'); ['uiRecord','patronSettingsOverrideRecord','legacyPatronLibrarySettingsRecord','duplicateStatusLabelResolution','materialFormats','uiText'].forEach(k => { if (typeof ui[k] !== 'function') throw new Error(k + ' is not a function'); }); console.log('ui_text barrel exports OK')"
```

Expected:

```txt
ui_text barrel exports OK
```

- [ ] **Step 0c: Verify barrel export names**

```bash
node -e "const ui = require('./lib/config/ui_text.js'); console.log(Object.keys(ui).sort().join('\n'))"
```

Expected:

```txt
duplicateStatusLabelResolution
duplicateStatusLabels
duplicateStatusLabelsFromUiRecord
hasAnyDuplicateStatusLabel
legacyPatronLibrarySettingsRecord
materialFormats
mergeDuplicateStatusLabels
patronSettingsOverrideRecord
uiRecord
uiText
uiTextFromRecord
```

- [ ] **Step 1: Run the config-focused tests**

Run:

```bash
node tests/config_ui_text_patron_options_scope.test.js
node tests/config_parseJsonObject.test.js
node tests/config_parseJsonArray.test.js
```

Expected: All pass.

- [ ] **Step 2: Run the settings tests**

Run:

```bash
node tests/settings_read_normalization.test.js
node tests/settings_serialization.test.js
```

Expected: All pass.

- [ ] **Step 3: Run the full suite**

Run:

```bash
npm test
```

Expected: All tests passed.
