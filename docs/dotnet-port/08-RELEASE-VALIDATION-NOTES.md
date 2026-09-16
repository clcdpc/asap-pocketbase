# Release Validation Notes

## Current Scope Notice - 2026-09-16

These notes preserve the deferred production/rehearsal and live-provider
contracts. They are not current reduced Slice 8 acceptance gates. The active
test-IIS repository contract is in
`docs/implementation/slice-08.md` and
`docs/implementation/test-iis-activation.md`; no production tag, host contact,
live provider validation or cutover is performed by that slice.

## 1. Purpose

Keep live Polaris validation separate from ordinary deterministic PR testing. This area was deliberately not expanded into a large release-engineering project during the port design interview; the decisions below are sufficient to guide implementation without blocking the core rewrite on additional policy discussion.

## Authorization-scope transition and inactive-library note

Authorization scope changes are lifecycle mutations. When an active StaffUser's new role/organization scope excludes libraries they previously could operate in, the StaffUser change, out-of-scope auto-claim-rule deactivation, open TitleRequest claim cleanup/events, and open AdditionalCopy claim cleanup/Notes occur atomically with administrative cleanup counts; closed history remains intact. StaffUser is also the serialization point used by claim/rule writers, so a relationship validated before a move/demotion/deactivation cannot commit afterward without revalidating current eligibility. Promotion to super-admin only broadens scope. Staff cookies retain the sign-in (`tid`,`oid`) tuple and revalidate exact equality with the current StaffUser and membership in loaded `AllowedTenantIds` every request, so explicit durable-identity rebinding invalidates old-identity cookies immediately. Patron bearer authorization independently rechecks the session's effective Organization on every request, and final session issuance serializes against library deactivation so a racing login cannot escape revocation.

`Organization.IsActive = false` pauses participation-dependent business work for that library: identifier checks, request-mutating hourly workflow phases, library-scoped manual Run Now actions, and ordinary staff/admin weekly summaries. The owning Organization row is the serialization point immediately before each participation-dependent local result/operation acquisition; deactivation-first prevents the later commit, while work whose local commit/acquisition won first may finish/reconcile without keeping SQL open across Polaris. Infrastructure operations continue. Already-committed immutable business-event outbox rows may drain; authorization-sensitive staff rows revalidate current scope immediately before delivery and suppress if that authorization has disappeared. Weekly summaries are authorization-scoped per recipient: library staff/admin receive only their own active library's counts/samples/links; super-admins may receive active-consortium scope. This intentionally corrects the pinned PocketBase shared consortium-wide summary behavior.

## Background schedule preservation note

External operational configuration is also a rehearsal/cutover parity gate. Capture current cron schedules plus global/timeout/queue-specific page-size/max-per-run values with provenance, map them into `Hangfire:Schedules`/`Hangfire:ProcessingLimits`, and verify effective target values before enabling recurring work. An explicit legacy `pending_isbn_checks` override must be reported because that hourly path is retired; it cannot silently disappear.

The target preserves the current hourly workflow as **one ordered Hangfire orchestrator**, not several independent same-cron jobs. `asap-workflow-processing` runs acquired-hold recovery (including inactive libraries) -> unreviewed-suggestion OutstandingTimeout / pending-hold / hold-pickup / additional-copy timeouts -> purchase promotion -> new hold placement -> fulfillment tracking in that order and cannot overlap with another scheduled/manual orchestrator run. This ordering is correctness-sensitive: an expired request closed by a timeout must not enter hold placement later in the same hourly run. HoldPlacementOperation acquisition follows Organization -> TitleRequest ordering and becomes a mutation barrier until terminal reconciliation; deactivation that wins the Organization lock first blocks acquisition.

Identifier/ISBN processing remains on the dedicated `*/5 * * * *` cadence but is an explicit behavioral consolidation. The pinned source's hourly `processPendingIsbnChecks()` and five-minute `processPendingSuggestionIsbnChecks()` differ. The target canonical processor uses the five-minute path's BIB persistence/reconciliation, workflow tags, `lastChecked`, and missing-identifier behavior while retaining the old path's bounded retry state. The target additionally corrects a dangerous source ambiguity: only a successfully completed zero-result Polaris search may become `not_found`; transient provider/network failures are retryable, auth/config/request/protocol failures are operational failures, and unknown failures remain failures rather than false catalog conclusions. Five transient attempts on fair revisits of the five-minute queue with no extra backoff is intentional; backlog means a row need not be examined every run; attempt five becomes `error_max_retries`, which is recoverable through the scoped **Retry identifier check** action. Migration exhaustively maps all legacy status values, including blocking legacy `found` without supporting BIB state and conditional handling/blocking for `error` and `found_in_polaris`. The hourly ISBN implementation is intentionally retired only after those useful semantics are carried forward. Weekly staff summaries remain Sunday at 20:00 in the configured business timezone. Ordinary runs are idempotent per recipient/reporting period. A manual forced execution creates one distinct durable `ManualRunId`; retries of that forced run reuse it, while a later explicit force creates a new business event and may resend.

