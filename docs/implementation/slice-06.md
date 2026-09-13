# Slice 6: Analytics

## Preparation Only

Not dispatched. Refresh this packet after Slices 1-5 pass their complete
tests, Terra review/fix/re-review gates, and milestone commits. Do not start
analytics implementation early. The canonical sequence remains document 02;
this packet narrows implementation context without changing the agreed plan.

Astra Max refreshes this packet against accepted prior implementation and
dispatches fresh Luna Max for the complete slice. Retain that Luna context
through tests, confirmed-review fixes and retesting. After required tests,
fresh Terra High independently reviews the whole slice and retains the same
context through document 10's unchanged full review/re-review gate. Sol is
escalation-only under document 10's conditions. Astra verifies acceptance,
commits/pushes the milestone and requires actual remote CI success for that exact
commit before dispatching the next slice. Prior exact-milestone CI must be green
before this slice is dispatched.

## Objective And Prior Contracts

Port the existing scoped analytics endpoint and browser view using server-side
SQL aggregation. Retain the compact aggregate DTO, metric meanings, vanilla
frontend and existing request/stale-response helpers. Do not add a reporting
framework, charts, precomputed analytics state, or request paging as part of
this slice. Do not load all requests to aggregate them in C# or the browser.

Before dispatch, record the actual previous milestone SHAs, schema version,
staff authorization and scope helpers, request/event/tag models, migration
contracts, and test fixture APIs. Reuse those contracts directly. Current
staff binding/allowed-tenant/role/activity/organization eligibility applies to
every request; the selected browser scope is never authorization evidence.

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
axe gates. Run all relevant prior tests and end-to-end acceptance before
Terra Pass 1. The same Luna fixes confirmed findings; Terra does not edit
implementation.

Return actual changed paths, commands/results and remaining concrete risks.
No commit, push, merge, tag, deployment or next-slice work by the implementer.
No completed-slice or production claim until the corresponding gates pass.
