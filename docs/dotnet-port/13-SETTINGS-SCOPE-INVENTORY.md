# ASAP Settings Scope and Storage Inventory

**Status:** Normative implementation contract  
**Applies to:** .NET port configuration, SQL design, migration, API behavior, settings UX, reset behavior, and tests

## 1. Purpose and precedence

This inventory defines how configuration in the current PocketBase ASAP application maps into the .NET implementation. It is intentionally more specific than the general architecture documents.

For **settings scope, inheritance, persistence destination, reset semantics, and migration mapping**, this document is normative. If a more general statement elsewhere in this porting pack conflicts with this inventory, this document controls.

The target does **not** use one generic or catch-all `Settings` table. Configuration is split by business domain so system-only data cannot accidentally become library-overridable, inheritance is explicit only where it actually applies, security-sensitive integrations are isolated, and structured configuration remains relational.

## 2. Scope model

| Scope | Meaning | Editing |
|---|---|---|
| **System-only** | One value controls the entire ASAP installation. It is not a library default and cannot be overridden. | Super-admin only |
| **System default + library override** | CLC establishes the default. A library stores a value only when it needs to differ. | Super-admin for system; library admin for own library |
| **Whole-set system default + library replacement** | The system owns an ordered/list/set default. Absence of a library set means inherit the whole system set; a meaningful library set replaces it completely. To preserve current behavior, a blank/empty library value means reset/inherit rather than “override to empty.” | Super-admin for system; library admin for own library |
| **Library-only** | The configuration inherently belongs to a particular library and has no meaningful system version. | Library admin for own library; super-admin for any library |
| **Not a setting** | Administrative records, environment configuration, operational state, or other first-class data. | Managed through the appropriate administration/operations surface |

`OrganizationId = 1` is the real Polaris system organization and is the target system/default scope. Do not introduce `NULL`, `-1`, or another synthetic scope sentinel for configuration ownership.

For inheritable scalar fields, a library override must remain distinguishable from an inherited value. Resetting an individual override means setting/removing the library value so the current system value becomes effective; never copy the current system value into the library record merely to simulate inheritance. For ordinary overrideable text fields, trim input and normalize a blank library value to `NULL`/no override when current behavior treats blank as fallback. Secret-edit forms are different: a blank submitted secret means preserve the existing secret; removing a library secret override requires an explicit **Use system default/Clear override** action.

## 3. Target configuration tables

### 3.1 System-only tables

`[asap].[SystemSettings]` contains non-integration application settings that can exist only at system organization 1. `[asap].[PolarisSettings]` contains the system-only Polaris integration configuration. Both are constrained to `OrganizationId = 1`.

The patron embed CSP origin allowlist uses `[asap].[PatronEmbedAllowedOrigin]` rows rather than a delimited/JSON setting, with normalized unique origins and system-only ownership.

### 3.2 Inheritable scalar tables

These tables use the same simple pattern:

- system/default row: `OrganizationId = 1`, containing complete configured defaults;
- optional library row: `OrganizationId = <library>`, containing only values that library overrides;
- nullable library field: inherit that field from organization 1;
- if a library row has no remaining overrides, it may be deleted;
- effective-value resolution is always library override -> system configured value -> seeded application default where a seed is defined;
- ordinary library text values that normalize to blank are stored as `NULL`/no override where current behavior treats blank as fallback; explicit blank-as-a-value behavior is not added during the port.

The tables are:

- `[asap].[WorkflowSettings]` — limits, eligibility, automation, timeout behavior, and common-creator presentation/behavior that belongs to those workflows;
- `[asap].[PatronSettings]` — patron-facing text/messages and duplicate-request status labels;
- `[asap].[EmailSettings]` — Postmark transport/sender configuration with system + library inheritance; reusable secret values are Data Protection ciphertext.

### 3.3 Whole-set replacement tables

Some settings are not merged field-by-field. The existence of a library-owned set is itself the override marker:

- `[asap].[PublicationOptionSet]` + `[asap].[PublicationOption]`;
- `[asap].[CommonCreatorSet]` + `[asap].[CommonCreatorTerm]` for the common-author/creator list itself;
- `[asap].[PatronCodeEligibilitySet]` + `[asap].[PatronCodeEligibilityMember]` for allowed Polaris patron-code IDs.

