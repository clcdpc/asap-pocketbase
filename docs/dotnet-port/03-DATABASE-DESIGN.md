# ASAP SQL Database Design

## 1. Database principles

- SQL Server 2022, compatibility level 160.
- Application-owned objects in `[asap]`.
- Hangfire owns `[HangFire]` outside the DACPAC.
- DACPAC is the schema source of truth.
- FK delete behavior is `RESTRICT`/`NO ACTION` by default; use `SET NULL` only for explicitly nullable historical relationships such as an additional-copy source.
- C# owns workflow behavior; avoid behavioral triggers.
- Constrained business codes use `nvarchar` plus `CHECK` constraints rather than lookup tables when the set is truly fixed.
- Use JSON only for dynamic payloads/configuration where relational columns would be artificial.
- `rowversion` is the standard optimistic-concurrency token for mutable workflows/config/admin records.
- Application tables are not a public/reporting contract.

The exact DDL can evolve during implementation, but it must preserve these semantics.

## 2. Schema/version foundation

### `[asap].[SchemaVersion]`

Purpose: exact app/database compatibility contract.

Suggested shape:

```text
Version int NOT NULL PK/check single row
UpdatedUtc datetime2 NOT NULL
```

The integer changes only for contract-affecting schema changes. Application startup compares the expected build value with SQL. Mismatch keeps liveness healthy but readiness unhealthy and blocks normal application functions. It is not used to decide whether a DACPAC needs deployment.

### `[asap].[DeploymentState]`

Purpose: deployment bookkeeping independent of the application/database compatibility contract.

```text
Id tinyint NOT NULL PK/check single row
LastDacpacSha256 char(64) NULL
LastHangfireSchemaVersion nvarchar(...) NULL
LastHangfireSchemaAssetSha256 char(64) NULL
LastReleaseVersion nvarchar(...) NULL
LastReleaseCommitSha char(40) NULL
LastDeployedUtc datetime2 NULL
```

The deployment script separately inspects application and dependency schema state. A changed/unknown DACPAC hash takes the database-changing path; an unchanged hash does not bypass required Hangfire DDL. The actual dependency schema and the artifact's tested compatibility/migration metadata are authoritative, not this bookkeeping row alone. Update database-component hashes/versions only after **all** planned database changes and compatibility checks succeed. These release fields identify the last database deployment; the installed file manifest identifies a later file-only application release. A genuine file-only deployment neither writes this row nor runs dependency DDL. See `05-DEPLOYMENT-OPERATIONS.md` sections 9-10.

## 3. Organization and staff

### `[asap].[Organization]`

```text
Id int PK                         -- actual Polaris Organization ID
DisplayName nvarchar(...)
Abbreviation nvarchar(...) NULL
IsActive bit NOT NULL
LastSyncedUtc datetime2 NULL
RowVersion rowversion
```

Rules:

- `Id = 1` is the actual Polaris system organization and remains active.
- Durable rows are system + participating/historically referenced libraries.
- Branches are reference/cache data, not permanent organization entities.
- Newly discovered libraries default inactive.

### `[asap].[StaffUser]`

```text
Id bigint IDENTITY PK
EntraTenantId uniqueidentifier NULL
EntraObjectId uniqueidentifier NULL
UserPrincipalName nvarchar(320) NULL       -- readable/searchable, not an auth key
NormalizedUserPrincipalName nvarchar(320) NULL
DisplayName nvarchar(...) NULL
NotificationEmail nvarchar(320) NULL
Role nvarchar(...) NOT NULL CHECK (staff/admin/super_admin)
OrganizationId int NOT NULL FK Organization
IsActive bit NOT NULL
WeeklyActionSummaryEnabled bit NOT NULL
WeeklyActionSummaryEmail nvarchar(320) NULL
PurchaseReminderDefault bit NOT NULL
AdditionalCopyReminderDefault bit NOT NULL
DefaultMineUnclaimedFilter bit NOT NULL
LastLoginUtc datetime2 NULL
RowVersion rowversion
```

Required invariants:

- filtered unique index on `(EntraTenantId, EntraObjectId)` when both are non-null; this pair is the only durable authorization identity;
- active StaffUser -> both Entra tenant and object ID are non-null; enforce structurally with a CHECK such as `IsActive = 0 OR (EntraTenantId IS NOT NULL AND EntraObjectId IS NOT NULL)`; imported inactive historical rows may remain unbound until an administrator explicitly binds them before reactivation;
- `UserPrincipalName`/normalized UPN and `DisplayName` are retained for human readability/search and may refresh from validated Entra claims, but are never used to authorize or auto-rebind an account; `NotificationEmail` is separate app-owned contact data and never refreshes from sign-in claims;
- super-admin -> Organization 1;
- staff/admin -> exactly one non-system library Organization; that Organization may be inactive without making the StaffUser row invalid;
- authorization uses the common current predicate in `01-PORTING-SPEC.md` section 7.6: active StaffUser, exact supplied/current durable identity tuple, membership in the loaded AllowedTenantIds, valid current role/scope, and Organization participation where required. For staff/admin interactive access this includes both `StaffUser.IsActive = 1` and the referenced `Organization.IsActive = 1`. Library deactivation does not mutate `StaffUser.IsActive`; staff reactivation while the referenced library is inactive is rejected; library reactivation restores eligibility for StaffUsers already active;
- any role/organization mutation that contracts or moves the StaffUser's resource scope is a transactional lifecycle mutation: in the same SQL transaction, deactivate active `FormatAutoClaimRule` rows outside the new scope; clear that user's claims from open/actionable `TitleRequest` rows outside the new scope with normal TitleRequest events; clear their claims from open `AdditionalCopyRequest` rows outside the new scope with a concise system Notes entry; preserve closed/nonactionable claimant snapshots as history; and write the administrative role/organization change plus per-type cleanup counts to `AdministrativeAudit`. Promotion to `super_admin` broadens scope and needs no cleanup; deactivation uses the same cleanup path with an empty usable scope;
- at least one usable super-admin under the common current predicate, not just an active role row. Mutations that can remove this eligibility (including rebind) take `sp_getapplock` with `@Resource = 'ASAP:ActiveSuperAdminInvariant'`, `@LockMode = 'Exclusive'`, `@LockOwner = 'Transaction'` before the normal row locks and prospective invariant read; zero remaining means rollback/409. The stopped-app external-config and startup gates separately validate prospective AllowedTenantIds against the same predicate;
- no PocketBase ID or Polaris staff ID/domain/identity-key fields.

