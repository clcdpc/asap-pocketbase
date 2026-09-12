# API and Frontend Compatibility Plan

## 1. Compatibility rule

The existing patron/staff UX and `/api/asap/...` contracts are the baseline for the initial port. Preserve behavior where practical. The API is internal to the bundled frontend, so a deliberate .NET-driven correction is allowed, but it must be explicit and tested.

Do not perform a broad API redesign or frontend rewrite simply because the backend technology changes.

## 2. Frontend architecture

Preserve:

- vanilla HTML/CSS/ES modules;
- `/patron/` patron application;
- `/staff/` tabbed staff application;
- existing module decomposition (grid rendering/actions/events/filtering, settings modules, etc.);
- Grid.js behavior;
- current client-side full-list filtering/search for staff queues;
- existing visual/interaction behavior except fixes directly required by auth/backend changes.

Do not introduce React, Blazor, Razor Pages for the app UI, a client router, TypeScript conversion, or a bundler in the port.

## 3. Asset strategy

Vendor the exact browser dependency versions already used by the PocketBase app (including Bootstrap 4.1.3, Font Awesome 4.7, and the current Grid.js version) into the repository and serve them locally. Remove production jsDelivr/CDN dependencies.

Do not combine the port with Bootstrap/Font Awesome/Grid.js upgrades.

Recommended source shape:

```text
src/Asap.Web/Frontend/
  patron/
  staff/
  shared/
  vendor/
```

The generated/publish web root is produced by an incremental MSBuild copy target. No npm step is part of `dotnet build`, `dotnet publish`, IIS deployment, or normal F5.

## 4. Staff authentication contract changes

Intentional change: PocketBase/local/Polaris staff credential login is replaced by Entra OIDC.

Expected flow:

1. `/staff` loads an anonymous shell.
2. Session API returns unauthenticated state.
3. UI displays ASAP branding and **Sign in with Microsoft**.
4. Explicit sign-in endpoint starts OIDC with local validated `returnUrl`.
5. Callback requires validated Entra `tid` + `oid`, verifies the tenant is allowed, and establishes/uses the staff session only after finding an active `StaffUser` by (`EntraTenantId`,`EntraObjectId`). The auth ticket records `StaffUserId` plus that validated tuple. Every later authenticated request reloads the StaffUser and requires the ticket tuple to still exactly match the row and its tenant to remain in the currently loaded `AllowedTenantIds`, so a durable identity rebind invalidates cookies issued to the old identity immediately. `UserPrincipalName`/`DisplayName` are readable Entra metadata, never authorization keys; `NotificationEmail` is separate app-owned contact data. For library-scoped staff/admin, the referenced non-system Organization must also be active before authorization succeeds.
6. On successful sign-in the server may refresh readable `UserPrincipalName`/`DisplayName` fields from validated claims. It never initializes/repopulates `NotificationEmail`; then the staff UI loads normal session/profile data.
7. API calls receive `401`/`403` JSON, not HTML auth redirects.
8. Sign-out clears local session and follows the intended OIDC/local sign-out behavior.

Frontend code must not depend on a PocketBase auth store after the port.

## 5. Antiforgery

Staff uses cookie authentication, so state-changing staff API calls use ASP.NET Core antiforgery protection. Provide one centralized frontend helper that obtains/attaches the antiforgery token. Do not scatter bespoke token logic across modules.

Patron APIs use the opaque bearer token and do not rely on the staff cookie/antiforgery model.

## 6. Patron session contract changes

Intentional change from current PocketBase session semantics:

- bearer token stored in `sessionStorage`;
- absolute 1-hour expiration;
- server stores only SHA-256 token hash + minimal session context;
- every patron-authenticated request resolves `EffectiveOrganizationId` and requires the Organization to still be active in addition to normal token/expiry/revocation checks;
- final session insertion locks/re-reads the effective Organization in a short transaction and can commit only while it is active; library deactivation uses the same serialization point while revoking matching sessions, so a racing login cannot create a usable post-deactivation token;
- logout revokes session;
- organization reactivation never un-revokes an old session; the patron must authenticate again;
- no durable PatronUser profile cache/account.

