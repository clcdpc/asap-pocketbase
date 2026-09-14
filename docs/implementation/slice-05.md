# Slice 5: Background Workflows And Email Operations

## Preparation Only

Not dispatched. Refresh this packet after Slices 1-4 pass their complete tests,
Terra review/fix/re-review gates, and milestone commits. Do not start Slice 5
implementation early. The canonical sequence remains document 02; this is a
focused implementation packet, not a regenerated architecture or plan.

Astra Max refreshes this packet against accepted prior implementation and
normally dispatches fresh Luna High for the complete slice, including durable
queues, external-operation recovery and authorization-sensitive delivery.
[Document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) governs bounded Luna Max
escalation, optional context rotation with concise handoffs, compact evidence
receipts, and Astra-first specialist escalation; Sol remains advisory only.
After required tests, fresh Terra High independently reviews the whole slice.
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
desktop/mobile with serious/critical axe gates. Run existing relevant tests,
build/publish and artifact-exclusion checks before Terra Pass 1.

Return actual changed paths, commands/results and remaining concrete risks.
No commit, push, merge, tag, deployment or next-slice work by the implementer.
No completed-slice or production claim until the corresponding gates pass.
