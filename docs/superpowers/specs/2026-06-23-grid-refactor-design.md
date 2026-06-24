# Grid Refactor Design

## Summary

Split `pb_public/staff/js/grid.js` into focused staff-grid modules while preserving the existing public import surface through `grid.js`. The refactor must avoid stale state snapshots, avoid circular imports, and account for tests that currently inspect `grid.js` source text directly.

This is a structural refactor only. It must not change staff grid behavior, request/refresh semantics, row action behavior, or public exports.

## Motivation

`grid.js` currently bundles data fetching, grid rendering, filtering, duplicate detection, row actions, action menu UI, and DOM event listeners in one large module. That makes behavior hard to isolate and makes small changes risky.

The recent request-helper and refresh-helper cleanup makes this a good time to split the file, but the split should preserve these architectural rules:

- Staff refreshes route through `refreshCurrentStaffView()` / `refreshStaffStatus()`, not ad hoc `loadTab(currentStatus)` calls outside the data module.
- `authorizedJson` callers pass plain object bodies and let `requestJson` serialize JSON.
- The public `grid.js` module remains backward compatible for existing consumers.
- Mutable state from `state.js` must stay live after extraction.

## Target Module Boundaries

### 1. `grid-context.js`

Owns the staff grid context adapter. It should expose live getters for mutable `state.js` bindings and pass through setters/actions.

Do not copy mutable values into plain object properties at construction time. ES module live bindings are lost when copied into an object. Use getters for mutable values.

```js
// pb_public/staff/js/grid-context.js
export function createGridContext(state) {
  return {
    // Stable DOM/auth references
    pb: state.pb,
    gridContainer: state.gridContainer,
    staffGridFilterBar: state.staffGridFilterBar,
    tagFilterSelect: state.tagFilterSelect,
    claimFilterSelect: state.claimFilterSelect,
    similarRequestFilterSelect: state.similarRequestFilterSelect,
    additionalCopyStatusFilterSelect: state.additionalCopyStatusFilterSelect,
    closedTypeFilterSelect: state.closedTypeFilterSelect,
    gridSearchInput: state.gridSearchInput,
    settingsContainer: state.settingsContainer,

    // Stable metadata
    formatMap: state.formatMap,
    ageMap: state.ageMap,
    closeReasonMap: state.closeReasonMap,
    descriptions: state.descriptions,
    emptyStateMessages: state.emptyStateMessages,
    statusStages: state.statusStages,
    rowActionRegistry: state.rowActionRegistry,

    // Live mutable values
    get grid() { return state.grid; },
    get currentStatus() { return state.currentStatus; },
    get currentSuggestions() { return state.currentSuggestions; },
    get allSuggestions() { return state.allSuggestions; },
    get activeTagFilter() { return state.activeTagFilter; },
    get gridSearchKeyword() { return state.gridSearchKeyword; },
    get currentClaimFilter() { return state.currentClaimFilter; },
    get currentSimilarRequestFilter() { return state.currentSimilarRequestFilter; },
    get currentAdditionalCopyStatus() { return state.currentAdditionalCopyStatus; },
    get currentClosedTypeFilter() { return state.currentClosedTypeFilter; },
    get currentWorkflowOrgScopeId() { return state.currentWorkflowOrgScopeId; },
    get workflowSettings() { return state.workflowSettings; },
    get currentSettingsSection() { return state.currentSettingsSection; },
    get activeActionMenu() { return state.activeActionMenu; },

    // Mutators
    setCurrentStatus: state.setCurrentStatus,
    setCurrentSuggestions: state.setCurrentSuggestions,
    setActiveTagFilter: state.setActiveTagFilter,
    setGridSearchKeyword: state.setGridSearchKeyword,
    setCurrentClaimFilter: state.setCurrentClaimFilter,
    setCurrentWorkflowOrgScopeId: state.setCurrentWorkflowOrgScopeId,
    setCurrentClosedTypeFilter: state.setCurrentClosedTypeFilter,
    setActiveActionMenu: state.setActiveActionMenu,
    setGrid: state.setGrid,
    setAllSuggestions: state.setAllSuggestions,
    incrementRowActionIdCounter: state.incrementRowActionIdCounter
  };
}
```

### 2. `grid-utils.js`

Owns shared formatting and HTML helpers that are used by more than one grid module.

