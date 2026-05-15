---
phase: 16
plan: 3
wave: 2
---

# Plan 16.3: Implement Cron Job for Auto-Closing Additional Copies

## Objective
Implement the automated background task that identifies and closes stale additional copy requests based on library settings.

## Context
- lib/jobs.js
- lib/additional_copies.js

## Tasks

<task type="auto">
  <name>Implement auto-close logic in jobs.js</name>
  <files>lib/jobs.js</files>
  <action>
    Add `processAdditionalCopyTimeout(app, result)` to `lib/jobs.js`.
    - Use `processPagedQueue` to iterate over `additional_copy_requests` where `status = 'open'`.
    - Retrieve the timeout setting for the record's library.
    - If `enabled` and `created` (or `updated`) is older than `days`, call `additionalCopies.closeTask(app, record)`.
    - Note: Log the action in the record's notes.
  </action>
  <verify>grep "function processAdditionalCopyTimeout" lib/jobs.js</verify>
  <done>Timeout processing logic is implemented.</done>
</task>

<task type="auto">
  <name>Hook into scheduled check</name>
  <files>lib/jobs.js</files>
  <action>
    Call `processAdditionalCopyTimeout(app, result)` from within the `runScheduledHoldCheck` function.
  </action>
  <verify>grep "processAdditionalCopyTimeout(app, result)" lib/jobs.js</verify>
  <done>The timeout check is executed as part of the hourly hold check job.</done>
</task>

## Success Criteria
- [ ] Stale additional copy requests are automatically closed by the background job.
- [ ] Action is logged in the request notes.
- [ ] Job result reflects the number of timed-out additional copies.
