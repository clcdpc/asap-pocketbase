# SPEC: Refine Auto-Claims Scoping and Staff Management

## Goal
Remove auto-claim configuration exposure from the general staff access interface, restrict auto-claim rule settings to library-scoped patron experience configurations, and implement regression testing for persistence scope.

## Status: FINALIZED

## Requirements
1. **Fix Missing Export**:
   - Implement and export `handleLibraryContextSwitch` in `settings.js` to resolve the `SyntaxError` in `api.js`.

2. **Staff Access List Refinement**:
   - Remove "Auto-claims" column from the staff grid.
   - Remove checkbox rendering and click-to-save logic for auto-claims in the staff list.

3. **Patron Experience Auto-Claim Scoping**:
   - Hide the "Auto-claim staff" column/selects in the "Material Formats" table *only* when the "System Defaults" context is active.
   - Filter the staff options in the auto-claim dropdowns to only show staff members whose library affiliation matches the current active library context.
   - Enforce that only one auto-claimant can be assigned per format type (handled by the table structure).

4. **Persistence Hardening**:
   - Update the settings save payload to ensure `formatClaimRules` are strictly library-scoped and cannot be saved under a global system context.
   - Ensure that `formatClaimRules` are only sent to the API when a library orgId is present and active (except for empty arrays in system saves).

5. **Regression Testing**:
   - Add tests to verify that `formatClaimRules` cannot be persisted under the system scope.
   - Verify that library-level overrides for auto-claims work correctly and only show relevant staff.
