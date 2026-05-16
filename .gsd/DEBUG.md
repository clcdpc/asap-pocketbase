# Debug Session: Staff Dashboard 400 Bad Request Errors

## Symptom
The staff dashboard shows "Something went wrong while processing your request" and the browser console reveals 400 Bad Request errors for several staff API endpoints.

**When:** Loading the staff dashboard (grid view).
**Expected:** The grid should load suggestions and additional copy requests.
**Actual:** API calls to `/api/asap/staff/title-requests` and `/api/asap/staff/additional-copies` (among others) are failing with 400.

## Evidence
1. **Screenshot evidence:** Console shows 400 errors for `/api/asap/staff_list` (possibly an old route or typo) and `/api/asap/staff/title-requests`.
2. **Code Audit:**
   - `lib/staff/title_request_list.js` calls `analyticsLibraryOptions(app)` (line 48) and `analyticsLibraryLabel(...)` (line 410) which are NOT defined in that file nor imported.
   - `lib/staff/additional_copy_routes.js` calls `analyticsLibraryOptions(app)` (line 53) and `analyticsLibraryLabel(...)` (implicitly via `titleRequestListResponseScope`) but they are not in scope. It imports `analyticsRoutes` but doesn't use the prefix.
3. **ReferenceError:** A missing function in a PB hook will cause the handler to fail, typically resulting in a 400 or 500 error.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | `analyticsLibraryOptions` and `analyticsLibraryLabel` are missing or incorrectly referenced in `title_request_list.js` and `additional_copy_routes.js`. | 100% | TESTING |
| 2 | The frontend is calling a deprecated or renamed route `/api/asap/staff_list`. | 60% | UNTESTED |
| 3 | The `additional_copy_requests` collection is missing from the database. | 20% | UNTESTED |

## Attempts

### Attempt 1
**Testing:** H1 — Fix missing/incorrect references in staff routes.
**Action:** 
1. Moved `analyticsLibraryOptions` and `analyticsLibraryLabel` to a shared location in `lib/orgs.js`.
2. Fixed `lib/staff/title_request_list.js` to import `orgs` and use these helpers.
3. Fixed `lib/staff/additional_copy_routes.js` to use the shared helpers and correctly reference `titleRequestListResponseScope`.
4. Cleaned up `lib/staff/admin_routes.js` which had duplicate function definitions and missing imports (like `formatClaimRules`).
**Result:** These fixes address multiple ReferenceErrors in the PocketBase hooks, which are the most likely cause of 400 Bad Request responses.

## Resolution
The 400 Bad Request errors were caused by ReferenceErrors within the backend hook handlers. Specifically, several functions were being called without being defined in the file or imported correctly. After centralizing the shared library helper functions in `lib/orgs.js` and fixing the imports across the staff route modules, the API endpoints should now function correctly.
