---
status: resolved
trigger: "getting this weird pub date timing drop down for additional copies, not sure what is going on"
created: 2026-05-18
updated: 2026-05-18
---

## Symptoms
- Expected behavior: Additional-copy edit modal should show configured publication timing options.
- Actual behavior: Publication timing dropdown can show a catalog publication year such as `2022`.
- Error messages: None reported.
- Timeline: Unknown; reported from staff additional-copy edit modal screenshot.
- Reproduction: Create or edit an additional-copy task after selecting a Polaris result with a catalog publication year.

## Current Focus
- hypothesis: Additional-copy creation copies Polaris catalog publication into the workflow publication timing field.
- test: Inspect UI payload and backend creation paths for `selectedPolarisPublication` and `publication`.
- expecting: Additional-copy paths should preserve the source request's patron publication timing option, not catalog year/date.
- next_action: Add regression coverage and fix payload/backend handling.

## Evidence
- timestamp: 2026-05-18
  finding: `pb_public/staff/js/modals.js` builds additional-copy payloads with `publication: result.publication || row.publication`, mixing Polaris catalog year into the workflow timing field.
- timestamp: 2026-05-18
  finding: `lib/additional_copies.js` also prefers `payload.selectedPolarisPublication` over `payload.publication`, so direct/server-side additional-copy creation can persist catalog dates as timing values.
- timestamp: 2026-05-18
  finding: Existing additional-copy tasks with catalog-looking date values need UI normalization on edit, otherwise `setSelectValue` preserves the invalid value as a historical option.

## Eliminated
- hypothesis: Publication option normalization alone renders byte arrays as numeric labels.
  reason: Existing normalization decodes byte-array JSON and resets stale select values; the screenshot value is a single catalog year saved as the record's publication value.

## Resolution
- root_cause: Additional-copy flow mixed Polaris catalog publication/date metadata with the patron publication timing setting.
- fix: Additional-copy payloads now preserve workflow publication timing, server action code clears Polaris publication for additional-copy actions, helper creation ignores `selectedPolarisPublication`, and the edit modal falls back from catalog-looking additional-copy values to configured timing options.
- verification: `npm test`
- files_changed: `pb_public/staff/js/modals.js`, `lib/additional_copies.js`, `lib/staff/title_request_actions.js`, `tests/additional_copies.test.js`, `tests/polaris_grid_search_ui.test.js`
