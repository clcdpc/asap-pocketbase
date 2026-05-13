# Summary: Plan 2.1 - Update Workflow Descriptions

## Actions Taken
- Updated the `outstanding_purchase` description in `pb_public/staff/js/state.js` to clarify that adding a BIB ID moves the request to the Pending hold phase.
- Updated the hardcoded tab description for "Pending purchase" in `pb_public/staff/js/grid.js` to match the updated wording.
- Updated the BIB ID field hint in `pb_public/staff/js/modals.js` (inside `setBibIdRequirement`) to explicitly state it is required before moving a suggestion to the Pending hold phase.

## Verification Results
- Verified using `grep` that all three files contain the updated text strings.
- [x] state.js: "move a suggestion to the Pending hold phase"
- [x] grid.js: "move a suggestion to the Pending hold phase"
- [x] modals.js: "Required before moving this suggestion to the Pending hold phase"
