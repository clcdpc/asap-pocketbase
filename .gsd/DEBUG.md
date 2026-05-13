# Debug Session: Library messaging not loading via libraryOrgId parameter

## Symptom
When navigating to `http://127.0.0.1:8090/patron/?libraryOrgId=2`, the patron portal does not show the messaging configured for library ID 2.

**When:** Navigating to the patron portal with a `libraryOrgId` query parameter.
**Expected:** The portal should load and display the UI configuration (labels, messages, logo) specific to that library.
**Actual:** The portal appears to be showing system defaults or possibly an incorrect library's configuration.

## Evidence
- User reported: `http://127.0.0.1:8090/patron/?libraryOrgId=2` doesn't show library 2 messaging.
- Screen shot shows library Alexandria (ID 2) has custom settings saved.
- I need to verify how `libraryOrgId` is handled in `pb_public/patron/js/config.js` and the backend.

## Hypotheses

| # | Hypothesis | Likelihood | Status |
|---|------------|------------|--------|
| 1 | The `libraryOrgId` query parameter is not correctly parsed or passed to the config loading API. | 40% | UNTESTED |
| 2 | The backend API `/api/asap/patron/config` does not correctly resolve the library-specific settings based on `libraryOrgId`. | 40% | UNTESTED |
| 3 | The frontend is loading the config but not applying it correctly to the UI. | 20% | UNTESTED |
