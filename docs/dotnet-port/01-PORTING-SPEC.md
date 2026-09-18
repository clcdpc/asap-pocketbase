# ASAP .NET Porting Specification

## 1. Scope and outcome

Replace PocketBase/SQLite/Goja with a .NET 10 ASP.NET Core application backed by SQL Server 2022. Preserve the existing vanilla HTML/CSS/JavaScript patron and staff applications as closely as practical. The new application remains self-hosted and lightweight: Node/npm is not required to build, publish, deploy, or run the application; Node remains development/CI-only for frontend tests and Playwright.

The port stays in the existing `clcdpc/asap-pocketbase` repository with `codex/csharp-port` as the long-lived integration branch and #264 as the single final draft PR into `main`. Beginning with Slice 6, temporary slice/work-package PRs are permitted below that branch under `10-CODEX-MULTI-MODEL-TASK.md`; they are development/review mechanics, not independent releases. Once the .NET deployment has been validated in production, rename the repository to `clcdpc/asap`. After successful .NET production cutover, create/verify a permanent Git tag for the final PocketBase implementation at the exact PocketBase commit frozen for that cutover.

## 2. Target solution and source layout

Use a simple solution and feature-oriented application structure:

```text
Asap.sln
src/
  Asap.Web/
    Frontend/
    Patron/
    Staff/
    TitleRequests/
    AdditionalCopies/
    Settings/
    Email/
    Jobs/
    Polaris/
    Analytics/
    Infrastructure/
  Asap.Migration/
database/
  Asap.Database/
tests/
  Asap.Tests/
  frontend/               # JS/jsdom + Playwright support as appropriate
scripts/
  deployment/
  monitoring/
```

Canonical project/namespace names: `Asap.Web`, `Asap.Database`, `Asap.Migration`, `Asap.Tests`, and namespaces `Asap.*`.

Organize `Asap.Web` primarily by feature. Keep controllers/endpoints, DTOs, services, and closely related logic together. Pull code into shared infrastructure only when it is genuinely cross-cutting. Do not create repository/UoW abstractions, MediatR, or a multi-project layered architecture.

## 3. Runtime platform

- .NET 10 / ASP.NET Core 10.
- Framework-dependent IIS deployment.
- Windows Server/IIS with the matching .NET 10 ASP.NET Core Hosting Bundle installed as a prerequisite.
- SQL Server 2022, database compatibility level 160.
- One IIS application instance per environment initially; no distributed cache or multi-node coordination required.
- Hangfire hosted in `Asap.Web` using SQL Server storage.
- IIS app pool configured `AlwaysRunning` with preload so recurring work is not dependent on first-user traffic.
- Canonical business timezone: `America/New_York`.
- Persist timestamps in UTC.

## 4. Data access and transaction model

Use one `AsapDbContext` for ordinary application persistence. Use EF Core directly from feature services/controllers; no repository or Unit of Work layer. Hand-author entities and fluent configuration; do not scaffold from the database.

Use Dapper/ADO.NET selectively for analytics and other genuinely heavy queries. Parameterized SQL should live with the owning feature by default. Create DACPAC-managed views/stored procedures only when there is a concrete reuse, reporting, or operational reason.

Local ASAP-owned state transitions that belong to one business action must be atomic in one SQL transaction: request change, event, tags, outbox insert, claim updates, etc. Never hold a SQL transaction open across a Polaris or Postmark network call. External-side-effect workflows must explicitly reconcile the result before committing the local state that depends on it.

## 5. Database ownership and deployment contract

The database schema is owned by an SDK-style `Microsoft.Build.Sql` project:

- Project: `Asap.Database.sqlproj`
- Target: SQL Server 2022 / compatibility level 160
- Schema: `[asap]`
- `BlockOnPossibleDataLoss = true`
- Hangfire objects live in `[HangFire]` and are not owned by the DACPAC.
- Future exceptional data transformations/backfills are explicit, versioned one-off scripts. Do not add a second general migration framework.

`[asap].[SchemaVersion]` is an explicit app/database compatibility contract. It contains a monotonic integer that is bumped only when the application/database contract changes. A mismatch leaves `/health/live` healthy, `/health/ready` unhealthy, and normal application functionality blocked. Hangfire storage compatibility is an additional startup/worker gate; an acceptable application SchemaVersion alone does not establish dependency compatibility.

Schema 6 is a pre-release reset boundary. Databases created with the earlier OID-based staff identity schema are recreated from the schema-6 DACPAC rather than upgraded in place. This one-time boundary does not weaken normal DACPAC safety: `BlockOnPossibleDataLoss` remains enabled and objects outside the source are not dropped.

`SchemaVersion` is **not** the deployment-change detector. Classify a release by whether it will mutate the production database: application DACPAC changes, explicit release data changes, and Hangfire/dependency-owned schema changes all use the database-changing deployment path. An unchanged application DACPAC is not a file-only determination. Before any required DDL on an existing database, quiesce the relevant app/workers, verify a successful SQL backup, apply the changes under the deployment operator, and validate both application and dependency schema compatibility before startup. Only a proven file-only release skips deployment-created SQL backup; see `05-DEPLOYMENT-OPERATIONS.md` sections 9-10. The release manifest carries the DACPAC SHA-256, and `[asap].[DeploymentState]` records the last successfully deployed DACPAC hash. Any DACPAC hash change triggers backup + DACPAC deployment even when `SchemaVersion` is unchanged (for example, an index, view, permission, or non-contract constraint change). If the target `SchemaVersion` differs while the DACPAC hash is unchanged, treat the artifact as inconsistent and abort deployment.

The underlying `[asap]` tables are application-internal, not a stable reporting/integration API. Add explicit reporting views/contracts later when a durable external reporting need exists.

## 6. Primary key and concurrency rules

- Use `bigint IDENTITY` primary keys for application-owned entities.
- `Organization.Id` is the actual durable Polaris Organization ID and is not an identity-generated surrogate.
- Use SQL `rowversion` on mutable workflow and selected mutable configuration/admin records.
- API DTOs expose rowversion as an opaque Base64 `version` value.
- Mutations send the expected `version` in the request body.
- Stale mutations return HTTP `409 Conflict`.
- Frontend behavior on `409`: refresh/reload current state; do not automatically replay the mutation.

## 7. Staff authentication and authorization

### 7.1 Authentication

Use Microsoft Entra ID/OpenID Connect from day one.

- Multi-tenant Entra application.
- Production and permanent nonproduction initially share one Entra app registration with both redirect URIs configured.
- Staff authentication requests and validates an ID token directly with `response_type=id_token`; ASAP does not redeem an authorization code, acquire access or refresh tokens, or save Entra tokens.
- Configure the app registration as a Web platform with each environment's `/signin-oidc` redirect URI and enable ID tokens. ASAP requires no Entra client secret or client certificate.
- Explicit allowed Entra tenant IDs are configured externally.
- Require and validate both Entra `tid` (tenant ID) and `oid` (object ID) claims. Validate the OIDC issuer/tenant normally and then apply the explicit allowed-tenant policy.
- Entra proves the user controls an account in an allowed tenant. ASAP locates and authorizes the active local `StaffUser` by normalized email; there is no general JIT account creation.
- Resolve the validated authentication email from `email`, falling back to `preferred_username` only when it is a valid real email. `NormalizedUserPrincipalName` is the case-insensitive authorization lookup key.
- `EntraTenantId` and `EntraObjectId` are nullable last-observed sign-in metadata, not authorization keys. A missing or changed stored OID never blocks an email-matched login.
- On successful sign-in, persist the validated `tid`/`oid`, refresh `DisplayName` when supplied, and update `LastLoginUtc`. Do not silently change `UserPrincipalName` or initialize/repopulate `NotificationEmail` from Entra claims.
- No Microsoft Graph lookup/existence validation is required. Staff provisioning requires only email, role, and library where applicable.

Normal Visual Studio F5 uses real Entra/OIDC. There is no interactive Development authentication bypass.

### 7.2 Bootstrap

The external configuration specifies the initial super-admin email/UPN plus optional display and notification values. Legacy tenant/object fields may remain readable but are not required or used as authorization identity. Only when the `StaffUser` table is empty may normal startup create that email-authenticated account. The one-time migration separately hard-gates on at least one active `super_admin` with a valid authentication email; when needed it provisions or promotes the configured bootstrap email while the application remains stopped, reports the intervention, and re-runs the gate.

### 7.3 Cookie/session

- Server-managed ASP.NET Core authentication cookie.
- HttpOnly/Secure/SameSite settings appropriate for OIDC and the production host.
- Approximately 8-hour sliding session.
- The authentication ticket stores `StaffUserId`, the normalized authentication email, and the validated Entra `tid` that established the session. On every request, reload the row and require the ticket email to equal the current normalized email and the ticket tenant to remain in `AllowedTenantIds`. Deactivation, an authentication-email change, or tenant removal expires the cookie; OID or display-name changes do not.
- Staff APIs return JSON `401` for unauthenticated requests and `403` for authenticated-but-unauthorized requests; do not redirect API calls to OIDC.
- Explicit sign-in endpoint starts OIDC.
- `returnUrl` is accepted only when local/relative and defaults to `/staff`.
- `/staff` shell can load anonymously and displays a simple ASAP-branded sign-in view with a **Sign in with Microsoft** action.

### 7.4 Roles and scope

Roles are constrained string codes: `staff`, `admin`, `super_admin`.

Rules:

- `super_admin` belongs to Organization `1` (system).
- `staff` and `admin` reference exactly one non-system library Organization. `Organization.IsActive` is **not** a row-validity invariant for `StaffUser`; an otherwise-active account may remain attached to an inactive library for lifecycle/history preservation.
- SQL + C# invariants prevent invalid role/org combinations.
- At least one usable super-admin with a valid authentication email must remain. Demotion, deactivation, email change, or any other mutation that could reduce that count uses the existing transaction-owned invariant lock and prospective check.
- Library admins can manage staff/admin accounts in their own library, including authentication email, readable profile values, demotion, and deactivation; only super-admins manage super-admin accounts.
- Library admins may demote or deactivate themselves; a library is allowed to have zero active library admins. The global minimum is at least one usable super-admin under section 7.6.
- Creating an account requires email, role, and library where applicable. Changing the normalized authentication email uses the ordinary authorized metadata mutation with uniqueness, row-version, scope, and audit enforcement; it clears the last-observed Entra tenant/object and last-login metadata until that identity signs in successfully. There is no manual Entra rebind operation.
- `UserPrincipalName` is the administrator-owned authentication email and never changes during sign-in. `DisplayName` may refresh from validated claims. `NotificationEmail` is separate application-owned contact data.
- Deactivation uses `IsActive = false`, never hard deletion.
- Re-adding the same normalized email reactivates the existing record while preserving preferences and unrelated metadata. For `staff`/`admin`, the referenced library must be active; Organization `1` is permanently active for `super_admin`.
- Promotion to super-admin atomically changes organization to `1` and broadens authorization scope; it does not require claim/rule cleanup.
- Demotion from super-admin requires a destination library.
- Treat any `Role`/`OrganizationId` mutation as a lifecycle operation, not a metadata-only edit. Compare the user's old and new **resource scope**. When the new scope excludes libraries the user could previously operate in (for example `super_admin` -> `admin`/`staff` at Library A, or Library A -> Library B), perform the scope-contraction side effects in the **same local SQL transaction** as the StaffUser mutation: deactivate active `FormatAutoClaimRule` rows outside the new scope; clear that user's claims from open/actionable `TitleRequest` rows outside the new scope and append the normal TitleRequest claim-cleared event; clear that user's claims from open `AdditionalCopyRequest` rows outside the new scope and append a concise system entry to the AdditionalCopy `Notes`; preserve closed/nonactionable claimant snapshots/history for both request types; and record the administrative role/organization change plus per-type cleanup counts in `AdministrativeAudit`. If any cleanup or StaffUser update fails, roll back the whole mutation. Do not invent an AdditionalCopy event table solely for this cleanup.
- A deactivated staff member's active auto-claim rules are deactivated and their claims on open/actionable TitleRequests **and open AdditionalCopyRequests** are cleared using the same lifecycle-cleanup service with an empty usable scope. TitleRequests receive the normal event; AdditionalCopyRequests receive the concise system Notes entry; the administrative audit records both cleanup counts. Closed history retains claimant snapshots. Reactivation never automatically restores old claims or rules.
- Super-admins may be cross-library auto-claim targets.
- The super-admin workflow queue defaults to **All libraries** on each visit; a selected library is a temporary filter and is not persisted across sessions.
- The settings UI may retain its existing local-browser library-context convenience, but the server has no persistent "current library" state for a super-admin.