Preserve user-facing login/submission behavior otherwise.

## 7. Rowversion change

Mutable DTOs that need optimistic concurrency gain opaque `version` field. Mutation bodies carry expected version. `409 Conflict` is the canonical stale-write response.

Frontend handling:

- stop the failed mutation;
- refresh the affected record/settings state;
- tell the user it changed and must be reviewed again;
- do not auto-replay user intent against new state.

## 8. HTTP status/error cleanup

Preserve useful existing error messages but correct obviously inappropriate HTTP behavior during the port. In particular:

- unauthenticated staff API -> `401`;
- authenticated unauthorized -> `403`;
- stale optimistic concurrency -> `409`;
- any staff mutation that would remove the final currently usable super-admin -> `409` with the explicit invariant message; external tenant-policy changes use the same predicate and are blocked by preflight/startup rather than bypassed by existing cookies;
- explicitly reactivating a staff/admin account while its referenced library is inactive -> `409`;
- client validation -> appropriate `400` family response;
- Polaris upstream failure where local operation cannot proceed -> appropriate `502`/service error semantics;
- rate limit -> `429` + `Retry-After`.

The frontend should distinguish auth/authorization/upstream/configuration failure rather than collapsing them into a generic "sign in with manager" style error.

## 9. Direct PocketBase access removal

All PocketBase JavaScript SDK/authStore/direct collection reads are removed. Remaining current direct browser reads (notably organization/patron-code reference access) become ASP.NET Core APIs so the browser has one backend contract.

The .NET app serves only explicit DTOs; never expose EF entities directly.

## 10. Deep links

Preserve staff links such as `?request=<id>` and additional-copy `stage`/source-request links.

New request IDs are bigint values. During the temporary migration-compatibility window, old PocketBase request IDs resolve through `LegacyPocketBaseMapping`; after successful resolution the frontend normalizes the URL to the new ID.

## 11. Staff workflow scope

- Ordinary staff/admin: server enforces own-library scope and requires both the StaffUser and referenced library Organization to be active. Library deactivation leaves the StaffUser row/lifecycle flag untouched but makes the account unusable until organization reactivation.
- Super-admin workflow default on each visit: **All libraries**.
- Super-admin may temporarily select a library for workflow queues.
- Do not persist the workflow library filter across sessions.
- Server authorization is authoritative; UI filters never grant access. Role/organization changes that contract or move a StaffUser's resource scope atomically deactivate out-of-scope auto-claim rules, clear out-of-scope open TitleRequest claims with events, and clear out-of-scope open AdditionalCopy claims with system Notes/admin-audit counts; promotion to super-admin is a scope expansion and needs no cleanup. Claim/rule writers revalidate the target StaffUser under the same StaffUser serialization lock so a stale relationship cannot commit after a move/demotion/deactivation.

Super-admin settings context may retain the existing local-browser convenience model where useful, but there is no persistent server-side "current library" state.

## 12. Queue behavior

Preserve initial full-list workflow behavior:

- API returns the complete authorized current set needed by the staff tab model;
- browser applies status/tag/claim/search filters;
- Grid.js renders existing grids;
- continue using latest-load/abort-style safeguards so older async responses do not overwrite newer UI state.

Server-side paging/search is deferred until measured need.

## 13. Analytics

Keep the current staff-facing analytics surface and scope, but change backend implementation to SQL aggregation. The browser consumes a compact aggregate DTO rather than requiring all raw rows.

All staff may see analytics for their own library; super-admin may see all or selected library.

## 14. Patron embed CSP

`/patron` remains dynamically served so the response can set request-specific `Content-Security-Policy: frame-ancestors ...` based on the configured **system-wide** allowlist and request origin/referer rules.

Only super-admins manage allowed origins. Library admins may see the effective embedding state/warning but cannot alter this security boundary.

