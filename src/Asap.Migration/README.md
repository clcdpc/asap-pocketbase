# Asap.Migration

`Asap.Migration` is a separate, self-contained migration artifact for the
PocketBase to SQL Server cutover. It reads a stopped PocketBase SQLite source
and its file storage directly; it does not start PocketBase or call the web
application.

The executable is pinned to PocketBase source
`150b30b776565194260cc327eeeffdfb46475e81`, DACPAC schema `5`, migration
contract `slice-05`, and export format `1`. Use `describe-contract` for the
machine-readable contract.

## Commands

```text
Asap.Migration --help
Asap.Migration --version
Asap.Migration describe-contract

Asap.Migration export --source <data.db> --storage <storage-dir> --output <package-dir> \
  --source-git-sha <40-char-sha> --confirm-source-stopped [--exported-at-utc <timestamp>]

Asap.Migration validate --package <package-dir> [--external-config <path>]

Asap.Migration import --package <package-dir> --connection-string-env <name> \
  --staff-identity-map <path> --allowed-tenant-ids <comma-separated-guids> --report <path> \
  --external-config <path> [--postmark-token-env <name>]

Asap.Migration reconcile --package <package-dir> --connection-string-env <name> \
  --report <path> --external-config <path>
```

## PACKAGE2 import and reconciliation

`import` accepts only a closed, immutable package whose manifest, source
schema, required files, referenced assets, and source-configuration warnings
validate together. The target is a fresh DACPAC database containing only the
permitted structural/static seeds; the application, web host, Hangfire, and
workers must remain stopped. A failed import is reset or recreated before a
retry; the importer never resumes a partial target.

Staff identity is supplied by an operator-controlled map of source staff IDs
to Entra tenant/object IDs. The map is checked against the allowed tenants and
is the only durable identity source; UPNs and email addresses are not used to
derive identity. A configured matching bootstrap tuple may promote the
existing target bootstrap account or insert the mapped account through the
import path. There is no startup repair.

Configuration is reconciled by scope: system defaults, library overrides,
sparse format overrides, custom formats, whole-set inheritance, the effective
legacy URL settings, and the eight queue/four schedule operational comparison
are recorded with intentional decisions. Legacy SMTP transport is not
converted to Postmark. Target provider credentials are supplied only through
the protected import boundary and target Data Protection storage; values,
fingerprints, and provider responses do not enter reports or ordinary logs.

`import` and `reconcile` require `--external-config` and fail closed when the
target operational configuration is missing or does not match the frozen
legacy schedules and processing limits. `validate` may omit it when only the
immutable package structure is being checked.

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
   operational configuration are separate artifacts.

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
to the export/import operators. Do not put packages, identity maps, source
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
