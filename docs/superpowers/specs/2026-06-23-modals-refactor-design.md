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
- `looksLikeCatalogWrappedValue(prefix)` — detects catalog-style title wrappers like "Title / Author, 2023"
- `fallbackPolarisSearchValue(value)` — resolves Polaris search text from potentially wrapped values
- `polarisSearchValueForRow(row, mode)` — resolves Polaris search text per mode (title/author/identifier), preferring stored overrides
- `looksLikeCatalogPublicationDate(value)` — detects catalog-style date strings like "2024" or "2024-01-15"
- `normalizedAdditionalCopyPublication(value, isAdditionalCopy, publicationOptions)` — normalizes publication for additional-copy tasks

### 3. `modals/patron-context.js`

Owns patron information display in the edit modal. All DOM construction is safe (no `innerHTML` except for static developer-authored markup).

Dependencies: `modals/context.js`, `modals/utils.js`, `./api.js` (for `leapPatronUrl`), `./grid.js` (for `escapeAttr`)

Contents:

- `renderPatronContext(row, options)` — generalized patron info block with expand/collapse toggle
- `renderEditPatronContext(row, ctx)` — edit-modal-specific wrapper handling additional-copy hide logic

### 4. `modals/confirm-duplicate.js`

Owns the duplicate request confirmation dialog.

Dependencies: `./dialogs.js` (for `showConfirm`), `./actions.js` (for `closeDuplicateRequest`)

Contents:

- `confirmDuplicateOpenRequestClose(err, id)` — shows confirmation, optionally closes duplicate

### 5. `modals/additional-copy.js`

Owns the additional-copy confirmation dialog with email reminder checkbox.

Dependencies: `modals/context.js` (for `pb`)

Contents:

- `confirmAdditionalCopyAction(result, options)` — standalone `<dialog>`-based confirm with email reminder checkbox; all safe DOM

### 6. `modals/edit-form.js`

Owns edit dialog form population. `openEdit` is the hub function that orchestrates all sub-renderers.

Dependencies:

- `modals/context.js`
- `modals/utils.js`
- `modals/patron-context.js`
- `modals/claim-tags.js`
- `modals/audit-preview.js`
- `modals/rejection-templates.js`
- `./recent-suggestions.js` (for `rememberRecentSuggestion`, `renderRecentSuggestionsSwitcher`)
- `./edit-pickup.js` (for `loadEditPickupForRequest`)
- `./request-custom-fields.js` (for `renderEditCustomFields`)

Contents:

- `openEdit(row, nextStatus, dialogTitle, actionStr, buttonLabel, ctx)` — populates entire edit modal; delegates to sub-renderers
- `getExistingHistory(row)` — extracts `row.notes`
- `getDraftCommentValue(ctx)` — reads `#edit-notes` from DOM via context
- `setBibIdRequirement(nextStatus, ctx)` — toggles BIB field required/hint based on next status
- `renderEditLeapBibLink(bibId, ctx)` — renders "Open Bib in Leap" link using DOM construction (no `innerHTML`)
- `renderExternalSearchButton(title, identifier, ctx)` — renders up to 4 configurable external search buttons
- `renderEditMetadata(row, ctx)` — displays auto-promoter last-checked timestamp
- `renderPurchaseReminderOption(actionStr, ctx)` — shows/hides email purchase reminder checkbox
- `renderEditCustomFieldsForCurrentFormat(row, ctx)` — reads format select, delegates to `renderEditCustomFields`

### 7. `modals/claim-tags.js`

Owns workflow tag and claim state rendering in the edit dialog.

Dependencies: `modals/context.js`, `./grid.js` (for `renderWorkflowTags`)

Contents:

- `renderEditClaimState(row, ctx)` — renders claim badge (unclaimed/mine/claimed by X)
- `renderEditWorkflowTags(tags, row, ctx)` — renders workflow tag badges; delegates to `grid.renderWorkflowTags`
- `reactiveCleanupWorkflowFlags(rowId, ctx)` — removes stale flags after BIB verification

### 8. `modals/audit-preview.js`

Owns the pending audit preview in the edit dialog.

Dependencies: `modals/context.js`, `modals/utils.js`

Contents:

- `buildPendingAuditPreview(row, nextStatus, actionStr, ctx)` — builds preview text string from form state
- `renderPendingAuditPreview(row, nextStatus, actionStr, ctx)` — writes preview text into DOM element

### 9. `modals/rejection-templates.js`

