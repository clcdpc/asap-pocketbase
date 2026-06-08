# Library Additional Fields Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add library-only additional patron form fields that can be reused across formats, submitted by patrons, and viewed/edited by staff.

**Architecture:** Store field definitions on `patron_settings_overrides`, per-format modes inside existing `patronFormatRules`, and submitted values on `title_requests.customFields`. Normalize definitions and values in a focused shared module, then have patron and staff UI render dynamic fields with DOM APIs and no dynamic `innerHTML`.

**Tech Stack:** PocketBase JS hooks, PocketBase migrations, vanilla JavaScript modules, jsdom tests, Node test runner via `npm test`.

---

## File Structure

- Create `lib/custom_fields.js`: shared backend normalization and validation for definitions, per-format modes, and submitted values.
- Modify `lib/format_rules.js`: include `customFields` in format rule normalization and call `custom_fields` from patron submission validation.
- Modify `lib/config/ui_text.js`: load `additionalFieldDefinitions` from `patron_settings_overrides` for the selected library and include them in `ui_text`.
- Modify `lib/staff/settings_ui.js`: save library-only `additionalFieldDefinitions`; keep system saves from writing them.
- Modify `lib/records/suggestions.js`: persist `customFields`, expose it in request JSON, and update it during staff edits.
- Add `pb_migrations/202606080001_library_additional_fields.js`: add JSON fields with PocketBase collection APIs.
- Modify `pb_migrations/0000000000_initial.js`: add the same fields for fresh installs.
- Create `pb_public/staff/js/settings-additional-fields.js`: render the Additional Fields accordion and collect definitions with DOM APIs.
- Modify `pb_public/staff/js/settings.js`: load/save additional field definitions and place the accordion above format rules in Patron Experience.
- Modify `pb_public/staff/js/settings-ui.js`: add per-format custom field mode controls in the format rules accordion.
- Modify `pb_public/staff/js/state.js`: add frontend default state for `additionalFieldDefinitions`.
- Modify `pb_public/patron/js/config.js`: normalize public `additionalFieldDefinitions` into client state.
- Modify `pb_public/patron/js/state.js`: add client state for additional field definitions.
- Create `pb_public/patron/js/custom-fields.js`: render and collect patron additional fields with DOM APIs.
- Modify `pb_public/patron/js/form-ui.js`: render additional fields when the selected format changes.
- Modify `pb_public/patron/js/submit.js`: include additional field values in submission payload.
- Modify `pb_public/staff/js/modals.js`: render, collect, and submit staff-editable custom fields.
- Modify `pb_public/staff/index.html`: add the Additional Fields accordion container.
- Modify `pb_public/patron/index.html`: add a patron additional fields container under canonical physical fields.
- Tests: add backend tests in `tests/custom_fields.test.js`, extend config/settings/records tests, add jsdom UI tests for staff and patron rendering.

## Task 1: Backend Custom Field Normalization

**Files:**
- Create: `lib/custom_fields.js`
- Test: `tests/custom_fields.test.js`
- Modify: `tests/run_all.js`

- [ ] **Step 1: Write failing backend normalization tests**

Add `tests/custom_fields.test.js`:

```js
const assert = require("assert");
const customFields = require("../lib/custom_fields.js");

function testNormalizeDefinitions() {
  const defs = customFields.normalizeDefinitions([
    {
      key: "Platform!",
      label: " Platform ",
      type: "select",
      helpText: " Preferred platform ",
      enabled: true,
      sortOrder: 20,
      options: [
        { id: "Switch", label: " Nintendo Switch ", enabled: true, sortOrder: 20 },
        { label: "PlayStation 5", enabled: true, sortOrder: 10 },
        { id: "Switch", label: "Duplicate", enabled: true, sortOrder: 30 }
      ]
    },
    { key: "bad", label: "", type: "date" }
  ]);

  assert.deepStrictEqual(defs.map(d => d.key), ["platform"]);
  assert.strictEqual(defs[0].label, "Platform");
  assert.strictEqual(defs[0].type, "select");
  assert.strictEqual(defs[0].helpText, "Preferred platform");
  assert.deepStrictEqual(defs[0].options.map(o => o.id), ["playstation_5", "switch"]);
  assert.deepStrictEqual(defs[0].options.map(o => o.label), ["PlayStation 5", "Nintendo Switch"]);
}

function testNormalizeFormatCustomFieldRules() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Switch" }] },
    { key: "content_note", label: "Content note", type: "textarea" },
    { key: "disabled_field", label: "Disabled", type: "text", enabled: false }
  ]);
  const rules = customFields.normalizeFormatCustomFieldRules({
    platform: { mode: "required" },
    content_note: { mode: "banana" },
    disabled_field: { mode: "required" },
    unknown: { mode: "required" }
  }, defs);

  assert.deepStrictEqual(rules, {
    platform: { mode: "required" },
    content_note: { mode: "optional" },
    disabled_field: { mode: "hidden" }
  });
}

function testSanitizeSubmittedValues() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] },
    { key: "content_note", label: "Content note", type: "textarea" },
    { key: "edition", label: "Edition", type: "text" }
  ]);
  const rules = {
    platform: { mode: "required" },
    content_note: { mode: "optional" },
    edition: { mode: "hidden" }
  };
  const sanitized = customFields.sanitizeSubmittedValues({
    platform: "Nintendo Switch",
    content_note: "  Keep as plain text  ",
    edition: "discard me",
    unknown: "discard me"
  }, defs, rules);

  assert.deepStrictEqual(sanitized, {
    platform: { label: "Platform", type: "select", value: "switch", displayValue: "Nintendo Switch" },
    content_note: { label: "Content note", type: "textarea", value: "Keep as plain text" }
  });
}

function testRequiredValueError() {
  const defs = customFields.normalizeDefinitions([
    { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
  ]);
  assert.throws(
    () => customFields.sanitizeSubmittedValues({}, defs, { platform: { mode: "required" } }),
    /Platform is required/
  );
}

testNormalizeDefinitions();
testNormalizeFormatCustomFieldRules();
testSanitizeSubmittedValues();
testRequiredValueError();
console.log("custom_fields tests passed");
```