For each domain, organization 1 owns the system set. If a library has no set row, it inherits the complete system set. If it has a meaningful set row, that set is authoritative. At library scope, clearing all values removes the set and resumes inheritance; do not introduce an “override to empty” behavior during the port. Resetting the override deletes the library set and its children.

### 3.4 Other specialized relational configuration

Keep these as domain models rather than flattening them into scalar settings:

- `[asap].[ExternalSearchProvider]` + `[asap].[ExternalSearchProviderOverride]`;
- `[asap].[PatronCustomField]` + `[asap].[PatronCustomFieldOption]` + `[asap].[MaterialFormatCustomFieldRule]`;
- `[asap].[MaterialFormat]` + `[asap].[MaterialFormatOverride]`;
- `[asap].[FormatAutoClaimRule]`;
- `[asap].[EmailTemplate]`;
- `[asap].[Branding]`.

Do not add an EAV `SettingKey/Value` table or a whole-configuration JSON document. ASAP has a known, strongly typed configuration surface and should retain SQL types, constraints, FKs, readable queries, and compile-time model names.

## 4. Settings inventory

| Area | Current PocketBase fields / concepts | Target scope | Target .NET destination / rule |
|---|---|---|---|
| **Library participation** | `system_settings.enabledLibraries`; `polaris_organizations.enabledForPatrons` | **System-only administration** | **Not a settings table.** `Organization.IsActive`. Activation immediately uses system defaults. Deactivation blocks staff authorization and every patron bearer request, transactionally revokes current patron sessions, and serializes against final session issuance without rewriting `StaffUser.IsActive`; revoked sessions stay revoked after reactivation. Staff relationships remain valid for lifecycle/history. Participation-dependent identifier/workflow/manual-run work locks/re-reads the owning Organization immediately before its final local result or external-operation acquisition; deactivation-first blocks that later commit. System/infrastructure jobs and recovery/completion of already-acquired holds continue; inactive libraries cannot acquire a new hold operation. Already-committed immutable business-event outbox rows may drain, while authorization-sensitive staff rows revalidate the original tuple against current binding, loaded tenant trust, and staff/library scope and suppress when authorization is lost. |
| **Legacy staff allowlist** | `system_settings.allowedStaffUsers` | **Remove** | Do not port. Entra authenticates and `StaffUser` authorizes. |
| **Staff application URL** | `staffUrl` | **System-only** | `SystemSettings.StaffApplicationUrl`. |
| **Leap BIB URL pattern** | `leapBibUrlPattern` | **System-only** | `SystemSettings.LeapBibUrlPattern`. |
| **Leap patron URL pattern** | `leapPatronUrlPattern` | **System-only** | `SystemSettings.LeapPatronUrlPattern`. |
| **Material-type icon URL pattern** | `formatIconUrlPattern` | **System-only** | `SystemSettings.MaterialTypeIconUrlPattern`. Verify the current PocketBase save-path gap; do not reproduce it. |
| **Patron embed origins** | `patronEmbedAllowedOrigins` | **System-only** | `PatronEmbedAllowedOrigin` rows. Security boundary; super-admin only. |
| **Nonparticipating-library message** | `systemNotEnabledMessage` | **System-only** | `SystemSettings.SystemNotEnabledMessage`. |
| **Misconfiguration message** | `misconfiguredMessage` | **System-only** | `SystemSettings.MisconfiguredMessage`. Treat the current save-path gap as a defect, not intended behavior. |
| **Polaris integration** | `host`, `accessId`, `apiKey`, `staffDomain`, `adminUser`, `adminPassword`, `overridePassword`, `workstationId`, fixed/default IDs | **System-only** | `PolarisSettings`. Keep only fields required by `Clc.Polaris.Api` and the target system/application credential model. Remove legacy per-staff authentication fields. Reusable secrets are protected ciphertext. |
| **Email transport** | current `smtp_settings` | **System default + library override in target** | `EmailSettings`; replace SMTP with approved Postmark configuration. SMTP host/port/user/password/TLS do not map to Postmark credentials and are intentionally dropped. The target Postmark token is separately provisioned as a new secret. Missing effective sender/transport configuration is a notification-state concern, not a business-transaction veto: notification intent is terminally suppressed when required configuration is absent at creation, or a previously valid queued row becomes retryable `failed/mail_not_configured` if transport configuration disappears before delivery. |
| **Email sender identity** | `fromAddress`, `fromName` | **System default + library override** | `EmailSettings.FromAddress` / `FromName`; do not duplicate sender configuration on every template. |
| **Weekly suggestion limit** | `suggestionLimit`, `suggestionLimitMessage` | **System default + library override** | `WorkflowSettings`. |
| **Unreviewed-suggestion timeout / auto-reject (`OutstandingTimeout`)** | `outstandingTimeoutEnabled`, `outstandingTimeoutDays`, `outstandingTimeoutSendEmail`, `outstandingTimeoutRejectionTemplate` | **System default + library override** | `WorkflowSettings`: eligible status `suggestion`, age from `CreatedUtc`, strict threshold, result `closed/rejected`; effective send-email switch/template govern the auto-rejection notification. No `outstanding_purchase` expiry. Template selection references target `EmailTemplate` identity with scope validation; full predicate in porting-spec section 23.2. |
| **Unpicked-up hold timeout** | `holdPickupTimeoutEnabled`, `holdPickupTimeoutDays` | **System default + library override** | `WorkflowSettings`: `hold_placed`, age `UpdatedUtc`, result `closed/hold_not_picked_up`, no timeout email. See porting-spec section 23.2 for common strict-clock/participation checks. |
| **Pending-hold timeout** | `pendingHoldTimeoutEnabled`, `pendingHoldTimeoutDays` | **System default + library override** | `WorkflowSettings`: `pending_hold`, age `UpdatedUtc`, result `closed/rejected`, no timeout email. See porting-spec section 23.2 for common strict-clock/participation checks. |
| **Additional-copy timeout** | `additionalCopyTimeoutEnabled`, `additionalCopyTimeoutDays` | **System default + library override** | `WorkflowSettings`: independent task `open`, age `UpdatedUtc` (source missing-updated fallback to `CreatedUtc`), close task with normal closure time/note; no TitleRequest reason or timeout email. See porting-spec section 23.2 for common strict-clock/participation checks. |
| **Automatic purchase promotion** | `autoPromote` | **System default + library override** | `WorkflowSettings.AutoPromote`. |
| **Popular/common creators - behavior/text** | `commonAuthorsEnabled`, `commonAuthorsLabel`, `commonAuthorsHelp`, `commonAuthorsMessage` | **System default + library override** | `WorkflowSettings`; each field inherits independently. |
| **Popular/common creators - list** | `commonAuthorsList` | **Whole-set system default + library replacement** | `CommonCreatorSet` + `CommonCreatorTerm`. No library set means inherit system list; library set means complete replacement. |
| **Patron auto-hold opt-out** | `allowPatronAutoholdOptOut` | **System default + library override** | `WorkflowSettings`. |
| **Allow any registered card** | `allowAnyRegisteredCardLogin` | **System default + library override** | `WorkflowSettings`. |
| **Patron-code eligibility behavior/text** | `patronCodeEligibilityEnabled`, `patronCodeEligibilityMessage` | **System default + library override** | `WorkflowSettings`. |
| **Allowed patron codes** | `allowedPatronCodeIds` | **Whole-set system default + library replacement** | `PatronCodeEligibilitySet` + `PatronCodeEligibilityMember`. Polaris patron codes remain live reference data; validate IDs at save/use without turning the cache into permanent business rows. |
| **External search links** | providers 1-4: `externalSearchNEnabled`, `externalSearchNLabel`, `externalSearchNUrlTemplate` | **System definitions + library field overrides** | `ExternalSearchProvider` system rows plus sparse `ExternalSearchProviderOverride` rows. Seed/migrate the current provider identities; library overrides cannot create a new provider identity in the initial port. |
| **Patron page text** | `pageTitle`, `barcodeLabel`, `pinLabel`, `loginPrompt`, `loginNote`, `suggestionFormNote`, `noEmailMessage`, `successTitle`, `successMessage`, `alreadySubmittedMessage` | **System default + library override** | `PatronSettings`; true field-by-field inheritance rather than current record-level fallback quirks. |
| **Duplicate-request status labels** | `suggestion`, `outstanding_purchase`, `pending_hold`, `hold_placed`, `closed`, `rejected`, `hold_completed`, `hold_not_picked_up`, `manual`, `silent` | **System default + library override** | Strongly typed `PatronSettings` columns for the fixed taxonomy. |
| **eBook / eAudiobook patron messages** | `ebookMessage`, `eaudiobookMessage` | **System default + library override** | Preserve as explicit `PatronSettings.EbookMessage` / `EaudiobookMessage` during the port and migrate stored values. Any later consolidation with format behavior is deferred product work. |
| **Publication timing options** | `publicationOptions` | **Whole-set system default + library replacement** | `PublicationOptionSet` + `PublicationOption`, preserving the current stable/normalized option ID as `OptionKey`. Do not merge system/library items. |
| **Additional/custom patron fields** | `additionalFieldDefinitions` | **Library-only** | `PatronCustomField` + `PatronCustomFieldOption`. Preserve text/textarea/select types, ordering, help text, enabled state, stable option IDs, option enabled state, and option ordering. No system-level custom fields in the initial port. |
| **Material formats** | `material_formats` | **System formats + library overrides + library custom formats** | `MaterialFormat` + `MaterialFormatOverride`; no generic settings storage. |
| **Built-in format field behavior** | `titleMode`, `titleLabel`, `authorMode`, `authorLabel`, `identifierMode`, `identifierLabel`, `publicationMode`, `publicationLabel`, `messageBehavior`; legacy `patronFormatRules` overlap | **System default + library format override** | Strongly typed columns on `MaterialFormat` / `MaterialFormatOverride`; remove duplicated JSON storage for the built-in fields. |
| **Custom-field behavior by format** | `patronFormatRules[format].customFields[fieldKey]` (`mode`, optional label override) | **Library-only** | `MaterialFormatCustomFieldRule` linking `PatronCustomField` to a system/library material format. Preserve required/optional/hidden mode and optional label override. |
| **Format auto-claim assignment** | `format_claim_rules` / `formatClaimRules` | **Library-only** | Versioned `FormatAutoClaimRule`; no inheritance. |
| **Built-in workflow email templates** | built-in keys with subject/body | **System default + library override** | `EmailTemplate`. |
| **Rejection templates** | named template collection | **System templates + library override/hide/custom** | `EmailTemplate` with source/lineage relationship and existing override/hide/custom semantics. |
| **Logo** | `ui_settings.logo` | **System default + library override** | `Branding`; library row/field inherits when absent. |
| **Logo alternate text** | `ui_settings.logoAlt` | **System default + library override** | `Branding.LogoAltText`; image and alt-text inheritance remain independent. |
| **Staff accounts** | `staff_users`, role, library association, active state | **Not a library setting** | `StaffUser`. |
| **Staff notification recipient / weekly summary preference** | `staff_users.email`, `weekly_action_summary_enabled`, `weekly_action_summary_email` | **User/profile data, not a setting** | `StaffUser.NotificationEmail` is the nullable target primary notification address; `WeeklyActionSummaryEmail` is an optional weekly-only override and falls back to primary when blank. This fallback is an intentional behavior normalization and may make migrated summary-enabled users newly eligible. Administrators may clear primary email; Entra sign-in never repopulates it. Preserve the summary-enabled preference. Authorization-sensitive queued staff mail records the recipient StaffUser/scope and immediately before delivery/retry requires the current effective app-owned address to still equal the snapshot; a clear/change therefore suppresses the stale queued row rather than sending it. Migration precedence, recipient-delta reporting, and `@staff.asap.local` rejection are normative in `04-MIGRATION-CUTOVER.md`. |
| **Purchase reminder default** | `purchase_reminder_default` | **User preference** | `StaffUser.PurchaseReminderDefault`; migrate exactly and preserve workflow default behavior. |
| **Additional-copy reminder default** | `additional_copy_reminder_default` | **User preference** | `StaffUser.AdditionalCopyReminderDefault`; migrate exactly and preserve workflow default behavior. |
| **Mine/unclaimed queue default** | `default_mine_unclaimed_filter` | **User preference** | `StaffUser.DefaultMineUnclaimedFilter`; migrate exactly and preserve queue default behavior. |
| **Organization/patron-code sync status** | sync status/message/error/last-synced values | **Not a business setting** | Diagnostics/reference-cache operational state and logs; do not expose as inheritable configuration. |
| **Job schedules and processing limits** | environment-driven schedules/page sizes/max-per-run | **Environment/system operational config** | External `Hangfire:Schedules` / `Hangfire:ProcessingLimits`, not SQL settings. Preserve effective legacy values through `effective-legacy-operational-config.json`; processing-limit precedence is queue-specific -> timeout-family -> global default. |
| **Connection strings, Entra configuration, bootstrap identity, filesystem paths, nonproduction safety controls** | environment values | **Environment-only** | External ACLed JSON. Never expose as library settings. |

