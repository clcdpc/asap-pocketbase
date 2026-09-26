# PocketBase -> SQL Migration and Production Cutover

## 1. Migration is a separate explicit process

The one-time PocketBase -> SQL conversion is **not** part of normal application deployment and is never auto-detected at startup. `Asap.Migration` is a permanent source project retained in the repository, but its executable/assets are built as a separate migration artifact and are not installed with the ordinary web deployment.

Build the migration artifact from the same exact version tag as the application/DACPAC artifact. Publish it **self-contained for `win-x64`**, including the SQLite native/runtime dependencies needed for direct PocketBase database access, so the old PocketBase server does not need a .NET 10 runtime/Hosting Bundle installed. The same immutable migration artifact should run export on the old host and validate/import/reconcile on the new host.

Migration functionality is developed alongside every implementation slice rather than being postponed until the application is otherwise complete.

## 2. Two-host export/import model

Production .NET runs on a different, already-existing Windows server from the current PocketBase application.

Therefore migration is deliberately split:

### Export - old PocketBase server

Run `Asap.Migration export` (exact CLI naming may vary) on the **old server** after PocketBase writes/jobs are stopped. Run it with access to the same effective legacy environment configuration used by the PocketBase process; if an environment-backed value that affects a SQL-bound setting cannot be recovered, export fails rather than guessing. It reads:

- the stopped PocketBase SQLite database directly;
- PocketBase file storage required for persisted assets such as branding logos;
- the effective legacy environment inputs needed by the pinned configuration resolvers so `effective-legacy-runtime-config.json` can be frozen alongside the data export;
- the effective cron and queue-limit environment inputs needed to freeze `effective-legacy-operational-config.json` for external operational-config parity.

Do not require a running PocketBase API for extraction. Exercise the exact self-contained artifact on a representative old-server environment during rehearsal, including direct SQLite open and branding/file-storage reads.

Export creates a normalized UTF-8 JSON directory/package plus manifest. The export format is intentionally independent of SQL schema details so the extraction and import phases can be separately validated.

### Import/reconcile - new .NET server

Transfer the normalized package through the trusted administrative path to the new ASAP server. Run `Asap.Migration validate/import/reconcile` there against the pre-provisioned empty SQL database. `validate` may omit external configuration when only package structure is being checked, but `import` and `reconcile` currently require the ACLed target external configuration path:

```text
Asap.Migration import ... --external-config <path>
Asap.Migration reconcile ... --external-config <path>
```

The required external configuration is the target operational configuration used for schedule and processing-limit parity; do not substitute a package-local or guessed file.

Do not install or run the migration executable on SQL Server itself.

## 3. Package security

The migration package contains PII and reusable integration credentials. Initial design intentionally does **not** add package-level encryption.

Controls:

- write it only to a tightly ACLed administrative directory;
- allow access only to the required operator/service identities;
- transfer only over the trusted administrative path;
- never place it in source control, release assets, ordinary logs, or shared folders;
- record hashes/manifest metadata without exposing sensitive content;
- delete copies from both servers immediately after successful migration validation/cutover;
- retain the original PocketBase production backup separately under the existing archival policy.

## 4. Normalized export contents

The manifest should identify at minimum:

- export format version;
- PocketBase source schema/app version and exact deployed PocketBase Git SHA, which becomes the permanent final-tag target after successful cutover;
- export UTC timestamp;
- source database hash/metadata sufficient to identify the snapshot;
- entity/file counts;
- file hashes for exported JSON/assets;
- warnings/intentional normalization notes.

`sourceDatabase` records the stopped source `*.db` file's name, length, and
SHA-256. It also records `wal: null` when the matching `*.db-wal` sidecar is
absent, or a `wal` object with that sidecar's name, length, and SHA-256 when it
is present. Export captures the main file and WAL metadata before opening
SQLite and verifies both are unchanged after the read; a changed, missing, or
new main/WAL file aborts export. The `*.db-shm` file is SQLite's rebuildable
WAL-index state, so it is intentionally neither part of snapshot identity nor
the normalized package; the stopped source must keep the main database and
WAL sidecar together for the complete read.

Suggested data files by dependency domain:

```text
manifest.json
organizations.json
staff-users.json
system-settings.json
effective-legacy-runtime-config.json
effective-legacy-operational-config.json
polaris-settings.json
workflow-settings.json
patron-settings.json
email-settings.json
external-search-providers.json
publication-options.json
common-creators.json
patron-code-eligibility.json
patron-custom-fields.json
branding.json (+ assets/)
material-formats.json
material-format-overrides.json
material-format-custom-field-rules.json
format-auto-claim-rules.json
workflow-tags.json
title-requests.json
title-request-tags.json
title-request-events.json
additional-copy-requests.json
email-templates.json
email-delivery-events.json
deleted-request-audit.json
...other explicitly mapped current collections...

```

The export shape should expose PocketBase IDs needed to resolve relationships during import but should not reproduce PocketBase internals indiscriminately. `email-settings.json` carries only migratable sender/configuration semantics and source provenance; it must not contain the legacy SMTP password or the new Postmark server token.

## 5. Import execution model

- Target database must be a fresh schema-6 migration target for the one-time import: the intended DACPAC may already be deployed and deterministic structural/static seed rows such as `SchemaVersion`, `DeploymentState`, and app-owned taxonomy/default scaffolding may exist, but runtime/business/bootstrap rows (`StaffUser`, requests, imported configuration-domain rows, outbox, etc.) must be empty. Schema 6 is a pre-release reset boundary, so any application database created from the earlier OID-based staff identity schema is recreated from the schema-6 DACPAC rather than upgraded in place. The final target must never have been used for production-hostname preflight.
- Deploy the `BibIdStaffVerified` column before the first source import into that fresh target. Adding its `DEFAULT (0)` to an existing database would give every preexisting BIB an unverified value without recovering its provenance; that is not a supported authority backfill. The cutover path recreates the pre-cutover target, imports every `TitleRequest` with an explicit classified value, and does not start the identifier worker until reconciliation passes. After import, ordinary DACPAC upgrades preserve the stored field values.
- If import fails, reset/recreate target and rerun; do not create a complicated resume-in-place engine.
- Execute dependency-ordered phases.
- Use a transaction per coherent phase rather than one giant transaction if data size/operability favors bounded phases.
- Stop immediately on a structural/semantic blocker.
- Produce a deterministic transformation/reconciliation report.
- No dual write and no reverse synchronization.

