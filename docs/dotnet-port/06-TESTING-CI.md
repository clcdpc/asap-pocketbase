# Testing and CI Strategy

## Current Scope Notice - 2026-09-16

The full release/rehearsal matrix in this document remains a future
production-readiness contract. Current reduced Slice 8 runs the existing
Release build, full .NET/real-SQL and frontend suites, publish/exclusion checks,
exact test-IIS artifact/manifest validation and workflow isolation checks. It
does not require a Windows runner or live provider/host evidence.

## 1. Testing philosophy

The port must replace PocketBase without building a temporary dual-implementation parity harness. Use the old application as a behavior reference, then encode durable behavior in .NET integration tests, real-SQL tests, frontend unit tests, and targeted browser journeys.

A slice is not complete merely because it compiles. Tests must exercise the boundaries the slice changes: SQL constraints/transactions, authorization/scope, external-operation reconciliation, frontend contract, and migration transformation where relevant.

## 2. .NET test project

Use one `Asap.Tests` project rather than splitting the solution into many test assemblies. Organize by folders/categories/fixtures:

```text
Asap.Tests/
  Unit/
  Integration/
  Sql/
  Auth/
  Patron/
  Staff/
  Requests/
  AdditionalCopies/
  Settings/
  Email/
  Jobs/
  Migration/
  Analytics/
```

Unit tests are used for pure normalization/state-transition/template logic. Do not substitute mocks for database behavior where SQL constraints/transactions/concurrency are material.

## 3. Real SQL testing

Persistence/integration tests use real SQL Server, not EF InMemory/SQLite emulation.

- CI uses SQL Server 2022 container/service with compatibility level 160.
- Local development uses SQL Server Developer Edition, configured via `Development.local.json`; no fixed instance name is imposed.
- Test setup deploys the DACPAC to an isolated test DB and seeds only necessary fixtures.
- Every PR and push to main runs the real-SQL suite.

Required real-SQL areas include:

- constraints/unique indexes/FKs;
- StaffUser active-identity requirements and filtered unique (`EntraTenantId`,`EntraObjectId`) binding; verify duplicate readable UPN/email does not become an authorization collision; issue a staff cookie to identity A, rebind the same StaffUser to identity B, and prove A's existing cookie immediately fails tuple revalidation while B can authenticate and unchanged-tuple UPN/display refresh does not invalidate the session;
- role/org invariants where DB-enforced, including a staff/admin row remaining structurally valid when its referenced non-system Organization is inactive; transactional StaffUser scope-contraction lifecycle behavior, including super-admin -> library admin/staff and Library A -> Library B cleanup of out-of-scope active auto-claim rules, open TitleRequest claims, and open AdditionalCopy claims while preserving closed history and recording the appropriate event/note/admin-audit counts;
- concurrent final-two-super-admin demotion/deactivation attempts serialize on `ASAP:ActiveSuperAdminInvariant`; no more than one succeeds, the other returns the defined 409 invariant error, and at least one active super-admin remains;
- StaffUser recipient/preference contract: `NotificationEmail` as the nullable primary staff notification address; weekly summary override/fallback behavior; placeholder `@staff.asap.local` rejection; authorized Staff Access setting **and clearing** of primary email; intentionally cleared primary email surviving later Entra sign-in unchanged; self-service weekly override clearing/fallback; and persistence/API round-trip for all profile preferences;
- system-only configuration cannot represent library scope;
- `WorkflowSettings`/`PatronSettings`/`EmailSettings` field-level inheritance, including partial-row fallback and per-field reset;
- whole-set publication/common-creator/patron-code inheritance, blank-input reset, and reset;
- external-search provider/override uniqueness and scope;
- patron custom-field/option ownership, stable option IDs/enabled/order, and per-format custom-field rule ownership/modes;
- `Reset inherited overrides` preserves library-owned custom fields, custom formats, custom rejection templates, and auto-claim rules/history;
- material format/override/rule uniqueness, typed built-in field behavior, and relational custom-field format behavior;
- rowversion 409 behavior;
- atomic local workflow transaction + outbox/event/tag behavior;
- SQL-stored Polaris/Postmark reusable secrets are ciphertext and round-trip only through the configured Data Protection protector; wrong/missing key ring fails safely without exposing plaintext;
- deletion audit;
- patron token hash lookup/expiry plus authorization-time `EffectiveOrganizationId` active-state enforcement, transactionally race-safe final session issuance versus library deactivation, and the rule that organization reactivation never resurrects revoked sessions;
- email outbox idempotency with a filtered unique non-null `BusinessKey`, simultaneous duplicate enqueue races resolving to one row/successful idempotent results, normal recipient+period weekly-summary idempotency, forced-summary `ManualRunId` keys, lease acquisition/fencing and expired-`sending` recovery under the fixed timeout/lease boundary, terminal `suppressed` state, authorization-sensitive recipient/scope/`RecipientAddressKind` fields, and payload-purge eligibility limited to terminal `sent`/`suppressed`;
- HoldPlacementOperation one-active-per-request and attempt-number uniqueness under concurrent acquisition, plus the incomplete-operation request-mutation barrier and Organization -> TitleRequest acquisition order;
- analytics queries;
- migration import/reconciliation, including system/global-only effective-runtime configuration provenance, a blank persisted Staff URL resolved from environment into the target SQL value, operational schedule/processing-limit capture and target parity, explicit handling of obsolete hourly-ISBN queue overrides, intentional SMTP-transport drops, target-only Postmark secret provisioning without plaintext appearing in export files/logs/reports, primary/weekly staff recipient migration precedence, explicit old-versus-target recipient/eligibility delta reporting (including a summary-enabled + blank weekly email + real primary address becoming newly eligible), exhaustive legacy ISBN-status fixtures/mappings/blockers, complete staff preference preservation, and the explicit active-bound-super-admin hard gate/provisioning path when imported StaffUsers contain zero qualifying super-admins, and auto-claim-rule fixtures for valid same-library assignee, valid cross-library super-admin, missing/deleted assignee, inactive assignee, wrong-library assignee, and historical request references to rules normalized inactive; reconciliation proves every active imported rule has a valid active scope-eligible assignee.
- recurring-job registration/configuration contract: every schedule key in the authoritative matrix exists, defaults match the matrix (including one hourly `WorkflowProcessing`, Sunday 20:00 weekly summary, and five-minute identifier processing), cron expressions are registered with `Application.BusinessTimeZone`, and no independent recurring timeout/promotion/hold/fulfillment jobs are registered.
- processing-limit resolver parity: queue-specific overrides win over timeout-family values, timeout-family values win over global defaults for timeout queues, ranges/defaults match the pinned source, and every known target logical queue resolves correctly.