- an authenticated staff ticket carries the `StaffUserId` plus the validated Entra tenant/object tuple used at sign-in. Request authorization always reloads the row, requires exact tuple equality and the current loaded allowed tenant; rebinding the row therefore invalidates old cookies without a separate session table.

Concurrency/locking contract: when multiple row categories are required, acquire them in this order: `Organization -> StaffUser -> TitleRequest/AdditionalCopyRequest -> dependent claim/rule/operation rows`; within one category use stable key order. `StaffUser` is the serialization point for lifecycle changes versus claim/rule relationship creation. Relationship writers (`TitleRequest` assignment, `AdditionalCopyRequest` inherited/new assignment and reopening, `FormatAutoClaimRule` changes, automatic rule execution) acquire/re-read the target StaffUser with `UPDLOCK,HOLDLOCK` or an equivalent SQL Server pattern before validating current eligibility. Participation-dependent operations lock/re-read Organization first. Never hold these transactions across external network calls.

Recipient contract:

- `NotificationEmail` is the primary destination for ordinary staff-directed notifications and is nullable by deliberate administrative choice.
- `WeeklyActionSummaryEmail` is an optional per-user weekly-summary override; a blank/null value falls back to `NotificationEmail`.
- `NotificationEmail` is managed through authorized Staff Access administration; ordinary self-service profile editing owns the weekly override and the other user preferences. Authorized administrators may clear it. Entra sign-in never initializes/repopulates it from claims, so an intentionally cleared value remains null until an explicit application mutation changes it.
- The weekly fallback is an intentional target normalization: some migrated users who currently have summaries enabled but no `weekly_action_summary_email` become eligible when they have a valid `NotificationEmail`. Reconciliation must report recipient/eligibility changes rather than hiding them.
- Reject/ignore legacy placeholder destinations ending in `@staff.asap.local`; they must never be sent to.

## 4. Configuration and branding

There is intentionally **no** generic `[asap].[Settings]` table. Configuration is split by domain, and `13-SETTINGS-SCOPE-INVENTORY.md` controls field ownership/inheritance semantics. Organization `1` is the real system/default scope.

### `[asap].[SystemSettings]`

True system-only application configuration.

```text
OrganizationId int PK/FK Organization CHECK (OrganizationId = 1)
StaffApplicationUrl nvarchar(...) NULL
LeapBibUrlPattern nvarchar(...) NULL
LeapPatronUrlPattern nvarchar(...) NULL
MaterialTypeIconUrlPattern nvarchar(...) NULL
SystemNotEnabledMessage nvarchar(max) NULL
MisconfiguredMessage nvarchar(max) NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Do not add library rows. These are not inheritable defaults.

### `[asap].[PatronEmbedAllowedOrigin]`

```text
Id bigint IDENTITY PK
OrganizationId int NOT NULL FK Organization CHECK (OrganizationId = 1)
Origin nvarchar(2048) NOT NULL
NormalizedOrigin nvarchar(2048) NOT NULL UNIQUE
CreatedUtc datetime2 NOT NULL
```

Parse/canonicalize origins in C# before persistence and use these rows to construct the system-wide CSP `frame-ancestors` policy.

### `[asap].[PolarisSettings]`

System-only integration configuration. Exact columns should match the fields actually required by the selected `Clc.Polaris.Api` version and system/application credential model.

```text
OrganizationId int PK/FK Organization CHECK (OrganizationId = 1)
Host nvarchar(...) NULL
AccessId nvarchar(...) NULL
ProtectedApiKey nvarchar(max) NULL
... other required non-legacy system/application credential fields ...
WorkstationId nvarchar(...) NULL
SystemPolarisUserId nvarchar(...) NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Do not reproduce legacy per-staff Polaris authentication fields. Protected credential columns contain Data Protection ciphertext, never plaintext.

### `[asap].[WorkflowSettings]`

One row per configured scope. Organization `1` is the complete system/default row; library rows are sparse nullable overrides. Representative fields:

```text
OrganizationId int PK/FK Organization
SuggestionLimit int NULL
SuggestionLimitMessage nvarchar(max) NULL
OutstandingTimeoutEnabled bit NULL
OutstandingTimeoutDays int NULL
OutstandingTimeoutSendEmail bit NULL
OutstandingTimeoutRejectionTemplateId bigint NULL FK EmailTemplate
HoldPickupTimeoutEnabled bit NULL
HoldPickupTimeoutDays int NULL
PendingHoldTimeoutEnabled bit NULL
PendingHoldTimeoutDays int NULL
AdditionalCopyTimeoutEnabled bit NULL
AdditionalCopyTimeoutDays int NULL
AutoPromote bit NULL
CommonAuthorsEnabled bit NULL
CommonAuthorsLabel nvarchar(...) NULL
CommonAuthorsHelp nvarchar(max) NULL
CommonAuthorsMessage nvarchar(max) NULL
AllowPatronAutoholdOptOut bit NULL
AllowAnyRegisteredCardLogin bit NULL
PatronCodeEligibilityEnabled bit NULL
PatronCodeEligibilityMessage nvarchar(max) NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

A null library field inherits only that field from Organization `1`; it does not cause record-level fallback. For ordinary overrideable text fields, trim and normalize blank library input to null/no override where the current application treats blank as fallback. Validate template scope and numeric ranges in C#/SQL as appropriate. `OutstandingTimeout*` applies only to unreviewed `suggestion` creation age and closes as `rejected`; it never expires `outstanding_purchase`. `OutstandingTimeoutSendEmail` and the scoped template ID govern only that auto-rejection notification. The complete status/timestamp/transition/notification predicates for all four timeout families are normative in `01-PORTING-SPEC.md` section 23.2; queue CreatedUtc ordering does not replace PendingHold/HoldPickup/AdditionalCopy UpdatedUtc age.

### `[asap].[CommonCreatorSet]` / `[asap].[CommonCreatorTerm]`

The creator list uses whole-set replacement, separate from the independently inheritable common-creator enabled/label/help/message fields.

```text
CommonCreatorSet:
  OrganizationId int PK/FK Organization
  RowVersion rowversion

CommonCreatorTerm:
  Id bigint IDENTITY PK
  OrganizationId int NOT NULL FK CommonCreatorSet(OrganizationId)
  Value nvarchar(...) NOT NULL
  SortOrder int NOT NULL
  UNIQUE (OrganizationId, SortOrder)
```

System set at Organization `1` is seeded. No library set means inherit system; a library set means complete replacement. To preserve current behavior, clearing all terms removes the library set and resumes inheritance.

### `[asap].[PatronCodeEligibilitySet]` / `[asap].[PatronCodeEligibilityMember]`

```text
PatronCodeEligibilitySet:
  OrganizationId int PK/FK Organization
  RowVersion rowversion