A dry-run/preflight mode should validate source values and mappings before target mutation wherever practical.

## 6. Binding transformation rules

### 6.1 Organizations

- Polaris Organization ID is the durable target key.
- System organization `1` maps to target Organization `1`.
- Historically referenced libraries that are no longer participating are still inserted as inactive Organization rows, using historical metadata where available, so request/staff/history FKs remain valid. Staff/admin relationships to those organizations remain valid regardless of `Organization.IsActive`; usability is an authorization concern, not a migration FK rule.
- Newly discovered/current but non-participating libraries are not implicitly activated by migration.

### 6.2 Staff users

Migration uses each PocketBase staff user's existing real email as the target authentication identity:

- Normalize `staff_users.email` into `UserPrincipalName`/`NormalizedUserPrincipalName`. Active rows with missing, malformed, or `@staff.asap.local` email block import.
- Duplicate normalized staff emails block import; never silently merge source users.
- Import Entra tenant/object metadata as null. It is populated automatically by later successful sign-ins and requires no operator mapping file.
- Preserve display name, role, organization, active state, preferences, and historical relationships. Active staff may reference an inactive historical library, but participation remains unavailable until that library is active.
- Initialize `NotificationEmail` from the valid source staff email, with a valid legacy weekly email only as fallback for an inactive historical row lacking a real primary email. Later Entra sign-in never repopulates it.
- Migrate `weekly_action_summary_email` independently into `WeeklyActionSummaryEmail` when it is a valid real address; do not collapse it merely because it equals the resolved primary address. The target weekly-summary recipient is this override when nonblank, otherwise `NotificationEmail`. This intentionally normalizes current PocketBase behavior where weekly summaries require a nonblank weekly field while that same field is also used by some non-weekly notification paths.
- The migration report must compute **old versus target effective recipient/eligibility**, not just copied field values. At minimum report: (a) every summary-enabled staff user who has no current weekly-summary recipient but gains one through target `NotificationEmail` fallback; (b) every staff user whose ordinary assignment/purchase/additional-copy notification recipient changes under the new primary-recipient contract; and (c) any staff whose target recipient becomes null. The newly eligible weekly-summary fallback is an intentional desired target behavior, but it must be visible before cutover.
- Preserve the remaining current staff profile preference fields exactly: `weekly_action_summary_enabled` -> `WeeklyActionSummaryEnabled`, `purchase_reminder_default` -> `PurchaseReminderDefault`, `additional_copy_reminder_default` -> `AdditionalCopyReminderDefault`, and `default_mine_unclaimed_filter` -> `DefaultMineUnclaimedFilter`. These values are per-user preferences and never participate in system/library settings inheritance.
- No `LegacyPocketBaseId` column is needed on StaffUser; use `LegacyPocketBaseMapping` if mapping is required.


#### Active email-authenticated super-admin cutover gate

Immediately after StaffUser import, and before enabling `Asap.Web` or Hangfire, migration must prove:

```text
COUNT(active StaffUser
      WHERE Role = super_admin
        AND normalized authentication email is valid and unique
        AND OrganizationId = 1
        AND the common current-eligibility predicate passes) >= 1
```

This is an explicit cutover hard gate, not an assumption delegated to normal startup bootstrap. If import produces zero qualifying rows, migration matches the configured bootstrap normalized email, promotes/reactivates that row when present, or inserts one email-authenticated super-admin when absent. Tenant/object metadata is not required. Record the intervention and re-run the gate before startup.

### 6.3 Effective operational claimant normalization

An imported FK is not proof of operational eligibility. After StaffUser identity/bootstrap normalization, apply this transform to **every** claim on an open/actionable TitleRequest (`suggestion`, `outstanding_purchase`, `pending_hold`, `hold_placed`) and every open AdditionalCopyRequest. Map the source StaffUser ID first; separately evaluate the mapped target StaffUser using the common predicate in `01-PORTING-SPEC.md` section 7.6 with participation not required for a stored relationship. Active target staff identity validation in section 6.2 remains a hard gate; claim repair must not bypass it.

| Source claim / mapped target | Effective open claim | Report reason |
|---|---|---|
| No source claimant or claim metadata | Remain unclaimed | No conversion |
| Source claimant cannot map, or attribution exists without a mappable source ID | Clear | `claimant_unmapped` |
| Mapped target inactive | Clear | `claimant_inactive` |
| Mapped active staff/admin belongs to another library, including a demoted super-admin now outside this scope | Clear | `claimant_out_of_scope` |
| Mapped active staff/admin with valid email in the same library | Preserve | Eligible |
| Mapped active super-admin with valid email in Organization 1 | Preserve, including cross-library claims | Eligible |

Eligibility requires valid target role/organization and identity/trust under section 6.2. Evaluate failure reasons in the listed order; unexpected invalid active identity/role shape blocks section 6.2 validation rather than silently becoming an apparently valid claim. **Organization.IsActive alone never clears a claim**: a correctly scoped claimant may remain attached to dormant library work. Do not run auto-claim rules or select a substitute person during import.

Clearing removes the effective StaffUser FK, display-name snapshot, claim time, claim type, and claim-rule reference where those fields exist. Preserve the original source claimant ID, mapped ID where available, display snapshot, claim time/type/rule, and conversion reason in the restricted deterministic transform report and existing TitleRequest legacy event/note or AdditionalCopy Notes. This is historical attribution, not an effective claim. Use the frozen export timestamp/provenance for migration annotations, never the import wall clock, so rerunning into a fresh target produces identical results.

