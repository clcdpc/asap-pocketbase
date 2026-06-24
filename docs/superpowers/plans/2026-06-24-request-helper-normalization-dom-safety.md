# Request Helper, Normalization, and DOM Safety Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** remove ad hoc staff request calls and duplicate byte-array/JSON decoding, then clean up the remaining unsafe staff UI HTML construction without changing patron rich-text rendering.

**Architecture:** Keep `pb_public/shared/http.js` as the only raw `fetch()` owner and make all staff/patron request callers go through `authorizedJson()` or the patron request wrapper. Treat `lib/config/normalization.js` as the backend source of truth for byte-array and JSON-field decoding so Polaris and custom-field code stop reimplementing the same logic. For the remaining staff UI HTML hotspots, move dynamic rendering to DOM node construction and reserve `innerHTML` only for static developer-authored markup or already-sanitized patron text.

**Tech Stack:** CommonJS backend modules, browser ES modules under `pb_public/`, shared request helpers, existing Node test runner, and the current static-analysis regression tests.

---

## File Structure

- Modify: `pb_public/staff/js/actions.js`
  - Replace the manual undo request with `authorizedJson()`.
- Modify: `pb_public/staff/js/settings-polaris.js`
  - Replace the hold/promoter/SMTP raw `fetch()` calls with `authorizedJson()`.
- Modify: `pb_public/staff/js/patron.js`
  - Replace the patron lookup and suggestion submission raw `fetch()` calls with `authorizedJson()`.
- Modify: `pb_public/staff/js/settings-ui.js`
  - Replace the BIB lookup raw `fetch()` call with `authorizedJson()` and convert the dynamic option-list editor away from string-built HTML.
- Modify: `tests/frontend_request_architecture.test.js`
  - Add a regression check that no staff JS file contains raw `fetch(` after the migration.
- Modify: `lib/polaris/helpers.js`
  - Reuse the shared backend byte-decoding helper instead of keeping a local decoder.
- Modify: `lib/polaris/bib.js`
  - Parse cached material-type JSON through the shared normalization helper.
- Modify: `lib/polaris/pickup_branch_cache.js`
  - Parse cached branch JSON through the shared normalization helper.
- Modify: `lib/custom_fields.js`
  - Parse array-shaped field definitions and options through the shared normalization helper.
- Modify: `tests/config_parseJsonArray.test.js`, `tests/pickup_branch_cache.test.js`, `tests/custom_fields.test.js`, `tests/polaris_material_types.test.js`
  - Verify the shared normalization path still accepts arrays, JSON strings, and byte-array JSON payloads.
- Modify: `pb_public/staff/js/grid-data.js`, `pb_public/staff/js/grid-actions.js`, `pb_public/staff/js/settings-users.js`, `pb_public/staff/js/settings-formats.js`, `pb_public/staff/js/analytics.js`, `pb_public/staff/js/settings-templates.js`, `pb_public/staff/js/modals/edit-form.js`, `pb_public/staff/js/modals/claim-tags.js`
  - Convert remaining dynamic staff HTML construction to DOM APIs in the follow-up cleanup pass.
- Do not modify: patron rich-text `innerHTML` paths that already pass through `sanitizeHtml()`.

---

### Task 1: Migrate Staff Requests To Shared Helpers

**Files:**
- Modify: `pb_public/staff/js/actions.js`
- Modify: `pb_public/staff/js/settings-polaris.js`
- Modify: `pb_public/staff/js/patron.js`
- Modify: `pb_public/staff/js/settings-ui.js`
- Modify: `tests/frontend_request_architecture.test.js`

- [ ] **Step 1: Add a raw-fetch regression guard**

Add this assertion block to `tests/frontend_request_architecture.test.js` after the existing authorizedJson checks:

```js
  {
    const staffJsFiles = findFiles(path.join(root, 'pb_public/staff/js'), file => file.endsWith('.js'));
    const offenders = [];
    for (const file of staffJsFiles) {
      const source = fs.readFileSync(file, 'utf8');
      if (/\bfetch\s*\(/.test(source)) {
        offenders.push(path.relative(root, file));
      }
    }

    assert.deepStrictEqual(
      offenders,
      [],
      'staff JS modules should use authorizedJson/request helpers instead of raw fetch'
    );
  }
```

- [ ] **Step 2: Run the request architecture test and confirm it fails before the migration**

Run:

```bash
node tests/frontend_request_architecture.test.js
```

Expected result before the code changes:

```text
AssertionError [ERR_ASSERTION]: staff JS modules should use authorizedJson/request helpers instead of raw fetch
```

- [ ] **Step 3: Replace the undo request in `actions.js`**

Change `undoRow()` to call `authorizedJson()` directly:

```js
    const body = row.type === 'additional_copy'
      ? {}
      : {
          ...row,
          status: 'suggestion',
          editedBy: pb.authStore.model.username
        };

    await authorizedJson(url, {
      method: 'POST',
      body
    });
```

- [ ] **Step 4: Replace the job and SMTP requests in `settings-polaris.js`**

Use `authorizedJson()` in all three handlers:

```js
    const data = await authorizedJson('/api/asap/jobs/hold-check', { method: 'POST' });
    const data = await authorizedJson('/api/asap/jobs/promoter-check', { method: 'POST' });
    const data = await authorizedJson('/api/asap/staff/test-smtp', {
      method: 'POST',
      body: { email: testEmail }
    });
```

- [ ] **Step 5: Replace patron lookup and submission requests in `patron.js`**

Use `authorizedJson()` for both flows and read error details from the thrown error:

```js
    const data = await authorizedJson('/api/asap/staff/patron-lookup', {
      method: 'POST',
      body: staffSuggestionLibraryPayload({ query: patronQuery })
    });
```

