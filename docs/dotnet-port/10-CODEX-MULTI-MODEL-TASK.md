# Multi-model Codex Task: Port ASAP from PocketBase to .NET 10

## Mission

Port `clcdpc/asap-pocketbase` from PocketBase/SQLite/Goja to the .NET architecture defined by this documentation pack. This is one large branch/PR delivered in complete vertical slices. Preserve existing ASAP behavior and frontend UX by default; change behavior only when the specification explicitly requires it or when a concrete technical benefit justifies a documented deviation.

The port is not an opportunity for a general rewrite beyond the backend/platform boundaries described here.

## Source baseline

The existing implementation is on `codex/csharp-port`, draft PR #264. Slices 0-4 are accepted; Slice 4's milestone is `417c72430652a35bc8fc1da549ae270eabc86429` and its exact-SHA remote CI passed. Slice 5 has not started. Verify the current branch/PR and `../implementation/PORT-STATUS.md` before resuming. Preserve historical execution and evidence: Slices 0-3 used the prior Sol-based strategy and Slice 4 used Luna Max with retained contexts and full Terra re-reviews. Do not repeat bootstrap or re-review accepted slices because the policy changed. The execution-policy refinement below applies beginning with Slice 5 and does not authorize its dispatch.

The original baseline procedure remains the source-tracking contract; branch/PR creation below was already completed:

1. Fetch the current `main` and compare it with the pack's reference SHA `150b30b776565194260cc327eeeffdfb46475e81`.
2. If `main` has moved, inspect every intervening change and update the slice packets/behavior map so urgent/current PocketBase fixes are not lost. The pinned baseline contains an unrelated `clc-carousel-manual-import-example/` subtree; explicitly exclude it from ASAP behavior/dependency/migration analysis and remove it from the port branch rather than porting it.
3. Record the starting PocketBase SHA and keep tracking the exact commit deployed to PocketBase production. Do not prematurely create a tag called "final" while emergency fixes can still land; after successful .NET production cutover, create/verify the permanent final-PocketBase tag at the exact PocketBase commit that was frozen for that cutover.
4. Create a dedicated port branch (use a clear name such as `codex/port-asap-to-dotnet` if no branch has already been established).
5. Open a draft PR immediately and use it for the entire port.
6. Freeze ordinary PocketBase feature work. If an urgent PocketBase production fix lands during the port, bring the behavior into the port branch immediately.

## Required model roles

### GPT-6 Astra Max - orchestrator

You own the full plan, integration, slice boundaries, progression, context management, delegation, material design/contract interpretation, escalation decisions, acceptance verification, milestone commits, and PR/status maintenance. Operate as a lean orchestrator using compact objective evidence under the evidence rule below.

For each remaining slice:

- refresh the already-prepared slice packet against accepted prior implementation and prepare a focused implementation context;
- include objective, acceptance criteria, relevant global invariants, relevant prior-slice contracts, exact current PocketBase files/behavior to preserve, migration impact, and relevant deferred/non-goal warnings;
- normally dispatch a fresh GPT-5.6 Luna High implementation context;
- after implementation and all required pre-review gates, dispatch a fresh GPT-5.6 Terra High reviewer context unless the user directed a review-candidate stop under the commit policy below;
- keep the Luna context while efficient and useful; rotate with a concise handoff when the context-management rule below warrants it;
- keep the same Terra context through that slice's review/fix/re-review sequence;
- verify acceptance and create the milestone commit only after all existing tests and review gates are satisfied;
- push the milestone and require actual remote CI success for that exact commit before dispatching the next slice.

Resolve ordinary ambiguity yourself from this pack, repository, existing behavior, and tests. Do **not** ask the user for routine implementation choices. Ask only if a genuinely material product/security/migration decision cannot be resolved from the pack/current system.

Astra Max is Luna's mandatory first escalation point. Astra already owns the complete port contract, cross-slice state, accepted decisions, packet scope, current implementation, progression and acceptance, including whether an issue requires design reasoning. For escalation questions, inspect the authoritative documentation, accepted implementation, pinned PocketBase behavior and relevant tests/evidence; decide which contract governs, narrow the problem, and give Luna a concrete implementation ruling. Resolve the issue directly whenever that evidence is sufficient. Astra owns substantive resolution as well as any decision to seek independent Sol advice.

### GPT-5.6 Luna High - default primary implementation

Normally begin each remaining slice with a fresh Luna High implementation context. Luna High owns the complete ordinary implementation workload, including C#, SQL/DACPAC changes, migration/export/import/reconciliation, frontend integration, tests, ordinary test-failure diagnosis, directly affected documentation, and confirmed Terra fixes as applicable. Context continuity is useful, but identity is not an acceptance invariant; follow the context-management rule below.