## 4. External integrations in ordinary CI

Normal PR/main CI uses deterministic fakes/stubs for Polaris and Postmark. The application should have small real integration boundaries that can be substituted in tests without mocking internal feature services.

`Clc.Polaris.Api` and `Clc.Postmark.Api` own their protocol mechanics. ASAP tests focus on orchestration, mapping, error/retry/reconciliation behavior, and configuration selection.

No live Postmark send is a normal release blocker.

### 4.1 Nonproduction recipient-domain safety

Implement deterministic tests for the single predicate in `01-PORTING-SPEC.md` section 14. Use a recording fake Postmark boundary and otherwise valid notification configuration/authorization; assert persisted outbox outcomes and provider-call counts, not just a helper's return value. For allowed cases, drive delivery through the worker and assert `sent` with one fake provider call. For blocked cases, assert terminal `suppressed`, reason `recipient_domain_not_allowed`, and zero provider calls. Unless stated otherwise, use `IsNonProduction=true` in the cases below.

| Case | Configuration / recipient | Required outcome |
| --- | --- | --- |
| Exact domain | `IsNonProduction=true`, allow `example.org`, send to `patron@example.org` | Allowed. |
| Case normalization | Allow `EXAMPLE.ORG`, send to `patron@Example.Org` | Allowed by case-insensitive comparison. |
| Unlisted domain | Allow `example.org`, send to `patron@other.example` | Suppressed. |
| No implicit subdomain | Allow only `example.org`, send to `patron@staff.example.org` | Suppressed. |
| Explicit subdomain | Explicitly allow `staff.example.org`, send to `patron@staff.example.org` | Allowed. |
| Empty allowlist | `AllowedRecipientDomains=[]` in nonproduction | All delivery suppressed. |
| Missing allowlist | Omit `AllowedRecipientDomains` in nonproduction | All delivery suppressed. |
| Malformed configuration | Entries such as `*.example.org`, `.example.org`, `https://example.org`, `patron@example.org`, or `example..org` | Startup configuration validation fails safely; zero provider calls, no broadened matching. |
| Production | `IsNonProduction=false`, recipient outside a valid list, or list empty/missing | Delivery allowed by this predicate. |

Run representative allowed and blocked cases through patron business-event mail, authorization-sensitive staff mail, ordinary and forced weekly summaries, and **Test email**, proving every path uses the same predicate. Every outgoing recipient must pass, including To/Cc/Bcc when present. Cover a queued message allowed at intent creation but blocked by the configuration loaded after restart: every send/retry rechecks and terminally suppresses it without calling Postmark.

Use real SQL to prove a business action with a blocked notification still commits its normal state/event and one suppressed intent atomically. Repeat/concurrently invoke the same idempotent business event and verify its deterministic `BusinessKey` resolves to the existing suppressed row without a duplicate or action failure. Suppressed rows expose no Retry and remain terminal after the allowlist is expanded.

Prove the switches are independent: `ASPNETCORE_ENVIRONMENT=Testing` with `IsNonProduction=false` does not activate the domain restriction; a normal Development host with `IsNonProduction=true` enforces it without registering testing authentication. The tests exercise the existing outbox/provider boundary, not a separate test-mail pipeline.

## 5. Frontend/jsdom tests

Keep/adapt useful existing jsdom tests. Node/npm is allowed for development/CI tests only.

Use these tests for fast behavior such as:

- status/filter policy;
- grid formatting/rendering helpers;
- settings form population/inheritance display across the domain-specific backend model;
- API helper behavior;
- accessibility-related DOM state where jsdom is sufficient;
- URL/deep-link normalization utilities.

Do not force Node into MSBuild app compilation merely because tests use it.

## 6. Playwright

Add a targeted Playwright suite, not exhaustive browser duplication of every unit/integration test.

Run on every PR and push to `main`.

Critical journeys should include at least:

### Patron

- patron page loads and embed/CSP-safe structure works;
- login success/failure/rate-limit behavior via test integration boundary;
- material format/options render;
- submit request;
- request/session expiry behavior where practical;
- key pickup/autohold choices.

### Staff

- anonymous `/staff` sign-in state;
- test-authenticated staff/admin/super-admin sessions;
- request queue scope/filter/open deep link;
- edit/claim/action and stale-rowversion conflict;
- additional-copy lifecycle;
- settings/configuration inheritance, whole-set replacement, inherited-override reset, and preservation of library-owned configuration;
- role/scope authorization boundaries;
- failed-email/admin operational surfaces as critical.
- staff profile UI preserves/edits all current user preferences and those values influence the same workflow defaults/filters as today.

