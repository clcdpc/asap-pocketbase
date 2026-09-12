# Multi-model Codex Task: Port ASAP from PocketBase to .NET 10

## Mission

Port `clcdpc/asap-pocketbase` from PocketBase/SQLite/Goja to the .NET architecture defined by this documentation pack. This is one large branch/PR delivered in complete vertical slices. Preserve existing ASAP behavior and frontend UX by default; change behavior only when the specification explicitly requires it or when a concrete technical benefit justifies a documented deviation.

The port is not an opportunity for a general rewrite beyond the backend/platform boundaries described here.

## Source baseline

Before work begins:

1. Fetch the current `main` and compare it with the pack's reference SHA `150b30b776565194260cc327eeeffdfb46475e81`.
2. If `main` has moved, inspect every intervening change and update the slice packets/behavior map so urgent/current PocketBase fixes are not lost. The pinned baseline contains an unrelated `clc-carousel-manual-import-example/` subtree; explicitly exclude it from ASAP behavior/dependency/migration analysis and remove it from the port branch rather than porting it.
3. Record the starting PocketBase SHA and keep tracking the exact commit deployed to PocketBase production. Do not prematurely create a tag called "final" while emergency fixes can still land; after successful .NET production cutover, create/verify the permanent final-PocketBase tag at the exact PocketBase commit that was frozen for that cutover.
4. Create a dedicated port branch (use a clear name such as `codex/port-asap-to-dotnet` if no branch has already been established).
5. Open a draft PR immediately and use it for the entire port.
6. Freeze ordinary PocketBase feature work. If an urgent PocketBase production fix lands during the port, bring the behavior into the port branch immediately.

## Required model roles

### GPT-6 Astra Max - orchestrator

You own the full plan, integration, slice boundaries, progression, context management, acceptance verification, commits, and PR state.

For each slice:

- prepare a focused implementation packet rather than dumping the entire pack;
- include objective, acceptance criteria, relevant global invariants, relevant prior-slice contracts, exact current PocketBase files/behavior to preserve, migration impact, and relevant deferred/non-goal warnings;
- create a fresh GPT-5.6 Sol High/XHigh implementation context;
- create a fresh GPT-5.6 Terra High reviewer context;
- keep the same Terra context through that slice's review/fix/re-review sequence;
- create the milestone commit only after tests and review gate are satisfied.

Resolve ordinary ambiguity yourself from this pack, repository, existing behavior, and tests. Do **not** ask the user for routine implementation choices. Ask only if a genuinely material product/security/migration decision cannot be resolved from the pack/current system.

Astra may delegate narrowly bounded mechanical or support work to another suitable model, including Luna. Such delegation must not replace Sol's required implementation ownership for a slice or weaken the independent Terra review boundary.

### GPT-6 Astra Max - on-demand advisor

When there is material uncertainty, architectural tradeoff, or a contemplated deviation from this pack, obtain a fresh independent Astra Max consultation. Do not use the advisor ceremonially on every slice.

### GPT-5.6 Sol High/XHigh - primary implementation

Use a fresh Sol implementation context per slice. Use High reasoning by default. Astra may escalate the slice context to XHigh when the slice or confirmed review findings involve materially difficult cross-cutting correctness, migration, concurrency, security, external-operation recovery, or deployment work; do not require XHigh ceremonially for every slice. Implement the entire focused slice, including application code, DACPAC, migration mapping, tests, frontend integration, and docs impacted by that slice.

When Terra reports confirmed findings, Sol fixes them. Terra does not implement its own review feedback.

### GPT-5.6 Terra High - independent review

Review the completed slice adversarially, including surrounding code, callers, tests, invariants, authorization/scope, data consistency, concurrency, external failure paths, migration, and unintended coupling.

## Binding architecture

Read the entire pack before orchestration begins. Especially treat the following as non-negotiable unless a blocking technical fact proves otherwise:

