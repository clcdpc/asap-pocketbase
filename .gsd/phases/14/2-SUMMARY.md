# Plan 14.2 Summary: Add Status Toggle to Additional Copy Tab

## Accomplishments
- Added `#additional-copy-status-filter` select element to the staff dashboard filter bar in `index.html`.
- Exported `additionalCopyStatusFilterSelect` and `currentAdditionalCopyStatus` from `state.js`.
- Bound a `change` listener to the status filter in `api.js` to refresh the tab when changed.
- Updated `loadTab` in `grid.js` to fetch both open and closed additional copies, and render the one matching the current filter state.
- Updated `renderAdditionalCopiesGrid` and `hideTagFilter` to manage the visibility of the new status filter.

## Verification Results
- The status filter appears on the Additional Copies tab.
- Selecting "Closed copies" refreshes the grid with closed additional copy requests.
- Tab counts are correctly updated for both open and closed statuses.
