# Duplicate Request Close Flow Design

## Overview
When a staff action receives a `409 duplicate_open_request` error, offer an option to close the current request silently as a duplicate instead of just showing an error alert.

## Architecture
- **Helper Function:** Create `confirmDuplicateOpenRequestClose(err, id)` in `pb_public/staff/js/modals.js`.
  - Displays a confirmation dialog:
    - **Title:** “Duplicate request found”
    - **Body:** “This patron already has an open request or hold for this BIB ID.” (Include details from error payload if available).
    - **Actions:** “Keep editing” (Secondary, closes dialog) / “Close as duplicate” (Primary, triggers close).
- **Integration:** Call this helper within the error catch block for staff action submission if `err.code === 'duplicate_open_request'`.
- **Action:** If confirmed, invoke `closeDuplicateRequest(id)` which already exists in `pb_public/staff/js/actions.js`.
- **Post-Action Behavior:**
  - Close edit modal.
  - Reload tab.
  - Show "Duplicate request closed" toast.

## Testing
- Unit test in `tests/staff_modal_duplicate_error.test.js`:
  - Verify confirmation dialog appears on `duplicate_open_request`.
  - Verify confirming calls `closeDuplicateRequest`.
  - Verify canceling does not close the request and leaves the modal open.
  - Verify generic error handling still works for other errors.