- .NET 10 / ASP.NET Core 10 / SQL Server 2022 compatibility 160 / IIS.
- Simple solution: `Asap.Web`, `Asap.Database`, `Asap.Migration`, `Asap.Tests`.
- Feature-oriented code in one web project; EF Core direct feature services; Dapper/ADO only targeted heavy paths; no repositories/MediatR/layer explosion.
- DACPAC schema source of truth; `[asap]`; Hangfire `[HangFire]` outside DACPAC and provisioned/upgraded by the deployment operator so the runtime identity needs no schema DDL rights.
- Exact application SchemaVersion startup contract plus independent DACPAC hash and actual Hangfire/dependency schema change/compatibility detection. Any application or dependency DDL against an existing production database requires stopped workers and a verified backup before DDL/files; only proven file-only deployment skips SQL backup and DB writes. Do not give runtime schema rights or move Hangfire objects into the DACPAC.
- Entra OIDC + local StaffUser allowlist/roles; durable identity is (`tid`,`oid`) only. Authentication tickets retain the sign-in tuple and every authenticated request requires exact equality with the current StaffUser binding and membership in the currently loaded AllowedTenantIds, so explicit rebind invalidates old cookies. `UserPrincipalName`/`DisplayName` are refreshable readable Entra metadata; `NotificationEmail` is separate app-owned nullable contact data and never refreshes from sign-in; real Entra for Development/F5.
- Keep staff-account lifecycle separate from library participation: staff/admin reference one non-system Organization even when it is inactive; authorization requires both StaffUser and Organization active; library deactivation must not rewrite `StaffUser.IsActive`; library reactivation restores eligibility for already-active staff; explicit StaffUser reactivation while its library is inactive returns a conflict. Role/organization mutations that contract or move authorization scope are atomic lifecycle operations: deactivate out-of-scope active `FormatAutoClaimRule` rows, clear out-of-scope open TitleRequest claims with events, clear open AdditionalCopy claims with system Notes/admin-audit counts, and preserve closed history. StaffUser is the common serialization point with claim/rule writers, which must lock/re-read current target eligibility before commit. Promotion to super-admin broadens scope and needs no cleanup.
- Preserve every current StaffUser preference (`weekly_action_summary_enabled`, `weekly_action_summary_email`, `purchase_reminder_default`, `additional_copy_reminder_default`, `default_mine_unclaimed_filter`) through SQL, migration, profile DTO/API, frontend behavior, and tests.
- Enforce the common current staff-eligibility predicate from `01-PORTING-SPEC.md` section 7.6 on every authenticated staff request, sensitive-mail delivery, usable-super-admin count, and candidate external configuration. Serialize any StaffUser mutation reducing usable-super-admin eligibility using `ASAP:ActiveSuperAdminInvariant` before row locks and re-read; return 409 if zero would remain. Removed-tenant cookies must fail next use after restart with the same keys; configuration cannot silently strand the installation without a usable administrator.
- Use one SQL lock order whenever multiple row categories are needed: `Organization -> StaffUser -> TitleRequest/AdditionalCopyRequest -> dependent claim/rule/operation rows`, with stable key ordering inside a category. Use short `UPDLOCK,HOLDLOCK`/equivalent transactions and never hold them across Entra/Polaris/Postmark calls.
- Testing-only auth handler registered only in `Testing`.
- Enforce `01-PORTING-SPEC.md` section 14's one recipient-domain predicate through the existing outbox: `Environment.IsNonProduction` independently governs all Postmark recipients, with case-insensitive exact matching, explicit-only subdomains, missing/empty-list suppression, malformed-config rejection, terminal `recipient_domain_not_allowed` suppression without business rollback, and preserved deterministic idempotency. Production does not apply the restriction; testing authentication remains controlled only by `ASPNETCORE_ENVIRONMENT=Testing`. Complete `06-TESTING-CI.md` section 4.1 and `08-RELEASE-VALIDATION-NOTES.md` section 6 without requiring a live Postmark send.
- Patron opaque 1-hour bearer tokens with SHA-256 token hash in SQL; no persistent PatronUser profile. Every patron-authenticated request must also require the session's effective Organization to remain active. Final session insertion serializes against library deactivation on that Organization row; deactivation transactionally revokes matching sessions, and reactivation never resurrects them.
- `Clc.Polaris.Api` and `Clc.Postmark.Api` own protocol clients.
- Polaris system/application credentials for all initial PAPI calls; no per-staff Polaris identity machinery.
- Reusable Polaris/Postmark secrets are Data Protection ciphertext in SQL; production/nonproduction use separate key rings and separate least-privilege runtime service accounts, never `db_owner`.
- `13-SETTINGS-SCOPE-INVENTORY.md` is normative for configuration scope/storage/inheritance/reset/migration. Do not create a catch-all `Settings` or EAV table. Use system-only `SystemSettings`/`PolarisSettings`, field-inheritable `WorkflowSettings`/`PatronSettings`/`EmailSettings`, the specified relational whole-set/provider/custom-field/custom-field-rule tables, and the specialized branding/template/material-format/auto-claim models. Organization `1` is the real system/default scope.
- **Reset inherited overrides** must preserve library-owned custom fields, custom formats, custom rejection templates, and auto-claim rules/history.
- Durable SQL email outbox + Hangfire with exactly `pending`/`sending`/`sent`/`failed`/`suppressed`: every expired `sending` lease is potentially transport-ambiguous; the provider call starts within 30 seconds of claim and has a 30-second complete-operation timeout; the lease is two minutes; reclaim/retry begins only after expiry; and final writes compare `Status`, expected `LeaseId`, and post-claim rowversion/equivalent ownership so late workers cannot overwrite newer attempts. Preserve explicit at-least-once semantics with no exactly-once claim, `failed` manual retry with payload retained, payload purge only for terminal `sent`/`suppressed` after 90 days, and missing enqueue-time notification configuration represented non-fatally rather than rolling back business state. Authorization-sensitive staff mail stores recipient/scope/original Entra tuple plus `RecipientAddressKind=notification_email|weekly_summary` and revalidates current binding/allowed-tenant/authorization plus the kind-specific effective address against `ToAddress` immediately before every send/retry; stale authorization/address becomes terminal suppression.
- Inactive organizations gate participation-dependent business automation and library-scoped Run Now work. Immediately before a participation-dependent local result or external-operation acquisition, lock/re-read the owning Organization first and require it active; no new identifier/promotion/hold/fulfillment/timeout mutation or ordinary library summary begins after deactivation wins that serialization point. Already-acquired hold recovery continues even while the Organization is inactive; this cannot authorize a new operation. System/infrastructure jobs continue; immutable committed business-event mail may drain, while authorization-sensitive staff mail suppresses if current scope is lost. Weekly summaries follow recipient authorization scope: staff/admin own active library only; super-admin active consortium-wide.
- Staff recipient contract: `NotificationEmail` is the nullable primary ordinary-notification destination; `WeeklyActionSummaryEmail` is a weekly-summary-only optional override/fallback. Persist which rule applies on each authorization-sensitive outbox row rather than infer it from its business key. Reject legacy `@staff.asap.local` placeholders, preserve migration precedence, permit authorized Staff Access to clear the primary email, never repopulate it from Entra sign-in, and emit migration reconciliation deltas for newly eligible weekly-summary users and every ordinary-recipient change. The target fallback normalization is deliberate; migration still starts with an empty outbox.
- Database-enforce idempotent outbox creation with a filtered unique index on non-null `EmailOutbox.BusinessKey`; deterministic keys are globally namespaced, ordinary weekly staff summaries are idempotent per recipient/reporting period, and duplicate-key races resolve as successful idempotent enqueue attempts. Preserve explicit forced weekly-summary resend by generating one durable `ManualRunId` per accepted forced invocation and using force-run recipient keys; retries of that same run reuse the ID, while a later force is a new business event.
- Hold placement is DB-guarded by one active `HoldPlacementOperation` per TitleRequest across every entry point before any Polaris mutation. Acquisition follows Organization -> TitleRequest locking and becomes a durable mutation barrier: status/close/reopen, delete, identifier, BIB, AutoHold, and recorded pickup changes cannot commit behind an incomplete operation. The pickup action checks the barrier before Polaris; hold placement re-resolves live pickup immediately before its call. Deactivation after successful acquisition does not cancel the already-started operation, which must reconcile safely.
- Apply the stage-aware identifier matrix in `07-API-FRONTEND-COMPATIBILITY.md` section 14.2: only legal pre-placement changes invalidate old result/retry/error/check/tag/BIB authority; changed/cleared placed/closed identifiers are rejected, unchanged normalized inputs are no-ops, combined BIB edits cannot bypass protection, and successful/legacy placed BIB remains the title authority for positive checkout through reopening; terminal unclaimed/cancelled/expired closure additionally requires the particular tracked final HoldRequestID under porting-spec section 9.2. A retained historical BIB marker never supplies that identity. SQL still rejects canonical found without a BIB.
- Explicit C# workflow transitions/current-state truth; append-only request events; no workflow engine/event sourcing.
- Stable relational material formats/rules/tags as specified; built-in material-format field behavior uses typed columns, custom-field per-format rules are relational, and there is no competing legacy `patronFormatRules` JSON source.
- Preserve vanilla frontend and Grid.js; exact existing browser library versions vendored locally; no Node/npm in app build/publish/runtime.
- Preserve internal API contracts/routes by default, with explicit rowversion/HTTP/auth changes.
- Real SQL tests, Playwright critical journeys, axe-core serious/critical gate.
- Migration direct from stopped PocketBase SQLite/file storage -> normalized JSON package -> SQL import/reconciliation; migration ZIP is self-contained `win-x64`, active StaffUsers receive explicit operator-supplied Entra tenant/object-ID mappings rather than UPN-derived identity, inactive organizations preserve staff/history FKs without forcing StaffUser deactivation, every populated source setting must map through `13-SETTINGS-SCOPE-INVENTORY.md` or an explicit intentional-drop rule, legacy SMTP transport credentials are never converted to Postmark credentials, the target Postmark token is supplied separately through secure target-only import input and persisted only as protected ciphertext, staff recipient changes are reported old-versus-target, and every legal legacy `isbnCheckStatus` is handled by the exhaustive conditional map: both `found` and `found_in_polaris` require supporting BIB state or block/report. After StaffUser import, hard-gate on at least one active super-admin with a valid allowed-tenant durable Entra binding; if none exists, `Asap.Migration` explicitly provisions/promotes the configured bootstrap identity while the app remains stopped, reports it, and re-runs the gate. Never rely on ordinary startup bootstrap after StaffUser rows exist. Source-active auto-claim rules remain active only when the migrated assignee exists, is active, and is scope-eligible; otherwise preserve them inactive without substitution and report the normalization, using nullable inactive-rule `StaffUserId` only when the source assignee no longer maps. `PreferredPickupBranchId`/`PreferredPickupBranchName` are current request pickup fields mutable only through the dedicated validated pickup workflow.
- Migration freezes two distinct parity artifacts: `effective-legacy-runtime-config.json` only for system/global SQL-bound values that require DB/environment/code fallback resolution, and `effective-legacy-operational-config.json` for legacy cron schedules plus global/timeout/queue-specific processing limits. Reconcile both before cutover; library-scoped SQL configuration stays in the domain export files. Explicitly test the persisted-blank/environment-derived Staff URL case and its `ASAP_STAFF_URL`/`ASAP_PUBLIC_URL`/initialization-helper `ASAP_BASE_URL` behavior.
- Hard reconciliation gate: no unexplained discrepancies.
- One full offline production migration window; no delta sync.
- Permanent nonproduction stays PocketBase until the .NET implementation is complete and a merged/tagged release candidate is ready.
- Exact tagged artifact that passes the final permanent-nonproduction rehearsal is the production artifact.