Owns rejection template selection UI.

Dependencies: `modals/context.js`

Contents:

- `renderRejectionTemplateSelector(actionStr, ctx)` — populates `<select>` with available templates
- `renderRejectionTemplateAvailability(count)` — shows "N other templates available" hint

### 10. `modals/polaris-search.js`

Owns the Polaris search dialog — the largest single subsystem (~400 lines). Uses `authorizedJson` instead of raw `fetch`.

Dependencies:

- `modals/context.js`
- `modals/utils.js`
- `modals/additional-copy.js`
- `./http.js` (for `authorizedJson`, `isAbortError`)
- `./grid.js` (for `refreshCurrentStaffView` via `onRefresh` callback)
- `./dialogs.js` (for `showToast`, `showAlert`)
- `./settings-ui.js` (for `applySelectedPolarisResultToEditForm`)

Contents:

- `openPolarisSearch(row, mode, options, ctx, onRefresh)` — opens dialog, wires mode/input UI, runs initial search
- `closePolarisSearchDialog()` — closes `<dialog>`
- `fetchPolarisSearch(row, mode, query, options, ctx)` — **uses `authorizedJson`**, not raw `fetch`
- `renderPolarisSearchResults(row, mode, data, options, ctx, onRefresh)` — renders result cards with holdings badges and action buttons
- `launchEditPolarisSearch(mode, button, context, ctx, onRefresh)` — called from button clicks; orchestrates `openPolarisSearch`
- `performImmediateStaffAction(id, payload, ctx, onRefresh)` — **uses `authorizedJson`**; closes both dialogs, calls `onRefresh`
- `currentEditPolarisSearchRow(context, ctx)` — builds synthetic row from edit/new form DOM via context
- `editPolarisSearchInputForMode(mode, context)` — returns the form input for a given mode
- `polarisSearchElements()` — cache object returning all Polaris dialog DOM element references
- `polarisSearchModeLabel(mode)` — human-readable mode string
- `polarisSearchButtonLabel(mode)` — human-readable button label per mode
- `renderPolarisSearchButtonMarkup(mode, attrs)` — returns HTML string for a search trigger button
- `polarisResultMeta(result)` — formats result metadata string
- `polarisSearchQueryForRow(row, mode)` — assembles query string from row + mode

### 11. `modals/edit-submit.js`

Owns edit form submission. Replaces the monolithic `#edit-form` submit listener with a named, testable function. Shares the `submitTitleRequestAction` path with `modals/polaris-search.js`.

Dependencies:

- `modals/context.js`
- `modals/utils.js`
- `./http.js` (for `authorizedJson`)
- `./dialogs.js` (for `showToast`, `showAlert`, `showConfirm`)
- `./recent-suggestions.js` (for `rememberRecentSuggestion`, `updateRecentSuggestion`, `renderRecentSuggestionsSwitcher`)
- `./request-custom-fields.js` (for `collectEditCustomFieldValues`)

Contents:

- `submitEditForm(ctx, onRefresh)` — validates form, builds payload, posts via `authorizedJson`, handles errors/toast/refresh
- `submitTitleRequestAction(id, payload, ctx, onRefresh)` — shared between edit form submit and `performImmediateStaffAction`; replaced from both paths

### 12. `modals/events.js`

Owns delegated DOM event handlers. The coordinator that wires modules together without circular imports.

Dependencies:

- `modals/polaris-search.js` (for `launchEditPolarisSearch`, `closePolarisSearchDialog`)
- `modals/edit-submit.js` (for `submitEditForm`)
- `modals/claim-tags.js` (for `reactiveCleanupWorkflowFlags`)
- `./api.js` (for `openProfileDialog`)

Contents:

- `initModalEvents(ctx, { onRefresh })` — registers all event listeners that currently run at import time:
  - Polaris search close buttons (2 calls)
  - Edit/new title/author/identifier Polaris search trigger buttons (6 calls)
  - `#edit-form` submit → `submitEditForm(ctx, onRefresh)`
  - `#edit-format`, `#edit-publication`, `#edit-autohold` change → audit refresh
  - `#edit-bibid` input → audit refresh
  - `window.asap-bib-verified` custom event → `reactiveCleanupWorkflowFlags` + audit refresh
  - `.js-open-profile-dialog` click → `openProfileDialog()`
- Uses `eventsBound` module-level flag to prevent double-binding

### 13. Barrel: `modals.js`