Serve other frontend assets statically.

## 14.1 Staff profile preferences

The staff contract must preserve the current user-scoped preferences independently of the settings refactor while separating the primary notification address from the optional weekly-summary override:

- `NotificationEmail` — primary staff notification address;
- `WeeklyActionSummaryEnabled`;
- `WeeklyActionSummaryEmail` — optional weekly-summary-only override;
- `PurchaseReminderDefault`;
- `AdditionalCopyReminderDefault`;
- `DefaultMineUnclaimedFilter`.

The authenticated staff/session DTO exposes the primary address and user preferences as needed by the current UI. Ordinary staff self-service profile updates may edit `WeeklyActionSummaryEnabled`, `WeeklyActionSummaryEmail`, `PurchaseReminderDefault`, `AdditionalCopyReminderDefault`, and `DefaultMineUnclaimedFilter`; clearing `WeeklyActionSummaryEmail` means use `NotificationEmail`. The compatibility self-service profile endpoint (`POST /api/asap/staff/profile`) does **not** accept or edit `NotificationEmail`. Staff Access owns primary-email administration: `POST /api/asap/staff/users` accepts it when creating an account, and the target adds a focused authenticated `PATCH /api/asap/staff/users/{id}` for editable staff metadata including `NotificationEmail` (plus other explicitly permitted readable profile fields). Apply the existing library/super-admin management boundaries. The PATCH may deliberately clear `NotificationEmail`; null means no primary ordinary-notification recipient. Entra sign-in never initializes/repopulates this field, so an intentional clear remains effective until another authorized application mutation changes it.

Ordinary staff-directed notifications use `NotificationEmail`. Weekly summaries use `WeeklyActionSummaryEmail` when nonblank and otherwise `NotificationEmail`; if both are null/blank there is no recipient and no message is enqueued. Reject a primary or weekly override ending in `@staff.asap.local`; it is a legacy placeholder, not a deliverable address. This is an intentional normalization from PocketBase, where a summary-enabled user with blank `weekly_action_summary_email` receives no weekly summary and the weekly field is also consulted by some non-weekly notification paths. The target deliberately allows such a user to become weekly-summary eligible through a real `NotificationEmail`; migration reports this and every ordinary-recipient change. These fields belong to `StaffUser`; they are never inherited from system/library settings and are not affected by **Reset inherited overrides**.

### Weekly-summary authorization scope

Weekly-summary data uses the same authorization scope as interactive staff access; recipient selection alone is not sufficient. For `staff`/`admin`, build counts, samples, and links only for that recipient's own **active** library and skip the summary entirely while that library is inactive. For `super_admin`, build the summary across active participating libraries. Links remain ordinary server-authorized staff URLs and cannot broaden scope. Keep recipient+period outbox idempotency. This is an intentional correction from the pinned PocketBase job, which constructs one consortium-wide summary and sends it to every opted-in recipient.

Preserve the existing manual `force` semantics explicitly. A normal scheduled/non-forced invocation remains recipient+period idempotent. An accepted forced invocation creates and returns/logs one `ManualRunId`, enqueues the same authorization-scoped summary path with that ID as an immutable Hangfire job argument, and uses force-run outbox keys so that run can intentionally resend while retries of the same run remain idempotent.

## 14.2 Identifier mutation and retry/recovery contract

One backend capability predicate governs ordinary edit validation and request DTOs. Compare normalized identifier values before mutation; omitted/unchanged values are no-ops. Always require normal authorization and expected `version`. The incomplete-operation barrier is checked first, then stage/placed-BIB protection. Validate persisted starting and proposed ending state so a combined status change/edit cannot bypass a restriction.

