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
defaults otherwise. An explicit `null` format entry behaves like an empty rule
object and uses those same defaults; a `null` `customFields` member means the
format has no custom-field rules, so each field is hidden by default. Rule code
keys and member names are case-sensitive; unknown mode or message-behavior
strings use the pinned per-code default. The importer
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
Publication options are schema-owned when their trimmed text starts with `[`: after
decoding, every object is checked recursively for exact, escaped-equivalent, and
case-insensitive duplicate properties, including nested properties. Both the
importer preflight and the independent verifier enforce this policy. Newline
labels and ordinary text outside this recognized array branch stay text.
For a disabled custom field the pinned source behavior is `hidden`; an enabled
select with no enabled options and an incoming `required` rule is imported as
`optional`, preserving the definitions and option identities.
Custom-field definitions and option sort orders use `(source array index + 1) * 10`
when `sortOrder` is omitted; explicit JSON `null` maps to `0`, and an explicit
integer is preserved. The independent oracle checks all three cases.
Custom-field format-rule members are exact-case `mode` and `label`; unknown
mode strings normalize to `hidden`, and `labelOverride` is not a source field.
For current custom-field definition/option text and current material-format rule
values, normalization uses the pinned ECMAScript `String.trim` whitespace set:
U+FEFF is trimmed and U+0085 is retained. The same rule is used independently
when reconciling and checking report counts. U+0085-wrapped type names therefore
remain unsupported instead of being invented as a valid `select` type. Exact
raw rule-map property keys are not normalized, and historical request snapshot
field identities and values are retained and compared semantically without
applying today's definition or option normalization.

`commonAuthorsList` is split on line feed only, then each line is trimmed using
the same ECMAScript whitespace rule; commas inside a creator name are data.
`allowedPatronCodeIds` is split on commas only. Each token must be the same
canonical positive Int32 identity the target stores; newline-separated,
leading-zero, or otherwise lossy tokens are rejected before SQL writes.
Duplicate-status override JSON preserves the effective raw label text. Modern
overrides use the legacy nonblank-label activation rule, and a wholly blank
modern object inherits system values while its record still suppresses retired
legacy fallback. Active values that are blank to the target, including NEL-only
labels, are rejected rather than silently changed; legacy direct label objects
have the same target-representation boundary.

Publication options trim the outer source text, option labels, and explicit IDs
with ECMAScript whitespace semantics. JSON alias selection preserves source
truthiness before trimming; a selected whitespace-only alias is refused before
SQL because the target cannot represent the pinned skipped-row behavior. An
empty label can still fall through to a valid name/value alias. Explicit IDs
retain their trimmed spelling, missing IDs use the pinned label-derived slug,
and newline input is split into lines rather than comma-delimited values.
System empty/numeric-only fallback uses the existing canonical seeded option
IDs; the equivalent library fallback inherits the effective system list.
In typed publication-option objects, an explicit JSON `null` for `enabled`
uses the source default `true`, and a `null` `sortOrder` uses the source ordinal
default `(index + 1) * 10`. These publication rules are separate from custom
field sort order, where explicit `null` remains `0`; the current API's own null
validation is unchanged.

The protected Postmark credential remains presence-only. Every import report
records one bounded `email_provider_token` operator-provisioning transformation
for system organization 1, including imports with no SMTP rows. Its boolean
records only whether an operator supplied a token; it does not contain the
token, ciphertext, or any secret-derived hash. Reconciliation and report
recovery check that bit against the target credential's presence, independently
of the SMTP source population.

When an `smtp_settings` row exists, its report entry preserves the exact source
ID and records `legacy_smtp_transport_intentionally_dropped` plus the stable
`target_email_sender_selected_by_external_configuration` boundary. The latter
means the target sender is selected by external configuration; it does not
snapshot the mutable `EmailSafety.DeliveryMode`. Switching between `capture`
and `postmark` therefore does not invalidate the migration's historical report.

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

`reconcile` and `recover-report` check the **stopped, pre-activation import
snapshot**. Complete import, report promotion/recovery, and reconciliation before
enabling the web host, background workers, or any other target writer. These
commands do not reconcile an activated application's evolving database. Stopping
an already activated application does not recreate the original import snapshot.
Retain the accepted package/report as cutover evidence; validate later operations
through application journals, audits, and the backup/recovery process. Runtime
rows cause `reconciliation_requires_pre_activation_target`, which states the
command's lifecycle limitation rather than declaring valid later activity corrupt.
No operational rows are deleted or rewritten by these commands.

The independent population inventory is:

| Target population | Import expectation and lifecycle |
| --- | --- |
| `PatronSession`, `EmailOutbox`, `QueueProgress`, `HoldPlacementOperation`, `PickupPreferenceOperation`, `AdministrativeAudit` | Empty on the fresh target and throughout import/reconcile/immediate report recovery. Import creates none. Legitimate application activity may populate them after activation. All six are checked independently of report fingerprints. |
| Organizations, staff, requests, additional copies, deleted-request audit, email-delivery history, formats/overrides/rules, workflow tags/joins, branding, and configuration rows/sets/options | Exact source-derived populations plus documented DACPAC seeds and migration transformations. Legacy mappings bind imported source identities. |
| `TitleRequestEvent` | Exact source events plus independently justified claim-normalization annotations and placed-BIB protection markers. |
| `StaffUser` bootstrap exception | Only when source staff lack a usable active email-authenticated super-admin, and only for the configured normalized authentication identity: promote/reactivate its source-mapped row or insert one if there is no source match. A report cannot authorize bootstrap. Promotion changes only UPN/normalized UPN, role, organization and activity; all other source fields remain exact. Inserted display/contact/default preferences must match operator configuration and the documented insertion policy. |
| `SchemaVersion`, `DeploymentState` | DACPAC/deployment-owned structural state; import pins the schema version and seed timestamp. Deployment metadata may exist from stopped-host preparation and is captured by the fingerprint, not treated as excluded application activity. |
| `[HangFire]` | Separate deployment/job-schema owner, outside the `[asap]` migration snapshot. Workers remain stopped until cutover activation. |