Keep the suite small enough that it remains reliable and useful as a PR gate.

## 7. Testing-only authentication

Implement a test authentication handler **inside `Asap.Web`**, but register it only when `ASPNETCORE_ENVIRONMENT=Testing`.

Requirements:

- Production and normal Development never register the scheme.
- There is no ordinary `TestAuth:Enabled=true` switch that could accidentally enable bypass in production.
- `Environment.IsNonProduction` governs only the recipient-domain safety concern here and never enables this scheme; section 4.1 verifies that the two switches remain independent.
- CI Playwright launches the actual web application under `Testing` and authenticates as seeded StaffUser identities/roles, with realistic `tid` + `oid` claims so the same durable local lookup path is exercised.
- Normal F5 uses real Entra/OIDC.

Test authorization must still flow through the normal local StaffUser/scope policies after authentication so Playwright exercises role/library enforcement rather than bypassing it.

## 8. Accessibility tests

Add `axe-core`/Playwright checks for major patron and staff states. CI fails on serious/critical violations.

Automation is not sufficient for interaction quality. Add explicit Playwright assertions for:

- keyboard reachability;
- focus-visible state;
- focus movement when tabs/modals/request views load;
- live-region/status announcements;
- label/control relationships;
- dialogs and escape/close behavior where current UI supports it.

Preserve existing accessibility affordances during the port rather than treating this as a later redesign.

## 9. Migration tests

Migration is a first-class tested system.

Use:

- curated PocketBase-like source fixtures for edge transformations;
- real stopped PocketBase backup rehearsals when available;
- real SQL target database;
- deterministic exported JSON snapshot assertions;
- transformation/report assertions;
- hard-failure tests for active staff missing/duplicate/conflicting Entra tenant/object-ID mappings, unknown actor types, unresolved formats, bad FKs, invalid branding, etc.;
- explicit migration-time active-bound-super-admin tests: imported qualifying super-admin passes; zero qualifying super-admins plus valid configured bootstrap identity promotes/reactivates the already-bound row or inserts exactly one new bound super-admin as appropriate and reports the intervention; missing/invalid/conflicting bootstrap identity blocks; normal startup bootstrap is never required to repair an already-populated StaffUser table;
- migration tests proving UPN/email is never used to synthesize or authorize a durable Entra binding; inactive historical staff may remain unbound until reactivation; and active or inactive historical staff may preserve references to inactive non-system libraries without rewriting staff lifecycle state;
- auto-claim migration fixtures proving source-active rules stay active only with a mapped active scope-eligible assignee; missing/deleted, inactive, and wrong-library assignees import as inactive historical rules without substitution; unmapped assignees use nullable target `StaffUserId`; valid super-admin cross-library targets remain active; historical request rule references remain mapped where possible; normalization is reported and reconciled;
- successful conversion tests for legacy event types/orphan claims/inactive historical organizations/reduced audit rows;
- one migration fixture for each legal legacy `isbnCheckStatus` value plus null/blank, including `found` with/without supporting BIB state, `error` with/without identifier, and `found_in_polaris` with/without supporting BIB state; deterministic branches transform exactly, inconsistent `found`/ambiguous branches block, imported `found` rows always have nonblank BIB state plus canonical found tag, and source/target transformed counts reconcile;
- reconciliation tests that intentionally inject mismatches and verify a blocking result.

The final migration oracles are explicit in section 10.2 F1 (effective timeout values/meaning) and F3 (exhaustive historical BIB protection and reconciliation); successful import counts alone are insufficient.

## 10. Concurrency/failure-path tests

Explicitly test:

- two staff editing same row -> second stale mutation `409`;
- background job result arrives after user changes request -> stale job skips;
- manual claim vs automatic rule ordering;
- StaffUser lifecycle serialization races using real SQL and the documented lock order: deactivation vs manual TitleRequest claim; deactivation vs AdditionalCopy claim; deactivation vs automatic claim execution; super-admin demotion vs cross-library `FormatAutoClaimRule` creation; Library A -> Library B move vs Library A assignment; and rule reassignment vs staff move. Test both lock-acquisition orderings and require the later writer to revalidate current eligibility rather than commit a stale relationship;
- lifecycle cleanup itself: deactivation, Library A -> Library B move, and super-admin -> Library A demotion clear now-invalid open TitleRequest **and AdditionalCopy** claims, deactivate invalid active rules, preserve closed claimant history, append TitleRequest events/AdditionalCopy Notes, and record audit counts;
- Postmark enqueue gap recovered by outbox sweeper;
- worker crashes after outbox claim but before provider call -> the row remains `sending` until the two-minute lease expires, is conservatively treated as transport-ambiguous, and is then recovered without permanent `sending` state;
- crash/provider ambiguity during or after the Postmark call -> the row is reclaimed only after lease expiry, may retry under explicit at-least-once semantics, and never claims exactly-once delivery;
- an old worker returns after its lease was reclaimed and a newer ownership state exists -> its final update fails the `Status` + `LeaseId` + rowversion/equivalent compare-and-set and cannot overwrite the newer attempt or schedule another retry;
- controlled-clock provider timing proves the call starts within 30 seconds of claim, the complete provider operation times out at 30 seconds, the sending lease expires at two minutes, and no second provider call begins before lease expiry/the documented safety boundary;
- worker/provider failure during send -> bounded retry/failed-state behavior; `failed` retains recipient/sender/content indefinitely until manual retry and returns to `pending` when retried;
- authorization-sensitive queued staff mail: queue an assignment/reminder/weekly message, then before delivery deactivate the recipient, move them to another library, demote a cross-library super-admin, or deactivate the authorization library; each case suppresses the old row and sends no restricted snapshot, while an immutable patron/business-event row still drains when intended. Address-kind cases prove an unchanged weekly override remains deliverable when only `NotificationEmail` changes; changing the weekly override suppresses a queued weekly row even when its old `ToAddress` equals the current primary address; clearing the weekly override makes resolution fall back to current `NotificationEmail`; and ordinary notifications always resolve/revalidate only `NotificationEmail`;
- outbox payload cleanup: rows in `pending`, `sending`, and `failed` retain payload even after 90 days; terminal `sent` and `suppressed` rows may have subject/body purged after 90 days while operational metadata remains; suppressed rows never expose Retry;
- missing notification configuration does not roll back business state: with missing sender and separately missing Postmark token, patron submission and staff purchase/reject/assignment/additional-copy mutations still commit; enqueue-time missing required configuration produces durable terminal suppression where an intent exists, while transport configuration disappearing after valid enqueue produces `failed/mail_not_configured` with payload available for manual retry;
- simulated crash after provider acceptance but before local `sent` commit -> row is recoverable only after the lease/safety boundary and the test explicitly acknowledges possible duplicate delivery under at-least-once semantics;
- duplicate email business trigger does not duplicate outbox message;
- two concurrent hold-placement workers/manual+scheduled paths -> only the current fenced executor can start an unmarked mutation phase; a marked phase is never replayed by an original/recovery worker;
- hold acquisition races each conflicting request mutation in both orderings: identifier change, explicit BIB change, close/reject/reopen, `AutoHold` change, hard delete, and pickup-preference change. If the request mutation commits first, acquisition observes the new state; if operation acquisition commits first, the conflicting mutation receives `409` and cannot invalidate the external operation. The pickup test also proves the dedicated action checks the barrier before Polaris, failed Polaris leaves local pickup unchanged, and a pickup mutation that externally completed first is reflected locally while the hold worker re-resolves live pickup before its own call;
- ambiguous Polaris hold response -> existing active operation remains a mutation barrier and is reconciled before local success or any later placement attempt;
- only authoritative no-effect evidence, with exclusion of any late original executor where required, can terminally resolve a marked hold operation and release its barrier; elapsed time or empty lookups never authorize another create;
- external Polaris failure does not corrupt local state;
- library deactivation revokes patron sessions and blocks subsequent staff authorization without changing `StaffUser.IsActive`; existing staff relationships remain stored; every later patron-authenticated request rejects a still-present token whose effective library is inactive; a login racing with deactivation either commits before the deactivation transaction and is revoked by it, or observes inactive state and cannot issue a session; library reactivation restores staff eligibility but never resurrects a revoked patron session;
- StaffUser authorization-scope contraction: a super-admin with cross-library rules/claims demoted to Library A loses active rules/open claims outside Library A in the same transaction, and a staff/admin move from Library A to Library B clears/deactivates the old-library operational relationships while preserving closed claimant snapshots/history;
- hourly workflow executes acquired-hold recovery -> ordered timeout queues -> purchase promotion -> new hold placement -> fulfillment, including timeout guards on later-phase candidates; recovery of acquired inactive-library work does not enable new acquisition; scheduled/manual runs cannot overlap;
- canonical identifier processor classification: only a successful zero-result search can produce `not_found`; network/timeout/throttling/transient upstream failures produce `TransientFailure` and never a not-found tag; auth/config/malformed-request/protocol failures produce `OperationalFailure`, do not consume/reset the request retry budget, surface/fail the run, and stop that run before later requests are interpreted against known-bad integration state; unknown failures default to failure, not `not_found`;
- canonical identifier retry/recovery: attempts 1-4 transiently fail -> retry count advances and state remains `pending`; fifth consecutive transient failure -> `error_max_retries`; retries occur on fair queue revisits at the normal five-minute schedule, subject to the bounded scan rather than a guaranteed per-row five-minute interval, with no extra backoff; found/not-found/skipped outcomes reset count/error state; manual **Retry identifier check** resets count/error state and enqueues the same scoped processor; `LastCheckedUtc` is updated consistently;
- permitted pre-placement identifier-change invalidation: `found` -> identifier changed -> transient failure leaves no old BIB/result/found/multiple-match authority; `found` -> identifier changed -> definitive not-found clears the old BIB/found tags and applies only the new not-found state; an old not-found tag is removed when the identifier changes; clearing the identifier produces `skipped_no_isbn` with no BIB/result/check/error/retry/identifier-derived tags; and no stale pre-edit BIB can trigger purchase promotion or hold placement before the new identifier is revalidated (unless staff explicitly performs a new validated manual BIB assignment after the edit);
- inactive-library automation: a request that is `pending_hold` when its library becomes inactive cannot begin a new Polaris hold placement; identifier/promotion/fulfillment/timeout processing and equivalent manual Run Now paths skip inactive-library work, while infrastructure jobs continue; immutable already-committed business-event outbox rows may still drain, but authorization-sensitive staff rows suppress when current authorization is gone;
- Organization-row serialization races using real SQL: deactivation versus identifier-result application, timeout closure, purchase promotion, HoldPlacementOperation acquisition, fulfillment advancement, and library-scoped manual Run Now. Test both orderings: deactivation-first prevents the later participation-dependent commit/acquisition, while a local item commit or hold-operation acquisition that wins first may finish/reconcile without holding SQL across Polaris;
- weekly-summary authorization scope with at least two libraries: staff/admin counts, sample titles, and links contain only their own active library; inactive-library staff receive no summary; super-admin receives an active-consortium summary; one library's request data cannot appear in another library's staff summary;
- weekly-summary run idempotency: ordinary scheduled/non-forced execution sends once per recipient+period; a second non-force invocation does not resend; an explicit forced invocation creates a distinct `ManualRunId` and sends again; retrying the same forced Hangfire run reuses that ID and sends at most once per recipient, while a later explicit force gets a new run identity;
- pickup-preference workflow: successful Polaris update changes `PreferredPickupBranchId`/`PreferredPickupBranchName` and records note/event; Polaris failure leaves the request unchanged; stale request version and live-Polaris changed-since-load conflicts return `409`; generic request editing cannot change pickup fields;
- operational-config parity fixture where custom legacy cron, global limits, timeout limits, and queue-specific limits are captured with provenance and mapped to target external JSON; mismatches block job enablement.