| Current state | Changed/cleared identifier | Unchanged identifier | Identifier plus explicit BIB |
|---|---|---|---|
| `suggestion`, `outstanding_purchase`, `pending_hold`, no incomplete operation or successful/retained legacy placed-history protection | Allow normal pre-placement invalidation | Preserve catalog/check state | Invalidate first, then use the existing explicit Polaris BIB validation/reconciliation path; never inherit stale BIB |
| Any state with an incomplete hold operation | `409 hold_operation_incomplete` | Other unrelated permitted edits remain possible | BIB change is also blocked by the operation |
| `hold_placed` or `closed` | `409 identifier_locked_by_stage` | No identifier reset; other actions retain their ordinary stage rules | Reject any identifier change; a changed/cleared BIB is independently forbidden for a placed hold |
| Reopened record with a previous successful hold or retained legacy placed-stage marker (known or null BIB) | `409 identifier_locked_by_stage` | Preserve actual placed BIB | Cannot use reopening or a completed operation to replace/erase the actual hold's BIB |

A permitted pre-placement edit atomically clears old IsbnCheckResult/retry/error/LastCheckedUtc, old BibId, and Identifier found / Identifier number not found in system / Multiple Polaris matches tags. Nonblank replacement becomes `pending`; a clear becomes `skipped_no_isbn`. A new explicit BIB is stored only after normal validation/reconciliation; it is not proof that the new identifier was found. Return the resulting fields, tags and new version together. A rejected mutation changes no field, event, audit, outbox intent or provider state; safe request failure logging is not a successful domain audit entry.

DTOs expose `canEditIdentifier`, `canChangeBib`, `canRetryIdentifierCheck`, and a safe blocking reason derived from that predicate. The frontend renders identifier/BIB controls read-only where blocked, preserves readable values, announces the reason accessibly, and refreshes on 409 without replay. Capability flags never replace backend validation. A completed successful HoldPlacementOperation keeps its immutable BibIdSnapshot; request BibId stays aligned and positive checkout fulfillment uses the actual placed-hold BIB, not a later lookup; unclaimed/cancelled/expired results additionally require the specific tracked final HoldRequestID under porting-spec section 9.2. Preserved legacy placement evidence supplies historical BIB protection, not an authoritative current hold ID. Use the exhaustive marker predicate in migration section 6.10, including all five terminal reasons and status_changed transitions into hold_placed. Marker presence, including explicit null BIB, disables identifier/BIB mutation and retry after reopening even when the reopen and edit are separate committed requests. Do not infer absence of protection from missing provider identity or lack of a target operation.

Keep one `POST /api/asap/staff/title-requests/{id}/retry-identifier-check`-equivalent action. It is legal only for an eligible canonical **suggestion** queue row with a nonblank identifier and `error_max_retries`, no incomplete operation and no placed-hold protection. Atomically reset pending/retry/error/result state, return the new version, and enqueue the same scoped canonical processor; the five-minute recurring job remains the safety net. Stale/ineligible rows return 409 with a safe reason. Provider/configuration failure remains distinct from definitive not-found. No new processor for placed/closed rows is introduced.

### Hold recovery operations

The scoped request/operations DTO exposes the incomplete operation ID, safe State/Phase, version, last recovery check/error, and whether super-admin Reconcile / Resolve succeeded / Resolve not performed is permitted. Do not return raw reply qualifiers, patron payloads, or credentials. Show `operator_required` as a blocking operational condition, not a generic Retry button. A completed placement must not leave edit capabilities incorrectly enabled.

Use the existing super-admin operations surface/service with equivalent routes `POST /api/asap/staff/hold-operations/{id}/reconcile` and `POST /api/asap/staff/hold-operations/{id}/resolve`. Require antiforgery and operation `version`; resolution additionally requires the named outcome, reason and authoritative evidence/reference in `01-PORTING-SPEC.md` section 9.1. Invalid/stale/owned operations return a safe 409; no client-supplied outcome bypasses evidence or execution fencing. Reconcile uses the existing workflow guard and same phase permissions, with inactive-library access only for already-acquired operations. Resolving no-effect never immediately creates a replacement hold. Normal library staff can view their scoped blocked state but cannot authorize operator resolution. Operator-required without decisive evidence remains blocked.

### Fulfillment identity diagnostics

