# Purchase Approved Patron Email Design

## Goal

Add a patron notification for the point where staff approve a suggestion for purchase and the request enters Pending purchase. The message should make clear that the item is awaiting ordering and cataloging before ASAP can place a hold.

## Current Behavior

ASAP currently sends patron emails for submission confirmation, already-owned/on-order outcomes, rejections, and hold placement. A staff Purchase action that leaves a request in `outstanding_purchase` can optionally send a staff purchase reminder, but it does not notify the patron.

The existing Hold placed template includes purchase-decision language, which makes the communication sequence unclear because the purchase decision and the hold placement are separate workflow events.

## Trigger Rule

Send the new patron email only from the normal staff Purchase action when all of these are true:

- `context.action === "purchase"`
- the request's previous status was not `outstanding_purchase`
- the final persisted status is `outstanding_purchase`

Do not send the new email when a Purchase action skips directly to Pending hold, Hold placed, or Closed. Do not send it for Already Own, Reject, Silent close, staff-created suggestions, additional-copy tasks, or repeat saves of an already Pending purchase request.

The existing optional staff purchase reminder remains separate. A single Purchase action can send both the automatic patron email and the optional staff reminder.

## Email Template

Add a fixed editable email template:

- Template key: `purchase_approved`
- Settings label: `Purchase approved`
- Settings position: between Submission confirmation and Already owned
- Default subject: `Purchase approved: {{title}}`

Default body:

```text
Hello {{name}},

Good news. The library has approved your suggestion for purchase: {{title}} by {{author}} in {{format}} format.

This request is now awaiting ordering and cataloging. Once the item is available in the catalog, ASAP will place a hold automatically when possible and send another update.

Thank you for your suggestion.
```

Use only the existing fixed-template placeholders: `{{name}}`, `{{firstName}}`, `{{lastName}}`, `{{title}}`, `{{author}}`, `{{format}}`, and `{{barcode}}`.

## Wording Alignment

Revise the default Hold placed template so it describes only the later hold-placement event. It should not say that the library has just decided to purchase the item.

Revise the default patron submission note so it explains the two-step communication sequence: purchase-approved email first, then a hold-placement update later when the item is available in the catalog and a hold can be placed.

Existing customized system or library wording must not be overwritten.

## Settings Flow

The new template follows the same scoped email-template model as the existing fixed templates:

- system template values provide defaults
- library template records can override subject/body
- blank library subject/body values remove the library override for this fixed template
- SMTP/sender configuration still controls whether any patron emails can be sent

The settings UI must include the new subject/body fields in form population, summaries, placeholder-helper tracking, dirty state, payload serialization, and backend save handling.

No per-template enabled toggle is added. The template is always available and sends by default when the trigger rule matches.

## Backend Flow

Add `mail.purchaseApproved(app, record, patron)` and route it through the same `dispatch` helper used by the other patron notifications.

The staff action side-effect layer should evaluate the trigger after the request is saved and after immediate promotion logic has settled, using the final persisted status to avoid sending a stale or duplicate message.

Email delivery must be non-blocking:

- successful sends create the usual `email_delivery_events` row
- SMTP/sender-not-configured cases use the existing skipped-email handling
- missing patron email skips without blocking the Purchase action
- send exceptions are logged and do not roll back the request status change

Successful sends should not add a visible request note. Skipped SMTP configuration should continue to use the existing skipped-email note/event path. Send exceptions should be logged without adding a new visible note.

## Migration

Add a PocketBase migration for existing installs:

- create the system `purchase_approved` email template if it is missing
- update the system Hold placed template only if it still matches the old shipped default
- update the system patron submission note only if it still matches the old shipped default
- leave customized system records and all library overrides untouched

Also update the initial migration/default config for fresh installs.

## Tests

Add or update tests for:

- first normal Purchase transition into `outstanding_purchase` sends `purchase_approved`
- repeat save of an already Pending purchase request does not send it again
- direct Purchase transitions to Pending hold, Hold placed, or Closed do not send it
- the existing optional staff purchase reminder still works independently
- the new fixed template loads, summarizes, serializes, and saves from Settings
- default config includes `purchase_approved`
- migration behavior only updates records that exactly match old shipped defaults and preserves customized values

## Out Of Scope

This design does not add per-template enabled toggles, new placeholders, a generic status-transition notification engine, new staff toasts, or patron notifications for staff-created suggestions.
