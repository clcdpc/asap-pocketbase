# SPEC: Clarify BIB ID Role in Workflow Descriptions

## Goal
Improve staff clarity by explicitly stating that a BIB ID is the mechanism for moving a suggestion from the "Pending purchase" stage to the "Pending hold" stage.

## Status: FINALIZED

## Requirements
1. Update the description for the "Pending purchase" tab in the staff interface.
2. Update the helper text (hint) for the BIB ID field in the edit modal to mention the "Pending hold" phase.
3. Ensure consistency across `state.js` (data source) and `grid.js` (UI implementation).

## Design
- **Current text (Pending purchase):** "Pending purchase contains approved suggestions that are waiting to appear in Polaris. Staff can also add a BIB ID manually."
- **Proposed text (Pending purchase):** "Pending purchase contains approved suggestions that are waiting to appear in Polaris. Staff can add a BIB ID to move a suggestion to the Pending hold phase."
- **Current text (BIB ID hint):** "Required to identify the item in the catalog and proceed with the request."
- **Proposed text (BIB ID hint):** "Required before moving this suggestion to the Pending hold phase."
