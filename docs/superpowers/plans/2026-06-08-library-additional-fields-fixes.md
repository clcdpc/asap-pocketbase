# Library Additional Fields — Code Review Fixes

> **For agentic workers:** Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 5 issues identified in the library-additional-fields code review (1 high, 2 medium, 2 low).

**Architecture:** Each fix is standalone; they touch different files and can be implemented in any order.

**Tech Stack:** JavaScript (ES modules on frontend, CommonJS on backend), PocketBase hooks, JSDOM tests.

---

### Task 1: Fix race condition in patron field config loading (HIGH)

The edit modal (`modals.js:renderEditCustomFieldsForCurrentFormat`) reads `currentAdditionalFieldDefinitions` and `currentFormatRules`, which are populated only by `setCurrentPatronFieldConfig` inside `loadSettings`. But `loadSettings` is fired fire-and-forget from `checkAuth()`, so the edit modal can open before the settings fetch completes — resulting in no custom fields rendered.

**Fix:** In `loadStaffConfig()`, after the config response arrives, also extract `formatRules` and `additionalFieldDefinitions` from the response and populate both `additionalFieldDefinitions` (for settings editor) and `currentAdditionalFieldDefinitions`/`currentFormatRules` (for edit modal). The settings-based `populatePatronUiForms` will overwrite with library-specific data when it completes.

**Files:**
- Modify: `pb_public/staff/js/settings.js`

- [ ] **Step 1: Add field config population to loadStaffConfig**

In `pb_public/staff/js/settings.js:1047-1068`, add `setAdditionalFieldDefinitions` and `setCurrentPatronFieldConfig` calls after `updatePublicationOptionsUi`:

```js
export async function loadStaffConfig() {
  try {
    const res = await fetch('/api/asap/config');
    const config = await res.json();
    if (config) {
      if (config.logoUrl) {
        document.getElementById('app-icon').href = config.logoUrl;
        document.getElementById('setup-logo').src = config.logoUrl;
        document.getElementById('login-logo').src = config.logoUrl;
        document.getElementById('nav-logo').src = config.logoUrl;
      }
      if (config.logoAlt) {
        document.getElementById('setup-logo').alt = config.logoAlt;
        document.getElementById('login-logo').alt = config.logoAlt;
        document.getElementById('nav-logo').alt = config.logoAlt;
      }
      updatePublicationOptionsUi(config.publicationOptions);
      setAdditionalFieldDefinitions(config.additionalFieldDefinitions || []);
      setCurrentPatronFieldConfig(config.additionalFieldDefinitions || [], config.formatRules || {});
    }
  } catch (err) {
    console.error('Failed to load global config');
  }
}
```

- [ ] **Step 2: Add the import**

Ensure `settings.js` imports `setCurrentPatronFieldConfig` from state (it already does — verify line 1 of the diff in settings.js, the import statement was updated to include it).

- [ ] **Step 3: Verify with tests**

Run: `node tests/run_all.js`
Expected: All tests pass, no regressions.

- [ ] **Step 4: Commit**

```bash
git add pb_public/staff/js/settings.js
git commit -m "fix: populate patron field config from initial config load to avoid race condition"
```

---

### Task 2: Fix aria-labelledby on additional fields accordion (MEDIUM)

The additional-fields-panel's `aria-labelledby` points to the wrapper div `additional-fields-accordion` instead of the trigger button. The button has no `id`.

**Files:**
- Modify: `pb_public/staff/index.html`

- [ ] **Step 1: Add id to button and fix aria-labelledby**

In `pb_public/staff/index.html:1067-1081`:

```html
                  <div class="asap-accordion-item" id="additional-fields-accordion">
                    <button type="button" class="asap-accordion-header" id="additional-fields-header" aria-expanded="false" aria-controls="additional-fields-panel">
                      <span class="asap-accordion-title">Additional Fields</span>
                      <span class="asap-accordion-summary">Library-only fields for patron submissions</span>
                      <i class="fa fa-chevron-down asap-accordion-chevron" aria-hidden="true"></i>
                    </button>
                    <div id="additional-fields-panel" class="asap-accordion-panel" role="region" aria-labelledby="additional-fields-header">
```

- [ ] **Step 2: Commit**

