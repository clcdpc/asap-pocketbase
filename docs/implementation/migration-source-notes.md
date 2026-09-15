# Pinned Migration Boundary Notes

Read-only preparation and acceptance context, not completion evidence. Source
is exact PocketBase `150b30b776565194260cc327eeeffdfb46475e81`. These notes
complement the source/schema notes for each domain and do not replace the
binding migration contract or executable source inspection.

## Staff Application URL

Inspected `lib/config/settings.js`, `lib/config/normalization.js` and
`lib/config/db_helpers.js` at the pin. The effective resolver and initialization
helper intentionally need separate fixtures:

- `settings.staffUrl(app)` reads the system record, trims the persisted value
  and, when nonblank, calls `normalizeStaffUrl`.
- For an existing blank value it tries trimmed `ASAP_STAFF_URL`, then trimmed
  `ASAP_PUBLIC_URL`, then `http://localhost:8090/staff/`.
- `normalization.defaultStaffUrl()` used when constructing a missing record
  instead tries `ASAP_STAFF_URL`, then `ASAP_BASE_URL`, then localhost. It does
  not use `ASAP_PUBLIC_URL`.
- `db_helpers.systemRecord` creates and saves a missing record with those
  defaults when the collection exists. An offline exporter must model the
  effective result without writing to or starting the stopped source.
- `normalizeStaffUrl` strips the hash and ensures a trailing `/staff/`.
  `staffUrlFromEnv` strips the hash and appends `staff/` unconditionally after
  a trailing slash. Thus an environment value already ending in `/staff/`
  produces `/staff/staff/` in the pinned helper. Do not silently substitute a
  more convenient normalization and call it exact parity; any correction must
  be explicit in the transform/reconciliation report.

The effective-runtime artifact records the system/global resolved SQL-bound
value and provenance. Library overrides belong in their domain exports. Do
not serialize reusable secret plaintext or fingerprints in either effective
configuration artifact. A code-default localhost result is not evidence that
production's process environment was recovered correctly.

## Legacy Request Links

Inspected `lib/route_utils.js` and
`pb_public/staff/js/app/url-utils.js` at the pin. `staffRequestUrl` appends
`stage=<normalized current status>&request=<PocketBase id>` to the effective
staff URL. `appendQuery` preserves existing query values and the hash, and
encodes names/values. Additional-copy side effects use the same `request`
parameter with `stage=additional_copies`.

The frontend reads `stage`, falling back to `status`, through the existing
stage map, and reads the trimmed `request` query parameter. It writes
`stage=submitted` for the suggestion tab and uses history replacement.

The target resolver must use the temporary type-qualified
LegacyPocketBaseMapping and normal current staff authorization. Mapping is
not permission, nor should equal numeric IDs in the two target request tables
conflate their identities. After successful resolution the browser replaces
the old request ID with the invariant decimal target ID while retaining the
appropriate supported query/navigation state. Unknown/deleted/out-of-scope
links must not disclose request details or bypass normal authorization.

Keep bigint IDs string-shaped in browser routing and mapping DTOs to avoid
loss through JavaScript numeric precision. A separate generic compatibility
service or retained PocketBase runtime is not required by this contract.

## Acceptance Focus

### Missing Historical Event Times

A narrow fresh Astra Max consultation confirmed that the current hard blocker
`request_event_created_missing` must remain for an affected package. Document
01 requires an event UTC timestamp and document 03 makes CreatedUtc non-null;
document 04 authorizes frozen-export time for the protection annotation only.
The pinned initial event schema and recordEvent helper do not themselves
establish historical timestamps. Document 11's summary is not evidence that a
particular deployed database contains those fields.

Do not invent original event time from export/import time, request creation,
or current UpdatedUtc merely to complete import. The source analytics fallback
for an undated hold event while currently hold_placed is a metric rule, not
an event-time recovery rule. Missing/null/blank required event time blocks;
malformed supplied time remains a validation blocker. Record affected source
references/counts in restricted diagnostics and do not emit a success report.

Whether the actual deployed data contains usable historical times remains
unverified. If real source evidence cannot resolve missing dates, that later
source-data/contract decision requires explicit resolution before rehearsal.
It does not block unrelated implementation or permit a synthetic fixture to
stand in for production evidence. Positive tests must supply explicitly known
event times, distinct from request UpdatedUtc and the frozen export time, and
assert exact preservation separately from annotation timestamps.

Test existing blank/persisted Staff URL separately from absent-record
initialization, environment precedence, whitespace/hash handling and the
known environment append behavior. Compare the frozen effective result to
target SystemSettings, not merely to exported source text. Use synthetic
source files and isolated fresh SQL targets; do not start the production
PocketBase installation or put migration packages in ordinary artifacts.

Legacy-link tests need scoped title/additional-copy resolution, unknown and
deleted IDs, authenticated/unauthenticated entry, URL normalization and
non-lossy large target IDs. Final Slice 7 acceptance must exercise the same
self-contained win-x64 artifact on representative old/new hosts, including
SQLite native loading and file-storage extraction. A locally successful
source build is not that rehearsal gate.