## Binding closure-remediation implementation requirements

Implement the following within the existing slices and models, not as another architecture review:

1. Complete `HoldPlacementOperation` exactly as `01-PORTING-SPEC.md` section 9.1: owner token/epoch and bounded lease, durable one-way create/reply markers, persisted provider GUID/qualifiers before reply, evidence-classified terminal outcomes, observation-only uncertainty, and super-admin operator resolution. Recovery runs before business phases and includes already-acquired inactive-library work; no new inactive-library acquisition or unsupported replay.
2. Enforce AdditionalCopy retained-claim validation under Organization -> StaffUser -> task locking on reopen and inherited-claim creation. Clear invalid effective fields, preserve Notes history, and do not auto-assign. Import both request types with map-then-current-eligibility checks, all conversion reasons/counts, valid inactive-organization relationships, and preserved closed history (`04-MIGRATION-CUTOVER.md` section 6.3).
3. Use the same current identity/binding/tenant/activity/scope predicate everywhere named above; persist the sensitive-mail recipient tuple and suppress stale authorization. A re-added tenant does not undo independent revocation/rebinding/scope changes. No Graph/session-store/key-rotation solution.
4. Implement the finite `QueueProgress` cycles in section 23.1 of the porting spec: per queue/scope scalar keyset, fixed ID watermark, post-durable-outcome checkpoints, wrap/restart/deletion/backdated insertion/failure semantics, and the existing non-overlap guard. Keep sequential PageSize/MaxPerRun limits, eight configured queue keys/seven schedules, and a separate bounded HoldRecovery phase reusing HoldPlacement limits. Later phases must enforce only applicable status-specific timeout guards from porting-spec section 23.2, never an OutstandingTimeout expiry for outstanding_purchase.
5. Use the database-changing classifier and failure/restart rules from `05-DEPLOYMENT-OPERATIONS.md` sections 9-10, including Hangfire-only DDL and compatibility-aware rollback.