PatronCodeEligibilityMember:
  OrganizationId int NOT NULL FK PatronCodeEligibilitySet(OrganizationId)
  PatronCodeId nvarchar(...) NOT NULL
  PRIMARY KEY (OrganizationId, PatronCodeId)
```

Patron codes remain Polaris reference data rather than durable lookup rows. Validate configured IDs against current reference data when practical. Set-row presence marks a meaningful replacement. At library scope, clearing all IDs removes the set and resumes inheritance.

### `[asap].[PatronSettings]`

Organization `1` contains complete configured defaults; library rows are sparse field-level overrides. Representative shape:

```text
OrganizationId int PK/FK Organization
PageTitle nvarchar(...) NULL
BarcodeLabel nvarchar(...) NULL
PinLabel nvarchar(...) NULL
LoginPrompt nvarchar(max) NULL
LoginNote nvarchar(max) NULL
SuggestionFormNote nvarchar(max) NULL
NoEmailMessage nvarchar(max) NULL
SuccessTitle nvarchar(...) NULL
SuccessMessage nvarchar(max) NULL
AlreadySubmittedMessage nvarchar(max) NULL
EbookMessage nvarchar(max) NULL
EaudiobookMessage nvarchar(max) NULL
SuggestionStatusLabel nvarchar(...) NULL
OutstandingPurchaseStatusLabel nvarchar(...) NULL
PendingHoldStatusLabel nvarchar(...) NULL
HoldPlacedStatusLabel nvarchar(...) NULL
ClosedStatusLabel nvarchar(...) NULL
RejectedStatusLabel nvarchar(...) NULL
HoldCompletedStatusLabel nvarchar(...) NULL
HoldNotPickedUpStatusLabel nvarchar(...) NULL
ManualStatusLabel nvarchar(...) NULL
SilentStatusLabel nvarchar(...) NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Use explicit columns for the fixed status-label taxonomy; do not reintroduce a generic labels JSON blob.

### `[asap].[EmailSettings]`

Postmark transport/sender configuration with system + library field-level inheritance.

```text
OrganizationId int PK/FK Organization
ProtectedServerToken nvarchar(max) NULL
FromAddress nvarchar(320) NULL
FromName nvarchar(...) NULL
... other actually required Postmark options ...
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

A null library field inherits from Organization `1`. Protected token/secret values are opaque Data Protection ciphertext in SQL. Secret API semantics remain blank=preserve, explicit Clear=remove override/system value according to authorization/confirmation rules.

### `[asap].[ExternalSearchProvider]`

System-owned provider identities replace numbered provider columns. Seed/migrate the current provider slots as stable provider keys; do not add library-created provider identities in the initial port.

```text
Id bigint IDENTITY PK
OrganizationId int NOT NULL FK Organization CHECK (OrganizationId = 1)
ProviderKey nvarchar(...) NOT NULL UNIQUE
IsEnabled bit NOT NULL
Label nvarchar(...) NOT NULL
UrlTemplate nvarchar(max) NOT NULL
SortOrder int NOT NULL
RowVersion rowversion
```

### `[asap].[ExternalSearchProviderOverride]`

```text
LibraryOrganizationId int NOT NULL FK Organization
ExternalSearchProviderId bigint NOT NULL FK ExternalSearchProvider
IsEnabled bit NULL
Label nvarchar(...) NULL
UrlTemplate nvarchar(max) NULL
RowVersion rowversion
PRIMARY KEY (LibraryOrganizationId, ExternalSearchProviderId)
```

Null means inherit the corresponding system-provider field. Trim library label/URL input and map blank values to null/no override, matching the current fallback behavior rather than adding an explicit-empty override state.

### `[asap].[PublicationOptionSet]` / `[asap].[PublicationOption]`

```text
PublicationOptionSet:
  OrganizationId int PK/FK Organization
  RowVersion rowversion

PublicationOption:
  Id bigint IDENTITY PK
  OrganizationId int NOT NULL FK PublicationOptionSet(OrganizationId)
  OptionKey nvarchar(...) NOT NULL
  Label nvarchar(...) NOT NULL
  IsEnabled bit NOT NULL
  SortOrder int NOT NULL
  UNIQUE (OrganizationId, OptionKey)
  UNIQUE (OrganizationId, SortOrder)
```

`OptionKey` preserves the current normalized publication-option ID. Organization `1` owns the system list. No library set means inherit the full system list; a library set is the entire replacement list. At library scope, clearing the list removes the set and resumes inheritance. Reset deletes the library set and children.

### `[asap].[PatronCustomField]` / `[asap].[PatronCustomFieldOption]`

Library-owned configuration; there is no system/default custom-field layer in the initial port.

```text
PatronCustomField:
  Id bigint IDENTITY PK
  LibraryOrganizationId int NOT NULL FK Organization CHECK (LibraryOrganizationId <> 1)
  FieldKey nvarchar(...) NOT NULL
  FieldType nvarchar(...) NOT NULL CHECK (text/textarea/select)
  Label nvarchar(...) NOT NULL
  HelpText nvarchar(max) NULL
  IsEnabled bit NOT NULL
  SortOrder int NOT NULL
  RowVersion rowversion
  UNIQUE (LibraryOrganizationId, FieldKey)

PatronCustomFieldOption:
  Id bigint IDENTITY PK
  PatronCustomFieldId bigint NOT NULL FK PatronCustomField
  OptionKey nvarchar(...) NOT NULL
  Label nvarchar(...) NOT NULL
  IsEnabled bit NOT NULL
  SortOrder int NOT NULL
  UNIQUE (PatronCustomFieldId, OptionKey)
```

`OptionKey` preserves the stable normalized option ID currently stored in submitted custom-field snapshots; label is presentation text. Preserve current supported field semantics and validation lengths; do not invent system-level custom fields or extra behavior during the port.

### `[asap].[Branding]`

```text
OrganizationId int PK/FK Organization
LogoData varbinary(max) NULL
LogoContentType nvarchar(...) NULL
LogoFileName nvarchar(...) NULL
LogoAltText nvarchar(...) NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Image and alt inheritance are independent. Application validation: PNG/JPEG/GIF, <=2 MB, <=4096x4096, valid signature/type/dimensions.

### Configuration reset semantics

`Reset inherited overrides` may delete/clear library rows in the inheritable tables above, external-search overrides, whole-set library rows, system-format overrides, system-template overrides/hides, and branding overrides. It must **not** delete library-owned custom formats, custom rejection templates, patron custom fields/options/format rules, auto-claim rules/history, staff, requests, or audit/history.

