# Pinned Analytics Source Notes

Read-only preparation, not Slice 6 implementation. Inspected exact PocketBase
`150b30b776565194260cc327eeeffdfb46475e81` files
`lib/staff/analytics_routes.js`, `pb_public/staff/js/analytics.js`, and
`tests/staff_analytics.test.js`. These are executable behavior anchors, not a
new analytics design or a replacement for the port pack.

## Scope And Rows

The endpoint accepts `scope`, falling back to `orgId`; non-super-admin scope
comes only from the authenticated staff library and ignores a forged selected
library/all value. Super-admin supports selected library or all/system.
Target authentication adds the pack's current binding/tenant/activity rules.
Apply scope inside every SQL aggregate, including event/tag subqueries.

The source combines TitleRequest rows and AdditionalCopyRequest rows. Open
additional-copy tasks become the display stage `additional_copies`; closed
tasks contribute to `closed`. In SQL, keep source-type-qualified keys: separate
target bigint identity sequences can share an ID, and an AdditionalCopy ID
must never join a TitleRequest event or workflow tag merely by equal number.

## Metric Oracles

| DTO field | Source behavior |
| --- | --- |
| summary.newSuggestions | All combined rows created in the selected period, not only current suggestion status |
| summary.openRequests | Current non-closed combined rows, independent of date range |
| summary.closedRequests | Current closed combined rows whose updated timestamp is in range |
| summary.heldRequests | Requests whose first explicit hold_placed event time is in range, regardless of current status |
| summary.averageDaysToHold | Mean elapsed fractional days from request creation to that first hold event for the heldRequests population; zero for empty population |
| stageCounts | Current combined-row counts, independent of range, including additional_copies and closed tasks |
| closedReasons | Closed rows updated in range, blank reason grouped as unrecorded, sorted by reason |
| aging | Current open stages, mean elapsed fractional creation-age days, and count strictly older than 30 days; independent of range |
| exceptions.holdFailures | At most one per title request having a case-insensitive tag prefix hold failed; independent of range |
| exceptions.identifierFailures | Source status error or error_max_retries; independent of range; target canonical status normalization must be explicit |

`daysBetween` uses elapsed milliseconds / 86400000, not truncated SQL
DATEDIFF(day); invalid or reverse intervals become zero in source. This is
distinct from the port's calendar-day timeout cutoff arithmetic. Preserve
metric semantics without importing invalid required timestamps contrary to
migration validation.

The first hold timestamp is the earliest event with literal eventType
`hold_placed`, not hold_skipped or every status_changed-to-hold_placed event.
The source falls back to request.updated only for a missing event timestamp
while that request is currently hold_placed; target migration timestamp rules
control whether such data is allowed. Do not substitute current UpdatedUtc or
the most recent hold event for valid historical event time. Existing regression
fixtures cover duplicate hold events, a closed request with earlier hold
history, and exclusion of hold_skipped.

## Range And Browser Behavior

- Endpoint default/unknown range is last30; browser initially selects lastMonth.
- thisMonth starts local first-of-month midnight and ends today's local
  23:59:59.999. lastMonth covers the previous whole local calendar month.
- last30/last90 start at local midnight 30/90 calendar dates before today and
  end today at 23:59:59.999; source comparisons are inclusive. Do not silently
  replace these with rolling elapsed-hour windows. Use the target configured
  business timezone, with explicit SQL parameter boundaries and fixture tests.
- Browser renders the compact aggregate DTO using safe DOM APIs. It retains
  separate analytics scope/range and the existing createLatestLoad stale-
  response guard, authorizedJson transport, abort handling and no-store loads.
- Preserve current labels, empty/error states, library options and visible
  selected scope. Exact zero-day display is currently N/A; the backend returns
  zero for empty means. No new chart, client-side raw-row aggregation or
  server-side workflow paging is implied by the port.

## Later Acceptance Focus

Use representative real SQL fixtures with two libraries, a super-admin all
view, additional-copy/title ID collisions, closed task reason fallback,
duplicate hold events, fractional/boundary ages and range edges. Assert each
aggregate and the absence of cross-library information. Browser regression
must retain stale scope/range/auth protection and serious/critical axe gates.
Analytics may reuse direct parameterized feature SQL; no reporting framework
or speculative materialized views are required.
