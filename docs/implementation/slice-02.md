# Slice 2: Core Staff Workflow

## Gate And Ownership

Prepared during Slice 1 and now cleared for dispatch, not an implementation
claim. Slice 1's build/tests/end-to-end, full Terra review/fix/re-review and
remote CI gates have passed. Re-read the actual prior slice implementation
before extending it. The temporary FileEmailSender decision remains in force;
real Postmark is a release/rehearsal blocker only.

Fresh Sol XHigh owns implementation because Entra lifecycle, persisted cookies,
scope contraction and irreversible provider recovery have cross-cutting
correctness/security consequences. Fresh Terra High reviews the entire slice
twice in the same context, with Sol fixes and a third pass when required.

Dispatched to fresh Sol XHigh Raman,
`01a0987f-93e7-7c23-833f-476a49a7359c`. Parent owns execution documentation and
independent synthetic-source acceptance preparation; Sol owns application,
schema, migration, frontend, tests and CI. No Slice 1 implementation/reviewer
context is reused. Fresh Terra High will be created after completed handoff.

Prior application milestone: `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44`,
plus reviewed CI correction `1e36761c771db70d0b669087d0a843b66cf5618b`.
Slice 1 has 120 passing .NET tests, clean full Terra Pass 3/CI closure review,
and successful actual Linux CI run 34731687718. The earlier failed Linux run
34730692517 remains evidence, not an open gate. Pinned source and freshly
fetched `origin/main` remain `150b30b776565194260cc327eeeffdfb46475e81`.

## Objective And References

Deliver anonymous ASAP staff shell -> explicit Microsoft sign-in -> authorized
complete request queue -> profile/view/edit/claim/assignment/core workflow and
dedicated pickup actions with truthful events/email/recovery. Preserve the
existing vanilla/Grid.js experience. Additional-copy, administration settings,
full recurring jobs and analytics stay with their assigned later slices.

Read root AGENTS, `02-IMPLEMENTATION-PLAN.md` Slice 2, and focused contracts:

- `01-PORTING-SPEC.md` sections 4-7.6, 9-10, 14-15, 18-19, 21, 23 participation
  and timeout guards, 24-27. Hold protocol 9.1/9.2 is binding in full.
- `03-DATABASE-DESIGN.md` StaffUser, TitleRequest/events/tags, AdministrativeAudit,
  DeletedRequestAudit, outbox, HoldPlacementOperation and mapping constraints.
- `04-MIGRATION-CUTOVER.md` strict staff map/bootstrap/recipient deltas, 6.3
  map-then-eligibility and 6.10 exhaustive retained placement evidence.
- `06-TESTING-CI.md` SQL/auth/provider/browser/accessibility and R1/R2/R5/F2/F3
  cases relevant to interactive recovery and protected reopened records.
- `07-API-FRONTEND-COMPATIBILITY.md` sections 4-12, 14.1-14.4 and 16-17.
- `05-DEPLOYMENT-OPERATIONS.md` external-config tenant-policy activation/startup
  and persistent certificate-protected keys. No deployment lifecycle rewrite.
- `10-CODEX-MULTI-MODEL-TASK.md` roles, fixed slice sequence and review gates.

Exact PB pin: `150b30b776565194260cc327eeeffdfb46475e81`. Use
`staff-source-notes.md` for the inspected route/helper map, then read actual
pinned source for each ported behavior. No per-staff Polaris identity,
password-override auth, mutable-UPN binding, or PB SDK in the target.

## Required Contracts

### Existing Slice 1 Boundaries

Read the accepted milestone's actual files before editing; the final milestone
SHA and test count are supplied at dispatch, not inferred from this preparation.

- `Infrastructure/Data/AsapDbContext.cs` and `DomainEntities.cs` already model
  Organizations, StaffUsers/preferences, requests/events, relational settings,
  formats/rules, patron sessions and outbox. Extend the DACPAC-owned schema and
  exact startup version together; do not add EF migrations or parallel models.
- `Features/Patron/PatronSessionService.cs` hashes opaque token bytes, serializes
  final session insertion on the effective Organization, and checks current
  activity. Integrate library lifecycle revocation without replacing the patron
  session/authentication mechanism with staff cookies.