## 5. Material formats and claim rules

### `[asap].[MaterialFormat]`

```text
Id bigint IDENTITY PK
OwnerOrganizationId int NOT NULL FK Organization
Code nvarchar(...) NOT NULL
Label nvarchar(...) NOT NULL
SortOrder int NOT NULL
IsEnabled bit NOT NULL
MessageBehavior nvarchar(...) NULL
TitleMode nvarchar(...) NULL
TitleLabel nvarchar(...) NULL
AuthorMode nvarchar(...) NULL
AuthorLabel nvarchar(...) NULL
IdentifierMode nvarchar(...) NULL
IdentifierLabel nvarchar(...) NULL
PublicationMode nvarchar(...) NULL
PublicationLabel nvarchar(...) NULL
CreatedUtc datetime2 NOT NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Unique normalized code rules must prevent invalid collisions according to system/library ownership semantics. `Code` is immutable after creation.

### `[asap].[MaterialFormatOverride]`

```text
Id bigint IDENTITY PK
LibraryOrganizationId int NOT NULL FK Organization
MaterialFormatId bigint NOT NULL FK MaterialFormat   -- system format only
Label nvarchar(...) NULL
SortOrder int NULL
IsEnabled bit NULL
MessageBehavior nvarchar(...) NULL
TitleMode nvarchar(...) NULL
TitleLabel nvarchar(...) NULL
AuthorMode nvarchar(...) NULL
AuthorLabel nvarchar(...) NULL
IdentifierMode nvarchar(...) NULL
IdentifierLabel nvarchar(...) NULL
PublicationMode nvarchar(...) NULL
PublicationLabel nvarchar(...) NULL
RowVersion rowversion
UNIQUE (LibraryOrganizationId, MaterialFormatId)
```

Null means inherit per field. A library custom format is a `MaterialFormat` owned by that library, not an override row. The target has no separate `patronFormatRules`/`FieldRulesJson` source; these typed columns are authoritative for format field behavior.

### `[asap].[MaterialFormatCustomFieldRule]`

Library-owned rule connecting a library custom field to an effective material format. This relationally replaces the custom-field portion of legacy `patronFormatRules`.

```text
Id bigint IDENTITY PK
LibraryOrganizationId int NOT NULL FK Organization CHECK (LibraryOrganizationId <> 1)
MaterialFormatId bigint NOT NULL FK MaterialFormat
PatronCustomFieldId bigint NOT NULL FK PatronCustomField
Mode nvarchar(...) NOT NULL CHECK (required/optional/hidden)
LabelOverride nvarchar(...) NULL
RowVersion rowversion
UNIQUE (LibraryOrganizationId, MaterialFormatId, PatronCustomFieldId)
```

C# validates that the custom field belongs to the same library and that the material format is usable in that library (system format or library-owned custom format). Disabled custom fields are effectively hidden without deleting their rules. When no rule exists for a field/format pair, the effective custom-field mode is `hidden` and the definition label is used; this matches current normalization behavior. These rules are library-owned configuration, not system-inherited overrides.

### `[asap].[FormatAutoClaimRule]`

```text
Id bigint IDENTITY PK
LibraryOrganizationId int NOT NULL FK Organization
MaterialFormatId bigint NOT NULL FK MaterialFormat
StaffUserId bigint NULL FK StaffUser                 -- nullable only for inactive migrated historical rules whose source assignee no longer maps
IsActive bit NOT NULL
CreatedUtc datetime2 NOT NULL
DeactivatedUtc datetime2 NULL
RowVersion rowversion
```

Use a filtered unique index for one active rule per library+format. Add a CHECK equivalent to `IsActive = 0 OR StaffUserId IS NOT NULL`; C# additionally requires every active rule's assignee to be active and scope-eligible for that library. Assignee changes deactivate+insert; do not rewrite historical rule identity. Migration may preserve an invalid/unmapped source rule only as inactive; when the source assignee maps, retain the FK even if the target user is inactive/out-of-scope, otherwise leave `StaffUserId` null and report the normalization. Historical request `ClaimRuleId` references may still point to that inactive rule.

## 6. Patron sessions

### `[asap].[PatronSession]`

```text
Id bigint IDENTITY PK
TokenHash binary(32) NOT NULL UNIQUE
Barcode nvarchar(...) NOT NULL
HomeOrganizationId int NULL
ExperienceOrganizationId int NULL
EffectiveOrganizationId int NOT NULL FK Organization
CreatedUtc datetime2 NOT NULL
ExpiresUtc datetime2 NOT NULL
RevokedUtc datetime2 NULL
```

No PIN. No long-lived patron-account/profile table. Index `ExpiresUtc` for cleanup and token hash for lookup; index `EffectiveOrganizationId` to support participation checks/revocation. A patron bearer token is authorized only when it matches a nonexpired, nonrevoked session **and** the referenced effective Organization is currently active. Final session insertion must occur in a short transaction that locks/re-reads the effective Organization row (`UPDLOCK,HOLDLOCK` or equivalent), verifies `IsActive = 1`, and inserts the session. Library deactivation uses that same Organization row as the serialization point while setting it inactive and revoking sessions for the effective organization, so a racing login cannot commit a usable post-deactivation session. Reactivation never clears `RevokedUtc`.

## 7. Title requests

### `[asap].[TitleRequest]`

Representative shape:

```text
Id bigint IDENTITY PK
LegacyId nvarchar(...) NULL
LibraryOrganizationId int NOT NULL FK Organization
PatronOrganizationId int NULL
StaffLibraryOrganizationIdCreatedBy int NULL

Barcode nvarchar(...) NOT NULL               -- historical snapshot
Email nvarchar(...) NULL                     -- historical submission snapshot
NameFirst nvarchar(...) NULL
NameLast nvarchar(...) NULL
PatronCodeId nvarchar(...) NULL
PatronCodeDescription nvarchar(...) NULL
PreferredPickupBranchId int NULL                    -- current recorded pickup; mutable only via dedicated validated pickup workflow
PreferredPickupBranchName nvarchar(...) NULL
LibraryNameSnapshot nvarchar(...) NULL

Title nvarchar(...) NOT NULL
Author nvarchar(...) NULL
Identifier nvarchar(...) NULL
Publication nvarchar(...) NULL
ExactPublicationDate date/datetime2 NULL
CustomFieldsJson nvarchar(max) NULL
AutoHold bit NOT NULL
MaterialFormatId bigint NOT NULL FK MaterialFormat