For closed TitleRequests/AdditionalCopyRequests, keep historical claimant fields and a mapped StaffUser FK even if the user is now inactive/out-of-scope. If unmapped, retain the display/time/type attribution with null FK and report the historical mapping conversion; do not pretend it is an operational claim. Closed records are not reopened or otherwise normalized as open work during import. Runtime AdditionalCopy reopening later applies section 20.1 of the porting spec.

Reconciliation must join every non-null open claimant to the current target StaffUser and prove activity, valid identity/trust, and role/library scope (same library or system super-admin), **not just FK existence**. Report exact converted/preserved counts by request type, library, and reason, separately from closed-history conversions and invalid-rule normalization. All unchanged historical fields and deliberate cleared fields must match the transform report.

### 6.4 Events

- Known event types map to target constrained codes.
- Unknown historical event type -> target `legacy`; store original type in event metadata. The new app never emits `legacy`.
- Unknown/invalid historical **actor type** is a migration blocker because actor semantics affect trust/audit interpretation.
- Historical events with `ActorType=staff` do not infer `StaffUserId` from display text; leave FK null unless a durable source relation exists.

### 6.5 Workflow status and close reason

Map known aliases to target constrained values. Unknown values that cannot be semantically mapped to the target business constraint block migration rather than silently falling back to a default.

### 6.6 Configuration/settings

`13-SETTINGS-SCOPE-INVENTORY.md` is the controlling migration map. Do not reconstruct a generic settings row in SQL. Export may be target-oriented, but preserve source provenance so every transformed value can be traced.

- Generate and freeze `effective-legacy-runtime-config.json` from the stopped pinned PocketBase installation **only for system/global SQL-bound values whose effective value requires runtime fallback resolution across persisted data, environment, and/or code defaults**. It is not the representation of library-scoped settings. Resolve each value using the same pinned runtime resolver path, record the resolved value and provenance (`persisted_database`, `environment_fallback`, or `code_default`) plus its source field/environment variable/default identifier, and reconcile the resulting target SQL system/global value. Secret values in this snapshot record only provenance/`hasValue`; reusable secret plaintext or hashes/fingerprints are never serialized.
- Library-scoped settings are reconciled through their domain export files (`workflow-settings.json`, `patron-settings.json`, `email-settings.json`, publication/material/template/etc. files), where organization/scope is explicit. Do not duplicate them into the runtime-fallback snapshot.
- See `examples/EffectiveLegacyRuntimeConfig.example.json` for the intentionally system/global-only snapshot shape.
- `StaffApplicationUrl` is a mandatory explicit resolver test. Current runtime behavior is: a meaningful persisted `system_settings.staffUrl` wins; if blank, `ASAP_STAFF_URL` is checked; then `ASAP_PUBLIC_URL`; then the runtime localhost fallback. Separately, the helper used when synthesizing/initializing a missing system record uses `ASAP_STAFF_URL`, then `ASAP_BASE_URL`, then localhost. The snapshotter must exercise the actual pinned resolver path for the frozen database state rather than flattening these two paths into an invented fallback order.
- For each system/global field represented in the runtime-fallback snapshot, import/reconcile the frozen effective value rather than a blank raw field. Other system/default and all library-scoped configuration comes from the appropriate domain export; the runtime-fallback snapshot is not a second complete settings export.
- Global application-only values -> `SystemSettings` / `PatronEmbedAllowedOrigin`.
- Polaris integration -> system-only `PolarisSettings`; drop legacy per-staff Polaris auth fields not used by the target.
- Workflow scalar defaults/overrides -> `WorkflowSettings`; only meaningful current library overrides become nullable library values. Common-creator list -> `CommonCreatorSet`/terms; allowed patron-code list -> `PatronCodeEligibilitySet`/members. Blank library lists mean no set/inherit.
- Patron UI/status/eBook/eAudiobook values -> `PatronSettings`, correcting current record-level fallback quirks to target field-level inheritance without discarding stored values. Blank fields on a partial library UI record become no override and may therefore change from a hard-coded fallback to the configured system value; enumerate those intentional corrections in the transform report.
- Current SMTP/sender state -> target Postmark-oriented `EmailSettings` only where there is a semantic equivalent: migrate effective sender identity/configuration, but treat legacy SMTP host/port/username/password/TLS transport fields as an intentional drop. Never reinterpret an SMTP password as a Postmark server token. The new target system Postmark token is supplied separately through secure target-provisioning input during import.
- Current external-search slots -> stable system `ExternalSearchProvider` identities plus sparse library overrides.
- Publication options -> `PublicationOptionSet`/options with whole-list replacement semantics and preserved normalized option IDs. Preserve inherited versus meaningful replacement; a blank library source value means inherit/reset.
- Additional/custom patron fields -> relational `PatronCustomField`/option rows plus `MaterialFormatCustomFieldRule` rows; preserve stable option IDs/enabled/order and do not invent system defaults.
- Branding, email templates, material formats/overrides, custom formats, and auto-claim rules remain specialized records. For format behavior, do not arbitrate raw `material_formats` versus `patronFormatRules` records independently: calculate each library/format's current effective runtime rule result using the pinned resolver precedence, then represent built-in title/author/identifier/publication behavior in typed `MaterialFormat`/`MaterialFormatOverride` columns and custom-field mode/label behavior in `MaterialFormatCustomFieldRule`.
- `allowedStaffUsers` is intentionally not migrated.
- Preserve library-owned configuration distinctly from inherited overrides so **Reset inherited overrides** cannot delete it after cutover.

Any populated source setting/configuration field not accounted for by the inventory or an explicit intentional-drop rule is a migration blocker. The transform/reconciliation report must enumerate target domain counts and every correction of a known PocketBase save/inheritance quirk.

The effective system/default and library override values for `OutstandingTimeoutEnabled`, `OutstandingTimeoutDays`, `OutstandingTimeoutSendEmail`, and rejection-template selection retain **unreviewed-suggestion creation-age auto-rejection** semantics. They do not become an outstanding-purchase expiration policy. Preserve sparse inheritance and template identity/scope through the existing mappings; reconcile effective values, selected template, and the status/timestamp/outcome semantics in `01-PORTING-SPEC.md` section 23.2. External `OutstandingTimeout` queue limits affect visitation only. For the other timeout families preserve their UpdatedUtc age and AdditionalCopy's source updated-or-created fallback; do not remap them to creation age because the fair cursor uses CreatedUtc.