Migration note: `effective-legacy-runtime-config.json` is intentionally limited to **system/global SQL-bound values that require runtime fallback resolution**; library-scoped values are reconciled through their organization-aware domain exports. Record persisted/environment/code-default provenance for the system/global fallback values. For `StaffApplicationUrl`, preserve the pinned resolver behavior around persisted `staffUrl`, `ASAP_STAFF_URL`, `ASAP_PUBLIC_URL`, and the separate initialization helper's `ASAP_BASE_URL` fallback. External cron/processing-limit parity is a separate contract in `effective-legacy-operational-config.json`.

## 5. Inheritance rules

### 5.1 Scalar domains

`WorkflowSettings`, `PatronSettings`, and `EmailSettings` resolve each field independently. A partial library row does not cut off fallback for its other fields.

The system row is required and should be seeded if missing without overwriting administrator edits. Readiness/diagnostics must report missing required system configuration rather than silently manufacturing operational secrets.

### 5.2 Whole-set domains

`PublicationOptionSet`, `CommonCreatorSet`, and `PatronCodeEligibilitySet` use row-existence semantics:

- no library set row -> inherit the system set;
- library set row present -> use only that library set;
- blank/empty library input is normalized to no library set -> inherit, preserving current behavior;
- reset -> delete the library set and children.

