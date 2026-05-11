# Debug Session: Format Display Names Persistence

## Symptom
Adding a new format at the library level appears to save, but is lost upon refreshing the page.

**When:** Occurs when adding a new format in the "Format display names" section of the "Patron experience" settings while a specific library is selected.
**Expected:** The new format should be persisted in the library-level settings and survive a page refresh.
**Actual:** The new format disappears after a refresh.

## Evidence
- Screenshot shows the "Format display names" section with a "New format key" and "Patron-facing label" input.
- User states it "saves" (presumably the UI indicates success) but is gone after refresh.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | The save logic does not correctly include new formats in the payload for library-level overrides. | 70% | UNTESTED |
| 2 | The load logic does not correctly merge library-level format overrides with system defaults. | 60% | UNTESTED |
| 3 | The backend does not support saving custom formats at the library level (schema issue). | 30% | UNTESTED |
| 4 | The UI state for "new formats" is not being properly captured during the save operation. | 50% | UNTESTED |
