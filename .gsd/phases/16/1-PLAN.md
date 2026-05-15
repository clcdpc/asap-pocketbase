---
phase: 16
plan: 1
wave: 1
---

# Plan 16.1: Add Auto-Close Setting to Workflow Settings UI

## Objective
Add a new settings section in the Workflow tab to allow libraries to configure automatic closing of stale additional copy requests.

## Context
- pb_public/staff/index.html
- pb_public/staff/js/settings.js (to be verified)

## Tasks

<task type="auto">
  <name>Add settings fields to index.html</name>
  <files>pb_public/staff/index.html</files>
  <action>
    Add a new settings group for "Additional copies" in the Workflow tab.
    - Locate the end of the "Hold placed" automation section (around line 646).
    - Add a header "Additional copy requests".
    - Add a checkbox `#additional-copy-timeout-enabled` and a number input `#additional-copy-timeout-days`.
    - Follow the same pattern as `hold-pickup-timeout-enabled`.
  </action>
  <verify>grep "additional-copy-timeout-enabled" pb_public/staff/index.html</verify>
  <done>UI fields for additional copy auto-close are present in the Workflow settings panel.</done>
</task>

<task type="auto">
  <name>Bind UI fields in settings logic</name>
  <files>pb_public/staff/js/settings.js</files>
  <action>
    Update the settings population and saving logic.
    - Add `additional-copy-timeout-enabled` and `additional-copy-timeout-days` to the list of fields to populate.
    - Add a change listener to the checkbox to show/hide the number input group (consistent with other timeout settings).
    - Ensure the fields are included in the `getSettingsPayload()` or equivalent function.
  </action>
  <verify>grep "additional-copy-timeout-enabled" pb_public/staff/js/settings.js</verify>
  <done>The new settings fields are functional in the settings form.</done>
</task>

## Success Criteria
- [ ] A new "Additional copy requests" section appears in Settings > Workflow.
- [ ] Toggling the checkbox shows/hides the days input field.
- [ ] Saving settings persists the values (verified by Plan 16.2).