Add the test to `tests/run_all.js` in the same list style as the existing tests:

```js
"custom_fields.test.js",
```

- [ ] **Step 2: Run the failing test**

Run: `rtk node tests/custom_fields.test.js`

Expected: FAIL with `Cannot find module '../lib/custom_fields.js'`.

- [ ] **Step 3: Implement `lib/custom_fields.js`**

Add:

```js
const FIELD_TYPES = ["text", "textarea", "select"];
const FIELD_MODES = ["required", "optional", "hidden"];

function optionIdFromLabel(label, fallback) {
  var id = String(label || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return id || fallback || "option";
}

function normalizeKey(value) {
  return String(value || "").trim().toLowerCase().replace(/[^a-z0-9_]+/g, "_").replace(/^_+|_+$/g, "");
}

function parseArray(value) {
  if (Array.isArray(value)) return value;
  if (typeof value === "string" && value.trim().charAt(0) === "[") {
    try {
      var parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed : [];
    } catch (err) {
      return [];
    }
  }
  return [];
}

function normalizeOptions(options) {
  var raw = parseArray(options);
  var seen = {};
  var normalized = [];
  for (var i = 0; i < raw.length; i++) {
    var item = raw[i] && typeof raw[i] === "object" ? raw[i] : { label: raw[i] };
    var label = String(item.label || item.value || item.id || "").trim().slice(0, 128);
    if (!label) continue;
    var id = optionIdFromLabel(item.id || label, "option_" + (i + 1)).slice(0, 128);
    if (seen[id]) continue;
    seen[id] = true;
    normalized.push({
      id: id,
      label: label,
      enabled: item.enabled !== false,
      sortOrder: Number(item.sortOrder || ((i + 1) * 10))
    });
  }
  normalized.sort(function (a, b) {
    if (a.sortOrder !== b.sortOrder) return a.sortOrder - b.sortOrder;
    return a.label.localeCompare(b.label);
  });
  return normalized;
}

function normalizeDefinitions(value) {
  var raw = parseArray(value);
  var seen = {};
  var normalized = [];
  for (var i = 0; i < raw.length; i++) {
    var item = raw[i] && typeof raw[i] === "object" ? raw[i] : {};
    var key = normalizeKey(item.key);
    var label = String(item.label || "").trim().slice(0, 128);
    var type = String(item.type || "text").trim();
    if (!key || seen[key] || !label || FIELD_TYPES.indexOf(type) < 0) continue;
    seen[key] = true;
    var def = {
      key: key,
      label: label,
      type: type,
      helpText: String(item.helpText || "").trim().slice(0, 500),
      enabled: item.enabled !== false,
      sortOrder: Number(item.sortOrder || ((i + 1) * 10)),
      options: []
    };
    if (type === "select") def.options = normalizeOptions(item.options);
    normalized.push(def);
  }
  normalized.sort(function (a, b) {
    if (a.sortOrder !== b.sortOrder) return a.sortOrder - b.sortOrder;
    return a.label.localeCompare(b.label);
  });
  return normalized;
}

function definitionsByKey(definitions) {
  var byKey = {};
  normalizeDefinitions(definitions).forEach(function (def) {
    byKey[def.key] = def;
  });
  return byKey;
}

function normalizeMode(value, fallback) {
  value = String(value || "").trim();
  return FIELD_MODES.indexOf(value) >= 0 ? value : (fallback || "optional");
}

function normalizeFormatCustomFieldRules(rules, definitions) {
  var byKey = definitionsByKey(definitions);
  var incoming = rules && typeof rules === "object" ? rules : {};
  var normalized = {};
  Object.keys(byKey).forEach(function (key) {
    var def = byKey[key];
    var mode = normalizeMode(incoming[key] && incoming[key].mode, "optional");
    if (!def.enabled) mode = "hidden";
    if (def.type === "select" && mode === "required") {
      var enabledOptions = (def.options || []).filter(function (opt) { return opt.enabled !== false; });
      if (!enabledOptions.length) mode = "optional";
    }
    normalized[key] = { mode: mode };
  });
  return normalized;
}

function enabledOptionForValue(def, value) {
  var text = String(value || "").trim();
  var options = (def.options || []).filter(function (opt) { return opt.enabled !== false; });
  for (var i = 0; i < options.length; i++) {
    if (options[i].id === text || options[i].label === text) return options[i];
  }
  return null;
}

function sanitizeSubmittedValues(values, definitions, rules) {
  var input = values && typeof values === "object" ? values : {};
  var byKey = definitionsByKey(definitions);
  var normalizedRules = normalizeFormatCustomFieldRules(rules, definitions);
  var output = {};
  Object.keys(byKey).forEach(function (key) {
    var def = byKey[key];
    var mode = normalizedRules[key] ? normalizedRules[key].mode : "hidden";
    if (mode === "hidden") return;
    var raw = input[key];
    var value = String(raw === undefined || raw === null ? "" : raw).trim();
    if (def.type === "textarea") value = value.slice(0, 2000);
    if (def.type === "text") value = value.slice(0, 250);
    if (def.type === "select") {
      var option = enabledOptionForValue(def, value);
      if (!option && mode === "required") {
        var selectErr = new Error(def.label + " is required.");
        selectErr.code = 400;
        throw selectErr;
      }
      if (option) {
        output[key] = { label: def.label, type: def.type, value: option.id, displayValue: option.label };
      }
      return;
    }
    if (mode === "required" && !value) {
      var err = new Error(def.label + " is required.");
      err.code = 400;
      throw err;
    }
    if (value) output[key] = { label: def.label, type: def.type, value: value };
  });
  return output;
}

module.exports = {
  normalizeDefinitions: normalizeDefinitions,
  normalizeFormatCustomFieldRules: normalizeFormatCustomFieldRules,
  sanitizeSubmittedValues: sanitizeSubmittedValues
};
```