For the five workflow integers, pinned `workflows.js` applies `getInt(...) ||` the
fixed defaults `5`, `30`, `14`, `14`, `14` after selecting the scoped value.
Import stores a system zero as that default. An explicit library zero also stores
that literal default as an override, even when the system has a different value;
only a null/missing library value inherits the system setting. Import and the
independent verifier derive this behavior separately from the pinned source.

The final semantic check independently derives source-owned projections for
organizations, formats and sparse overrides, custom definitions/options/rules,
system and library settings, workflow sets/providers, template lineage,
staff, requests/copies, claims, tags, branding, deleted audit, request events,
and email-delivery history. It checks exact target row populations, each mapped
identity/relationship, source-owned SQL field, and authorized transformation;
refreshing a pending report fingerprint does not bless semantic drift. This
projection reads the immutable package and SQL directly and does not call the
importer, its settings resolvers, or reconciliation code to decide expected
values. Protected Polaris credentials and the target provider token are
presence-only/operator-provisioned boundaries: the oracle does not compare or
report plaintext, ciphertext, hashes, or fingerprints for them. The full
source-family/field and exception matrix is maintained in the PR review's
contract evidence.

The report always carries one typed `email_provider_token` transformation
containing only organization `1` and a `postmarkTokenProvisioned` boolean. It
exists even when the source has no SMTP row, because the target Postmark token
is supplied by the operator rather than migrated from PocketBase. If the
source-SMTP audit transformation is present, its presence bit must agree. The
oracle checks token presence against this boundary on every reconcile and
report recovery; it never compares or emits token plaintext, ciphertext,
ciphertext hashes, or per-secret fingerprints. The current unshipped report
version remains version 6; this bounded field is part of that contract.

All imported timestamps target `datetime2(7)` with explicit seven-digit
parameter precision and exact source/manifest UTC ticks. The embed-origin
preflight validates raw authority and numeric port range before SQL while
preserving pinned legacy normalization. Ordinary origins allow only an
optional trailing slash; wildcard origins are HTTPS-only ASCII DNS suffixes
with at least one dot and no path. Authorities reject user info, malformed
DNS/IPv6, empty/nondecimal ports and numeric ports outside 0–65535, queries,
fragments, and other paths. An explicit port is preserved as supplied,
including `:443`; this does not equate omitted and explicit port text for the
target CSP contract. Template
lineage must remain editable in the target: system templates
cannot declare override parents; ordinary library overrides preserve the
system template key; rejection overrides inherit only from system rejection
templates; and workflow timeout choices reference rejection-template rows in
system or the selected library scope. Legacy ordinary-email lookup ignored an
explicit parent ID for effective selection, so an explicit parent/key mismatch
is refused as an unsupported target lineage shape instead of being rewritten.
Ordinary source templates whose key begins with the target-reserved
`rejection:` prefix are also refused: target rejection policy classifies by
that prefix, while the pinned source keeps ordinary and rejection templates
in separate collections. SQL template keys are case-insensitive, so imports
refuse case-only collisions with the seeded `suggestion_submitted` template,
case-only implicit ordinary-template parent matches, and rejection-key
collisions that would collapse separate source identities. Noncolliding
source key casing is preserved. Workflow-tag codes retain their exact source
case except for the four pinned display-label aliases; case-only collisions
with another source tag or a static seed are refused before SQL. Numeric
`libraryOrganization` fallback references follow the importer's integer
syntax (including a leading `+`), and still require a code-2 library row.

Publication option arrays and newline lists use the pinned source normalizer.
An empty System list, or more than three labels where over half consist only
of ASCII digits, resolves to the canonical seeded options
`already_published`, `coming_soon`, and `published_a_while_back`; the numeric
safety rule applies to both JSON arrays and newline lists. The same fallback
in a library means inherit the current System set, so it creates no library
set or option rows. A zero or omitted option order uses its original array
index times ten; effective options are stored in stable numeric order. Explicit
IDs are trimmed and missing IDs use the legacy label slug, including its
`İ`-to-`i` mapping. A truly empty label alias falls through to `name`/`value`,
but a selected whitespace-only alias is rejected before SQL because the source
would skip that item. This source-import normalization is separate from the
target settings API, where an explicit empty list is authoritative.

Current custom-field definition keys and option IDs follow the pinned source
normalizer, including label-derived identities and the array-index default for
omitted sort order; explicit null sort order remains zero. Invalid or colliding
normalized identities are refused before writes. Historical request snapshots
retain their historical keys, labels, types, and values without current
definition normalization; comparison uses validated semantic JSON equality,
not byte-for-byte serialization. Mapping source keys and entity types are
checked with ordinal identity comparisons even though target SQL lookups use a
case-insensitive collation.

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

Branding `UpdatedUtc` follows the linked current `ui_settings.logoAlt.updated`
timestamp when that non-null alt-text row is imported. When an asset has no
linked imported UI alt-text value, the branding timestamp is the immutable
package export time: the branding asset package record has no source update
timestamp of its own. Dormant legacy `library_settings` logo fields do not
provide a timestamp or override this rule.

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
