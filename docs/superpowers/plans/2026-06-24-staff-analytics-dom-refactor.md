# Staff Analytics DOM Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** convert `pb_public/staff/js/analytics.js` from string-based rendering to DOM node construction and remove its local HTML-escaping helper.

**Architecture:** keep analytics as a single module because it owns one screen and is already compact. Preserve the existing abort-guarded `authorizedJson` load path, scope/date-range state, and `refreshAnalyticsView(container)` entrypoint. Replace the template-string render pipeline with small DOM-builder helpers that return `HTMLElement`s and compose them with `append`, `appendChild`, and `replaceChildren`.

**Tech Stack:** ES modules in `pb_public/staff/js/`, shared `authorizedJson`, shared `createLatestLoad`, browser DOM APIs, and the existing Node test runner.

---

## File Structure

- Modify: `pb_public/staff/js/analytics.js`
  - Remove the local `escapeHtml` helper.
  - Convert loading, error, shell, control, card, panel, and row rendering to DOM nodes.
- Create: `tests/staff_analytics_dom_safety.test.js`
  - Static regression guard that fails if `analytics.js` still uses `innerHTML` or keeps the local escaping helper.
- No change: `pb_public/staff/js/grid-data.js`
  - It should continue to call `refreshAnalyticsView(container)` unchanged.

---

### Task 1: Add Analytics DOM-Safety Regression

**Files:**
- Create: `tests/staff_analytics_dom_safety.test.js`

- [ ] **Step 1: Write the failing test**

Create `tests/staff_analytics_dom_safety.test.js` with:

```js
const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/analytics.js'), 'utf8');

assert.ok(source.includes('export async function loadAnalytics(container)'), 'analytics.js should still export loadAnalytics');
assert.ok(!/\.innerHTML\s*=/.test(source), 'analytics.js should not assign innerHTML');
assert.ok(!source.includes('function escapeHtml('), 'analytics.js should not define a local escapeHtml helper');
assert.ok(source.includes('document.createElement('), 'analytics.js should build DOM nodes directly');
assert.ok(source.includes('replaceChildren('), 'analytics.js should replace container contents with DOM nodes');

console.log('staff analytics DOM safety test passed.');
```

- [ ] **Step 2: Run the test to verify it fails before the refactor**

Run:

```bash
node tests/staff_analytics_dom_safety.test.js
```

Expected result before the code change:

```text
AssertionError [ERR_ASSERTION]: analytics.js should not assign innerHTML
```

- [ ] **Step 3: Confirm the test is discovered by the suite**

Run:

```bash
node tests/run_all.js staff_analytics_dom_safety
```

Expected result before the fix:

```text
==> staff_analytics_dom_safety.test.js
AssertionError [ERR_ASSERTION]: analytics.js should not assign innerHTML
```

---

### Task 2: Convert Analytics Rendering To DOM Nodes

**Files:**
- Modify: `pb_public/staff/js/analytics.js`

- [ ] **Step 1: Replace the local escaping helper and loading/error markup**

Remove the local `escapeHtml` function and render the loading/error states with a small DOM helper inside the same file:

```js
function renderStatus(container, className, message) {
  const node = document.createElement('div');
  node.className = className;
  node.textContent = message;
  container.replaceChildren(node);
}

export async function loadAnalytics(container) {
  if (!container) return;
  const guard = analyticsLoads.begin('analytics');
  renderStatus(container, 'alert alert-light border', 'Loading analytics...');

  try {
    const data = await authorizedJson(analyticsUrl(), {
      cache: 'no-store',
      signal: guard.signal
    });
    if (!guard.isCurrent()) return;
    analyticsScope = data.scope && data.scope.mode === 'all' ? 'all' : (data.scope && data.scope.libraryOrgId) || analyticsScope;
    renderAnalytics(container, data);
  } catch (err) {
    if (isAbortError(err) || !guard.isCurrent()) return;
    renderStatus(container, 'alert alert-danger', err.message || 'Analytics could not be loaded.');
  } finally {
    analyticsLoads.finish('analytics', guard.token);
  }
}
```

- [ ] **Step 2: Convert `renderAnalytics()` into DOM composition**

Replace the template string shell with a DOM tree built from helper functions:

```js
function renderAnalytics(container, data) {
  const shell = document.createElement('section');
  shell.className = 'analytics-shell';
  shell.setAttribute('aria-labelledby', 'analytics-title');
  shell.append(
    renderAnalyticsHeader(data),
    renderSummaryCards(data.summary),
    renderAnalyticsGrid(data)
  );
  container.replaceChildren(shell);
  bindAnalyticsControls(container);
}
```

- [ ] **Step 3: Convert each render helper to return nodes instead of HTML strings**

Update these helpers to return `HTMLElement`s:

```js
function renderScopeControl(data) { /* returns div or label */ }
function renderDateRangeControl(selected) { /* returns label with select and option nodes */ }
function renderAnalyticsHeader(data) { /* returns header wrapper with title, date range, and controls */ }
function renderAnalyticsGrid(data) { /* returns div.analytics-grid */ }
function renderSummaryCards(summary) { /* returns div.analytics-summary */ }
function renderSummaryCard(label, value, hint) { /* returns article.analytics-card */ }
function renderStageCounts(stageCounts) { /* returns article.analytics-panel */ }
function renderAging(aging) { /* returns article.analytics-panel */ }
function renderClosedReasons(closedReasons) { /* returns article.analytics-panel */ }
function renderExceptions(exceptions) { /* returns article.analytics-panel */ }
function renderPanel(title, hint, body) { /* returns article.analytics-panel */ }
function renderRows(rows) { /* returns div.analytics-table */ }
```

Keep the existing labels, IDs, class names, and `aria-*` attributes exactly the same so `bindAnalyticsControls(container)` continues to work.

- [ ] **Step 4: Preserve select state and row text with DOM properties**

Use `option.selected = true`, `select.value = ...`, and `textContent` for dynamic values instead of string interpolation. The super-admin scope selector should still include:

```js
const allOption = document.createElement('option');
allOption.value = 'all';
allOption.textContent = 'All libraries';
if (data.scope.mode === 'all') allOption.selected = true;
```

And the date-range selector should still render the same four labels from `dateRangeLabels`.

- [ ] **Step 5: Keep the public entrypoint unchanged**

Leave `refreshAnalyticsView(container)` as a thin wrapper around `loadAnalytics(container)` so `grid-data.js` does not need to change.

---

### Task 3: Verify The DOM Refactor

**Files:**
- No additional edits.

- [ ] **Step 1: Run the analytics DOM safety test**

Run:

```bash
node tests/staff_analytics_dom_safety.test.js
```

Expected result after the refactor:

```text
staff analytics DOM safety test passed.
```

- [ ] **Step 2: Run the generic staff DOM safety scan**

Run:

```bash
node tests/dom_safety_innerhtml_static_analysis.test.js
```

Expected result:

```text
(no output)
```

- [ ] **Step 3: Run the full suite**

Run:

```bash
npm test
```

Expected result:

```text
All tests passed.
```