Contents:

- `escapeAttr`
- `sanitizeHtml`
- `formatStandardDate`
- `formatDateTime`
- `formatPublication`
- `formatNote`
- URL helpers used by grid cells, if needed after extraction

`grid.js` should re-export public utilities from this module so existing imports like `import { escapeAttr } from './grid.js'` keep working.

### 3. `grid-filters.js`

Owns pure record filtering and duplicate/tag derivation.

Dependencies:

- `grid-policy.mjs`
- `grid-utils.js` only if escaping remains in badge HTML helpers
- context object as a parameter

Contents:

- `applyTagFilter(records, ctx)`
- `applyTypeFilter(records, ctx)`
- `applySimilarRequestFilter(records, ctx)`
- `applyClaimFilter(records, ctx)`
- `applyAllFilters(records, ctx)`
- `getDuplicateSummary(row)`
- `getDuplicateLabels(row)`
- `hasWorkflowTag(row, label)`
- `getWorkflowTagPresentation(tag)`
- `getDuplicateBadgesHtml(row, ctx)`
- `getIsbnCheckBadgesHtml(row, ctx)`
- `normalizeMatchText`
- `normalizeMatchIdentifier`
- `duplicateMatchReasons`
- `duplicateStatusNames`

Do not put DOM-mutating filter controls in this module. Keeping this module pure makes it easy to test with plain records and a mock context.

### 4. `grid-filter-controls.js`

Owns the staff filter UI controls and their DOM/state mutation.

Dependencies:

- `grid-filters.js` for filter metadata where needed
- `grid-data.js` is not imported directly
- `onRefresh` callback injected by the barrel/coordinator

Contents:

- `updateTagFilter(records, ctx)`
- `updateClaimFilter(records, ctx)`
- `hideTagFilter(ctx)`
- `hideClaimFilter(ctx)`
- `toggleTagFilter(tag, ctx, onRefresh)`
- filter select/listener binding helpers if they are extracted from `grid.js`

This separate module prevents the pure filter module from growing DOM side effects.

### 5. `grid-rendering.js`

Owns gridjs construction, column definitions, and cell rendering. It should not own row action execution or global event binding.

Dependencies:

- `grid-filters.js`
- `grid-filter-controls.js` only for rendering filter UI state if needed
- `grid-utils.js`
- `grid-actions.js` for `renderRowActions` only, or receive `renderRowActions` as an injected callback if a cycle appears during implementation
- context object as a parameter

Contents:

- `renderCurrentGrid(ctx)`
- `renderStatusGrid(records, ctx)`
- `renderAdditionalCopiesGrid(records, ctx)`
- `renderAdditionalCopiesLoadError(error, ctx)`
- `getGridColumns(status, rowById, ctx)`
- `getActionsColumnWidth(status)`
- `renderTitleCell(row, ctx)`
- `renderAuthorCell(row)`
- `renderClaimCell(row)`
- `renderBarcodeCell(row, ctx)`
- `renderBibIdCell(row, ctx)`
- `renderAdditionalCopySourceCell(row)` — private/exported as needed by `grid-events.js` for the note/activity dialog
- `rowMarker(row)`
- `renderWorkflowTags(tags, row, ctx)`
- `renderDuplicateSummary(row, ctx)`
- `renderPolarisRowSearchButton(row, mode)`
- sort helpers and `NOTES_COLUMN_WIDTH`

### 6. `grid-actions.js`

Owns row action descriptors, action materialization, row mutation actions, and the floating action menu.

Dependencies:

- `grid-utils.js`
- modal/action modules currently used by row actions
- context object as a parameter
- `onRefresh` callback injected by caller for mutations that need to reload the current grid

This module must not import `grid-data.js`. Importing data from actions while rendering or event code imports actions creates a cycle. Use callback injection:

```js
export async function claimRequest(requestId, ctx, onRefresh) {
  await mutateRequestClaim(requestId, 'claim', 'Claimed.', ctx);
  await onRefresh();
}
```

Contents:

- `runRowActionDescriptor(row, action, ctx, onRefresh)`
- `openAssignDialog(row, ctx, onRefresh)`
- `buyAnotherCopyForRow(row, ctx, onRefresh)`
- `closeAdditionalCopyRequest(id, ctx, onRefresh)`
- `additionalCopyActionForRow(row)`
- `additionalCopyConfirmMessage(bibid, count)`
- `claimRequest(requestId, ctx, onRefresh)`
- `unclaimRequest(requestId, ctx, onRefresh)`
- `mutateRequestClaim(requestId, action, successMessage, ctx)`
- `claimActionsForRow(row, ctx)`
- `getRowActions(row, ctx)`
- `materializeRowActions(row, actions, ctx, onRefresh)`
- `renderRowActions(row, ctx, onRefresh)`
- `openActionMenu(triggerButton, actionIds, ctx)`
- `positionActionMenu(triggerButton, menu)`
- `closeActionMenu(ctx)`
- `registerRowAction(action, ctx)`
- `getRegisteredRowAction(actionId, ctx)`
- `runRowAction(action)`
- `currentStaffId(ctx)`
- `isClaimedByCurrentUser(row, ctx)`
- `isUnclaimed(row)`
- `formatCloseReason(row, ctx)`

### 7. `grid-events.js`

Owns delegated DOM event handlers for the grid container and document/window action-menu listeners.

Dependencies:

- `grid-actions.js`
- `grid-filter-controls.js`
- modal/patron/note modules currently used by click handlers
- context object
- refresh callback injected by `grid.js`

This module is the coordinator that resolves the previous rendering/actions/data cycle. Rendering should render. Actions should mutate. Events should dispatch.

Contents:

- `initGridEventListeners(ctx, onRefresh)`
- `initActionMenuListeners(ctx)`
- `handleRowActionClick(event, ctx)`
- `handleMenuTriggerClick(event, ctx)`
- `handleNoteTruncateClick(event, ctx)`
- `handleDuplicateToggleClick(event, ctx)`
- `handleTagBadgeClick(event, ctx, onRefresh)`
- `handleQuickNewClick(event)`
- `handlePolarisSearchClick(event, ctx)`
- `handleRowEditClick(event, ctx)`
- `shouldIgnoreRowEditClick(target, event)`
- `openSuggestionEditFromRow(recordId, ctx)`

### 8. `grid-data.js`

Owns API calls, tab orchestration, guarded load lifecycle, workflow scope controls, and grid refresh functions.

Dependencies:

- `grid-rendering.js`
- `grid-filter-controls.js`
- `latest-load.js`
- `http.js`
- context object as a parameter

Contents:

- `loadTab(status, ctx)`
- `refreshCurrentStaffView(ctx)`
- `refreshStaffStatus(status, ctx)`
- `fetchTitleRequests(signal, ctx)`
- `fetchAdditionalCopies(status, signal, ctx)`
- `safeFetchTitleRequests(signal, ctx)`
- `safeFetchAdditionalCopies(status, signal, ctx)`
- `syncStatusTab(status, ctx)`
- `renderTabDescription(status, ctx)`
- `clearJobMessage()`
- `updateAdminActions(status, ctx)`
- `loadSettingsTab(ctx)`
- `prepareGridView(ctx)`
- `announceTabLoaded(status, ctx)`
- `handleLoadTabError(error, status, ctx)`
- `updateWorkflowScopeControl(data, ctx, onRefresh)`
- `hideWorkflowScopeControl(ctx)`
- `updateTabCounts(records, openCount, closedCount, ctx)`
- `resetGrid(ctx)`

`grid-data.js` is allowed to call `renderCurrentGrid(ctx)`, but `grid-rendering.js`, `grid-actions.js`, and `grid-events.js` must not import `grid-data.js`.

### 9. Barrel: `grid.js`

Owns the public compatibility surface and wires the default context.

Existing consumers should continue importing from `./grid.js`. Because internal implementations accept `ctx`, `grid.js` must export wrapper functions for context-aware APIs instead of directly re-exporting them.

