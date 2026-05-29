# Move "Allow patron autohold opt-out" setting Design

## Overview
Move the "Allow patron autohold opt-out" checkbox from the "Workflow" section to the "Patron Experience" section on the staff settings page for better UX.

## Architecture
- **DOM Move:** Relocate the `allow-patron-autohold-opt-out` checkbox and its corresponding label in `pb_public/staff/index.html` from the workflow section to the patron experience section.
- **JavaScript Compatibility:**
  - The existing logic in `pb_public/staff/js/settings.js` relies on `document.getElementById('allow-patron-autohold-opt-out')`.
  - No changes are required to `settings.js` or `settings_save.js` because they target the element by ID, which will remain constant.
- **Verification:**
  - Ensure the setting correctly populates, toggles, and saves after the move.

## Testing
- Verify that `pb_public/staff/index.html` renders the checkbox in the new location.
- Verify that `settings.js` continues to populate the checkbox with the correct value on load.
- Verify that changing the checkbox and saving works as expected.