Owns the public compatibility surface and wires the default context.

Existing consumers should continue importing from `./modals.js`. Because internal implementations accept `ctx`, `modals.js` must export wrapper functions for context-aware APIs instead of directly re-exporting them.

```js
// pb_public/staff/js/modals.js
import * as state from './state.js';
import { createModalContext } from './modals/context.js';
import {
  actionErrorMessage, duplicateOpenRequestMessage, workflowStatusLabel, staffProfileEmail,
  hasOwn, basicPolarisSearchText, fallbackPolarisSearchValue, polarisSearchValueForRow,
  looksLikeCatalogPublicationDate, normalizedAdditionalCopyPublication
} from './modals/utils.js';
import { renderPatronContext, renderEditPatronContext } from './modals/patron-context.js';
import { confirmDuplicateOpenRequestClose } from './modals/confirm-duplicate.js';
import { confirmAdditionalCopyAction } from './modals/additional-copy.js';
import {
  openEdit as openEditWithContext, getExistingHistory, getDraftCommentValue,
  setBibIdRequirement as setBibIdReqWithContext, renderEditLeapBibLink as renderEditLeapBibWithCtx,
  renderExternalSearchButton, renderEditMetadata as renderEditMetadataWithCtx,
  renderPurchaseReminderOption as renderPurchaseReminderWithCtx
} from './modals/edit-form.js';
import {
  renderEditClaimState as renderEditClaimWithCtx, renderEditWorkflowTags as renderEditTagsWithCtx,
  reactiveCleanupWorkflowFlags as reactiveCleanupFlagsWithCtx
} from './modals/claim-tags.js';
import {
  buildPendingAuditPreview as buildAuditPreviewWithCtx,
  renderPendingAuditPreview as renderAuditPreviewWithCtx
} from './modals/audit-preview.js';
import {
  renderRejectionTemplateSelector as renderRejectionWithCtx
} from './modals/rejection-templates.js';
import {
  openPolarisSearch as openPolarisSearchWithCtx, closePolarisSearchDialog,
  fetchPolarisSearch, renderPolarisSearchResults, launchEditPolarisSearch,
  polarisSearchElements, polarisSearchModeLabel, polarisSearchButtonLabel,
  renderPolarisSearchButtonMarkup, polarisResultMeta, polarisSearchQueryForRow
} from './modals/polaris-search.js';
import { submitEditForm } from './modals/edit-submit.js';
import { initModalEvents } from './modals/events.js';

const ctx = createModalContext(state);

// Export wrappers that bind ctx and onRefresh
function onRefresh() {
  // imported from grid.js at barrel level
  return refreshCurrentStaffView();
}

export function openEdit(id, nextStatus, dialogTitle, actionStr, buttonLabel) {
  return openEditWithContext(id, nextStatus, dialogTitle, actionStr, buttonLabel, ctx);
}

export function openPolarisSearch(row, mode, options) {
  return openPolarisSearchWithCtx(row, mode, options, ctx, onRefresh);
}

export function renderEditClaimState(row) {
  return renderEditClaimWithCtx(row, ctx);
}

export function renderEditWorkflowTags(tags, row) {
  return renderEditTagsWithCtx(tags, row, ctx);
}

export function renderEditLeapBibLink(bibId) {
  return renderEditLeapBibWithCtx(bibId, ctx);
}

export function renderEditMetadata(row) {
  return renderEditMetadataWithCtx(row, ctx);
}

export function renderPurchaseReminderOption(actionStr) {
  return renderPurchaseReminderWithCtx(actionStr, ctx);
}

export function renderRejectionTemplateSelector(actionStr) {
  return renderRejectionWithCtx(actionStr, ctx);
}

export function reactiveCleanupWorkflowFlags(rowId) {
  return reactiveCleanupFlagsWithCtx(rowId, ctx);
}

export function setBibIdRequirement(nextStatus) {
  return setBibIdReqWithContext(nextStatus, ctx);
}

export function buildPendingAuditPreview(row, nextStatus, actionStr) {
  return buildAuditPreviewWithCtx(row, nextStatus, actionStr, ctx);
}

export function renderPendingAuditPreview(row, nextStatus, actionStr) {
  return renderAuditPreviewWithCtx(row, nextStatus, actionStr, ctx);
}

// Direct re-exports (no context needed or already receives onRefresh)
export { renderPatronContext, renderEditPatronContext };
export { confirmDuplicateOpenRequestClose };
export { confirmAdditionalCopyAction };
export { getExistingHistory, getDraftCommentValue, renderExternalSearchButton };
export { workflowStatusLabel };
export { polarisSearchValueForRow, staffProfileEmail };
export { polarisSearchButtonLabel, renderPolarisSearchButtonMarkup };

// Init events on module load
import { refreshCurrentStaffView } from './grid.js';
initModalEvents(ctx, { onRefresh });
```