### 5.3 Material formats

System formats are durable identities. `MaterialFormatOverride` contains nullable library-specific changes to system formats. A library-created format is its own `MaterialFormat` owned by that library. Built-in field behavior is represented by strongly typed columns rather than a JSON rule blob.

`MaterialFormatCustomFieldRule` is library-owned. Absence of a rule for a custom-field/format pair means that custom field is hidden for that format; an absent `LabelOverride` uses the custom-field definition label. Disabled custom fields remain effectively hidden.

### 5.4 Email templates

Built-in/system templates remain system identities. Library rows may override/hide a system template through lineage/source identity. Library-created rejection templates are library-owned records and are not inherited overrides.

### 5.5 Branding

Logo image and logo alt text inherit independently. Clearing only the image override does not implicitly clear alt text, and vice versa.

## 6. Reset behavior

The target supports individual **Use system default** actions and a library-level **Reset inherited overrides** operation.

An individual reset removes only the selected override.

**Reset inherited overrides** removes only configuration whose semantics are inheritance from organization 1:

- nullable library values/rows in `WorkflowSettings`, `PatronSettings`, and `EmailSettings`;
- library `ExternalSearchProviderOverride` rows;
- library `PublicationOptionSet` and children;
- library `CommonCreatorSet` and children;
- library `PatronCodeEligibilitySet` and members;
- `MaterialFormatOverride` rows for system formats;
- library overrides/hides of system `EmailTemplate` rows;
- library `Branding` overrides.

