# Asap.Migration

`Asap.Migration` is a separate, self-contained migration artifact for the
PocketBase to SQL Server cutover. It reads a stopped PocketBase SQLite source
and its file storage directly; it does not start PocketBase or call the web
application.

The executable is pinned to PocketBase source
`150b30b776565194260cc327eeeffdfb46475e81`, DACPAC schema `12`, migration
contract `structured-policy-v4`, and export format `1`. Use `describe-contract` for the
machine-readable contract. The exporter accepts any syntactically valid 40-character
Git SHA so a compatible source revision can be represented; operators verify the
actual revision against deployment records. The baseline SHA is a behavior pin,
not an exact-SHA import allowlist.

Reusable Polaris credentials and positive workstation/system-user IDs remain
system integration settings. Legacy requesting/pickup aliases are recognized
only for exact migration provenance; operations select their library/branch.
Invalid supplied integration IDs fail import. The DACPAC seeds these IDs as
unconfigured NULL; readiness requires valid local configuration, without a live
Polaris call. Native schema-7/8/9/10/11 targets upgrade in place to 12 using the
DACPAC's embedded pre-deployment script before publishing the guarded plan;
pre-7 targets must be recreated and the stopped source re-exported with this
contract. Targets newer than schema 12 are rejected.

## Commands

```text
Asap.Migration --help
Asap.Migration --version
Asap.Migration describe-contract

Asap.Migration export --source <data.db> --storage <storage-dir> --output <package-dir> \
  --source-git-sha <40-char-sha> --confirm-source-stopped [--exported-at-utc <timestamp>]

Asap.Migration validate --package <package-dir> [--external-config <path>]

Asap.Migration import --package <package-dir> --connection-string-env <name> \
  --allowed-tenant-ids <comma-separated-guids> --report <path> \
  --external-config <path> [--postmark-token-env <name>]

Asap.Migration reconcile --package <package-dir> --connection-string-env <name> \
  --report <path> --external-config <path>

Asap.Migration recover-report --package <package-dir> --connection-string-env <name> \
  --report <path> --external-config <path>
```

## PACKAGE2 import and reconciliation

`import` accepts only a closed, immutable package whose manifest, source
schema, required files, referenced assets, and source-configuration warnings
validate together. The target is a fresh DACPAC database containing only the
permitted structural/static seeds; the application, web host, Hangfire, and
workers must remain stopped. A failed import is reset or recreated before a
retry; the importer never resumes a partial target.

Staff identity comes directly from each source staff user's valid real email.
Active rows with missing, invalid, placeholder, or duplicate normalized emails
block import before SQL mutation. Entra tenant/object metadata imports as null
and is populated by later successful sign-ins. A configured matching bootstrap
email may promote the existing row or insert a new super-admin through the
import path. There is no startup repair.

Configuration is reconciled by scope: system defaults, library overrides,
sparse format overrides, custom formats, whole-set inheritance, the effective
legacy URL settings, and the eight queue/four schedule operational comparison
are recorded with intentional decisions. Legacy SMTP transport is not
converted to Postmark. Target provider credentials are supplied only through
the protected import boundary and target Data Protection storage; values,
fingerprints, and provider responses do not enter reports or ordinary logs.

Material-format rows and patron format rules follow separate pinned legacy
paths. Missing row values use the legacy defaults, including `none` message
behavior, required author/publication, and title mode forced to required. A
sparse library row resets omitted row values instead of inheriting a customized
system row. The source row has no message column; message text comes from a
`patronFormatRules` entry when present. That JSON object replaces the whole
runtime rules set: omitted built-in codes use per-code defaults, and an
unlisted custom code uses the exact `book` rule when present or the legacy book
defaults otherwise. Rule code keys and member names are case-sensitive; unknown
mode or message-behavior strings use the pinned per-code default. The importer
projects those effective values into target format rows/overrides, and both
import reconciliation and read-only report recovery independently verify the
projection.

Two source format rows in the same scope and owner that collapse to the same
normalized target code (for example `book` and `0`) are rejected before SQL
mutation. A system and library row may share a target code only when their raw
source codes are identical; aliases such as system `book` plus library `0`
would collapse distinct legacy identities and are rejected. An exact same-code
system/library pair remains valid and maps to a library override. Format-rule
defaults use the exact raw legacy code before target-code normalization, so
alias `0` or `Book` uses custom-format defaults unless the exact `book` fallback
rule applies.

A library-only format that normalizes to a reserved system seed (`book`,
`audiobook_cd`, `dvd`, `music_cd`, `ebook`, or `eaudiobook`) is rejected with
`format_library_seed_unsupported` unless the source also contains an exact
same-code system row. This is valid legacy source data, but the current target
settings/editor contract reserves those codes for system formats and cannot
preserve the row as a library-owned editable format through settings edits or
reset. Resolve that source identity before export; the importer does not
reinterpret the DACPAC seed as source ownership.

Keep the six DACPAC system-format identities, but do not make a target-only
seed available to patrons. During import, system-format availability follows
the source system row's `enabled` value; a seeded format absent from source
system rows is disabled and listed in the
`system_material_format_availability` report transformation. Reconciliation
re-derives that list and checks the target `IsEnabled` values.

`import` and `reconcile` require `--external-config` and fail closed when the
target operational configuration is missing or does not match the frozen
legacy schedules and processing limits. `validate` may omit it when only the
immutable package structure is being checked.