6. Implement OutstandingTimeout as unreviewed-suggestion creation-age auto-rejection, with strict injected-clock boundary, ordinary participation/version/barrier checks, and the configured timeout rejection-email/template behavior. An old outstanding_purchase awaiting BIB is not expired by this setting. Preserve all four explicit family predicates and existing field/queue names; fair scan ordering cannot redefine age.
7. Apply porting-spec section 9.2 to new placement, runtime existing-hold adoption, and fulfillment. Capture authoritative final HoldRequestID on the existing HoldPlacementOperation when available, separately from RequestGUID/reply qualifiers. Only the tracked hold plus expected BIB/patron can authorize unclaimed/cancelled/expired closure; historical same-BIB rows, ambiguous identity, and provider failures preserve state with diagnostics. Preserve positive title-level checkout and normal manual/timeout closure. Do not invent a final ID, replay a successful mutation to obtain one, or add a second journal.
8. Implement migration section 6.10's exhaustive normalized placement evidence, including all five hold-terminal close reasons and status_changed -> hold_placed adoption history; preserve deterministic provenance and known/null BIB in the existing legacy marker without fabricated operations/provider success/IDs. Protect across reopen, commit, then separate identifier/BIB edit; API capabilities and backend agree. Reconcile explicit protection/evidence/ambiguity counts and identical equivalent fresh-target imports.