- [ ] **Step 4: Run tests**

Run: `rtk node tests/custom_fields.test.js`

Expected: PASS and output `custom_fields tests passed`.

- [ ] **Step 5: Commit**

Run:

```bash
rtk git add lib/custom_fields.js tests/custom_fields.test.js tests/run_all.js
rtk git commit -m "Add custom field normalization"
```

## Task 2: PocketBase Schema Fields

**Files:**
- Create: `pb_migrations/202606080001_library_additional_fields.js`
- Modify: `pb_migrations/0000000000_initial.js`
- Test: `tests/custom_fields_schema.test.js`

- [ ] **Step 1: Write a schema migration guard test**

Add `tests/custom_fields_schema.test.js`:

```js
const assert = require("assert");
const fs = require("fs");

const migration = fs.readFileSync("pb_migrations/202606080001_library_additional_fields.js", "utf8");
const initial = fs.readFileSync("pb_migrations/0000000000_initial.js", "utf8");

assert(migration.includes('app.findCollectionByNameOrId("patron_settings_overrides")'));
assert(migration.includes('field("additionalFieldDefinitions", "json")'));
assert(migration.includes('app.findCollectionByNameOrId("title_requests")'));
assert(migration.includes('field("customFields", "json")'));
assert(initial.includes('field("additionalFieldDefinitions", "json")'));
assert(initial.includes('field("customFields", "json")'));
assert(!migration.toLowerCase().includes("select "));
assert(!migration.toLowerCase().includes("update "));
assert(!migration.toLowerCase().includes("alter table"));

console.log("custom field schema tests passed");
```

Add `"custom_fields_schema.test.js",` to `tests/run_all.js`.

- [ ] **Step 2: Run the failing test**

Run: `rtk node tests/custom_fields_schema.test.js`

Expected: FAIL because the migration file does not exist.

- [ ] **Step 3: Add migration using collection field APIs**

Create `pb_migrations/202606080001_library_additional_fields.js`:

```js
/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function hasField(collection, name) {
  var fields = collection.fields || [];
  for (var i = 0; i < fields.length; i++) {
    if (fields[i].name === name) return true;
  }
  return false;
}

migrate((app) => {
  var patronOverrides = app.findCollectionByNameOrId("patron_settings_overrides");
  if (!hasField(patronOverrides, "additionalFieldDefinitions")) {
    patronOverrides.fields.add(field("additionalFieldDefinitions", "json"));
    app.save(patronOverrides);
  }

  var titleRequests = app.findCollectionByNameOrId("title_requests");
  if (!hasField(titleRequests, "customFields")) {
    titleRequests.fields.add(field("customFields", "json"));
    app.save(titleRequests);
  }
}, (app) => {
  try {
    var titleRequests = app.findCollectionByNameOrId("title_requests");
    titleRequests.fields.removeByName("customFields");
    app.save(titleRequests);
  } catch (err) {}

  try {
    var patronOverrides = app.findCollectionByNameOrId("patron_settings_overrides");
    patronOverrides.fields.removeByName("additionalFieldDefinitions");
    app.save(patronOverrides);
  } catch (err2) {}
});
```

- [ ] **Step 4: Update initial migration**

In `pb_migrations/0000000000_initial.js`, add `field("customFields", "json")` in the `title_requests` fields after `field("publication", "text", { max: 128 })`.

In the `patron_settings_overrides` collection definition, add `field("additionalFieldDefinitions", "json")` after `field("patronFormatRules", "json")`.

- [ ] **Step 5: Run schema and raw SQL guards**

Run:

```bash
rtk node tests/custom_fields_schema.test.js
rtk node tests/no_raw_sql_runtime.test.js
```

Expected: both PASS.

- [ ] **Step 6: Commit**

Run:

```bash
rtk git add pb_migrations/202606080001_library_additional_fields.js pb_migrations/0000000000_initial.js tests/custom_fields_schema.test.js tests/run_all.js
rtk git commit -m "Add custom field schema"
```

## Task 3: Backend Config, Save, Submission, And Request JSON

**Files:**
- Modify: `lib/config/ui_text.js`
- Modify: `lib/staff/settings_ui.js`
- Modify: `lib/format_rules.js`
- Modify: `lib/records/suggestions.js`
- Test: `tests/config_custom_fields_scope.test.js`
- Test: `tests/records_custom_fields.test.js`

- [ ] **Step 1: Write failing config scope tests**

Add `tests/config_custom_fields_scope.test.js` using existing mock patterns from `tests/config_ui_text_patron_options_scope.test.js`. Include these assertions:

```js
assert.deepStrictEqual(config.uiText(app, "").additionalFieldDefinitions, []);
assert.deepStrictEqual(config.uiText(app, "2").additionalFieldDefinitions.map(f => f.key), ["platform"]);
assert.deepStrictEqual(config.uiText(app, "3").additionalFieldDefinitions, []);
assert.strictEqual(config.uiText(app, "2").formatRules.videogame.customFields.platform.mode, "required");
```

The mock app needs a `patron_settings_overrides` record for org `2` with:

```js
additionalFieldDefinitions: [
  { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
],
patronFormatRules: {
  videogame: { customFields: { platform: { mode: "required" } } }
}
```

