# Email template placeholder capability map

The staff placeholder helper follows the sending paths currently implemented in the ASP.NET port. It does not infer support from a template name or advertise the full renderer vocabulary for every template.

| Template usage | Verified placeholders | Consuming path |
| --- | --- | --- |
| `suggestion_submitted` | `{{name}}`, `{{title}}`, `{{author}}`, `{{format}}`, `{{barcode}}`, `{{firstName}}`, `{{lastName}}` | `PatronEmailTemplateRenderer.Render` in the patron submission outbox path |
| `rejection:*` | `{{title}}` | `WorkflowProcessingService.AddTimeoutEmailAsync`, which replaces only `{{title}}` |
| `purchase_approved`, `already_owned`, `hold_placed`, `rejected` | None verified in the current ASP.NET port | No implemented sending path currently establishes placeholder support |
| Any other key | None | Capability is intentionally unknown; staff can still edit literal template text manually |

This is a UI capability map only. It does not change template rendering, missing-value behavior, delivery, validation, or saved template contents. When a future sending path gains placeholder support, update the backend contract test, this map, and the staff helper catalog together.
