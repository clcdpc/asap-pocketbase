---
phase: 7
plan: 3
wave: 1
---

# Plan 7.3: Persistence Hardening & Regression Testing

## Objective
Ensure `formatClaimRules` are strictly library-scoped and add regression tests to verify this behavior.

## Context
- .gsd/SPEC.md
- pb_public/staff/js/settings.js
- tests/library_settings_save_scope.test.js

## Tasks

<task type="auto">
  <name>Harden formatClaimRules Persistence</name>
  <files>
    <file>pb_public/staff/js/settings.js</file>
  </files>
  <action>
    1. Review `saveSettings` in `settings.js`.
    2. Ensure `formatClaimRules` are sent as an empty array `[]` when `currentLibraryContextOrgId === 'system'`.
    3. Ensure `formatClaimRules` are collected and sent correctly when a library orgId is active.
  </action>
  <verify>Save settings in System context and verify (via network tab or logs) that `formatClaimRules` is sent as `[]`.</verify>
  <done>Persistence logic for `formatClaimRules` is hardened to prevent accidental global saves.</done>
</task>

<task type="auto">
  <name>Add Regression Tests for formatClaimRules Scope</name>
  <files>
    <file>tests/format_claim_rules_scope.test.js</file>
  </files>
  <action>
    1. Create a new test file `tests/format_claim_rules_scope.test.js`.
    2. Add a test case that attempts to save `formatClaimRules` at the system level and verifies they are not persisted (or are ignored by the backend).
    3. Add a test case that saves `formatClaimRules` for Library A and verifies they do not appear in Library B.
  </ housework: I'll check existing tests for patterns.
  </action>
  <verify>Run `npm test tests/format_claim_rules_scope.test.js` (or equivalent test runner).</verify>
  <done>Regression tests confirm auto-claim rules are strictly library-scoped.</done>
</task>

## Success Criteria
- [ ] `formatClaimRules` cannot be saved under system scope.
- [ ] Tests confirm that auto-claim rules are library-scoped and isolated.
