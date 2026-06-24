# Modals Refactor Design

## Summary

Split `pb_public/staff/js/modals.js` (1499 lines, 24 exports, 53 functional blocks) into focused modules while preserving the existing public import surface through a `modals.js` barrel. Replace three raw `fetch` calls with `authorizedJson`. Merge two near-duplicate submit paths into a shared action submitter. Extract Polaris search into its own module. Gate scattered top-level event listeners behind an explicit `init()` call.

This is a structural refactor only. It must not change staff dialog behavior, request/refresh semantics, row action behavior, or public exports.

## Motivation

`modals.js` bundles the edit dialog, Polaris search dialog, patron context rendering, additional-copy confirmation, rejection template selection, audit preview rendering, claim/workflow tag rendering, and event listeners into a single file. The edit form submit handler and `performImmediateStaffAction` contain near-duplicate 90-line `fetch` + error-handling logic. Three locations use raw `fetch` with manual `Authorization` headers and `JSON.stringify` — bypassing the project-standard `authorizedJson` helper and the `requestJson` serialization contract.

The grid.js refactor establishes patterns that apply here: context objects with live getters, callback injection to avoid cycles, events coordinator modules, and incremental phased execution.

Architectural rules to preserve:

- Staff refreshes route through `refreshCurrentStaffView()`, not ad hoc `loadTab(currentStatus)` calls.
- `authorizedJson` callers pass plain object bodies and let `requestJson` serialize JSON.
- `innerHTML` is not used for new or refactored UI code; DOM construction is preferred.
- The public `modals.js` module remains backward compatible for existing consumers.

## Target Module Boundaries

### 1. `modals/context.js`

Owns the edit form context adapter. Exposes live getters for mutable DOM elements and passes through stable `state.js` references. Follows the same getter pattern as `grid-context.js` — do not copy mutable values into plain object properties.

```js
// pb_public/staff/js/modals/context.js
export function createModalContext(state) {
  return {
    // Stable PocketBase/settings references
    pb: state.pb,
    formatMap: state.formatMap,
    availableFormats: state.availableFormats,
    publicationOptions: state.publicationOptions,

    // Live mutable values from state.js
    get currentRejectionTemplates() { return state.currentRejectionTemplates; },
    get workflowSettings() { return state.workflowSettings; },
    get currentAdditionalFieldDefinitions() { return state.currentAdditionalFieldDefinitions; },
    get currentFormatRules() { return state.currentFormatRules; },

    // State setters
    setVerifiedBibId: state.setVerifiedBibId,

    // Live edit form DOM elements
    get modal() { return document.getElementById('editModal'); },
    get id() { return document.getElementById('edit-id'); },
    get title() { return document.getElementById('edit-title'); },
    get author() { return document.getElementById('edit-author'); },
    get identifier() { return document.getElementById('edit-identifier'); },
    get bibid() { return document.getElementById('edit-bibid'); },
    get format() { return document.getElementById('edit-format'); },
    get publication() { return document.getElementById('edit-publication'); },
    get exactPublicationDate() { return document.getElementById('edit-exact-publication-date'); },
    get autohold() { return document.getElementById('edit-autohold'); },
    get action() { return document.getElementById('edit-action'); },
    get nextStatus() { return document.getElementById('edit-next-status'); },
    get notes() { return document.getElementById('edit-notes'); },
    get rejectionTemplate() { return document.getElementById('edit-rejection-template'); },
    get submitBtn() { return document.getElementById('edit-submit-btn'); },
    get bibInfoDisplay() { return document.getElementById('bib-info-display'); },
    get bibInfoText() { return document.getElementById('bib-info-text'); },
    get selectedPolarisBibId() { return document.getElementById('selectedPolarisBibId'); },
    get selectedPolarisTitle() { return document.getElementById('selectedPolarisTitle'); },
    get selectedPolarisAuthor() { return document.getElementById('selectedPolarisAuthor'); },
    get selectedPolarisIdentifier() { return document.getElementById('selectedPolarisIdentifier'); },
    get selectedPolarisPublication() { return document.getElementById('selectedPolarisPublication'); },
    get selectedPolarisFormat() { return document.getElementById('selectedPolarisFormat'); },
    get emailPurchaseReminder() { return document.getElementById('edit-email-purchase-reminder'); },
    get bibidHint() { return document.getElementById('edit-bibid-hint'); },
    get bibidRequiredMarker() { return document.getElementById('edit-bibid-required'); },
    get auditPreview() { return document.getElementById('edit-pending-audit-preview'); },
    get auditPreviewText() { return document.getElementById('edit-pending-audit-preview-text'); },
    get rejectionContainer() { return document.getElementById('edit-rejection-template-container'); },
    get rejectionAvailability() { return document.getElementById('edit-rejection-template-availability'); },
    get leapBibLinkContainer() { return document.getElementById('edit-leap-bib-link-container'); },
    get externalSearchContainer() { return document.getElementById('edit-external-search-container'); },
    get purchaseReminderContainer() { return document.getElementById('edit-purchase-reminder-container'); },
    get purchaseReminderCheckbox() { return document.getElementById('edit-email-purchase-reminder'); },
    get purchaseReminderHelp() { return document.getElementById('edit-purchase-reminder-help'); },
    get patronContextBlock() { return document.getElementById('edit-patron-context'); },
    get workflowTags() { return document.getElementById('edit-workflow-tags'); },
    get claimState() { return document.getElementById('edit-claim-state'); },
    get metadataBlock() { return document.getElementById('edit-metadata'); },
    get modalLabel() { return document.getElementById('editModalLabel'); },

    // Mutable flags that were previously module-level variables
    holdingsLookupUnavailable: false
  };
}
```