- [ ] **Step 2: Write failing record persistence tests**

Add `tests/records_custom_fields.test.js`:

```js
const assert = require("assert");
const customFields = require("../lib/custom_fields.js");

const defs = customFields.normalizeDefinitions([
  { key: "platform", label: "Platform", type: "select", options: [{ id: "switch", label: "Nintendo Switch" }] }
]);
const values = customFields.sanitizeSubmittedValues(
  { platform: "switch" },
  defs,
  { platform: { mode: "required" } }
);

assert.deepStrictEqual(values.platform, {
  label: "Platform",
  type: "select",
  value: "switch",
  displayValue: "Nintendo Switch"
});
console.log("records custom field tests passed");
```

This starts with shared value validation and gets extended after `records/suggestions.js` is wired to mocks.

- [ ] **Step 3: Run failing tests**

Run:

```bash
rtk node tests/config_custom_fields_scope.test.js
rtk node tests/records_custom_fields.test.js
```

Expected: config test FAILS because `additionalFieldDefinitions` is missing from `uiText`; record test PASS after Task 1.

- [ ] **Step 4: Wire config load and save**

In `lib/config/ui_text.js`, require `custom_fields.js` and include:

```js
var additionalFieldDefinitions = overrideRecord
  ? customFields.normalizeDefinitions(overrideRecord.get("additionalFieldDefinitions"))
  : [];
```

Return `additionalFieldDefinitions` from `uiTextFromRecord`. Pass those definitions into format rule normalization:

```js
formatRules: fRules.normalizeFormatRules(overrideFormatRules || formats.rules, additionalFieldDefinitions),
additionalFieldDefinitions: additionalFieldDefinitions,
```

In `lib/staff/settings_ui.js`, require `custom_fields.js` and save only in `savePatronLibrarySettings`:

```js
if (ui.additionalFieldDefinitions !== undefined) {
  record.set("additionalFieldDefinitions", customFields.normalizeDefinitions(ui.additionalFieldDefinitions));
}
```

- [ ] **Step 5: Preserve custom field rules in `lib/format_rules.js`**

Change `normalizeFormatRules(rules)` to `normalizeFormatRules(rules, additionalFieldDefinitions)`, require `custom_fields.js`, and add inside each format:

```js
normalized[format].customFields = customFields.normalizeFormatCustomFieldRules(
  incomingFormat.customFields || normalized[format].customFields || {},
  additionalFieldDefinitions || []
);
```

Keep existing canonical `fields` behavior unchanged.

- [ ] **Step 6: Validate patron submission custom fields**

In `sanitizePatronSuggestion(data, uiText)`, after canonical field validation:

```js
data.customFields = customFields.sanitizeSubmittedValues(
  data.customFields || {},
  uiText && uiText.additionalFieldDefinitions,
  rule.customFields || {}
);
```

- [ ] **Step 7: Persist request custom fields**

In `lib/records/suggestions.js`:

```js
record.set("customFields", data.customFields || {});
```

Add to `titleRequestToJson`:

```js
customFields: record.get("customFields") || {},
```

Add `"customFields"` to the update allowlist and set object values directly:

```js
if (fields[i] === "customFields") {
  record.set(fields[i], data[fields[i]] && typeof data[fields[i]] === "object" ? data[fields[i]] : {});
}
```

- [ ] **Step 8: Run backend tests**

Run:

```bash
rtk node tests/custom_fields.test.js
rtk node tests/config_custom_fields_scope.test.js
rtk node tests/records_custom_fields.test.js
rtk npm test
```

Expected: all PASS.

- [ ] **Step 9: Commit**

Run:

```bash
rtk git add lib/custom_fields.js lib/config/ui_text.js lib/staff/settings_ui.js lib/format_rules.js lib/records/suggestions.js tests/config_custom_fields_scope.test.js tests/records_custom_fields.test.js
rtk git commit -m "Wire custom fields through backend settings and requests"
```

## Task 4: Staff Settings UI

**Files:**
- Create: `pb_public/staff/js/settings-additional-fields.js`
- Modify: `pb_public/staff/js/state.js`
- Modify: `pb_public/staff/js/settings.js`
- Modify: `pb_public/staff/js/settings-ui.js`
- Modify: `pb_public/staff/index.html`
- Test: `tests/staff_additional_fields_ui.test.js`

- [ ] **Step 1: Write failing jsdom settings UI test**

Add `tests/staff_additional_fields_ui.test.js`:

```js
const assert = require("assert");
const { JSDOM } = require("jsdom");

const dom = new JSDOM(`<!doctype html>
  <div id="additional-fields-editor"></div>
  <button id="btn-add-additional-field"></button>
`);
global.window = dom.window;
global.document = dom.window.document;
global.Option = dom.window.Option;

const mod = await import("../pb_public/staff/js/settings-additional-fields.js");

mod.renderAdditionalFieldsEditor([
  { key: "platform", label: "Platform", type: "select", helpText: "Pick one", enabled: true, sortOrder: 10, options: [{ id: "switch", label: "Nintendo Switch", enabled: true, sortOrder: 10 }] }
]);

assert.strictEqual(document.querySelector('[data-additional-field-key="platform"] .additional-field-label-input').value, "Platform");
assert.strictEqual(document.querySelector(".additional-field-option-label-input").value, "Nintendo Switch");

const collected = mod.collectAdditionalFieldDefinitions();
assert.strictEqual(collected[0].key, "platform");
assert.strictEqual(collected[0].options[0].id, "switch");
console.log("staff additional fields UI tests passed");
```

Add it to `tests/run_all.js`.

- [ ] **Step 2: Run failing test**

Run: `rtk node tests/staff_additional_fields_ui.test.js`