`StaffUser` keeps the canonical authentication email in `UserPrincipalName`/`NormalizedUserPrincipalName`, nullable last-observed Entra tenant/object metadata, display and notification values, role, organization, active state, preferences, and last-login timestamp. Preserve all existing preferences. Only `DisplayName` and last-observed metadata refresh during sign-in; authentication and notification email remain administrator-owned.

Recipient semantics are explicit in the target: `NotificationEmail` is the primary address for ordinary staff-directed notifications such as assignment/purchase/additional-copy reminders. The weekly summary uses `WeeklyActionSummaryEmail` when nonblank and otherwise falls back to `NotificationEmail`. `WeeklyActionSummaryEmail` is a user-editable optional override; clearing it resumes the primary-address fallback. `NotificationEmail` is managed through Staff Access by an administrator authorized to manage that account (with the normal super-admin boundary), not by the ordinary self-service profile endpoint. Administrators **may deliberately clear `NotificationEmail`**; null then means no ordinary staff-notification destination, and a weekly summary with no weekly override also has no recipient. Any address ending in `@staff.asap.local` is a legacy placeholder and is never accepted or used as a notification destination.

This is an **intentional behavior normalization**, not strict recipient parity. In PocketBase, weekly summaries require a nonblank `weekly_action_summary_email`, while several ordinary reminder/assignment paths also consult that field. In the target, a migrated user with `WeeklyActionSummaryEnabled = true`, no weekly override, and a valid `NotificationEmail` becomes eligible for weekly summaries through the fallback. That newly eligible behavior is desired. Migration/reconciliation must calculate and report old-versus-target effective recipients/eligibility for weekly summaries and ordinary staff notifications so no recipient change is silent.

### 7.5 Locking and staff lifecycle serialization

Use one locking hierarchy for multi-row authorization/workflow mutations:

```text
Organization
    -> StaffUser
        -> TitleRequest / AdditionalCopyRequest
            -> dependent claim/rule/operation rows
```

An operation locks only the categories it actually needs, but if it needs more than one category it acquires them in this order. When multiple rows in one category are needed, acquire them in stable key order. Use short SQL transactions with `UPDLOCK,HOLDLOCK` (or an equivalent transactionally correct SQL Server pattern) for rows that are acting as serialization points. Never keep these transactions open across Entra, Polaris, or Postmark network calls. The `ASAP:ActiveSuperAdminInvariant` application lock is taken before row locks whenever a mutation can remove usable super-admin eligibility; all remaining row categories retain the hierarchy above.

`StaffUser` is the common serialization point between lifecycle mutation and creation/change of operational relationships to that user. A lifecycle mutation locks/re-reads the target StaffUser before changing active/role/library state and before cleanup. Any manual TitleRequest assignment, AdditionalCopy creation/inherited assignment/reassignment/reopening, `FormatAutoClaimRule` create/change, or automatic rule execution that would establish or reactivate a StaffUser relationship locks/re-reads the target StaffUser in its own transaction and validates the **current** active/role/library eligibility only after acquiring that lock. A writer that waited behind a deactivation/move/demotion therefore cannot commit a stale relationship.

When the same operation also depends on library participation, lock/re-read the owning Organization first. This common order is mandatory in implementation and in real-SQL concurrency tests; do not create feature-specific reverse lock orders.

### 7.6 Common current staff eligibility and tenant-policy changes

Use one common eligibility function with explicit identity evidence, required role/resource scope, and a `requireParticipation` context. Request evidence is StaffUser ID, normalized authentication email, and sign-in tenant. Require an active current row, exact email equality, current tenant allowance, valid role/scope, and participation where applicable. Stored tenant/object metadata is not part of this decision.

Use the same rules at OIDC completion, on every authenticated request, for new operational relationships, and for usable-super-admin checks. Authorization-sensitive mail snapshots the normalized authentication email and revalidates it with current role/scope/participation and destination ownership before send. Staff may be assignment or auto-claim targets before first login.

**Stored claim activation:** whenever a claimant becomes effective on open operational work (including an inherited claim or reopening without changing the FK), evaluate current StaffUser eligibility for that work's library with `requireParticipation = false`. This validates active state, authentication email, and same-library staff/admin or cross-library super-admin scope, but does not clear a valid stored relationship merely because its Organization is inactive. Existing closed history is not rewritten.

External JSON remains startup-only. For a populated database, a configuration-only restart or deployment must validate the prospective tenant set against current SQL and prove at least one usable super-admin **before** normal application functions or Hangfire workers are enabled. The operator's configuration-change procedure stops/quiesces the app, validates a staged candidate configuration, and only then activates it and restarts; rejected candidates do not replace the last accepted external file. A direct manual edit that bypasses preflight still fails closed at startup: liveness/diagnostic logging remain available, readiness is false, and no business endpoints/workers start until the operator repairs the configuration. Do not silently restore a removed tenant, promote someone, or use populated-table startup bootstrap to bypass this gate. Empty-table bootstrap and the explicit migration bootstrap gate retain their existing distinct rules, each validated against the prospective allowed tenants.

After a successful restart with tenant T removed, T's existing cookies fail on their next use even with the same persistent keys. Other currently eligible allowed-tenant identities continue to work. Re-adding T only removes the tenant-policy obstacle: it cannot override deactivation, a changed binding, current role/library restrictions, expired/revoked cookie state, or terminal suppression of queued mail. No invalid authorization is resurrected by the allowlist alone.

## 8. Patron authentication and session model

Patrons continue to authenticate using Polaris barcode/PIN. Do not persist a long-lived patron user/account table. Polaris is authoritative for current patron data.

The application issues an opaque bearer session token:

- 256-bit cryptographically random value, Base64URL encoded.
- Return plaintext token only to the browser.
- Store only `SHA-256(token)` in SQL.
- Store the plaintext barcode with the session because it is needed for subsequent Polaris lookups; never store PIN.
- Absolute 1-hour expiration; no sliding extension.
- Multiple active sessions for a barcode are allowed.
- Logout revokes the presented session.
- Browser stores the token in `sessionStorage`.
- Session stores only minimum context: barcode, token hash, home/experience/effective organization IDs, created/expiration timestamps.
- Every patron-authenticated request must resolve the session's `EffectiveOrganizationId` and require that Organization to still have `IsActive = true`, in addition to token-hash match, expiration, and revocation checks. A valid token for an inactive library is not authorized for patron business APIs.
- Final session issuance is serialized against library deactivation. After Polaris authentication and effective-library resolution, create the session in a short SQL transaction that locks/re-reads the effective Organization row (`UPDLOCK`/`HOLDLOCK` or an equivalent transactionally correct pattern), verifies `IsActive = true`, then inserts the session. Library deactivation updates the Organization and revokes its patron sessions in one transaction using the same Organization row as the serialization point. If issuance commits first, deactivation subsequently revokes that newly inserted session; if deactivation commits first, issuance observes inactive state and refuses the session.
- Library reactivation never clears `RevokedUtc` or resurrects old bearer tokens; the patron must authenticate again.
- Hangfire cleans expired sessions approximately daily.

Use ASP.NET Core partitioned IP rate limiting for login attempts, default `20` attempts per IP per 5 minutes, configurable. Return generic `429` with `Retry-After`. Do not implement a persistent barcode lockout initially.

Permanent nonproduction may accept any patron valid against its nonproduction Polaris environment. A dedicated patron allowlist may be added later only if it proves useful and simple.

## 9. Polaris integration

Use the current/latest `Clc.Polaris.Api` package, including CLC prerelease builds when appropriate. Do not create a second PAPI protocol client in ASAP. If required functionality is missing, extend the package rather than duplicating signing/protocol code locally.

All initial PAPI operations use the configured system/application Polaris credentials. Do not reproduce staff-specific Polaris authentication or persisted per-staff Polaris user IDs. A configured system Polaris user ID is required for mutation attribution where Polaris requires it; ASAP's own event/audit records use the actual Entra staff actor.

Retry policy:

- Safe/read operations may use bounded transient retries.
- Do not blindly retry mutations.
- Hold placement uses explicit ambiguity reconciliation.
- Pickup-preference update is treated as idempotent and does not require its own operation journal.

### 9.1 Hold placement journal

Use the existing purpose-specific `HoldPlacementOperation`, not a generic distributed-operation framework. The states, fields, and filtered one-incomplete-operation uniqueness in `03-DATABASE-DESIGN.md` section 12 implement this protocol. All scheduled, manual, and interactive placement/recovery paths use it; a unique operation row is not itself an execution lock.

#### Acquisition and request barrier

Before acquiring **new** placement work, use a short transaction in the common hierarchy: lock/re-read Organization and require active participation, lock/re-read TitleRequest/current version, verify hold eligibility including the applicable timeout predicate, and insert the next numbered operation only if none is incomplete. Freeze the authorized request identity, patron, and BIB inputs. Unique `(TitleRequestId, AttemptNumber)` and `TitleRequestId WHERE CompletedUtc IS NULL` indexes are authoritative. Acquisition commits before any external call.

While incomplete, the operation blocks status/close/reopen, deletion, identifier/BIB change, AutoHold change, and ordinary recorded-pickup changes with `409 hold_operation_incomplete`; claim and unrelated descriptive changes remain allowed. Every conflicting mutation checks this barrier under the same request serialization point. The dedicated pickup action checks it before Polaris. If that idempotent pickup call began first, a successful result may finish its local pickup snapshot update despite later hold acquisition; the hold executor re-reads live patron/pickup immediately before its create marker and persists that pickup. A failed pickup call changes no local snapshot. No SQL transaction spans a network call.

#### Exclusive executor and takeover