### 10.1 Mandatory closure-remediation regressions (R1-R7)

These are blocking implementation/release requirements, not results already executed by this documentation revision. Use real SQL Server for locking, rowversion, checkpoints, and durable failure boundaries; provider fakes must distinguish final positive/negative evidence from ambiguous transport or prompt responses. UI cases use the actual frontend, not only service tests.

#### R1 - Hold operation recovery

Inject process/transport/SQL failures (a) after acquisition, (b) immediately before create, including either side of the committed `create_started` marker, (c) after possible provider acceptance with lost response, (d) after receiving create response before/after persisting it, (e) between durable create response and reply, including either side of `reply_started`, and (f) before local completion. Assert frozen BIB/patron and complete GUID/qualifier/reply context, one local completion/event/business-key intent, correct barrier/state, and no unsupported second create or repeated uncertain reply.

Race original worker versus recovery, two recovery workers, and an old worker paused after its marker until after lease expiry/takeover. Verify token/epoch CAS fences stale local writes; lease expiry does not fence Polaris and therefore cannot permit replay. Verify first unmarked create/reply can resume, but marked uncertainty remains observation-only. Restart with every incomplete phase, including Organization deactivation after acquisition; hourly recovery still selects it, completes already-authorized work when safe, and never acquires a new operation for that inactive organization. Verify no SQL transaction spans a network call and hidden SDK mutation retries/auto-replies are disabled.

Test correlated final success; definitive no-effect with no live pending reply/late dispatch; empty/delayed/failed/mismatched lookups; generic HTTP/SDK success that is only a prompt; missing response context; and unsupported provider recovery. Time alone and repeated empty queries never close ambiguity. After at most three recovery evaluations without proof (or immediately when proof capability is absent), expose `operator_required`; it stays a barrier without endless automatic scans. Reconcile/resolve requires current super-admin, expected version, reason, evidence, and original-executor exclusion where applicable. Reject unsupported ResolveNotPerformed and absent proof; ResolveSucceeded preserves actual placed BIB; later numbered attempts after proven no-effect require a fresh active-organization acquisition. No ForceRetry exists.

#### R2 - Identifier stage and placed BIB

For **each** `suggestion`, `outstanding_purchase`, `pending_hold`, `hold_placed`, and `closed` status, test changed identifier, cleared identifier, normalized unchanged identifier, and changed identifier plus explicit BIB. Cover every closed reason and combined transition/edit payload, including reopen plus edit. Pre-placement legal edits atomically invalidate only old identifier-derived state; explicit BIB is validated after invalidation. Placed/closed or retained placed-history protection rejects changed/cleared inputs; unchanged is a no-op and cannot legitimize an otherwise illegal BIB change. A completed operation does not lift protection; migrated recorded-BIB guard survives reopening without claiming provider proof.

Race acquisition/completion with editing in both orders. Assert rejected edits change no BIB/result/tags/retry data, event/audit/outbox, or provider state; stale version still returns normal 409. Verify server DTO capabilities, disabled/read-only controls, explanatory reason, keyboard/focus behavior, and errors match backend rules. Positive checkout fulfillment after attempted editing continues matching the actual successful operation BIB (or protected recorded legacy BIB), not a replacement identifier lookup. Terminal hold outcomes additionally require the tracked final hold identity in F2; exhaustive imported protection and separately committed reopen/edit fixtures are in F3.

#### R3 - AdditionalCopy retained claimant activation

Close a task, then separately deactivate its claimant, move Library A -> B, or demote its cross-library super-admin to a Library A role for a Library B task. Closed rows remain unchanged. Reopen in the task's authorized active library: clear all invalid effective claim fields, append exactly one Notes attribution/reason, and never assign the acting user or another user. Still-valid same-library and still-valid cross-library super-admin claims retain original identity/display/time. A task with no retained claimant remains unclaimed.

Race reopen with each lifecycle mutation in both Organization -> StaffUser -> task lock orderings; whichever commits first, no invalid effective open claim survives. A changed candidate claimant restarts validation in order or returns conflict, never locks StaffUser after task. Stale/repeated reopen returns the normal conflict response, leaves history unduplicated, and the UI refreshes mine/unclaimed results and `claimClearedReason`.

#### R4 - Migration operational claims

For **both** TitleRequest and AdditionalCopy, import unmapped, mapped inactive, mapped wrong-library, demoted-super-admin, valid same-library, and valid cross-library super-admin claimants; test open/actionable and closed counterparts. Cover all actionable TitleRequest statuses, inactive organization with an otherwise valid claimant (preserved), and inactive organization with an independently invalid claimant (cleared). Active identity-map errors still block the separate identity gate.

