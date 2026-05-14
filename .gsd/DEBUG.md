# Debug Session: Staff Duplicate Error Message Cut Off

## Symptom
The error message "This patron already has this suggestion." (or other API errors) in the "New suggestion" modal is partially cut off or poorly rendered.

**When:** Submitting a duplicate suggestion on the staff side.
**Expected:** The error message should be fully visible, ideally on its own line above the action buttons.
**Actual:** The error message appears on the same line as the buttons and is clipped because the footer container (`asap-dialog-edit-footer`) does not allow wrapping.

## Evidence
- `pb_public/staff/index.html` uses `.asap-dialog-edit-footer` for the `newSuggestionModal` footer.
- `pb_public/staff/styles.css` defines `.asap-dialog-edit-footer` as `display: flex` but lacks `flex-wrap: wrap`.
- `.asap-dialog-footer-error` has `flex: 1 0 100%`, which intended to take full width but fails without `flex-wrap` on the parent.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | The `asap-dialog-edit-footer` class lacks `flex-wrap: wrap`, causing the full-width error element to collide with buttons. | 95% | CONFIRMED |
| 2 | The error message container has insufficient padding or a fixed height. | 5% | ELIMINATED |

## Attempts

### Attempt 1
**Testing:** H1 — Add `flex-wrap: wrap` and `gap` to `.asap-dialog-edit-footer`.
**Action:** Modified `pb_public/staff/styles.css`.
**Result:** SUCCESS. Adding `flex-wrap: wrap` allows the `flex: 1 0 100%` error summary to take its own line.
**Conclusion:** CONFIRMED.

## Resolution

**Root Cause:** The flex container for the dialog footer was not configured to wrap, preventing the full-width error message from dropping to its own line as intended by its `flex: 1 0 100%` property.
**Fix:** Added `flex-wrap: wrap` and `gap: 12px` to `.asap-dialog-edit-footer`. Also added `overflow-wrap: anywhere` to the error container for defensive robustness.
**Verified:** Verified via code audit of the layout and comparison with the screenshot.
**Regression Check:** Checked other modals using `asap-dialog-edit-footer` (Edit, etc.) to ensure their layout remains stable.
