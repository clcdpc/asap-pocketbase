# Debug Session: Polaris Search Result Format Numbers

## Symptom
In the "Search Polaris" results, the "Format" field sometimes displays a raw number (e.g., "35") instead of a human-readable string (e.g., "Book", "eBook").

**When:** Performing a Polaris search in the staff dashboard and viewing result cards.
**Expected:** Format should be a human-readable name.
**Actual:** Format is sometimes a number.

## Reference
- Polaris API Documentation: [MARCTypeOfMaterialsGet](https://documentation.iii.com/polaris/PAPI/current/PAPIService/MARCTypeOfMaterialsGet.htm)
- Note from user: We might need to cache these results as they don't change often.

## Evidence
(To be gathered)

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | The Polaris search API returns FormatID (number) instead of FormatDescription for some record types. | 90% | UNTESTED |
| 2 | The frontend UI logic lacks a mapping table for these FormatIDs. | 90% | UNTESTED |
| 3 | The mapping exists but is incomplete or fails to load. | 30% | UNTESTED |

## Attempts

## Resolution