## Staff notification-recipient normalization note

The target deliberately normalizes staff email ownership: `NotificationEmail` is the nullable primary ordinary-notification address and `WeeklyActionSummaryEmail` is an optional weekly-only override. A summary-enabled migrated user with no legacy weekly email but a real target `NotificationEmail` **will begin receiving weekly summaries** through the fallback; this is a desired target behavior change. Conversely, ordinary assignment/purchase/additional-copy reminder destinations may change because the target stops treating the legacy weekly-summary field as a general-purpose recipient. Migration/reconciliation must report old-versus-target recipients/eligibility per affected staff user before cutover. Administrators may clear `NotificationEmail`, and Entra sign-in never repopulates an intentionally null value.

## Durable-email closure note

The target outbox has `pending`, `sending`, `sent`, `failed`, and terminal `suppressed`. Authorization-sensitive staff messages persist recipient StaffUser/scope, durable identity tuple, and `RecipientAddressKind=notification_email|weekly_summary`; send/retry resolves the current address from that explicit rule and compares it with `ToAddress` rather than inferring semantics from `BusinessKey`. Stale staff/library authorization or a changed effective destination suppresses rather than sends the old snapshot. Every expired `sending` lease is potentially transport-ambiguous. The complete Postmark operation has a 30-second timeout, must start within 30 seconds of claim, uses a two-minute sending lease, and cannot be reclaimed/retried before lease expiry. Final worker writes compare status, expected `LeaseId`, and rowversion/equivalent ownership so a late old worker cannot overwrite a reclaimed attempt. Delivery remains explicitly at-least-once, not exactly-once. `failed` remains manually retryable and retains payload; only terminal `sent`/`suppressed` payload may be purged after 90 days. Missing required notification configuration does not roll back the owning business mutation: an undeliverable intent is suppressed at commit, while transport configuration disappearing after a valid enqueue produces a retryable failed row. Immutable business-event mail keeps the existing drain-after-commit semantics.

## Cutover-era PocketBase safety note

If authoritative PocketBase production needs a critical fix after the .NET merge but before cutover, use only a temporary branch/tag from its exact last deployed commit, follow the normal emergency process, immediately port the behavior into .NET `main`, and produce/rehearse the replacement exact .NET artifact. A schema, stored-data, migration-input, or migration-assumption change also requires updated migration tooling/contracts and a repeated full rehearsal. The permanent final-PocketBase tag is not allowed to preserve an earlier candidate: after successful cutover it must identify the exact PocketBase commit that was actually frozen. No permanent PocketBase branch is maintained, and temporary hotfix branches may be removed after cutover.

After .NET accepts production writes, the retired production PocketBase deployment must never be started as-is. Retention is forensic/reference only and direct database/file inspection is preferred. If executable investigation is unavoidable, release evidence must show that only a separate isolated copy can run, with outbound Polaris/email access blocked and all recurring production jobs disabled before startup; it can never be parallel read/write or fallback production.

## Staff/library lifecycle preservation note

`StaffUser.IsActive` and `Organization.IsActive` are independent lifecycle switches. A staff/admin row references one non-system library even when that library is inactive. Library deactivation blocks authorization and revokes patron sessions but does not mass-deactivate staff rows; library reactivation restores eligibility for StaffUsers already active. Explicit StaffUser reactivation is rejected while its referenced library remains inactive. This separation is a required migration and authorization invariant, not an optional cleanup.

## Identifier-state integrity note

