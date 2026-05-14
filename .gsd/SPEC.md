# SPEC: Consolidate Polaris Integration and UI Enhancements

## Goal
Retroactively document and harden the recent Polaris integration improvements, expand the audit trail descriptions for staff actions, and ensure the UI maintains a "premium" aesthetic through reactive flag cleanup and layout optimization.

## Status: FINALIZED

## Requirements
1. **Audit Trail Expansion**:
   - Update `buildPendingAuditPreview` in `pb_public/staff/js/modals.js` to support the following action strings:
     - `alreadyOwn`: "This request will be marked Already own and move directly to Closed."
     - `silentClose`: "This request will be closed silently and move directly to Closed."
     - `reassign`: "This request will be reassigned to the selected format."
   - Ensure the preview updates dynamically when these actions are selected or the modal state changes.

2. **Reactive Flag Cleanup**:
   - In `pb_public/staff/js/modals.js`, when a BIB ID is successfully verified or selected from Polaris search results, trigger a cleanup of the row's workflow flags.
   - Specifically, remove "Hold failed" and "No holdable items" flags if they exist, as the new BIB ID represents a fresh state.

3. **UI Polish & "WOW" Factor**:
   - Perform a visual audit of the Staff Grid and Edit Modal.
   - Resolve horizontal crowding in the grid (especially when many flags are present).
   - Ensure "Queue Hold" buttons and other primary actions use consistent, high-contrast brand colors.
   - Optimize micro-animations for modal transitions and flag updates.

4. **Consistency Audit**:
   - Verify that all recent reactive fixes (e.g., "Queue Hold" terminology) are applied consistently across all workflow tabs and tooltips.
