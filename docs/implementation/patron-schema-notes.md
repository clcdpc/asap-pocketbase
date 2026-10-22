# Pinned Patron Schema Notes

Read-only Slice 1 preparation by Luna High, completed against
`150b30b776565194260cc327eeeffdfb46475e81`. No source data was accessed and no
PocketBase runtime or SQL was used. These are inspection anchors, not a new
schema specification; the port pack remains authoritative.

## Source Collections

- `pb_migrations/0000000000_initial.js` defines organizations, staff,
  material formats, workflow/UI/system settings, requests, events/tags, and
  mail templates/audit. Inspect later migrations before assuming final shape.
- `202604300004_scoped_lookup_tables.js` gives material formats system/library
  ownership and stable codes. The six seeded formats are not an exhaustive
  product restriction; custom formats exist. Typed field behavior also lives
  in `lib/format_rules.js`. Age groups were removed by `202605130002`.
- `202605010001_patron_library_settings.js` and
  `202605010002_patron_settings_overrides.js` define publication/status/rule
  override storage; `202606080001_library_additional_fields.js` adds custom
  definitions and request custom-field snapshots. `lib/custom_fields.js`
  defines text/textarea/select fields, options, per-format modes, and submitted
  label/type/value/displayValue snapshots. Relationalize configuration per
  the pack; retain request snapshots.
- `202605110001_format_auto_claim_rules.js` adds claim/rule fields. Rules
  require active assignee and same library or super-admin at submission.
  Source stores both PocketBase relations and duplicated IDs; do not infer
  target Entra identities from these legacy values or actor names.
- `202605010003_preferred_pickup_branch_fields.js` and
  `202607270001_patron_code_eligibility.js` add pickup and patron-code snapshots.
  Organizations have external organization ID, parent IDs/relations, labels,
  participation flags, raw source JSON, and sync timestamp. Legacy enabled
  libraries also appear in system settings; pack target participation wins.
- Request state includes status/format/close-reason relations and strings,
  immutable patron/contact/library snapshots, current pickup, bibliographic
  data, autohold, claims, ISBN lookup state, custom fields, notes and dates.
  `lib/records/helpers.js` resolves canonical relations and append-style events.
- `202604300005_hold_result_workflow_tags.js` and
  `202604300006_merge_legacy_workflow_tags.js` normalize tag taxonomy/joins.
  Migration must retain normalized historical placement evidence, not merely
  the current status and tags. No synthetic successful provider operations.
- `202606120001_purchase_approved_email_template.js` and
  `202606240001_rejection_template_source_ids.js` update template content and
  override lineage. Historical email_delivery_events and weekly scheduled
  delivery history are audit only, not pending outbox work.

## Reconciliation And Fixtures

Every implemented source setting must map through pack document 13 or have an
explicit intentional drop. Publication defaults are system UI plus
patron_settings_overrides, not a possibly branding-only library UI row. Test
null/missing versus explicit false/blank/empty sets per the declared setting.

Active staff require operator-supplied tenant/object ID mapping; claims must
map first and then satisfy current staff eligibility independently of library
participation. Invalid rules retain inactive history without substitution.
Preserve all five preferences when the staff foundation is introduced.

Unknown event actor types, unresolvable status/close reason/material format,
unmapped active staff, and unclassifiable identifier state require explicit
blocking diagnostics. In particular, found/found_in_polaris without BIB and
legacy error with a nonblank identifier are not silently normalized to success
or retry. Apply the pack's exhaustive maps and placement-evidence rules.

Legacy SMTP transport credentials are intentional drops; never turn them into
Postmark credentials. Import target Postmark tokens only through the pack's
secure target-only input and protected SQL representation. Outbox begins empty.

Source helper limits exceed some PocketBase collection widths (title/author
helper 500 versus collection 256, with identifier/publication differences too).
Use actual exported values and representative runtime/fixture evidence; do not
guess historical truncation or silently truncate during import. JSON byte
arrays need decoding at the PocketBase boundary; normalized export is UTF-8
JSON with deterministic IDs/order/hashes and strict reconciliation.
