# Age Groups Feature Removal Plan

## Goal
Track the completed end-to-end removal of the **Age Groups** feature from DB schema/data, backend, API payloads, UI, reports, and tests without breaking patron suggestion workflows, staff workflows, or scoped settings behavior.

## Scope and terms
Track and remove all related terms:
- `ageGroups`, `ageGroup`, `age_group`, `agegroup`
- `audience_groups`, `audienceGroup`, `audienceMode`, `audienceLabel`
- UI labels: “Age Group”, “Age Groups”

This is a cross-cutting feature, not a single field removal.

## Retired settings-scope model
Before removal, Age Groups followed **system default + library override** behavior:
- system defaults in `ui_settings`
- library overrides in `patron_settings_overrides`
- lookup rows in `audience_groups`

The removal updated load, populate, save, and runtime-read paths together so Publication Timing remains the only public option setting using the scoped fallback behavior.

## Phased rollout

### Phase 1 — Discovery inventory
1. Repo-wide search for all variants above.
2. Build an inventory table for every DB object/API/UI surface/test using the feature.
3. Confirm usage in:
   - `ui_settings.ageGroups`
   - `patron_settings_overrides.ageGroups`
   - `audience_groups`
   - `title_requests.audienceGroup`
   - format rules (`fields.agegroup`, `audienceMode`, `audienceLabel`)
4. Record all routes that accept/return age-group fields.

### Phase 2 — Removal semantics
Recommended policy:
1. New submissions do not include age group.
2. Staff cannot edit/filter/report by age group.
3. Public config no longer returns `ageGroups`.
4. Short compatibility window: ignore legacy incoming age-group fields.
5. Export historical values once before destructive migration.

### Phase 3 — Backup and rollback prep
Before migration:
- Back up complete `pb_data/` (SQLite + storage)
- Save deployed revision + PocketBase version + env vars
- Generate historical export for request-level age-group values
- Validate restore procedure

### Phase 4 — Application changes
1. Remove age-group resolution and response payload from public config.
2. Remove patron form age-group field, validation, defaults.
3. Remove staff settings editors and override/reset handling for age groups.
4. Remove backend load/save/use paths for age-group settings and request fields.
5. Remove report/export/filter usage.
6. Keep compatibility shim (temporarily ignore legacy payload fields) if needed.

### Phase 5 — Data migration (safe order)
1. Deploy compatibility code first (no runtime dependency on age-group data).
2. Export historical data.
3. Null/remove `title_requests.audienceGroup` references.
4. Delete `audience_groups` rows (system + library).
5. Remove `ageGroups` values from `ui_settings` and `patron_settings_overrides`.
6. Drop obsolete fields/collections in schema migration.

Dependency rule: clear request references before deleting lookup rows.

### Phase 6 — Testing
Required checks:
- Public config excludes `ageGroups`
- Patron/staff submit flows work without age group
- Legacy payload fields are ignored (during compatibility window)
- Scoped settings still behave correctly for remaining fields
- Reset/copy settings no longer touch `audience_groups`
- Format rules no longer depend on `fields.agegroup`

Keep/extend coverage near:
- `tests/config_ui_text_patron_options_scope.test.js`
- `tests/staff_public_option_selects.test.js`

### Phase 7 — Deploy
1. Staging dry run with production-like data + full migration.
2. Smoke + integration + background-job checks.
3. Production: final backup, export, deploy, migrate, verify.

### Phase 8 — Rollback
If critical failures occur:
1. Stop app
2. Restore pre-migration `pb_data`
3. Redeploy previous revision
4. Restart and verify core workflows

## Verification checklist
- [ ] No UI shows Age Group(s)
- [ ] No API payloads return/require age-group fields
- [ ] No settings pages load/save age-group options
- [ ] No active reports/exports include age-group columns
- [ ] DB has no live age-group references
- [ ] Repo search only finds intentional leftovers (migration history/release notes/tests)

## Notes
This document is retained as historical context for the feature retirement across settings scope, request lifecycle, and reporting.
