# MODULE_TYPELESS_PACKAGE_JSON Warning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove Node's `MODULE_TYPELESS_PACKAGE_JSON` warnings from tests without converting PocketBase hook code or CommonJS tests to ESM.

**Architecture:** Keep the repository root package as CommonJS-compatible. Add a nested `pb_public/package.json` that marks only browser-delivered frontend JavaScript as ESM for Node's resolver, then add a regression test that dynamically imports representative frontend modules and fails if Node emits `MODULE_TYPELESS_PACKAGE_JSON`.

**Tech Stack:** Node.js, CommonJS test runner, browser ES modules under `pb_public/`, existing `tests/run_all.js`.

---

## File Structure

- Create: `tests/module_type_warning.test.js`
  - Runs a child Node process that dynamically imports representative frontend ES modules.
  - Asserts stderr does not contain `MODULE_TYPELESS_PACKAGE_JSON`.
- Create: `pb_public/package.json`
  - Contains only `{ "type": "module" }`.
  - Scopes ESM package behavior to browser frontend modules under `pb_public/`.
- No change: root `package.json`
  - Must remain without `"type": "module"` so CommonJS backend/tests continue working.
- No change: `pb_hooks/`, `lib/`, or PocketBase route files
  - These stay CommonJS-style and outside the nested ESM package scope.

---

### Task 1: Add Failing Warning Regression Test

**Files:**
- Create: `tests/module_type_warning.test.js`

- [ ] **Step 1: Write the failing test**

Create `tests/module_type_warning.test.js` with:

```js
const assert = require('assert');
const { spawnSync } = require('child_process');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');

const modules = [
  './pb_public/patron/js/custom-fields.js',
  './pb_public/patron/js/form-ui.js',
  './pb_public/staff/js/settings-additional-fields.js',
  './pb_public/staff/js/request-custom-fields.js',
];

const script = `
const modules = ${JSON.stringify(modules)};
for (const specifier of modules) {
  try {
    await import(specifier);
  } catch (err) {
    // These are browser modules and may touch DOM globals during evaluation.
    // This test only cares about Node's module-type warning emitted during parse.
  }
}
`;

const result = spawnSync(process.execPath, ['--input-type=module', '--eval', script], {
  cwd: ROOT,
  encoding: 'utf8',
});

assert.strictEqual(
  result.status,
  0,
  `module warning probe should exit cleanly\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`
);

assert.ok(
  !result.stderr.includes('MODULE_TYPELESS_PACKAGE_JSON'),
  `frontend ES module imports should not emit MODULE_TYPELESS_PACKAGE_JSON warnings\nstderr:\n${result.stderr}`
);

console.log('module type warning regression test passed.');
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
node tests/module_type_warning.test.js
```

Expected result before the fix:

```text
AssertionError [ERR_ASSERTION]: frontend ES module imports should not emit MODULE_TYPELESS_PACKAGE_JSON warnings
```

The stderr should include at least one `MODULE_TYPELESS_PACKAGE_JSON` warning.

- [ ] **Step 3: Confirm this test is picked up by the suite**

Run:

```bash
node tests/run_all.js
```

Expected result before the fix:

```text
==> module_type_warning.test.js
AssertionError [ERR_ASSERTION]: frontend ES module imports should not emit MODULE_TYPELESS_PACKAGE_JSON warnings
```

---

### Task 2: Scope ESM Type To Frontend Assets

**Files:**
- Create: `pb_public/package.json`
- Verify unchanged: `package.json`

- [ ] **Step 1: Add nested package metadata**

Create `pb_public/package.json` with:

```json
{
  "type": "module"
}
```

- [ ] **Step 2: Verify root package stays CommonJS-compatible**

Run:

```bash
node -e "const pkg = require('./package.json'); if (pkg.type) throw new Error('root package.json must not set type'); console.log('root package remains typeless')"
```

Expected result:

```text
root package remains typeless
```

- [ ] **Step 3: Re-run the warning regression test**

Run:

```bash
node tests/module_type_warning.test.js
```

Expected result after the fix:

```text
module type warning regression test passed.
```

---

### Task 3: Verify Existing Frontend Module Guards

**Files:**
- Existing test: `tests/module_import_paths.test.js`
- Existing test: `tests/module_import_cycles.test.js`

- [ ] **Step 1: Run import path and named export validation**

Run:

```bash
node tests/module_import_paths.test.js
```

Expected result:

```text
Running module import path validation tests...
All ... import paths resolve correctly.
```

- [ ] **Step 2: Run settings import cycle validation**

Run:

```bash
node tests/module_import_cycles.test.js
```

Expected result:

```text
Running module import cycle regression tests...
All settings import cycle regression checks passed.
```

---

### Task 4: Run Full Verification

**Files:**
- No additional edits.

- [ ] **Step 1: Run the full suite**

Run:

```bash
npm test
```

Expected result:

```text
All tests passed.
```

The output should not contain `MODULE_TYPELESS_PACKAGE_JSON`.

- [ ] **Step 2: Check for accidental broad ESM conversion**

Run:

```bash
node - <<'NODE'
const fs = require('fs');
const rootPkg = JSON.parse(fs.readFileSync('package.json', 'utf8'));
const publicPkg = JSON.parse(fs.readFileSync('pb_public/package.json', 'utf8'));
if (rootPkg.type) throw new Error('Root package.json should not define type');
if (publicPkg.type !== 'module') throw new Error('pb_public/package.json should define type=module');
console.log('module type scope is correct');
NODE
```

Expected result:

```text
module type scope is correct
```

- [ ] **Step 3: Inspect working tree**

Run:

```bash
git diff -- package.json pb_public/package.json tests/module_type_warning.test.js
```

Expected result:

- `package.json` has no diff.
- `pb_public/package.json` is a new file containing only `"type": "module"`.
- `tests/module_type_warning.test.js` is the new warning regression test.

---

## Self-Review

**Spec coverage:** The plan removes `MODULE_TYPELESS_PACKAGE_JSON` warnings by giving Node an explicit module type for frontend ES modules. It avoids the known high-risk approach of setting root `"type": "module"`, which would affect CommonJS tests and backend hook code.

**Placeholder scan:** No task contains placeholder implementation text. Each code/file change and verification command is explicit.

**Type consistency:** The package-type boundary is consistently `pb_public/` only. Test file names and commands match the current CommonJS test runner conventions.