### 2. `modals/utils.js`

Owns shared pure helpers. No DOM access, no fetch calls, no state mutations.

Dependencies: none beyond standard JS.

Contents:

- `actionErrorMessage(status, data, raw)` — normalizes API error messages
- `duplicateOpenRequestMessage(data)` — formats duplicate detection error text
- `workflowStatusLabel(status)` — maps status keys to human-readable labels
- `staffProfileEmail(pb)` — reads staff profile email from PocketBase auth store
- `hasOwn(obj, key)` — safe `Object.prototype.hasOwnProperty` call
- `basicPolarisSearchText(value)` — cleans title/author text for Polaris search
- `looksLikeCatalogWrappedValue(prefix)` — detects catalog-style title wrappers
- `fallbackPolarisSearchValue(value)` — resolves Polaris search text from potentially wrapped values
- `polarisSearchValueForRow(row, mode)` — resolves Polaris search text per mode
- `looksLikeCatalogPublicationDate(value)` — detects catalog-style date strings
- `normalizedAdditionalCopyPublication(value, isAdditionalCopy, publicationOptions)` — normalizes publication for additional-copy tasks

### 3. `modals/patron-context.js`

Owns patron information display in the edit modal. All DOM construction is safe.

Dependencies: `modals/context.js`, `modals/utils.js`, `./api.js` (for `leapPatronUrl`), `./grid.js` (for `escapeAttr`)

Contents:

- `renderPatronContext(row, options)` — generalized patron info block with expand/collapse toggle
- `renderEditPatronContext(row, ctx)` — edit-modal-specific wrapper

### 4. `modals/confirm-duplicate.js`

Owns the duplicate request confirmation dialog.

Dependencies: `./dialogs.js` (for `showConfirm`), `./actions.js` (for `closeDuplicateRequest`)

Contents: `confirmDuplicateOpenRequestClose(err, id)`

### 5. `modals/additional-copy.js`

Owns the additional-copy confirmation dialog with email reminder checkbox.

Dependencies: `modals/context.js` (for `pb`)

Contents: `confirmAdditionalCopyAction(result, options)` — standalone `<dialog>`-based confirm; all safe DOM

### 6. `modals/edit-form.js`

Owns edit dialog form population. `openEdit` is the hub function that orchestrates all sub-renderers.

Dependencies: `modals/context.js`, `modals/utils.js`, `modals/patron-context.js`, `modals/claim-tags.js`, `modals/audit-preview.js`, `modals/rejection-templates.js`, `./recent-suggestions.js`, `./edit-pickup.js`, `./request-custom-fields.js`

Contents:

- `openEdit(row, nextStatus, dialogTitle, actionStr, buttonLabel, ctx)` — populates entire edit modal
- `getExistingHistory(row)`, `getDraftCommentValue(ctx)`
- `setBibIdRequirement(nextStatus, ctx)`
- `renderEditLeapBibLink(bibId, ctx)` — DOM construction, no `innerHTML`
- `renderExternalSearchButton(title, identifier, ctx)`
- `renderEditMetadata(row, ctx)`
- `renderPurchaseReminderOption(actionStr, ctx)`
- `renderEditCustomFieldsForCurrentFormat(row, ctx)`

### 7. `modals/claim-tags.js`

Owns workflow tag and claim state rendering in the edit dialog.

Dependencies: `modals/context.js`, `./grid.js` (for `renderWorkflowTags`)

Contents:

- `renderEditClaimState(row, ctx)`
- `renderEditWorkflowTags(tags, row, ctx)`
- `reactiveCleanupWorkflowFlags(rowId, ctx)`

### 8. `modals/audit-preview.js`

Owns the pending audit preview in the edit dialog.

Dependencies: `modals/context.js`, `modals/utils.js`

Contents:

- `buildPendingAuditPreview(row, nextStatus, actionStr, ctx)`
- `renderPendingAuditPreview(row, nextStatus, actionStr, ctx)`

### 9. `modals/rejection-templates.js`

Owns rejection template selection UI.

Dependencies: `modals/context.js`

Contents:

- `renderRejectionTemplateSelector(actionStr, ctx)`
- `renderRejectionTemplateAvailability(count)`

### 10. `modals/polaris-search.js`

Owns the Polaris search dialog — the largest single subsystem (~400 lines). Uses `authorizedJson` instead of raw `fetch`.

Dependencies: `modals/context.js`, `modals/utils.js`, `modals/additional-copy.js`, `./http.js`, `./dialogs.js`, `./settings-ui.js`

Contents:

- `openPolarisSearch(row, mode, options, ctx, onRefresh)` — opens dialog, wires UI, runs search
- `closePolarisSearchDialog()`
- `fetchPolarisSearch(row, mode, query, options, ctx)` — **uses `authorizedJson`**
- `renderPolarisSearchResults(row, mode, data, options, ctx, onRefresh)` — renders result cards
- `launchEditPolarisSearch(mode, button, context, ctx, onRefresh)` — orchestrates from button clicks
- `performImmediateStaffAction(id, payload, ctx, onRefresh)` — **uses `authorizedJson`**
- `currentEditPolarisSearchRow(context, ctx)`, `editPolarisSearchInputForMode(mode, context)`
- `polarisSearchElements()`, `polarisSearchModeLabel(mode)`, `polarisSearchButtonLabel(mode)`
- `renderPolarisSearchButtonMarkup(mode, attrs)`, `polarisResultMeta(result)`, `polarisSearchQueryForRow(row, mode)`

### 11. `modals/edit-submit.js`

Owns edit form submission. Replaces the monolithic `#edit-form` submit listener with a named, testable function. Shares the `submitTitleRequestAction` path with `modals/polaris-search.js`.

Dependencies: `modals/context.js`, `modals/utils.js`, `./http.js`, `./dialogs.js`, `./recent-suggestions.js`, `./request-custom-fields.js`

Contents:

- `submitEditForm(ctx, onRefresh)` — validates form, builds payload, posts via `authorizedJson`
- `submitTitleRequestAction(id, payload, ctx, onRefresh)` — shared between edit form submit and `performImmediateStaffAction`

### 12. `modals/events.js`

Owns delegated DOM event handlers. The coordinator that wires modules without circular imports.

Dependencies: `modals/polaris-search.js`, `modals/edit-submit.js`, `modals/claim-tags.js`, `./api.js`

Contents:

- `initModalEvents(ctx, { onRefresh })` — registers all event listeners:
  - Polaris search close buttons (2)
  - Edit/new Polaris search trigger buttons (6)
  - `#edit-form` submit → `submitEditForm(ctx, onRefresh)`
  - `#edit-format`, `#edit-publication`, `#edit-autohold` change → audit refresh
  - `#edit-bibid` input → audit refresh
  - `window.asap-bib-verified` → `reactiveCleanupWorkflowFlags` + audit refresh
  - `.js-open-profile-dialog` click → `openProfileDialog()`
- Uses `eventsBound` module-level flag to prevent double-binding

### 13. Barrel: `modals.js`

Owns the public compatibility surface. Exports wrapper functions that bind `ctx` and `onRefresh`.

