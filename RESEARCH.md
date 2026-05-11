# Research: Patron Library Participation Message

## Findings

1.  **Message Location**: The message "Your library does not currently participate in this suggestion service." is hardcoded as a default in:
    *   `lib/config.js` (`uiTextFromRecord` function)
    *   `lib/patron_routes.js` (`patronLogin` function)
    *   `pb_migrations/0000000000_initial.js` (initial DB record)

2.  **Patron Context**: In `lib/patron_routes.js`, the `patron` object after `orgs.attachPatronScope` contains:
    *   `LibraryOrgID`: The ID of the parent library (OrganizationCodeID 2).
    *   `LibraryOrgName`: The display name of the parent library.
    *   `PatronOrgID`: The ID of the patron's registered branch.

3.  **Mapping Logic**: `orgs.resolveParentLibrary` (called by `attachPatronScope`) performs the mapping from branch to parent library by traversing the organization hierarchy in `polaris_organizations` until it finds a record with `organizationCodeId = "2"`.

4.  **Placeholders**: The project already uses `{{placeholder}}` syntax for other messages (e.g., `{{next_available_date}}`, `{{duplicate_date}}`).

5.  **UI Gaps**: The `systemNotEnabledMessage` is currently not exposed in the Staff UI for editing, although it exists in the `ui_settings` collection schema.

## Proposed Solution

1.  Update `lib/config.js` and `lib/patron_routes.js` to support a `{{library}}` placeholder in the message.
2.  Update the default message to: `"{{library}} does not currently participate in this suggestion service."`
3.  Add a field to the Staff UI (`pb_public/staff/index.html` and `pb_public/staff/js/settings.js`) to allow staff to edit this message.
4.  In `lib/patron_routes.js`, perform the replacement: `msg.replace(/{{library}}/g, patron.LibraryOrgName || "Your library")`.
5.  Add a fallback for "Your library" to support existing hardcoded defaults if they haven't been updated to the placeholder.