### 6.7 Material formats

- Resolve every migrated request/rule to a target stable MaterialFormat ID.
- Existing library format with the same code as a system format becomes the library override for that system format.
- Library-only code remains a library custom MaterialFormat.
- Any request/rule whose historical format cannot be resolved after known mappings is a migration blocker. Do not create a synthetic "legacy format" merely to allow import.
- No request-level format-label snapshot is added.

### 6.8 Auto-claim rules

Map existing source rules to target versioned rule rows. A source rule may remain **active** only when its assignee maps to a target `StaffUser` that is active and currently scope-eligible for that rule's library (same-library staff/admin or a valid active super-admin). Otherwise preserve the rule as **inactive historical configuration** and report the normalization; never substitute another assignee and never run a retroactive claim sweep. If the source assignee maps to a retained StaffUser, keep that FK even when the rule is normalized inactive. If the source assignee no longer maps at all, import the inactive rule with nullable `StaffUserId` as allowed by the target schema and preserve the unmapped source-assignee identity only in the migration transformation report. Preserve historical request `ClaimRuleId` references by mapping them to the resulting inactive/active rule when possible. Reconciliation must prove that every target active rule has a non-null, active, scope-eligible assignee.

### 6.9 Identifier-check status migration

The source `isbnCheckStatus` select permits `pending`, `found`, `not_found`, `error`, `error_max_retries`, `skipped_no_isbn`, and historical `found_in_polaris`; the target intentionally permits only `pending`, `found`, `not_found`, `skipped_no_isbn`, and `error_max_retries` (plus null). Migration must use this exhaustive map rather than relying on enum casts or implementation guesses:

| Legacy value/state | Target state | Rule |
|---|---|---|
| null/blank | null | Preserve absence; do not invent completed identifier work. |
| `pending` | `pending` | Preserve retry count/last-check state subject to target validation. |
| `found` + nonblank supporting `bibid` | `found` | Preserve the BIB ID and normalize the target identifier-found workflow tag if absent. |
| `found` without a supporting BIB ID | **block/report** | The old hourly processor could write this state. It does not satisfy the target canonical `found` invariant and must not be silently stranded outside the pending queue. Resolve the source record before final export/cutover. |
| `not_found` | `not_found` | Preserve. |
| `skipped_no_isbn` | `skipped_no_isbn` | Preserve; normalize retry count to `0`. |
| `error_max_retries` | `error_max_retries` | Preserve as terminal legacy retry exhaustion; it is recoverable through the target manual retry action. |
| `error` + blank identifier | `skipped_no_isbn` | Deterministic conversion of the old missing-identifier write path; retry count becomes `0`. |
| `error` + nonblank identifier | **block/report** | The source value is ambiguous; do not guess `not_found` or retryable failure. Resolve the source record before final export/cutover. |
| `found_in_polaris` + nonblank supporting `bibid` | `found` | Historical alias normalized to target `found`; preserve the BIB ID and normalize the target identifier-found workflow tag if absent. |
| `found_in_polaris` without a supporting BIB ID | **block/report** | Historical meaning is insufficient to establish the target `found` invariant safely. |
| any out-of-contract value | **block/report** | Never coerce an unknown business state into a valid target enum. |

For every transformed record, include the source value, target value, and reason in the deterministic transform report. Reconciliation reports source and target counts by identifier status plus blocker counts. Migration fixtures must cover every legal legacy enum value, `found` with/without supporting BIB state, both conditional branches of `error`/`found_in_polaris`, null/blank, and an invalid value. Reconciliation must assert that every imported target `found` row has a nonblank `BibId` and the canonical identifier-found tag. The offline importer does not invent a BIB ID or require a live Polaris lookup merely to repair inconsistent history; source `found` without supporting BIB state remains a blocker until resolved.

#### BIB authority and cutover

The pinned PocketBase schema has no BIB provenance field. The identifier jobs write `bibid` together with an automatic `found` result, but staff request updates can also write `bibid` and do not create a structured event that captures the current BIB value or invalidates identifier status. Status-transition events, `editedBy`, workflow tags, and free-text notes therefore cannot prove that a current BIB was staff-authoritative. The pinned source contains no durable staff-authority evidence that the importer can safely map to `BibIdStaffVerified = true`.

The importer inserts `BibIdStaffVerified` explicitly. It classifies a BIB as automation-derived only when the normalized source identifier status is `found`, the source identifier and `lastChecked` are nonblank, and `lastChecked` equals `updated`; the pinned automatic identifier write assigns both timestamps from the same time after looking up a nonblank identifier. Other nonblank BIBs without cutover risk are imported with staff authority unset and reported as ambiguous, rather than being promoted to staff-authoritative based on request status, tags, notes, or `editedBy`. The report contains deterministic per-request classifications and counts for automation-derived, safely imported ambiguous, and staff-authoritative BIBs.

Block source correction when a nonblank BIB has `pending` or `error_max_retries` identifier state, its request is `suggestion` or can be reopened from `closed` into `suggestion`, and no placed-history protection is imported. The identifier processor can replace or clear a `suggestion/pending` BIB; the staff retry action can queue an `error_max_retries` row, and reopening a closed retryable row can make it eligible too. The blocker is `bib_authority_ambiguous` and identifies the PocketBase request. Known placed-history protection makes these rows ineligible for that mutation path and remains intact through import. Purchase promotion does not consult this flag. Identifier edits clear old BIB authority before lookup, and a newly selected BIB is validated through the target staff path.

The migration report format is version 5 and semantic reconciliation compares `BibIdStaffVerified` explicitly. The target fingerprint also covers the column, so the later `reconcile` command detects any authority value changed after import. Equivalent frozen packages and fresh targets must produce byte-identical reports. No live Polaris call is used to classify or repair historical BIB authority.

