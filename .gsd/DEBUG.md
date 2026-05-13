# Debug Session: Suggestion Submission Failure (Unique Email)

## Symptom
When submitting a new suggestion for patron `PACREG2473720`, the request fails with a 400 error: `validation_not_unique` on the `email` field.

**When:** Submitting a new suggestion via the staff "New suggestion" modal.
**Expected:** Suggestion is created successfully.
**Actual:** Error: `{"data": {"email": {"code": "validation_not_unique", "message": "Value must be unique."}}}`

## Evidence
- Payload includes `barcode`, `title`, etc.
- Error explicitly points to a uniqueness constraint violation on the `email` field.
- Patron: `PACREG2473720` (Wes Osborn-DEL, cwosborn@gmail.com)
- Database check confirms another record (`21868001586580`) already has the email `cwosborn@gmail.com`.
- `patron_users` is an Auth collection, which enforces unique emails in PocketBase.

## Hypotheses
| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | `patron_users` (Auth collection) requires unique emails, and the patron's email is already in use by another barcode. | 100% | CONFIRMED |
| 2 | Multiple suggestions are being submitted with the same email in the `title_requests` table. | 0% | ELIMINATED (no unique constraint on `title_requests.email`) |

## Attempts

### Attempt 1
**Testing:** H1 — Email uniqueness in `patron_users`.
**Action:** Checked database and Polaris data.
**Result:** Confirmed that `PACREG2473720` has an email already owned by `21868001586580` in the local DB.
**Conclusion:** CONFIRMED. The system cannot currently handle shared emails because of the PB Auth collection constraint.

## Resolution Plan
1. Add a non-unique `notificationEmail` field to `patron_users`.
2. Use a unique fake email (`barcode@patron.asap.local`) for the PB Auth record to bypass uniqueness constraints.
3. Update suggestion creation to use the real email from Polaris or `notificationEmail`.