Each acquired operation has a random `OwnerToken`, monotonically increasing `ExecutionEpoch`, and SQL-clock `LeaseExpiresUtc`. Use a two-minute renewable lease, heartbeat every 30 seconds while executing, and a maximum 60-second timeout per provider call. Lease timings control ownership only, never evidence of an external outcome. All incomplete-operation phase/result/ownership writes require the current token/epoch and an unexpired lease; acquisition/takeover atomically establishes that ownership. A queue-progress-only skip of another live owner does not mutate that operation or grant execution rights. Acquisition/renewal/takeover is atomic; takeover of an expired or explicitly released owner increments the epoch. Use Organization -> TitleRequest -> operation order when those rows are needed; recovery does not require Organization to be active. A stale owner must stop, cannot update the journal, and cannot obtain a fresh mutation permit.

A lease cannot fence a request already sent to Polaris, or eliminate the pause between committing a dispatch marker and sending it. Therefore a takeover does **not** authorize replay of a marked create/reply. Such takeover can observe/reconcile only, unless durable evidence establishes a specifically permitted later phase. Do not use an in-process mutex, Hangfire job retry, lease expiry, HTTP cancellation, or a second operation row as permission to repeat an uncertain mutation. Disable hidden SDK/HTTP retries and auto-replies for these mutation calls.

#### Durable phases and evidence

`Phase` advances monotonically within one numbered operation. `State` is `in_progress`, `ambiguous`, `operator_required`, or terminal `succeeded`/`no_hold`/`failed`. Only a terminal state has `CompletedUtc`.

| Durable phase | Permitted work and next durable boundary |
|---|---|
| `acquired` | No create marker exists. The current owner may perform safe reads/prechecks and the **first** create, even after later library deactivation. A precheck that ends the operation without dispatch is definitive local no-mutation evidence. A takeover can safely continue this phase because the old owner cannot win the fenced transition below. |
| `create_started` | Immediately before the create, atomically verify owner/epoch/phase and persist `CreateStartedUtc`, final live PatronID/pickup, BIB and all non-secret create inputs. Commit, then that winning execution may invoke create **once**. A crash before the actual send is intentionally indistinguishable from possible acceptance; never infer no-effect merely because no response was stored. |
| `reply_ready` | Before any required reply, durably persist the create response's `RequestGUID`, normalized `TxnGroupQualifier` (including the source `TxnGroupQualifer` alias), `TxnQualifier`, provider status/type, any hold ID, and the exact approved reply answer/state/requesting Organization. Keep only necessary safe fields, not a raw patron payload. The pinned workflow's required status-5 reply uses answer `1`, state `3`; do not broaden its policy prompts. A create response requesting a reply is not completed placement. |
| `reply_started` | Under current ownership, atomically persist `ReplyStartedUtc` and the exact persisted context being used, commit, then send the **first** reply once to the same RequestGUID. `reply_ready` with no reply marker permits this continuation; a missing/invalid context requires operator resolution. An uncertain marked reply is never blindly repeated or replaced with a create. |
| `result_recorded` | Persist a classified final result and evidence. A final success can be completed locally without another provider mutation; a definitive no-effect result may close as `no_hold`/`failed`. The final response may be recorded in the same transaction as local completion. Otherwise recovery finishes the durable recorded result idempotently. |

A normal create with no reply requirement can move directly from `create_started` to `result_recorded`. The provider adapter must distinguish final success, pending policy/reply state, and failure; a generic HTTP success or the source helper's `ok` flag alone is not proof of a finalized hold. Persist received response evidence before any subsequent external step.

**Succeeded:** a documented final create/reply response, or authoritative correlated provider status, proves a finalized hold for the snapshotted patron and BIB (including a documented final duplicate-hold outcome under the preserved workflow). Record the operation/request identity and authoritative final HoldRequestID separately as required by section 9.2. A merely similar patron/BIB lookup is not proof that this exact incomplete request finished. Complete the operation, set/retain TitleRequest.BibId to `BibIdSnapshot`, perform the normal hold-placed transition/event/tags and durable notification intent once, all in one local transaction. Recovery-only local completion is allowed for inactive libraries. Notification suppression rules still apply.

**Definitively not performed:** either the fenced journal proves no create dispatch marker was ever committed, or a documented correlated final provider result proves the attempted operation cannot produce a hold and no required reply remains pending. Terminal `failed` is restricted to this proven-no-effect class; transport failure is not that evidence. Before terminalizing an uncertain marked dispatch as no-effect, also prove that the original executor cannot later send it (quiesce/terminate that executor and account for in-flight provider work). Provider cancellation/expiry is evidence only when its documented final semantics exclude later acceptance. The absence of a local response, elapsed time, repeated empty searches, or a single empty lookup is never sufficient. After terminal no-effect completion, a new numbered attempt is allowed only on a later ordinary eligible visit, after current prechecks and a fresh active-Organization acquisition; there is no grace-based replay rule.

**Still ambiguous:** timeout, disconnect, lost response, unclassified/malformed result, uncertain reply, uncorrelated lookup, or provider visibility lag leaves the same operation incomplete and the barrier intact. Read-only reconciliation may establish final positive/negative evidence; it cannot turn uncertainty into a new create. This pack assumes **no** undocumented provider idempotency, final-negative lookup, or request-lifetime guarantee. Without a verified provider guarantee, automatic replay of a marked mutation is forbidden and operator resolution is the defined outcome, not an implementation decision to invent later.

#### Recovery cadence, inactivity, and operator resolution

The first phase of the existing workflow orchestrator examines incomplete operations independently of Organization activity, using the durable `HoldRecovery` queue in section 23.1. It runs on every configured WorkflowProcessing invocation (hourly by default), can also be invoked through the same authorized operations service, and observes the same non-overlap guard. Unexpired ownership is durably skipped in QueueProgress for this cycle without changing the operation or incrementing RecoveryAttemptCount. Expired ownership is taken over as above. A safe `acquired`, `reply_ready`, or `result_recorded` continuation is performed by its one current owner. Other incomplete states allow read-only observation. Increment RecoveryAttemptCount and persist LastRecoveryUtc after each owned recovery evaluation is durably handled. After at most **three recovery evaluations** without authoritative resolution, persist `operator_required`; use that state immediately when the adapter cannot obtain decisive evidence or reply context is missing. Safe-read transport retries remain bounded inside an evaluation. Time/cadence bounds when the condition is surfaced, not whether a mutation happened. Operator-required rows stay visible and blocking, but leave the automatic scan until an explicit Reconcile action requests another evaluation.

An inactive Organization cannot acquire a new operation, but previously acquired authorization is sufficient to safely finish/reconcile that same operation, including an unstarted first create or first required reply. No later numbered placement attempt is permitted while inactive. Timeouts/edit actions cannot erase the barrier. Recovery precedes ordinary timeout/promotion/placement/fulfillment phases and is not skipped by their participation filters.

Use the existing super-admin operations surface for **Reconcile**, **Resolve succeeded**, and **Resolve not performed**. Resolution requires current super-admin authorization, the operation version, reason, and authoritative evidence tied to the operation. In the final short mutation transaction, use the common Organization -> acting StaffUser -> TitleRequest -> operation order where needed and revalidate the actor/operation after locking; evidence collection stays outside SQL transactions. All reconciliation/resolution actions share the existing workflow non-overlap guard and operation ownership checks. A resolution of an ambiguous marked dispatch also establishes that no superseded executor can later dispatch it; quiesce/terminate affected workers when necessary. Reconcile cannot bypass the phase permissions. An explicit Reconcile evaluates that one operation without resetting queue progress or the recovery counter; if it remains unresolved, retain operator_required rather than silently enrolling it in unlimited automatic retries. Resolve succeeded performs the same idempotent local completion; Resolve not performed closes with verified no-effect evidence and does **not** immediately create another hold. When no conclusive evidence exists, leave `operator_required`; there is no force-retry/assume-failure escape hatch. Record actor, evidence type/reference, reason, and before/after state in the existing request event/AdministrativeAudit, without secrets or raw PII. Keep operation/evidence history with the existing request-deletion rules. Expose safe state, phase, last check/error, and the supported action through the scoped API/UI; never expose provider reply qualifiers or credentials.

The normal Polaris **Test connection** function remains a narrow authentication/connectivity test. Broader real PAPI validation is handled separately by release validation.

Polaris settings are system-only and live in `[asap].[PolarisSettings]`, constrained to system Organization `1`. There are no library-level Polaris overrides. Changes may be saved even if a connection test fails; enforce local shape validation and provide an explicit test action. In-memory configuration cache is invalidated immediately so changes apply to the next PAPI operation.

### 9.2 Hold identity and fulfillment correlation

**Intentional correction to the pinned PocketBase fulfillment tracker:** positive checkout fulfillment is title-level; a terminal hold outcome is specific-hold-level. BIB protection alone is not terminal-hold correlation. Use the existing HoldPlacementOperation/provider adapter, not a second journal, protocol client, or fulfillment-history subsystem.

| Existing field/evidence | Meaning and authority |
|---|---|
| `PolarisRequestGuid` | The provider `RequestGUID` used for the create/reply conversation and supported recovery lookup. It is not automatically the final persisted HoldRequestID. |
| `TxnGroupQualifier`, `TxnQualifier` | Provider conversation/reply context, not hold identity. Preserve the documented source alias handling. |
| `PolarisHoldId` | The authoritative final Polaris `HoldRequestID` of the particular placed/adopted hold. Nullable when the provider supplies no proven mapping; never fill it with a RequestGUID, BIB, transaction qualifier, or arbitrary same-BIB match. |
| `BibIdSnapshot` | Immutable BIB of the successful placement/adoption. Identifies the title, not which hold supplied a terminal outcome. |

PAPI's documented create/reply conversation exposes RequestGUID, whereas patron hold-list rows expose HoldRequestID separately from BibID. Do not assume an interchangeability or GUID-to-ID lookup that the selected package/provider version has not established. The adapter must preserve the distinction, capture the final hold identity from a documented final response or authoritative correlated read **whenever available**, and retain the safe evidence/source of that mapping in the operation's existing evidence fields. Package-version contract tests and live validation verify the mapping; absent provider capability has the defined uncorrelated fallback below, not a guessed mapping. `11-CURRENT-POCKETBASE-REFERENCE.md` section 18 records the executable/documentation anchors.

For a new hold, persist any authoritative final HoldRequestID with the final result/local completion. Final-success proof and final-ID availability are separate: a documented successful create/reply may complete as succeeded even when the final ID cannot be obtained; keep PolarisHoldId null and surface uncorrelated fulfillment rather than replaying create or manufacturing identity. A later safe correlated read may fill a previously null ID on that **same** completed operation, with evidence; it cannot replace an already recorded different ID, alter its BIB snapshot, re-complete placement, or generate another placement notification. This narrow identity-only enrichment is not incomplete-operation recovery: under ordinary active-Organization -> TitleRequest -> operation locks, require unchanged request/operation rowversions, the same latest succeeded operation/patron/BIB and a still-null PolarisHoldId before atomically writing the proven ID and evidence. It does not reuse an expired executor lease, change Phase/State/CompletedUtc, or create a new attempt. Incomplete operations still use every ownership/lease rule in section 9.1.