It **must not delete library-owned configuration**, including:

- library custom `MaterialFormat` rows;
- library custom rejection templates;
- `PatronCustomField` definitions/options and `MaterialFormatCustomFieldRule` rows;
- `FormatAutoClaimRule` history or active rules;
- `Organization`, `StaffUser`, requests, events, audit/history, or any other business records.

Do not overload this operation into a destructive "reset the library to factory state" command. If such an operation is ever needed, design it separately with explicit destructive confirmation.

After an inherited-override reset, effective configuration immediately resolves from the current system defaults.

## 7. Current PocketBase quirks that should not be blindly ported

**Patron UI text inheritance:** current `uiRecord()` can select a library `ui_settings` record as a whole, causing blank fields on a partial library record to fall back to hard-coded defaults rather than configured system values. The target field-by-field `PatronSettings` resolver corrects this.

**`misconfiguredMessage`:** runtime consumes the system value, but the current save path does not appear to persist it consistently. Treat this as a current save-path defect rather than intended behavior.

**`formatIconUrlPattern`:** current UI/server code has a save-path mismatch. Verify actual stored data during migration and support the field normally in `SystemSettings`.

**`ebookMessage` / `eaudiobookMessage`:** schema/runtime support scoped values even though the primary settings serializer does not consistently expose them. Preserve existing data and keep both values directly editable in `PatronSettings` for the initial port.