### 6.10 Title request patron snapshots

Preserve historical submission snapshots as data: barcode, email/name where present, patron code, patron org, library/name, current recorded pickup branch, creation timestamps, custom fields, workflow/current processing state, etc. These are not refreshed from Polaris during migration. After import, `PreferredPickupBranchId`/`PreferredPickupBranchName` are mutable only through the dedicated validated pickup-preference workflow; they are not generic editable fields.

Preserve `LegacyId` as the current business/provenance field.

#### Placed-BIB history guard

Migration must distinguish **no dependable evidence of reaching placed state** from **known placed-state history with a known or unknown BIB**. Preserve the existing historical protection mechanism; do not manufacture a target HoldPlacementOperation, provider success, or a Polaris HoldRequestID. This marker protects source-recorded history (including source opt-out rows already in `hold_placed`); it is not a statement that an external hold was newly verified.

Normalize evidence from the frozen export **before** converting event types to target `legacy`. Resolve source `statusRef`, `closeReasonRef`, event `fromStatus`/`toStatus`/`closeReason` relations to their exported taxonomy codes and preserve the raw values/references for provenance. Accept the actual canonical codes as well as the explicit known aliases from section 6.5. Do not blindly call the source's permissive/lossy normalizers: a canonical `hold_unclaimed`, `hold_cancelled`, `hold_expired`, or `hold_not_picked_up` must not disappear merely because a source helper's alias map omits it. Unknown/dangling/conflicting business codes remain existing migration blockers, not default suggestions or empty reasons.

Let `HoldTerminalReasons` be exactly `{hold_completed, hold_not_picked_up, hold_unclaimed, hold_cancelled, hold_expired}`. Evaluate the **union of all dependable evidence**, not an exclusive branch or a literal-event-name-only test:

| Normalized source evidence | Protection/provenance |
|---|---|
| Current TitleRequest status is `hold_placed` | Protect; record the source request/status reference, even with no events. |
| Source request close reason belongs to HoldTerminalReasons | Protect; record the particular normalized reason and source field/reference. All five reasons qualify without a literal `hold_placed` event. |
| Retained dedicated source `hold_placed` event for the request | Protect; retain event ID/type and any explicit status/BIB evidence. |
| Retained state-transition evidence has `toStatus = hold_placed`, including ordinary `status_changed` | Protect regardless of the event's literal name. This includes staff existing-hold adoption and source `hold_skipped` placed-stage transitions; later manual closure does not erase it. |
| Retained transition has `fromStatus = hold_placed`, or a retained event has a hold-terminal close reason | Protect: departure from that state or a recorded hold-terminal outcome independently establishes prior placed-stage history. Preserve the exact evidence reference. |

Thus `knownPlacedHistory` is true when **any** row above applies. Best-effort event recording means an absent event is never contrary evidence: current placed state or any of the five terminal reasons remains sufficient. An existing-hold adoption followed by `manual`/silent/other closure remains protected through its status transition, not through the later close reason. Evaluate raw normalized history even when its target EventType becomes `legacy`.

Do not over-classify: being closed, having a BIB, an identifier-found tag, a pending-hold attempt, or an editable free-text note alone does not prove placed-stage history. A genuinely never-placed closed suggestion receives no marker. When all dependable placement evidence is missing (for example manual closure after an adoption whose event and every other structured trace were lost), migration cannot prove placement. Report any available conflicting/hint-only evidence as `placement_history_ambiguous` and block that unresolved case under the existing correction/reporting philosophy; do not guess success, protect every closed request, or silently treat a known ambiguity as never placed. A record with no such evidence or conflict is classified `no_placement_evidence`, not as externally certified never placed.

A null or absent derived source relation does not contradict an otherwise recognized raw canonical status/close reason. Only an actual conflicting non-null value or unresolved supplied reference triggers the corresponding normalization blocker.

**Historical BIB selection.** Collect the nonblank BIB explicitly recorded on the source request and any explicit BIB attached to the qualifying placed/terminal history. Preserve normalized IDs and source references. One distinct supported BIB becomes the marker's `bibId`; no recorded BIB means explicit null. If recorded values conflict and the protected historical BIB cannot be determined without guessing, report/block `placed_bib_conflict` for source correction, not a catalog lookup or arbitrary newest-row choice. Do not infer a BIB from title/identifier, a current Polaris search, or a hold ID. Preserve the imported request's recorded fields; a historical-event BIB can remain in the marker when the current source BIB is null. Existing inconsistent-found and required-state migration blockers still apply independently.

**One deterministic marker.** For each knownPlacedHistory request, persist or reuse one equivalent `legacy` TitleRequestEvent with existing metadata: `legacyBibProtection: true`, `bibId` (including explicit null), `transform: placed_bib_protection_v1`, `sourceTitleRequestId`, and `evidence` containing every qualifying normalized evidence reference. Each evidence entry records kind, source collection/record ID, source field, and normalized status/reason/type. Include the BIB-source references separately when known. Deduplicate exact references and sort ordinally by kind/collection/record ID/field/value; use the frozen export timestamp, not the import clock, for the annotation. Keep evidence keyed to source IDs, not nondeterministic target identity allocation. Reuse existing equivalent metadata rather than adding a duplicate marker. Ordinary original events remain preserved; the marker is not a new workflow transition or replay of placement.

The edit/reopen service tests marker presence, not `bibId != null`, the current status alone, or existence of a target operation. Protection survives reopening, commit, and a later separate edit request. Known historical BIB cannot be replaced/cleared; unknown historical BIB remains explicitly unknown and cannot be filled through identifier/BIB editing to bypass the guard. Capability flags and backend rejection use this same predicate. Current status is not reconstructed from events.

