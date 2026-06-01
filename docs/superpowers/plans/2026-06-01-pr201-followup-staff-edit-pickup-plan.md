# PR 201 Follow-up: Staff Edit Pickup Flow

This PR intentionally does **not** include staff edit pickup endpoints and modal update flow.

Follow-up scope:

1. Add pickup options endpoint for edit modal:
   - `POST /api/asap/staff/requests/{id}/pickup-options`
2. Add pickup preference update endpoint for edit modal:
   - `POST /api/asap/staff/requests/{id}/pickup-preference`
3. Enforce read-only behavior for `hold_placed`.
4. Add audit note/event only when pickup actually changes.
5. Add optimistic concurrency guard (409 on stale live preference).
6. Add UI wiring in staff edit modal for lazy load + pickup-only save.

Reason for split:

- Keep PR 201 focused on correctness and error-handling regressions found in review.
- Land high-confidence fixes quickly without expanding endpoint/UI scope in the same patch.