- `Features/Email/IEmailSender.cs` is the narrow cancellable readiness/send
  transport boundary. Its envelope includes outbox ID, owning Organization,
  business key and sender/recipient/content snapshots. Keep the temporary file
  implementation behind this boundary and ordinary tests on recording senders.
- `Features/Email/EmailOutboxJobs.cs` already owns delivery claims, deadlines,
  fencing and sensitive-recipient checks. Staff authentication/lifecycle must
  share the same current eligibility semantics; preserve these real-SQL
  regressions when extracting only the common predicate needed by this slice.
- `Asap.Migration` already exports, validates, imports and reconciles owned
  records, assets, both frozen configuration artifacts, Entra mappings and
  legacy placement markers. Extend its explicit maps and semantic comparison,
  including negative package-binding/value-drift tests, rather than introduce
  a separate migration path. Exercise the actual newly published native CLI.
- `tests/Asap.Tests/Integration/PatronJourneyTests.cs` owns real SQL/Kestrel and
  the checked-in browser runner. Preserve all existing desktop/mobile states
  and authentication/configuration race tests when adding staff coverage.

### Slice Requirements

1. Entra multi-tenant OIDC validates issuer and allowed tid/oid; local allowlist
   is authoritative. Empty-table-only startup bootstrap creates the configured
   exact identity; no JIT account/UPN inference/populated-table repair. Preserve
   protected persistent cookies, secure local returnUrl, approximately eight
   hour sliding session, explicit sign-in, JSON API 401/403 and antiforgery.
   Only Testing registers test authentication; Development uses real Entra.
2. One current eligibility predicate rechecks original ticket tuple, current
   binding, loaded tenants, active account, role/Organization and required
   participation on every protected request, including session/diagnostics.
   Sign-in may refresh readable UPN/display, never NotificationEmail. Invalid
   identity expires its cookie and returns 401; valid identity lacking scope
   receives 403. Persistent keys do not preserve removed-tenant authorization.
3. Preserve all five user preferences through SQL/migration/profile DTO/API/UI.
   Ordinary profile cannot edit primary email. Authorized metadata PATCH can
   explicitly clear NotificationEmail; weekly override clear resumes fallback.
   Reject placeholder destinations and keep sensitive outbox kind/tuple/address
   revalidation. File output does not weaken any of those checks.
4. Implement lifecycle service/API contracts needed for core authorization and
   tests even though administration UI is Slice 4. Deactivate, do not hard-delete
   staff. Role/library contraction atomically cleans invalid active rules/open
   title claims with events/audit counts while preserving closed history.
   Extend that same service for AdditionalCopy when Slice 3 introduces it; no
   fake task table/event subsystem now. Rebinding is explicit/confirmed/audited.
   Library deactivation never changes StaffUser.IsActive; reactivation restores
   eligibility but not old claims/rules. Explicit account reactivation while
   its library is inactive conflicts. Promotion to super-admin expands scope.
5. Serialize final usable-admin reductions with transaction-owned exclusive
   `ASAP:ActiveSuperAdminInvariant` before row locks. Re-read loaded-tenant
   eligibility; bounded lock timeout/invariant violation is a 409, never a
   bypass. Candidate configuration validation rejects zero usable admin before
   activation. Direct configuration bypass fails startup closed with no business
   endpoints/workers; liveness and safe repair diagnostics remain available.
6. All multi-category writes lock Organization -> StaffUser -> TitleRequest ->
   dependents with stable ordering. Claim/rule writers lock/re-read target staff
   before establishing a relationship. Stored-claim activation uses current
   identity/scope with requireParticipation=false; acting on work separately
   requires active participation. No transaction spans provider calls.
7. Request API returns explicit scoped DTOs and complete authorized rows for
   existing client filters/Grid.js. Super-admin workflow defaults to All each
   visit, selected library is temporary, and stale loads never replace newer
   auth/status/scope results. Preserve deep links and available mapping
   normalization. No server paging/cache/framework expansion.
8. Every mutation accepts expected rowversion and returns new version/current
   capabilities. Preserve source action/claim/final-outcome email semantics,
   immutable patron snapshots, notes versus committed audit distinction,
   canonical transitions and atomic local event/tag/outbox changes. Refresh on
   409 without replaying intent. Closed-request deletion preserves reduced audit
   and respects the incomplete-operation barrier/history rules.