Luna implements the existing contract rather than redesigning it. Do not add repositories, MediatR, generalized workflow engines, generalized messaging infrastructure, migration frameworks, layering, speculative hardening, or other machinery the pack does not require.

### GPT-5.6 Luna Max - bounded implementation escalation

Astra may elevate a concrete implementation problem when additional reasoning materially helps: subtle concurrency/serialization, recovery around irreversible external effects, difficult migration reconciliation, complex state-machine correctness, or a confirmed review finding that Luna High cannot resolve confidently. Bound the problem and prefer returning to Luna High once it is resolved. Slice size, SQL, authentication, migration, many tests, or elapsed implementation time alone do not justify Luna Max.

### GPT-5.6 Terra High - independent review

Use a fresh Terra High context per slice after implementation and required tests, then retain it through any required re-review. Pass 1 covers the FULL slice, including correctness/regression, callers and surrounding code, state/data consistency, SQL and migrations, authorization/scope, concurrency/ordering, recovery, external side effects, frontend/API compatibility, test adequacy, and unnecessary complexity/coupling. Terra reports findings only; it does not implement fixes. Confirmed findings return to the current Luna implementation context. Re-review scope follows the slice review/fix rule below.

### Mandatory first escalation: Luna -> Astra Max

Luna always consults Astra Max first for a materially blocking issue, including:

1. The pack, accepted target implementation, pinned source, and tests appear to conflict or fail to determine required behavior.
2. A Terra finding cannot be resolved confidently from the existing contract.
3. Repeated implementation attempts fail because the correct invariant or behavior is unclear, rather than because of an ordinary coding defect.
4. A cross-slice concurrency, recovery, migration, authorization, security, or external-operation question requires interpretation of the broader design.
5. An external provider/API limitation appears to require changing an existing contract.
6. The smallest apparent implementation would require deviation from a binding architectural decision.
7. Luna believes work belongs to a different slice or should be deferred, and the answer materially affects current implementation.

No fixed number of Luna failures is required before Astra reasons about an issue. Luna must not bypass Astra or dispatch Sol directly. If Astra resolves the issue, the current Luna implementation context implements the ruling and runs required tests; Terra independently verifies it under the slice review/fix rule.

### GPT-5.6 Sol High - focused escalation advisor only

Only Astra may dispatch a fresh Sol High consultation, after focused Astra analysis determines that the issue remains materially uncertain or an independent/deeper specialist opinion provides material value. Examples include:

1. Contradictory evidence with no clear governing source.
2. Subtle cross-system concurrency/recovery reasoning where multiple plausible solutions remain.
3. External provider semantics that remain uncertain after available source, package and documentation have been inspected.
4. A proposed architectural deviation with material correctness implications.
5. A substantive Terra issue unresolved after focused Luna work and Astra guidance.

Code size, difficult SQL, migration, concurrency, authentication, configuration, deployment, an external integration, or a routine Luna implementation question alone does not justify Sol consultation.

Sol analyzes only the bounded problem and provides root cause, alternatives, the smallest faithful resolution, and affected invariants/tests. Astra evaluates that advice against the authoritative pack and accepted implementation, then decides the resolution. The current Luna implementation context implements it and runs required tests; Terra independently verifies it through the review/re-review gate. Neither Sol High nor Sol XHigh takes ownership of the slice.

The escalation hierarchy is Luna -> Astra Max focused consultation -> optional specialist escalation chosen by Astra -> Astra ruling -> Luna implementation/tests -> Terra independent verification. Luna Max handles bounded implementation escalation; Sol remains an independent advisor outside the normal slice lifecycle.

### GPT-5.6 Sol XHigh - exceptional escalation only

Use only when Astra determines that the bounded Sol High consultation still leaves a blocking issue unresolved, or the problem requires exceptional cross-system reasoning. Astra retains the resolution decision; Luna implements/tests and Terra independently verifies. Sol XHigh remains outside the normal slice lifecycle and never replaces Astra as Luna's first escalation point.

## Implementation context management

Use the same Luna context while it remains efficient and useful. Astra may rotate to fresh Luna High when accumulated context becomes materially large or repetitive: repeated build/test output, obsolete diagnostic logs, a cleanly completed major phase, safely isolated review fixes, or substantial repeated context processing. A long-lived conversation is not a correctness requirement.

Before rotation, create a concise handoff containing only:

- repository, branch, PR, and exact current SHA;
- slice objective and completed work;
- binding decisions/invariants relevant to remaining work;
- outstanding work and known failures/findings;
- test status/counts and evidence/artifact paths;
- exact next action.

Do not paste large logs or prior conversations into the handoff. The new context must inspect current code before editing; the handoff does not substitute for the repository.