For **runtime existing-hold adoption**, the preserved staff/BIB or documented duplicate-hold path must retain the selected live hold row's authoritative HoldRequestID when supplied. Reuse the same operation lifecycle: acquire under the existing active-Organization/request rules, record `OutcomeEvidenceKind = existing_hold_adoption`, the exact patron/BIB/hold evidence, and complete the normal hold-placed transition once. A read-only adoption can advance from `acquired` to `result_recorded` without create/reply markers; do not claim a create was sent. A single eligible nonterminal hold can be adopted after complete provider reads; several same-BIB candidates need provider evidence identifying the particular adopted hold, not first-row, newest-row, or queue-position guessing. A Boolean `patronHasHoldForBib` alone cannot supply an ID. Ambiguous adoption leaves workflow state unchanged, surfaces a safe diagnostic, and uses existing operator-required resolution if an operation has been acquired. This is actual runtime evidence, not permission for migration to fabricate operations.

For runtime tracking, use the latest succeeded placement/adoption operation by AttemptNumber for that TitleRequest; subsequent failed/no-effect attempts do not become hold identity. Its frozen patron/BIB must agree with the current placed request. If that operation has no final ID, do not fall back to an older successful operation's ID. A conflicting or incomplete operation remains subject to the existing barrier. Reopening does not erase the historical BIB/identity evidence; any later genuine successful placement/adoption records its own operation and becomes the tracked identity. Migration creates no synthetic succeeded operation and does not infer a current hold from historical terminal evidence.

Fulfillment runs only for currently eligible `hold_placed` requests, subject to the ordinary participation, applicable HoldPickupTimeout, scope, and request-version rules:

| Provider evidence | Allowed local result |
|---|---|
| Successful current-checkout read for the expected patron with a checkout whose BibID is the expected placed BIB | Preserve title-level positive fulfillment: `closed/hold_completed`, ordinary `fulfilled` event/system note. No final hold ID is required. |
| Authoritative row for the tracked HoldRequestID **and** expected BibID/patron, status `unclaimed` | `closed/hold_unclaimed`, ordinary `fulfilled` event/system note, once. |
| Same exact identity checks, status `cancelled` | `closed/hold_cancelled`, ordinary `fulfilled` event/system note, once. |
| Same exact identity checks, status `expired` | `closed/hold_expired`, ordinary `fulfilled` event/system note, once. |
| Old/different hold for the same BIB; different BIB even with a matching ID; active tracked hold; no matching terminal row | No terminal closure. A different row is not evidence about the tracked hold. |
| Missing final hold identity, unresolved/conflicting correlation, malformed/failed required provider response | Preserve workflow state; record/surface a safe diagnostic, not a fabricated terminal outcome. |

Check current checkouts first. If that required read fails, preserve state rather than falling through to an apparent terminal outcome. A positive checkout needs no later terminal-status reads. Otherwise use an authoritative lookup of the tracked hold where supported, or successfully completed status-filtered reads with exact ID/BIB/patron matching; no arbitrary first same-BIB row may decide the outcome. Conflicting statuses for the tracked ID are ambiguous, not an opportunity to choose result ordering. A required read failure prevents terminal closure; absence is not proof of cancellation/expiry.

After provider reads, lock/re-read Organization (active), then TitleRequest, expected version, current `hold_placed` status, applicable timeout, barrier and tracked operation/patron/BIB/hold identity before committing closure. Scope/authorization for manual execution is revalidated normally. Changed local evidence or a stale version discards the result. One winning local transition records its close reason and existing event/note exactly once; no new rejection-mail class or external hold cancellation is introduced.

For imported/otherwise uncorrelated placed requests, `legacyBibProtection` can supply a known expected historical BIB but **never** a final hold ID. Unknown BIB stays unknown. Preserve state on uncorrelated terminal evidence and show `hold_identity_unavailable` (or `hold_identity_ambiguous` / `hold_provider_error` as applicable) in existing scoped request/operations diagnostics, without raw patron payloads. Missing identity is not an incomplete placement and must not manufacture an operation/barrier. Existing manual closure and configured local timeout paths remain available under their ordinary guards; title-level checkout fulfillment remains available when its expected BIB is known. See `06-TESTING-CI.md` section 10.2 F2.

## 10. Current-data/reference caching

Polaris remains authoritative for live patron and reference information.

- Refresh patron data at login, before existing live-data-dependent actions, and before creating an email outbox record that needs the current patron email.
- Use a very small `IMemoryCache` window to collapse duplicate patron reads; never cache a PIN.
- Cache organization/patron-code/material-type/pickup-branch reference data in memory for roughly 10 minutes.
- Provide super-admin manual refresh.
- Warm common reference caches at startup when practical; failure is nonfatal and should not make readiness fail solely because Polaris is unavailable.
- Do not add distributed or SQL reference-cache synchronization.

## 11. Organizations

`Organization` is the durable ASAP record for system/participating libraries:

- Organization `1` is the real Polaris system organization and is permanently active.
- Persist the system row plus participating/historically referenced library organizations.
- Do not persist branch rows as first-class durable ASAP organizations; branches can remain cached reference data.
- Newly discovered Polaris libraries are inserted/available but inactive until a super-admin explicitly activates participation.
- Activating a library immediately makes it usable using inherited system defaults; there is no draft/onboarding lifecycle.
- Deactivation sets `Organization.IsActive = false` and retains history/FKs. It does **not** mass-set `StaffUser.IsActive = false`; staff-account lifecycle and library participation are separate.
- Deactivation blocks new patron/staff activity for the library. In one SQL transaction, set `Organization.IsActive = false` and revoke current patron sessions whose `EffectiveOrganizationId` is that library, using the Organization row as the serialization point shared with final patron-session issuance. Authorization for library-scoped staff/admin requires both `StaffUser.IsActive = true` and `Organization.IsActive = true`, and every patron bearer request independently revalidates the effective Organization, so a missed/racing revocation cannot preserve access.
- Library deactivation does not run staff-deactivation side effects such as clearing each staff member's claims or disabling their personal auto-claim rules solely because the organization was disabled; those rows remain stored but are unusable while the library is inactive.
- Super-admins retain historical/settings access to inactive libraries.
- Claims, staff relationships, and configuration remain associated with the historical organization. Reactivating the library restores authorization eligibility for StaffUsers that were already `IsActive = true` without rewriting those staff rows; individually inactive StaffUsers remain inactive.

Persist minimal organization metadata: Polaris ID, display/name fields, abbreviation as useful, active flag, and optional last-refreshed metadata.

## 12. Configuration model

Do **not** use a generic/catch-all `[asap].[Settings]` table or EAV key/value store. Split configuration by business domain so scope, security, inheritance, and relational structure are explicit. `13-SETTINGS-SCOPE-INVENTORY.md` is the normative field-by-field contract.

System Organization `1` remains the real Polaris system/default scope; do not introduce null/negative/synthetic scope IDs.

Use these primary configuration domains:

- `[asap].[SystemSettings]` — true system-only application values such as staff/Leap/icon URL patterns and system patron messages; constrained to Organization `1`.
- `[asap].[PolarisSettings]` — system-only Polaris integration configuration/credentials; constrained to Organization `1`.
- `[asap].[WorkflowSettings]` — limits, eligibility, automation, timeout behavior, and related common-creator behavior/text; Organization `1` is the complete system default and library rows contain only nullable field overrides.
- `[asap].[PatronSettings]` — patron-facing page/messages and fixed duplicate-status labels with the same field-level inheritance model.
- `[asap].[EmailSettings]` — Postmark transport/sender configuration with system default + nullable library overrides; reusable secret values are protected ciphertext.

Use relational tables rather than JSON when the configuration has meaningful identity, ordering, children, or whole-set override semantics: `PatronEmbedAllowedOrigin`, `ExternalSearchProvider`/`ExternalSearchProviderOverride`, `PublicationOptionSet`/`PublicationOption`, `CommonCreatorSet`/`CommonCreatorTerm`, `PatronCodeEligibilitySet`/`PatronCodeEligibilityMember`, and `PatronCustomField`/`PatronCustomFieldOption`/`MaterialFormatCustomFieldRule`. Keep `MaterialFormat`/`MaterialFormatOverride`, `FormatAutoClaimRule`, `EmailTemplate`, and `Branding` as specialized domain models. Built-in material-format field behavior is strongly typed; custom-field per-format mode/label behavior uses `MaterialFormatCustomFieldRule`. No competing JSON rule blob remains.

For inheritable scalar tables, the system row holds complete configured defaults and a library row is sparse. A null library field means inherit that field from Organization `1`, even when the same row overrides other fields. For ordinary overrideable text settings, trim library input and normalize blank to no override where current PocketBase behavior treats blank as fallback. For whole-set tables, absence of a library set means inherit the complete system set; presence means complete replacement, while preserving current blank-means-inherit behavior.

The UI must make inheritance explicit: show the effective value, identify whether a library overrides it, show the inherited system value, and provide a clear **Use system default / Reset override** action. Library-owned configuration such as custom fields, custom formats, custom rejection templates, and auto-claim rules must not be presented as inherited values.

C# seeds/ensures required system/default records and built-in rows when missing but never overwrites persisted administrator choices merely because code defaults change. Missing required operational configuration is reported through readiness/diagnostics rather than silently manufacturing credentials.

A library-level **Reset inherited overrides** operation clears only inheritable overrides. It must not delete library-owned custom fields, custom formats, custom rejection templates, auto-claim rule history/active rules, staff, requests, or other business records.

### 12.1 External configuration

Use one tightly ACLed JSON file outside the application directory for environment/bootstrap/infra configuration. Example setting: `Asap:ConfigFile` points to it. It contains items such as:

- ASAP/Hangfire connection strings
- Entra client ID and allowed tenants; no Entra client credential is configured
- initial super-admin email/UPN and optional profile/contact seed values; legacy tenant/object values are optional compatibility inputs
- environment marker/name and `Environment.IsNonProduction`, the application-level switch for section 14's recipient-domain safety rule
- filesystem paths (logs, environment-specific Data Protection key ring, backup/reporting paths as needed)
- `EmailSafety.AllowedRecipientDomains`, the nonproduction exact-domain email allowlist governed by section 14
- Hangfire recurring schedules
- Hangfire/job queue processing limits using the documented queue-specific -> timeout-family -> global fallback precedence
- deployment/environment-specific host information

It does **not** become a second copy of ordinary SQL-managed application settings. Complete Polaris and Postmark operational settings live in SQL.
Startup does not copy/bootstrap Polaris or Postmark settings from external JSON into SQL. SQL is authoritative after cutover migration/provisioning: reusable Polaris credentials are migrated from PocketBase and protected before persistence, while the new Postmark server token is supplied separately through the migration tool's secure target-provisioning input because no SMTP credential can be converted into a Postmark token.

External JSON is startup-only: `reloadOnChange` is not used. Changes require an application restart.

For local Visual Studio F5, checked-in `appsettings.Development.json` overrides only `Asap:ConfigFile` to use an ignored `Development.local.json` with the same general shape. Commit only a safe template/example.

## 13. Sensitive operational settings

Reusable Polaris/Postmark credentials remain SQL-managed application settings, but their secret values are protected before persistence. SQL stores only Data Protection ciphertext; this is a required baseline control, not deferred hardening.

Requirements:

- UI/API secrets are write-only.
- Blank input preserves the existing secret.
- **Clear** is an explicit, deliberate operation and is audited.
- Clearing a library override removes it and reverts to inheritance.
- Clearing a system secret requires confirmation that includes the count of active dependent libraries and leaves diagnostics/configuration visibly unconfigured.
- Never return a reusable secret from an API after save.
- Never log secrets.
- Administrative audit records only a masked representation when necessary.

Masking convention:

- length >= 16: first 4 + mask + last 4
- length 4-15: first 1 + mask + last 1
- length 1-3: fully masked

Reusable Polaris/Postmark credential values stored in SQL are encrypted at the application boundary with ASP.NET Core Data Protection using a dedicated purpose/version. Persist the key ring outside the application directory and protect persisted production/nonproduction keys with an **environment-specific X.509 key-encryption certificate** whose private-key ACL grants only the environment runtime identity and required deployment/migration administrators. Do not use machine-bound DPAPI for these durable rings because the ring must remain recoverable on a replacement IIS host. SQL stores ciphertext only; decrypt only immediately before the owning integration uses the value. Web/API responses remain write-only/masked as above. Production and nonproduction use separate key rings, separate key-encryption certificates, and separate runtime service accounts.

Because SQL secrets now depend on the Data Protection key ring, disaster recovery requires the matching key-ring backup **and recoverable certificate/private key** as well as the SQL backup. Back up that certificate/private key through the organization's protected certificate/credential recovery process, document import/ACL restoration on a replacement host, and never purge Data Protection keys or certificate material that may still protect persisted credential ciphertext.

## 14. Email/Postmark/outbox

Use the latest `Clc.Postmark.Api`. Postmark is the transport; templates remain application-owned.

Email configuration uses system defaults plus library overrides in `[asap].[EmailSettings]`, including credential inheritance semantics. Do not collapse Postmark configuration into a system-only model merely because Polaris is system-only.

All business email is durable, but notification availability never rolls back an otherwise valid ordinary business mutation:

1. A business action computes the notification intent and final immutable business-event content snapshot.
2. If patron email is involved, refresh current patron data first.
3. Resolve the effective recipient/sender/config needed to decide whether the intent is deliverable **at that business-action moment**.
4. Insert/update ASAP local state, event, and either a deliverable outbox row or a terminal suppression record in the same SQL transaction where they belong to the same action.
5. Commit the business transaction regardless of ordinary notification deliverability.
6. Enqueue deliverable rows through Hangfire.
7. A sweeper finds committed due outbox rows if the post-commit enqueue step is missed.

The outbox state model is exactly:

```text
pending
sending
sent
failed
suppressed
```

`pending` is automatically deliverable/retryable work; `sending` is an active leased attempt; `failed` is a durable nonterminal-for-operations state after bounded automatic attempts and remains eligible for explicit administrator **Retry**, which returns the same row to `pending`; `sent` and `suppressed` are terminal and are never retried. `suppressed` means the notification intent is deliberately not deliverable, for example because required sender/recipient configuration was absent when the business event committed or because an authorization-sensitive staff message lost authorization before delivery. Do not add an `abandoned` state unless implementation later proves a real operational need.

For optional staff-directed notifications where the current contract says a missing staff destination means **no notification is requested** (for example no `NotificationEmail`/weekly fallback), it is acceptable to create no outbox row. For a business action that did create a notification intent, missing required recipient/sender/transport configuration must never roll back the business mutation: persist a `suppressed` row with a safe reason and deterministic `BusinessKey` when the notification is otherwise idempotent. A notification suppressed because configuration was absent at commit is not automatically resurrected if configuration appears later. If a row was validly queued and the dynamic Postmark transport token/config later becomes unavailable at delivery time, mark/retain it as `failed` with a safe `mail_not_configured` classification and payload intact so an authorized manual retry can be performed after configuration is repaired.

Idempotent notification types must compute a deterministic, globally namespaced `BusinessKey` before inserting the outbox row. SQL enforces uniqueness for every non-null business key with a filtered unique index. A duplicate-key race is treated as a successful idempotent enqueue/suppression: load/return the existing outbox row rather than surfacing an application failure or inserting a second notification. Keys are defined by business event, not by Hangfire attempt. Ordinary weekly staff summaries are recipient-level idempotent for one reporting period, for example `weekly-summary:{StaffUserId}:{periodStart}:{periodEnd}`. An explicit **forced weekly-summary** run is a distinct manually initiated business event: generate one immutable `ManualRunId` when that forced invocation is accepted, persist it as the Hangfire job argument and in administrative/log context, and use keys such as `weekly-summary-force:{ManualRunId}:{StaffUserId}`. Retries of that same forced run reuse the same `ManualRunId` and remain idempotent; a later explicitly forced invocation gets a new run ID and may intentionally send another summary. Test/ad-hoc emails that are intentionally repeatable may use a null business key.

Distinguish immutable business-event messages from authorization-sensitive staff messages. Assignment notices, reminders, and weekly summaries are `staff_authorization_sensitive`. Persist recipient StaffUser ID, normalized authentication-email snapshot, authorization scope, and explicit `RecipientAddressKind`. Before every send/retry, require the snapshot to equal the current authentication email, revalidate active role/scope/participation, resolve the current destination for the stored kind, and compare it with `ToAddress`. Authentication-email or destination ownership changes suppress the stale row; OID metadata changes do not.

Delivery workers claim one `pending` row atomically by moving it to `sending`, setting `SendingStartedUtc` to the claim time, assigning a unique `LeaseId`, setting `LeaseExpiresUtc = SendingStartedUtc + 2 minutes`, and returning the post-claim rowversion. The worker commits before calling Postmark; no SQL transaction is held across the network call. The complete provider operation, including any transport retry performed by the selected client, has a hard 30-second timeout and must begin no later than 30 seconds after `SendingStartedUtc`. If the worker misses that start deadline, it must not call Postmark. Thus the latest legitimately in-flight provider operation ends no later than 60 seconds after claim, leaving at least a 60-second safety interval before lease expiry. These are fixed initial implementation constants, not new settings.

Every expired `sending` lease is potentially transport-ambiguous, including one whose worker may actually have crashed before the provider call; persisted state does not claim to distinguish those cases. Only after `LeaseExpiresUtc <= now` may the five-minute sweeper atomically reclaim the row to the bounded retry path, and a second provider call may begin only after a new worker has successfully claimed it with a new lease. A worker's final success/failure update must compare `Status = sending`, the expected `LeaseId`, and the post-claim rowversion/equivalent ownership state. If an old worker returns after reclaim or a newer attempt, its compare-and-set fails and it must not overwrite state or enqueue another retry. Database-enforced business/outbox idempotency prevents duplicate local messages, but a crash or timeout during/after Postmark acceptance may still cause duplicate delivery after the safety boundary; transport remains explicitly at-least-once and no exactly-once guarantee is made.

Outbox retries use the original recipient/sender/content snapshot but resolve the current effective transport credential/configuration for the owning organization at delivery/retry time. Use bounded transient retries, explicit failed status/details, stale-lease recovery, and manual retry. Record enough lease/attempt/provider metadata to diagnose ambiguous delivery without persisting the reusable Postmark token. Authorization-sensitive staff rows additionally perform the send-time authorization/address revalidation above.

Delivery metadata is retained indefinitely initially. Payload retention is state-aware: `pending`, `sending`, and `failed` rows retain recipient/sender/subject/body payload for as long as they can still be delivered or manually retried. Only terminal `sent` and `suppressed` rows are eligible for subject/body payload purge after 90 days; retain status/provider/attempt/suppression metadata. Provider webhooks are validated and idempotently recorded. Business trigger idempotency must prevent duplicate notification creation.

Historical PocketBase delivery audit is migrated, but the new outbox starts empty: migration never replays historical mail.

**Test email** uses the real durable outbox + Hangfire + Postmark route, not a synchronous shortcut. Library admins may send tests for their own library; super-admins may test any context.

The nonproduction recipient-domain safety contract uses the existing external configuration and the same outbox/delivery path:

- `Environment.IsNonProduction` is the application-level switch governing this rule. `ASPNETCORE_ENVIRONMENT=Testing` is independently authoritative only for enabling the testing authentication handler; it does not select this email policy, and `IsNonProduction` cannot enable testing authentication.
- When `IsNonProduction` is true, every outgoing Postmark recipient must pass the same exact-domain predicate before any provider call, including patron mail, ordinary and authorization-sensitive staff mail, ordinary/forced weekly summaries, and **Test email**. Check every recipient address (including To/Cc/Bcc when present) at intent creation and immediately before every send/retry using the currently loaded configuration. Existing staff authorization/address checks still apply independently.
- Compare the recipient's domain with `EmailSafety.AllowedRecipientDomains` case-insensitively. Match the entire domain, never a suffix or wildcard. A parent-domain entry does not allow subdomains; a subdomain is allowed only when explicitly listed.
- A missing or empty allowlist in nonproduction allows no recipient and suppresses all email delivery. Validate configured entries as domain names, not email addresses, URLs, wildcards, or suffix patterns. A malformed configured domain fails startup configuration validation; never interpret it as a broader match.
- An intent blocked by this rule becomes terminal `suppressed` with the safe machine-readable reason `recipient_domain_not_allowed`; no Postmark call occurs. The owning business mutation still commits normally. Preserve the normal deterministic `BusinessKey`, SQL uniqueness, and duplicate-race-as-success behavior for suppressed intents; intentionally repeatable test/ad-hoc messages retain their existing null-key allowance. Suppressed rows are never retried or resurrected after an allowlist change.
- When `IsNonProduction` is false (production), this nonproduction recipient-domain restriction does not apply. Production and nonproduction may initially share a Postmark server/token; the token/server does not select the safety policy.

Use one shared safety predicate in the existing mail path, with no additional environment-detection abstraction, pipeline, subsystem, or outbox state. Required deterministic coverage is in `06-TESTING-CI.md` section 4.1; release evidence is in `08-RELEASE-VALIDATION-NOTES.md` section 6.

Library admins may inspect and manually retry `failed` emails for their own library; super-admins have consortium-wide access; ordinary staff do not. `sent` and `suppressed` rows are terminal and do not expose Retry.

## 15. Email templates

Use relational `EmailTemplate` rows for built-in templates and rejection templates.

- Templates have stable keys.
- System templates are Organization `1`.
- Library templates may override/inherit system templates.
- Rejection templates may inherit, override, hide a system template, or add library-specific custom entries.
- `SourceTemplateId` self-FK associates an override with its source when useful.
- Blocking rules prevent deleting system templates that have dependents.
- C# may bootstrap missing built-ins but never overwrite configured content.

## 16. Branding

Use a dedicated `Branding` table keyed by organization rather than placing image bytes in `Settings`.

Store:

- original image bytes (`varbinary(max)`)
- content type
- original filename
- alternate text
- update metadata/rowversion

Image and alt-text inheritance are independent. Support PNG/JPEG/GIF, not SVG. Maximum file size 2 MB and maximum dimensions 4096x4096. Validate file signature, type, and image dimensions; store the accepted original unchanged. Branding changes are audited.

## 17. Material formats

Replace string-only format references with stable relational identities.

### 17.1 Core model