```bash
git add pb_public/staff/index.html
git commit -m "fix: correct aria-labelledby on additional fields accordion panel"
```

---

### Task 3: Align normalizeMode default between frontend and backend (MEDIUM)

`lib/custom_fields.js:normalizeMode` defaults invalid modes to `'optional'`, while the patron frontend (`form-rules.js`) defaults to `'hidden'`. Since the backend normalizes before the frontend renders, invalid modes never reach the frontend in practice — but the defaults should agree.

**Fix:** Change the backend default from `'optional'` to `'hidden'`. Update the test that asserts the old default.

**Files:**
- Modify: `lib/custom_fields.js`
- Modify: `tests/custom_fields.test.js`

- [ ] **Step 1: Change normalizeMode default in lib/custom_fields.js**

In `lib/custom_fields.js:113`, change:

```js
function normalizeMode(value) {
  value = normalizeString(value);
  if (FIELD_MODES.indexOf(value) === -1) return "hidden";
  return value;
}
```

- [ ] **Step 2: Update the test expectation**

In `tests/custom_fields.test.js:1416-1432`, the `testNormalizeFormatCustomFieldRules` function asserts that `content_note: { mode: "banana" }` normalizes to `"optional"`. Change the assertion to expect `"hidden"`:

```js
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
    content_note: { mode: "hidden" },
    disabled_field: { mode: "hidden" }
  });
}
```

- [ ] **Step 3: Run tests to verify**

Run: `node tests/run_all.js custom_fields`
Expected: All custom field tests pass (including the updated assertion).

- [ ] **Step 4: Commit**

```bash
git add lib/custom_fields.js tests/custom_fields.test.js
git commit -m "fix: align normalizeMode default to 'hidden' between frontend and backend"
```

---

### Task 4: Omit additionalFieldDefinitions from system-context save payload (LOW)

`_serializeSettingsState` sets `additionalFieldDefinitions: isSystemContext ? undefined : collectAdditionalFieldDefinitions()`. The `undefined` value relies on `JSON.stringify` dropping it. More explicit to conditionally add the key.

**Files:**
- Modify: `pb_public/staff/js/settings.js`

- [ ] **Step 1: Refactor to conditionally include the key**

In `pb_public/staff/js/settings.js:765-782`, change the return object to conditionally include `additionalFieldDefinitions`:

```js
    publicationOptions: collectOptionList('ui-publication-options-editor', defaultPublicationOptions),
    formatRules: collectPatronFormatRules()
  };

  if (!isSystemContext) {
    payload.additionalFieldDefinitions = collectAdditionalFieldDefinitions();
  }
```

But wait — the current code uses a single return statement with a big object literal. Let me check the exact structure.

Looking at lines 759-782, it's:
```js
  return {
    pinLabel: ...,
    ...
    additionalFieldDefinitions: isSystemContext ? undefined : collectAdditionalFieldDefinitions()
  };
```

The refactor should be:

```js
  const payload = {
    pinLabel: getFieldValue('ui-pin-label'),
    loginPrompt: getFieldValue('ui-login-prompt'),
    loginNote: getFieldValue('ui-login-note'),
    suggestionFormNote: getFieldValue('ui-suggestion-note'),
    noEmailMessage: getFieldValue('ui-no-email-msg'),
    systemNotEnabledMessage: isSystemContext ? getFieldValue('ui-system-not-enabled-msg') : undefined,
    misconfiguredMessage: isSystemContext ? getFieldValue('ui-misconfigured-msg') : undefined,
    successTitle: getFieldValue('ui-success-title'),
    successMessage: getFieldValue('ui-success-message'),
    ...
    formatRules: collectPatronFormatRules()
  };

  if (!isSystemContext) {
    payload.additionalFieldDefinitions = collectAdditionalFieldDefinitions();
  }

  return payload;
```

Wait, I need to verify exact field names and structure. Let me look at the full function.

Actually, I'll keep it really tight — just change the one line:

```js
    formatRules: collectPatronFormatRules(),
  };
  if (!isSystemContext) {
    result.additionalFieldDefinitions = collectAdditionalFieldDefinitions();
  }
```

But I need to know the variable name used. The current code returns the object literal directly: `return { ... };`. I need to capture it in a variable first.

