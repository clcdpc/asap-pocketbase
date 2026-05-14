# Plan 7.2 Summary: Patron Experience Auto-Claim Scoping & Filtering

## Changes
- **Auto-claim Scoping**:
    - Updated `renderFormatSettings` in `settings-formats.js` to conditionally render the "Auto-claim staff" column only when a library context is active (`currentLibraryContextOrgId !== 'system'`).
    - Added an informative message to the help text explaining that auto-claims are library-only.
- **Staff Filtering**:
    - Implemented filtering for the auto-claimant dropdowns. They now only display staff members whose `libraryOrgId` matches the current library context.
    - Imported `currentLibraryContextOrgId` from `state.js` into `settings-formats.js` to enable this logic.

## Verification Results
- In "System Defaults" context, the "Auto-claim staff" column is hidden.
- In a specific Library context, the "Auto-claim staff" column is visible.
- The staff dropdowns in the format settings only list staff from the currently selected library.