Assert map-then-eligibility ordering, exact reason/count per type/library, effective-field clearing, original attribution in existing history/report, no substitute/auto-claim, valid claim preservation, and closed-history preservation even for mapped inactive/out-of-scope staff. A real-SQL reconciliation anti-join/predicate finds zero invalid effective open claims; FK existence alone must not satisfy the test. Fresh-target reruns from the same frozen export produce identical data/conversion reports. Test placed-history marker provenance/counts and unknown BIB staying unknown rather than fabricated.

#### R5 - Current tenant policy with real persisted cookies

Use the real application cookie middleware and persistent Data Protection ring/certificate, not the Testing-only fake authentication handler. Issue valid cookies in two allowed tenants; retain another qualifying super-admin so the removal configuration is valid. Restart against the same SQL database/key ring after removing one tenant. On next use, that tenant's old cookie is rejected/expired and protected API returns 401 with no redirect loop or sliding renewal; the other tenant's cookie still succeeds. Test staff shell/session UX and a direct protected endpoint, not only a fresh OIDC login.

Queue authorization-sensitive mail with the removed tenant's snapshotted tuple before restart; dispatch/retry suppresses it without provider call. Re-add the tenant and verify independently deactivated/rebound/moved/demoted authorization remains invalid for the corresponding resource; suppressed mail does not revive. Confirm sensitive mail also rejects a tuple rebound away and usable-admin counts include loaded trust. Candidate configuration leaving zero usable system super-admins is rejected before activation; a direct-file bypass fails startup readiness closed with workers/protected endpoints disabled, while liveness/diagnostics permit repair. Empty-database bootstrap and stopped migration gate remain the documented distinct exceptions, never repair a populated table silently.

#### R6 - Bounded queue fairness and phase order

Use PageSize 1 and MaxPerRun 2 with at least five eligible rows. Leave the oldest rows unresolved and put an actionable row later; assert it is examined within the finite-cycle bound in `01-PORTING-SPEC.md` section 23.1 across repeated invocations. Parameterize over the eight configured queues and logical HoldRecovery; operator-required operations are excluded until explicit reconciliation. Verify no parallel workers or per-invocation cap overruns.

Restart after each checkpoint; delete the cursor row; insert new rows with both newer and backdated creation timestamps after the watermark; make an existing row eligible ahead of/behind the cursor; and use a late commit below the watermark. Assert deterministic ahead/current-cycle versus behind/next-cycle behavior, scalar cursor safety, finite wrap, and no reset at cap exhaustion. New insertions cannot extend the current cycle indefinitely. Change configured limits without resetting progress.

A repeatedly failing item with a durably recorded handled outcome advances progress so later work is reached. SQL failure before durable handling/checkpoint does not advance; a global operational integration failure records the current failure/checkpoint then stops safely, with future healthy runs resuming. Test a crash after external evidence journaling and before local checkpoint to prove revisiting does not duplicate a mutation. Progress is atomic with local outcomes where applicable and never precedes a required durable operation result.

Scoped manual runs use only their authorized scope's cursor and rows, never reset/advance the global cursor, and share the existing logical-job non-overlap guard with scheduled/all-library runs. Preserve recovery -> four timeout queues -> promotion -> new placement -> fulfillment. With a low timeout cap, the later-phase guard defers a row that satisfies its current status-specific timeout predicate (section 23.2 of the porting spec) until its fair timeout queue handles it. An old outstanding_purchase does not satisfy OutstandingTimeout and must not be deferred by that timeout. Incomplete acquired operations cannot be cancelled/replayed by timeout processing.

#### R7 - Database-changing deployment safety

Record an ordered deployment trace and assert these cases:

| Release | Required behavior |
|---|---|
| Unchanged DACPAC, no Hangfire/dependency DDL or other DB writes | Genuine file-only path: no SQL backup/DDL/DeploymentState mutation; stopped file replacement, installed-manifest tracking, both-schema startup checks |
| Changed DACPAC, including unchanged SchemaVersion | Stop/quiesce -> verified backup of existing DB -> DDL -> both-schema compatibility -> files/start |
| Unchanged DACPAC, required Hangfire schema change | Same database-changing path and ordering; unchanged app hash never bypasses backup |
| Backup failure/verification failure | No DDL, deployment-state update, or file replacement; no unsafe automatic worker restart |
| Dependency schema upgrade failure/partial DDL | Keep workers stopped; no incompatible old/new restart or automatic database rollback |

Also test unknown dependency version/schema (fail preflight), manifest/asset hash mismatch, changed SchemaVersion with unchanged DACPAC, automatic Hangfire schema preparation disabled on normal startup, least-privilege runtime DDL refusal, and old-code rollback compatibility against the **actual** Hangfire schema as well as application SchemaVersion. A compatible old-code restart must be an explicit validated recovery action, not an exception-handler assumption. Fresh empty initial-target preparation preserves the documented no-prior-dataset exception and remains stopped for migration.

### 10.2 Final three-finding remediation regressions (F1-F3)

These are required implementation and release tests, not execution results from editing this documentation. Retain every prior R1-R7 gate. Use injected clocks, documented provider-adapter fixtures, real SQL for transactions/rowversion/outbox/import, and actual scoped API/frontend capability assertions.

#### F1 - OutstandingTimeout status and age semantics

Use `01-PORTING-SPEC.md` section 23.2 for every timeout family. For the 30-day examples below inject `now = 2026-09-12T16:00:00Z`, timezone America/New_York, so the cutoff is `2026-08-13T16:00:00Z`. Business-calendar subtraction and exact UTC comparison must be shared by scheduled/manual invocation, not a rounded integer age.

