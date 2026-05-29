# Recent Suggestions Tracker Sync Design

## Overview
Keep the upper-right “recent suggestions” tracker in sync when staff edit a suggestion. Currently, the tracker uses a stale copy from `localStorage`.

## Architecture
- **Helper Function:** Create `updateRecentSuggestion(row, options = {})` in `pb_public/staff/js/recent-suggestions.js`.
  - Searches for existing record by `id` in `localStorage`.
  - If found, updates display fields (`title`, `author`, `status`, `type`).
  - Optionally "bumps" to top of list if `options.bump` is true.
- **Integration:**
  - Call `updateRecentSuggestion(updatedRecord)` in `pb_public/staff/js/modals.js` after successful edit-form submissions.
  - Call `updateRecentSuggestion(updatedRecord)` in `performImmediateStaffAction()` after Polaris grid actions modify a record.
  - Call `renderRecentSuggestionsSwitcher()` after calling the update helper to refresh the dropdown UI.
- **Data Integrity:** Only update existing records; do not incorrectly add unremembered records.

## Testing
- Unit tests in `tests/recent_suggestions_ui.test.js`:
  - Verify existing record in `localStorage` gets updated display fields.
  - Verify `renderRecentSuggestionsSwitcher()` reflects the updated fields in the UI.
  - Verify updating a non-recent record does not create a new entry in the list.
