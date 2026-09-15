# Slice 5: Background Workflows And Email Operations

## Refreshed Implementation Packet - 2026-09-14

The user has authorized complete Slice 5 implementation, all pre-review gates,
independent Terra review and necessary fixes/re-review, Astra acceptance, one
coherent milestone commit/push, and successful remote CI for that exact SHA.
Do not stop at a review candidate. Stop after accepted Slice 5; Slice 6 must not
start. PR #264 remains draft, with no merge, tag, deployment, rehearsal or cutover.
The canonical sequence remains document 02; this packet does not replace it.

### Verified Starting Boundary

- Repository/checkout: `clcdpc/asap-pocketbase`, existing
  `C:\Users\mfields\code\asap-pocketbase`, branch `codex/csharp-port`.
- Starting local HEAD, fetched branch and PR head all equal
  `ae706931da09d2781add4b96bbca9cfc4f768011`; the worktree was clean.
  No later commits or unexpected Slice 5 implementation exist.
- Accepted Slice 4 milestone `417c72430652a35bc8fc1da549ae270eabc86429`
  remains an ancestor. Its exact-SHA CI is recorded in the existing acceptance
  record; Slices 0-4 are accepted and are not being re-reviewed.
- Current-head [CI run 34881551700](https://github.com/clcdpc/asap-pocketbase/actions/runs/34881551700)
  completed successfully. PR #264 is open, draft, mergeable and based on main.
- Fetched `origin/main` equals behavioral pin
  `150b30b776565194260cc327eeeffdfb46475e81`; no intervening PocketBase
  changes require propagation. The deployed production SHA remains unverified.
- App and migration expected schema version: `5`; migration contract:
  `slice-05`. Starting schema version was `4`; Slice 5 advances it to `5`.
  The Slice 5 DACPAC must be freshly published after the QueueProgress schema
  addition. DACPAC owns `[asap]`; pinned Hangfire 1.8.25 owns its separate
  schema.
  QueueProgress uses the prescribed scalar composite key `(QueueName,
  ScopeOrganizationId)` shape; `ScopeOrganizationId=1` is the all-libraries
  scope, `CycleMaxId` is nullable with zero representing an empty cycle, and
  rowversion fences checkpoint updates.

### Existing Implementation To Extend

- `Infrastructure/Jobs/WorkflowProcessingGuard.cs` owns distributed resource
  `ASAP:WorkflowProcessing` through the selected Hangfire storage. Reconcile and
  operator resolution already use it. Preserve that resource and avoid nested
  acquisition when the hourly orchestrator invokes the existing services.
  Other logical jobs need one common guard across scheduled/manual scopes.
- `HangfireWorkerHostedService` currently registers only the outbox sweep and
  starts the `asap-email` worker queue. Expand it to the authoritative seven
  schedules, business timezone and required execution queues. Startup schema,
  initialization and usable-staff checks remain in force; no runtime DDL.
- `Features/Staff/HoldPlacementService.cs` already implements interactive
  acquisition, SQL-clock owner/epoch/lease, heartbeat, durable create/reply
  phases, result completion, adoption, explicit reconciliation/operator
  resolution, and proven null-to-final-ID enrichment. Add background entry
  points to that lifecycle, including inactive-library acquired recovery.
  Preserve `hold-resolution-operator-evidence.md` and the accepted S2-T1 ruling.
- `TitleRequestMutationService`, `AdditionalCopyService`, `StaffLifecycleService`
  and `StaffEligibilityService` provide current actor/participation/claim
  serialization. Use Organization -> StaffUser -> request/task -> dependent rows
  when needed; QueueProgress updates come last. Do not create synthetic staff
  authorization for scheduled work or hold SQL transactions across providers.
- `PatronSuggestionService.ProcessIdentifierLookupAsync` and
  `IdentifierLookupJobs` already supply the immediate identifier path. Complete
  one canonical implementation, preserving callers and reset/tag semantics.
  `IPatronProvider` currently spells the successful zero-result outcome
  `NotFound`; the Slice 5 boundary must expose `DefinitiveNotFound` as specified.
  Audit aggregate search failures and operational-stop propagation.
- `PolarisPatronProvider` implements `IPatronProvider`, `IStaffPolarisProvider`
  and `IPolarisReferenceProvider` using pinned `Clc.Polaris.Api` 4.0.0-beta.3.
  Current staff boundary includes BIB validation, patron hold snapshots,
  create and reply. Extend only the provider evidence needed for checkout and
  exact-hold tracking; inspect the installed package when typed results omit it.
  No automatic GUID-to-final-ID recovery guarantee is established.
- `Features/Email/EmailOutboxJobs.cs` already claims/delivers, sweeps due work,
  reclaims expired leases, fences completion, applies domain protection and
  validates sensitive staff tuple/address kind. Keep its five states and
  complete timing/retry/manual operations/retention and lifecycle-race coverage.
  `RecipientDomainPolicy`, `PatronEmailTemplateRenderer`, `IEmailSender` and
  `IEmailOutboxDispatcher` are the existing boundaries; extend their callers.
- `AdministrationService`/endpoints provide scoped typed settings, organization
  refresh, participation changes and patron-code reference operations. Existing
  vanilla `Frontend/staff` has settings/profile/request and hold-resolution UI.
  Add usable scoped Run Now, queue/recovery diagnostics, failed-email inspection
  and Retry, durable Test email, and protected Hangfire dashboard access.
  Preserve shared requests, stale-scope guards, safe DOM and focus behavior.

### Migration And Evidence Starting Point

`Asap.Migration` already exports frozen source packages, imports/reconciles
processing fields and historical delivery audit, enforces identifier/placed-BIB
evidence, reports recipient deltas, and validates effective runtime/operational
configuration. `MigrationOperationalConfiguration` checks four source schedules,
eight mapped queues, global/timeout precedence and retired hourly ISBN overrides.
Complete Slice 5 gaps within these files; preserve deterministic transforms and
prove empty target Hangfire/session/QueueProgress/operation/outbox runtime state.
Do not reimplement the accepted migration framework.

Retain existing ignored fixtures, source snapshots, harnesses and receipts:

- `.git/asap-slice-04-candidate.ps1`, `asap-slice-04-final-fixtures.ps1`,
  `asap-slice-04-final-browsers.ps1`, `asap-slice-04-verification.cjs`;
- `.git/asap-real-pb-source`, `.git/asap-admin-migration-acceptance`,
  `.git/asap-staff-migration-acceptance`, `.git/asap-patron-browser-probe`;
- `.git/asap-slice-04-runtime-oracle.cjs` and the existing configuration oracle;
- `.artifacts/slice-04-candidate-corrective-20260914d` and corresponding
  `.git/asap-slice-04-*-corrective-20260914d` receipts.

The accepted prior evidence is 234/234 .NET tests (146 integration, 88 unit;
19 migration cases), zero skips; 172 Node files; fresh Web/native publications;
four native fixture/oracle runs (161/164/3/3 checks); and 12 published-browser
modes (103 states and 14 CSP cases). These are prior-slice baselines, not Slice 5
results. Adapt retained harnesses under fresh Slice 5 names without overwriting
historical evidence; run the complete required gates for final candidate bytes.

The only approved temporary transport substitution remains `FileEmailSender`
at the final provider boundary. Real cancellable Rest 3-compatible CLC Postmark,
webhook/provider and release/rehearsal checks remain explicitly deferred release
requirements, not Slice 5 blockers or permission to fake provider delivery.
Analytics and all other later-slice work remain outside this implementation.

Astra Max refreshes this packet against accepted prior implementation and
normally dispatches fresh Luna High for the complete slice, including durable
queues, external-operation recovery and authorization-sensitive delivery.
[Document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) governs bounded Luna Max
escalation, optional context rotation with concise handoffs, compact evidence
receipts, and Astra-first specialist escalation; Sol remains advisory only.
After the complete pre-review gates below, fresh Terra High independently reviews
the whole slice unless the user directs a review-candidate stop under document 10.
A clean Pass 1 needs no ceremonial full Pass 2; fixes receive focused re-review
unless document 10's full-review triggers apply. Astra verifies acceptance,
commits/pushes the milestone and requires actual remote CI success for that exact
commit before dispatching the next slice. Prior exact-milestone CI must be green
before this slice is dispatched.

## Objective And Prior Contracts

Complete all prescribed background processing and email operations using the
earlier slices' workflow, hold-operation, identity, configuration and outbox
services. Preserve the four-project solution, direct EF/targeted SQL, operator
owned dependency schemas, vanilla frontend, and scoped administration. Do not
add another operation journal, job-run framework, scheduler, or messaging
hierarchy. Reuse tested prior services rather than independent job copies.

Before dispatch, record the actual prior milestone SHAs, implemented services,
schema version, migration capabilities, and outstanding nonblocking findings.

Slice 2's S2-T1 correction introduces the concrete shared
`Infrastructure/Jobs/WorkflowProcessingGuard.cs` for current reconciliation and
operator resolution, using the selected Hangfire SQL storage. After that slice
is accepted, reuse its exact guard/resource for the orchestrator and relevant
Run Now/recovery actions; do not add a competing lock or bypass the existing
operation owner/epoch/lease checks. Read `slice-02-review.md` and
`hold-resolution-operator-evidence.md` for the explicit operator-verified
evidence/executor-exclusion boundary. A guard or requested cancellation is not
proof that provider work or a superseded executor has ended.

## Required Reading

- Root AGENTS and `10-CODEX-MULTI-MODEL-TASK.md` execution/review gates.
- `02-IMPLEMENTATION-PLAN.md` Slice 5.
- `01-PORTING-SPEC.md` 7.6, 9.1-9.2, email/outbox 13-14, 20.1, 23-24,
  especially the entire 23.1 finite-cycle and 23.2 timeout contracts.
- `03-DATABASE-DESIGN.md` request/operation, outbox, recipient, QueueProgress
  and administrative tables; `13-SETTINGS-SCOPE-INVENTORY.md` effective
  workflow/email/participation ownership.
- `04-MIGRATION-CUTOVER.md` identifier/placement evidence, current claim and
  recipient normalization, both effective configuration artifacts and empty
  runtime infrastructure rules.
- `06-TESTING-CI.md` real-SQL/outbox/provider/browser sections, all applicable
  R1-R7 and F1-F3 cases; `14-REMEDIATION-AUDIT.md` mapping.
- `07-API-FRONTEND-COMPATIBILITY.md` identifier stage matrix, operations,
  concurrency/auth errors, UI capabilities and legacy-link behavior.
- `temporary-email-transport.md`: only final provider transport is substituted.

Read the executable pinned files at
`150b30b776565194260cc327eeeffdfb46475e81`: `pb_hooks/main.pb.js`, `lib/jobs.js`,
`lib/job_queue.js`, `lib/job_routes.js`, every `lib/jobs/*.js`, configuration
cron/queue resolvers, mail/template/transport helpers, relevant Polaris
hold/checkout/BIB helpers, operations UI and matching tests. Use
`background-source-notes.md` and `staff-source-notes.md` only as reading aids.
Inspect installed CLC executable/package source whenever typed results omit
required evidence; do not write parallel PAPI signing/protocol machinery.

## Observable Acceptance

1. Register exactly the seven prescribed recurring schedules in the configured
   business zone. One hourly orchestrator executes acquired-hold recovery,
   four timeouts, promotion, new placement, fulfillment in that order. The
   five-minute identifier processor is separate. Scheduled/manual/scoped work
   shares each logical job's non-overlap guard; duplicate runs are rejected.
2. Implement scalar QueueProgress per logical queue/scope with fixed ID cycle
   watermark, immutable creation/ID ordering, rowversion fencing, outcome
   before checkpoint, cap/restart/tail-wrap/deletion/backdated-insert handling.
   Eight configured queues plus HoldRecovery reuse existing bounds. Recovery
   has its own HoldPlacement-sized budget; no new setting or self-enqueue loop.
3. Canonical identifier outcomes are Found, DefinitiveNotFound, TransientFailure
   and OperationalFailure. Retain BIB/reconciliation/tags, exact missing/reset
   rules, five scheduled transient evaluations without extra backoff, safe
   operational failure stop and scoped terminal retry. No failed required
   search becomes definitive not-found. Stage-aware edits cannot erase placed
   BIB protection after reopening or bypass it through combined changes.
4. Complete leased/fenced hold stages, durable one-way create/reply markers,
   provider context and final identity, bounded observation/recovery, and
   operator resolution. No uncertain/empty lookup permits another create.
   Already-acquired inactive-library recovery continues; new acquisition does
   not. The incomplete operation remains the mutation barrier everywhere.
5. All four timeout families use their exact current status and age timestamp,
   strict injected-clock calendar-day/DST boundary and current locked settings.
   OutstandingTimeout affects suggestions only and uses its configured
   rejection mail. The other families send no timeout mail. Later phases skip
   applicable timeout-due rows even when a bounded timeout scan did not reach
   them. Scanning/checkpoints never modify age timestamps.
6. Fulfillment preserves positive title-level checkout. Unclaimed/cancelled/
   expired closure requires exact tracked HoldRequestID and expected BIB/patron;
   missing/ambiguous evidence or required provider failures preserve state.
   Independent manual/timeout closure remains available under its own rules.
7. Weekly summary counts, samples and links obey recipient current authorization
   scope. Normal recipient/period keys are idempotent; one durable ManualRunId
   distinguishes each explicit forced invocation and is reused by its retries.
   Store RecipientAddressKind and original identity tuple, and revalidate the
   correct effective address and allowed-tenant/binding/role/activity/scope.
8. Complete failed-email inspection/retry, domain protection at intent and
   every send/retry, timeout/lease/fencing recovery and at-least-once behavior.
   Failed retains payload; only terminal sent/suppressed content is purged
   after 90 days. Missing optional mail configuration does not undo business
   state. Immutable committed patron mail can drain after deactivation;
   sensitive staff mail revalidates. Manual Test email uses the same protection.
9. Complete session cleanup, organization/patron-code refresh, scoped Run Now,
   super-admin-only Hangfire dashboard, and useful scoped queue/recovery/email
   diagnostics. Infrastructure continues for inactive libraries. No secret,
   recipient PII or raw patron payload is logged.
10. Retain the minimal cancellable file sender only at final transport until
    the real maintained Rest 3-compatible CLC Postmark adapter is available.
    Generated previews remain ignored and excluded from app/CI artifacts.
    Do not implement fake provider webhook/delivery behavior. Provider-specific
    work and transport/release tests remain explicitly incomplete release and
    rehearsal gates; this exception does not waive any SQL/application test.

## Migration And Tests

Keep actual export/import/reconciliation current for request processing state,
exhaustive legacy ISBN/placement evidence and historical delivery audit. Freeze
effective old schedules and global/timeout/queue limit provenance; reconcile
target external JSON and explicitly report configured retired hourly ISBN
overrides. Do not import Hangfire state, sessions, cursors, operations, or
pending outbox work. Reconcile recipient eligibility/address differences.

Require real SQL concurrency tests for queue checkpoints/cycles, both
participation race orderings, stale workers, lifecycle/recipient races, forced
summary idempotency and operation barriers/recovery. Fake provider tests must
exercise every irreversible-boundary failure, not merely happy paths. Include
strict clock/DST, timeout cap, exact tracked-hold identity and aggregate search
failure cases. Browser-check operations/email/retry and protected controls on
desktop/mobile with serious/critical axe gates.

## Pre-Review Validation

Before Slice 5 is `implemented / ready for independent review`, all of the
following applicable gates must pass against the final candidate. Focused tests
alone do not satisfy this boundary:

- Clean Release build with zero warnings/errors.
- Complete .NET test suite and complete real-SQL integration suite, with zero
  unexplained skips.
- Complete Node/frontend test suite.
- Fresh Web publication and fresh self-contained `win-x64` migration publication.
- Required native migration/export/import/reconciliation fixtures and oracles.
- Required published-browser journeys, applicable desktop/mobile checks, and
  the serious/critical axe accessibility gate.
- Application artifact/exclusion checks and required DACPAC/source/artifact
  verification.
- `git diff --check`.

Retain full detailed logs as evidence and return compact command/result/count/
hash/path/warning receipts under document 10. Preserve the detailed tests above
and the established fake-provider/real-SQL distinction. Use the existing Testing
browser-auth boundary and retain required real-cookie/OIDC regression coverage;
these gates add no live Entra browser authentication or live Postmark requirement.
The documented temporary transport and later real-provider release gates remain.

At the user's direction, Astra may push `WIP Slice 5 review candidate` at this
boundary. `PORT-STATUS.md`/handoff material must say
`implemented / ready for independent review` and state that Terra has not run.
The candidate is not an accepted milestone; its CI cannot authorize Slice 6 or
replace the accepted milestone's own exact-SHA CI. Resume with document 10's
independent Terra review, fixes and Astra acceptance before creating the accepted
milestone.

Return actual changed paths, commands/results and remaining concrete risks.
No commit, push, merge, tag, deployment or next-slice work by the implementer.
No completed-slice or production claim until the corresponding gates pass.