9. A single backend identifier/BIB capability predicate validates starting and
   proposed state. Incomplete operation blocks first. Legal pre-placement
   identifier change atomically invalidates old BIB/result/retry/error/check and
   derived tags; omitted/normalized unchanged input is a no-op. New explicit BIB
   needs normal provider validation, not inherited authority. Placed/closed and
   successful/legacy-protected reopened records reject changes, including null
   protected BIB and separate reopen-then-edit requests. Safe retry action is
   eligible suggestion+nonblank+error_max_retries only; use the canonical
   processor, never a second placed/closed lookup pipeline.
10. Dedicated pickup action checks version/barrier before Polaris, revalidates
    live selection/changed-since-load, commits snapshot/event only after success,
    and does not expose these fields through generic editing. Hold placement
    re-reads live pickup; a preexisting permitted in-flight idempotent pickup
    update can finish under the pack's precise interleaving contract.
11. Implement the complete existing HoldPlacementOperation protocol for every
    interactive placement/adoption: one incomplete row, unique attempt number,
    owner token/epoch/two-minute lease, thirty-second heartbeat, sixty-second
    provider-call cap, fenced monotonic dispatch/reply/result boundaries, and
    exact status-5 answer 1/state 3 reply context. Disable hidden mutation
    retries/auto-replies. A marked uncertain call is never replayed after lease
    expiry. No in-memory lock or Boolean provider success substitutes for SQL
    ownership and durable final evidence.
12. Recovery safely continues acquired/reply_ready/result_recorded work including
    acquired inactive-library work; uncertain marked operations observe only and
    become operator_required within the specified evaluation bound. Implement
    usable scoped recovery diagnostics and super-admin Reconcile/Resolve actions
    with current authorization/version/evidence and quiescence requirements;
    no force-retry/assumed no-effect route. Full scheduling/fair scans are Slice
    5, but this slice cannot strand an operation without its safe recovery path.
13. Final HoldRequestID is distinct from RequestGUID/qualifiers/BIB. Runtime
    adoption retains a proven live identity in the same journal without fictive
    dispatch markers. Proven final success may complete with null ID and safe
    uncorrelated diagnostic; never replay for an ID or guess one. Identity-only
    enrichment obeys latest-successful-operation/version/current-null fencing.
    Preserve exact evidence for Slice 5 fulfillment; historical protection is
    not a provider ID or artificial incomplete barrier.

## Migration And Acceptance

Extend Slice 1's real exporter/importer/reconciler alongside owned data. Active
staff require explicit operator tid/oid mappings; readable identities never
authorize. Preserve preferences/contacts/history and report deterministic
old-versus-target weekly eligibility and every ordinary recipient change.
Map claims then validate current account/trust/scope independently of inactive
library participation; preserve valid and closed historical attribution and
report each cleared operational claim. Missing identity mappings remain blockers.

Hard-gate at least one usable super-admin after import; explicitly provision/
promote configured bootstrap in the stopped migration when needed and report
it. Preserve exhaustive legacy placement marker/evidence including all terminal
reasons and enter/leave placed transitions; never fabricate provider operations
or identities. Reconcile equivalent fresh-target imports deterministically.

Tests include real persisted-cookie restart/tenant removal/rebind behavior;
antiforgery/local-returnUrl/scope; primary contact clearing and all preferences;
two-final-admin race; lifecycle versus claim/rule race in both orderings; active
library session behavior retained from Slice 1; version conflicts/atomic actions;
all interactive journal crash boundaries/lease fencing/reply recovery/adoption;
identifier stage matrix and reopened known/null historical protection; safe
pickup interleavings; email snapshots/current sensitive recipient suppression;
migration negative/reconciliation cases. Use real SQL and deterministic provider
fixtures. Browser gate exercises sign-in shell, profile, authorized queue scope,
deep-link/view/edit/claim/action, stale conflict and blocked recovery states at
desktop/mobile with serious/critical axe and focus/keyboard assertions.

Do not commit/push/tag/deploy. Astra independently integrates/verifies, obtains
Terra full-slice passes and records evidence before the milestone and Slice 3.