The snippet is illustrative, not the full export list. The exact export list must be generated from current `modals.js` before implementation and checked after each phase.

## Dependency Graph

All allowed edges:

```text
modals.js (barrel)
  -> modals/context.js
  -> modals/utils.js
  -> modals/patron-context.js
      -> modals/context.js
      -> modals/utils.js
      -> (api, grid)
  -> modals/confirm-duplicate.js
      -> (dialogs, actions)
  -> modals/additional-copy.js
      -> modals/context.js
  -> modals/edit-form.js
      -> modals/context.js
      -> modals/utils.js
      -> modals/patron-context.js
      -> modals/claim-tags.js
      -> modals/audit-preview.js
      -> modals/rejection-templates.js
      -> (recent-suggestions, edit-pickup, request-custom-fields)
  -> modals/claim-tags.js
      -> modals/context.js
      -> (grid.js for renderWorkflowTags)
  -> modals/audit-preview.js
      -> modals/context.js
      -> modals/utils.js
  -> modals/rejection-templates.js
      -> modals/context.js
  -> modals/edit-submit.js
      -> modals/context.js
      -> modals/utils.js
      -> (http, dialogs, recent-suggestions, request-custom-fields)
  -> modals/polaris-search.js
      -> modals/context.js
      -> modals/utils.js
      -> modals/additional-copy.js
      -> (http, dialogs, settings-ui)
  -> modals/events.js
      -> modals/polaris-search.js
      -> modals/edit-submit.js
      -> modals/claim-tags.js
      -> (api)
  -> (grid.js for onRefresh callback)
```

Forbidden imports:

- `modals/polaris-search.js` must not import `modals/events.js` or `modals/edit-submit.js`.
- `modals/edit-form.js` must not import `modals/polaris-search.js`, `modals/edit-submit.js`, or `modals/events.js`.
- `modals/events.js` must not import `modals/edit-form.js` or `modals/audit-preview.js`.
- No new `modals/*` module should import from `modals.js` (the barrel) to avoid cycles.
- No new module should use raw `fetch` for JSON API calls; use `authorizedJson`.

## Technical Debt Fixed In-Refactor

| Location | Current behavior | Fix |
|----------|-----------------|-----|
| `fetchPolarisSearch` (line 701) | Raw `fetch` with manual `Authorization` + `JSON.stringify` | `authorizedJson('/api/asap/staff/bib-lookup', { method: 'POST', body })` |
| Edit form submit handler (line 1273) | Raw `fetch` with manual auth + serialize + error parse | `authorizedJson(...)` in `submitEditForm` |
| `performImmediateStaffAction` (line 1364) | Raw `fetch` with manual auth + serialize + error parse | `authorizedJson(...)` with `onRefresh` callback |
| Edit submit + `performImmediateStaffAction` | Near-duplicate ~90 line error handling + payload + toast logic | Merge into shared `submitTitleRequestAction(id, payload, ctx, onRefresh)` |
| `renderEditLeapBibLink` (line 523) | `innerHTML` for link markup | DOM construction (`createElement('a')`) |
| `holdingsLookupUnavailable` (line 799) | Mutable module-level variable | Moved to context as `ctx.holdingsLookupUnavailable` |
| Top-level event listeners (lines 320-325, 1196-1205, 1482-1496) | Run at import time, no guard | Gated behind `initModalEvents(ctx, { onRefresh })` |

## Execution Plan

### Phase 0: Baseline inventory and tests

1. Capture all 24 current exports from `pb_public/staff/js/modals.js` (names and line numbers).
2. Capture all consumer imports from `./modals.js`:
   - `grid.js`: `openEdit`, `openPolarisSearch`, `polarisSearchValueForRow`, `renderPolarisSearchButtonMarkup`, `confirmAdditionalCopyAction`
   - `patron.js`: `renderPatronContext`
   - `recent-suggestions.js`: `workflowStatusLabel`
   - `settings.js`: `renderEditLeapBibLink`