```js
// pb_public/staff/js/grid.js
import * as state from './state.js';
import { createGridContext } from './grid-context.js';
import { loadTab as loadTabWithContext, refreshCurrentStaffView as refreshCurrentStaffViewWithContext, refreshStaffStatus as refreshStaffStatusWithContext } from './grid-data.js';
import { initGridEventListeners, initActionMenuListeners } from './grid-events.js';
import { renderBibIdCell as renderBibIdCellWithContext } from './grid-rendering.js';
import { closeActionMenu as closeActionMenuWithContext, claimRequest as claimRequestWithContext } from './grid-actions.js';

const ctx = createGridContext(state);

export { normalizeLabel, flagDisplayMap, getFlagDisplay, getIsbnCheckLabel, effectiveWorkflowFlagsForRow, getFilterableLabelsForRow, normalizeWorkflowTagLabel, cleanWorkflowTags, tagCountsForRecords, normalizeStatus } from './grid-policy.mjs';
export { escapeAttr, sanitizeHtml, formatStandardDate, formatDateTime, formatPublication, formatNote } from './grid-utils.js';
export { renderPolarisRowSearchButton, getActionsColumnWidth } from './grid-rendering.js';
export { positionActionMenu, isUnclaimed } from './grid-actions.js';

export function loadTab(status) {
  return loadTabWithContext(status, ctx);
}

export function refreshCurrentStaffView() {
  return refreshCurrentStaffViewWithContext(ctx);
}

export function refreshStaffStatus(status) {
  return refreshStaffStatusWithContext(status, ctx);
}

export function renderBibIdCell(row) {
  return renderBibIdCellWithContext(row, ctx);
}

export function closeActionMenu() {
  return closeActionMenuWithContext(ctx);
}

export function claimRequest(requestId) {
  return claimRequestWithContext(requestId, ctx, refreshCurrentStaffView);
}

initGridEventListeners(ctx, refreshCurrentStaffView);
initActionMenuListeners(ctx);
```

The snippet is illustrative, not the full export list. If an exported function from `grid-rendering.js` or `grid-actions.js` needs `ctx` or `onRefresh`, export a wrapper instead of a direct re-export. The exact export list must be generated from current `grid.js` before implementation and checked after each phase.

## Dependency Graph

Allowed dependency direction:

```text
grid.js
  -> grid-context.js
  -> grid-data.js
      -> grid-rendering.js
      -> grid-filter-controls.js
      -> grid-filters.js
  -> grid-events.js
      -> grid-actions.js
      -> grid-filter-controls.js
      -> grid-utils.js
  -> grid-rendering.js
      -> grid-filters.js
      -> grid-utils.js
  -> grid-actions.js
      -> grid-utils.js
  -> grid-filter-controls.js
      -> grid-filters.js
  -> grid-utils.js
  -> grid-policy.mjs
```

Forbidden imports:

- `grid-actions.js` must not import `grid-data.js`.
- `grid-rendering.js` must not import `grid-events.js`.
- `grid-data.js` must not import `grid-events.js`.
- No new module should import from `grid.js`; imports should target the owning module to avoid barrel cycles.

## Execution Plan

### Phase 0: Baseline inventory and tests

1. Capture current public exports from `pb_public/staff/js/grid.js`.
2. Capture current `from './grid.js'` consumers.
3. Inventory private helpers used across current concern boundaries, including `renderAdditionalCopySourceCell(row)` because the note/activity dialog uses it while it belongs with DOM renderers.
4. Identify tests that read `grid.js` source directly:
   - `tests/security_url_validation.test.js`
   - `tests/polaris_grid_search_ui.test.js`
   - `tests/note_activity_ui.test.js`
   - any additional matches from `rg "grid\\.js" tests`
5. Run `npm test` before extracting modules.

### Phase 1: Context and utilities

1. Create `grid-context.js` with live getters for mutable state.
2. Create `grid-utils.js` and move shared utility functions.
3. Update `grid.js` to import from `grid-utils.js` and re-export the same utility names.
4. Update source-inspection tests that target moved utility functions to read `grid-utils.js` or the new owning module.
5. Run focused tests, then `npm test`.

### Phase 2: Pure filters

1. Create `grid-filters.js`.
2. Move pure filtering, duplicate summary, duplicate labels, and workflow tag derivation.
3. Keep DOM filter controls in `grid.js` for this phase.
4. Update `grid.js` imports/re-exports.
5. Add or update focused tests for `applyAllFilters` and duplicate/tag derivation if existing coverage is only source-string based.
6. Run focused tests, then `npm test`.

### Phase 3: Filter controls

1. Create `grid-filter-controls.js`.
2. Move filter DOM helpers and ensure refresh behavior is injected with an `onRefresh` callback.
3. Verify tag toggles still use `refreshCurrentStaffView()` behavior through the callback.
4. Run `node tests/frontend_request_architecture.test.js`, then `npm test`.

