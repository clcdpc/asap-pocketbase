# Phase 1: Include library name in participation warning message

**Objective:** Update the hardcoded "Your library does not currently participate..." message to support a `{{library}}` placeholder and expose it for staff editing.

---

### Plan 01-01: Backend Implementation & Configuration
**Wave 1 | Autonomous: True**

**Task 1: Update Default Configuration**
- **File:** `lib/config.js`
- **Action:** Update the default value for `systemNotEnabledMessage` in `uiTextFromRecord` to: `"{{library}} does not currently participate in this suggestion service."`.
- **Verify:** `grep` check of the file content.

**Task 2: Implement Placeholder Replacement**
- **File:** `lib/patron_routes.js`
- **Action:** In the `patronLogin` function, locate the participation check. Replace the message resolution with logic that performs the `{{library}}` replacement using `patron.LibraryOrgName`. Include a fallback to "Your library" if the name is missing and a backward compatibility check to replace the old "Your library" string if the new placeholder isn't present in a custom message.
- **Verify:** Run `node tests/patron_login.test.js` (requires updating the test to mock `LibraryOrgName` and check the returned message).

**Task 3: Update Initial Migration**
- **File:** `pb_migrations/0000000000_initial.js`
- **Action:** Update the seed record for `ui_settings` (`uisettings00010`) to use the new placeholder string.

---

### Plan 01-02: Staff UI Exposure
**Wave 2 | Autonomous: True**

**Task 1: Add Field to Staff UI**
- **File:** `pb_public/staff/index.html`
- **Action:** Add a new form group for "Participation warning message" under the Patron Experience section. Use ID `ui-system-not-enabled-msg`.
- **Verify:** Open the settings tab in the browser and verify the field appears.

**Task 2: Wire Field to Settings Logic**
- **File:** `pb_public/staff/js/settings.js`
- **Action:** 
  1. Update `populatePatronUiForms` to load `systemNotEnabledMessage` into the new field.
  2. Update `_serializeSettingsState` to include `systemNotEnabledMessage` in the `uiText` object sent to the server.
- **Verify:** Save a custom message in the Staff UI and verify it persists in the database and is served to the patron portal.

---

### Success Criteria
- [ ] Patrons from non-participating libraries see their library name in the error message.
- [ ] Staff can customize this message per-library via the Settings > Patron Experience tab.
- [ ] Existing installations without the placeholder still display a coherent message (backward compatibility).