```js
    await authorizedJson('/api/asap/staff/suggestions', {
      method: 'POST',
      body: payload
    });
```

Keep the multiple-results branch and the existing user-facing messages intact.

- [ ] **Step 6: Replace the BIB lookup request in `settings-ui.js`**

Use `authorizedJson()` and keep the existing error handling/UI reset logic:

```js
    const data = await authorizedJson('/api/asap/staff/bib-lookup', {
      method: 'POST',
      body: { bibId, barcode }
    });
```

- [ ] **Step 7: Re-run the request architecture test**

Run:

```bash
node tests/frontend_request_architecture.test.js
```

Expected result after the migration:

```text
frontend_request_architecture.test.js passed.
```

---

### Task 2: Centralize Backend Byte/JSON Decoding

**Files:**
- Modify: `lib/polaris/helpers.js`
- Modify: `lib/polaris/bib.js`
- Modify: `lib/polaris/pickup_branch_cache.js`
- Modify: `lib/custom_fields.js`
- Modify: `tests/config_parseJsonArray.test.js`
- Modify: `tests/pickup_branch_cache.test.js`
- Modify: `tests/custom_fields.test.js`
- Modify: `tests/polaris_material_types.test.js`

- [ ] **Step 1: Point backend modules at the shared normalization helper**

Use `require('../config/normalization.js')` or `require('./config/normalization.js')` from the backend modules and replace local byte decoding with shared helpers:

```js
const normalization = require('../config/normalization.js');

function decodeByteArray(value) {
  return normalization.decodeUtf8Bytes(value);
}
```

```js
const normalization = require('../config/normalization.js');

function normalizeMaterialTypesCache(cached) {
  const data = normalization.parseJsonObject(cached, null);
  if (!data) return null;
  // keep the existing v1/v2 material-type conversion logic
}
```

```js
const normalization = require('../config/normalization.js');

function decodeJsonValue(value) {
  return normalization.parseJsonArray(value, []);
}
```

```js
const normalization = require('./config/normalization.js');

function parseArray(value) {
  return normalization.parseJsonArray(value, []);
}
```

- [ ] **Step 2: Run the focused normalization tests before any follow-up cleanup**

Run:

```bash
node tests/config_parseJsonArray.test.js
node tests/pickup_branch_cache.test.js
node tests/custom_fields.test.js
node tests/polaris_material_types.test.js
```

Expected result before the code changes:

```text
At least one of the tests should fail if the shared helper replacement is incomplete.
```

- [ ] **Step 3: Keep the shared helper behavior covered by existing tests**

Verify the following cases remain true after the refactor:

```js
assert.deepStrictEqual(config.parseJsonArray(byteJson([{ label: 'Café' }]), []), [{ label: 'Café' }]);
assert.deepStrictEqual(cache.normalizeBranchList([{ id: '10', label: 'Branch 10' }], appForMapping), [{ id: '10', label: 'Synced Main Branch' }]);
assert.deepStrictEqual(customFields.normalizeDefinitions([{ key: 'Platform!', label: ' Platform ', type: 'select' }]).map(d => d.key), ['platform']);
```

- [ ] **Step 4: Re-run the focused normalization tests**

Run:

```bash
node tests/config_parseJsonArray.test.js
node tests/pickup_branch_cache.test.js
node tests/custom_fields.test.js
node tests/polaris_material_types.test.js
```

Expected result after the refactor:

```text
... tests passed.
```

---

### Task 3: Remove Unsafe Dynamic Staff HTML Construction

**Files:**
- Modify: `pb_public/staff/js/settings-ui.js`
- Modify: `pb_public/staff/js/grid-data.js`
- Modify: `pb_public/staff/js/grid-actions.js`
- Modify: `pb_public/staff/js/settings-users.js`
- Modify: `pb_public/staff/js/settings-formats.js`
- Modify: `pb_public/staff/js/analytics.js`
- Modify: `pb_public/staff/js/settings-templates.js`
- Modify: `pb_public/staff/js/modals/edit-form.js`
- Modify: `pb_public/staff/js/modals/claim-tags.js`

- [ ] **Step 1: Convert the settings option-list editor to DOM nodes**

Replace the current string-built rows in `renderOptionListEditor()` with `document.createElement()` + `replaceChildren()` so labels and attributes are never interpolated into HTML strings:

```js
  const rows = list.map((option) => {
    const row = document.createElement('div');
    row.className = 'option-list-row';
    row.dataset.optionId = option.id;
    // build the drag handle, input, checkbox, and delete button with DOM APIs
    return row;
  });
  editor.replaceChildren(...rows);
```

- [ ] **Step 2: Convert the remaining staff UI status fragments to DOM APIs**

Apply the same pattern to dynamic status/error/result blocks in the listed staff modules so the code no longer builds HTML strings from runtime values.

- [ ] **Step 3: Keep sanctioned HTML cases explicitly documented**

Retain only the existing static developer-authored markup or sanitized rich text, and add the nearby static-html comment only where a literal fragment must remain string-based.

- [ ] **Step 4: Re-run the DOM safety and UI tests**

Run:

```bash
node tests/dom_safety_innerhtml_static_analysis.test.js
node tests/staff_public_option_selects.test.js
node tests/settings_accordion_behavior.test.js
node tests/staff_users_list.test.js
node tests/staff_analytics.test.js
```

Expected result after the cleanup:

```text
... tests passed.
```

---

### Task 4: Full Verification

**Files:**
- No additional edits.

- [ ] **Step 1: Run the full test suite**

Run:

```bash
npm test
```

Expected result:

```text
All tests passed.
```
