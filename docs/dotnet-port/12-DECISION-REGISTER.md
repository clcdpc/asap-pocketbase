# Binding Decision Register

**Purpose:** compact authoritative register for the PocketBase -> .NET port. Detailed rationale and implementation guidance live in the other pack documents.

## 1. Interpretation rules

- These decisions are binding for the initial .NET port unless an implementation discovery proves one impossible or unsafe.
- Preserve current behavior by default when a detail is not explicitly changed here.
- Prefer the simpler implementation when two approaches satisfy the same requirement.
- Do not reopen settled decisions merely because another implementation pattern is common.
- Material product/security/migration deviations require explicit escalation; ordinary implementation detail does not.
- Superseded decisions at the end of this document must **not** be revived.

## 2. Repository and port workflow

- Keep the port in `clcdpc/asap-pocketbase` with one long-lived integration branch (`codex/csharp-port`) and one final draft PR into `main` (#264). Beginning with Slice 6, temporary slice/work-package branches and PRs are permitted solely to bound implementation/review. Package PRs target the slice branch; slice PRs target `codex/csharp-port`; neither targets `main`. They are not independent releases and do not bypass full slice validation, holistic review, acceptance or exact-milestone CI.
- Deliver complete vertical slices through the staged mechanics in document 10; package completion is not slice acceptance. Preserve one accepted milestone per completed slice on `codex/csharp-port`.
- Keep PocketBase source in place as a behavioral reference until final cleanup, except that the known unrelated `clc-carousel-manual-import-example/` subtree at the pinned baseline is explicitly not ASAP behavior and should be removed/excluded rather than ported.
- Freeze ordinary PocketBase feature development during the port; urgent production fixes must be deliberately brought into the port branch.
- Track the exact commit deployed to PocketBase production throughout the port. After successful .NET production cutover, create/verify the permanent final-PocketBase tag at the exact PocketBase commit frozen for that cutover, including any emergency fix after the .NET merge. Do not maintain a permanent PocketBase branch.
- Rename the repository to `clcdpc/asap` only after the first .NET production deployment is validated.
- Use canonical names: `Asap.sln`, `Asap.Web`, `Asap.Database`, `Asap.Migration`, and `Asap.Tests`.
- Organize `Asap.Web` primarily by feature, not global technical layers.
- Commit coherent package/slice work under document 10. Review candidates remain unaccepted; package/slice-branch CI cannot replace complete slice validation or remote CI for the resulting integration-branch milestone SHA. No later slice begins before acceptance.
- Every slice must leave the .NET branch buildable, runnable, and green; unimplemented features may be absent/disabled rather than broken.
- Beginning with Slice 6, detailed Terra package reviews where applicable are followed by fresh holistic review of the complete slice delta, then short Astra acceptance and integration. Bounded fixes receive focused re-review; material broadening requires another full holistic review under document 10. No pass cap permits a known substantive blocker. PR #264 stays draft until the final whole-app review and existing completion gates.
- Final whole-app review stops after three consecutive passes with no new substantive findings, maximum six passes; blocking issues still must be fixed.

## 3. Thin autonomous supervision and bounded workers

Staged PRs began with Slice 6. Beginning with Slice 7, GPT-6 Astra High is the preferred/default thin autonomous slice supervisor unless the user explicitly requests manual/direct execution. Existing completed bootstrap remains valid. Slices 0-6 retain their actual historical execution/review evidence and acceptance. [Document 10](10-CODEX-MULTI-MODEL-TASK.md) is the authoritative execution/model/review policy; [PORT-STATUS.md](../implementation/PORT-STATUS.md) records current state.

- Luna Max owns implementation, tests, ordinary debugging, complete required pre-review validation and affected documentation in fresh/bounded child tasks for small slices, packages, integration/glue and confirmed fixes. Successful tasks return compact receipts. Luna High is optional, never a required first tier.
- Terra High remains an independent bounded reviewer and never fixes code. Detailed package review asks whether the bounded behavior is correct. Fresh holistic slice review assesses the complete slice delta from the technical review base recorded at bootstrap to final slice head. Distinguish historical acceptance, authorized reviewed corrections and later docs-policy bytes; do not mechanically re-review independently reviewed corrections. Confirmed findings go to bounded Luna fixes, then focused Terra re-review; retaining the same Terra is not an acceptance invariant.
- Astra High owns phase/state progression, dispatch, branch/PR state, compact receipt verification, fix routing, short acceptance, integration, status/milestone records, the GitHub projection/event journal, CI polling and restart/recovery. Persistent technical-parent behavior is prohibited: implementation/debugging/detailed review remain with bounded Luna Max and Terra High workers. Retain state/SHAs/PRs/receipts/findings/reviews/CI, not transcripts/logs/patches/source dumps.
- GPT-6 Astra Max is a bounded escalation tier for concrete material supervisor/contract ambiguities, irreconcilable worker conclusions, difficult cross-package interactions or difficult reconciliation/acceptance questions. Return a concise ruling/recommendation, authoritative basis and required next action; after consultation control returns to Astra High. Max is neither a persistent replacement supervisor nor a routine acceptance pass. Routine branch operations, failures/debugging, findings, polling, docs and coordination do not justify escalation.
- Beginning with Slice 7, the slice integration PR is the durable orchestration surface: one mutable supervisor-state comment projects current state; compact append-only supervisor-event comments record material transitions; Terra uses GitHub review threads/findings where practical. Actual Git/PR/review/Actions state outranks comments under document 10's authority order. Append the event before projecting the established transition. Recover from GitHub instead of retaining worker transcripts. Package events share the slice journal. Never put sensitive migration/business data in comments. Automatic Codex GitHub review is not a normal temporary PR gate; bounded Terra remains the independent reviewer.
- Luna replies to substantive Terra threads with fix SHA, resolution and validation; only independent Terra verifies and resolves findings. Native thread resolution is preferred. If authenticated tooling cannot toggle it, document 10 requires Terra's durable same-thread reply with stable ID, exact verified fix/reviewed SHA, explicit `RESOLVED` disposition and verification result, plus a record of the unavailable capability. Summary IDs also require explicit Terra resolution. `unresolved_findings` counts IDs lacking independent resolved disposition, not raw UI bits. Acceptance and recovery use actual Terra evidence, never Luna replies or supervisor decrements; no technical finding may remain open. Short Astra High acceptance still requires exact reviewed bytes and review/journal agreement. Unexpected automatic Codex review must finish and its substantive findings be resolved before merge, without triggering a duplicate manual review; record one compact event.
- Normal phase transitions need no new user prompt. Manual/direct mode remains fallback for explicit phase control, unavailable dispatch, orchestration diagnosis or unusual resumption, with identical independence and validation/review/acceptance/exact-SHA gates.
- After initial full slice review, stop unresolved after at most three fix/re-review cycles; broad re-review counts. No cap waives a finding. Stop for unresolved material contract decisions, unexpected state or substantive exact-milestone CI failure. Retry obviously transient infrastructure once when safe. Stop after acceptance unless cross-slice continuation is explicitly authorized.
- Sol High/XHigh remains exceptional focused specialist consultation after a concrete unresolved issue and Astra assessment. Size, SQL, migration, concurrency or security sensitivity alone is insufficient; Sol does not own a slice.
- Optional packages use coherent independently testable/reviewable boundaries, normally 2-4 for a large slice, not a quota. Document 10's Slice 6-11 package guidance is nonbinding and refreshed at slice start.
- Workers use branch/PR diffs and relevant instructions/packets/contracts. The supervisor consumes compact receipts and inspects raw evidence only for failed/incomplete/conflicting gates or a specific material invariant. Never reread successful logs merely for confidence. Preserve required local evidence and cycle counts across context rotation.
- Package validation is proportionate; integrated slice validation runs the complete existing matrix. Review fixes rerun affected gates and broaden when prior evidence is invalidated. Astra verifies receipts without repeating successful work. After integration, record acceptance/status documentation and commit if required; keep the reviewed implementation SHA separate. Successful CI on that exact final candidate milestone, including any status commit, completes acceptance.
- Existing architecture and all objective acceptance/release criteria are unchanged. Luna implements the settled contract without speculative abstractions. Package/slice reviews supplement the unchanged final whole-application adversarial review.

## 4. Target runtime and project structure

- .NET 10 / ASP.NET Core 10.
- Framework-dependent IIS deployment; matching .NET 10 ASP.NET Core Hosting Bundle is a server prerequisite.
- SQL Server 2022, database compatibility level 160.
- Single `Asap.Web` application hosts HTTP API, static frontend, OIDC/cookies, patron auth API, and Hangfire workers/dashboard.
- One `AsapDbContext` for ordinary application persistence.
- EF Core for normal CRUD/entity persistence; Dapper/ADO for analytics and genuinely heavy SQL.
- Direct feature services over `DbContext`; no repository/UoW layer.
- No MediatR/CQRS framework or broad application-layer abstraction.
- Handwritten entities/fluent mappings and explicit DTOs; no mapper library/scaffolding dependency.
- Complex parameterized SQL stays near the owning C# feature by default; stored procedures/views only for concrete reuse/operational benefit.
- `[asap]` application tables are internal implementation, not a supported reporting/integration contract; add explicit views/contracts if durable external reporting is later required.

## 5. Frontend

- Preserve the current vanilla HTML/CSS/ES-module frontend and existing UX as the baseline.
- Do not introduce React, Blazor, Razor Pages as a UI framework, or a client-side router as part of this port.
- Retain Grid.js and current grid-module design for the initial port.
- Vendor the exact currently used Bootstrap, Font Awesome, Grid.js, and other browser assets locally; no production CDN dependency.
- Upgrade frontend libraries only after the port unless a required security/correctness issue forces a change.
- Keep frontend source under `src/Asap.Web/Frontend/`; generated `wwwroot` output is ignored.
- MSBuild incrementally copies frontend source/assets for build/publish/F5.
- No Node/npm dependency in normal app build, F5, publish, production, or deployment.
- Node/npm remain development/CI-only for JavaScript tests and Playwright.
- Support modern evergreen Edge/Chrome/Firefox/Safari only; no legacy IE/webview compatibility absent a concrete integration need.
- Preserve the current full-list staff queue/client-side filtering model initially; add server paging/search only if real measurements justify it.

## 6. Internal API policy

- `/api/asap/...` is an internal frontend/backend contract, not a supported external/public API.
- Preserve current routes, payloads, and observable behavior where practical to minimize simultaneous frontend/backend change.
- Deliberate deviations are allowed for concrete benefit, especially authentication, PocketBase removal, rowversion concurrency, corrected HTTP status semantics, and reliability.
- Document material compatibility deviations.
- No API versioning is required for the initial internal API.

## 7. Staff authentication and authorization

- Replace PocketBase/staff-password auth with Microsoft Entra ID/OIDC on day one.
- Multi-tenant Entra authentication with explicit allowed tenant IDs in external environment configuration.
- Require validated Entra `tid` and `oid`; validate issuer/tenant and then enforce the explicit allowed-tenant list.
- Entra authenticates; SQL `StaffUser` authorizes.
- A matching active local `StaffUser` is required. No general JIT staff account creation.
- Durable authorization identity is (`EntraTenantId`, `EntraObjectId`), with a filtered unique DB constraint/index. UPN, `preferred_username`, and email are never authorization keys.
- Persist readable `UserPrincipalName`, `DisplayName`, and `NotificationEmail` beside the durable IDs so DB/admin inspection remains human-friendly. UPN/display may refresh from validated claims; `NotificationEmail` is app-owned/nullable and is never initialized or repopulated by Entra sign-in.
- Initial super-admin tenant ID, object ID, and readable UPN/email come from external config and are auto-created only when the StaffUser table is empty; no setup page or first-login UPN binding.
- Use OIDC client secret from external config initially; certificate auth is future hardening.
- Production and nonproduction share the same Entra app registration initially, with both redirect URIs configured.
- Normal local Visual Studio F5 uses real Entra/OIDC.
- `/staff` may load anonymously and shows a branded sign-in screen; OIDC begins only after the user chooses Sign in with Microsoft.
- Explicit sign-in endpoint; local-relative validated `returnUrl`, default `/staff`.
- ASP.NET Core server-managed HttpOnly auth cookie, approximately 8-hour sliding lifetime.
- The auth ticket retains `StaffUserId` and the validated sign-in (`tid`,`oid`). Every authenticated request applies the common predicate in `01-PORTING-SPEC.md` section 7.6: exact current binding, loaded allowed tenant, active StaffUser, current role/organization/scope, and participation where required. Rebind/deactivation/tenant removal invalidates old authorization; readable profile changes do not. Sensitive mail persists and checks its recipient tuple; usable-admin and candidate configuration checks use the same predicate. Persistent Data Protection keys do not bypass changed tenant trust.
- Staff API unauthenticated = JSON 401; authenticated but unauthorized = 403, not login redirects.
- Coarse policies may use ASP.NET authorization; resource/library scope is enforced by explicit shared helpers.

## 8. StaffUser model and administration

- Roles remain constrained `staff`, `admin`, `super_admin`.
- Super-admin belongs to Polaris system organization 1; staff/admin reference exactly one non-system library Organization. The StaffUser row remains valid if that Organization is inactive; authorization separately requires both staff and organization active.
- Active staff must have non-null Entra tenant/object IDs. Inactive imported historical staff may remain unbound until an administrator explicitly binds them before reactivation.
- Deactivate staff (`IsActive=false`) rather than deleting them. Staff reactivation requires an active referenced library (unless super-admin); reject reactivation against an inactive library. Re-adding the same (`EntraTenantId`,`EntraObjectId`) reactivates the row when eligible. A recycled UPN/email with a different object ID is a different account.
- At least one currently usable super-admin in Organization 1 must remain. Every StaffUser mutation reducing that eligibility (including rebind) takes the transaction-owned exclusive `ASAP:ActiveSuperAdminInvariant` lock before row locks, re-reads the common predicate, and returns 409 if zero would remain. Candidate external configuration is checked before activation; direct edits that evade preflight fail startup closed if the current policy leaves zero usable administrators. Never silently repair a populated table with bootstrap.
- Promotion to super-admin atomically changes organization to 1; demotion requires an explicit destination library.
- Library admins can manage staff/admin lifecycle within their own library, including explicit Entra identity provisioning/rebinding; only super-admins manage super-admins.
- Library admins may demote or deactivate themselves; zero active admins in a particular library is allowed. Preserve the global invariant of at least one currently usable super-admin under the common predicate.
- Provision/rebind requires allowed tenant ID + object ID + readable UPN/email label and explicit confirmation/audit. Do not use Microsoft Graph to pre-validate identities initially and never infer an object ID from readable claims.
- Readable UPN/profile changes are not identity changes; durable tenant/object-ID replacement is a distinct high-impact audited operation.
- On staff deactivation or any role/organization mutation that contracts/moves usable scope: in one lifecycle transaction deactivate now-invalid active auto-claim rules; clear now-invalid open TitleRequest claims with normal TitleRequest events; clear now-invalid open AdditionalCopy claims with a concise system Notes entry; preserve closed/nonactionable claimant snapshots; and record per-type cleanup counts in `AdministrativeAudit`. Reactivation does not automatically restore old rules.
- Super-admin may be a cross-library auto-claim target.
- Super-admin workflow queue defaults to **All libraries** on each visit; a selected workflow library is temporary and not persisted across sessions.
- Super-admin settings context may retain a local-browser convenience selection, but no server-side persistent current-library context exists.
- `NotificationEmail` is the app-owned nullable primary ordinary staff-notification address. Administrators edit or deliberately clear it through authorized Staff Access; ordinary self-service profile updates do not edit it. Entra sign-in never initializes/repopulates it, so an intentional null remains null. Reject legacy `@staff.asap.local` placeholders.
- Refresh usable `DisplayName` from Entra; keep only `LastLoginUtc` as login-history field.
- Drop legacy PocketBase/Polaris staff identity fields and do not add a StaffUser `LegacyPocketBaseId` column.
- Preserve current StaffUser-owned preferences as first-class columns: weekly action summary enabled/email, purchase-reminder default, additional-copy-reminder default, and mine/unclaimed default filter. `WeeklyActionSummaryEmail` is an optional weekly-summary-only override; weekly summaries fall back to `NotificationEmail` when it is blank. This deliberately makes summary-enabled users with no legacy weekly email but a real primary address newly eligible after migration. Other ordinary staff-directed notifications use `NotificationEmail`. Migration reports old-versus-target recipients/eligibility for both categories. These are StaffUser data, not system/library configuration.

### Staff/participation locking hierarchy

- When a transaction needs multiple row categories, acquire them in this order: `Organization -> StaffUser -> TitleRequest / AdditionalCopyRequest -> dependent claim/rule/operation rows`; within one category use stable key order. An operation acquires only rows it actually needs.
- `StaffUser` is the common serialization point for lifecycle changes versus relationship creation. Manual TitleRequest assignment, AdditionalCopy assignment, FormatAutoClaimRule create/change, and automatic rule execution lock/re-read the target StaffUser with `UPDLOCK,HOLDLOCK` or an equivalent SQL Server pattern and validate activity/role/library eligibility after acquiring that lock. The lifecycle mutation takes the same StaffUser lock before changing scope and performing cleanup.
- Participation-dependent work locks/re-reads the owning Organization first immediately before the final local state commit or durable acquisition of an external-operation intent. If deactivation commits first, the work must not commit/acquire afterward; if the work's local commit/acquisition wins first, later deactivation does not pretend to cancel an external operation that has already begun.
- `ASAP:ActiveSuperAdminInvariant` remains the separate global application-lock invariant for final-super-admin mutations. Never hold row locks or SQL transactions across Polaris/Postmark network calls.

## 9. Patron authentication/session model

- Patron authentication remains Polaris barcode/PIN.
- No persistent PatronUser/profile table.
- Patron session is an opaque 256-bit random Base64URL bearer token.
- Persist only SHA-256 token hash; never persist the token itself or PIN.
- 1-hour absolute session lifetime; no sliding lifetime.
- Multiple active sessions per barcode are permitted.
- Logout revokes the presented session.
- Browser stores patron bearer token in `sessionStorage`.
- Session retains only required context such as plaintext barcode, home/experience/effective organization IDs, and expiration. Every patron-authenticated request re-resolves `EffectiveOrganizationId` and requires the Organization to remain active in addition to token-hash, expiration, and revocation checks.
- Final session issuance is race-safe with library deactivation: after Polaris authentication/effective-library resolution, a short SQL transaction locks/re-reads the effective Organization row, verifies it is still active, and inserts the session. Library deactivation uses the same row as its serialization point while setting inactive and revoking sessions for that effective organization. Reactivation never clears session revocation.
- Clean expired sessions with a daily-ish Hangfire job.
- Use ASP.NET partitioned IP rate limiting for login attempts: default 20/IP/5 minutes, configurable; return 429 + Retry-After with generic messaging.
- No persistent barcode lockout/rate-limit table initially.
- Permanent nonproduction initially permits any patron accepted by its nonproduction Polaris environment; a dedicated patron allowlist is deferred unless trivially needed.

## 10. Polaris and live reference data

- Use the latest/current/prerelease CLC `Clc.Polaris.Api` package appropriate at implementation time; CLC prerelease is acceptable.
- Extend that package when missing capability is required; do not create a parallel PAPI protocol client in ASAP.
- All initial PAPI calls use configured application/system credentials.
- A configured system Polaris user ID is required for mutation attribution in Polaris.
- ASAP event/audit attribution still records the actual Entra staff actor.
- Do not persist or dynamically resolve per-staff Polaris IDs in the initial port.
- Retry safe/read operations as appropriate; do not blindly retry ambiguous mutations.
- Use the purpose-specific `HoldPlacementOperation` lifecycle in `01-PORTING-SPEC.md` section 9.1: unique incomplete operation/attempt number, exclusive token/epoch/lease, durable create/reply phase markers and reply context, correlated final evidence, and bounded reconciliation/operator resolution. Finding an incomplete row authorizes observation, not replay. No elapsed-time/empty-lookup rule permits a second create; no generic operation framework.
- Hold identity and fulfillment follow `01-PORTING-SPEC.md` section 9.2: RequestGUID/reply qualifiers are operation context, while PolarisHoldId is a proven final HoldRequestID. Capture that ID for new placement and actual existing-hold adoption when available on the existing operation; no second journal. Documented final success may have an unavailable final ID without authorizing replay. Terminal unclaimed/cancelled/expired closure needs the exact tracked hold plus expected BIB/patron, not a historical same-BIB row. Missing/ambiguous identity or required provider failure preserves workflow state with a safe diagnostic. Positive current checkout stays title-level; normal manual/timeout closure stays available. This corrects the pinned terminal matcher deliberately.
- Pickup-preference updates are idempotent and do not require a general mutation journal.
- Polaris integration configuration is system-only in `[asap].[PolarisSettings]`; no library Polaris overrides.
- Saving Polaris settings is allowed even if connection testing fails; validate local shape and expose explicit Test Polaris.
- Polaris setting changes invalidate local effective caches and apply to the next operation.
- Test Polaris remains a narrow authentication/connectivity test; broader live validation is handled separately by release validation.
- Patron/reference information is live Polaris data with small `IMemoryCache` usage to collapse duplicate reads.
- Reference organizations/patron codes/material types/pickup branches use roughly 10-minute memory caching plus manual super-admin refresh.
- Common cache warmup may run at startup but failure is nonfatal.
- Do not build a persistent SQL mirror of branches/patron codes/reference lists merely to replace PocketBase caching.

## 11. SQL/DACPAC and concurrency

- SQL Server is the sole target application database.
- `database/Asap.Database/Asap.Database.sqlproj` is SDK-style `Microsoft.Build.Sql` and is the schema source of truth.
- Database compatibility 160; `BlockOnPossibleDataLoss` for normal deployment.
- App schema `[asap]`; Hangfire objects live under `[HangFire]` and are outside DACPAC ownership.
- EF migrations do not own schema.
- Primary keys are `bigint IDENTITY` unless the domain has a durable external key; `Organization.Id` is the actual Polaris organization ID.
- Public/internal API IDs and deep links use the new bigint IDs.
- Use `rowversion` on mutable workflow records and selectively mutable settings/admin records.
- Expose rowversion as opaque Base64 `version` in DTOs; mutations submit it in the body.
- Stale mutation returns 409; frontend refreshes current data and does not silently replay the mutation.
- Related ASAP-owned local writes for an action occur in one SQL transaction, including state, event/tag/audit, and outbox records.
- Never hold a SQL transaction across a Polaris/Postmark network call.
- SQL constraints enforce structural invariants; workflow behavior stays in explicit C# logic rather than triggers.
- FK behavior defaults to restrict; use set-null only where explicitly required, such as surviving AdditionalCopy source deletion.
- `[asap].[SchemaVersion]` is an explicit monotonic integer compatibility contract bumped only for application/schema contract changes.
- `[asap].[DeploymentState]` records successfully applied application DACPAC and dependency schema/assets. Release classification uses app hash changes independently of SchemaVersion plus actual Hangfire/dependency DDL and other DB mutations. Any such change requires quiescence/verified backup before DDL. Actual schema state is authoritative after failure; file-only releases update only installed file metadata, not DeploymentState.
- Runtime expected SchemaVersion mismatch: process remains live, liveness healthy, readiness unhealthy, normal application behavior blocked.
- Normal code rollback/restart requires both current application SchemaVersion and actual Hangfire/dependency schema compatible with that code. Never silently publish an older DACPAC or downgrade a dependency schema; exceptional repair/restore remains explicit.
- Future ordinary schema evolution stays DACPAC-first; rare data transformations use explicit versioned one-off scripts, not a second general migration runner.

## 12. SQL identities and environment topology

- Production runtime connection uses Windows Integrated Security.
- IIS application pool runs under a dedicated traditional domain service account, with **separate production and permanent-nonproduction identities** from day one. gMSA remains a later operational refinement.
- Runtime service accounts are least-privilege and are not `db_owner`; they receive only the DML/EXECUTE permissions required for `[asap]` plus the minimum rights required for already-provisioned `[HangFire]` objects. They do not receive DACPAC, DDL, backup, or deployment rights.
- Hangfire schema creation/upgrades are performed by the deployment operator using version-matched dependency-owned SQL assets; production runtime is configured not to require schema creation/alteration.
- Deployment/backup/DACPAC/Hangfire-schema commands run using the interactive operator/administrator Windows identity with separately sufficient SQL permissions, not the app-pool identity.
- Production and permanent nonproduction run on separate Windows servers and separate SQL Server infrastructure/instances.
- The new .NET production host is a different, already-existing Windows server from the old PocketBase host.

## 13. Organization model

- `[asap].[Organization]` uses actual Polaris organization ID as PK.
- Polaris org 1 is the real system organization, universal, and permanently active.
- Persist organization 1, participating libraries, and inactive historically referenced libraries required for history/FKs.
- Branches remain live/reference memory data rather than persistent organization rows unless later evidence requires otherwise.
- Staff/settings scoped FKs use real organization IDs; no null system scope and no `-1` sentinel.
- `IsActive` replaces the narrower patron-only participation flag as the broad ASAP participation switch.
- Newly discovered libraries enter as inactive; super-admin explicitly activates them.
- First activation immediately uses inherited system defaults; no separate onboarding/publish state.
- Library deactivation retains history/config but blocks new patron/staff participation; patron sessions are invalidated and every later bearer-authenticated request also rechecks the session's effective Organization as active. It does not mutate `StaffUser.IsActive` or invoke staff-deactivation-specific cleanup merely because the organization was disabled. Final patron-session issuance serializes against this deactivation on the Organization row so a racing login cannot escape revocation; reactivation never resurrects revoked sessions.
- Existing staff relationships, claims/rules, and configuration remain stored through library deactivation. Reactivating the library restores authorization eligibility for StaffUsers already active without rewriting them; individually inactive staff remain inactive.

## 14. Settings and external configuration

- There is no generic `[asap].[Settings]` or EAV key/value table. `13-SETTINGS-SCOPE-INVENTORY.md` is the normative settings scope/storage/reset contract.
- Organization `1` is the real system/default scope; do not use null/negative/synthetic scope sentinels.
- `SystemSettings` and `PolarisSettings` are system-only and constrained to Organization `1`; patron embed origins are normalized system-only rows.
- `WorkflowSettings`, `PatronSettings`, and `EmailSettings` use complete system rows plus sparse nullable library field overrides. A null field inherits only that field. Ordinary overrideable library text input that normalizes to blank is stored as null/no override where current behavior treats blank as fallback; secret edit forms keep blank=preserve and require an explicit action to clear an override.
- Publication options, common-creator lists, and allowed patron-code IDs use relational whole-set tables where absence of a library set means inherit and presence means complete replacement, with blank/empty library input treated as reset/inherit.
- External search providers use stable system provider identities plus sparse library override rows instead of numbered columns.
- Patron custom fields/options and per-format custom-field rules are relational library-owned configuration, not JSON and not inheritable.
- Built-in material-format field behavior is represented by typed `MaterialFormat`/`MaterialFormatOverride` columns; custom-field per-format mode/label behavior uses `MaterialFormatCustomFieldRule`; do not preserve competing `patronFormatRules` JSON.
- `EmailTemplate`, `Branding`, `MaterialFormat`, and `FormatAutoClaimRule` remain specialized relational models.
- **Reset inherited overrides** removes only inherited overrides and never library-owned custom fields, custom formats, custom rejection templates, or auto-claim rules/history.
- C# bootstrap creates missing required system/default/built-in records but never overwrites persisted administrator choices.
- One tightly permissioned external JSON file contains pre-application/environment/bootstrap configuration and secrets: connection strings, Entra app secret/tenant allowlist/bootstrap admin, environment-specific Data Protection key-ring path, filesystem paths, Hangfire schedules, environment banner, nonproduction recipient-domain safety, etc.
- Do not duplicate ordinary SQL-admin settings into external config.
- Do not bootstrap/copy Polaris or Postmark operational settings from external JSON into SQL on startup. SQL is authoritative after cutover migration/provisioning. Polaris credentials migrate from PocketBase; the new Postmark server token is supplied as target-only secure import input and encrypted before persistence because SMTP credentials are not Postmark credentials.
- External JSON is startup-only; changes require application restart. No reload-on-change behavior.
- Checked-in `appsettings.json` points `Asap:ConfigFile` at `C:\ProgramData\clc-asap\Config\application.json`; deployment never overwrites that external file.
- Local F5 layers checked-in `appsettings.Development.json` to point `Asap:ConfigFile` at an ignored `Development.local.json` of the same general shape; commit only a safe example/template.
- Business timezone is `America/New_York`; persist timestamps in UTC.

## 15. SQL-stored reusable credentials

- Reusable Polaris/Postmark credentials that are part of SQL-managed integration settings are protected with ASP.NET Core Data Protection before persistence; SQL stores ciphertext, not plaintext.
- UI/API treat credential fields as write-only; never return raw secrets and never log them.
- Blank secret input preserves the current value.
- Explicit Clear is deliberate and audited.
- Library Clear removes an email credential override and resumes inheritance.
- System Clear is allowed only with explicit confirmation including affected dependent-library count; diagnostics then show integration unconfigured.
- Masked audit/display convention: length >=16 shows first 4 + last 4; length 4-15 first 1 + last 1; length 1-3 fully masked.
- Use a dedicated/versioned Data Protection purpose for integration-secret protection. Production/nonproduction use separate key rings and separate environment-specific X.509 key-encryption certificates with tight key/private-key ACLs. Do not use machine-bound DPAPI as the sole durable-ring protection because replacement-host recovery is required. Decrypt only at the integration boundary. The corresponding key ring plus recoverable certificate/private key are required recovery material alongside SQL backups; do not purge keys/certificate material still needed to decrypt persisted ciphertext.
- An external enterprise secret vault remains optional future hardening, not an initial-port dependency.

## 16. Email and Postmark

- Use the current/latest `Clc.Postmark.Api` package rather than a custom transport.
- Preserve system-default + library-override email delivery configuration.
- Use a durable SQL outbox; ordinary notification delivery is not a prerequisite for the owning business mutation and email is never sent synchronously inside that business transaction.
- Canonical outbox states are `pending`, `sending`, `sent`, `failed`, and terminal `suppressed`. `failed` is manually retryable; `sent` and `suppressed` are not.
- Before creating patron-facing email content, refresh current patron email from Polaris where required. Snapshot sender, recipient, subject/body/content and relevant business identity when a deliverable row is created; resolve the effective Postmark transport token/config at send/retry time.
- Distinguish immutable `business_event` from `staff_authorization_sensitive` mail. Sensitive rows persist RecipientStaffUserId, recipient Entra tuple, authorization scope, intended address, and `RecipientAddressKind=notification_email|weekly_summary`. Immediately before every send/retry apply the common current binding/allowed-tenant/activity/role/scope/participation predicate, resolve the current address by that explicit kind, and require equality with `ToAddress`; never infer the kind from `BusinessKey`. `notification_email` always uses `NotificationEmail`; `weekly_summary` uses nonblank `WeeklyActionSummaryEmail` and otherwise `NotificationEmail`. Otherwise terminally suppress without sending. Immutable business-event mail may drain after participation changes without gaining this staff dependency.
- Missing notification configuration never rolls back an otherwise valid ordinary business action. If required sender/recipient/transport information is already unavailable when the notification intent is created, persist a deterministic terminal `suppressed` intent with a safe reason such as `mail_not_configured` when an intent should be auditable. Existing optional-staff-recipient semantics may still produce no row when there is simply no notification recipient/request. A suppressed row never resurrects merely because configuration later appears. If a previously valid queued message loses transport configuration at delivery time, keep it as retryable `failed` with safe `mail_not_configured` detail and retain its payload for operator retry after configuration is restored.
- Commit business transaction/outbox intent first, then enqueue Hangfire; a sweeper repairs enqueue gaps. Workers atomically claim due rows with `Status=sending`, claim-time `SendingStartedUtc`, a unique lease ID, `LeaseExpiresUtc` two minutes later, and the resulting rowversion before calling Postmark. The complete provider operation has a 30-second timeout and must start within 30 seconds of claim. Reclaim/retry may begin only after lease expiry, leaving at least 60 seconds beyond the latest legitimate provider completion.
- Treat every expired `sending` lease as potentially transport-ambiguous; do not claim persisted state distinguishes a pre-call crash. Final worker updates compare `Status=sending`, expected `LeaseId`, and post-claim rowversion/equivalent ownership so an old worker cannot overwrite a reclaimed/newer attempt or schedule another retry.
- Email transport is explicitly at-least-once: a crash or timeout during/after possible Postmark acceptance may cause a duplicate retry after the safety boundary. Business/outbox idempotency prevents duplicate local notification creation but cannot guarantee provider exactly-once semantics.
- Use bounded transient retry, durable `failed` details, stale-lease recovery, and manual retry. `pending`, `sending`, and `failed` rows retain subject/body payload for as long as they remain deliverable/retryable. Only terminal `sent` and `suppressed` rows are payload-purge eligible after 90 days; retain delivery/operational metadata.
- Validate Postmark webhook authenticity and make webhook processing idempotent.
- Make notification-intent creation idempotent with a filtered unique SQL index on non-null `EmailOutbox.BusinessKey`; deterministic keys are globally namespaced and duplicate-key races resolve to the existing row as success. Ordinary weekly summary keys are unique per StaffUser + reporting period. An accepted explicit forced weekly-summary invocation gets one durable generated `ManualRunId`; force-run keys such as `weekly-summary-force:{ManualRunId}:{StaffUserId}` intentionally permit a resend while retries of that **same** forced run remain idempotent. A later explicit force gets a new ManualRunId. Null keys are reserved for intentionally repeatable ad-hoc/test messages.
- Migrate historical delivery/audit metadata but start the new outbox empty; never replay historical messages.
- Test email follows the same durable outbox -> Hangfire -> Postmark path, using the selected organization's effective configuration.
- Library admins can send test email and inspect/retry `failed` messages for their own library; super-admins can do so for any context; ordinary staff cannot. `suppressed` rows are visible diagnostically but never expose Retry.
- `Environment.IsNonProduction` governs the nonproduction recipient-domain rule in `01-PORTING-SPEC.md` section 14, independently of `ASPNETCORE_ENVIRONMENT=Testing` for testing authentication. When true, every outgoing patron/staff/weekly-summary/Test email recipient must pass the same case-insensitive exact-domain predicate against external `EmailSafety.AllowedRecipientDomains` at intent creation and before every provider call/send/retry. Subdomains require explicit entries; a missing/empty list allows nobody, and malformed configured domains fail startup configuration validation rather than broaden matching.
- A domain-blocked intent becomes terminal `suppressed` with `recipient_domain_not_allowed`, zero Postmark calls, normal deterministic `BusinessKey`/idempotency behavior, and no rollback of its otherwise valid owning business mutation. Production (`IsNonProduction=false`) does not apply this restriction. Use the existing outbox path/five states; no second environment detector or mail pipeline. Deterministic tests in `06-TESTING-CI.md` section 4.1 are a release gate under `08-RELEASE-VALIDATION-NOTES.md` section 6, without a live send requirement.
- Production and nonproduction may share the same Postmark server/token initially.

## 17. Email templates and branding

- Use relational `EmailTemplate` for built-ins and rejection templates.
- Support system templates, nullable library overrides, rejection inherit/override/hide, and library custom rejection templates.
- Use nullable `SourceTemplateId` self-FK where needed for lineage.
- Block deletion of required/dependent system templates; C# seeds missing built-ins without overwriting admin edits.
- Branding uses its dedicated row/table with independent logo/alt-text inheritance.
- Store image bytes, content type, filename, alt text, timestamps, and rowversion.
- Image and alt text inherit independently from system to library.
- Accept PNG/JPEG/GIF, not SVG; max 2 MB and max 4096x4096; validate signature/type/dimensions and store original bytes unchanged.
- Branding changes are administratively audited.

## 18. Material formats, publication options, workflow taxonomy

- Use stable relational `MaterialFormat` identity plus `MaterialFormatOverride` for library overrides of system formats.
- TitleRequest and auto-claim rules reference MaterialFormat ID, not free-form format text.
- System format code is stable/immutable after creation; label and behavior are editable.
- Library custom formats are distinct MaterialFormat rows and may not collide with system codes; use override instead.
- A future system format whose code collides with an existing library custom format is a blocking/admin-resolution condition, not an automatic remap.
- System format disable is soft; no hard delete of system format.
- Library override may re-enable a system-disabled format.
- Library custom format may be hard-deleted only when unreferenced.
- Resetting a library override deletes the override row.
- Do not persist a request-level material-format label snapshot; historical request display resolves through current format configuration.
- Publication options use relational `PublicationOptionSet`/`PublicationOption` whole-set inheritance; no library set means inherit, and a library set is the complete replacement with blank/empty library input treated as reset/inherit.
- Workflow tags are a fixed relational seeded taxonomy with join table; no general CRUD UI required.
- Request statuses/close reasons are constrained application codes/SQL checks rather than editable taxonomy tables.

## 19. Auto-claim

- Auto-claim rules are versioned: assignment change deactivates old rule and inserts new rule.
- At most one active rule per organization + material format.
- Request records the exact rule used for an automatic claim.
- Disabled/inapplicable material format makes the rule dormant rather than deleting it; re-enable restores applicability.
- Staff deactivation deactivates their rules; staff reactivation does not automatically restore those rules. Any role/organization mutation that contracts or moves authorization scope uses the common StaffUser-serialized lifecycle transaction and the TitleRequest/AdditionalCopy cleanup contract in §8. Every writer that creates/reassigns a rule or claim takes the same target-StaffUser serialization lock and revalidates eligibility after lock acquisition. Promotion to super-admin broadens scope and needs no cleanup.
- Every **active** FormatAutoClaimRule has a non-null active assignee whose current role/library scope is eligible for the rule library; an active super-admin is eligible across libraries. Migration may preserve a source rule with a missing/unmapped/ineligible assignee only as inactive historical configuration, with nullable StaffUser FK only when the source assignee cannot be mapped, and reports the normalization rather than substituting another person.
- Saving/changing a rule does not retroactively sweep existing requests.
- Evaluate automatic claim on request submission and material-format change.
- Never overwrite a manual/legacy claim with automatic assignment.
- Super-admin may be configured as a target across libraries.

## 20. Title requests, events, additional copies, deletion

- Implement workflow transitions explicitly in C#; no workflow engine/event-sourcing architecture.
- Current-state row is authoritative; append-only events provide history.
- Patron submission identity/contact/library snapshots on a TitleRequest remain historical and are not rewritten by routine later edits. `PreferredPickupBranchId`/`PreferredPickupBranchName` are the explicit exception: they are the request's current recorded pickup preference and may change only through the dedicated validated pickup-preference workflow; generic request editing cannot change them. Successful Polaris pickup mutation updates those fields plus the normal note/event, while Polaris failure leaves them unchanged and stale/live changed-since-load conflicts return `409`.
- Preserve established workflow status and close-reason semantics.
- Keep nullable `LegacyId` as business/provenance data, distinct from the temporary PocketBase migration mapping.
- Remove broad `editedBy` field; use targeted event/audit attribution instead.
- Do not automatically purge closed requests initially.
- Explicit hard deletion of closed requests creates a permanent reduced `DeletedRequestAudit` entry and removes dependent operational detail.
- Reduced deletion audit intentionally omits unnecessary names/email/freeform notes/full barcode; retain only useful identity/context and masked barcode where appropriate.
- Library admins can bulk-delete closed requests in their own library; super-admins can do so for selected/all libraries.
- Claim state stores StaffUser FK where valid plus display-name snapshot, timestamp, type, and exact rule where applicable.
- Preserve current duplication where system workflow information appears in both notes and events.
- AdditionalCopy is a separate domain/table with constrained Open/Closed state.
- AdditionalCopy can retain a nullable source TitleRequest FK with set-null semantics; it survives source deletion.
- Source claim snapshot is copied once where current behavior does so; AdditionalCopy thereafter evolves independently.
- One deletion-audit model covers both request types.

- Identifier changes/clears are rejected in hold_placed and closed and cannot bypass protection through a combined transition/BIB edit. Normalized unchanged identifiers are no-ops. Completed successful operations retain their immutable placed BIB; migration preserves recorded-BIB history through reopening under section 6.10 of the migration document. Positive checkout fulfillment never switches to a new identifier-derived BIB; terminal hold matching also requires the tracked final HoldRequestID. Historical protection (including explicit null BIB) survives a committed reopen and a separate later edit, regardless of whether runtime terminal correlation is available.
- Whenever a retained claimant becomes effective on open work, validate current StaffUser eligibility. AdditionalCopy reopen uses Organization -> StaffUser -> task locking, preserves an eligible same-library/super-admin claim, otherwise clears effective fields with Notes attribution; it never assigns a substitute or actor automatically. Closed historical claims remain unchanged until reopen.

## 21. Event history

- Events have stable relational fields plus small metadata JSON for event-specific details.
- Constrain actor type to system/staff/patron for new data.
- New staff events populate nullable StaffUser FK plus ActorName snapshot.
- Historical PocketBase staff events do not infer StaffUser FK from ActorName; StaffUserId remains null.
- Unknown historical event type maps to constrained `legacy` and stores the original type in metadata; new .NET code never emits `legacy` for new normal events.
- Unknown historical actor type blocks migration until explicitly corrected/mapped.

## 22. Analytics

- Analytics is required for the first .NET production cutover.
- Preserve user-facing metrics/behavior and authorization scope unless a documented beneficial correction is required.
- Move aggregation to SQL-side Dapper/ADO queries rather than loading all rows and aggregating in JavaScript.
- All authenticated staff can view analytics for their own authorized library; super-admin can view consortium-wide or select a library.

## 23. Hangfire/background jobs

- Hangfire runs inside `Asap.Web` under IIS using SQL storage.
- Hangfire objects use `[HangFire]` schema outside DACPAC; initially use same SQL database through separate `HangfireDatabase` connection string.
- IIS uses AlwaysRunning/preload so job execution is not dependent on user traffic.
- Recurring schedules live in external environment JSON and require restart to change. The authoritative matrix/defaults are in `01-PORTING-SPEC.md` and must be completely represented in the operational template embedded in `scripts/deployment/Initialize-AsapTestHost.ps1`; `examples/Config.example.json` is its test-enforced documentation-pack representation. All cron expressions use the configured business timezone.
- Preserve the hourly workflow as one non-overlapping `asap-workflow-processing` orchestrator with mandatory phase order: acquired-hold recovery -> timeouts -> purchase promotion -> new hold placement -> fulfillment tracking. Do not register those phases as independent same-cron recurring jobs.
- Use one canonical five-minute identifier processor. It intentionally combines the dedicated path's BIB persistence/reconciliation/tag/`skipped_no_isbn` behavior with the older hourly path's bounded retry state. The Polaris boundary classifies `Found`, `DefinitiveNotFound`, `TransientFailure`, and `OperationalFailure`; only a successfully completed zero-result search may become `not_found`, unknown/provider failures never do. Retry transient failures on fair bounded-queue revisits at the normal five-minute cadence with no extra backoff; the fifth consecutive transient failure becomes `error_max_retries`. Operational/auth/config/request/protocol failures do not consume/reset the request retry budget and surface/fail the run. Found/not-found/skipped and manual retry reset retry/error state. Permitted pre-placement identifier changes are stronger atomic invalidations: clear result/retry/error/check timestamp, the three identifier-derived workflow tags, and old BIB; nonblank replacement -> `pending`, blank -> `skipped_no_isbn`. The old BIB may be stored again only through a new explicit validated/reconciled staff BIB action. `error_max_retries` is explicitly recoverable through the scoped **Retry identifier check** action. The old hourly ISBN implementation is retired after semantic consolidation, not because it is equivalent.
- External `Hangfire:ProcessingLimits` preserves the current queue-specific -> timeout-family -> global-default precedence and source default/range contract. Effective legacy cron/limit values are frozen and reconciled separately from SQL settings before jobs are enabled.
- Permanent nonproduction runs recurring jobs normally using its own schedules/data/Polaris environment.
- Jobs are idempotent/single-execution at the job level and resilient per item; one item failure must not abort the entire batch.
- Process sequentially in oldest-first finite cycles using persisted `QueueProgress` per logical queue/scope (`01-PORTING-SPEC.md` section 23.1). Keep existing limits, immutable creation/ID keys and a fixed cycle ID watermark; checkpoint only after durable handling, resume across restart, wrap only at exhaustion, and isolate manual scope. No parallelization or JobRun framework. HoldRecovery is a separate bounded logical phase using existing HoldPlacement limits, not a new setting.
- Each item uses its own local SQL transaction and revalidates rowversion/state before applying results; participation-dependent result commits and external-operation acquisitions first lock/re-read the owning Organization in the common lock order and require it still active. A deactivation that wins first prevents the later commit/acquisition; work whose short local commit/acquisition wins first may finish/reconcile its already-begun external operation without holding SQL open across the network. Stale request work still skips safely.
- Use bounded automatic retry only for appropriate transient job failures.
- Do not add an application JobRun history table; Hangfire history plus domain events/logs are sufficient.
- Manual Run Now enqueues the same real Hangfire job path and reports queued state; duplicate concurrent runs are refused. Ordinary weekly-summary execution remains recipient+period idempotent. An explicit forced weekly-summary execution is a distinct manual business event: generate one `ManualRunId` when accepted, persist/pass it as an immutable Hangfire argument/audit-log context, and reuse it on retries so that forced run is still idempotent. A later explicit force gets a new ID and may intentionally resend.
- Library admins can run applicable jobs for their own library; super-admin can run selected-library or system-wide jobs.
- `Organization.IsActive` gates participation-dependent automation and equivalent library-scoped manual runs: new identifier processing, timeout/promotion/hold/fulfillment workflow changes, and ordinary library summaries skip inactive-library work; recovery/completion of already-acquired holds is the explicit exception in section 9.1 of the porting spec. The owning Organization row is the serialization point immediately before result commit/external-operation acquisition as described above. Organization refresh, session cleanup, outbox recovery/delivery, and terminal-payload cleanup continue. Already-committed immutable business-event outbox rows may drain after deactivation; authorization-sensitive staff rows instead revalidate current staff/library authorization and suppress if it no longer exists.
- Weekly summary contents are authorization-scoped per recipient: staff/admin see only their own active library's counts/samples/links; super-admin may receive consortium-wide data across active participating libraries. Do not reuse one global payload for all recipients; this intentionally corrects the PocketBase behavior.
- HoldPlacementOperation acquisition is the durable boundary that authorizes a later hold mutation: inside a short transaction lock/re-read Organization first, then the TitleRequest, validate participation/current status/BIB/autohold/live-required inputs, and create the unique incomplete operation before committing. An incomplete operation is a request-mutation barrier: status/close/reopen, hard delete, identifier, explicit BIB, `AutoHold`, and pickup-preference mutations return `409` while it exists. Do not keep SQL open across Polaris. If a conflicting request mutation commits first, acquisition sees the new state; if operation acquisition commits first, the edit is blocked until the operation is terminally reconciled. Hold delivery re-resolves live patron/pickup immediately before the external call and snapshots the actual pickup used.
- Remove the old cron-secret mechanism.

- Recovery is the first existing workflow phase and selects incomplete acquired operations independently of Organization activity. First unmarked mutation/reply and safe completion may finish previously authorized work after deactivation; no new inactive-organization acquisition is allowed. Three unsuccessful recovery evaluations (or immediately unavailable proof capability) produce operator-required state without lifting the barrier. Super-admin evidence-based resolution uses the existing operations surface, not forced replay.
- OutstandingTimeout means unreviewed-suggestion auto-rejection: current suggestion, CreatedUtc older than the strict injected-clock calendar cutoff, effective enabled setting and ordinary active participation; close as rejected and use its configured send-email/template behavior. No outstanding_purchase expiration policy is introduced. PendingHoldTimeout uses pending_hold/UpdatedUtc -> closed/rejected/no timeout email; HoldPickupTimeout uses hold_placed/UpdatedUtc -> closed/hold_not_picked_up/no timeout email; AdditionalCopyTimeout uses open/UpdatedUtc (source missing-updated fallback to created) -> independent task closure/no timeout email. Porting-spec section 23.2 supplies the common scheduled/manual, concurrency and notification rules. Fair cursor order is not timeout age.
- After recovery, retain the four timeout queues' order before promotion/new placement/fulfillment. Later-phase candidates must recheck applicable expiry and defer to timeouts even when a capped timeout scan has not reached that row. Queue progress advances only after the deferral/outcome is durably handled. Known global operational integration failures stop safely; handled per-item failures advance so poison rows cannot starve later work.

## 24. Health, diagnostics, logging, monitoring

- Expose `/health/live` and `/health/ready` anonymously with deliberately minimal healthy/unhealthy payloads and no sensitive dependency/server details.
- Liveness means process is alive; readiness checks critical local SQL/init/schema requirements.
- Polaris/Postmark outages do not make readiness fail merely because an external integration is temporarily unavailable.
- Detailed SQL/Hangfire/config/Polaris/Postmark diagnostics require authenticated super-admin access.
- Use NLog structured logging to local rolling files as canonical production application logs; console remains secondary/development-friendly.
- Keep 30 days of local rolling logs with size rollover as required.
- Do not log secrets, raw credentials, full barcode, patron names/email/phone, or raw Polaris payloads containing PII.
- Use correlation IDs consistently.
- Include a small repository PowerShell PRTG sensor/check that calls the minimal readiness endpoint and emits valid PRTG XML; keep the application monitoring-vendor agnostic.

## 25. Administrative audit

- Use one `[asap].[AdministrativeAudit]` for high-impact access/configuration changes, not every business mutation.
- Store stable actor, organization, target, action, UTC timestamp, and small Details JSON.
- Mask secret changes; never store secret values in audit.
- Super-admin can inspect all audit history; library admin can inspect own-library audit; ordinary staff cannot.
- Staff durable Entra identity provisioning/rebinding and readable UPN/profile changes are audited distinctly.

## 26. Testing

- One `Asap.Tests` .NET project contains unit, ASP.NET integration, and real-SQL tests organized by category/fixture.
- Persistence behavior is validated against real SQL Server; do not substitute EF in-memory/SQLite for SQL contract tests.
- Local development uses SQL Server Developer Edition; instance/connection string is developer-configurable rather than a mandated named instance.
- CI uses SQL Server 2022 and exercises DACPAC + exact SchemaVersion contract.
- Normal PR tests fake Polaris/Postmark; live external-service tests are not ordinary PR requirements.
- Keep/adapt useful existing jsdom tests.
- Add targeted Playwright critical journeys; run every PR and push to `main`.
- Playwright uses a test-only auth handler that lives in `Asap.Web` but is registered only when `ASPNETCORE_ENVIRONMENT=Testing`.
- Development and production never register the test scheme.
- Add targeted axe-core/Playwright checks; CI fails on serious/critical violations.
- Keep explicit keyboard/focus tests where automated accessibility scanning cannot judge behavior.
- Do not build a temporary PocketBase-vs-.NET parity harness.

## 27. Migration architecture

- PocketBase -> SQL migration is an explicit one-time cutover workflow, never auto-detected/run by normal deployment.
- Keep `Asap.Migration` source permanently in the repo for audit/reproducibility, but exclude it from normal web publish/deploy output.
- Build migration logic alongside relevant vertical slices rather than at the end.
- Release a separate versioned **self-contained `win-x64`** migration artifact from the same version tag as the web release; include SQLite native/runtime dependencies so the old server does not need .NET installed.
- `Asap.Migration` has distinct `export` and `import`/reconciliation modes.
- Export runs on the **old PocketBase server** against the stopped local PocketBase SQLite database and file storage.
- Export produces normalized UTF-8 JSON files plus manifest/checksums; importer does not depend on raw PocketBase schema/files.
- Transfer the normalized package through the trusted administrative path to the **new .NET server**.
- Import/reconciliation runs on the new ASAP server against the target SQL Server.
- Migration package is not additionally encrypted initially; it is short-lived, tightly ACL-restricted, transferred only through trusted administration, and deleted from both servers immediately after successful validation.
- Target SQL database must be a fresh migration target: intended DACPAC/structural/static seeds are allowed, but runtime/business/bootstrap data must be empty and the database must never have been used for production-hostname preflight. Unexpected target data blocks import. Recovery is reset/recreate/rerun, not clever idempotent merge.
- Import in dependency-ordered phase transactions.
- Support dry-run/preflight validation and deterministic reconciliation reports.
- Reconciliation is a hard go/no-go gate: every discrepancy must be reconciled or explicitly accounted for as an intentional transformation.
- Migrate meaningful per-request processing state; initialize Hangfire infrastructure/state fresh.
- Do not migrate patron sessions.
- Migrate historical email delivery/audit metadata but create no new outbox entries from history.
- Export the reusable PocketBase Polaris credential material needed for continuity and protect it during target import; do **not** export the legacy SMTP password as a Postmark credential. Supply the new Postmark server token separately as target-only secure import input that is excluded from command lines, normalized export JSON, manifests, and logs.
- Preserve source timestamps where meaningful.
- Use a generic temporary `LegacyPocketBaseMapping` for old PocketBase IDs and deep-link resolution; do not add legacy ID columns to every target table.
- Temporary old request links resolve through mapping to the new bigint ID and normalize the browser URL.
- Remove migration mapping later only through an explicit post-cutover decision, not automatically.

## 28. Migration transformation rules

- Active StaffUser migration requires an explicit operator-supplied PocketBase-staff-ID -> allowed Entra tenant ID/object ID mapping plus readable UPN/email. Missing/malformed/duplicate/conflicting durable bindings block import; never derive identity from UPN/email. Inactive historical staff may remain unbound until explicit reactivation binding. Staff/admin may preserve references to inactive historical organizations regardless of their own `IsActive`; authorization remains blocked by the organization state, and explicit staff reactivation requires an active library.
- After StaffUser import and before the web app/Hangfire can start, migration hard-gates on at least one active `super_admin` with a valid allowed-tenant (`EntraTenantId`,`EntraObjectId`) binding. If none exists, `Asap.Migration` explicitly provisions/promotes the configured bootstrap identity while the app remains stopped, reusing an already-bound matching StaffUser instead of duplicating it, reports the intervention, and re-runs the gate. Invalid/missing/conflicting bootstrap identity blocks cutover; ordinary startup bootstrap is not a recovery mechanism for a populated StaffUser table.
- Staff migration preserves every current user preference: weekly summary enabled/email, purchase-reminder default, additional-copy-reminder default, and mine/unclaimed default filter.
- Freeze `effective-legacy-runtime-config.json` only for system/global SQL-bound values whose effective value requires pinned DB/environment/code fallback resolution; library-scoped configuration remains in scope-aware domain export files. Staff URL resolution explicitly covers persisted `staffUrl`, `ASAP_STAFF_URL`, `ASAP_PUBLIC_URL`, and the related missing-record helper behavior involving `ASAP_BASE_URL`.
- Separately freeze `effective-legacy-operational-config.json` for current cron schedules and global/timeout/queue-specific processing limits/provenance; reconcile it to target external JSON before Hangfire runs. Capture obsolete hourly-ISBN queue overrides and report their explicit resolution rather than silently dropping them.
- Resolve migrated `NotificationEmail` in order: operator identity-map override -> real PocketBase staff email -> real legacy weekly-summary email -> null; reject `@staff.asap.local`. Migrate the legacy weekly-summary email independently as `WeeklyActionSummaryEmail`. Report old-versus-target recipient/eligibility deltas, including newly eligible weekly summaries and changed ordinary reminder recipients; this normalization is intentional.
- Every imported open/actionable TitleRequest and open AdditionalCopy claimant is mapped then independently checked for target activity/identity/trust/role/library eligibility. Clear/report unmapped, inactive, and out-of-scope claims (including demoted super-admin); preserve valid same-library and cross-library super-admin claims. Organization inactivity alone never clears a valid stored relationship. Closed history retains mapped FK/attribution, or null FK plus snapshots if unmapped. Reconciliation proves the predicate, not just FK existence (`04-MIGRATION-CUTOVER.md` section 6.3).
- Active source auto-claim rule: remain active only when the source assignee maps to a target StaffUser that is active and scope-eligible for the rule library (same-library staff/admin or active super-admin). Otherwise import the rule as inactive historical configuration, never substitute a different assignee, retain the FK when a mapping exists, allow null assignee only for an inactive rule whose source assignee cannot be mapped, preserve historical request rule references where possible, and report/reconcile every normalization.
- Pickup migration preserves the current stored `PreferredPickupBranchId`/`PreferredPickupBranchName`; after import they remain mutable only through the dedicated validated pickup-preference workflow rather than through generic request editing.
- Historically referenced nonparticipating library: create/retain inactive Organization row using available stored metadata so history/FKs survive.
- Legacy identifier status mapping is exhaustive: null/blank -> null; `pending`/`not_found`/`skipped_no_isbn`/`error_max_retries` preserve; `found` requires supporting BIB ID or blocks; `error` with blank identifier -> `skipped_no_isbn`; `error` with identifier blocks; `found_in_polaris` requires supporting BIB ID to normalize to `found`, otherwise blocks; unknown values block. Valid imported found states ensure the canonical identifier-found tag. Reconcile source/target counts and inconsistent-found blockers.
- Material format: apply known deterministic mappings; unresolved referenced format blocks migration rather than creating a synthetic historical format.
- Current library format with same code as system format becomes a library override; library-only code becomes library custom format.
- Event type unknown: map to `legacy` + original type metadata.
- Actor type unknown: block migration.
- Historical staff event: do not infer StaffUser FK from actor display name.
- Existing deletion-audit records transform into the reduced target audit and intentionally discard extra old PII/freeform fields.
- Retained BIB protection uses the existing legacy-event marker and exhaustive normalized source evidence in migration section 6.10: current hold_placed; all five hold-terminal reasons (hold_completed, hold_not_picked_up, hold_unclaimed, hold_cancelled, hold_expired); dedicated placement events; ordinary status_changed -> hold_placed adoption and other explicit transition/terminal evidence. Missing best-effort events do not negate other proof. Preserve known/null historical BIB and sorted deterministic provenance/counts; report/block ambiguous evidence or conflicting BIBs without over-classifying closed suggestions. Never fabricate a HoldPlacementOperation/provider success/HoldRequestID. Historical BIB protection is not current terminal-hold correlation.
- Migrated OutstandingTimeout defaults/library overrides and rejection-template mapping retain suggestion creation-age semantics, not approved-purchase expiry; include behavioral as well as effective-value reconciliation.
- Existing system-generated notes + event duplication is retained.
- Branding files are exported from PocketBase file storage and imported into SQL Branding rows.

## 29. Production deployment

- Deployment is a small explicit idempotent PowerShell script run locally on the **new ASAP web server**.
- IIS/site/app-pool/service-account creation is a one-time server prerequisite; normal deployment assumes infrastructure exists.
- Production SQL database and permissions are provisioned/validated ahead of cutover.
- Deployment takes the path of an already-present local release ZIP. It does not authenticate to/download from GitHub itself.
- Release bundle is immutable and contains app publish, DACPAC, deploy scripts, and manifest/checksums/SchemaVersion/**DACPAC SHA-256** metadata; migration is a separate same-tag artifact.
- Validate bundle/config/preconditions before mutating production.
- Classify whether this release will mutate production SQL: changed application DACPAC, required Hangfire/dependency schema update, or other explicit DB change all use the database-changing path. Unchanged DACPAC alone never proves file-only. A genuinely file-only release performs no SQL backup, schema work, or DeploymentState update; installed manifest records file identity.
- A changed expected SchemaVersion with an unchanged recorded DACPAC hash is an inconsistent artifact/deployment state and blocks deployment.
- Database-changing deployment stops/quiesces relevant app/workers, verifies a successful recoverable backup of each existing affected database, applies application/dependency DDL in validated artifact order, validates both schema compatibility contracts, records successful DB state, then replaces files/starts/checks health. Hangfire remains outside the application DACPAC and normal runtime cannot auto-upgrade its schema.
- First migration cutover has an explicit preparation mode that can deploy DACPAC/files while leaving IIS stopped until import/reconciliation succeeds.
- SQL backup destination is a configured directory local to the SQL Server; SQL Server service identity writes it. Deployment operator issues and verifies backup.
- App files deploy in place while app pool stopped; preserve timestamped previous file copy/location for rollback/reference.
- Do not automatically purge old app-file snapshots or deployment-created DB backups initially; report their locations for operations housekeeping.
- If application/dependency DDL fails before file replacement, old files remain untouched and workers stay stopped. Explicit restart of old/new code requires proof of compatibility with both actual resulting schemas; do not assume SchemaVersion or a stale successful-deployment record is sufficient.
- If readiness fails after a schema-changing deployment: leave new version available for diagnosis/repair-forward; do not automatically restore old DB/app.
- Explicit code rollback requires application and dependency compatibility without silently redeploying an older DACPAC or dependency schema; incompatible restart/downgrade is blocked.
- Ordinary deployments may be scheduled off-hours; no global patron maintenance-mode feature is required.

## 30. Production/nonproduction cutover topology

- Permanent nonproduction remains on PocketBase throughout the implementation branch; do not deploy incomplete .NET milestones there.
- After port review completes: merge to `main`, create intended production version tag, and build immutable release + migration artifacts from that tag.
- The **exact tagged artifacts** intended for production must undergo the permanent-nonproduction cutover rehearsal.
- Permanent nonproduction rehearsal migrates its accumulated nonproduction PocketBase database using the same export/import/reconciliation tooling and deploys to its permanent IIS/SQL environment.
- If rehearsal finds a blocking defect, fix on `main`, create a new tag/artifact, and repeat rehearsal; never promote an un-rehearsed rebuild.
- If authoritative PocketBase production needs a critical fix after the .NET merge but before cutover, use a temporary branch/tag from the exact last deployed PocketBase commit and the normal emergency process; immediately port the equivalent behavior into .NET `main`, create/rehearse a replacement exact artifact, and update migration contracts/tooling plus repeat the full rehearsal when schema, stored-data semantics, migration input, or migration assumptions change. Do not create a permanent PocketBase branch; temporary hotfix branches may be removed after successful cutover.
- No production data is copied into permanent nonproduction; it uses its existing/synthetic/test history.
- Permanent nonproduction remains internet-accessible intentionally for testing/demo and shows a persistent external-config-driven nonproduction marker in patron/staff UI.
- Production .NET runs on a different already-existing Windows server from PocketBase and retains the existing public hostname after cutover.
- Fully stage/validate the new production server before cutover using temporary/internal access **and a disposable production-preflight SQL database/config**.
- Before DNS cutover, use a workstation hosts-file override so the real production hostname resolves to the new server and validate TLS, cookies, Entra redirect/tenant+object-ID authorization, CSP/embed behavior, static assets, and deep links under the real URL. Then stop IIS, restore the final config, destroy the disposable preflight DB, and recreate/reset the final target to fresh migration state; do not start against it before import.
- First production data cutover prioritizes simple/safe full offline maintenance window over delta synchronization.
- Stop PocketBase writes/jobs; export; transfer; import; reconcile; deploy/start .NET; switch hostname/DNS; validate.
- If migration/reconciliation fails **before .NET accepts production writes**, abort and restart PocketBase as authoritative production, then fix/retry later.
- Once .NET has accepted production writes, it becomes authoritative and recovery is repair-forward rather than reverting to PocketBase.
- After .NET accepts production writes, keep the retired production PocketBase deployment stopped/offline and never start it as-is. Retain executable/data/config/backup material for about 30 days only for forensic/reference use and prefer direct database/file inspection. If execution is genuinely necessary, use a separate isolated copy with outbound Polaris/email access blocked and every recurring production job disabled before startup. It must never be parallel read/write or fallback production.
- Retain the final PocketBase backup/tag as the durable historical reference per operations policy; the permanent tag must point to the exact PocketBase commit frozen at the successful cutover, not an earlier pre-merge candidate.

## 31. Data protection and filesystem operations

- Use normal ASP.NET Core Data Protection for cookie/auth cryptography.
- Baseline persistent key directory: `C:\ProgramData\clc-asap\DataProtection-Keys` on each IIS server/environment; production/nonproduction key rings are separate and ACLed, with persisted keys protected by separate environment-specific X.509 key-encryption certificates. The certificate/private key must be recoverable and importable on a replacement IIS host, with narrow private-key ACLs restored there. The ring protects cookies/antiforgery and persisted SQL integration-secret ciphertext, so the ring plus certificate/private key must be backed up/recovered with the database and old keys/certificate material must not be purged while ciphertext may reference them.
- Store local application logs under a dedicated protected ASAP data/log directory; canonical example `C:\ProgramData\clc-asap\Logs`.
- External environment JSON lives outside the deployed application directory with tight ACLs and is never overwritten by deploy.

## 32. Patron embed/security boundary

- `/patron` remains a dynamic HTML endpoint so response-specific CSP `frame-ancestors` can be emitted; its JS/CSS/images remain ordinary static assets.
- Patron embed allowed origins remain a single system-wide super-admin-controlled trust list.
- Library admins cannot alter the CSP trust boundary.

## 33. Synthetic data

- Provide an explicit opt-in synthetic/demo seed/reset tool near the end of the port, after core functionality.
- Never run synthetic seeding automatically at application startup.
- Guard against production execution.
- Reuse seeded scenarios for demos/manual validation/tests where useful without making production runtime depend on seed tooling.

## 34. Canonical implementation order

The detailed plan may split supporting foundations as needed, but the functional order is:

1. solution/runtime/DACPAC/config/testing foundation;
2. patron login -> title request submission -> SQL persistence, including triggered email/outbox behavior;
3. staff Entra sign-in -> scoped request queues -> view/edit/claim/process core TitleRequests, including email/events;
4. AdditionalCopy workflow as its own slice;
5. administration/configuration: StaffUser, organizations, domain-specific settings/inheritance/whole-set semantics, material formats/auto-claim, branding, email settings/templates, admin audit;
6. remaining background workflows/Hangfire/Polaris reconciliation paths;
7. analytics SQL-side implementation;
8. migration completion, deployment/health/monitoring, CI/release-validation integration;
9. end-of-port synthetic seed/reset tooling;
10. legacy PocketBase removal/documentation rewrite/final whole-app review;
11. record current PocketBase production commit -> merge port -> create production version tag -> exact-artifact permanent-nonproduction rehearsal -> apply/port/rehearse any emergency PocketBase hotfix -> production cutover -> tag the exact frozen final PocketBase commit.

Migration export/import/reconciliation support is implemented continuously alongside the data-owning slices, not only in step 8.

## 35. Release validation

- Normal PR/main deterministic tests and real-SQL tests are required.
- Live release-validation gate covers Polaris/PAPI, not Postmark.
- Live Polaris validation is tied to exact tagged release workflow/GitHub Environment and may be explicitly overridden only for legitimate infrastructure conditions with recorded reason.
- Use a dedicated disposable/controlled Polaris target and fixtures; exercise the relevant PAPI method gamut through ASAP orchestration where practical.
- Nightly/disposable test state may require baseline/refresh rules; dirty same-day baseline should fail/wait unless explicitly overridden.
- Do not turn this live release gate into a prerequisite for ordinary local/PR development.

## 36. Documentation/archive policy

- Rewrite canonical docs around the .NET application before merge.
- Remove obsolete PocketBase setup/operations docs from canonical `main` rather than keeping a permanent duplicate `docs/legacy-pocketbase` tree.
- Keep only migration-relevant legacy material that still helps operate/reproduce the cutover.
- Use final PocketBase Git tag/history as the archival source.

## 37. Explicitly superseded/rejected choices

The following earlier possibilities are **not** part of the target design:

- Plaintext reusable Polaris/Postmark secrets in SQL — superseded by Data Protection ciphertext plus write-only/masked/audited handling.
- Development-only interactive auth bypass for normal F5 — superseded by real Entra/OIDC in Development; bypass exists only in `Testing` for automated tests.
- Persisting per-staff Polaris user identity for PAPI attribution — intentionally dropped initially; PAPI uses configured system user identity and ASAP audit records the Entra actor.
- General persistent Polaris organization/reference mirror — replaced by minimal Organization persistence plus live/short-memory reference data.
- `enabledForPatrons` as a narrow library flag — replaced by broad Organization `IsActive` participation state.
- Generic migration framework alongside DACPAC — rejected; future exceptional data transformations are explicit one-off scripts.
- React/Blazor/Razor/frontend rewrite — rejected for initial port.
- Server-side grid paging/search from day one — deferred pending demonstrated need.
- Third-party CDN assets — replaced by vendored exact current browser assets.
- Temporary PocketBase-vs-.NET parity harness — rejected.
- Automatic deployment from `main` to permanent nonproduction — deferred; deployment remains manual initially.
- Separate Entra registrations per environment — deferred; same registration initially.
- Separate Postmark servers/tokens per environment — deferred; same initially with nonprod recipient-domain safety.
- Shared production/nonproduction IIS runtime service account — superseded by separate least-privilege environment identities; gMSA itself remains deferred.
- Runtime `db_owner` for operational simplicity — superseded by purpose-built least-privilege application/Hangfire runtime permissions and operator-owned schema/deployment work.
- Global patron maintenance-mode/staged reopening for ordinary deployments — superseded by off-hours deployment and immediate normal service once healthy.
- Minimal-downtime/delta synchronization for first production migration — rejected in favor of one simple offline cutover window.
- Running the whole migration on the new server by copying raw PocketBase data there — refined to export on old PocketBase server and import/reconcile on new ASAP server.
- Running the whole migration directly on SQL Server or an operator workstation — rejected.
- Starting the retired production PocketBase app as-is after .NET accepts writes — rejected; retention is forensic/reference only, with direct inspection preferred and any necessary execution limited to an isolated copy with outbound integrations blocked and recurring jobs disabled.
- Building/rehearsing a pre-tag artifact then rebuilding for production — rejected; production uses the exact tagged artifact that passed permanent-nonproduction rehearsal.
- Treating administration/configuration as the third functional slice — superseded by the later ordering decision that AdditionalCopy immediately follows core staff workflow, then administration.
- Repo rename during/before cutover — deferred until first .NET production deployment is validated.
- Node/npm in app build/publish path — rejected; development/CI tests only.

## 38. Deferred items are not port blockers

See `09-DEFERRED-FOLLOWUPS.md`. In particular, do not expand the initial port solely to add separate Entra app registrations/Postmark servers, gMSA, an external enterprise secret vault, automatic CI/CD deployment, frontend modernization, server-side queue paging, richer operational retention automation, or optional patron allowlisting unless a concrete blocker is discovered. **Separate production and permanent-nonproduction IIS runtime service identities are already required from day one and are not deferred.**