```js
import * as state from './state.js';
import { createModalContext } from './modals/context.js';
import { ... } from './modals/utils.js';
import { renderPatronContext, renderEditPatronContext } from './modals/patron-context.js';
import { confirmDuplicateOpenRequestClose } from './modals/confirm-duplicate.js';
import { confirmAdditionalCopyAction } from './modals/additional-copy.js';
import { openEdit as _openEdit, ... } from './modals/edit-form.js';
import { ... } from './modals/claim-tags.js';
import { ... } from './modals/audit-preview.js';
import { ... } from './modals/rejection-templates.js';
import { openPolarisSearch as _openPolarisSearch, ... } from './modals/polaris-search.js';
import { submitEditForm } from './modals/edit-submit.js';
import { initModalEvents } from './modals/events.js';

const ctx = createModalContext(state);

function onRefresh() { return refreshCurrentStaffView(); }

// Wrappers that bind ctx and onRefresh
export function openEdit(id, ns, title, action, label) { return _openEdit(id, ns, title, action, label, ctx); }
export function openPolarisSearch(row, mode, opts) { return _openPolarisSearch(row, mode, opts, ctx, onRefresh); }
export function renderEditClaimState(row) { return _renderEditClaimState(row, ctx); }
// ... etc for all context-dependent exports

// Direct re-exports (no context needed)
export { renderPatronContext, renderEditPatronContext };
export { confirmDuplicateOpenRequestClose };
export { confirmAdditionalCopyAction };
export { getExistingHistory, getDraftCommentValue, renderExternalSearchButton };
export { workflowStatusLabel };
export { polarisSearchValueForRow, staffProfileEmail };
export { polarisSearchButtonLabel, renderPolarisSearchButtonMarkup };

// Init events
import { refreshCurrentStaffView } from './grid.js';
initModalEvents(ctx, { onRefresh });
```

## Dependency Graph

```text
modals.js (barrel)
  -> modals/context.js
  -> modals/utils.js
  -> modals/patron-context.js
      -> modals/context.js, modals/utils.js, (api, grid)
  -> modals/confirm-duplicate.js
      -> (dialogs, actions)
  -> modals/additional-copy.js
      -> modals/context.js
  -> modals/edit-form.js
      -> modals/context.js, modals/utils.js
      -> modals/patron-context.js, modals/claim-tags.js, modals/audit-preview.js, modals/rejection-templates.js
      -> (recent-suggestions, edit-pickup, request-custom-fields)
  -> modals/claim-tags.js
      -> modals/context.js, (grid)
  -> modals/audit-preview.js
      -> modals/context.js, modals/utils.js
  -> modals/rejection-templates.js
      -> modals/context.js
  -> modals/edit-submit.js
      -> modals/context.js, modals/utils.js, (http, dialogs, recent-suggestions, request-custom-fields)
  -> modals/polaris-search.js
      -> modals/context.js, modals/utils.js, modals/additional-copy.js
      -> (http, dialogs, settings-ui)
  -> modals/events.js
      -> modals/polaris-search.js, modals/edit-submit.js, modals/claim-tags.js
      -> (api)
  -> (grid.js for onRefresh callback)
```

Forbidden imports:

- `modals/polaris-search.js` ←X `modals/events.js`, `modals/edit-submit.js`
- `modals/edit-form.js` ←X `modals/polaris-search.js`, `modals/edit-submit.js`, `modals/events.js`
- `modals/events.js` ←X `modals/edit-form.js`, `modals/audit-preview.js`
- No `modals/*` module imports from `modals.js` (barrel)
- No raw `fetch` for JSON API calls; use `authorizedJson`

## Technical Debt Fixed

| Location | Issue | Fix |
|----------|-------|-----|
| `fetchPolarisSearch` line 701 | raw `fetch` | `authorizedJson(...)` |
| Edit submit handler line 1273 | raw `fetch` | `authorizedJson(...)` in `submitEditForm` |
| `performImmediateStaffAction` line 1364 | raw `fetch` | `authorizedJson(...)` |
| Edit submit + `performImmediateStaffAction` | ~90 line duplicated error/toast logic | `submitTitleRequestAction` |
| `renderEditLeapBibLink` line 523 | `innerHTML` | DOM construction |
| `holdingsLookupUnavailable` line 799 | module-level mutable flag | `ctx.holdingsLookupUnavailable` |
| Top-level `addEventListener` calls | run at import time | `initModalEvents(ctx, { onRefresh })` |