Let me look at the actual full function to be precise.

Actually, looking at the diff again at line 1265-1282, it shows:
```
1265: @@ -773,7 +777,8 @@ function _serializeSettingsState(validate = false) {
1266:      formatOrder: collectFormatOrder(),
1267:      availableFormats: collectAvailableFormats(),
1268:      publicationOptions: collectOptionList('ui-publication-options-editor', defaultPublicationOptions),
1269: -    formatRules: collectPatronFormatRules()
1270: +    formatRules: collectPatronFormatRules(),
1271: +    additionalFieldDefinitions: isSystemContext ? undefined : collectAdditionalFieldDefinitions()
1272:    };
```

So the function returns an object literal. The cleanest fix is to extract it to a variable and then conditionally add the key. Let me check if there are other fields after this one (the `;` at line 1272).

The object literal is `return { ... };` — I need to change it to assign to a variable and return it.

Actually, there's a much simpler approach that's less disruptive: use object spread conditionally:

```js
    formatRules: collectPatronFormatRules(),
    ...(isSystemContext ? {} : { additionalFieldDefinitions: collectAdditionalFieldDefinitions() })
  };
```

This is clean, doesn't require refactoring the return, and explicitly omits the key in system context.

- [ ] **Step 1: Replace undefined with conditional spread**

Change line 1271:

```js
    ...(isSystemContext ? {} : { additionalFieldDefinitions: collectAdditionalFieldDefinitions() })
```

- [ ] **Step 2: Verify with tests**

Run: `node tests/run_all.js`
Expected: All tests pass (settings serialization test mocks `collectAdditionalFieldDefinitions`).

- [ ] **Step 3: Commit**

```bash
git add pb_public/staff/js/settings.js
git commit -m "fix: omit additionalFieldDefinitions key in system-context save payload"
```

---

### Task 5: Move setCurrentPatronFieldConfig before format rules render (LOW)

In `populatePatronUiForms`, `renderPatronFormatRulesEditor` is called at line 715, but `setCurrentPatronFieldConfig` is called at line 716 — after the format rules render. This means `currentAdditionalFieldDefinitions` and `currentFormatRules` are stale during the format rules render call (though in practice the format rules editor reads `additionalFieldDefinitions` directly, not `currentAdditionalFieldDefinitions`). Move the `setCurrentPatronFieldConfig` call to right after the definitions are loaded for clarity.

**Files:**
- Modify: `pb_public/staff/js/settings.js`

- [ ] **Step 1: Reorder calls**

In `pb_public/staff/js/settings.js:699-717`, move `setCurrentPatronFieldConfig` before `renderPatronFormatRulesEditor`:

```js
  renderOptionListEditor('ui-publication-options-editor', uiText.publicationOptions, defaultPublicationOptions);
  setAdditionalFieldDefinitions(uiText.additionalFieldDefinitions || []);
  renderAdditionalFieldsEditor(uiText.additionalFieldDefinitions || []);
  setCurrentPatronFieldConfig(uiText.additionalFieldDefinitions || [], uiText.formatRules || {});
  const patronScope = document.getElementById('patron-options-scope');
  if (patronScope) {
    if (currentLibraryContextOrgId === 'system') {
      patronScope.textContent = 'Editing global patron form defaults.';
      patronScope.className = 'small mt-2 mb-0 text-muted';
    } else if (uiText.patronSettingsInherited) {
      patronScope.textContent = 'Showing inherited global patron form options. Saving will create custom options for the selected library only.';
      patronScope.className = 'small mt-2 mb-0 text-warning';
    } else {
      patronScope.textContent = 'Editing custom patron form options for the selected library.';
      patronScope.className = 'small mt-2 mb-0 text-info';
    }
  }
  renderPatronFormatRulesEditor(uiText.formatRules);
  updatePublicationOptionsUi(uiText.publicationOptions);
```

Delete the old `setCurrentPatronFieldConfig` line that was after `renderPatronFormatRulesEditor`.

- [ ] **Step 2: Verify with tests**

Run: `node tests/run_all.js`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add pb_public/staff/js/settings.js
git commit -m "fix: set patron field config before rendering format rules editor"
```