The target `found` state requires a supporting nonblank `BibId`; inconsistent legacy found states block migration. Permitted pre-placement identifier edits atomically invalidate old result/check/error/retry/tag/BIB authority, then validate any explicit new BIB. Identifier changes/clears in `hold_placed`/closed or under retained placed-BIB protection are rejected without side effects; unchanged normalized values are no-ops. A completed hold journal does not remove protection, and positive checkout fulfillment remains tied to the actual placed BIB; terminal hold outcomes additionally require the authoritative tracked HoldRequestID. A legacy marker protects even an unknown BIB but does not create that runtime identity. See `07-API-FRONTEND-COMPATIBILITY.md` section 14.2 and the migration guard in `04-MIGRATION-CUTOVER.md` section 6.10.

## Final timeout, hold-correlation, and legacy-history note

OutstandingTimeout preserves the pinned source's unreviewed-suggestion auto-rejection: current `suggestion`, creation age strictly beyond the configured threshold, active participation, `closed/rejected`, and its configured rejection-email behavior. It does not expire outstanding purchases. Every timeout family's status/age/outcome/mail rule is explicit in porting-spec section 23.2 and stays independent of scan ordering.

Terminal unclaimed/cancelled/expired fulfillment intentionally differs from the pinned source: require the specific tracked final HoldRequestID, not patron+BIB alone; RequestGUID is conversation/recovery context, not an interchangeable final ID. Keep positive checkout title-level. A provider failure or missing/ambiguous association preserves state with diagnostics; manual closure and configured local timeout remain available.

Migration's existing legacy marker recognizes every normalized placement evidence class in migration section 6.10, including all five terminal reasons and ordinary status_changed adoption transitions. Preserve recorded/unknown BIB and deterministic provenance through separate reopen/edit requests. A protected imported history can remain uncorrelated at runtime; never fabricate an operation or provider ID.

## 2. Required deterministic gates

Before any release candidate reaches live validation:

- .NET build/unit/integration tests pass;
- real SQL Server 2022 tests pass;
- jsdom frontend tests pass;
- Playwright critical journeys pass;
- accessibility gate passes;
- DACPAC builds, SchemaVersion is coherent, and deployment tests classify both application and dependency-owned DB changes; unchanged DACPAC plus Hangfire DDL still requires backup/quiescence and both-schema compatibility;
- migration tests/reconciliation tests pass, including StaffUser tenant/object-ID mapping, preservation of staff relationships to inactive organizations, primary/weekly notification-recipient migration plus explicit recipient/eligibility delta reporting, intentional `NotificationEmail` clear/no-Entra-refill behavior, exhaustive legacy ISBN-status mapping/blockers (including `found` without BIB state), canonical imported found-state checks, all current StaffUser preferences, system/global runtime-fallback capture, external operational schedule/processing-limit parity, and an explicit pre-start active-bound-super-admin hard gate with deterministic migration-time bootstrap provisioning/promotion when required;
- real-SQL concurrency/idempotency tests pass for final-usable-super-admin serialization; StaffUser lifecycle vs claim/rule creation; Organization deactivation vs identifier/timeout/promotion/hold-acquisition/fulfillment/manual-run commits; HoldPlacementOperation acquisition vs every conflicting request mutation; and unique outbox business keys, conservative expired-lease recovery, stale-worker fencing, timeout/safety-boundary enforcement, suppression, and payload retention;
- recurring-job registration matches the authoritative schedule matrix/defaults/business timezone, uses one ordered hourly workflow orchestrator, and exposes the documented processing-limit precedence/effective values;
- identifier-processing tests prove definitive-zero-result versus transient/operational failure classification, five scheduled transient attempts/no extra backoff, retry-state resets, terminal manual recovery, that provider outages/auth/config errors never create `not_found`, and that permitted pre-placement identifier edits atomically clear old BIB/result/check/tag authority while protected-stage edits are rejected before downstream workflow can use it;
- staff/library lifecycle tests prove library deactivation leaves `StaffUser.IsActive` unchanged while blocking authorization, library reactivation restores eligibility for already-active staff, explicit StaffUser reactivation is rejected while its library remains inactive, and role/organization scope contractions atomically clean out-of-scope rules/open claims while preserving closed history;
- patron-session tests prove every authenticated request rejects inactive effective organizations, login/deactivation races cannot issue a usable post-deactivation token, and library reactivation never resurrects revoked sessions;
- inactive-library job tests prove no new participation-dependent Polaris/workflow mutation begins after deactivation while infrastructure jobs continue; previously committed immutable business-event mail may drain, while authorization-sensitive staff mail suppresses when current scope is gone;
- weekly-summary tests with multiple libraries prove staff/admin receive only their own active-library counts/samples/links, inactive-library staff receive no summary, and super-admin scope is limited to active participating libraries;
- staff-auth tests prove a cookie issued to Entra identity A is rejected immediately after the same StaffUser is durably rebound to identity B, while readable-profile refresh with unchanged tuple preserves the session;
- email tests prove authorization-sensitive queued staff messages suppress after recipient deactivation/move/demotion or library deactivation; `RecipientAddressKind` independently proves ordinary mail always follows `NotificationEmail`, an unchanged weekly override survives a primary-address change, a changed weekly override suppresses even when old `ToAddress` equals current primary, and a cleared weekly override falls back to current primary. The crash-before-call, provider-ambiguity, late-old-worker, and no-early-second-call lease cases in `06-TESTING-CI.md` section 10 pass; failed rows retain payload beyond 90 days and can be manually retried; terminal sent/suppressed payload is purgeable; missing mail/sender configuration never rolls back ordinary business state;
- migration fixtures prove source-active auto-claim rules with missing/inactive/out-of-scope assignees normalize inactive and are reported, while every imported active rule has an active scope-eligible assignee;
- AdditionalCopy lifecycle tests prove open claims are cleared on staff deactivation/scope contraction with Notes/admin-audit evidence while closed snapshots remain;
- pickup-preference tests prove successful updates change the request's current pickup fields, Polaris failure leaves them unchanged, stale/live conflicts return 409, and hold-operation conflicts obey the mutation barrier;
- forced weekly-summary tests prove ordinary recipient+period idempotency, a distinct forced resend, and retry idempotency within the same `ManualRunId`;
- self-contained `win-x64` migration artifact is validated on a representative old-server environment;
- environment-isolation validation proves production and permanent-nonproduction IIS app pools use different runtime domain principals, each principal is denied access to the other environment database, and neither runtime identity has deployment/schema/backup privileges;
- release artifact/manifest checks pass, including DACPAC SHA-256;
- cutover/rehearsal evidence records the exact PocketBase commit represented by migration and the .NET artifact, any post-merge emergency-fix propagation/rehearsal, the final tag at the successfully frozen commit, and the post-write prohibition against starting retired production PocketBase as-is;
- nonproduction recipient-domain safety tests in `06-TESTING-CI.md` section 4.1 pass with fake Postmark evidence as required by section 6 below.