Expected: FAIL because `settings-additional-fields.js` does not exist.

- [ ] **Step 3: Add staff state**

In `pb_public/staff/js/state.js` add:

```js
export let additionalFieldDefinitions = [];
export function setAdditionalFieldDefinitions(definitions) {
  additionalFieldDefinitions = Array.isArray(definitions) ? definitions : [];
}
```

- [ ] **Step 4: Implement `settings-additional-fields.js` with DOM APIs**

Create exports:

```js
import { markSettingsDirty } from './api.js';
import { additionalFieldDefinitions, setAdditionalFieldDefinitions } from './state.js';

export function renderAdditionalFieldsEditor(definitions = additionalFieldDefinitions) {
  const editor = document.getElementById('additional-fields-editor');
  if (!editor) return;
  setAdditionalFieldDefinitions(Array.isArray(definitions) ? definitions : []);
  editor.replaceChildren(...additionalFieldDefinitions.map(renderDefinitionRow));
}

function renderDefinitionRow(def) {
  const row = document.createElement('div');
  row.className = 'additional-field-row border rounded p-2 mb-2';
  row.setAttribute('data-additional-field-key', def.key || '');

  const label = document.createElement('input');
  label.className = 'form-control form-control-sm additional-field-label-input';
  label.value = def.label || '';
  label.setAttribute('aria-label', 'Additional field label');

  const key = document.createElement('input');
  key.className = 'form-control form-control-sm additional-field-key-input';
  key.value = def.key || '';
  key.setAttribute('aria-label', 'Additional field key');

  const type = document.createElement('select');
  type.className = 'form-control form-control-sm additional-field-type-select';
  ['text', 'textarea', 'select'].forEach(value => type.appendChild(new Option(value, value)));
  type.value = def.type || 'text';

  const help = document.createElement('input');
  help.className = 'form-control form-control-sm additional-field-help-input';
  help.value = def.helpText || '';
  help.setAttribute('aria-label', 'Additional field help text');

  const enabled = document.createElement('input');
  enabled.type = 'checkbox';
  enabled.className = 'additional-field-enabled-check';
  enabled.checked = def.enabled !== false;

  const options = document.createElement('div');
  options.className = 'additional-field-options';
  (def.options || []).forEach(opt => options.appendChild(renderOptionRow(opt)));

  row.append(label, key, type, help, enabled, options);
  return row;
}

function renderOptionRow(opt) {
  const row = document.createElement('div');
  row.className = 'additional-field-option-row d-flex mb-1';
  const id = document.createElement('input');
  id.className = 'form-control form-control-sm additional-field-option-id-input';
  id.value = opt.id || '';
  const label = document.createElement('input');
  label.className = 'form-control form-control-sm additional-field-option-label-input';
  label.value = opt.label || '';
  row.append(id, label);
  return row;
}

export function collectAdditionalFieldDefinitions() {
  return Array.from(document.querySelectorAll('.additional-field-row')).map((row, index) => ({
    key: row.querySelector('.additional-field-key-input')?.value.trim() || '',
    label: row.querySelector('.additional-field-label-input')?.value.trim() || '',
    type: row.querySelector('.additional-field-type-select')?.value || 'text',
    helpText: row.querySelector('.additional-field-help-input')?.value.trim() || '',
    enabled: !!row.querySelector('.additional-field-enabled-check')?.checked,
    sortOrder: (index + 1) * 10,
    options: Array.from(row.querySelectorAll('.additional-field-option-row')).map((optRow, optIndex) => ({
      id: optRow.querySelector('.additional-field-option-id-input')?.value.trim() || '',
      label: optRow.querySelector('.additional-field-option-label-input')?.value.trim() || '',
      enabled: true,
      sortOrder: (optIndex + 1) * 10
    }))
  }));
}

document.addEventListener('input', event => {
  if (event.target.closest && event.target.closest('#additional-fields-editor')) markSettingsDirty();
});
```

Before committing this task, extend `renderDefinitionRow` with Remove, Move up, and Move down buttons. Each button mutates the in-memory `additionalFieldDefinitions` array, calls `renderAdditionalFieldsEditor(additionalFieldDefinitions)`, and calls `markSettingsDirty()`. Bind `#btn-add-additional-field` so it appends `{ key: '', label: '', type: 'text', helpText: '', enabled: true, sortOrder: (additionalFieldDefinitions.length + 1) * 10, options: [] }`, then re-renders and marks dirty.

- [ ] **Step 5: Place accordion in Patron Experience**

In `pb_public/staff/index.html`, add a standalone accordion panel in Patron Experience above the format rules section:

```html
<div class="asap-accordion-item" id="additional-fields-accordion">
  <button type="button" class="asap-accordion-header" aria-expanded="false" aria-controls="additional-fields-panel">
    <span class="asap-accordion-title">Additional Fields</span>
    <span class="asap-accordion-summary">Library-only fields for patron submissions</span>
    <i class="fa fa-chevron-down asap-accordion-chevron" aria-hidden="true"></i>
  </button>
  <div id="additional-fields-panel" class="asap-accordion-panel" role="region">
    <div id="additional-fields-editor"></div>
  </div>
</div>
```

- [ ] **Step 6: Load/save definitions in settings**

In `pb_public/staff/js/settings.js`, import:

```js
import { renderAdditionalFieldsEditor, collectAdditionalFieldDefinitions } from './settings-additional-fields.js';
import { setAdditionalFieldDefinitions } from './state.js';
```

In settings population:

```js
setAdditionalFieldDefinitions(uiText.additionalFieldDefinitions || []);
renderAdditionalFieldsEditor(uiText.additionalFieldDefinitions || []);
```

In `_serializeSettingsState` inside `uiText`:

```js
additionalFieldDefinitions: isSystemContext ? undefined : collectAdditionalFieldDefinitions(),
```

- [ ] **Step 7: Add per-format custom field controls**

In `pb_public/staff/js/settings-ui.js`, import definitions from state and render rows after canonical fields:

```js
const customFieldRules = rule.customFields || {};
additionalFieldDefinitions.forEach(def => {
  const tr = document.createElement('tr');
  const nameTd = document.createElement('td');
  nameTd.textContent = def.label;
  const modeTd = document.createElement('td');
  const select = document.createElement('select');
  select.className = 'form-control form-control-sm format-rule-custom-field-mode';
  select.setAttribute('data-format', format);
  select.setAttribute('data-field', def.key);
  ['required', 'optional', 'hidden'].forEach(mode => select.appendChild(new Option(mode, mode)));
  select.value = (customFieldRules[def.key] && customFieldRules[def.key].mode) || 'hidden';
  modeTd.appendChild(select);
  tr.append(nameTd, modeTd, document.createElement('td'));
  tbody.appendChild(tr);
});
```

In `collectPatronFormatRules`, collect `.format-rule-custom-field-mode` into `rules[format].customFields`.

- [ ] **Step 8: Run staff UI test and DOM safety guard**

Run:

```bash
rtk node tests/staff_additional_fields_ui.test.js
rtk node tests/dom_safety_innerhtml_static_analysis.test.js
```

Expected: both PASS.

- [ ] **Step 9: Commit**

Run:

```bash
rtk git add pb_public/staff/js/settings-additional-fields.js pb_public/staff/js/state.js pb_public/staff/js/settings.js pb_public/staff/js/settings-ui.js pb_public/staff/index.html tests/staff_additional_fields_ui.test.js tests/run_all.js
rtk git commit -m "Add staff additional fields settings UI"
```

## Task 5: Patron Form Rendering And Submission

**Files:**
- Create: `pb_public/patron/js/custom-fields.js`
- Modify: `pb_public/patron/js/state.js`
- Modify: `pb_public/patron/js/config.js`
- Modify: `pb_public/patron/js/form-ui.js`
- Modify: `pb_public/patron/js/submit.js`
- Modify: `pb_public/patron/index.html`
- Test: `tests/patron_custom_fields_ui.test.js`

- [ ] **Step 1: Write failing patron jsdom test**

Add `tests/patron_custom_fields_ui.test.js`:

```js
const assert = require("assert");
const { JSDOM } = require("jsdom");

const dom = new JSDOM(`<!doctype html>
  <form id="suggestion-form">
    <div id="additional-fields-container"></div>
  </form>
`);
global.window = dom.window;
global.document = dom.window.document;
global.Option = dom.window.Option;

const mod = await import("../pb_public/patron/js/custom-fields.js");

mod.renderCustomFields(
  [
    { key: "platform", label: "Platform", type: "select", helpText: "Pick one", enabled: true, options: [{ id: "switch", label: "Nintendo Switch", enabled: true }] },
    { key: "note", label: "Note", type: "textarea", enabled: true }
  ],
  { platform: { mode: "required" }, note: { mode: "optional" } }
);

assert.strictEqual(document.querySelector('label[for="custom-field-platform"]').textContent.includes("Platform"), true);
assert.strictEqual(document.getElementById("custom-field-platform").required, true);
assert.strictEqual(document.querySelector("option").textContent, "Nintendo Switch");

document.getElementById("custom-field-platform").value = "switch";
document.getElementById("custom-field-note").value = "Plain text";
assert.deepStrictEqual(mod.collectCustomFieldValues(), { platform: "switch", note: "Plain text" });
console.log("patron custom fields UI tests passed");
```

Add it to `tests/run_all.js`.

- [ ] **Step 2: Run failing test**

Run: `rtk node tests/patron_custom_fields_ui.test.js`

Expected: FAIL because `custom-fields.js` does not exist.

- [ ] **Step 3: Add patron state and config**

In `pb_public/patron/js/state.js`:

```js
export let additionalFieldDefinitions = [];
export function setAdditionalFieldDefinitions(definitions) {
  additionalFieldDefinitions = Array.isArray(definitions) ? definitions : [];
}
```

In `pb_public/patron/js/config.js`, when applying config:

```js
setAdditionalFieldDefinitions(Array.isArray(uiConfig.additionalFieldDefinitions) ? uiConfig.additionalFieldDefinitions : []);
```

- [ ] **Step 4: Add patron container**

In `pb_public/patron/index.html`, inside `#physical-fields` after `field-publication`, add:

```html
<div id="additional-fields-container"></div>
```

- [ ] **Step 5: Implement patron custom field rendering**

Create `pb_public/patron/js/custom-fields.js`:

```js
import { byId } from './dom.js';

export function renderCustomFields(definitions, rules) {
  const container = byId('additional-fields-container');
  if (!container) return;
  const nodes = [];
  (definitions || []).forEach(def => {
    const mode = rules && rules[def.key] ? rules[def.key].mode : 'hidden';
    if (mode === 'hidden' || def.enabled === false) return;
    nodes.push(renderField(def, mode === 'required'));
  });
  container.replaceChildren(...nodes);
}

function renderField(def, required) {
  const row = document.createElement('div');
  row.className = 'form-group row reqAuth custom-field-row';
  row.setAttribute('data-custom-field-key', def.key);

  const label = document.createElement('label');
  label.className = 'col-5 col-form-label';
  label.setAttribute('for', `custom-field-${def.key}`);
  label.textContent = def.label + (required ? ' *' : '');

  const col = document.createElement('div');
  col.className = 'col-7';
  let input;
  if (def.type === 'textarea') {
    input = document.createElement('textarea');
  } else if (def.type === 'select') {
    input = document.createElement('select');
    input.appendChild(new Option('', ''));
    (def.options || []).filter(opt => opt.enabled !== false).forEach(opt => {
      input.appendChild(new Option(opt.label, opt.id));
    });
  } else {
    input = document.createElement('input');
    input.type = 'text';
  }
  input.id = `custom-field-${def.key}`;
  input.name = `customFields.${def.key}`;
  input.className = def.type === 'select' ? 'custom-select custom-field-input' : 'form-control custom-field-input';
  input.setAttribute('data-custom-field-key', def.key);
  input.required = required;
  input.setAttribute('aria-required', required ? 'true' : 'false');
  col.appendChild(input);
  if (def.helpText) {
    const help = document.createElement('small');
    help.className = 'form-text text-muted';
    help.textContent = def.helpText;
    col.appendChild(help);
  }
  row.append(label, col);
  return row;
}

export function collectCustomFieldValues() {
  const values = {};
  document.querySelectorAll('.custom-field-input').forEach(input => {
    const key = input.getAttribute('data-custom-field-key');
    if (key) values[key] = input.value;
  });
  return values;
}
```

- [ ] **Step 6: Render on format changes**

In `pb_public/patron/js/form-ui.js`, import:

```js
import { additionalFieldDefinitions } from './state.js';
import { renderCustomFields } from './custom-fields.js';
```

In `updateFormatUI`, after canonical field rendering:

```js
renderCustomFields(additionalFieldDefinitions, rule.customFields || {});
```

When message behavior hides physical fields, clear custom fields:

```js
renderCustomFields([], {});
```

- [ ] **Step 7: Submit custom field values**

In `pb_public/patron/js/submit.js`, import `collectCustomFieldValues` and add:

```js
data.customFields = collectCustomFieldValues();
```

- [ ] **Step 8: Run patron UI and DOM safety tests**

Run:

```bash
rtk node tests/patron_custom_fields_ui.test.js
rtk node tests/dom_safety_innerhtml_static_analysis.test.js
rtk npm test
```

Expected: all PASS.

- [ ] **Step 9: Commit**

Run:

```bash
rtk git add pb_public/patron/js/custom-fields.js pb_public/patron/js/state.js pb_public/patron/js/config.js pb_public/patron/js/form-ui.js pb_public/patron/js/submit.js pb_public/patron/index.html tests/patron_custom_fields_ui.test.js tests/run_all.js
rtk git commit -m "Render patron additional fields"
```

## Task 6: Staff Request Display And Edit

**Files:**
- Create: `pb_public/staff/js/request-custom-fields.js`
- Modify: `pb_public/staff/js/modals.js`
- Modify: `pb_public/staff/js/grid.js`
- Test: `tests/staff_custom_fields_request_ui.test.js`

- [ ] **Step 1: Write failing staff request UI test**

Add `tests/staff_custom_fields_request_ui.test.js`:

```js
const assert = require("assert");
const { JSDOM } = require("jsdom");

const dom = new JSDOM(`<!doctype html>
  <div id="edit-custom-fields"></div>
`);
global.window = dom.window;
global.document = dom.window.document;
global.Option = dom.window.Option;

const mod = await import("../pb_public/staff/js/request-custom-fields.js");

mod.renderEditCustomFieldsForTest(
  { customFields: { platform: { label: "Platform", type: "select", value: "switch", displayValue: "Nintendo Switch" } } },
  [{ key: "platform", label: "Platform", type: "select", enabled: true, options: [{ id: "switch", label: "Nintendo Switch", enabled: true }] }],
  { platform: { mode: "required" } }
);

assert.strictEqual(document.querySelector('#edit-custom-fields label').textContent.includes("Platform"), true);
assert.strictEqual(document.querySelector('#edit-custom-fields select').value, "switch");
console.log("staff custom fields request UI tests passed");
```

Use the focused `pb_public/staff/js/request-custom-fields.js` module for rendering and collection. Import it from `pb_public/staff/js/modals.js`.

- [ ] **Step 2: Run failing test**

Run: `rtk node tests/staff_custom_fields_request_ui.test.js`

Expected: FAIL because the render helper does not exist.

- [ ] **Step 3: Add edit modal container**

In the edit modal markup in `pb_public/staff/index.html`, add:

```html
<div id="edit-custom-fields"></div>
```

Place it after the canonical title/author/identifier/publication controls and before notes.

- [ ] **Step 4: Render staff edit custom fields**

Create `pb_public/staff/js/request-custom-fields.js` with a renderer using DOM APIs:

```js
function inputId(key) {
  return 'edit-custom-field-' + String(key || '').replace(/[^a-z0-9_]/g, '_');
}

export function renderEditCustomFields(row, definitions, rules) {
  const container = document.getElementById('edit-custom-fields');
  if (!container) return;
  const existing = row && row.customFields ? row.customFields : {};
  const nodes = [];
  (definitions || []).forEach(def => {
    const mode = rules && rules[def.key] ? rules[def.key].mode : 'hidden';
    const historical = existing[def.key];
    if (mode === 'hidden' && !historical) return;
    nodes.push(renderEditCustomField(def, mode === 'required', historical));
  });
  Object.keys(existing).forEach(key => {
    if ((definitions || []).some(def => def.key === key)) return;
    nodes.push(renderHistoricalCustomField(key, existing[key]));
  });
  container.replaceChildren(...nodes);
}

function renderEditCustomField(def, required, historical) {
  const row = document.createElement('div');
  row.className = 'form-group row custom-field-row';
  row.setAttribute('data-custom-field-key', def.key);

  const label = document.createElement('label');
  label.className = 'col-5 col-form-label';
  label.setAttribute('for', inputId(def.key));
  label.textContent = def.label + (required ? ' *' : '');

  const col = document.createElement('div');
  col.className = 'col-7';
  let input;
  if (def.type === 'textarea') {
    input = document.createElement('textarea');
  } else if (def.type === 'select') {
    input = document.createElement('select');
    input.appendChild(new Option('', ''));
    (def.options || []).filter(opt => opt.enabled !== false).forEach(opt => {
      input.appendChild(new Option(opt.label, opt.id));
    });
  } else {
    input = document.createElement('input');
    input.type = 'text';
  }
  input.id = inputId(def.key);
  input.className = def.type === 'select' ? 'custom-select custom-field-input' : 'form-control custom-field-input';
  input.setAttribute('data-custom-field-key', def.key);
  input.required = required;
  input.setAttribute('aria-required', required ? 'true' : 'false');
  input.value = historical ? String(historical.value || '') : '';
  col.appendChild(input);

  row.append(label, col);
  return row;
}

function renderHistoricalCustomField(key, value) {
  const row = document.createElement('div');
  row.className = 'form-group row custom-field-row custom-field-historical';
  const label = document.createElement('div');
  label.className = 'col-5 col-form-label font-weight-bold';
  label.textContent = value && value.label ? value.label : key;
  const col = document.createElement('div');
  col.className = 'col-7';
  col.textContent = value && value.displayValue ? value.displayValue : String(value && value.value || '');
  row.append(label, col);
  return row;
}

export function renderEditCustomFieldsForTest(row, definitions, rules) {
  renderEditCustomFields(row, definitions, rules);
}
```

- [ ] **Step 5: Collect staff edit values**

Add:

```js
export function collectEditCustomFieldValues() {
  const values = {};
  document.querySelectorAll('#edit-custom-fields .custom-field-input').forEach(input => {
    const key = input.getAttribute('data-custom-field-key');
    if (key) values[key] = input.value;
  });
  return values;
}
```

In edit form submit payload:

```js
customFields: collectEditCustomFieldValues(),
```

- [ ] **Step 6: Pass definitions and rules into edit modal**

Add focused state exports in `pb_public/staff/js/state.js` for the currently loaded patron field config:

```js
export let currentAdditionalFieldDefinitions = [];
export let currentFormatRules = {};
export function setCurrentPatronFieldConfig(definitions, formatRules) {
  currentAdditionalFieldDefinitions = Array.isArray(definitions) ? definitions : [];
  currentFormatRules = formatRules && typeof formatRules === 'object' ? formatRules : {};
}
```

Set this during settings/bootstrap load. In `openEditModal`, call:

```js
const formatRule = currentFormatRules[row.format] || {};
renderEditCustomFields(row, currentAdditionalFieldDefinitions, formatRule.customFields || {});
```

- [ ] **Step 7: Validate staff edits server-side**

Before `records.updateTitleRequest` in staff edit action code, load `config.uiText(e.app, record.get("libraryOrgId"))`, find the selected format rule, and sanitize `context.data.customFields` with `custom_fields.sanitizeSubmittedValues`. Preserve historical hidden fields by merging sanitized visible fields with existing hidden historical values.

- [ ] **Step 8: Run request UI and backend tests**

Run:

```bash
rtk node tests/staff_custom_fields_request_ui.test.js
rtk node tests/records_custom_fields.test.js
rtk npm test
```

Expected: all PASS.

- [ ] **Step 9: Commit**

Run:

```bash
rtk git add pb_public/staff/js/request-custom-fields.js pb_public/staff/js/modals.js pb_public/staff/js/grid.js pb_public/staff/index.html lib/staff/title_request_actions.js tests/staff_custom_fields_request_ui.test.js tests/records_custom_fields.test.js
rtk git commit -m "Add staff request custom field editing"
```

## Task 7: PocketBase Runtime Smoke And Final Verification

**Files:**
- No planned production file edits unless verification finds a defect.

- [ ] **Step 1: Run full test suite**

Run: `rtk npm test`

Expected: PASS.

- [ ] **Step 2: Start PocketBase**

Run with the project hooks and migrations directories:

```bash
rtk ./pocketbase serve --http=127.0.0.1:8090 --dir=/tmp/asap_custom_fields_pb_data --hooksDir=pb_hooks --migrationsDir=pb_migrations
```

Expected: server starts without hook or migration errors.

- [ ] **Step 3: Smoke public boot endpoints**

Run:

```bash
rtk curl -sS http://127.0.0.1:8090/api/asap/setup/status
rtk curl -sS http://127.0.0.1:8090/api/asap/config
```

Expected: neither response is `{"message":"Something went wrong while processing your request.","status":400}`. If either returns generic 400, inspect PocketBase logs before changing code.

- [ ] **Step 4: Run DOM safety guard**

Run: `rtk node tests/dom_safety_innerhtml_static_analysis.test.js`

Expected: PASS.

- [ ] **Step 5: Finish with a clean worktree**

Run: `rtk git status --short`

Expected: no output. If verification exposed a defect, return to the task that introduced that defect, add a focused test for it, implement the fix, run `rtk npm test`, and commit with the affected task's files.

## Self-Review

Spec coverage:

- Library-only scope: Tasks 3 and 4.
- Own accordion above format rules: Task 4.
- Patron runtime rendering and validation: Tasks 3 and 5.
- Staff view/edit: Task 6.
- Storage and migration: Task 2.
- DOM safety: Tasks 4, 5, and 7.
- Tests and PocketBase route smoke: Tasks 1 through 7.

Placeholder scan: no `TBD`, `TODO`, `implement later`, or open product decisions.

Type consistency: `additionalFieldDefinitions`, `customFields`, `customFields[formatKey].mode`, and `title_requests.customFields` are used consistently across tasks.
