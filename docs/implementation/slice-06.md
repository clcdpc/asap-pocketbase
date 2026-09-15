# Slice 6: Analytics

## Current Status - 2026-09-15

Slice 6 was accepted at historical milestone
`7ba59421176ede99ba48488be6bc81010e60f65c`; correction PR #266 is complete at
authorized product/integration baseline
`d607723e846f633ebe206163f6c232dd79566d6f`, with successful exact-SHA CI
`34988112346`. See [PORT-STATUS.md](PORT-STATUS.md) and the
[review record](slice-06-review.md). Slice 7 has not started; its execution
follows [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md).
The implementation/bootstrap entries below preserve historical checkpoints,
including their then-current pending actions and model roles.

## Historical Implementation Status - 2026-09-15, Fix/Re-review Cycle 1

Full Terra review confirmed S6-P2-1 in the `AverageDaysToHold` aggregate: an
in-range first literal `hold_placed` event earlier than request creation was
counted as held but omitted from the average. The SQL now retains that row as
`0.0`, and the compact real-SQL fixture covers the negative-duration case.
Renewed Release, focused real-SQL, Kestrel browser, and Web-publication
evidence is recorded in the [handoff](slice-06-handoff.md). Slice 6 remains
unaccepted; focused Terra re-review is pending.

## Bootstrap (Historical) - 2026-09-15

This packet was refreshed against accepted Slices 0-5 before implementation.
The canonical sequence remains document 02; this packet narrows implementation
context without changing the agreed plan. See the [thin autonomous supervisor
handoff](slice-06-handoff.md).

