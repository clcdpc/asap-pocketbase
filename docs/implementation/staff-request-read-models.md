# Staff request processing — issue #487, Workstream 3

Baseline: `4e9fafb9087c7617922339497110330f65bca3af` (current main).
R03, R09 and the backend ActionAsync portion of R17 were still present.

## Phase-entry producers (R03)

| Event | Resulting status | Producer |
| --- | --- | --- |
| `status_changed` | The resolved manual action target | `TitleRequestMutationService.ActionAsync` |
| `promoted` | `pending_hold` with automatic holds; otherwise `closed` | `WorkflowProcessingService.PromoteRowAsync` |
| `hold_placed` | `hold_placed` | `HoldPlacementService` successful completion and operator success resolution |
| `fulfilled` | `closed` | `WorkflowProcessingService` fulfillment |
| `timeout_closed` | `closed` | `WorkflowProcessingService` timeout processing |

The display selects the latest matching transition into the current status by
CreatedUtc, then ID. Reentry therefore supersedes an earlier visit. Notes, edits,
email, receipts and other activity do not change phase entry. Missing transition
history falls back only to CreatedUtc. Existing UTC serialization and secondary
list ordering (Updated, Created, ID descending) remain intact. No producer,
timeout policy, history record or business transition changed.

Regressions exercise actual manual reentry, both automatic promotion destinations,
hold adoption/completion, fulfillment and all three timeout phases through DTO
reads. Direct history seeding is limited to timestamp ties and missing history.

## Action baseline

Before extraction, 68 retained queue/action/template/patron-email/post-commit tests
passed on real local SQL Server 2022, using deterministic providers. These include
the named replay, timeout-around-commit, intermediate-flush rollback and distinct
notification outcome gates in the task.

## Read measurements and retained contracts (R09)

`RequestReadQueryEvidenceAndMixedRecoveryHistory` captures actual EF commands,
selected column names, reader calls and materialized entity types against SQL
Server 2022. The same fixtures were run before changing the reads and afterward:
one request and twelve requests, each with 15 notes and six hold attempts (an
older success, four failures and a newer incomplete attempt). Reader calls include
the final unsuccessful Read; reported row counts below exclude it. Related matching
is measured separately as the candidate command within detail, not as a new API.
No latency improvement is claimed.

| Path | Reader commands, 1 / 12 requests (before → after) | Data reduction |
| --- | --- | --- |
| List | 10 / 10 → 9 / 9 | 15 / 180 full 11-column event rows → one computed phase column in the request query; no activity entities |
| Detail | 19 / 19 → 19 / 19 | Eight-column activity projection replaces 11-column entities; shared scalar lookups are projected |
| Related candidates | 1 / 1 → 1 / 1 | 1 / 12 rows, 41 → 6 columns; no candidate request entities |
| Template lookup | 20 / 20 → 9 / 9 | No request DTO/entities, activity, related candidates, workflow, hold/pickup recovery or email reads |
| Hold summaries (list) | 1 / 1 → 1 / 1 | 6 / 72 rows × 36 columns → 2 / 24 rows × 15 columns; no operation entities |
| Hold summaries (detail) | 1 / 1 → 1 / 1 | 6 rows × 36 columns → 2 rows × 15 columns |

List materializes only the displayed request entities (1 / 12), whose 41-column
shape is substantially consumed by the public DTO, workflow policy and research
context. Detail materializes only its one request plus the two locked organization
rows and current staff row. Previously detail materialized 2 / 13 requests, 15
events, six hold operations and broad organization/workflow/format rows. Template
lookup now materializes only those authoritative organization/staff lock reads.
The list's request status and correlated phase value come from the same SQL
statement, avoiding a second read of possibly changed status during assembly.
There is no query per displayed request, cache, pagination or similarity redesign.

Shared authorization helpers preserve native ID resolution, preliminary scope,
serializable organization locks, post-wait current identity/role/library checks,
request-to-organization consistency and classification. Search and selected-library
details still require active libraries; authorized all-scope recovery and template
reads retain inactive-library access. Template reads keep the same cancellation
normalization as detail. The template policy consumes identity, source/override
links, custom/hidden flags, sort order, display names and nonblank subject/body
validity; only the unused rowversion is excluded.

Related matching retains .NET letter/digit and invariant-case normalization,
nonempty keys, self exclusion, candidate-ID deduplication, status/library groups
and their existing sort. Scoped candidates remain a single batch of six-column
rows; normalization has not been replaced by SQL collation semantics. Latest
incomplete hold, latest successful hold and pending pickup remain independent.
Mixed-history coverage checks incomplete recovery, older-success protection,
allowed actions, masking and simultaneous pickup visibility. Existing DTO fields,
UTC precision, versions, custom-field validity, activity ordering and effective
workflow defaults remain covered by production service/API regressions.

## Action extraction (R17 backend)

ActionAsync retains input validation, receipt replay before BIB preflight, replay
fallback on preflight failure, locked receipt lookup before final stale-version
rejection, final SaveChanges and commit. `PrepareActionAsync` owns the existing
preview/claim-rule reads, stale-preview replay fallback, patron refresh and email
readiness, outside the mutation transaction. Its typed preparation separates
provider/transport facts from the locked current state. Operational patron refresh
failure still logs and follows the existing suppressed-notification path; SQL/email
readiness failure or an independent timeout still rejects before mutation.

`ApplyLockedActionAsync` uses the caller's context, tracked request and transaction
for capability/configuration/template validation, local field/status/claim changes,
additional-copy creation, independent staff/patron outboxes and the accepted receipt.
Identity-generating SaveChanges calls remain transactional flushes. The outer method
owns commit; returned failures and exceptions dispose/roll back the whole local
outcome. `DispatchActionNotifications` runs only afterward and keeps staff/reminder
and patron outcomes separate. Cancellation or dispatcher failure cannot undo durable
acceptance or its recoverable outbox. Existing separate receipt/preflight contexts
and the complete command fingerprint/original actor comparison remain unchanged.

The unused full request-event read in ActionAsync was removed after confirming its
local had no consumers. No authority or history decision depended on it.

New focused production-entry-point tests cover DMV-observed replay lock waits
with identity revocation, email/library changes, request reassignment and a different
authorized actor; they also cover commit followed by cancellation/dispatch failure
and an exact retry with the original version, without another dispatch or durable
effect. The retained queue tests already cover provider-unavailable replay,
changed-command conflicts, failures after intermediate writes, and timeout around
commit. Existing template, claim, readiness, caller cancellation and distinct
staff/patron notification regressions remain required.
