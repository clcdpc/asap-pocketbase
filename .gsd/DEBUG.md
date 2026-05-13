# Debug Session: staffUsersList 400 Bad Request

## Symptom
`GET /api/asap/staff/users?orgId=8` and `GET /api/asap/staff/users?orgId=system` return 400 Bad Request.

**When:** When loading the Staff Access settings tab or switching library context.
**Expected:** Should return a list of staff users.
**Actual:** Returns 400 Bad Request.

## Evidence
- Browser console shows 400 error.
- Screenshot shows "Something went wrong while processing your request."
- Try-catch added to `staffUsersList` should have returned a JSON error message, but the user's screenshot still shows "Something went wrong...", which is the default `authorizedJson` error message when the response body is not valid JSON or doesn't have a message.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | Runtime error in `staffUsersList` or its dependencies (records.js, route_utils.js) | 80% | UNTESTED |
| 2 | Syntax error in `lib/staff_routes.js` preventing it from being required correctly | 10% | UNTESTED |
| 3 | PocketBase version incompatibility with used functions (e.g. e.request.queryParam) | 10% | UNTESTED |