## Execution Plan

### Phase 0: Baseline inventory and tests

1. Capture all 24 current exports from `modals.js` (names and line numbers).
2. Capture all consumer imports from `./modals.js`: `grid.js` (5 symbols), `patron.js` (1), `recent-suggestions.js` (1), `settings.js` (1).
3. Identify source-inspection tests: `security_url_validation.test.js`, `staff_modal_duplicate_error.test.js`, `external_search_provider4.test.js`, `polaris_grid_search_ui.test.js`, `frontend_request_architecture.test.js`.
4. `npm test`

### Phase 1: Context and utilities

1. Create `modals/context.js` with live getters.
2. Create `modals/utils.js` — move all pure helpers.
3. Update `modals.js` imports/re-exports.
4. Update source-inspection tests pointing at moved utility functions.
5. `npm test`

### Phase 2: Small self-contained modules

1. Create `modals/patron-context.js`, `modals/additional-copy.js`, `modals/confirm-duplicate.js`.
2. Wire re-exports in `modals.js`.
3. `npm test`

### Phase 3: Polaris search

1. Create `modals/polaris-search.js` — move all 14 Polaris functions.
2. Replace raw `fetch` with `authorizedJson` in `fetchPolarisSearch` and `performImmediateStaffAction`.
3. Add `ctx` and `onRefresh` parameters.
4. Wire re-exports with wrappers.
5. Update source-inspection tests.
6. `npm test`

### Phase 4: Edit form renderers

1. Create `modals/claim-tags.js`, `modals/audit-preview.js`, `modals/rejection-templates.js`.
2. Create `modals/edit-form.js` — move `openEdit` and all sub-renderers.
3. Fix `renderEditLeapBibLink` innerHTML → DOM construction.
4. Wire re-exports with wrappers.
5. Update source-inspection tests.
6. `npm test`

### Phase 5: Edit submit and shared action submitter

1. Create `modals/edit-submit.js` — `submitEditForm` + `submitTitleRequestAction`.
2. Replace `performImmediateStaffAction` to use `submitTitleRequestAction`.
3. Update `staff_modal_duplicate_error.test.js`.
4. `npm test`

### Phase 6: Events coordinator

1. Create `modals/events.js` — `initModalEvents(ctx, { onRefresh })`.
2. Move all `addEventListener` calls; add `eventsBound` guard.
3. Barrel calls `initModalEvents` once.
4. `npm test`

### Phase 7: Barrel cleanup and final validation

1. `modals.js` is now thin barrel.
2. Verify zero consumer import changes.
3. Verify all 24 exports present.
4. Update `frontend_request_architecture.test.js` paths.
5. Circular dependency check.
6. `npm test`.
7. Commit.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| `openEdit` calls 15+ sub-renderers — signature changes break chain | Keep same signatures; only add `ctx` parameter uniformly |
| Polaris search references edit form DOM | Context object with live getters abstracts DOM access |
| Source-inspection tests extract functions from `modals.js` source | Update test file paths each phase; no duplicate function bodies |
| `holdingsLookupUnavailable` loses module state | Moved to `ctx.holdingsLookupUnavailable` |
| Barrel misses an export | Phase 0 inventory; Phase 7 diff verification |
| Circular imports via barrel | No `modals/*` imports `modals.js`; manual review at Phase 7 |
| Double-bound event listeners | `eventsBound` guard in `initModalEvents` |
| `renderEditLeapBibLink` innerHTML change breaks output | DOM construction produces identical HTML; verify with test |
| `submitTitleRequestAction` diverges between callers | Single implementation in `modals/edit-submit.js` |

## Success Criteria

- `npm test` passes after every phase
- `modals.js` under 70 lines
- Zero consumer import path changes
- All 24 exports preserved
- All 3 raw `fetch` calls → `authorizedJson`
- Edit submit + `performImmediateStaffAction` share `submitTitleRequestAction`
- `renderEditLeapBibLink` uses DOM construction
- All event listeners gated behind `initModalEvents`
- `frontend_request_architecture.test.js` assertions updated and pass
- No circular dependencies among `modals/*` modules