**Not runtime terminal correlation.** A protected historical BIB can be the expected title for the existing positive-checkout rule when known. It cannot identify which particular Polaris hold is currently tracked. The marker contains no invented final hold ID and creates no successful operation. Any actual source provider identifiers retained elsewhere remain archival evidence unless an authoritative current association is established under `01-PORTING-SPEC.md` section 9.2; neither RequestGUID nor a historical terminal row is promoted to that association by import. Uncorrelated terminal evidence leaves the workflow unchanged with a safe diagnostic; normal manual closure and configured timeout paths remain available. New .NET placements/adoptions instead capture identity on their real operation.

Reconciliation must explicitly count source requests evaluated; unique protected requests/markers by library and current status; known-BIB versus explicit-null protected history; each of the five terminal-reason evidence classes; dedicated events, `status_changed -> hold_placed`, other qualifying transitions/departures and event-terminal evidence; reused versus inserted equivalent metadata; no-evidence requests; and each ambiguity/conflict blocker. Evidence-class counts may overlap; unique-marker totals may not. Report exact source references and compare expected versus imported marker/BIB/provenance results, not only aggregate event counts. Repeated import of the same frozen package into equivalent fresh targets must produce identical protection/provenance counts and equivalent metadata, with no duplicate equivalent marker. Report historical protection and current terminal-hold correlation separately and prove zero fabricated HoldPlacementOperation/provider-identity rows. See `06-TESTING-CI.md` section 10.2 F3.

### 6.11 Additional copies

Import into the separate target table, preserving independent open/closed state and source snapshots. Apply section 6.3 to every open effective claim and preserve closed claimant history. Resolve source title-request relation when source exists; otherwise source may be null according to target semantics.

### 6.12 Deleted-request audit

Transform existing PocketBase deletion audit into the reduced target audit. Intentionally discard unnecessary historical full barcode, email/name, freeform notes, and other fields that the new audit does not need. Report the reduction as an expected transformation.

### 6.13 Email

- Migrate reusable Polaris credentials into `PolarisSettings`, protecting each secret with the target environment's Data Protection protector/key ring before SQL persistence so the database receives ciphertext only.
- The source SMTP transport has no credential-compatible Postmark equivalent. Do not export/import SMTP password/host/user as Postmark credentials. Record those transport fields as an intentional drop in the transformation report.
- Migrate current effective sender identity into system/library `EmailSettings` according to the target inheritance rules.
- Provision the **new system Postmark server token** as a target-only secret during import through a secure prompt or process-environment secret input, never a command-line argument, normalized export JSON, manifest, or log. Encrypt it with the target Data Protection protector before inserting `EmailSettings`. Permanent nonproduction and production use their own supplied token/input even if the underlying Postmark server is intentionally shared. Library-specific Postmark-token overrides have no source equivalent and are configured later through the normal settings UI if needed.
- Production reconciliation/cutover requires a usable effective system Postmark token before background processing or production traffic is enabled.
- Migrate historical delivery/audit metadata.
- Do not create pending outbox rows from historical mail; outbox begins empty.
- `RecipientAddressKind` is target-only outbox state and requires no legacy-row transformation because the outbox begins empty.
- Do not resend historical messages.

### 6.14 Branding/files

Export actual current logo bytes from PocketBase file storage, not just database filenames. Apply target image validation during preflight/import. A legacy SVG logo (target does not support SVG) must be resolved before production cutover rather than silently imported as unsupported content.

### 6.15 Sessions and job infrastructure

Do not migrate:

- PocketBase auth/session tokens;
- patron bearer sessions;
- Entra/OIDC cookies;
- PocketBase cron scheduler infrastructure;
- Hangfire internal state.

Do migrate meaningful per-request processing state such as identifier check status/result/retries and promoter/check timestamps where the state still affects workflow semantics.

New `QueueProgress` rows begin absent/empty; the first target invocation starts a finite cycle from the current imported rows. Do not import the legacy in-memory cursor. New hold-operation storage begins empty because no trustworthy target execution journal exists in PocketBase; retain only source workflow/placed-BIB history under section 6.10.

### 6.16 External operational configuration parity

External cron/queue controls are **not SQL settings**, but their effective production values must survive cutover deliberately. During rehearsal and final export, freeze `effective-legacy-operational-config.json` from the same environment used by the stopped PocketBase process. Capture effective values **and provenance** for:

- `ASAP_CRON_SCHEDULE`, `ASAP_ORG_SYNC_CRON_SCHEDULE`, `ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE`, and `ASAP_ISBN_CHECK_CRON_SCHEDULE` including code defaults when unset;
- global `ASAP_JOB_PAGE_SIZE` / `ASAP_JOB_MAX_PER_RUN`;
- timeout-family `ASAP_TIMEOUT_PAGE_SIZE` / `ASAP_TIMEOUT_MAX_PER_RUN`;
- every actually configured `ASAP_<QUEUE>_PAGE_SIZE` / `ASAP_<QUEUE>_MAX_PER_RUN` override and the effective result for each known queue.

The target external JSON uses `Hangfire:Schedules` plus `Hangfire:ProcessingLimits`. Processing-limit precedence is queue-specific -> timeout-family -> global default, with the same default/range contract as the pinned source. Map source queues to target logical queues explicitly. The canonical `IdentifierProcessing` queue maps from the dedicated five-minute source queue `pending_suggestion_isbn_checks`. Because `pending_isbn_checks` belongs to the retired hourly ISBN implementation, any explicit source override for that queue is retained in the operational snapshot and must produce a transform/reconciliation entry; it cannot disappear silently. The default resolution is to retain the dedicated five-minute queue's effective target limits and mark the obsolete hourly-only override as intentionally retired unless an operator deliberately chooses a different target limit after reviewing production throughput.

`examples/EffectiveLegacyOperationalConfig.example.json` defines the snapshot shape. Before Hangfire recurring jobs are enabled, reconciliation must prove that target effective schedules and processing limits match the mapped source values or an explicitly documented intentional change. Target-only outbox/session/payload cleanup schedules have no legacy parity requirement but must match this pack's defaults/configuration.

## 7. Legacy deep links

Maintain generic `LegacyPocketBaseMapping` entries for old request IDs needed by staff links/bookmarks/email history. During the temporary compatibility period:

1. accept old PocketBase request ID in the existing deep-link parameter;
2. resolve to a new invariant-decimal bigint ID using the type-qualified mapping (`title_request` or `additional_copy`) when needed;
3. open the target request, then apply the ordinary current-staff eligibility and library-scope checks;
4. normalize the browser URL to the new ID as a string while preserving supported `stage`/`status`, other navigation parameters, and the hash.

Resolution first checks whether the supplied value is an existing current target ID, and that current target ID wins. The type-qualified mapping (`title_request` or `additional_copy`) is used only as a fallback when no current target exists. Mapping is translation only and never grants authorization. Unknown, deleted, and out-of-scope targets have the same non-disclosing not-found behavior. Independently, browser code must keep request IDs as strings and must not coerce them through JavaScript `Number`, including numeric-looking legacy IDs.

Mapping cleanup is a reviewed, explicit operator action after the reference window. Keep both request-type keys distinct when the same legacy ID text appears in both types, and do not tie cleanup to an automatic purge timer.

## 8. Reconciliation is a hard gate

Migration passes only when **every difference is understood**.

System/global runtime-fallback reconciliation is part of this gate: compare every SQL-bound value represented by `effective-legacy-runtime-config.json` against the target and explicitly verify that a blank persisted Staff URL resolved from an environment fallback arrives in `SystemSettings.StaffApplicationUrl` correctly. Separately, compare `effective-legacy-operational-config.json` against target external schedules/processing limits before enabling Hangfire.

Required reconciliation should include at least:

- entity counts by type and library;
- request counts by status/library/material format plus source/target counts for every identifier-check status, including `found` rows with/without supporting BIB state, deterministic `error`/`found_in_polaris` transforms, canonical found-tag normalization, and any blocking ambiguous rows;
- additional-copy counts by status/library;
- tag associations;
- event counts and legacy-event transformations;
- StaffUser normalized-email uniqueness and role/org invariants, including active/inactive StaffUsers legitimately referencing inactive organizations; explicitly prove at least one active email-authenticated super-admin exists and report any migration-time bootstrap promotion/insertion;
- StaffUser recipient mapping/source (`NotificationEmail`), independent weekly-summary override, preference values, and a per-user old-versus-target recipient/eligibility delta report covering newly eligible weekly summaries, changed ordinary-notification recipients, and target-null recipients;
- auto-claim rule normalization counts and details, including every source-active rule imported inactive because the assignee was missing, inactive, or scope-ineligible; prove every target active rule has a valid active scope-eligible StaffUser and every historical request rule reference maps deterministically where possible;
- organization participation/inactive history rows;
- format/rule references;
- claimant mapping/clearing report;
- per-domain settings/configuration counts and effective-value spot checks, including whole-set inherited/replacement/reset states;
- email template/config counts;
- branding presence/hash where applicable;
- historical delivery counts;
- deletion-audit transformed counts;
- null/FK/invariant checks;
- source created/updated timestamp preservation checks where required;
- exported asset/hash validation;
- explicit list/count of every intentional data reduction.

No unexplained mismatch is acceptable. "Harmless-looking" discrepancies block cutover until understood.
### Operational-claim and placement reconciliation gates

For both TitleRequest and AdditionalCopyRequest, verify every mapped effective open claimant satisfies section 6.3, every cleared claim has exactly one deterministic history/report conversion, eligible claims retain their original attribution, and closed claims remain historical. Exercise inactive organizations without clearing otherwise valid relationships. Reconcile every placement-evidence class, known/null BIB, deterministic marker/provenance, duplicate prevention, and unresolved ambiguity count from section 6.10; verify no synthetic completed hold operations or inferred provider IDs. Protected legacy history and current terminal-hold correlation have separate results. These checks are independent of active auto-claim-rule reconciliation.

First-cutover schema preparation remains offline/fresh-target work. Classify any change to an **existing** target database, including Hangfire-owned DDL, under `05-DEPLOYMENT-OPERATIONS.md` section 9; no unchanged-DACPAC exception bypasses backup/quiescence for dependency schema changes. Do not enable the web application/Hangfire until both schema compatibility checks, current usable-super-admin gate, and reconciliation pass.


## 9. Rehearsals

### 9.1 During development

Use real PocketBase backups against disposable SQL targets to exercise migration. Delete sensitive rehearsal exports/databases when no longer needed.

### 9.2 Permanent nonproduction

Permanent nonproduction stays on PocketBase until the .NET implementation is complete and merged. The final full rehearsal occurs only after the intended production version tag is created from `main`:

- use the accumulated nonproduction PocketBase database as source;
- run the exact same old-server export -> transfer -> new-server import/reconcile process;
- deploy the exact intended production tagged artifact;
- retain the resulting nonproduction SQL database and upgrade it release-over-release afterward.

Do not seed permanent nonproduction from production data.

### 9.3 Emergency PocketBase hotfix after the .NET merge

PocketBase production can remain authoritative between the .NET merge and final cutover. If a critical production fix is required in that interval:

1. Record the exact PocketBase commit currently deployed and create a temporary branch and/or tag from that commit. Do not maintain a permanent PocketBase branch.
2. Develop, test, and deploy the PocketBase correction through the normal emergency process.
3. Immediately port the equivalent behavioral correction into .NET `main`, create a replacement .NET version tag/artifact, and repeat the required exact-artifact permanent-nonproduction rehearsal.
4. If the PocketBase correction changes its schema, stored-data semantics, migration input, or any behavior assumed by export/import/reconciliation, update this migration contract and the tooling before repeating the full rehearsal.
5. Before the production maintenance window, record the exact PocketBase commit actually running and verify that the rehearsed .NET artifact and migration tooling account for it.
6. After successful .NET cutover, create/verify the permanent final-PocketBase tag at that exact frozen commit. Temporary hotfix branches may then be removed.

## 10. Production preflight database isolation

Production-hostname preflight must **never** use the final SQL database that will receive the PocketBase import. Before the maintenance window:

1. Create a disposable production-preflight database (for example `Asap_Preflight_<version>`).
2. Deploy the exact release DACPAC to it and point the staged IIS application at it through a temporary preflight external config.
3. Allow normal bootstrap/runtime writes there as needed to validate real-hostname TLS, Entra sign-in/callback/cookies, antiforgery, static assets, deep links, health, SQL connectivity, and authenticated diagnostics.
4. Stop the app pool when preflight is complete.
5. Restore the real production external config that points at the final production database, then drop/destroy the disposable preflight database.
6. Create/reset the final production database to the defined fresh migration-target state and do **not** start `Asap.Web` against it before import/reconciliation.

The final migration target therefore cannot contain bootstrap StaffUser/default runtime rows created by staging. The initial production deployment/cutover path must support preparing DACPAC/files while leaving the app pool stopped until migration is complete.

## 11. Production cutover runbook

### Prerequisites, completed before the window

- New production Windows/IIS server already provisioned.
- Dedicated ASAP IIS site/app pool/service account configured.
- .NET 10 Hosting Bundle installed.
- Production hostname TLS certificate/binding staged.
- External production config file installed and ACLed.
- Environment-specific Data Protection key directory and X.509 key-encryption certificate provisioned/ACLed; the certificate/private key is recoverable on a replacement IIS host and included with the key ring in the documented protected backup/recovery plan because persisted SQL integration secrets depend on them.
- Production SQL database/logins/permissions provisioned.
- SQL backup destination configured on the SQL Server's local storage.
- Tagged release + self-contained `win-x64` migration artifact staged on respective hosts.
- Active staff source emails validated as real and case-insensitively unique.
- Production Postmark server token is available to the migration operator through the approved secure input mechanism; it is not stored in the migration export package or command line.
- New server validated using temporary/internal access and a workstation hosts-file override for the **real production hostname**, backed only by the disposable preflight SQL database/config.
- Entra redirect, normalized-email StaffUser matching, allowed-tenant enforcement, cookies, CSP, deep links, SQL, health, and static assets verified under that production hostname.
- Disposable preflight database removed; final production config restored; final target SQL database recreated/reset to the defined fresh migration-target state; app pool remains stopped against that final target.
- Final nonproduction rehearsal passed on the exact artifact.
- Exact PocketBase production commit recorded; every post-merge emergency fix is represented in .NET and, where relevant, the migration tooling used by the passing rehearsal.

### Maintenance window

1. Announce/start the planned outage by taking the old application out of normal service as operationally appropriate.
2. Record the exact deployed PocketBase commit, then stop PocketBase application writes and all PocketBase background jobs.
3. Take/verify final PocketBase backup/snapshot.
4. Run final `Asap.Migration export` on the old server against the stopped DB + file storage with the same effective legacy environment inputs; freeze both `effective-legacy-runtime-config.json` (system/global SQL-bound fallback values only) and `effective-legacy-operational-config.json` (cron/queue limits), and fail export if required effective values cannot be resolved.
5. Validate export manifest/hashes/counts.
6. Transfer normalized package to the new server through the trusted path.
7. Verify the final target SQL database is in the expected fresh migration-target state and has never been used for preflight.
8. Use the deployment script's migration-cutover preparation mode (or equivalent explicit steps) to deploy/verify the exact DACPAC and staged web files **without starting the app pool/bootstrap**. Classify application and Hangfire/dependency schema changes under the normal backup/quiescence/compatibility rules; a truly fresh first-cutover DB alone may use the documented no-prior-dataset exception.
9. Run migration import in dependency order with no staff identity-map input, supplying the target system Postmark token through the secure target-provisioning input; verify the token is persisted only as Data Protection ciphertext.
10. Run the active email-authenticated super-admin cutover gate. If none exists, use the migration-time bootstrap-email promotion/insertion procedure, record it, and re-run the gate.
11. Run full reconciliation, including normalized staff-email uniqueness/validity, null initial Entra metadata, recipient/preference mappings, notification deltas, claimant eligibility, effective email configuration, identifier transformations, and operational-config parity.
12. Start the app pool only after import/reconciliation and the usable-super-admin gate succeed.
13. If all gates pass, switch production hostname/DNS to the new server.
14. Perform production smoke tests, including patron and staff critical paths.
15. Allow normal production use; from the first accepted .NET production write onward, SQL/.NET is authoritative.
16. Create/verify the permanent final-PocketBase historical tag so it points to the exact commit recorded and frozen for this successful cutover.
17. Delete sensitive normalized migration packages from both servers after successful validation.

The design favors a simple full offline maintenance window. Do not build delta synchronization/prestaging complexity solely to reduce minutes of downtime.

## 12. Rollback / point of no return

### Before .NET accepts production writes

If export/import/reconciliation/new-server validation fails, abort the cutover and restart the unchanged PocketBase application. Correct the problem, produce/rehearse a replacement artifact if code changed, and schedule another attempt.

### After .NET accepts production writes

The .NET/SQL system is authoritative. Recovery is repair-forward or restore/repair according to the SQL/app failure, **not** a switch back to PocketBase, because reverse synchronization does not exist.

This point-of-no-return must be explicit in the production runbook.

## 13. Post-cutover PocketBase retention

- The retired production PocketBase deployment remains stopped and must never be started as-is after .NET has accepted production writes. Its executable, data, configuration, and backup material are retained for approximately 30 days only for forensic/reference use.
- Prefer direct inspection of the retained SQLite database, files, logs, and configuration without executing PocketBase.
- If running PocketBase is genuinely necessary for investigation, create a separate isolated copy. Before startup, block all outbound Polaris and email access and disable registration/execution of every recurring production job, including hold/workflow processing, ISBN processing, organization sync, and weekly email. Both isolation controls are required.
- The retained deployment or isolated copy must never become a parallel read/write service, a production fallback, or a source that can mutate external systems or send mail.
- Retain the final PocketBase backup/tag indefinitely according to source/backup policies.
- After the reference period, remove the runnable old deployment deliberately; do not automate deletion as part of the cutover.