Existing scoped request/operations responses expose a safe hold-tracking reason when terminal correlation is unavailable/ambiguous or provider reads fail (`hold_identity_unavailable`, `hold_identity_ambiguous`, `hold_provider_error`). Staff can understand that a title is protected historically but automatic terminal-hold tracking lacks a proven current hold. Do not expose raw qualifiers/credentials/patron payloads or accept a generic edit that supplies an asserted hold ID. Genuine new-placement/adoption identity comes from the existing provider/operation evidence path; no new hold journal or history UI is needed.

An otherwise completed or imported uncorrelated hold is **not** an incomplete operation. Missing identity neither unlocks BIB editing nor introduces an operation barrier that prevents the existing manual-close/timeout paths. Runtime terminal closure requires exact final HoldRequestID plus expected BIB/patron, normal current scope/Organization/version checks, and no applicable timeout/barrier; positive checkout fulfillment remains title-level. A historical same-BIB terminal row never supplies a current association.

## 14.3 Pickup-preference mutation contract

`PreferredPickupBranchId` and `PreferredPickupBranchName` are not generic editable patron snapshots. Preserve the pinned dedicated pickup-preference workflow:

- load current request/version and live Polaris patron pickup context;
- reject stale request state or changed-since-load live pickup with `409`;
- reject/return `409` before the Polaris update when an incomplete `HoldPlacementOperation` already exists for the request;
- validate the selected pickup branch and perform the idempotent Polaris update;
- if Polaris fails, leave the request pickup fields unchanged and return the appropriate upstream error;
- after success, persist the new pickup ID/name and normal note/event. Generic request edit DTOs do not accept these fields.

Hold placement re-resolves live patron/pickup state immediately before its own external mutation, so a pickup update that completed externally first is the value the hold path uses even if local commits interleave.

## 14.4 Email operations state contract

The administrative email surface must distinguish `pending`, `sending`, `sent`, `failed`, and `suppressed`. Manual **Retry** is available only for `failed` rows and reuses the same business identity/payload; attempting to retry a non-retryable row returns a normal conflict/validation response. `suppressed` is terminal and visible with a safe suppression reason but never exposes Retry. Payload cleanup may remove subject/body only from terminal `sent`/`suppressed` rows after the retention period, so the UI must remain useful from retained delivery metadata even after payload purge. Library admins remain scoped to their own library and super-admins may inspect all authorized contexts.

For `staff_authorization_sensitive` messages, delivery/retry applies the common current staff predicate: active recipient, exact stored/current Entra binding, current AllowedTenantIds, role/resource scope and required Organization activity, plus current message-class address equality. Persist the recipient tuple at intent creation; rebind or removed tenant suppresses the old message even when the StaffUserId/address still match. This is intentionally different from immutable business-event notifications, which may drain after later authorization/participation changes. Business APIs do not convert missing mail/sender/transport configuration into failure of the owning suggestion/workflow mutation; the notification becomes terminally suppressed when configuration is already missing at intent creation, or a previously valid queued row becomes retryable `failed` if transport configuration disappears later.

### AdditionalCopy reopen response

Reopening revalidates a retained claimant under Organization -> StaffUser -> task locks and commits the final claim state with the reopen version. Return the effective claimant fields and a safe `claimClearedReason` when an invalid retained claim was cleared; refresh Mine/unclaimed views and announce the result. The response must never briefly present the historical claimant as an effective open claim. Retained attribution is visible through the existing Notes/history, not a new event table, and reopening never auto-assigns the actor. A stale version uses the normal 409/refresh contract.

### Current tenant-policy session response

Every authenticated staff endpoint, including session, diagnostics and Hangfire dashboard, uses `01-PORTING-SPEC.md` section 7.6. Missing/deactivated/rebound/removed-tenant identities reject the principal and expire the cookie; APIs return JSON `401 staff_session_invalid`, never an OIDC redirect. Current scope/participation denial for an otherwise valid identity returns 403. The staff shell displays a sign-in/access explanation on session loss, not an endless silent retry. Persistent Data Protection keys do not exempt old cookies from loaded tenant policy. The external-config usable-admin startup gate is an operator repair condition, not an invitation to create a new staff session.