| Case | Required oracle |
|---|---|
| 31-day-old suggestion versus 31-day-old outstanding_purchase awaiting BIB, same enabled 30-day setting | Suggestion becomes `closed/rejected` with one `timeout_closed` event; outstanding purchase does not close or get deferred by OutstandingTimeout, and retains normal promotion eligibility. |
| 29-day-old suggestion | Remains suggestion; no closure event or notification intent. |
| Expired suggestion, effective timeout disabled | Remains suggestion, including an explicit false library override of enabled system settings. |
| Expired suggestion in inactive Organization | No new ordinary business processing, closure event, or rejection mail; exercise scheduled and scoped manual paths. |
| Exact cutoff and adjacent supported timestamp values | Exactly at cutoff stays open; one datetime2 tick before expires; one tick after stays open. Also cover calendar-day cutoff across business-timezone DST transitions, including gap/overlap resolution. |
| Recent edit to a 31-day-old suggestion | Still creation-aged and expired; UpdatedUtc must not restart OutstandingTimeout. |
| Enabled configured timeout-rejection email/template | Exactly one durable intent for the winning closure event and correct effective recipient/sender/template; repeated/concurrent scheduled/manual execution creates no duplicate event or intent. |
| Sending disabled; missing template/sender/transport; subsequent delivery failure | Business closure still commits. No deliverable intent when disabled; missing-at-creation configuration follows existing suppression diagnostics; delivery failure never reopens the request. |
| Migrated complete system/default row plus sparse library overrides/reset | Preserve enabled/days/send/template identity, effective inheritance, and suggestion-creation-age meaning. Different library template selection and disabled override are respected; no field is reinterpreted as approved-purchase expiry. |
| Scheduled versus manual and stale/concurrent candidates | Identical predicate/order; status changed to outstanding_purchase, timestamp/settings changed, Organization deactivated, new operation barrier, or stale request version cannot commit a stale closure. Assert both race orders under existing locks. |
| Low caps / different queue ordering | CreatedUtc/Id cursor order never becomes the age for pending_hold or hold_placed; use updated-age fixtures. AdditionalCopy uses updated age with the source missing-updated fallback to created. Each family produces its specified close reason/task closure and notification behavior. |

Assert the later-phase guard dispatches by **current status**: OutstandingTimeout does not apply to approved purchases; PendingHoldTimeout and HoldPickupTimeout still apply to their own stages. Queue-only visits/diagnostics do not reset timeout age. No test may replace the four predicates with a generic created-age expiry rule.

#### F2 - Particular-hold terminal authority

Use Patron P, expected BIB 42, tracked H200, and historical H100. H200/H100 are fixture labels for distinct valid provider HoldRequestID values (for example 200 and 100), not BIBs or RequestGUIDs. Keep HoldPickupTimeout disabled/not due except in the explicit timeout-control cases so the tested closure authority is unambiguous.

| Case | Required oracle |
|---|---|
| Current H200/BIB 42 active, old H100/BIB 42 unclaimed | Request remains hold_placed; H100 cannot authorize closure. |
| Current H200 active, old H100 cancelled | Remains hold_placed. |
| Current H200 active, old H100 expired | Remains hold_placed. |
| Actual H200 terminal, parameterized unclaimed/cancelled/expired | Closes exactly once as hold_unclaimed/hold_cancelled/hold_expired respectively, with one correct ordinary fulfilled event/note; retries/races do not duplicate the transition. |
| Terminal row with different BIB, including matching HoldRequestID | Cannot close; inconsistent ID/BIB evidence is diagnostic. A different hold ID also cannot close. |
| Mixed result ordering and several same-BIB historical/current holds | Permute rows/status response ordering. Only H200 supplies terminal authority; arbitrary first/latest same-BIB rows do not decide it. Conflicting statuses for H200 preserve state as ambiguous. |
| Missing/legacy final hold identity | Same-BIB terminal rows preserve state and produce hold_identity_unavailable/ambiguous diagnostic; no fabricated operation, ID, success, or mutation barrier. Include protected imported BIB and protected-null-BIB histories. |
| Required checkout/hold provider read fails or is malformed | Preserve state and surface hold_provider_error; do not treat missing/partial results as no hold or fall through after a failed checkout read. |
| New placement yields proven final ID | Persist exact HoldRequestID separately from RequestGUID/qualifiers and BIB, from the documented normalized adapter result/correlated read. Real package-contract tests prove raw-field mapping. |
| Final placement succeeds but final ID is unavailable | Complete the genuine successful operation once with null ID and uncorrelated diagnostic, no replay/old-ID fallback; a later proven null-to-ID enrichment uses that completed row with current request/operation versions, without an old executor lease or repeated placement/event/email; competing enrichment or changed current association cannot overwrite identity. |
| Existing-hold adoption supplies final ID | Capture that selected nonterminal row's HoldRequestID in the same operation with existing_hold_adoption evidence and no fictitious create/reply marker; no extra external create. Multiple indistinguishable candidates or Boolean-only evidence cannot manufacture correlation. |
| State/scope/identity changes during provider reads | Before local closure, normal Organization activity, manual authorization/library scope, request version/current status, patron/BIB/tracked operation/ID, timeout, and operation barrier are revalidated. A stale result makes no business change. |
| Successful current checkout for expected BIB | Preserve closed/hold_completed and one fulfilled event without requiring final hold ID; another BIB does not fulfill. Successful checkout needs no terminal queries. |
| Manual closure and enabled local HoldPickupTimeout | Continue under ordinary rules even when final hold ID is absent; no identity-based barrier is invented. Verify their reasons/events separately and retain timeout-before-fulfillment ordering. |

Also cover latest-successful-operation selection: a newer successful operation with null ID must not inherit an older successful hold's ID; an unsuccessful attempt is not a tracking identity. A historical BIB-protection marker may keep edits locked while terminal correlation remains unavailable.