| Anchor | Value |
| --- | --- |
| Accepted Slice 5 PRODUCT milestone | `36727414d02cbe34ba13cd3f6f1bb57980b83a6f` |
| Original Slice 6 bootstrap branch base | `1c3b46d13a42bb1a2887136385f3dce18b610c8c` |
| Original bootstrap head (before policy synchronization) | `df80ec821b0cde9309e1e4d765d736ed832f48b3` |
| Current Slice 6 policy/branch base after rebase | `5493efee9ce140d6eaa83da2d178e2004fea7c2c` |
| Rebased bootstrap commit | `1f6886ff1428ff87269e9534b7cd4e5edad70fe8` |
| Slice branch | `codex/slice-06-analytics` |
| Draft slice PR | [#265](https://github.com/clcdpc/asap-pocketbase/pull/265), base `codex/csharp-port` |
| Topology | No child packages — one Slice 6 integration PR. |
| Application/schema compatibility | .NET 10 (`net10.0`); `SchemaVersion.ExpectedVersion = 5` |
| Migration contract | `MigrationContract.ExpectedSchemaVersion = 5`, `ContractVersion = "slice-05"`; export format `1` |

The accepted product and both policy-base anchors differ only by
documentation/process policy. Neither policy commit is Slice 6 implementation.
Local HEAD, remote `codex/csharp-port` and draft PR #264 matched the original
branch base before branch creation; remote `main`
still matched the behavioral pin. Prior acceptance and policy CI success are
established evidence, not rerun by this bootstrap. Application code remains at
the accepted Slice 5 product state; no new application release version is set.

Aggregation, DTOs, browser rendering and SQL/browser fixtures share one metric
and authorization contract. Inspection found no independently useful package
boundary. PR #264 remains the final draft port PR into `main`.

Bootstrap is complete and remains valid after the docs-only rebase. The next
action is one GPT-6 Astra Max thin autonomous supervisor task from PR #265,
dispatching bounded Luna Max implementation and independent Terra High review
internally through document 10's validation, fix/re-review, acceptance,
integration, status-record and exact-milestone CI gates. Manual/direct tasks
remain fallback. This synchronization launches no supervisor or workers.

Follow [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) for the
Slice-6+ staged-PR lifecycle, optional package boundaries, review modes,
context firewall and exact-milestone CI. Thin autonomous supervision is the
preferred/default mode; Astra dispatches bounded Luna Max and independent
Terra High workers. Manual/direct execution remains fallback. Reading lists
below identify full-slice authority; workers start with directly relevant
excerpts and expand for concrete concerns. Prior-slice acceptance and its
exact-SHA CI must be complete before this slice starts.
Default: one cohesive analytics slice PR; child packages need a material benefit.

## Objective And Prior Contracts

Port the existing scoped analytics endpoint and browser view using server-side
SQL aggregation. Retain the compact aggregate DTO, metric meanings, vanilla
frontend and existing request/stale-response helpers. Do not add a reporting
framework, charts, precomputed analytics state, or request paging as part of
this slice. Do not load all requests to aggregate them in C# or the browser.

Current staff binding/allowed-tenant/role/activity/organization eligibility
applies to every request; selected browser scope is never authorization
evidence. The simple direction remains feature-oriented Analytics code in
`src/Asap.Web`, a scoped aggregate endpoint/DTO, parameterized SQL using the
existing EF context/SqlClient boundary (Dapper where useful), and the current
vanilla frontend. No repositories, MediatR or materialized Analytics state.

## Current Reuse Map

Paths below are repository-relative. These are existing contracts, not new
Analytics infrastructure.

### Staff Authorization And Scope

- `src/Asap.Web/Features/Staff/StaffAuthenticationRegistration.cs`:
  `AddStaffAuthentication` establishes Entra/cookie authentication and retains
  the sign-in tuple. `StaffClaims.TryRead` in `StaffClaims.cs` produces
  `StaffIdentityEvidence(StaffUserId, TenantId, ObjectId)`.
- `StaffEligibilityService.cs`: `FindByBindingAsync`, `EvaluateAsync`,
  `CurrentStaff`, `StaffEligibilityOutcome` and `StaffRoleRequirement.Any`.
  Every request requires the original tuple to equal the current active
  `StaffUser` binding and its tenant to remain in `AllowedTenantIds`. Roles are
  `staff`/`admin` on a non-system Organization or `super_admin` on Organization
  `1`; participation requires the actor's Organization to be active.
- `StaffCurrentUserMiddleware.InvokeAsync` applies that eligibility to
  `/api/asap/staff`, returning `401 staff_session_invalid` or
  `403 staff_scope_forbidden` when invalid. Use `RequireAuthorization()` and
  `StaffAuthenticationEndpoints.RequireCurrentStaff(HttpContext)` in the new
  endpoint. Analytics is available to ordinary staff, not admin-only.
- `TitleRequestViewService.ListAsync` contains the current list-scope contract;
  `CanAccess(CurrentStaff, int)` is internal. `AdditionalCopyService.ResolveScope`
  implements the same parsing privately: staff/admin always use their own
  `OrganizationId` and ignore forged selections; super-admin uses `all`/omitted
  or a validated active non-system library ID. Invalid super-admin selection
  becomes `400 invalid_scope` at the list endpoints. No public generic scope
  resolver exists. Reuse these semantics with the common eligibility service;
  do not call a raw-row list loader to compute Analytics or add a second auth
  layer. Preserve Analytics' `scope` then `orgId` input and `system` all-scope
  alias at the route boundary.

### Requests, Events, Tags And Time

- `src/Asap.Web/Infrastructure/Data/DomainEntities.cs` and `AsapDbContext.cs`:
  `TitleRequest`, `AdditionalCopyRequest`, `Organization`, `StaffUser`,
  `TitleRequestEvent`, `WorkflowTag`, `TitleRequestWorkflowTag` and their DbSets.
  Both request types own independent bigint `Id` sequences and scope through
  `LibraryOrganizationId`, not patron Organization or copy source request.
- DACPAC sources: `database/Asap.Database/Tables/TitleRequests.sql`,
  `AdditionalCopyRequest.sql`, `OrganizationAndStaff.sql`. Both requests have
  required `CreatedUtc`/`UpdatedUtc` (`datetime2(7)`) and current `Status`.
  Title statuses are `suggestion`, `outstanding_purchase`, `pending_hold`,
  `hold_placed`, `closed`; copies use `open`/`closed` and display open as
  `additional_copies`. Only titles have `CloseReason`; closed copies therefore
  use Analytics' `unrecorded` fallback. Copy `ClosedUtc` is not the range field.
- Events have `TitleRequestId`, literal `EventType`, `Status`, `CloseReason`
  and required `CreatedUtc`. Tags join through `TitleRequestWorkflowTag`;
  exception matching uses `WorkflowTag.Label`'s case-insensitive `hold failed`
  prefix, once per title. Copies acquire neither events nor tags through a
  matching numeric ID or their `SourceTitleRequestId`.
- Canonical `TitleRequest.IsbnCheckStatus` permits null, `pending`, `found`,
  `not_found`, `skipped_no_isbn`, `error_max_retries`. Failure count uses
  `error_max_retries`; `IsbnCheckLastErrorCode`/retry count are diagnostics,
  not additional failure populations. Migration's accepted legacy `error`
  normalization is described below.
- `Infrastructure/Data/SchemaVersion.cs` and
  `database/Asap.Database/Scripts/PostDeployment.sql` agree on version `5`.
  Reuse injected `TimeProvider` and
  `ExternalConfiguration.Application.BusinessTimeZone` from
  `Infrastructure/Configuration/ExternalConfiguration.cs`, resolved with
  `TimeZoneInfo.FindSystemTimeZoneById` as in `WorkflowProcessingService`.
  `TimeoutSemantics` calendar-day cutoffs are not fractional Analytics ages.

### Frontend And Fixtures

- `src/Asap.Web/Frontend/staff/js/http.js`: `authorizedJson`,
  `loadStaffSession`, `onSessionInvalid`, `onAccessUnavailable`, `isAbortError`,
  shared `latestLoads`. `Frontend/shared/http.js` owns `requestJson`;
  `Frontend/shared/latest-load.js` owns `createLatestLoad`,
  `begin(slot)`/`signal`/`isCurrent()`/`abort()`/`finish(slot, token)`.
- `Frontend/staff/js/workflow.js`: `createWorkflowApp`, `state.staff`,
  `state.scope`, `state.activeView`, `showWorkspace`, `populateScopes`,
  `startSession`, `switchView`, `showSignedOut`, `showAccessUnavailable`.
  Extend existing view/navigation and auth invalidation wiring for Analytics'
  separate scope/range. `element`, `announce`, `renderOperationsTable` and
  `loadOperations` demonstrate safe DOM, loading/empty/error states and guarded
  completion. These functions are local, not exported shared APIs. Keep DOM
  construction with `textContent`/`replaceChildren`, accessible status and
  heading focus. Edit tracked `Frontend/staff/index.html`/`styles.css`, not
  generated `wwwroot` files.
- `tests/Asap.Tests/Integration/PatronJourneyTests.cs`: existing partial
  `PatronJourneyTests`, `Initialize` (isolated real-SQL DACPAC deployment),
  `StartApplication`, `CreateApplicationFactory`, `SeedLibraryAsync` (library
  `2`), `UpsertTestOrganizationAsync`, `ReadConfiguredSuperAdminAsync`,
  `AddTestingStaffHeaders`, `ProtectStaffCookie`, `CookieClient`,
  `ReadAntiforgeryTokenAsync`, `MutableTimeProvider.SetUtcNow` and
  `TestArtifactPaths.FindDacpac`. Reuse `IDbContextFactory<AsapDbContext>` for
  fixture data and `ASAP_TEST_SQL_CONNECTION_STRING` for real SQL.
- `StaffCorrectiveJourneyTests.cs`: `CreateCorrectiveStaffAsync(actor, role,
  organizationId)` and `ReadCorrectiveStaffAsync` create/read actual eligible
  staff/admin/super-admin scope. `Slice5QueueFairnessRuntimeTests.cs` has
  `EnsureSlice5IsolatedLibraryAsync(contextFactory, organizationId)` for another
  Organization. These helpers are private members of the same partial fixture.
  Use explicit two-library fixtures with ordinary and super-admin callers.
- `StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers` and
  `SeedStaffBrowserStateAsync` in `PatronJourneyTests.cs` run the existing
  `tests/browser/staff.cjs`. Its local `createContext`, `scan`,
  `delayNextServerResponse` and `deferred` cover authenticated scope, delayed
  responses, screenshots, overflow/images and axe serious/critical assertions.
  Existing desktop `1280x900`/mobile `390x844`, keyboard presses and focus
  assertions are the browser patterns to extend.
- Frontend regression anchors: `tests/staff_dotnet_frontend.test.js`,
  `staff_dialog_focus_dotnet.test.js`, `staff_forbidden_recovery_dotnet.test.js`,
  `settings_mutation_scope_race_dotnet.test.js`,
  `frontend_request_architecture.test.js`, `staff_analytics_dom_safety.test.js`.
  Migration fixtures: `tests/Asap.Tests/Migration/MigrationCliTests.cs`,
  `CreateMinimalPackage`, `DeployDacpac`, identifier normalization, historical
  event-time rejection and additional-copy timestamp/history tests.

## Migration Dependency Check

All required Analytics data dependencies are present in the accepted contract;
no missing dependency correction was identified. In `src/Asap.Migration`,
`MigrationPackageExporter` exports the request/event/status/reason/tag domains;
`MigrationImporter.ImportTitleRequests`, `ImportAdditionalCopies`,
`ImportTitleRequestEventsAndPlacementProtection`, `ImportWorkflowTags` and
`ImportTitleRequestTags` preserve their Analytics inputs. `ReconcileImportedSourceState`,
`ReconcileTarget` and `MigrationReconciler.Reconcile` retain the existing
semantic/count/package checks. Extend Analytics assertions using these fixtures.

Title creation/update and event creation timestamps are required and invalid
history blocks import; copies retain updated time with the accepted
created-time fallback when absent. Literal `hold_placed` stays literal;
`status_changed` adoption and synthetic `legacy` protection markers do not
become literal holds. `NormalizeIsbnStatus` preserves terminal
`error_max_retries`, converts legacy `error` without an identifier to
`skipped_no_isbn`, and blocks ambiguous `error` with an identifier. This is
the accepted canonical migration contract, not lost Analytics data to restore.

Executable baseline inspection confirmed the prepared metric/range/DOM
contract. No substantive packet discrepancy was found. The reuse map makes
the canonical identifier population, copy close-reason fallback and current
scope APIs explicit. Target current staff eligibility and configured business
timezone govern the already-planned platform differences from PocketBase.

## Required Reading

- Root AGENTS and document 10 execution/review rules.
- `02-IMPLEMENTATION-PLAN.md` Slice 6 and the completed prior-slice packets.
- `01-PORTING-SPEC.md` staff eligibility, scope and business-time contracts.
- `03-DATABASE-DESIGN.md` title/additional-copy requests, events and tags.
- `07-API-FRONTEND-COMPATIBILITY.md` analytics and frontend request contracts.
- `06-TESTING-CI.md` SQL, authentication, browser and accessibility gates.
- `04-MIGRATION-CUTOVER.md` preserved request/event timestamps and history.
- `temporary-email-transport.md`: the real provider remains a release blocker,
  not an analytics implementation blocker.

Inspect exact PocketBase baseline
`150b30b776565194260cc327eeeffdfb46475e81` files
`lib/staff/analytics_routes.js`, `pb_public/staff/js/analytics.js`,
`tests/staff_analytics.test.js`, associated scope helpers and the staff
request/stale-result wrapper tests. `analytics-source-notes.md` is a reading
aid, not a replacement for executable source.

## Observable Acceptance

1. Staff/admin can aggregate only their own library. Super-admin can select
   one library or the all-library view. Apply the authorized predicate inside
   every SQL aggregate and event/tag subquery; reject or ignore forged scope
   according to the existing API contract. Never rely on client filtering.
2. Preserve the existing summary, stage, closed-reason, aging and exception
   DTOs. TitleRequest and AdditionalCopyRequest populations are combined only
   where the source does so. Keep type-qualified identities when unioning
   bigint IDs so an additional-copy task cannot acquire unrelated title
   events or tags merely because its numeric ID matches.
3. Preserve each metric's population and date filter. Current stage/open/aging
   counts are not restricted to the selected reporting range. New suggestions
   count the combined created population, not only current suggestion status.
   Closed counts use current closed state and UpdatedUtc within the range.
4. Held count and average time to hold use the first explicit hold_placed
   event, including later-closed requests. Do not use the latest event or
   current status timestamp, count hold_skipped, or silently treat all
   status_changed adoption history as a literal hold_placed event. Preserve
   fractional elapsed-day arithmetic, distinct from calendar-day timeouts.
5. Preserve source range defaults, local calendar boundaries and inclusive
   populations using the configured business timezone and parameterized SQL
   boundaries. Endpoint default is last30; browser initial view is lastMonth.
   Test DST/month/range edges rather than replacing them with elapsed-hour
   windows. Preserve empty means, closed-reason fallback and exception rules.
6. Preserve visible scope/range, labels, compact layout, loading/empty/error
   states and keyboard access. Use existing authorizedJson and latest-load
   cancellation/generation checks for scope, range and authentication races.
   No runtime-data HTML interpolation or remote browser assets.

## Migration And Tests

Analytics introduces no independent imported analytics store. Verify that
the existing export/import/reconciliation preserves all timestamps, event
types, closed reasons, canonical identifier status and workflow tags used by
the queries. Extend those mappings/tests alongside this slice if an actual
analytics dependency is missing; do not defer it to migration hardening.

Real-SQL fixtures must cover two libraries and all-library scope, forged
selection by ordinary staff, overlapping title/additional-copy IDs, closed
task reason fallback, duplicate/earliest hold events, closed historical holds,
hold_skipped exclusion, fractional ages, strict older-than-30 aging, range
boundaries and empty populations. Assert every aggregate and no cross-library
information. Use parameterized feature SQL/Dapper in the existing web project.

Browser tests cover scope/range changes, stale responses across those changes
and authentication, mobile/desktop layout, keyboard/focus and serious/critical
axe gates.

Run the complete integrated slice validation before holistic Terra review;
package/fix validation and integration follow document 10. Return changed paths,
compact receipts and remaining risks. Commit/push and temporary PR operations
stay within the authorized branch/checkpoint. No final-port merge, tag,
deployment or next-slice work; acceptance requires the exact integration SHA's CI.
