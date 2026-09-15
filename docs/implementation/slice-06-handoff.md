# Slice 6 Thin Autonomous Supervisor Handoff

## Implementation Status - 2026-09-15

Slice 6 Analytics implementation is complete and ready for independent review;
it is not accepted. The exact review candidate SHA is recorded in PR #265 and
the local candidate receipt after push because a commit cannot embed its own
hash. The candidate started from `e250ef1f40e8cae79d3761d77330a8560d799d3b`
and is linked to `.git/asap-slice-06-candidate-20260915-final4.json` and
`.artifacts/slice-06-candidate-20260915-final4/artifact-manifest.json`.

The implemented surfaces are the scoped server-side SQL aggregation endpoint
and DTO, staff Analytics navigation/view, scope/range/auth stale-load guards,
real-SQL and frontend regressions, the dedicated two-library collision fixture,
and the published/native verification wrappers. Complete local gates are
recorded in the candidate, focused Analytics, browser, native and Node receipts.
The earlier Grid.js focus failure was an ordinary isolated-JSDOM teardown race;
the harness now drains scheduled Grid.js work before teardown. The candidate
suite contains no retry-based masking. Release .NET evidence remains the
successful 305/305 report whose application inputs were unchanged by that
Node-only harness correction.

Evidence paths: `TestResults/analytics-focused-7/analytics.trx`,
`TestResults/node-full-final.log`,
`TestResults/staff-browser-analytics/browser.trx`,
`.artifacts/browser/staff-0550dd9acaa84370a32954809c003011/staff-browser-results.json`,
`.git/asap-slice-06-final-fixtures-20260915-final5/receipt.json`,
`.git/asap-slice-06-final-browsers-20260915-final4/receipt.json`, and
`.git/asap-slice-06-source-linkage-final4.json`.

PR #265 remains draft, PR #264 remains draft, Slice 6 is not accepted, and
Terra has not run. Next state: full independent Terra review.

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
not be repeated. The implementation and local pre-review gates are complete;
no independent review was dispatched during synchronization.

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

Implemented surface: feature-oriented endpoint/service/DTO in `Asap.Web`,
normal endpoint/DI registration, parameterized aggregate SQL using existing
EF/SqlClient boundaries or Dapper where appropriate, an Analytics module in
the tracked vanilla staff frontend and its existing navigation/styles, real-SQL
fixtures, frontend/browser/accessibility tests. DACPAC remains schema owner.
All required migration inputs are present; no missing Analytics dependency was
demonstrated during implementation.

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

Run the full independent Terra review for the pushed PR #265 candidate. Terra
has not run; this handoff does not accept Slice 6, merge either draft PR, or
authorize Slice 7. Any later acceptance must preserve the exact candidate SHA,
run the governing exact-SHA CI, and follow document 10's review/acceptance
gates.

For a later documentation-only acceptance commit, run from the repository root:

```text
node .git/validate-supervisor-docs.cjs HEAD^
git diff --check HEAD^ HEAD
```
