# Debug Session: Timing Column Shows Numbers

## Symptom
The "Timing" column in the staff grid (specifically the "Hold placed" tab) displays numeric years (e.g., 2019, 2023) instead of the expected publication timing labels (e.g., "Already published").

**When:** Viewing the "Hold placed" tab in the staff grid.
**Expected:** Labels like "Already published", "Coming soon", etc.
**Actual:** Numbers like 2019, 2023, 2018, 2012, 1888, 2008.

## Evidence
- Screenshot shows numeric values in the Timing column.
- These look like publication years from the Polaris catalog.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | `formatPublication` in `grid.js` is returning the raw value if it doesn't match a known option, and Polaris search is overwriting the suggestion's `publication` field with the catalog year. | 80% | UNTESTED |
| 2 | The grid column for "Timing" is using the wrong data field (e.g., `pubYear` instead of `publication`). | 10% | UNTESTED |
| 3 | The labels in `state.js` or the mapping logic have a bug where they don't recognize standard strings. | 10% | UNTESTED |