Status nvarchar(...) NOT NULL CHECK (...)
CloseReason nvarchar(...) NULL CHECK (...)
BibId nvarchar(...) NULL
Notes nvarchar(max) NULL

ClaimedByStaffUserId bigint NULL FK StaffUser
ClaimedByDisplayName nvarchar(...) NULL
ClaimedAtUtc datetime2 NULL
ClaimType nvarchar(...) NULL CHECK (manual/automatic_format_rule/legacy as needed)
ClaimRuleId bigint NULL FK FormatAutoClaimRule

LastPromoterCheckUtc datetime2 NULL
IsbnCheckStatus nvarchar(...) NULL             -- CHECK pending/found/not_found/skipped_no_isbn/error_max_retries
IsbnCheckResult nvarchar(max) NULL
IsbnCheckRetryCount int NOT NULL DEFAULT 0 CHECK (IsbnCheckRetryCount >= 0)
IsbnCheckLastErrorCode nvarchar(...) NULL         -- safe classification/code only; no raw Polaris payload/PII
LastCheckedUtc datetime2 NULL

CreatedUtc datetime2 NOT NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

`LegacyId` is provenance/business-history data and is not the PocketBase mapping key used by migration. Keep a separate migration mapping table.

Use indexes for library/status, claim state, format, created/updated, background-job selection fields, and any currently frequent duplicate/identifier lookups. Normalize blank `BibId` to null and add a CHECK equivalent to `IsbnCheckStatus <> 'found' OR NULLIF(LTRIM(RTRIM(BibId)), '') IS NOT NULL` so SQL cannot contain a canonical `found` row without supporting BIB state. The application/migration additionally keeps the identifier-found workflow tag coherent with that state.

`PreferredPickupBranchId`/`PreferredPickupBranchName` are not immutable patron-submission identity snapshots. Generic request editing cannot change them, but the dedicated pickup-preference workflow may update them after a successful validated Polaris pickup mutation and records the corresponding note/event. A failed Polaris pickup update leaves them unchanged.

Canonical identifier-processing state is owned by the five-minute processor. New runtime writes of `found` occur only after a successful identifier lookup yields a BIB ID and the application performs its normal reconciliation/tag updates in the same domain operation. Migration follows the explicit legacy-state normalization rules in `04-MIGRATION-CUTOVER.md`; it never invents a BIB ID. The target deliberately merges the two legacy ISBN implementations: it keeps BIB ID persistence/reconciliation, found/not-found workflow tags, `skipped_no_isbn`, and `LastCheckedUtc` from the dedicated path while retaining bounded transient retries from the older hourly path. The Polaris adapter classifies each lookup as `Found`, `DefinitiveNotFound`, `TransientFailure`, or `OperationalFailure`; only definitive successful zero-result searches produce `not_found`. Transient failures increment `IsbnCheckRetryCount`; attempts 1-4 remain `pending`, attempt 5 becomes `error_max_retries`, with fair revisits on the normal five-minute schedule and no extra backoff; backlog/restarts may extend elapsed time. Operational/configuration/auth/protocol failures never become `not_found`, do not consume/reset the transient retry budget, and fail/surface the job while the request remains `pending`. Found/not-found/skipped outcomes and the explicit manual retry action reset retry state as defined in the porting spec. A permitted pre-placement identifier edit is stronger: in one rowversion-protected mutation it invalidates the old identifier result/check timestamp/error/retry state, removes the three identifier-derived workflow tags, and clears the prior `BibId`; the new identifier becomes `pending` (or `skipped_no_isbn` when cleared). An explicit BIB selected in the same staff workflow is processed only after invalidation through the normal Polaris BIB validation/reconciliation path and is never inherited implicitly from the old identifier. `IsbnCheckLastErrorCode` stores only a safe classification/code; raw upstream errors remain in secret/PII-safe operational logs. The hourly workflow orchestrator does not perform identifier checks. Identifier changes/clears are rejected in `hold_placed` and `closed`; normalized unchanged values are no-ops. A completed successful operation does not remove placed-BIB protection, and reopening does not permit replacing a real hold's BIB. The successful operation's immutable `BibIdSnapshot` remains the placement authority and must equal request `BibId`; positive checkout fulfillment uses that BIB authority, not a fresh identifier lookup; terminal hold fulfillment additionally requires the exact tracked PolarisHoldId under `01-PORTING-SPEC.md` section 9.2. Migrated placed-hold lifecycle evidence retains the source BIB through one existing legacy-event metadata marker (`legacyBibProtection`, recorded `bibId`, source reference), exactly as `04-MIGRATION-CUTOVER.md` section 6.10; it does not fabricate provider success/journal or reconstruct current workflow state from events. The marker covers the exhaustive normalized source evidence in migration section 6.10, including all five hold-terminal reasons and `status_changed -> hold_placed`. Marker presence with `bibId: null` means known placed-stage history with unknown BIB, not absence of protection. A historical null remains unknown, never inferred from a replacement identifier; the marker never supplies an authoritative current HoldRequestID. Stage/combined-transition validation and API capabilities are normative in `07-API-FRONTEND-COMPATIBILITY.md` section 14.2.

## 8. Request events/tags

### `[asap].[TitleRequestEvent]`

```text
Id bigint IDENTITY PK
TitleRequestId bigint NOT NULL FK TitleRequest
EventType nvarchar(...) NOT NULL
Status nvarchar(...) NULL
CloseReason nvarchar(...) NULL
ActorType nvarchar(...) NOT NULL CHECK (system/staff/patron)
StaffUserId bigint NULL FK StaffUser
ActorName nvarchar(...) NULL
Message nvarchar(max) NULL
MetadataJson nvarchar(max) NULL
CreatedUtc datetime2 NOT NULL
```

Events are append-only at the application level. Historical unknown event types map to constrained `legacy` with original event type in metadata. Unknown historical actor types are migration blockers. The one retained `legacyBibProtection` marker per imported request uses this existing MetadataJson; preserve sorted normalized evidence references, recorded/unknown historical BIB, and deterministic transform identity as specified in migration section 6.10. It is an edit guard, not event-sourced current status or final-provider hold identity.

### `[asap].[WorkflowTag]`

```text
Id bigint IDENTITY PK
Code nvarchar(...) NOT NULL UNIQUE
Label nvarchar(...) NOT NULL
SortOrder int NOT NULL
```

Seeded/app-owned taxonomy; no general CRUD UI.

### `[asap].[TitleRequestWorkflowTag]`

```text
TitleRequestId bigint NOT NULL FK TitleRequest
WorkflowTagId bigint NOT NULL FK WorkflowTag
PRIMARY KEY (TitleRequestId, WorkflowTagId)
```