All R1-R7 cases in `06-TESTING-CI.md` section 10.1 and F1-F3 cases in section 10.2 are blocking. `14-REMEDIATION-AUDIT.md` maps each finding to contracts and tests. Preserve prior decisions and the pinned SHA; do not add unrelated abstractions, product changes, or deferred hardening.

## Slice review/fix rule

For **every** vertical slice:

1. Astra prepares the focused slice packet.
2. A fresh Sol High/XHigh context implements the slice.
3. Run all relevant tests and verify the slice end-to-end.
4. A fresh Terra High context performs Pass 1 over the full slice.
5. Sol fixes confirmed substantive findings.
6. The same Terra context performs Pass 2 over the full slice, including regression from fixes.
7. If Pass 2 finds a new substantive issue, Sol fixes and the same Terra context performs Pass 3. If Pass 2 is clean, stop.
8. After Pass 3, record only non-blocking leftovers in deferred follow-ups.
9. Never progress with a known correctness, security, data-integrity, migration, or material-regression defect merely because the pass cap was reached.
10. Commit the completed slice with a coherent milestone commit only after the slice gate passes.

## Required implementation slice sequence

Follow `02-IMPLEMENTATION-PLAN.md` as the canonical sequence:

- **Slice 0:** freeze/branch/skeleton/engineering baseline, including removal/exclusion of the unrelated carousel-example subtree and DeploymentState/DACPAC-hash foundation.
- **Slice 1:** patron login -> title request submission.
- **Slice 2:** core staff Entra + request workflow.
- **Slice 3:** additional-copy workflow.
- **Slice 4:** administration/configuration.
- **Slice 5:** background workflows + complete email operations.
- **Slice 6:** analytics.
- **Slice 7:** migration hardening + legacy-link compatibility.
- **Slice 8:** deployment/health/monitoring/release artifacts.
- **Slice 9:** CI/Playwright/accessibility/release validation integration.
- **Slice 10:** end-of-port explicit synthetic seed/reset tooling.
- **Slice 11:** remove legacy PocketBase implementation, rewrite canonical docs and repository-level `AGENTS.md`, and run final review. Before the port PR merges, remove obsolete PocketBase-only agent instructions, retain/adapt general simplicity/scope, settings-scope, DOM-safety, accessibility, and behavioral-testing guidance, and make `AGENTS.md` describe the completed .NET repository, including its DACPAC/EF Core/Dapper/ADO.NET/SQL rules, rather than a planned future port.

Migration export/import/reconciliation code is developed **alongside** every data-owning slice; the migration-hardening slice consolidates and productionizes it rather than starting it from scratch.

## Per-slice completion invariant

At each milestone the .NET branch must build, tests must be green, and all features already claimed as implemented must work end-to-end. Do not leave deliberately broken placeholders. Features scheduled for later may be absent/disabled.