These are mandatory and non-overridable in the normal release process.

For production-hostname preflight, use a disposable SQL database/config only. Preflight must validate real Entra (`tid`,`oid`) authorization and other host-dependent behavior without creating any runtime/bootstrap rows in the final PocketBase-migration target. Destroy the disposable database and keep the application stopped against the recreated final target until import/reconciliation completes.
### 2.1 Seven-finding closure release gate

All R1-R7 requirements in `06-TESTING-CI.md` section 10.1 must pass on the release candidate. Capture evidence of exclusive hold execution and safe operator-required ambiguity; post-placement identifier rejection with actual-BIB positive checkout and particular-hold terminal correlation; reopen/import claimant eligibility rather than mere FK validity; persistent-cookie tenant removal and zero-usable-admin configuration prevention; low-cap finite-cycle queue progress across restart/failure; and dependency-only DDL backup/quiescence/compatibility ordering. These are substantive deterministic gates and cannot be waived as live-provider availability problems.

Rehearsal must prove already-acquired hold recovery continues after Organization deactivation without granting any new acquisition, scoped manual execution cannot cross libraries, and capped timeout scans cannot let expired work advance. Migration must retain valid inactive-organization relationships and historical attribution while removing independently ineligible open claims. Runtime startup uses current loaded trust policy, not a historical ticket's successful decryption.

For the selected Polaris SDK/version, record which provider responses/status lookups authoritatively establish final success or final no-effect, which require the status-5 reply, and the exact durable GUID/qualifier context. The conservative fallback is already decided: absent authoritative proof, retain the barrier and require operator resolution; a live-test omission never enables replay. No undocumented negative-result or idempotency guarantee is assumed. Verify provider mutation retry/auto-reply behavior is disabled as required by the journal contract.

The exact Hangfire SQL package/schema assets and compatibility ranges must be locked in the release artifact. Test the actual upgrade path against a restored database before production. An unknown schema or failed upgrade leaves workers stopped until explicit compatibility verification/repair.


### 2.2 Final three-finding release gate