Model-context rotation does not imply changing the checkout or environment. Preserve the current checkout and any local-only harnesses, fixtures, receipts, or evidence required by the slice; do not move to a fresh worktree/checkout merely because the model context rotates. If an environment change is unavoidable, explicitly recreate and rerun any required local-only evidence before relying on it. Do not claim prior evidence that is no longer available.

## Evidence and acceptance verification

Retain full command output in local evidence/log files by default. Successful gates return compact receipts to Astra/Luna containing:

- exact command and exit/result status;
- passed/failed/skipped counts;
- relevant source/artifact/DACPAC hashes;
- paths to retained detailed evidence;
- concise warning/error summary.

Astra must verify that every required gate actually ran and passed for the intended source/artifact. Do not repeatedly ingest or reproduce successful command logs, complete source dumps, browser output, or migration output when compact objective evidence suffices. Inspect raw evidence selectively for failures, counts/hashes that do not reconcile, findings dependent on details, security/concurrency/migration correctness requiring direct inspection, or other ambiguity. On failure, inspect the relevant tail/section/error and expand only as diagnosis requires; do not repeatedly reread the complete log.

Do not rerun an expensive objective gate solely for Astra to consume the same successful output again. Rerun when changed source/inputs or the gate's requirements demand fresh verification. Do not weaken tests or suppress useful diagnostics to reduce context use. Required Release builds, real-SQL tests, Node/frontend tests, Playwright/browser acceptance, axe gates, native migration/export/import/reconciliation, artifact and DACPAC verification, exact-SHA remote CI, and applicable release/rehearsal gates remain binding. This reduces duplicate model processing, not objective verification.

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

For **every remaining** vertical slice beginning with Slice 5:

1. Astra Max refreshes the prepared focused slice packet against accepted prior implementation.
2. Normally a fresh Luna High context implements the complete slice, with bounded escalation/context rotation under the rules above.
3. Luna runs all required pre-review gates and verifies the slice end-to-end. At the user's direction, stop here at a pushed review-candidate checkpoint under the commit policy below; otherwise continue to Terra.
4. A fresh Terra High context performs Pass 1 over the full slice.
5. If Pass 1 has no substantive finding, no ceremonial full Pass 2 is required. Astra may proceed to acceptance verification or request focused confirmation of a particular high-risk invariant.
6. Confirmed substantive findings go to the current Luna implementation context for fixes and affected/full required tests.
7. Terra then performs focused re-review of each fix, directly affected callers/invariants, regression surface, added/changed tests, and interactions among fixes. A localized correction does not automatically restart review of the entire slice.
8. Require another FULL-slice review if the finding was systemic; the fix changed broad/shared infrastructure; authorization/concurrency/migration architecture changed materially; several unrelated areas changed; focused re-review exposes a new unrelated substantive defect; or Astra/Terra cannot confidently bound the regression surface.
9. Repeat fixes and the appropriate re-review as necessary. There is no numeric pass cap permitting a known blocking defect to remain. Correctness, security, data-integrity, migration, authorization, concurrency, and material-regression defects block acceptance; only nonblocking leftovers may be deferred.
10. Astra verifies required gate receipts and acceptance, then creates the coherent milestone commit only after the slice gate passes.
11. Astra pushes the milestone and requires actual remote CI success for that exact commit before dispatching the next slice; no earlier green run substitutes for it.

The normal loop is Astra packet refresh -> Luna High implementation/tests -> fresh Terra full Pass 1 -> fixes/tests and focused or full re-review when required -> Astra acceptance/milestone/push/exact-commit green remote CI -> next slice. Sol remains outside that loop. The separate final whole-app review below is unchanged.

The user-authorized `../implementation/temporary-email-transport.md` decision remains binding: `FileEmailSender` substitutes only the final provider boundary. It does not weaken the durable SQL outbox, authorization-sensitive recipient checks, recipient-domain safety, or lease/fencing/idempotency/retry behavior; it does not simulate Postmark webhook/provider success or become another subsystem. Real Rest 3-compatible cancellable `Clc.Postmark.Api` integration and required provider/webhook/transport tests remain release/rehearsal blockers. This model-policy update executes no implementation or release validation and starts no slice.

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

At the user's direction, Astra may commit and push a review-candidate checkpoint after implementation and all required pre-review gates, before Terra runs. Use a clearly nonfinal message such as `WIP Slice 5 review candidate`. `../implementation/PORT-STATUS.md` and handoff material must label it `implemented / ready for independent review`, not `accepted`, and state that Terra has not run yet.

A review candidate is not an accepted slice milestone and does not satisfy the independent Terra gate. Its remote CI cannot substitute for the accepted milestone's exact-SHA CI, and no later slice may start from merely a candidate. Resume from that candidate with the normal independent Terra review/fix/acceptance process. Create the accepted milestone only after required review/fixes and Astra acceptance; only that accepted milestone's own successful exact-SHA remote CI satisfies the progression gate.

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
