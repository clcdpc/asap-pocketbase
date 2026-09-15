# Slice 6 Thin Autonomous Supervisor Handoff

## Checkpoint

- Repository: `clcdpc/asap-pocketbase`.
- Integration branch: `codex/csharp-port`; final draft port PR: [#264](https://github.com/clcdpc/asap-pocketbase/pull/264).
- Slice branch: `codex/slice-06-analytics`.
- Slice PR: [#265](https://github.com/clcdpc/asap-pocketbase/pull/265), draft
  into `codex/csharp-port`.
- Accepted Slice 5 PRODUCT milestone: `36727414d02cbe34ba13cd3f6f1bb57980b83a6f`.
- Original bootstrap branch base: `1c3b46d13a42bb1a2887136385f3dce18b610c8c`.
- Original bootstrap head: `df80ec821b0cde9309e1e4d765d736ed832f48b3`.
- Current policy/branch base after rebase: `5493efee9ce140d6eaa83da2d178e2004fea7c2c`.
- Rebased bootstrap commit: `1f6886ff1428ff87269e9534b7cd4e5edad70fe8`.
- Final synchronization head: recorded in [PR #265's policy synchronization
  receipt](https://github.com/clcdpc/asap-pocketbase/pull/265); a commit cannot
  embed its own hash. Verify that receipt against the current branch before
  dispatch; the original bootstrap anchors above remain historical evidence.
- Topology: No child packages — one Slice 6 integration PR. Aggregation,
  DTOs, UI and fixtures form one tightly coupled behavioral contract.
- Current application/schema: .NET 10, `SchemaVersion.ExpectedVersion = 5`;
  migration `ExpectedSchemaVersion = 5`, `ContractVersion = "slice-05"`, export
  format `1`. No new application release version is set.

The accepted product and both policy-base anchors are intentional: intervening
changes are documentation/process policy only. Bootstrap is complete and must
not be repeated. Analytics implementation has not started. No supervisor,
implementation or independent review was dispatched during synchronization.

## Objective And Reuse

Implement the existing scoped Analytics endpoint and browser view through
server-side SQL aggregation. The [Slice 6 packet](slice-06.md#current-reuse-map)
contains the concrete file map and unchanged acceptance contract. Follow
[document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) for execution gates.

| Surface | Existing contracts to reuse |
| --- | --- |
| Auth, `src/Asap.Web/Features/Staff/` | `StaffAuthenticationRegistration.AddStaffAuthentication`, `StaffClaims.TryRead`, `StaffIdentityEvidence`, `StaffEligibilityService.EvaluateAsync`/`FindByBindingAsync`, `StaffRoleRequirement.Any`, `StaffCurrentUserMiddleware`, `StaffAuthenticationEndpoints.RequireCurrentStaff`, `CurrentStaff` |
| Scope, same directory | `TitleRequestViewService.ListAsync`/internal `CanAccess`; private `AdditionalCopyService.ResolveScope`. Common staff eligibility plus these list-scope semantics; there is no public generic scope resolver. |
| Data, `src/Asap.Web/Infrastructure/Data/` | `AsapDbContext`, `DomainEntities.cs`: `TitleRequest`, `AdditionalCopyRequest`, `Organization`, `StaffUser`, `TitleRequestEvent`, `WorkflowTag`, `TitleRequestWorkflowTag` |
| Clock/config | Injected `TimeProvider`; `ExternalConfiguration.Application.BusinessTimeZone`; `TimeZoneInfo` conversion, not timeout age arithmetic |
| Frontend, `src/Asap.Web/Frontend/` | `staff/js/http.js`: `authorizedJson`, session callbacks, `latestLoads`, `isAbortError`; `shared/http.js`: `requestJson`; `shared/latest-load.js`: `createLatestLoad`; `staff/js/workflow.js`: `createWorkflowApp`, local scope/auth/view state, `switchView`, `element`, `announce`, `loadOperations` |
| Real SQL/auth fixtures | Partial `PatronJourneyTests`: `Initialize`, `CreateApplicationFactory`, `SeedLibraryAsync`, `UpsertTestOrganizationAsync`, `AddTestingStaffHeaders`, `ProtectStaffCookie`, `CookieClient`, `ReadConfiguredSuperAdminAsync`, `MutableTimeProvider`; `StaffCorrectiveJourneyTests.cs`: `CreateCorrectiveStaffAsync`, `ReadCorrectiveStaffAsync` |
| Browser | `StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers`, `SeedStaffBrowserStateAsync`; `tests/browser/staff.cjs`: `createContext`, `scan`, `delayNextServerResponse`, keyboard/focus and desktop/mobile journeys |
| Migration | `MigrationPackageExporter`, `MigrationImporter` request/event/tag imports and `NormalizeIsbnStatus`, `MigrationReconciler`; `MigrationCliTests.CreateMinimalPackage`/`DeployDacpac` |

Helpers described as local/private are reusable in their existing controller
or partial fixture, not public APIs to import from a new module. Keep the
current request/state/testing mechanisms; any small sharing needed stays
within the existing feature boundary.

## Critical Invariants

- Every request rechecks durable `(tenantId, objectId)`, allowed tenant, active
  StaffUser, role and active actor Organization. Staff/admin scope is their
  own library even with a forged selection; super-admin supports selected/all
  scope. Client scope is never authorization evidence. Apply the authorized
  predicate inside every SQL aggregate and event/tag subquery.
- Combine request populations with type-qualified IDs and each row's
  `LibraryOrganizationId`. Copies do not inherit title events/tags; their open
  display stage is `additional_copies`, closed reason `unrecorded`.
- Current stages/open/aging/exceptions ignore reporting range. New suggestions
  use combined `CreatedUtc` in range regardless of status; closed counts use
  current closed state and `UpdatedUtc` in range, including copies.
- Find the earliest literal `hold_placed` event across all history, then count
  it only if that first timestamp is in range, even after closure. Exclude
  `hold_skipped`, adoption-only `status_changed` and
  synthetic legacy protection. Preserve fractional elapsed days, nonnegative
  ages, strict `> 30` aging and zero empty means.
- Endpoint default/unknown range is `last30`; browser starts `lastMonth`.
  Use configured business-calendar boundaries with explicit UTC SQL parameters
  and DST/month/edge fixtures. Keep scope/range visible, `N/A` zero-day display,
  labels, loading/empty/error behavior, safe DOM and auth/scope/range stale guards.
- Identifier failures use canonical `error_max_retries`; hold failures count
  at most once per title by case-insensitive `WorkflowTag.Label` prefix
  `hold failed`, independent of range.

## Surfaces And Validation

Expected implementation: feature-oriented endpoint/service/DTO in `Asap.Web`,
normal endpoint/DI registration, parameterized aggregate SQL using existing
EF/SqlClient boundaries or Dapper where appropriate, an Analytics module in
the tracked vanilla staff frontend and its existing navigation/styles, real-SQL
fixtures, frontend/browser/accessibility tests. DACPAC remains schema owner.
All required migration inputs are present; no concrete correction is pending.
Fix any subsequently demonstrated missing Analytics dependency in this slice.

Luna must run complete integrated Slice 6 validation before the supervisor
dispatches fresh independent Terra High full-slice review. Required
validation: Release build; full .NET/real-SQL and Node/frontend suites;
two-library/all-library/forged-scope and ID-collision metric fixtures;
browser desktop/mobile, auth/scope/range races, keyboard/focus and axe
serious/critical gates; native migration/export/import/reconciliation;
Web/native publication, exclusions and source/artifact/DACPAC hashes; docs
integrity and `git diff --check`. Retain compact receipts and local evidence.
Existing local publication/native harness paths are in [Slice 5's
handoff](slice-05-handoff.md#preserved-local-evidence); preserve those files.

Original bootstrap validation passed: 19/19 manifest hashes; 19 relative file/heading
links across 18 documents; `git diff --check`; final documentation-only file
inspection. That bootstrap did not change the manifest or run the application
acceptance suite. Policy synchronization validation is recorded separately in
PR #265; it also runs documentation checks only.
No substantive baseline/packet discrepancy or unresolved contract issue was
identified. Canonical identifier normalization and required event timestamps
remain accepted target behavior.

Non-goals: repositories, MediatR, reporting framework, charts, materialized
Analytics state, raw-row aggregation in C# or browser, a second auth/request
framework, new test infrastructure, paging, Slice 7, final-port PR merge,
tag/deploy/rehearsal or cutover. PR #265 stays draft until the authorized
supervisor reaches its review/acceptance gates and integrates it under document
10. PR #264 stays draft. Implementation alone does not imply acceptance or
authorize later-slice progression.

## Next Action

Run one GPT-6 Astra Max thin autonomous supervisor task for Slice 6 from PR #265.

The supervisor internally dispatches bounded Luna Max implementation with full
integrated validation, fresh Terra High full-slice review, and confirmed Luna
fix/Terra focused re-review loops (maximum three cycles after initial full
review). It performs short acceptance, merges PR #265 into `codex/csharp-port`,
records status/acceptance documentation, commits if required and waits for
successful CI on the exact final milestone SHA. Stop at accepted Slice 6 or
document 10's hard-stop conditions; no Slice 7 continuation is authorized.
Use compact receipts and the binding context firewall. Manual/direct bounded
tasks remain fallback with identical gates, not required separate user launches.
