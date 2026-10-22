# Slice 3: Additional-Copy Workflow

## Gate And Ownership

Dispatched after the completed Slice 2 milestone, tests/Terra review and actual
remote CI gate. Fresh Sol XHigh owns this slice due
to retained-claim concurrency and migration, with a fresh Terra High retained
through the required full-slice review/fix/re-review cycle. Read actual prior
code before extending it. Continue the temporary final FileEmailSender decision;
real Postmark remains a release/rehearsal blocker, not an implementation stop.

Slice 2's reviewed milestone is
`9f946aed4b091a82407ac929345819d0a0c87b10`; local 181-test/native/artifact
acceptance and full Terra Pass 2 are green. Actual remote Linux CI run
`34744506275` passed all 181 tests with zero skips and publication gates.

## Scope And References

Deliver source-request preview/create -> independent additional-copy grid,
claim/assignment/close/reopen -> safe source deletion and reduced task deletion
audit. Preserve existing vanilla/Grid.js UX and useful route/DTO shape; no
special TitleRequest status, new event system or configuration redesign.

Read root AGENTS, `02-IMPLEMENTATION-PLAN.md` Slice 3,
`01-PORTING-SPEC.md` 4-7.6, 14, 19-21,
`03-DATABASE-DESIGN.md` AdditionalCopyRequest/audit/outbox,
`04-MIGRATION-CUTOVER.md` 6.3 and entity mapping,
`06-TESTING-CI.md` SQL/browser/accessibility plus R3/R4,
`07-API-FRONTEND-COMPATIBILITY.md` version/scope/profile, AdditionalCopy reopen
response and deep links, and `10-CODEX-MULTI-MODEL-TASK.md` review gates.

## Pinned Executable Behavior

Source pin `150b30b776565194260cc327eeeffdfb46475e81`. Astra read these exact
files via `git show`; Sol inspects their callers/tests and any changed path.

- `lib/additional_copies.js`: create copies source library/bibliographic fields
  and claimant identity/display/time once. Own open/closed state, notes,
  creation/closure attribution and timestamps. Subsequent source changes do not
  retarget the task. Lists return all rows in authorized scope/status, newest
  creation first. The source's blind claimant copy/reopen is explicitly corrected
  by the pack; do not preserve its string-prefix FK heuristic.
- `lib/staff/title_request_actions.js`: preview/create requires an authorized
  source in pending_hold or hold_placed and a BIB; returns open task count for
  the same library/BIB. Creation leaves the original hold queued/placed and
  records its explanatory source note, with optional reminder. Preserve the
  separate additionalCopy action path where the source UI uses it.
- `lib/staff/additional_copy_routes.js`: list/close/reopen/claim/unclaim/assign
  route names and DTO shape. Assignment validates actor, assignee, open item
  and scope before after-commit notification. Legacy best-effort event calls
  are not permission to add an AdditionalCopy event table; use the required
  existing Notes and administrative cleanup counts. Do not hide SQL failures
  as an apparently empty successful queue.
- `pb_public/staff/js/modals/additional-copy.js`: confirmation is an actual
  dialog with safe DOM nodes, explicit reminder checkbox/default, Cancel focus,
  Escape/cancel handling and restoration of prior focus. Adapt auth references
  to the existing target staff session, do not redesign the interaction.

Also inspect `grid-row-actions.mjs`, actions/grid/modal request paths,
`assignment_policy.js`, `assignment_notifications.js`, current reduced deletion
helpers/migrations and relevant existing tests before adapting them.

## Correctness Contract

1. Follow the canonical AdditionalCopy table and rowversion constraints. Source
   FK is nullable and `ON DELETE SET NULL`; source deletion preserves the task.
   Closed task deletion uses reduced permanent audit, not full PII/notes copies.
2. Preserve creator/closer/current claimant snapshots and independent Notes.
   Preserve the source's task-owned library-name snapshot used by its DTO;
   canonical database section 9 permits exact fields needed by the current API.
   Do not replace it with a later Organization or source-request display name.
   Creation copies a source claim once only after current target staff identity,
   tenant trust and resource eligibility validation. No substitute assignment.
3. Every relationship writer shares Slice 2's StaffUser serialization point.
   Lock Organization -> relevant StaffUsers -> source/task -> dependents, stable
   key order. Read candidate/version before locks, then re-read after locking.
   Changed candidate/version restarts the sequence or returns 409; never take a
   newly discovered StaffUser lock behind the task lock.
4. Reopen validates a retained claimant under the common predicate with stored
   relationship participation disabled, independently of actor permission and
   active-library action eligibility. Valid claimant keeps original attribution.
   Invalid claimant clears every effective FK/display/time/type/rule field and
   appends old attribution/time/reason to Notes in the same transaction as
   reopening and clearing closure fields. Never auto-assign the actor.
5. Extend the existing staff lifecycle service to clean open additional-copy
   claims on deactivation/demotion/library move, append concise Notes, and
   include separate task cleanup counts in the same administrative audit.
   Preserve closed claims until reopen. Valid stored relationships to inactive
   libraries remain valid; inactivity alone is not staff deactivation.
6. Request versions and authorization are checked server-side for every action.
   DTO returns current effective claimant and safe claimClearedReason; browser
   refreshes Mine/unclaimed and announces clearing, without stale attribution
   flicker or automatic replay on 409. Source/task deep links remain usable.
7. Assignment/reminder intents use the existing durable outbox and current
   NotificationEmail, sensitive identity/scope/address-kind revalidation and
   nonproduction protection. Missing optional contact does not fail task work.
   File transport changes no business or delivery semantics.

## Migration And Acceptance

Extend actual stopped-SQLite export/normalized-package import/reconciliation,
not a target-only seed. Import independent task snapshots/history and nullable
source mapping, including deleted-source cases. Map every operational claimant
first, then validate current target identity/trust/scope independently of
Organization participation. Preserve valid inactive-library and closed history;
clear/report unmapped, inactive and out-of-scope open claims with exact reasons,
counts and retained attribution. No UPN/actor-string inferred authorization.
Equivalent fresh targets produce deterministic reports. Real SQL reconciliation
must prove zero invalid effective open claims, not just existing FK references.

Parent-only source fixture preparation is recorded in `slice-03-evidence.md`.
It is not completed migration support or permission to start before Slice 2's
gate. Use the stopped `additional-copy-data` fixture through the new native
exporter once this slice changes the package contract; do not hand-edit a
previous artifact's manifest to claim the newer contract.

Use real SQL for source/task independence and deletion, atomic creation/event/
intent, scoped queues/actions, rowversion conflicts, inherited claims and reopen
races against deactivation/library move/demotion in both orderings. R3 cases
include valid same-library staff/admin, cross-library super-admin, already-open
no-op, changed candidate and rollback after intermediate failure. Extend R4
fixtures for all conversions and closed history. Retain source/patron/staff
regressions. Browser test preview/create, source link, claim/assign, close/reopen,
stale conflict and Notes/claim-cleared feedback at desktop/mobile with axe
serious/critical and explicit keyboard/focus assertions.

No commit/push/tag/deploy. Astra verifies the complete slice, obtains Terra
full-slice passes with Sol fixes, then creates the milestone before Slice 4.
