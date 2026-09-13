# Slice 3 Acceptance Preparation

Slice 2's milestone `9f946ae` and exact-commit Linux CI `34744506275` passed;
fresh Sol XHigh is starting implementation. The fixture below was prepared
independently before dispatch. No Slice 3 test/review/milestone is complete.

## Isolated Source Fixture

Astra copied the stopped `staff-expanded-v2-data` fixture to a new ignored
`.git/asap-real-pb-source/additional-copy-data`, retaining every previous source
and evidence directory. Only Record/Collection API migration
`additional-copy-migrations/209901010015_additional_copy_acceptance.js` ran,
with empty hooks and no HTTP server or external calls. Actual pinned
`lib/additional_copies.js` and the three additional-copy schema migrations were
inspected first; no repository PocketBase implementation was edited.

Twelve `copycase0000000` through `copycase0000011` tasks have independent
bibliographic/library snapshots, original Notes, creator/closer snapshots and
distinct frozen creation/update/claim timestamps. Cases intentionally include
task snapshots different from the linked source; migration must not refresh
them from TitleRequest or current Organization data.

| Case | Claim / State | Required Import Result |
| --- | --- | --- |
| 0 | Active same-library staff, open | Preserve original effective attribution |
| 1 | Inactive mapped staff, open | Clear and retain history; claimant_inactive |
| 2 | Active foreign-library staff, open | Clear and retain history; claimant_out_of_scope |
| 3 | Active staff in its inactive library, open | Preserve original effective attribution |
| 4 | Active system super-admin, other library open | Preserve original effective attribution |
| 5 | Inactive mapped staff, closed | Preserve mapped historical claimant |
| 6 | Unmapped claimant, open | Clear and retain history; claimant_unmapped |
| 7 | Unmapped claimant, closed | Null FK, retain display/time and report |
| 8 | Attribution with blank claimant ID, open | Clear and retain history; claimant_unmapped |
| 9 | Attribution with blank claimant ID, closed | Null FK, retain display/time and report |
| 10 | No claim, closed, absent source | Preserve task; no claim conversion; null source |
| 11 | Foreign-library mapped staff, closed | Preserve mapped historical claimant |

Every open conversion appends exactly one deterministic annotation after the
existing Notes and retains all original attribution in the restricted report.
Use exported time `2030-01-04T00:00:00Z`, not import wall-clock time. Source
creator/closer display snapshots remain unchanged even when the current staff
name/activity differs. Task-source independence and source deletion also need
real-SQL runtime acceptance, not only migration counts.

The frozen Slice 2 native executable exported all twelve tasks successfully to
`slice02-additional-copy-preparation-package`. That package is a preparatory
`slice-02` contract, not a Slice 3 import result. Re-export the stopped database
with Slice 3's actual new native executable before final import acceptance.

Current-slice negative boundary check
`58e436f000a84635a46565ab20c2ebb0` ran the frozen native executable against a
new DACPAC-deployed SQL target using an already-compiled isolated harness. It
returned `source_domain_not_supported` for the twelve task rows, with zero
TitleRequest/StaffUser/outbox rows and complete owned-database cleanup. This is
an expected current-slice refusal, not a Slice 3 migration implementation test.

## Independent Acceptance Harness

Astra extended its ignored, app-reference-free staff migration harness with a
`copies` mode. It retains expanded staff/rule/title-request assertions and adds
all twelve task mappings, independent bibliography/library snapshots, original
creator/closer/time/Notes, effective claim/history/conversion reasons, current
eligibility SQL and same-count task snapshot drift detection. This harness
builds independently with zero warnings/errors; no Slice 3 execution has passed
yet. Final source-built native export/import remains the acceptance boundary.

A second stopped copy, `additional-copy-missing-updated-data`, applies only
Record API migration `209901010016_additional_copy_missing_updated.js` to clear
case 10's `updated` field. Read-back verifies it remains absent. Original data
and evidence are retained; no HTTP server/provider or raw legacy SQL ran. The
`copies-missing-updated` acceptance mode requires that task's target UpdatedUtc
to use original CreatedUtc, as specified for AdditionalCopy timeout age. Export
this separate source with the new native artifact without rewriting a manifest.

The accepted Slice 2 native exporter separately produced
`slice02-additional-copy-missing-updated-preparation-package`; direct JSON
inspection confirms case 10 retains its original creation/closure times and
an empty `updated` value. This proves the source fixture, not target fallback.

Harness baseline regression `661b31ca6b2e4a64be9674367d176bb5` passed against
the unchanged accepted Slice 2 native artifact and expanded staff package:
both identity-map refusals were clean, valid import/reconciliation succeeded,
all existing staff/title-claim/rule assertions passed, same-count preference
drift was rejected, and the owned database was removed. The added task checks
still await Slice 3's artifact; no early task acceptance is claimed.