- `MaterialFormat`: stable ID, owner organization (system or library custom), immutable code, label, order, enabled/message behavior and strongly typed built-in title/author/identifier/publication field configuration.
- `MaterialFormatOverride`: library override for a system format, with nullable built-in behavior/field overrides.
- `MaterialFormatCustomFieldRule`: library-owned per-format rule for a `PatronCustomField`, preserving required/optional/hidden mode and optional label override.
- `TitleRequest.MaterialFormatId` and `FormatAutoClaimRule.MaterialFormatId` use FKs.

Rules:

- System formats are owned by Organization `1`.
- Libraries may create custom formats whose codes do not collide with system codes.
- A library uses an override, not a duplicate custom format, when customizing a system code.
- A future system format whose code collides with an existing library custom format is a blocking configuration/migration condition; do not silently merge.
- Format code is immutable; label is editable.
- System formats are disabled rather than hard-deleted.
- Library custom formats may be hard-deleted only when unreferenced.
- Resetting a library customization deletes the override and returns to system inheritance.
- A library may re-enable a system-disabled format through its override.
- Requests do not carry a permanent format-label snapshot; display resolves the current effective label.

Publication options use `PublicationOptionSet` + `PublicationOption`. Organization `1` owns the system list; absence of a library set means inherit it, while a meaningful library set completely replaces it. Clearing the library list means reset/inherit, matching current behavior. Disabled entries are preserved.

## 18. Auto-claim rules

Use versioned `FormatAutoClaimRule` records rather than mutating assignment history in place.

- Exactly one active rule per `(LibraryOrgId, MaterialFormatId)`.
- Rule points to `StaffUser` and has active/deactivated timestamps.
- Assignee change deactivates the old rule and inserts a new rule.
- Disabled formats make the rule dormant rather than deleting it; re-enable restores behavior.
- Staff deactivation deactivates rules; reactivation does not automatically reactivate old rules.
- No retroactive sweep when configuration/rules are saved.
- Evaluate on request submission and material-format change.
- Never overwrite a manual/legacy claim.
- Request stores exact rule ID, claim type, claimant FK, claimant display-name snapshot, and timestamp.
- Super-admin may be the target across libraries.

## 19. Request/workflow model

Keep explicit C# transition logic and current-state truth. Do not introduce a workflow engine or event sourcing.

`TitleRequest` is the primary patron suggestion. Preserve the established statuses as constrained codes:

- `suggestion`
- `outstanding_purchase`
- `pending_hold`
- `hold_placed`
- `closed`

Close reasons are constrained to the established business values and known aliases are normalized during migration. Unknown historical values are not silently invented.

Workflow tags are a relational, seeded taxonomy (`WorkflowTag` + join table). There is no general CRUD UI for the taxonomy. Existing app-defined tags are seeded by code/DACPAC data scripts as appropriate.

Patron submission snapshots remain historical and immutable: barcode, submitted email/name, patron code ID/description, patron organization, effective library/name, and staff-created-library snapshot where applicable. `PreferredPickupBranchId`/`PreferredPickupBranchName` are the one deliberate exception: they are the request's **current recorded pickup preference** and may change only through the dedicated validated pickup-preference workflow. On successful Polaris update, update those two request fields and append the normal note/event; on Polaris failure leave them unchanged. Normal generic request edits cannot change them.

For hold placement, those request pickup fields are temporary display/audit
state only. Immediately before a new Polaris hold create, refresh the live
patron and eligible pickup branches. Use the patron's registered Polaris
organization as `RequestingOrgID`; use the live default pickup organization
when it is eligible, otherwise use the registered organization only when it is
an eligible pickup location. A stale or ineligible live default is an
actionable validation failure and must not dispatch a hold. Once the create
operation is marked as dispatched, snapshot both resolved IDs and reuse them
for reply/recovery; never substitute the staff actor's organization or a
retired connection setting.

Keep `LegacyId` as a nullable historical/provenance field distinct from PocketBase migration mapping.

Remove broad `editedBy` state; use events/audit where provenance matters.

No automatic purge of closed requests. Admin/super-admin deletion is explicit and writes a reduced permanent deletion audit.

### 19.1 Events

`TitleRequestEvent` is append-only and includes stable relational/current fields plus a small flexible metadata JSON payload:

- request FK
- event type
- status/close reason where applicable
- actor type (`system`, `staff`, `patron`)
- nullable `StaffUserId`
- actor display-name snapshot
- message
- metadata
- UTC timestamp

New .NET staff events populate both `StaffUserId` and display-name snapshot. Migrated historical staff events do not infer a staff FK from an actor display name.

Preserve the existing behavior where some system-generated workflow notes are represented both in the editable notes field and as append-only events. Do not "clean up" this dual behavior during the port.

## 20. Additional-copy workflow

Keep `AdditionalCopyRequest` as a separate persisted workflow, not a special TitleRequest status.

- Separate open/closed lifecycle.
- Own notes and workflow fields.
- Nullable `SourceTitleRequestId`; deleting the source sets it null so the additional-copy record survives.
- On creation, copy source claim identity/display/timestamp once only after validating the retained claimant under section 7.6; thereafter the copy request is independent. Its open claim participates in the same StaffUser lifecycle serialization/cleanup contract as TitleRequest claims: a deactivation or scope contraction that makes the claimant ineligible clears the open AdditionalCopy claim, appends a concise system Notes entry, and records the cleanup count in `AdministrativeAudit`; closed claimant snapshots remain historical.
- Source request may already be missing; tolerate that where historical behavior requires.
- Deletion uses the same reduced permanent `DeletedRequestAudit` concept.

### 20.1 Reopening an AdditionalCopy task

Reopening is relationship activation, even when the retained claimant FK would be unchanged. Read the candidate claimant and expected task version, then in a short transaction lock Organization, the candidate StaffUser, and the task in the common order; re-read the task/version and claimant after locking. If either changed, restart the read/lock sequence or return the normal `409`, never acquire a newly discovered StaffUser lock after the task lock. Validate the actor's current permission and participation separately.

Retain the claimant/display/timestamp/type/rule only when the current claimant passes section 7.6 for this task's library with participation not required for the **stored relationship**. Otherwise clear all effective claim fields and append a concise timestamped system entry to the existing Notes containing the old claimant attribution/claim timestamp and safe clearing reason. Commit that note, claim result, reopen/closure-field updates, and new rowversion atomically. Do not assign the actor or another staff member automatically. Closed history remains untouched until this reopen; valid retained claims keep their original attribution. A lifecycle mutation racing reopen uses the same StaffUser serialization point, so either reopen retains a then-valid claim followed by normal open-claim cleanup, or reopen observes the changed eligibility and clears it itself.

## 21. Hard deletion/audit

`DeletedRequestAudit` is permanent and covers both request types. Keep only fields needed to prove what was deleted and from which library/workflow context. Do not retain unnecessary historical freeform notes or full PII merely because PocketBase did.

If a barcode is useful in deletion audit, store only the agreed masked representation. Existing PocketBase deletion-audit rows are transformed into this reduced schema; discarded name/email/full-barcode/note fields are intentionally not preserved.

`AdministrativeAudit` covers high-impact access/configuration changes, not every normal workflow mutation. Store actor, organization, target/action, timestamp, and small `Details` JSON. Never store raw secrets. Super-admins may view all audit history; library admins may view their own library's administrative audit; ordinary staff have no access.

## 22. Analytics

Analytics remains part of the first .NET cutover and remains available to all authenticated staff within their authorized library scope; super-admins may view all libraries or one library.

Move aggregation to SQL-side Dapper/ADO queries. Do not copy the PocketBase implementation that pages all rows and aggregates them in JavaScript. Preserve the existing metrics/semantics unless a concrete correctness improvement is documented.

## 23. Background processing / Hangfire

Use Hangfire SQL storage in the same SQL Server/database initially, with Hangfire's own schema and connection string.

Recurring jobs cover the current automation set plus target-only recovery/maintenance work. The following table is the authoritative default schedule contract; `examples/Config.example.json` documents the same keys and defaults as the operational template embedded in `scripts/deployment/Initialize-AsapTestHost.ps1`. Cron expressions are interpreted in the configured `Application.BusinessTimeZone` (`America/New_York` by default), not server-local time or UTC unless explicitly configured otherwise.

| Current PocketBase trigger | Current default | Target Hangfire recurring job | Config key | Target default | Behavior note |
|---|---:|---|---|---:|---|
| `asap-hold-check` | `0 * * * *` | `asap-workflow-processing` | `WorkflowProcessing` | `0 * * * *` | Preserve one ordered hourly orchestrator rather than several same-cron jobs. |
| `asap-isbn-check` | `*/5 * * * *` | `asap-identifier-processing` | `IdentifierProcessing` | `*/5 * * * *` | Canonical five-minute identifier processor; see normalization contract below. |
| `asap-organization-sync` | `0 2 * * *` | `asap-organization-refresh` | `OrganizationRefresh` | `0 2 * * *` | Preserve current organization + patron-code refresh behavior. |
| `asap-weekly-staff-action-summary` | `0 20 * * 0` | `asap-weekly-staff-summary` | `WeeklyStaffSummary` | `0 20 * * 0` | Preserve Sunday 20:00 behavior. |
| none - new durable outbox recovery | n/a | `asap-email-outbox-sweep` | `EmailOutboxSweep` | `*/5 * * * *` | Dispatch committed due rows missed by immediate enqueue and reclaim expired sending leases; individual delivery jobs are enqueued immediately and are not recurring. |
| none - PocketBase session model differs | n/a | `asap-patron-session-cleanup` | `PatronSessionCleanup` | `0 3 * * *` | Remove expired SQL patron sessions daily. |
| none - new SQL outbox retention | n/a | `asap-email-payload-cleanup` | `EmailPayloadCleanup` | `30 3 * * *` | Purge eligible outbox subject/body payloads after the documented retention period. |

The hourly `asap-workflow-processing` job is a **single ordered orchestrator**: (0) recovery/completion of already-acquired holds, (1) unreviewed-suggestion (`OutstandingTimeout`) -> pending-hold -> hold-pickup -> additional-copy timeouts, (2) purchase promotion, (3) new hold placement, (4) fulfillment tracking. Preserve the relative order of all existing business phases; do not register them as independent same-cron jobs. Recovery ignores library activity only for already-acquired operations. Ordinary phases retain participation gating, per-item transactions, and failure isolation. The whole orchestrator cannot overlap a scheduled/manual invocation. Because timeout queues are bounded, every later phase also rechecks only the applicable status-specific timeout predicate in section 23.2 before acting (there is no OutstandingTimeout predicate for `outstanding_purchase`): an expired-but-not-yet-scanned row is durably skipped by that later phase for eventual timeout handling, not promoted/placed/fulfilled ahead of its timeout. Incomplete hold operations remain barriers and are settled through recovery, not cancelled by timeout.

Identifier processing is intentionally no longer part of the hourly orchestrator. The pinned PocketBase source has **two different** ISBN implementations, not duplicate wrappers: the older hourly `processPendingIsbnChecks()` supplies bounded retry state/`error_max_retries`, while the dedicated five-minute `processPendingSuggestionIsbnChecks()` supplies the newer BIB persistence/reconciliation, found/not-found workflow tags, `lastChecked`, and `skipped_no_isbn` behavior. The .NET target consolidates them into one canonical five-minute processor using the dedicated path as the behavioral base **plus** the useful bounded retry contract from the older path.