## 9. Additional-copy requests

### `[asap].[AdditionalCopyRequest]`

```text
Id bigint IDENTITY PK
SourceTitleRequestId bigint NULL FK TitleRequest ON DELETE SET NULL
LibraryOrganizationId int NOT NULL FK Organization
BibId nvarchar(...) NULL
Title nvarchar(...) NOT NULL
Author nvarchar(...) NULL
MaterialFormatId bigint NULL FK MaterialFormat
Identifier nvarchar(...) NULL
Publication nvarchar(...) NULL
Status nvarchar(...) NOT NULL CHECK (open/closed)
Notes nvarchar(max) NULL

ClaimedByStaffUserId bigint NULL FK StaffUser
ClaimedByDisplayName nvarchar(...) NULL
ClaimedAtUtc datetime2 NULL
ClaimType nvarchar(...) NULL
ClaimRuleId bigint NULL FK FormatAutoClaimRule

CreatedByStaffUserId bigint NULL FK StaffUser
CreatedByDisplayName nvarchar(...) NULL
ClosedByStaffUserId bigint NULL FK StaffUser
ClosedByDisplayName nvarchar(...) NULL
CreatedUtc datetime2 NOT NULL
ClosedUtc datetime2 NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
```

Exact fields may follow current API needs, but additional copies remain independent persisted workflows. Open AdditionalCopy claims participate in StaffUser lifecycle cleanup/serialization exactly like TitleRequest claims for eligibility purposes: relationship creation locks/revalidates the target StaffUser; deactivation/scope contraction clears now-invalid open claims, adds a concise system entry to `Notes`, and includes the cleanup in `AdministrativeAudit`; closed claimant snapshots remain unchanged. There is intentionally no new AdditionalCopy event table solely for this lifecycle cleanup. Reopening revalidates any retained claimant before it becomes effective: lock Organization -> candidate StaffUser -> task, re-read expected task version/claimant, and retry the read/lock sequence if the candidate changed. Preserve only a currently eligible claimant; otherwise clear all effective claim fields and append prior attribution/timestamp/reason to existing Notes in the reopen transaction. No automatic replacement claimant and no new event table. Closed history is unchanged until reopening. The same activation check applies to inheriting a source claim at creation. Organization inactivity alone never invalidates the stored relationship; actor participation is checked separately.

## 10. Deletion and administrative audit

### `[asap].[DeletedRequestAudit]`

One table for title requests and additional-copy deletions.

```text
Id bigint IDENTITY PK
RequestType nvarchar(...) NOT NULL CHECK (title_request/additional_copy)
OriginalRequestKey nvarchar(64) NOT NULL       -- new bigint ID as text, or historical PocketBase request ID
LibraryOrganizationId int NOT NULL
Title nvarchar(...) NULL
Author nvarchar(...) NULL
Identifier nvarchar(...) NULL
BibId nvarchar(...) NULL
Status nvarchar(...) NULL
CloseReason nvarchar(...) NULL
MaskedBarcode nvarchar(...) NULL
CreatedUtc datetime2 NULL
DeletedUtc datetime2 NOT NULL
DeletedByStaffUserId bigint NULL FK StaffUser
DeletedByDisplayName nvarchar(...) NULL
```

`OriginalRequestKey` is intentionally string-shaped because migrated deletion-audit rows may reference a PocketBase request ID for a request that no longer exists and therefore cannot be mapped to a new bigint. New deletions store the target bigint request ID using its invariant decimal representation.

Do not preserve unnecessary names/email/full barcode/freeform notes from legacy deletion audit.

### `[asap].[AdministrativeAudit]`

```text
Id bigint IDENTITY PK
ActorStaffUserId bigint NULL FK StaffUser
ActorName nvarchar(...) NULL
OrganizationId int NULL FK Organization
Action nvarchar(...) NOT NULL
TargetType nvarchar(...) NULL
TargetId nvarchar(...) NULL
DetailsJson nvarchar(max) NULL
CreatedUtc datetime2 NOT NULL
```

High-impact access/config only. Details must be secret-safe.

## 11. Email

### `[asap].[EmailTemplate]`

```text
Id bigint IDENTITY PK
OrganizationId int NOT NULL FK Organization
TemplateKey nvarchar(...) NOT NULL
SourceTemplateId bigint NULL FK EmailTemplate
DisplayName nvarchar(...) NULL
SubjectTemplate nvarchar(max) NULL
BodyTemplate nvarchar(max) NULL
IsHidden bit NOT NULL
IsCustom bit NOT NULL
SortOrder int NOT NULL
RowVersion rowversion
```

Use uniqueness rules appropriate to system/library key semantics. For system and library-custom templates, required content is non-null by application/DB invariant. A library override/hide row references its system source and may leave individual content/display fields null to inherit them. `Reset inherited overrides` deletes source-linked library override/hide rows but preserves library-owned custom templates.

### `[asap].[EmailOutbox]`

```text
Id bigint IDENTITY PK
OrganizationId int NOT NULL FK Organization
BusinessKey nvarchar(450) NULL              -- deterministic idempotency key
DeliveryClass nvarchar(...) NOT NULL CHECK (business_event/staff_authorization_sensitive/operational_test)
RecipientStaffUserId bigint NULL FK StaffUser
RecipientEntraTenantId uniqueidentifier NULL  -- original recipient identity evidence for sensitive mail
RecipientEntraObjectId uniqueidentifier NULL
AuthorizationOrganizationId int NULL FK Organization
ToAddress nvarchar(...) NULL                -- nullable only for terminal suppressed intent
FromAddress nvarchar(...) NULL              -- nullable only for terminal suppressed intent
FromName nvarchar(...) NULL
Subject nvarchar(max) NULL
BodyText nvarchar(max) NULL
BodyHtml nvarchar(max) NULL
Status nvarchar(...) NOT NULL CHECK (pending/sending/sent/failed/suppressed)
SuppressionReason nvarchar(...) NULL
AttemptCount int NOT NULL
NextAttemptUtc datetime2 NULL
LastAttemptUtc datetime2 NULL
SendingStartedUtc datetime2 NULL
LeaseId uniqueidentifier NULL
LeaseExpiresUtc datetime2 NULL
LastErrorCode nvarchar(...) NULL
LastErrorDetail nvarchar(max) NULL
ProviderMessageId nvarchar(...) NULL
CreatedUtc datetime2 NOT NULL
SentUtc datetime2 NULL
SuppressedUtc datetime2 NULL
RowVersion rowversion
```