3. Identify all tests that read `modals.js` source directly:
   - `tests/security_url_validation.test.js` — extracts `renderEditLeapBibLink`
   - `tests/staff_modal_duplicate_error.test.js` — reads `modals.js` source for assertions
   - `tests/external_search_provider4.test.js` — reads `modals.js` for assertions
   - `tests/polaris_grid_search_ui.test.js` — reads `modals.js` for assertions
   - `tests/frontend_request_architecture.test.js` — reads `modals.js` for `refreshCurrentStaffView`/`loadTab(currentStatus)` checks
4. Run `npm test` before extracting modules.

### Phase 1: Context and utilities

1. Create `modals/context.js` with live getters for all edit form DOM elements and mutable state.
2. Create `modals/utils.js` and move:
   - `actionErrorMessage`, `duplicateOpenRequestMessage`
   - `workflowStatusLabel`, `staffProfileEmail`
   - `hasOwn`, `basicPolarisSearchText`, `looksLikeCatalogWrappedValue`, `fallbackPolarisSearchValue`, `polarisSearchValueForRow`
   - `looksLikeCatalogPublicationDate`, `normalizedAdditionalCopyPublication`
3. Update `modals.js` to import from `modals/utils.js` and re-export the same names.
4. Update source-inspection tests that target moved utility functions to read `modals/utils.js`.
5. Run focused tests, then `npm test`.

### Phase 2: Small self-contained modules

1. Create `modals/patron-context.js` → move `renderPatronContext`, `renderEditPatronContext`.
2. Create `modals/additional-copy.js` → move `confirmAdditionalCopyAction`.
3. Create `modals/confirm-duplicate.js` → move `confirmDuplicateOpenRequestClose`.
4. Wire re-exports in `modals.js`.
5. Update source-inspection tests where needed.
6. Run `npm test`.

### Phase 3: Polaris search

1. Create `modals/polaris-search.js` → move all 14 Polaris functions:
   - `openPolarisSearch`, `closePolarisSearchDialog`, `fetchPolarisSearch`
   - `renderPolarisSearchResults`, `launchEditPolarisSearch`, `performImmediateStaffAction`
   - `currentEditPolarisSearchRow`, `editPolarisSearchInputForMode`, `polarisSearchElements`
   - `polarisSearchModeLabel`, `polarisSearchButtonLabel`, `renderPolarisSearchButtonMarkup`
   - `polarisResultMeta`, `polarisSearchQueryForRow`
2. Replace `fetchPolarisSearch` raw `fetch` with `authorizedJson`.
3. Replace `performImmediateStaffAction` raw `fetch` with `authorizedJson`. Add `onRefresh` callback parameter.
4. Add `ctx` parameter to `openPolarisSearch`, `renderPolarisSearchResults`, `launchEditPolarisSearch`, `performImmediateStaffAction`, `currentEditPolarisSearchRow`.
5. Wire re-exports in `modals.js`. Wrap context-dependent exports.
6. Update source-inspection tests pointing at Polaris search functions.
7. `npm test`.

### Phase 4: Edit form renderers

1. Create `modals/claim-tags.js` → move `renderEditClaimState`, `renderEditWorkflowTags`, `reactiveCleanupWorkflowFlags`. Add `ctx` parameter.
2. Create `modals/audit-preview.js` → move `buildPendingAuditPreview`, `renderPendingAuditPreview`. Add `ctx` parameter.
3. Create `modals/rejection-templates.js` → move `renderRejectionTemplateSelector`, `renderRejectionTemplateAvailability`. Add `ctx` parameter.
4. Create `modals/edit-form.js` → move:
   - `openEdit` (add `ctx` parameter)
   - `getExistingHistory`, `getDraftCommentValue` (add `ctx` parameter to the latter)
   - `setBibIdRequirement` (add `ctx` parameter)
   - `renderEditLeapBibLink` (add `ctx` parameter; replace `innerHTML` with DOM construction)
   - `renderExternalSearchButton`, `renderEditMetadata` (add `ctx` parameter)
   - `renderPurchaseReminderOption` (add `ctx` parameter)
   - `renderEditCustomFieldsForCurrentFormat` (add `ctx` parameter)
5. Wire re-exports in `modals.js`. Wrap all context-dependent functions.
6. Update source-inspection tests:
   - `security_url_validation.test.js` → point at `modals/edit-form.js`
   - `polaris_grid_search_ui.test.js` → point at `modals/polaris-search.js` + `modals/edit-form.js`