All F1-F3 tests in `06-TESTING-CI.md` section 10.2 are blocking in addition to R1-R7. Capture evidence for the 30-day suggestion/purchase contrast; strict injected-clock boundary, inherited/overridden timeout/template semantics, nonblocking mail, manual equivalence and stale-state races; old H100 versus tracked H200 across all three terminal statuses and provider ordering; new/adopted final-ID capture and unavailable-ID fallback; and exhaustive known/null legacy marker/provenance transformations, reopen-commit-separate-edit rejection, and deterministic fresh-target reconciliation. No final finding is deferred.

Record the actual selected Clc.Polaris.Api/provider contract for create/reply RequestGUID and final HoldRequestID separately. Verify any authoritative GUID-to-final-ID mapping with version-matched evidence, not field-name similarity or a same-BIB search. Where no supported mapping exists, the documented null-identity diagnostic/no-terminal-closure path must pass; it does not permit provider replay or invent a second journal. A successful placement without a final ID is distinct from an ambiguous mutation still requiring recovery. Do not waive deterministic identity/timeout/history tests as live-environment uncertainty.

Rehearsal output must include the explicit unique-marker/evidence/known-BIB/null-BIB/ambiguity counts from migration section 6.10, effective OutstandingTimeout system/library/template parity, and zero fabricated operations/IDs. Preserve all previously clean deployment, recovery, identity, outbox, fairness, and bootstrap gates without redesign.

## 3. Live Polaris release validation

A live PAPI validation gate is useful because ASAP depends on real Polaris behavior that mocks cannot completely prove.

Binding decisions:

- Validate **Polaris**, not live Postmark, as the external release gate.
- Target a designated disposable/test Polaris environment and exact configured host/canary fixture.
- Exercise the full set of PAPI methods ASAP relies on, including representative reads and mutations/reconciliation behavior. For hold tracking, verify final-ID capture on creation/adoption, separation from RequestGUID, exact-ID terminal responses, and the defined uncorrelated fallback against the selected provider version; synthetic old-same-BIB controls remain mandatory in ordinary CI.
- ASAP orchestration code should run against ephemeral/isolated SQL state while talking to real PAPI.
- Use dedicated Polaris fixtures/test patrons/records appropriate for mutation testing.
- The disposable Polaris environment is periodically/nightly refreshed; do not build elaborate per-run cleanup initially.
- If fixture state is unexpectedly dirty within the same day, fail/wait for refresh or require an explicit override rather than mutating arbitrary data blindly.

## 4. GitHub controls

Use a protected GitHub Environment for live Polaris credentials/approval as appropriate.

The live validation workflow should support `workflow_dispatch` against an **existing exact version tag**. If an operator overrides a live-validation warning/failure condition that policy permits overriding, require an explicit reason and record it in workflow metadata/output.

The release manifest/tag/commit tested must be the same release identity that is later deployed.

## 5. Blocking semantics

Deterministic tests + real SQL are mandatory. Live Polaris validation is intended as a release gate but is **overridable** for legitimate infrastructure/test-environment problems with explicit operator reason/authorization.

An override must not convert an actual product correctness failure into a passing release. It exists for conditions such as disposable Polaris fixture/environment availability when other evidence is sufficient and the operator consciously accepts the risk.

## 6. Postmark

The recipient-domain boundary in `01-PORTING-SPEC.md` section 14 is a blocking deterministic release gate, verified without a live Postmark send. Record passing evidence from `06-TESTING-CI.md` section 4.1: exact/case-insensitive domains, explicit-only subdomains, missing/empty allowlist suppression, malformed configuration rejection, all patron/staff/weekly-summary/Test email paths, send/retry checks, committed business state and preserved idempotency after suppression, and unrestricted production delivery under this predicate. Blocked intents must be terminal `suppressed` with `recipient_domain_not_allowed` and zero fake provider calls.

Verify the permanent-nonproduction external config sets `Environment.IsNonProduction=true` and lists only approved exact recipient domains. This application switch is independent of `ASPNETCORE_ENVIRONMENT=Testing`, which controls only testing authentication. Production does not apply the nonproduction list; sharing a Postmark server/token does not bypass the rule.

Do not make a live Postmark send a production-release blocker. Ordinary CI uses fakes; optional nonproduction live outbox/diagnostics/**Test email** checks use the same safety predicate and durable pipeline.

## 7. Future refinement

If live PAPI validation becomes flaky, expensive, or insufficiently isolated, refine the disposable Polaris fixture/environment process rather than weakening deterministic application tests or adding a temporary PocketBase-vs-.NET parity harness.