## 15. Settings UX

Where a library setting inherits from system defaults, the UI must make the distinction explicit rather than merely displaying the effective value. The backend may assemble the existing combined settings DTO from multiple domain tables; the frontend/API shape does not dictate SQL storage. `13-SETTINGS-SCOPE-INVENTORY.md` controls which values are inheritable, whole-set replacements, system-only, or library-owned. Show:

- effective current value;
- inherited system value;
- whether an override exists;
- clear reset/use-system-default action.

Ordinary inheritable text controls should normalize blank library input to **Use system default** where current behavior treats blank as fallback; do not introduce a hidden explicit-empty override state.

Secret fields are never populated with the actual secret. A blank entry preserves existing value; explicit **Clear** has separate semantics/confirmation.

A library-level **Reset inherited overrides** action affects only inherited configuration. Library-owned custom fields, custom formats, custom rejection templates, and auto-claim rules must remain intact and must not be visually presented as if they inherit from system defaults.

## 16. Existing behavior intentionally retained

Unless a slice discovers a documented defect, preserve:

- all distinct existing workflow statuses/close reasons/tags;
- current title-request and additional-copy distinction;
- current notes + event duplication where system notes are also stored in notes;
- claimant display-name snapshots;
- library scoping rules;
- current material-format system/library inheritance behavior, translated to stable IDs;
- current publication/custom-field UX;
- current analytics metrics;
- current manual job controls, expanded only to allow library-admin own-library execution; this includes explicit forced weekly-summary resend as a distinct manual run with retry-idempotent `ManualRunId`;
- current staff profile preference behavior for weekly summaries, purchase reminders, additional-copy reminders, and the mine/unclaimed default filter;
- current explicit deletion model (no background retention purge);
- existing accessibility affordances.

## 17. Intentional behavior changes to document in release notes

At minimum:

- staff authentication becomes Entra/OIDC/local allowlist;
- patron sessions become 1-hour opaque bearer sessions;
- staff request/config writes gain optimistic concurrency and possible `409`;
- browser no longer talks directly to PocketBase;
- browser assets are locally vendored instead of CDN-loaded;
- email delivery becomes durable five-state (`pending`/`sending`/`sent`/`failed`/`suppressed`) outbox/Hangfire rather than synchronous mail transport; ordinary business mutations remain successful when notification configuration is unavailable, and authorization-sensitive staff mail revalidates current scope/address before delivery;
- background scheduling becomes Hangfire;
- terminal hold fulfillment intentionally corrects the pinned BIB-only matching defect: unclaimed/cancelled/expired outcomes require the tracked final HoldRequestID, while positive checkout remains title-level. Historical BIB protection and missing-ID diagnostics are distinct;
- OutstandingTimeout preserves source unreviewed-suggestion creation-age rejection, not approved-purchase expiry; the four explicit timeout predicates are in porting-spec section 23.2;
- analytics aggregation moves to SQL;
- Polaris calls use system/application credentials rather than staff-specific Polaris identity;
- organization `IsActive` becomes the broad participation switch;
- library admins may run own-library background jobs and inspect/retry own-library failed emails;
- delete audit is intentionally reduced;
- unsupported/ambiguous legacy values may block migration rather than being silently normalized.

The closure-remediation changes above (stage-aware identifiers, inactive-library acquired-hold recovery, retained-claim revalidation, current tenant checks and durable bounded queue progress) are intentional corrections to the pinned source, not a frontend redesign.

The common current staff-eligibility predicate in `01-PORTING-SPEC.md` section 7.6 governs all earlier authentication/scope summaries in this document. The durable legacy placed-BIB guard in `04-MIGRATION-CUTOVER.md` section 6.10 applies after reopening as well as to current placed/closed stages; it is not a new workflow event-sourcing model.