### Phase 4: Rendering

1. Create `grid-rendering.js`.
2. Move cell renderers, `renderAdditionalCopySourceCell`, grid column builders, sort helpers, row marker helpers, and gridjs render functions.
3. Keep click/event listeners in `grid.js` until `grid-events.js` exists.
4. Update source-inspection tests to point at `grid-rendering.js` for moved renderers.
5. Run `tests/security_url_validation.test.js`, `tests/polaris_grid_search_ui.test.js`, `tests/note_activity_ui.test.js`, then `npm test`.

### Phase 5: Actions

1. Create `grid-actions.js`.
2. Move row action descriptors, action materialization, action menu behavior, claim lifecycle, and additional-copy row actions.
3. Use `ctx` plus injected `onRefresh` instead of importing `grid-data.js`.
4. Keep delegated event listeners in `grid.js` until `grid-events.js` exists.
5. Run row/action tests and `npm test`.

### Phase 6: Events

1. Create `grid-events.js`.
2. Decompose the current `gridContainer` click listener into named handlers.
3. Move document/window action-menu listeners into `initActionMenuListeners(ctx)`.
4. Wire `grid.js` to call `initGridEventListeners(ctx, refreshCurrentStaffView)` and `initActionMenuListeners(ctx)` once on module load.
5. Verify no duplicate listeners are registered after repeated imports or tab loads.
6. Run `tests/frontend_request_architecture.test.js`, `tests/note_activity_ui.test.js`, `tests/polaris_grid_search_ui.test.js`, then `npm test`.

### Phase 7: Data and barrel cleanup

1. Create `grid-data.js`.
2. Move guarded tab loads, fetch helpers, tab counts, workflow scope controls, and refresh helpers.
3. Export public wrapper functions from `grid.js` that bind `ctx`.
4. Verify no consumer imports need to change.
5. Verify current export names still exist from `grid.js`.
6. Run `npm test`.

### Phase 8: Final validation

1. Run `rg "from './grid\\.js'|from \\\"./grid\\.js\\\"" pb_public/staff/js tests`.
2. Run `rg "from './grid\\.js'" pb_public/staff/js/grid-*.js` and verify there are no new-module imports from the barrel.
3. Run a circular dependency check manually by inspecting imports or with a lightweight script if available locally.
4. Run `npm test`.
5. Commit the full refactor only after all phases pass.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Stale context values | Use getters for mutable state bindings. Add a small test or source assertion that `createGridContext` defines getters for `currentStatus`, `currentSuggestions`, `allSuggestions`, `activeTagFilter`, `grid`, and `activeActionMenu`. |
| Circular dependencies | Keep events as a coordinator module and inject refresh callbacks into actions/filter controls. Forbid imports from `grid.js` inside new `grid-*` modules. |
| Consumer breakage from context-aware internals | `grid.js` exports wrapper functions that bind the default context. Do not directly re-export internal functions that require `ctx` unless their public signature remains unchanged. |
| Source-inspection tests fail after moving functions | Update tests to read the owning module. Do not keep duplicate function bodies in `grid.js` just to satisfy old tests. |
| Click handler decomposition changes behavior | Move one handler at a time and preserve selector order. The default row edit handler must remain last. Run focused UI/source tests after the event phase. |
| Action menu listeners double-bind | Move global listeners into `initActionMenuListeners(ctx)` and call it once from the barrel. Use a module-level `listenersBound` guard if repeated initialization is possible. |
| Refresh architecture regressions | Keep `frontend_request_architecture.test.js` checks for `refreshCurrentStaffView`, stale `loadTab` guards, and pre-stringified `authorizedJson` bodies. |

## Success Criteria

- `npm test` passes after every phase.
- `grid.js` remains the only public import surface for existing consumers.
- No existing consumer file needs an import change.
- `grid.js` exports the same public names as before the refactor.
- `grid.js` is a thin barrel/coordinator with default-context wrappers.
- Mutable context properties stay live after state setters run.
- No circular dependencies exist among new grid modules.
- Source-inspection tests point to the modules that own the inspected functions.
- The delegated grid click handler is decomposed into named handlers without changing selector order.