Create the database invariant explicitly:

```sql
CREATE UNIQUE INDEX UX_EmailOutbox_BusinessKey
    ON [asap].[EmailOutbox] (BusinessKey)
    WHERE BusinessKey IS NOT NULL;
```

Deterministic business keys are globally namespaced by notification semantics; test/ad-hoc repeatable messages may leave the key null. When two writers race on the same key, SQL uniqueness is authoritative and the loser resolves the existing row as an idempotent success rather than reporting a failed enqueue. Ordinary weekly summaries use one key per recipient/reporting period. Forced weekly summaries use one generated `ManualRunId` per accepted forced invocation and keys such as `weekly-summary-force:{ManualRunId}:{StaffUserId}`; retries of that same forced Hangfire job reuse the ID.

For `staff_authorization_sensitive`, `RecipientStaffUserId`, `RecipientEntraTenantId`, `RecipientEntraObjectId`, and `AuthorizationOrganizationId` are required. Snapshot the recipient's current explicit binding at intent creation. Apply the common current eligibility predicate at every send/retry: the stored recipient tuple must still equal the StaffUser binding and its tenant must still be in loaded AllowedTenantIds, in addition to active/role/scope/participation checks. The delivery worker revalidates current StaffUser activity/scope and the current effective app-owned staff destination immediately before each send/retry; library-scoped mail also requires the library still active, while consortium-wide summary scope uses Organization `1` and requires the recipient still be an active super-admin. Lost authorization/address ownership transitions the row to terminal `suppressed`. `business_event` rows do not acquire this send-time staff authorization dependency and may drain after later participation changes.

DB/application invariants for deliverable rows (`pending`/`sending`/`failed`) require usable recipient/sender/content snapshots. `suppressed` may retain null recipient/sender when those values were the reason the notification intent could not be delivered. Missing required notification configuration at business-transaction time creates a terminal suppressed intent rather than rolling back the business mutation; optional staff notifications with no configured destination may instead create no row where the product contract says no notification is requested. If transport configuration disappears only after a valid row was queued, record `failed`/`mail_not_configured` and retain the payload for manual retry.

Do not snapshot the reusable Postmark token here. Resolve effective transport config at each send/retry. A worker atomically claims a due `pending` row by setting `Status=sending`, a new `LeaseId`, and bounded `LeaseExpiresUtc`, commits, and only then calls Postmark. The sweeper must reclaim expired `sending` rows according to the at-least-once ambiguity policy; rows must never remain permanently stranded in `sending`. A crash after provider acceptance but before the local `sent` update may cause a duplicate retry, so exactly-once transport delivery is not promised. `failed` remains manually retryable and retains its full delivery payload. Only terminal `sent` and `suppressed` rows are eligible for subject/body payload purge after 90 days; retain status/provider/attempt/lease/suppression metadata needed for operational history.

### `[asap].[EmailDeliveryEvent]`

```text
Id bigint IDENTITY PK
EmailOutboxId bigint NULL FK EmailOutbox
ProviderMessageId nvarchar(...) NULL
ProviderEventId nvarchar(...) NULL
EventType nvarchar(...) NOT NULL
ReceivedUtc datetime2 NOT NULL
MetadataJson nvarchar(max) NULL
```

Use a filtered unique index on `ProviderEventId` when non-null so multiple legacy/provider-less rows are allowed while webhook event IDs remain idempotent. Preserve historical PocketBase delivery audit in an appropriate equivalent record form without creating pending outbox work.

## 12. Polaris mutation journal and bounded queue progress

### `[asap].[HoldPlacementOperation]`

Purpose-specific durable journal, not a generic external-operation framework. `01-PORTING-SPEC.md` section 9.1 is the normative execution/evidence protocol.

```text
Id bigint IDENTITY PK
TitleRequestId bigint NOT NULL FK TitleRequest
PatronBarcodeSnapshot nvarchar(...) NOT NULL
PatronIdSnapshot nvarchar(...) NULL
BibIdSnapshot nvarchar(...) NOT NULL
PickupBranchIdSnapshot int NULL
RequestingOrganizationIdSnapshot int NULL
WorkstationIdSnapshot int NULL
PolarisUserIdSnapshot nvarchar(...) NULL
AttemptNumber int NOT NULL
State nvarchar(...) NOT NULL CHECK (in_progress/ambiguous/operator_required/succeeded/no_hold/failed)
Phase nvarchar(...) NOT NULL CHECK (acquired/create_started/reply_ready/reply_started/result_recorded)
OwnerToken uniqueidentifier NULL
ExecutionEpoch bigint NOT NULL
LeaseExpiresUtc datetime2 NULL
RequestStartedUtc datetime2 NOT NULL          -- immutable queue creation key
CreateStartedUtc datetime2 NULL               -- committed before first create call
CreateResponseObservedUtc datetime2 NULL
ReplyStartedUtc datetime2 NULL                -- committed before first reply call
ReplyResponseObservedUtc datetime2 NULL
CompletedUtc datetime2 NULL
PolarisRequestGuid nvarchar(...) NULL         -- create/reply/recovery RequestGUID; not a final HoldRequestID
PolarisHoldId nvarchar(...) NULL              -- authoritative final HoldRequestID for this placed/adopted hold
TxnGroupQualifier nvarchar(...) NULL
TxnQualifier nvarchar(...) NULL
ReplyAnswer nvarchar(...) NULL
ReplyState nvarchar(...) NULL
ProviderStatusType nvarchar(...) NULL
ProviderStatusValue nvarchar(...) NULL
ResultCode nvarchar(...) NULL
OutcomeEvidenceKind nvarchar(...) NULL
RecoveryAttemptCount int NOT NULL DEFAULT 0
LastRecoveryUtc datetime2 NULL
LastErrorCode nvarchar(...) NULL
DetailJson nvarchar(max) NULL                 -- necessary safe evidence/reference only
RowVersion rowversion
UNIQUE (TitleRequestId, AttemptNumber)
```

Use a filtered unique index on `TitleRequestId WHERE CompletedUtc IS NULL`; nonterminal states require null CompletedUtc and terminal states require it. Validate marker/phase consistency, positive attempt/epoch, lease token/expiry nullability as a pair, nonnegative recovery count, and all required persisted reply context before `reply_started`. A confirmed-no-effect evidence classification is required for terminal `no_hold`/`failed`; uncertain transport errors are `ambiguous`/`operator_required`, never terminal failures. `BibIdSnapshot` and acquired request identity are immutable. Final patron/pickup/create inputs become immutable when the create marker commits.

