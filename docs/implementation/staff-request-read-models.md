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