7. `npm test`.

### Phase 5: Edit submit and shared action submitter

1. Extract shared `submitTitleRequestAction(id, payload, ctx, onRefresh)` — centrally handles `authorizedJson` call, error parsing, toast, and refresh. Located in `modals/edit-submit.js`.
2. Create `submitEditForm(ctx, onRefresh)` in `modals/edit-submit.js` — validates form, builds edit payload, delegates to `submitTitleRequestAction`.
3. Replace `performImmediateStaffAction` in `modals/polaris-search.js` to use `submitTitleRequestAction`.
4. Wire `modals.js` to use `submitEditForm` in the edit form submit event (event wiring happens in Phase 6).
5. Update `staff_modal_duplicate_error.test.js` to point at `modals/edit-submit.js`.
6. `npm test`.

### Phase 6: Events coordinator

1. Create `modals/events.js` → `initModalEvents(ctx, { onRefresh })` registers all event listeners:
   - Polaris search close buttons (2 calls)
   - Edit/new Polaris search trigger buttons (6 calls)
   - `#edit-form` submit → `submitEditForm(ctx, onRefresh)`
   - `#edit-format`, `#edit-publication`, `#edit-autohold` change → audit refresh
   - `#edit-bibid` input → audit refresh
   - `window.asap-bib-verified` → `reactiveCleanupWorkflowFlags` + audit refresh
   - `.js-open-profile-dialog` click → `openProfileDialog()`
2. Guard against double-binding: use a module-level `eventsBound` flag.
3. `modals.js` barrel calls `initModalEvents(ctx, { onRefresh: refreshCurrentStaffView })` once at module load.
4. Remove all scattered top-level `addEventListener` calls from `modals.js`.
5. `npm test`.

### Phase 7: Barrel cleanup and final validation

1. `modals.js` is now the thin barrel: creates context, exports wrappers, initializes events.
2. Verify zero consumer import changes needed by diffing against Phase 0's consumer list.
3. Verify all 24 exports are still present from `modals.js`.
4. Run `frontend_request_architecture.test.js` — update assertions that reference `modals.js` source to point at `modals/polaris-search.js` and `modals/edit-submit.js`.
5. Run circular dependency check manually.
6. Run `npm test`.
7. Commit.

## Risks and Mitigations

| Risk | Mitigation |
|---|---|
| `openEdit` calls 15+ sub-renderers in sequence — changing any signature breaks the chain | Keep same function signatures initially; `ctx` is the only new parameter added uniformly |
| Polaris search references edit form DOM (e.g., `edit-next-status`) | Context object with live getters abstracts the DOM; Polaris module uses `ctx.nextStatus.value` instead of `document.getElementById(...)` |
| Source-inspection tests extract functions by name from `modals.js` source | Each phase updates test file paths to point at the owning module. Do not keep duplicate function bodies in `modals.js` just to satisfy old tests. |
| `holdingsLookupUnavailable` loses module-level state | Moved to `ctx.holdingsLookupUnavailable` as a mutable property on the context |
| Barrel re-exports miss an exported symbol | Phase 0 baseline inventory; Phase 7 diff verification |
| Circular imports via barrel | No `modals/*` module imports from `modals.js`. Forbidden imports enforced by manual review at Phase 7. |
| Double-bound event listeners after module hot-reload | `eventsBound` flag in `initModalEvents`; called once from barrel |
| `renderEditLeapBibLink` innerHTML replacement changes rendered output | DOM construction produces identical HTML; verify with `security_url_validation.test.js` |
| `submitTitleRequestAction` shared path diverges between callers | Single implementation in `modals/edit-submit.js`; both edit submit and Polaris immediate action call the same function |

## Success Criteria

- `npm test` passes after every phase
- `modals.js` is under 70 lines (barrel + init call only)
- Zero consumer files need import path changes
- `modals.js` exports the same 24 public names as before the refactor
- All 3 raw `fetch` calls replaced with `authorizedJson`
- Edit form submit and `performImmediateStaffAction` share a single `submitTitleRequestAction` function
- `renderEditLeapBibLink` uses DOM construction, not `innerHTML`
- All top-level event listeners are gated behind `initModalEvents(ctx, { onRefresh })`
- `frontend_request_architecture.test.js` assertions about modals.js point at the correct new module files and still pass
- No circular dependencies among `modals/*` modules
- `holdingsLookupUnavailable` is no longer a rogue module-level flag