#### F3 - Exhaustive migration BIB-protection evidence

Run the real importer/reconciler against normalized source fixtures, including raw status/close-reason codes and actual source relation-to-taxonomy forms. Assert the complete evidence union and deterministic metadata contract in `04-MIGRATION-CUTOVER.md` section 6.10, before event types are mapped to target legacy.

| Fixture | Required oracle |
|---|---|
| Each of hold_completed, hold_not_picked_up, hold_unclaimed, hold_cancelled, hold_expired; BIB 42, no literal hold_placed event | Five separately parameterized fixtures each receive exactly one protected marker with BIB 42 and exact reason provenance. Include a null closeReasonRef with a valid raw canonical reason, reflecting the source helper's lossy alias behavior. |
| Current status hold_placed, no events | Protect from current state alone; preserve source status/BIB. |
| Dedicated hold_placed event, later non-hold-specific closure | Protect from the retained event. |
| Ordinary status_changed with toStatus resolving to hold_placed | Protect from normalized transition; include relational source status reference and recognized legacy status alias. |
| Existing-hold adoption -> status_changed/hold_placed -> later manual closure | Protect despite manual reason and no literal placement event. Preserve exact adoption-transition provenance without inventing final provider ID. |
| Placement event missing because recording failed/best-effort | Current placed state or any terminal reason still protects. Also exercise departure from hold_placed/event-terminal evidence; when only hint/conflicting evidence survives, report/block placement_history_ambiguous instead of inventing success. |
| Known placement, null/missing historical BIB | Marker is present with explicit null; distinguish from no marker. It never becomes editable merely because no BIB or HoldPlacementOperation exists. Include historical-event known BIB with current source BIB null, preserving that evidence without a lookup. |
| Genuinely never-placed closed suggestion | No marker merely for closed status, manual/rejected reason, BIB, or identifier-found tag. No false success or BIB protection. |
| Reopen, commit, then separate changed/cleared identifier or explicit-BIB edit | Reject for both known and unknown protected history, even from suggestion/pending_hold/outstanding_purchase after reopen; no event/tag/outbox/provider side effect. Marker survives both requests. |
| API capabilities and backend mutation | canEditIdentifier/canChangeBib/canRetryIdentifierCheck and the safe blocking reason agree with actual backend rejection after refresh/reopen. Missing runtime hold ID does not unlock editing; it only prevents uncorrelated terminal closure. |
| Repeated import into equivalent fresh targets | Identical protected/known/null/evidence/provenance counts and equivalent sorted metadata using frozen export time; multiple qualifying pieces of evidence and an equivalent retained marker still yield one marker per request, not duplicates. |
| Conflicting BIBs, dangling/unknown/conflicting taxonomy references | Explicit deterministic blockers and source references; do not choose arbitrary BIB or silently default/drop a canonical terminal reason. |

Assert reconciliation counts each evidence class (including all five reasons), unique protected requests, known/null BIBs, inserted/reused metadata, no-evidence requests and blockers. Reject an intentionally omitted/mutated marker/provenance result. Separately prove zero fabricated HoldPlacementOperation/provider-identity rows and that BIB protection is not treated as current terminal-hold correlation. F2 supplies runtime tests for those uncorrelated imports.

## 11. CI pipeline

At minimum on PR and push to main:

1. restore/build .NET 10 solution;
2. build/validate DACPAC;
3. start/provision SQL Server 2022 test DB;
4. run `Asap.Tests` including real-SQL categories;
5. install Node dependencies for tests only;
6. run jsdom/frontend tests;
7. install/use Playwright browser dependencies as required;
8. run targeted Playwright + accessibility suite;
9. verify publish artifact contains no PocketBase runtime and does not require Node/npm;
10. optionally perform static checks for committed secret/config artifacts.

Specialized CI may initially run on self-hosted Linux where existing infrastructure supports it; Windows runners can be introduced later if deployment-specific tests need them. Do not make Windows CI a prerequisite unless a test genuinely depends on Windows/IIS behavior.

## 12. Release build

Version tags produce:

- immutable app/DACPAC/deployment ZIP;
- separate self-contained `win-x64` migration ZIP from the same tag;
- checksummed manifest including DACPAC SHA-256;
- exact expected SchemaVersion.

Release/deployment tests must cover changed DACPAC with unchanged SchemaVersion, unchanged DACPAC with no dependency DDL (true file-only), unchanged DACPAC with required Hangfire DDL (database-changing), and inconsistent changed SchemaVersion with unchanged DACPAC (blocked). Section 10.1 R7 and `05-DEPLOYMENT-OPERATIONS.md` sections 9-10 govern backup/quiescence, compatibility, and failure ordering; application SchemaVersion alone cannot authorize restart/rollback.

Do not rebuild production from an untagged branch state after a rehearsal. If a fix changes code, create a new tag/artifact and rehearse that exact replacement.

## 13. Local F5 developer experience

A normal developer with:

- .NET 10 SDK;
- SQL Server Developer Edition;
- valid ignored `Development.local.json`;
- Entra redirect configured for local development;

should be able to F5 without manually running frontend build steps.

Debug/startup behavior may:

- create the missing local ASAP database;
- deploy the DACPAC;
- validate SchemaVersion;
- seed only structural/system defaults needed for a valid empty application.

Do not silently reset developer data if DACPAC reports possible data loss. Provide an explicit reset action/script. The normal reset recreates schema/defaults, not synthetic demo data.

Synthetic/demo seed/reset tooling is a separate explicit end-of-port utility.