Acquisition uses Organization -> TitleRequest -> operation ordering and active-Organization/current eligibility checks. Incomplete-operation takeover/renewal/result writes compare the current token/epoch, lease validity, phase and rowversion; takeover increments epoch. An unexpired owner excludes another executor. A marked create/reply excludes automatic repetition even after takeover: SQL fencing protects SQL transitions, not an already-dispatched provider request. Persist provider RequestGUID and both normalized reply qualifiers plus the approved reply fields before permitting reply. No transaction spans Polaris.

An incomplete row is the request-mutation barrier defined in the spec, including `operator_required`. Successful completion atomically updates the operation, request's placed BIB/status, event/tags and outbox intent, and is idempotent. A recovery result must not be discarded solely because the request's unrelated claim/description version changed; under the request lock preserve those unrelated fields and validate the frozen operation inputs. Recovery selects previously acquired rows even for inactive Organizations. New numbered acquisition always requires active participation and proven terminal no-effect for any prior unsuccessful attempt. Store operator resolution in the existing TitleRequestEvent/AdministrativeAudit, not a new event system.

Identity capture and later fulfillment use `01-PORTING-SPEC.md` section 9.2. PolarisRequestGuid/transaction qualifiers are conversation context; only PolarisHoldId contains a proven final HoldRequestID. Persist its correlation provenance in OutcomeEvidenceKind/DetailJson, not a second journal. A genuine runtime existing-hold adoption may complete the same operation from acquired through result_recorded with `existing_hold_adoption` evidence and no create/reply markers. A final successful placement without an obtainable final ID keeps PolarisHoldId null; it does not replay the mutation or borrow an older/same-BIB ID. A proven later null-to-ID enrichment uses the same completed row with evidence and no repeated business completion, under section 9.2's current request/operation rowversion checks rather than the expired incomplete-operation lease. It leaves Phase/State/CompletedUtc unchanged. Recorded non-null final IDs are immutable; contradictory mapping is diagnostic/operator work. Fulfillment selects the latest succeeded operation by AttemptNumber, checks its patron/BIB/current association, and never falls back to an older operation when that latest row lacks identity. Imported history has no fabricated operation. These rules add no new SQL table or hold journal.

### `[asap].[QueueProgress]`

One bounded-progress record per logical queue/scope; not execution history, a work queue, or a general JobRun model. Normative algorithm: `01-PORTING-SPEC.md` section 23.1.

```text
QueueName nvarchar(...) NOT NULL
ScopeOrganizationId int NOT NULL FK Organization  -- 1 = all libraries; otherwise exact library
CycleMaxId bigint NULL                            -- null = no active cycle; zero = empty cycle
LastCreatedUtc datetime2 NULL
LastItemId bigint NULL
LastOutcomeItemId bigint NULL
LastOutcomeCode nvarchar(...) NULL                -- safe handled result/skip/failure code
LastOutcomeUtc datetime2 NULL
UpdatedUtc datetime2 NOT NULL
RowVersion rowversion
PRIMARY KEY (QueueName, ScopeOrganizationId)
```

The allowed queue names are the eight configured logical queues plus `HoldRecovery`, which uses HoldPlacement limits without a new setting. LastCreatedUtc/LastItemId are a nullable pair; they and CycleMaxId are scalar values, not source-row FKs, so deletion cannot invalidate progress. Use TitleRequest/AdditionalCopy CreatedUtc and Id, or HoldPlacementOperation RequestStartedUtc and Id for recovery; preserve immutable creation ordering. CycleMaxId excludes later inserts, including backdated ones. Clear watermark/key only after tail exhaustion, never merely on cap or restart. Normal results and handled failure/skip outcomes checkpoint only after durable handling, updating QueueProgress last in the existing lock order. Expected progress rowversion and the existing logical-job guard prevent concurrent or stale cursor advancement. No per-run history or additional scheduler is introduced.

## 13. PocketBase migration mapping

### `[asap].[LegacyPocketBaseMapping]`

Generic temporary mapping used during/after cutover for legacy IDs and deep links.

```text
EntityType nvarchar(...) NOT NULL
PocketBaseId nvarchar(...) NOT NULL
NewId bigint NOT NULL
PRIMARY KEY (EntityType, PocketBaseId)
```

Keep only as long as needed for old deep-link compatibility and migration diagnostics. Removal is an explicit post-cutover cleanup, not automatic.

## 14. Index/concurrency checklist

At implementation time verify, with real query plans where useful:

- active StaffUser (`EntraTenantId`, `EntraObjectId`) lookup plus readable normalized-UPN search;
- StaffUser role/organization scope-contraction transaction paths that lock the StaffUser serialization row, update the user, deactivate out-of-scope active auto-claim rules, clear out-of-scope open TitleRequest and AdditionalCopy claims, and write the appropriate TitleRequest event/AdditionalCopy note/admin audit atomically; relationship writers lock/revalidate the same StaffUser so stale claim/rule creation cannot commit afterward;
- serialized active-super-admin removal path using the transaction-owned `ASAP:ActiveSuperAdminInvariant` application lock;
- system-only configuration ownership checks; one row per organization for inheritable scalar domains; external-provider override uniqueness; whole-set child ordering/membership constraints; custom-field/option/rule ownership, key, mode, and ordering constraints;
- patron session token hash + expiration + `EffectiveOrganizationId`; authorization joins/rechecks current Organization activity, and session issuance/deactivation use the Organization row serialization path;
- request library/status/CreatedUtc+Id keyset ordering, immutable creation keys, finite CycleMaxId filtering, and per-queue/scope progress checkpoint CAS;
- retained-claim activation on AdditionalCopy reopen and import reconciliation of every operational claimant against current target eligibility, not FK existence alone;
- open/actionable claim queries;
- auto-claim active unique index;
- job selection by status/last-checked/retry fields;
- event request/time and hold-placed lookup;
- email outbox status/next-attempt/expired-lease selection plus filtered unique non-null `BusinessKey` idempotency, authorization-sensitive recipient/scope lookup, terminal suppression, and state-aware payload purge (`sent`/`suppressed` only);
- HoldPlacementOperation one-incomplete-per-request/attempt uniqueness, phase-marker/evidence constraints, token+epoch ownership fencing, durable reply context, inactive-library recovery selection and operator-required barrier;
- successful placed-BIB protection after journal completion and immutable legacy placement evidence, including identifier/explicit-BIB/fulfillment invariants;
- provider webhook idempotency IDs;
- analytics common scope/date predicates;
- legacy PocketBase mapping lookup.

Do not over-index preemptively. Add indexes because known workflows or measured queries need them.
