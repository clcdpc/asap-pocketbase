---
status: resolved
trigger: "i am seeing weird things happen when i go to a library setting and look at public timing options. this shows up sometimes in the suggestion form too. can you fix and add coding instructions and / or tests to prevent it from happening again?"
created: 2026-05-11T20:07:29Z
updated: 2026-05-11T20:31:00Z
---

# Debug Session: library-settings-suggestion-form

## Symptoms

- Expected behavior: Library settings should show values for the selected library only, with scoped fallback behavior only where explicitly designed. Public timing controls should not leak values across system/library selections, and the public suggestion form should receive the correct effective settings for its selected library.
- Actual behavior: User reports intermittent weird behavior when viewing library settings for public timing options, sometimes also visible in the suggestion form.
- Error messages: None reported.
- Timeline: Observed after recent settings and suggestion-form work.
- Reproduction: Navigate to a library settings view, inspect public timing options, and compare with what appears in the suggestion form.

## Current Focus

- hypothesis: Public timing settings have mismatched load, form population, save, or runtime read scope behavior, causing stale or cross-scope values.
- test: Inspect settings loaders, UI form population, serialization, and public config/suggestion-form readers; add regression coverage for library isolation.
- expecting: Find a scope mismatch or stale UI state path and confirm with focused tests.
- next_action: gather initial evidence

## Evidence

- timestamp: 2026-05-11T20:24:00Z
  observation: `config.uiText(app, orgId)` calls `uiTextFromRecord(app, uiRecord(app, orgId), orgId)`, so when a library `ui_settings` row exists, that library row is passed into the UI text resolver.
  source: lib/config.js
- timestamp: 2026-05-11T20:25:00Z
  observation: Before the fix, `uiTextFromRecord` derived global publication options from the passed `record`. For a library row created for branding/text only, this field can be empty, causing fallback to hard-coded defaults instead of configured system patron options.
  source: lib/config.js
- timestamp: 2026-05-11T20:26:00Z
  observation: Library saves store publication timing customizations in `patron_settings_overrides`, while system saves store defaults in `ui_settings`. Runtime resolution therefore must read system defaults from the system row and library overrides from `patron_settings_overrides`.
  source: lib/staff_routes.js
- timestamp: 2026-05-11T20:31:00Z
  observation: Added regression coverage that a library with its own `ui_settings` row but no patron option override still inherits system publication timing, and that explicit `patron_settings_overrides` still win.
  source: tests/config_ui_text_patron_options_scope.test.js
- timestamp: 2026-05-11T20:37:00Z
  observation: New-suggestion option selects preserved invalid values from a previous settings context. Edit selects may preserve historical values, but new suggestion fields should reset to the selected library's valid options.
  source: pb_public/staff/js/settings-ui.js

## Eliminated

- Library selector request races are partially guarded by `libraryContextLoadSerial`, and the issue is reproducible from the server resolver without UI timing.

## Resolution

- root_cause: `uiTextFromRecord` used a library `ui_settings` record as the source of global publication timing defaults whenever that library had any UI row, so unrelated library branding/text overrides could make patron option lists fall back to hard-coded defaults or stale scoped data.
- fix: Resolve publication timing defaults from the system `ui_settings` record, then apply `patron_settings_overrides` for library-specific option overrides. Also reset new-suggestion selects to the current library's valid options while still allowing edit flows to display historical values.
- verification: `node tests/config_ui_text_patron_options_scope.test.js`; `node tests/staff_public_option_selects.test.js`; `node tests/config_scopedRows.test.js`
- files_changed: `lib/config.js`; `pb_public/staff/js/settings-ui.js`; `pb_public/patron/app.js`; `tests/config_ui_text_patron_options_scope.test.js`; `tests/staff_public_option_selects.test.js`; `AGENTS.md`; `.planning/debug/library-settings-suggestion-form.md`