The export preserves each organization's native organization-code and parent
identities. A code-2 organization above system ID 1 is a library; code-3 branch
rows and unrecognized rows remain stored as references but import inactive.
Missing type metadata never promotes a row from `enabledForPatrons` alone.
System organization ID 1 remains active even when its legacy flag is false.
Material-format ownership and additional-copy source-request ownership are
checked against both the source snapshot and target SQL.

Title-request `customFields` snapshots must be JSON objects whose field values
have the pinned `label`, `type`, and `value` properties (`displayValue` is
optional). Field types are `text`, `textarea`, or `select`; field keys are kept
even when the current configuration has retired them. Unsupported snapshots
block import. Duplicate JSON properties are rejected case-insensitively in
package JSON objects and schema-owned JSON fields such as format rules and
custom-field snapshots. Ordinary title, notes, label, and other text is never
decoded just because it resembles JSON, so literal text containing
duplicate-looking keys remains text.
For a disabled custom field the pinned source behavior is `hidden`; an enabled
select with no enabled options and an incoming `required` rule is imported as
`optional`, preserving the definitions and option identities.
Custom-field format-rule members are exact-case `mode` and `label`; unknown
mode strings normalize to `hidden`, and `labelOverride` is not a source field.

Operational integer overrides follow the pinned legacy `parseInt` prefix rule
and are bounded only after parsing, including values outside signed Int32.
Malformed values use the documented fallback; large positive and negative
values clamp to the configured minimum or maximum instead of falling back.
Leading whitespace follows ECMAScript `parseInt`: U+FEFF is trimmed and U+0085
is not.

The importer writes a package-identified `.pending` report before committing the
SQL transaction. The pending report binds the immutable package identity, the
expected `commit_pending` state, reconciled counts, the complete target
fingerprint, and a SHA-256 hash of SQL Server's reported server name and current
database name. That bounded target identity allows recovery to reject a report
presented to a different server or database without recording a connection
alias, endpoint, or credentials; it does not attest to machine identity or
prevent aliases that resolve to the same SQL Server identity. The report is
promoted only after SQL confirms commit. A report
promotion failure returns `import_committed_report_failed`; do not retry the
import. Resolve the report path and run `recover-report` with the same package
and target. Recovery only reads SQL and the pending report, independently checks
source relationships, target organization/format/request relationships,
complete typed transformation entries, source-derived staff/BIB decisions,
custom-field normalization, report metadata, counts, and fingerprint, and writes
no database state. The target fingerprint includes all SQL values, including
protected ciphertext, but stores only the resulting digest in the report. A
report fingerprint alone cannot authorize recovery. If SQL does not contain a
complete, matching import, report recovery fails closed.
An exception returned by SQL Server during commit has an ambiguous outcome; do
not assume rollback or retry until the target is inspected.

Claims are mapped before eligibility is evaluated. Eligible open claims are
preserved, invalid open claims are cleared only in their mutable fields, and
closed attribution remains historical. Status, close-reason, identifier,
actor, original-date, delivery, and request-event transforms are retained in
deterministic metadata. Placement protection records every applicable evidence
class and terminal reason, preserves known or explicit-null BIB provenance,
and inserts no synthetic hold operation, provider success, outbox, queue, or
pending-work record. Reconciliation compares source values and relationships,
not only counts or target fingerprints; the report includes the transform
decisions and semantic counters.

## Export handling

1. Stop PocketBase writes, scheduled jobs, and workers. Copy the SQLite file
   and associated storage to a trusted staging location while preserving the
   source. Keep the source stopped for the complete read.
2. Run `export` with `--confirm-source-stopped` and an empty output directory.
   The output path must be separate from both the source database and storage.
3. Run `validate` before transfer. The package contains normalized UTF-8 JSON,
   source SHA/schema/time and SQLite snapshot metadata, entity counts, file
   lengths, and SHA-256 entries in `manifest.json`. Effective runtime and
   operational configuration are separate artifacts. When SQLite is using WAL,
   `manifest.json` binds the matching `data.db-wal` name, length, and SHA-256;
   `data.db-shm` is transient index state and is not packaged or fingerprinted.

The exporter uses read-only SQLite access, copies supported PNG/JPEG/GIF
branding bytes, and blocks missing, unsupported, or invalid assets. It never
rewrites the old database or storage. Existing blank system settings and
missing-record initialization follow their distinct pinned PocketBase
environment precedence; the frozen artifact records the selected provenance.

Legacy SMTP transport fields, PocketBase auth/session state, and scheduler
runtime state are intentionally excluded. Source Polaris credentials needed by
the importer remain only in the restricted source package; effective artifacts
record secret presence and provenance, never values or fingerprints. A target
Postmark token is supplied through the import environment boundary and is
protected on the target.

## Transfer and cleanup

Use a trusted administrator-controlled transfer path with a minimal ACL limited
to the export/import operators. Do not put packages, source
credentials, or restricted import reports in Git, release assets, ordinary CI
artifacts, or normal command logs. Keep working copies in a restricted staging
directory and delete them deliberately only after successful import,
reconciliation, and any required retention window. Failed imports require a
fresh target or reset; this tool does not resume a partially migrated target.

## Native publication

Build the release artifact with the intended RID and without a target .NET
runtime:

```text
dotnet publish src/Asap.Migration/Asap.Migration.csproj -c Release -r win-x64 --self-contained true -o .artifacts/migration
```

The published directory must contain `Asap.Migration.exe` and the SQLite
native dependency `e_sqlite3.dll`. Exercise that published
executable for export, validate, import, and reconcile against controlled
stopped-source fixtures and a disposable SQL target. A representative old-host
rehearsal remains a release gate and is not replaced by a local synthetic
fixture.
