# ASAP Roadmap

## Milestone 1: Refine Patron Format Settings
**Status**: ✅ Complete

---

### Phase 1: Move eContent Messages to Format Rules
**Status**: ✅ Complete
**Objective**: Move eBook and eAudiobook message editors into the accordion for those formats and ensure they always display as messages.
**Depends on**: None

**Tasks**:
- [x] Update `lib/format_rules.js` to include `message` field in rules and update default rules.
- [x] Update `pb_public/staff/index.html` to remove the separate message textareas.
- [x] Update `pb_public/staff/js/settings-ui.js` to render message editors inside the format rules accordion.
- [x] Update `pb_public/staff/js/settings.js` to correctly serialize and populate the new format-specific messages.
- [x] Update `pb_public/patron/app.js` to use the messages from the format rules.
- [x] Polish UI labels to remove "(legacy)" terminology.

**Verification**:
- [x] Staff can edit eBook/eAudiobook messages inside the accordion.
- [x] eBook/eAudiobook formats in the patron portal display the configured messages.
- [x] Field settings are hidden for eBook/eAudiobook formats in the staff UI.
- [x] Custom format messages display correctly in the patron portal.
- [x] Custom format messages save properly per library 

---

### Phase 2: Clarify BIB ID Role in Workflow Descriptions
**Status**: ✅ Complete
**Objective**: Update UI text to explicitly state that a BIB ID is needed to move a suggestion to the Pending hold phase.
**Depends on**: Phase 1

**Tasks**:
- [x] Update descriptions in `pb_public/staff/js/state.js`.
- [x] Update hardcoded description in `pb_public/staff/js/grid.js`.
- [x] Update BIB ID hint text in `pb_public/staff/js/modals.js`.

**Verification**:
- [x] Staff UI displays updated "Pending purchase" description.
- [x] Edit modal displays updated BIB ID hint when "Pending hold" is selected.

---

### Phase 3: Guard System-Only Settings Sections Under Library Context
**Status**: ✅ Complete
**Objective**: When a super admin has a library selected in the settings context selector, system-only sections (Getting Started, Polaris, SMTP) should clearly show they are read-only / system-level and cannot be edited until the user switches back to "System Defaults." Library-context saves must never blank out system-level Getting Started, Polaris, or SMTP data.
**Depends on**: Phase 2

**Tasks**:
- [x] Add a `systemOnlySections` list in `api.js` (or `settings.js`) containing `['start', 'polaris', 'smtp']` and use it alongside the existing `overridableSections` check.
- [x] In `activateSettingsSection`, when the current library context is non-system and the target section is system-only, show a prominent "System-only" overlay or banner explaining the user must switch to System Defaults to edit, and disable all inputs/buttons inside that section panel.
- [x] When the user switches back to system context or navigates to an overridable section, re-enable the inputs.
- [x] Audit `_serializeSettingsState()` in `settings.js` to confirm SMTP, Polaris, Getting Started fields (staffUrl, leapBibUrlPattern, enabledLibraryOrgIds, systemNotEnabledMessage, misconfiguredMessage) are never sent to the save endpoint when `currentLibraryContextOrgId !== 'system'`.
- [x] Audit the `saveSettings()` payload assembly to confirm the `libraryPayload.smtp` and `libraryPayload.polaris` keys are excluded (or null) when not in system context, preventing accidental overwrite.
- [x] Add CSS for the system-only overlay/disabled state, including reduced opacity and pointer-events: none on form controls.
- [x] Add a test or manual verification checklist confirming that saving at library level does not alter SMTP, Polaris, or Getting Started values on the server.

**Verification**:
- [x] Navigating to SMTP while a library is selected shows a clear "switch to System Defaults to edit" message and all fields are disabled.
- [x] Navigating to Polaris while a library is selected shows the same system-only guard.
- [x] Navigating to Getting Started while a library is selected shows the same system-only guard.
- [x] Saving settings while in library context does not send SMTP, Polaris, or Getting Started data.
- [x] Switching back to System Defaults re-enables editing on all system-only sections.
- [x] Library-context saves preserve the correct system-level values in the database (no blanking).

---

### Phase 4: Refine Staff Access Context Guard and Filtering
**Status**: ✅ Complete
**Objective**: Allow library admins to manage their own staff by removing the "Staff Access" section from the system-only guard. Ensure that when a Super Admin views the staff list in a library context, they only see staff for that library, matching the behavior of library admins.
**Depends on**: Phase 3

**Tasks**:
- [x] Remove `'staff'` from `systemOnlySections` in `api.js`.
- [x] Update `loadStaffUsers()` in `settings-users.js` to pass `?orgId=${currentLibraryContextOrgId}` to the `/api/asap/staff/users` endpoint.
- [x] Update `staffUsersList(e)` in `lib/staff_routes.js` to respect the `orgId` query parameter for Super Admins. If `orgId` is provided and is not `'system'`, filter by that library.
- [x] Update the "Add Staff Member" form in `settings-users.js` to pre-select the current library context (if not `'system'`) and disable the dropdown for Super Admins when in a library context to prevent cross-library mistakes.

**Verification**:
- [x] Library admin can access the "Staff Access" section without a warning banner or disabled inputs.
- [x] Super admin in "System Defaults" context sees ALL staff users.
- [x] Super admin in a library context sees ONLY staff users for that library.
- [x] Adding a staff member while in a library context defaults to that library.
