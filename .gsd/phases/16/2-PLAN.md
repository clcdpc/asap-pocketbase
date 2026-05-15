---
phase: 16
plan: 2
wave: 1
---

# Plan 16.2: Update Settings Persistence and Defaults

## Objective
Ensure the new auto-close settings are correctly handled by the backend configuration system, including defaults and library-level overrides.

## Context
- lib/config.js

## Tasks

<task type="auto">
  <name>Update config.js with new setting</name>
  <files>lib/config.js</files>
  <action>
    Add `additionalCopyTimeout` to the settings schema.
    - Add a helper function `additionalCopyTimeout(app, libraryOrgId)` to retrieve the setting (enabled/days) with fallback to system defaults.
    - Update `librarySettings` or the workflow defaults object to include these fields.
  </action>
  <verify>grep "function additionalCopyTimeout" lib/config.js</verify>
  <done>Backend configuration logic supports the new setting with correct scoping.</done>
</task>

## Success Criteria
- [ ] `lib/config.js` has the new helper function.
- [ ] System-level and library-level saves persist the new settings.