The Polaris identifier-search boundary must return one of four semantic outcomes: `Found`, `DefinitiveNotFound`, `TransientFailure`, or `OperationalFailure`. Only a successfully completed search that produces no eligible match may become `DefinitiveNotFound`; a timeout, connection failure, throttling response, transient upstream/server failure, or other explicitly classified retryable provider failure must never become `not_found`. Missing/invalid Polaris configuration, authentication/authorization failure, malformed request, protocol/contract failure, or another non-transient setup/programming failure is `OperationalFailure`. Unknown/ambiguous failures default to failure, never to `DefinitiveNotFound`. If identifier lookup fans out across ISBN/UPC/LCCN requests, `DefinitiveNotFound` is allowed only when the required searches completed successfully (including API-defined no-record responses); if a required search fails, the aggregate result is a failure rather than a false catalog conclusion.

Canonical state rules:

- missing identifier -> `skipped_no_isbn`, retry count `0`, clear any prior identifier error code/result/current-check timestamp and any identifier-derived catalog-match state;
- found -> persist the BIB ID, reconcile, apply the `Identifier found` tag (and `Multiple Polaris matches` when applicable), status `found`, retry count `0`, clear prior identifier error code; a target `found` row must never exist without a nonblank `BibId`;
- definitive not found -> clear any identifier-derived BIB match, apply `Identifier number not found in system`, remove contradictory identifier-found/multiple-match tags, status `not_found`, retry count `0`, clear prior identifier error code;
- transient failure -> record a safe `IsbnCheckLastErrorCode`, increment `IsbnCheckRetryCount`, keep status `pending` while attempts 1-4 remain, and revisit through the fair queue on the normal five-minute schedule with **no additional exponential backoff**;
- fifth consecutive transient failure -> status `error_max_retries`; this five-evaluation policy is an intentional target choice; evaluations use the normal five-minute job cadence subject to fair queue capacity, so backlog/restarts can extend elapsed exhaustion time;
- operational failure -> record a safe error code, do **not** consume/reset the per-request transient retry budget, keep the request `pending`, fail/surface the job operationally, and stop that run from interpreting further requests against known-bad integration/configuration state;
- `LastCheckedUtc` records each processing evaluation/Polaris attempt as appropriate; `IsbnCheckResult` remains the user/workflow result rather than a raw provider-error dump.

An **identifier change is an atomic invalidation boundary only in an eligible pre-placement state** (`suggestion`, `outstanding_purchase`, or `pending_hold`), with no incomplete operation and no previous successful placed-hold BIB protection. Compare normalized values before mutating anything. `hold_placed` and `closed` reject changed or cleared identifiers with `409 identifier_locked_by_stage`, even when the placement operation is already complete; an unchanged normalized identifier is a no-op and does not reset catalog state. Reopening cannot be used to change the BIB of a real placed hold: successful operation snapshots and preserved placed-hold lifecycle evidence remain authoritative. No generic identifier or explicit-BIB edit may erase or replace that BIB.

For a permitted pre-placement change, atomically clear `IsbnCheckResult`, `IsbnCheckRetryCount`, `IsbnCheckLastErrorCode`, and `LastCheckedUtc`; remove `Identifier found`, `Identifier number not found in system`, and `Multiple Polaris matches`; and clear prior `BibId`. A nonblank new identifier becomes `pending`; a cleared identifier becomes `skipped_no_isbn`. Descriptive title/author/publication/format values remain editable data, not proof of a match. Validate both the persisted starting state and proposed ending state: a combined edit/transition must not bypass this stage rule. A rejected edit commits no field/tag/event/audit/outbox change and performs no Polaris mutation. Use the same backend predicate for DTO capabilities/UI controls, but never rely on the client to enforce it.

Do not add a `BibIdSource` column solely for this. Only in a permitted pre-placement edit, if the same staff workflow deliberately supplies/selects a BIB ID while changing the identifier, perform invalidation first and then run the normal explicit BIB-assignment/Polaris reconciliation path. Only that newly validated manual BIB may be stored; the old BIB is never carried forward implicitly, and a manual BIB assignment does not set `IsbnCheckStatus = found` unless the identifier lookup itself establishes the match. Downstream automatic promotion/hold logic must never act on the pre-edit BIB or old identifier tags while the new identifier is pending/failed.

A staff user with normal authorization to act on the request can invoke **Retry identifier check** for `error_max_retries` only while the request is in the canonical suggestion identifier queue and has no placed-hold protection/incomplete operation; that action sets status `pending`, retry count `0`, clears the last safe error code/result, and enqueues the same scoped canonical identifier-processing path immediately (while the recurring five-minute processor remains the normal safety net). No second retry implementation is created. The old hourly ISBN path is therefore an intentionally retired implementation whose useful semantics have been absorbed, not a merely redundant invocation.

**Legacy placed-BIB protection.**

For imported rows with placement-stage evidence, `04-MIGRATION-CUTOVER.md` section 6.10 defines the deterministic legacy-event metadata guard carrying the recorded BIB and source provenance. It survives reopening without asserting newly verified provider success or creating a synthetic HoldPlacementOperation. New .NET placements use the successful operation's immutable BIB snapshot. This guard permanently limits edits, including a separately committed reopen followed by a later edit; current request state remains the workflow authority. Its placement evidence is exhaustive under migration section 6.10, including all five hold-terminal reasons and normalized status transitions into `hold_placed`. Protected history with an unknown BIB is distinct from no placement evidence. Neither kind of historical BIB evidence supplies runtime terminal-hold identity (section 9.2).

`Organization.IsActive` is also the background-processing participation gate. Except for section 9.1 recovery/completion of previously acquired operations, participation-dependent business automation must skip work owned by inactive libraries: identifier processing; every hourly workflow phase including timeouts, purchase promotion, hold placement, and fulfillment advancement; ordinary library weekly-summary generation; and equivalent library-scoped manual **Run Now** actions. Prefer filtering inactive organizations out of candidate queries, but filtering alone is not the concurrency boundary. Whenever a job is ready to apply a participation-dependent local result or authorize a later external mutation, its short SQL transaction must follow the common lock order: lock/re-read the owning Organization (`UPDLOCK,HOLDLOCK` or equivalent) and require `IsActive = true`, then lock/revalidate the request/current rowversion before committing the local state or operation acquisition. If deactivation commits first, the job cannot commit new work; if the job's local commit/acquisition wins first, deactivation may then proceed and any already-started external call may finish/reconcile safely. No SQL transaction is held across Polaris. Organization refresh, patron-session cleanup, email-outbox recovery/delivery, and payload cleanup are infrastructure/system operations and continue while libraries are inactive.

Library deactivation does **not** blanket-cancel already-committed `EmailOutbox` rows. Immutable patron/business-event rows may drain under the normal at-least-once delivery contract. Authorization-sensitive staff rows are different: immediately before delivery/retry they revalidate current StaffUser/library authorization and are terminally `suppressed` rather than sent when that authorization has disappeared. Deactivation prevents new participation-dependent actions from creating additional business messages; it does not rewrite immutable business-event outbox history.

Weekly summary contents follow the same authorization scope as interactive staff access; do not build one consortium-wide payload and send it to every recipient. For `staff`/`admin`, generate counts, samples, and links from that recipient's own **active** library only, and do not enqueue a summary while the library is inactive. For `super_admin`, generate a consortium-wide summary over active participating libraries. Links must open through normal server authorization and cannot grant broader scope than the recipient already has. Recipient+reporting-period outbox idempotency remains unchanged. This is an intentional privacy/authorization correction from the pinned PocketBase implementation, which builds one unscoped consortium summary for all opted-in staff.

Preserve the pinned manual weekly-summary `force` behavior explicitly. A normal scheduled or non-forced manual execution uses recipient+reporting-period idempotency and does not resend a completed period. A forced execution creates one new `ManualRunId` and enqueues a distinct set of recipient messages for that run; Hangfire retries of that run reuse the same ID and keys, while a later explicitly forced invocation gets a new ID and is intentionally allowed to send again. Forced runs use the same current recipient authorization scope and send-time authorization-sensitive outbox checks as ordinary summaries.

External job throughput is also part of parity. `Hangfire:ProcessingLimits` uses nullable override values with this precedence: queue-specific -> timeout-family (for timeout queues only) -> global default. Default bounds/values preserve the pinned resolver: PageSize `50` (allowed `1..500`) and MaxPerRun `500` (allowed `1..5000`). Target logical queues are `IdentifierProcessing`, `PurchasePromotion`, `HoldPlacement`, `FulfillmentTracking`, `OutstandingTimeout`, `PendingHoldTimeout`, `HoldPickupTimeout`, and `AdditionalCopyTimeout`. `examples/Config.example.json` shows the complete documented shape and is tested against the embedded operational template. Migration/cutover captures the effective old cron and processing-limit values in `effective-legacy-operational-config.json` and reconciles them against target external JSON before jobs are enabled. The canonical identifier queue maps from the dedicated `pending_suggestion_isbn_checks` path; any explicit legacy `pending_isbn_checks` queue override is captured and reported as an obsolete-path value requiring an explicit documented resolution rather than silent loss.

If implementation discovers another registered recurring PocketBase trigger or queue-specific operational override, it must be added to the parity contract before the port can claim schedule/throughput parity. Cadence or capacity changes are not incidental implementation choices: any deliberate difference must be recorded here and in release-validation notes.

Principles:

- One recurring execution at a time per logical job.
- Jobs are idempotent/resumable at item level.
- Process sequentially in `(CreatedUtc, Id)` order within durable finite scan cycles (section 23.1); retain configured limits and do not restart at the oldest unresolved row each invocation. No parallel processing is introduced.
- Use short local per-item/phase transactions; a hold lifecycle has separate acquisition, dispatch-marker, evidence and completion transactions, never one transaction across Polaris.
- Revalidate rowversion/current state before applying results; stale work is skipped rather than overwriting newer user actions.
- Bounded Hangfire-level retry for transient job failures; item processing itself must isolate failures so one bad request does not abort the entire batch.
- Hangfire history is sufficient; do not add a general `JobRun` table.
- Recurring schedules live in external environment JSON and take effect after restart.
- Permanent nonproduction runs the recurring schedules normally.

Manual **Run Now** for the complete background workflow enqueues the same `asap-workflow-processing` orchestrator and uses the same single-run guard. Deliberately scoped actions that already exist as individual business operations (for example a specific request promotion/check path) may enqueue/invoke that scoped operation, but they must not recreate the recurring phases as independent competing jobs. Reject duplicate concurrent runs. Library admins may trigger applicable scoped work for their own library; super-admins may run a selected library or all libraries.

Hangfire dashboard in production requires OIDC and super-admin authorization.

### 23.1 Bounded cross-run queue progress

