# Move "Popular creator notifications" setting Design

## Overview
Move the "Popular creator notifications" section from the "Workflow" section to the "Patron Experience" section on the staff settings page for better UX.

## Architecture
- **DOM Move:** Relocate the entire section for "Popular creator notifications" in `pb_public/staff/index.html` (lines 687-720) to the "Patron Experience" section (`#patron-experience-accordion`).
- **JavaScript Compatibility:**
  - The existing logic in `pb_public/staff/js/settings.js` targets elements by ID (`wf-common-authors-enabled`, `wf-common-authors-label`, `wf-common-authors-help`, `wf-common-authors-list`, `wf-common-authors-message`).
  - No changes are required to `settings.js` because it targets the elements by ID, which will remain constant.
- **Verification:**
  - Ensure the setting correctly populates, toggles, and saves after the move.

## Testing
- Verify that `pb_public/staff/index.html` renders the section in the new location.
- Verify that `settings.js` continues to populate the section with the correct values on load.
- Verify that changing the settings and saving works as expected.