## Commit policy

Commit by completed vertical slice/meaningful milestone. Keep coherent slice changes together. Do not manufacture tiny commits for every file or reviewer fix; do not let unrelated multi-slice work accumulate into one giant final commit.

## Final whole-app review

After legacy cleanup and all deterministic tests are green, use fresh Terra High whole-app review passes. Vary focus across correctness/integration, failure paths, migration/data, concurrency, APIs/contracts, security, performance/resources, maintainability/coupling, deployment/recovery, and test adequacy.

Stop after **3 consecutive passes with no new substantive finding**, maximum **6 passes**. Maintain a running confirmed-finding set so later passes do not repeat old issues. Fix all blocking findings regardless of pass count.

## Merge, tag, rehearsal, production

The PR must include everything required for real cutover:

- complete app;
- DACPAC;
- migration console + transforms/reconciliation;
- deployment PowerShell;
- health/diagnostics;
- PRTG sensor;
- CI/Playwright/accessibility;
- external config example/docs;
- complete recurring-job/processing-limit contract from the pack: one non-overlapping hourly ordered workflow orchestrator; one canonical five-minute identifier processor with explicit Found/DefinitiveNotFound/TransientFailure/OperationalFailure classification, five scheduled transient attempts/no extra backoff, reset rules and manual terminal recovery; exhaustive migration handling for every legacy ISBN status; Sunday 20:00 weekly summaries generated per recipient authorization scope plus explicit forced runs with durable `ManualRunId`; inactive-library participation gating for business jobs/manual runs; continued system/infrastructure jobs and immutable business-event outbox draining with authorization-sensitive staff suppression; outbox/cleanup jobs; and global/timeout/queue-specific processing-limit precedence, all using the configured business timezone;
- release artifact/manifest generation;
- migration and deployment/cutover runbooks.

Do not merge a "code complete, ops later" port.

After the final whole-app review:

1. Record the exact commit currently deployed to PocketBase production and confirm it has no urgent fix missing from the port.
2. Merge the complete port to `main`.
3. Create the intended production version tag from the merged commit.
4. Build immutable app/deployment and self-contained `win-x64` migration artifacts from that exact tag.
5. Perform the full permanent-nonproduction PocketBase -> SQL migration and IIS deployment rehearsal using those exact artifacts, including the explicit staff Entra identity map and fresh-target checks.
6. Exercise critical workflows, scheduled Hangfire jobs, migration reconciliation, health/diagnostics, Entra, Polaris, and safe nonproduction Postmark behavior.
7. If a blocking defect is found, fix it on `main` through the normal reviewed process, create a **new tag/artifact**, and repeat the exact-artifact rehearsal.
8. Only an artifact that passes this rehearsal is eligible for production.
9. Stage the same artifact on production; perform real-hostname/Entra preflight only against a disposable production-preflight SQL database, destroy it afterward, then execute the documented offline cutover with the final target kept fresh and the app pool stopped until migration/reconciliation succeeds.
10. At the successful cutover, record the exact frozen PocketBase commit and create/verify the permanent final-PocketBase tag at that commit. Once .NET accepts production writes, never start the retired production PocketBase deployment as-is: retain it only for forensic/reference use, prefer direct database/file inspection, and permit execution only from an isolated copy with outbound Polaris/email access blocked and recurring production jobs disabled. It is never parallel read/write or fallback production.

If PocketBase production needs a critical fix after step 2 but before production cutover, branch and/or tag temporarily from its exact last deployed PocketBase commit; do not create a permanent PocketBase branch. Test/deploy through the normal emergency process, immediately port the equivalent behavior into .NET `main`, build a replacement versioned artifact, and repeat the exact-artifact rehearsal. If source schema, stored-data semantics, migration input, or a migration-tool assumption changes, update the migration contract/tooling before the repeated rehearsal. Remove temporary hotfix branches after successful cutover; the permanent final tag must identify the actual last frozen PocketBase commit.

## Scope control

Whenever you discover cleanup/modernization that is not needed for correctness or cutover, add it to `09-DEFERRED-FOLLOWUPS.md` instead of expanding the port. In particular do not opportunistically add frontend frameworks, dependency major upgrades, server-side paging, an external secret-vault product beyond the required Data Protection SQL-secret protection, distributed caching, automatic CD, or generalized migration/workflow frameworks.