Persist one small `[asap].[QueueProgress]` row per logical queue and execution scope, not a JobRun framework. Scope is Organization `1` for system/all-libraries or the exact non-system library for a scoped run. The eight configured queues keep their names/limits. `HoldRecovery` is a ninth **logical scan**, using the existing HoldPlacement PageSize/MaxPerRun values for its own recovery-phase budget; it adds no configuration key or recurring job and does not consume/starve the ordinary new-placement budget. Its rows are incomplete operations other than `operator_required`; their immutable acquisition time is the created-time sort key. No loop may exceed its resolved budget by re-enqueuing itself in the same invocation.

At the start of a new cycle, persist `CycleMaxId`, the greatest currently visible committed ID in that queue's source table (zero for an empty table), and an empty last key. Query current candidates in scope with `Id <= CycleMaxId`, ordered by immutable `(CreatedUtc, Id)`, strictly after the persisted last key. Fetch at most `min(PageSize, remaining MaxPerRun)` at a time. The high-water ID makes the cycle finite even when new records have older creation timestamps. No FK points from the scalar cursor/watermark to a source row.

For each examined candidate, perform its existing processing and atomically persist the handled outcome plus the new last key **after** the relevant durable domain result, operation state, or safe skipped/failure outcome has been recorded. Include the progress update in the same local transaction as the result when possible; after an external boundary, first persist the journal/result needed for safe replay and only then checkpoint. Acquire any Organization/StaffUser/request/operation locks in the existing order and update the progress row last, with expected cursor rowversion. Never hold a progress or domain transaction over a provider call. A crash before checkpoint may re-examine the row; normal rowversion/journal/idempotency rules make that safe. A crash after checkpoint resumes strictly later. Never checkpoint a whole fetched page in advance.

Unresolved/no-match, lease-owned, stale, deleted, newly ineligible, timeout-due, and handled per-item failure outcomes all count against MaxPerRun and advance the key after durable handling. A caught item failure is recorded using existing safe request/event/note/journal diagnostics as applicable plus the progress outcome, then the next item is considered; do not spend the whole run repeatedly retrying the same row. An operational integration failure that must stop the run records that outcome and advances the current item before stopping; a later invocation can reach later rows once the dependency is repaired. If SQL cannot durably record the outcome/checkpoint, stop without advancing. Liveness cannot be promised while the database or all workers remain unavailable.

When a query finds no candidate beyond the key within `CycleMaxId`, mark that cycle complete by clearing its watermark/key and stop that queue for this invocation, even if budget remains. Start a fresh cycle on its next invocation. Reaching MaxPerRun alone **does not** clear progress or wrap. If the cursor row is deleted, scalar key comparison still works. New inserts above the captured ID watermark (including backdated inserts) wait for the next cycle; rows with older IDs that commit late or become eligible behind the cursor also wait for the next cycle. An existing row that becomes eligible ahead of the cursor can join the current cycle. No creation time is rewritten to manipulate ordering.

Scheduled all-library execution and scoped manual execution have independent persisted scope rows, but share the logical job's single-run guard across scopes, so they cannot overlap. A scoped run neither scans another library nor changes the global cursor; current authorization is checked at execution, not inferred from a supplied scope ID. Repeated scoped runs cannot reset global progress. A super-admin may invoke scoped recovery for an inactive library, but ordinary scoped business phases still skip it. Request-specific actions do not reset/advance unrelated queue cursors.

**Bound:** for a continuously eligible row inside a finite cycle of at most N remaining candidates and positive cap C, at most `ceil(N/C) + 1` successful queue invocations reach it or finish that cycle (the extra invocation allows an end-of-cycle probe). A row deferred behind the key/above the watermark is reached within the rest of this cycle plus the next finite cycle; ongoing later inserts cannot extend the captured cycle. This assumes workers run and outcomes can be durably recorded. Errors/unresolved older rows cannot permanently consume the first C slots on every run. Persisted progress survives restart and configured limit changes without resetting the cycle. Show scope, last key/outcome, watermark, and completed-cycle state in existing operations diagnostics; Hangfire history remains the job-run history.

### 23.2 Timeout predicates (scheduled and manual)

The legacy `OutstandingTimeout` name means **unreviewed-suggestion auto-rejection**, not approved-purchase expiration. Keep the existing settings/queue keys. This preserves executable `lib/jobs/timeouts.js` at the pinned SHA; it does not introduce a new expiration policy for `outstanding_purchase` awaiting a BIB.

All four families require the effective family `Enabled` value, a valid configured `Days` threshold, the listed current status, and an Organization eligible for ordinary participation-dependent processing. Evaluate a strict `ageTimestamp < cutoff` comparison: equality is **not** expired. Take `now` from the injected clock. Preserve the source's calendar-day subtraction (`Date.setDate`) using the target's configured `Application.BusinessTimeZone`, then compare instants in UTC; do not substitute elapsed whole-day rounding. A cutoff in a daylight-saving gap moves forward by that gap; an overlapping local cutoff uses the earlier occurrence, matching the source date arithmetic. The same helper and clock semantics govern scheduled/manual execution and later-phase guards.

| Queue/settings family | Eligible row/status | Governing age timestamp | Terminal transition and record | Notification |
|---|---|---|---|---|
| `OutstandingTimeout` | TitleRequest `suggestion` only | `TitleRequest.CreatedUtc` (source `created`), not review/edit time | `closed`, `CloseReason = rejected`; ordinary `timeout_closed` event/system note | Effective `OutstandingTimeoutSendEmail` and `OutstandingTimeoutRejectionTemplateId` select the existing auto-rejection notification/template. Never substitute purchase approval or a generic rejection template. |
| `PendingHoldTimeout` | TitleRequest `pending_hold` only | `TitleRequest.UpdatedUtc` (source `updated`), not creation/placement time | `closed`, `CloseReason = rejected`; ordinary `timeout_closed` event/system note | No timeout-triggered patron email; do not apply OutstandingTimeout mail settings. |
| `HoldPickupTimeout` | TitleRequest `hold_placed` only | `TitleRequest.UpdatedUtc` (source `updated`), not a guessed pickup/hold-create timestamp | `closed`, `CloseReason = hold_not_picked_up`; ordinary `timeout_closed` event/system note | No timeout-triggered patron email. This independent local timeout needs no Polaris HoldRequestID. |
| `AdditionalCopyTimeout` | AdditionalCopyRequest `open` only | `AdditionalCopyRequest.UpdatedUtc`; source `updated` falls back to `created` only when absent. Preserve that fallback when normalizing such an import. | Close the independent task, set normal closure attribution/time, append the timeout note; no TitleRequest close reason and no source-request transition | No timeout-triggered email. |

The three TitleRequest timestamps do not gain a fallback to another field: missing/invalid required source timestamps follow the existing migration validation/blocker rules. No state-entry timestamp is introduced. Immutable `(CreatedUtc, Id)` ordering and QueueProgress checkpoints determine **which row is visited**, not its timeout age. Scans, safe skip diagnostics, and cursor maintenance must not rewrite domain age timestamps merely to record a visit or manipulate ordering.

Under the existing Organization -> request/task serialization, re-read participation, effective timeout settings, current status, governing timestamp, expected request version, and the incomplete-hold-operation barrier before closure. A stale row or changed status/settings/participation cannot be closed on the earlier candidate snapshot. Capture the final evaluation clock value and apply the same strict predicate. Later phases defer only rows that actually satisfy their current status's timeout; an old `outstanding_purchase` remains eligible for its ordinary promotion work.

Business closure, event/note, and any configured durable notification intent use the existing local transaction/idempotency contract. One committed timeout closure yields at most one intent for that business event, including on concurrent or retried execution. With sending disabled, do not enqueue a deliverable message; with required mail/template configuration missing, follow the existing suppressed-intent/diagnostic contract. Neither missing/disabled mail nor a later delivery failure reverses the business closure. No timeout here cancels a Polaris hold externally. See `06-TESTING-CI.md` section 10.2 F1 for the exact behavioral oracles.

## 24. Health, diagnostics, logging, monitoring

Endpoints:

- `/health/live`: anonymous, deliberately minimal process-liveness response.
- `/health/ready`: anonymous, deliberately minimal ready/not-ready response.
- detailed diagnostics: authenticated super-admin surface/API.

Readiness covers critical local dependencies and initialization, including ASAP SQL connectivity, exact SchemaVersion, compatible pre-provisioned Hangfire storage, and the current-policy usable-super-admin gate. Do not fail readiness simply because Polaris or Postmark is temporarily unavailable. Detailed diagnostics may actively test SQL, Hangfire/config state, Polaris, and Postmark.

Logging:

- NLog structured logs.
- Local rolling files are canonical production application logs.
- 30-day local retention, with size-based rollover inside a day when needed.
- Console logging retained for development/diagnostics.
- Correlation ID on requests and useful cross-boundary operations.
- Never log secrets, full barcode, names, email, phone, or raw Polaris payloads containing patron PII.

Include a small repository PowerShell PRTG sensor script that calls `/health/ready` and emits correct PRTG XML. Keep the ASP.NET application itself monitoring-system agnostic.

## 25. Frontend/browser policy

The frontend remains vanilla HTML/CSS/ES modules and the existing tabbed staff application. Do not introduce React, Razor Pages, Blazor, client-side routing, or a frontend bundler as part of the port.

- Browser support: current evergreen Edge, Chrome, Firefox, Safari.
- No legacy IE/webview compatibility unless a concrete integration later proves it necessary.
- Preserve Grid.js for the initial port.
- Vendor exact current third-party browser-library versions locally instead of using CDNs. Do not upgrade Bootstrap/Font Awesome/Grid.js as part of the port.
- `Frontend/` is source-only in Git; generated/copied `wwwroot` output is ignored.
- An incremental MSBuild target copies frontend assets during build/publish/F5 without invoking Node/npm.
- Node/npm remains only for development/CI frontend tests.

Staff queue loading preserves the current full scoped-list/client-side filtering model initially. Do not introduce server-side paging/search unless measured production volume demonstrates the need.

## 26. Patron embedding / CSP

`/patron` remains a special HTML endpoint capable of applying a request-specific CSP `frame-ancestors` value. Static assets remain ordinary static files.

The allowed patron-embed origins are a system-level setting managed only by super-admins. Library admins may see whether embedding is enabled but may not alter the shared CSP trust boundary. Preserve the current behavior of removing conflicting `X-Frame-Options` where required for approved embedding.

## 27. API contract policy

`/api/asap/...` is an internal contract between the bundled frontend and backend, not a supported public integration API.

During the initial port:

- preserve existing route structure and DTO shapes wherever they still make sense;
- change contracts only when there is a concrete benefit (Entra/session model, rowversion, corrected status codes, removal of PocketBase concepts, etc.);
- document every intentional incompatibility;
- do not add API versioning solely for this rewrite.

Future frontend/backend changes may evolve together without a public compatibility guarantee.

## 28. Explicit non-goals for the first port

Do not add the following unless a blocker emerges:

- distributed cache or multi-instance coordination
- general event sourcing/workflow framework
- general migration framework in addition to DACPAC
- external secret-vault product beyond the required Data Protection encryption for SQL-stored reusable integration credentials
- automatic CI/CD to permanent nonproduction/production
- per-staff Polaris credentials/identity integration
- Microsoft Graph validation
- frontend framework rewrite
- third-party frontend dependency upgrades
- server-side queue paging/search
- production data copy into nonproduction
- automatic retention/purge of deployment backups or old app directories
