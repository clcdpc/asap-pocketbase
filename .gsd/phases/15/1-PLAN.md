---
phase: 15
plan: 1
wave: 1
---

# Plan 15.1: Backend Analytics for Additional Copies

## Objective
Update the backend analytics logic to aggregate metrics from both `title_requests` and `additional_copy_requests` collections.

## Context
- lib/staff_routes.js
- lib/records.js
- lib/additional_copies.js

## Tasks

<task type="auto">
  <name>Aggregate additional copies in fetch</name>
  <files>lib/staff_routes.js</files>
  <action>
    Modify `fetchAnalyticsRecords` to fetch from both collections.
    - Fetch all records from `title_requests` (existing logic).
    - Fetch all records from `additional_copy_requests` using the same scope filter.
    - Return a combined list, or modify the downstream functions to handle two lists.
    - Important: Tag additional copy records with a pseudo-status `additional_copies` if they are "open", so downstream logic can distinguish them.
  </action>
  <verify>grep -A 20 "function fetchAnalyticsRecords" lib/staff_routes.js</verify>
  <done>The analytics fetch logic includes both request types.</done>
</task>

<task type="auto">
  <name>Update aggregation functions</name>
  <files>lib/staff_routes.js</files>
  <action>
    Update aggregation helpers to support the new data.
    - `loadStageCounts`: Add a counter for `additional_copies`.
    - `loadAgingMetrics`: Include `additional_copies` in the `openStages` list.
    - `loadAnalyticsSummary`: Ensure `openRequests` and `closedRequests` counts include additional copies.
  </action>
  <verify>grep -A 10 "function loadStageCounts" lib/staff_routes.js</verify>
  <done>Aggregation functions correctly count and age additional copy requests.</done>
</task>

## Success Criteria
- [ ] The `/api/asap/staff/analytics` endpoint returns `additional_copies` in `stageCounts`.
- [ ] `summary.openRequests` includes open additional copies.
- [ ] `summary.closedRequests` includes closed additional copies within the date range.