**`patronFormatRules`:** current PocketBase storage overlaps with material-format configuration. Consolidate built-in title/author/identifier/publication behavior into strongly typed `MaterialFormat` / `MaterialFormatOverride` columns, and map custom-field mode/label rules to `MaterialFormatCustomFieldRule`.

**`allowedStaffUsers`:** legacy configuration is superseded by Entra authentication plus local `StaffUser` authorization and is not migrated.

**Transport versus sender configuration:** current SMTP transport is global while sender values can effectively be scoped through template rows. Target Postmark transport/sender settings use `EmailSettings`; templates contain content, not redundant transport/sender configuration.

## 8. Migration requirements

The PocketBase export may use target-oriented files rather than reproducing source collection names. It must preserve enough source provenance to diagnose transformations.

Import must explicitly populate/reconcile the target settings domains rather than inserting one generic settings document. Use **effective current system behavior** to produce complete system/default rows: where the PocketBase system field is absent/blank and current runtime supplies a hard-coded default, migrate that effective default rather than leaving the target system row accidentally incomplete. For library scope, only meaningful current overrides become nullable override values/rows unless this document explicitly defines an intentional behavior correction.

In particular:

- transform global/system fields into `SystemSettings` and `PolarisSettings`;
- transform workflow scalar defaults/overrides into `WorkflowSettings`;
- transform patron text/status/eBook messages into `PatronSettings` with corrected field-level inheritance;
- transform only semantically equivalent SMTP-era configuration: move effective sender identity into Postmark-oriented `EmailSettings` and template content into `EmailTemplate`; intentionally drop legacy SMTP host/port/username/password/TLS transport fields rather than pretending they are Postmark settings; provision the target Postmark token separately through secure target-only import input and persist only its Data Protection ciphertext;
- create system/library provider rows/overrides from the current four external-search slots;
- create whole-set rows for publication options (preserving normalized option IDs), common creators, and patron-code IDs; blank library source values map to no set/inherit, while meaningful library values create complete replacement sets;
- normalize custom-field definitions/options relationally, preserving stable option IDs/enabled/order without inventing unsupported behavior;
- compute each library/format's **current effective runtime format behavior** using the pinned resolver precedence, then represent built-in differences through typed format/override columns and custom-field mode/label behavior through `MaterialFormatCustomFieldRule`; do not choose between conflicting raw PocketBase stores arbitrarily;
- preserve library-owned custom formats/templates/auto-claim configuration as owned records rather than inherited settings;
- migrate reusable Polaris/Postmark secrets only as target Data Protection ciphertext;
- report any source values that cannot be mapped without loss or ambiguity as a blocking migration discrepancy unless an explicit transformation rule exists.

The deliberate patron-UI inheritance correction remains an exception to exact effective-behavior preservation: blank fields on a partial library UI record become no override and therefore inherit the configured system value instead of reproducing the current hard-coded fallback artifact. Report affected libraries/fields in the transformation report.

## 9. API and UI contract

The current frontend settings surface may continue to use a combined settings DTO/API shape where that minimizes frontend churn. The SQL model does not need to mirror that DTO.

For each inheritable setting the API must expose enough information for the UI to distinguish:

- effective value;
- configured system value;
- whether a library override exists;
- library override value when present and safe to return.

Secret values remain write-only/masked and never appear in the DTO as plaintext.

Whole-set domains must expose whether the current library is inheriting or owns a replacement set. Library-owned domains must not be presented as if they can be reset to a system default.

## 10. Required test contract

For every scalar field marked **system default + library override**, tests must verify:

1. system value can be saved;
2. library override can be saved;
3. effective runtime resolution selects the library value when present;
4. clearing only that field immediately exposes the current system value, even when the same library row still overrides another field.

Additionally test:

- system-only tables reject/nonrepresent library scope;
- whole-set absence/inherit, meaningful complete replacement, blank-value reset, and reset semantics;
- `Reset inherited overrides` preserves every library-owned domain listed above;
- external-search provider overrides do not create unauthorized library provider identities;
- built-in and custom-field material-format behavior has no competing legacy JSON rule source;
- secret inheritance/clear behavior never reveals plaintext;
- migration reconciliation covers every settings domain and catches unmapped source values.

Timeout semantic parity is mandatory, not merely property-value parity: use F1 in `06-TESTING-CI.md` section 10.2 to verify inherited system/default and explicit library OutstandingTimeout values/template selection, disabled overrides, exact clock boundary, suggestion-versus-approved-purchase behavior, and scheduled/manual equivalence. Missing/disabled mail cannot undo rejection. The four timeout names, database fields, inheritance/reset rules and queue limit keys remain unchanged; fair cursor ordering does not redefine age.

## 11. Primary current-code references

The authoritative current behavior should be verified against the pinned PocketBase source, especially:

| File | Why it matters |
|---|---|
| `pb_public/staff/js/settings/serialize-save.js` | Settings collected from current UI and system-context-only fields. |
| `pb_public/staff/js/settings/save-controller.js` | Current system/library payload boundaries and known save-path gaps. |
| `lib/staff/settings_routes.js` | Authorization, context handling, override detection, and reset routing. |
| `lib/staff/settings_save.js` | Workflow, system, SMTP, participation, and auto-claim persistence. |
| `lib/staff/settings_ui.js` | Patron text, publication options, material formats, and custom-field persistence. |
| `lib/staff/settings_email.js` | Built-in/rejection-template inheritance, hiding, overrides, and reset behavior. |
| `lib/staff/settings_logo_routes.js` | Branding permissions/reset behavior. |
| `lib/config/settings.js` | Overall system/library effective configuration. |
| `lib/config/workflows.js` | Workflow defaults and per-field library fallback. |
| `lib/config/ui_text.js` and `lib/config/ui-text/*` | Patron text, duplicate labels, format behavior, and current inheritance quirks. |
| `lib/config/emails.js` | Effective built-in/rejection email-template resolution. |
| `lib/config/smtp.js` | Current global transport configuration. |
| `lib/config/polaris.js` | Current Polaris integration field inventory. |
| `lib/custom_fields.js` | Custom patron-field definition, option identity/order, and submitted-value behavior. |
| `lib/format_rules.js` | Built-in and custom-field per-format rule normalization, including hidden-by-default custom-field behavior. |
| `AGENTS.md` | Existing repository rules requiring explicit system/library scope and consistent load/save/read behavior. |

## 12. Closure-remediation scope boundaries

`Authentication.Entra.AllowedTenantIds` remains restart-loaded external environment configuration, not an inheritable SQL/library setting. The common predicate in `01-PORTING-SPEC.md` section 7.6 applies its current value to every staff cookie use, sensitive-mail delivery/retry, and usable-super-admin check. Candidate configuration must leave at least one usable system super-admin; validate before activation, and fail startup closed if direct editing bypasses preflight. Removing a tenant does not require Graph, a session table, or key rotation. Re-adding it does not undo independent deactivation/rebind/scope invalidity.

`Organization.IsActive` still governs participation rather than stored StaffUser relationship validity. Closed history is preserved; reopen/import revalidate effective operational claimants, but library inactivity alone does not clear a valid stored claim. Recovery of an already-acquired HoldPlacementOperation is the narrowly defined inactive-library exception, not permission to start a new placement.

The seven schedule keys and eight configured queue keys, PageSize/MaxPerRun values, bounds, and precedence remain unchanged. `QueueProgress` is purpose-specific SQL operational state per logical queue/scope, not a configurable business setting or JobRun log. HoldRecovery reuses effective HoldPlacement limits as a separate bounded phase; no ninth configuration key is added. Fair cycles replace per-run cursor resets without changing effective operational-config parity values.

Configuration/release tests include persistent-cookie tenant removal with another allowed tenant still usable, sensitive-mail suppression, independently invalid authorization after tenant re-addition, zero-usable-super-admin candidate rejection/startup failure, exact existing queue-limit resolution with persisted progress, and the inactive acquired-work exception. Database-changing configuration/deployment work uses `05-DEPLOYMENT-OPERATIONS.md` section 9 when it actually mutates SQL; an external JSON edit alone is not a dependency-schema migration.
