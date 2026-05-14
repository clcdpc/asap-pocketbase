# Debug Session: Sticky Search Filter Failure

## Symptom
The search filter in the staff grid was not persisting after a row action.

**When:** After taking an action on a row (e.g., Undo, Claim) that triggers a grid reload.
**Expected:** The search filter keyword should remain in the search box and the grid should remain filtered.
**Actual:** The search filter was lost.

## Evidence
- The project uses an **external** search input `#grid-search-input` and hides the internal `gridjs` search box via CSS.
- Initial attempt incorrectly targeted the internal `gridjs` search box selector `.gridjs-search-input`.
- Existing listener in `api.js` for `#grid-search-input` did not update the application state `gridSearchKeyword`.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | `loadTab` is clearing the keyword incorrectly | 60% | ELIMINATED |
| 2 | `gridjs` is not respecting the `keyword` property on re-render | 20% | ELIMINATED |
| 3 | Incorrect selector for search input | 90% | CONFIRMED |

## Attempts

### Attempt 1
**Testing:** Syncing external search input with state.
**Action:** 
1. Updated `api.js` to import `setGridSearchKeyword` and update the state during the `input` event on `#grid-search-input`.
2. Updated `grid.js` to populate `#grid-search-input.value` from the state during `renderCurrentGrid`.
3. Removed incorrect internal listener in `grid.js`.
**Result:** PASSED. State is now synced between the external UI element and the grid configuration.

## Resolution

**Root Cause:** The application uses a custom external search input that was not synced with the newly introduced `gridSearchKeyword` state. The internal `gridjs` search box was hidden and unusable for state capture.
**Fix:** Unified the external search input listener with the state management and ensured the UI reflects the state on every re-render.
**Verified:** Logic now covers both real-time typing (via `api.js`) and full grid reloads (via `grid.js`).
