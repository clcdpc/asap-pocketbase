# Comma-Separated List Parsing Helper Refactor Plan

**Goal:** consolidate the 6 duplicated comma-separated-string parsing sites into a shared helper.

**Architecture:** create `lib/split-list.js` as a tiny dependency-free module. Do not put the helper in `lib/route_utils.js` because `identity.js` must remain low-level and free of route/runtime dependencies.

**Tech Stack:** CommonJS backend modules, PocketBase hook runtime.

---

## File Structure

### Create
- `lib/split-list.js` — `splitList`

### Modify
- `lib/staff/settings_save.js` — import `split-list` and replace inline parse
- `lib/config_routes.js` — import `split-list` and replace inline parse
- `lib/staff/auth_routes.js` — import `split-list` and replace inline parse
- `lib/staff/effective_library.js` — import `split-list` and replace inline parse
- `lib/patron_routes.js` — import `split-list` and replace inline parse
- `lib/identity.js` — import `split-list` and replace `String(value || "").split(",").forEach(...)` with `splitList.split(value).forEach(...)`
- `tests/split-list.test.js` — add direct unit tests

### No change
- All other tests — behavior is identical, just implementation moves

---

## Guardrail

Do not import `route_utils.js` into `identity.js`. `identity.js` must remain low-level and free of route/runtime dependencies.

---

## Current call sites

| File | Line | Current code |
|---|---|---|
| `lib/staff/settings_save.js` | 150 | `String(csv || "").split(",").map(s => s.trim()).filter(Boolean)` |
| `lib/config_routes.js` | 36 | `enabledLibraries.split(",").map(id => id.trim()).filter(id => id.length > 0)` |
| `lib/staff/auth_routes.js` | 80 | `String(enabledListStr || "").split(",").map(id => id.trim()).filter(id => id.length > 0)` |
| `lib/staff/effective_library.js` | 42 | `String(appSettings.enabledLibraryOrgIds || "").split(",").map(id => id.trim()).filter(id => id.length > 0)` |
| `lib/patron_routes.js` | 13 | `String(appSettings.enabledLibraryOrgIds || "").split(",").map(id => id.trim()).filter(id => id.length > 0)` |
| `lib/identity.js` | 59 | `String(value || "").split(",").forEach(...)` |

Note: `identity.js` currently does not trim/filter before iterating. However, `parseStaffIdentity()` trims internally and empty identity entries are skipped, so the end result is equivalent.

---

## Task 1: Create `lib/split-list.js`

```js
function split(value) {
  return String(value || "")
    .split(",")
    .map(function (s) { return s.trim(); })
    .filter(function (s) { return s.length > 0; });
}

module.exports = {
  split: split
};
```

---

## Task 2: Create `tests/split-list.test.js`

```js
const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";
const splitList = require("../lib/split-list.js");

assert.deepStrictEqual(splitList.split("a,b,c"), ["a", "b", "c"]);
assert.deepStrictEqual(splitList.split(" a , b , c "), ["a", "b", "c"]);
assert.deepStrictEqual(splitList.split(""), []);
assert.deepStrictEqual(splitList.split(null), []);
assert.deepStrictEqual(splitList.split("single"), ["single"]);
assert.deepStrictEqual(splitList.split("a,,b"), ["a", "b"]);

console.log("split-list tests passed.");
```

---

## Task 3: Replace each call site

Each replacement follows the same pattern — change:

```js
String(value || "").split(",").map(s => s.trim()).filter(Boolean)
```

to:

```js
splitList.split(value)
```

Import at the top of each file using the hook-relative path:

```js
const splitList = require(`${__hooks}/../lib/split-list.js`);
```

For `identity.js`, replace:

```js
String(value || "").split(",").forEach(function (item) {
```

with:

```js
splitList.split(value).forEach(function (item) {
```

---

## Task 4: Verify

```bash
node tests/split-list.test.js
node tests/route_utils.test.js
node tests/settings_save.test.js
node tests/config_routes.test.js
node tests/staff_auth_users.test.js
node tests/patron_login.test.js
npm test
```

---

## Risk

Minimal. Each replacement is a mechanical 1-line substitution. `.filter(Boolean)` and `.filter(function(id) { return id.length > 0; })` are semantically identical. The `identity.js` case is behavior-equivalent because `parseStaffIdentity` already trims and empty entries are skipped.
