# Pinned Patron Behavior Notes

Preparation for Slice 1, not implementation or acceptance evidence. Every
source read below used `git show` at
`150b30b776565194260cc327eeeffdfb46475e81`. The port pack overrides source
behavior wherever it explicitly specifies a correction.

## Routes And Browser

- `pb_hooks/main.pb.js` registers GET `/patron`, `/patron/`,
  `/api/asap/config`, POST `/api/asap/patron/login`, and
  `/api/asap/patron/suggestions`. The HTML handler is
  `lib/patron_embed_routes.js`, which reads Referer before Origin, emits dynamic
  frame-ancestors CSP and removes X-Frame-Options. `lib/embed_security.js`
  accepts HTTPS origins (HTTP only local development), deduplicates, and
  supports explicit HTTPS wildcard subdomains. Wildcards exclude the apex and
  require matching port. It adds a concrete request ancestor only if allowed
  by the configured list, never just because a request supplied that origin.
- `pb_public/patron/js/auth.js` resolves the experience library from URL,
  context cookie, then saved local-browser context. Login sends that value;
  the response supplies effective-library UI, email, and allowed pickup list.
  An empty/unavailable pickup list disables submission. Preserve focus/error
  and step behavior; target token persistence and server logout follow the
  pack, replacing legacy token/context semantics.
- `pb_public/patron/js/submit.js` collects form/autohold/custom fields and
  selected pickup. Status 409 shows the duplicate view, 406 shows the weekly
  limit message, and 401 returns to login. Dynamic rendered content must keep
  the project's sanitizer/safe-DOM boundary when this path is ported.

## Login And Effective Library

`lib/patron_routes.js` accepts username/barcode and password/PIN aliases,
requires nonblank credentials, authenticates Polaris and attaches home scope.
An explicit experience library becomes effective only when its effective
`allowAnyRegisteredCardLogin` is true. Otherwise the home library owns work.
The experience library participation check still applies; ordinary home-card
login additionally checks the home library. Effective library selects workflow
eligibility and UI. Target participation uses active Organization rows and the
serialized final SQL session insertion, not the legacy empty-list-means-all
shortcut or mutable durable patron profile.

`lib/patron_codes.js:isEligible` permits login when eligibility is disabled,
the allowed-code list is empty, or Polaris omits PatronCodeID (with a safe
warning); otherwise it checks membership and returns the configured message.
Preserve these actual source semantics unless the pack explicitly overrides
one; do not silently add a new product restriction.

## Submission

`lib/patron_routes.js:createSuggestion` resolves library from server session
context, requires pickup, reloads live patron/reference data, validates pickup
(with one reference refresh when needed), and calls the idempotent pickup
update when the choice differs. Polaris failure returns 502 and no local
pickup update. Then it sanitizes by effective format, creates the suggestion,
applies an auto-claim rule, captures confirmation mail, and attempts immediate
identifier reconciliation. Target transactions and participation/version
checks must follow the pack with no SQL transaction across Polaris.

`lib/route_utils.js` initializes identifier state as pending for a supported
nonblank identifier or skipped_no_isbn otherwise. Immediate submission lookup
uses `jobs.promoteRequestNow`; lookup failure does not undo submission. Inspect
that actual path before implementing its minimal Slice 1 equivalent.

`lib/records/suggestions.js:createSuggestion` snapshots patron contact/name,
code, organization, effective library, selected pickup, custom fields and
timestamps. It title-cases title, trims author/identifier/publication/notes,
uses the effective autohold opt-out policy, creates suggestion status, adds
cross-patron duplicate tagging, and records the created event. Generic later
source edits are not authority for target identifier/pickup mutation policy.

`lib/format_rules.js` requires title for all formats; enforces configured
required/optional/hidden fields; removes hidden input; rejects informational
formats; validates lengths title/author 500, identifier 100, publication 200;
trims/truncates notes at 2000. It uses custom-field normalization and hidden
defaults. Custom format behavior is real and must not be restricted to the
six seeded formats. Keep stable relational format identities in the target.

## Duplicate And Limit Checks

`lib/records/duplicates.js` applies limits per barcode and effective library
over the previous seven calendar days, with default five and configurable
next-available-date text. Duplicate checks cover all statuses/history for that
barcode/library: identifier first, then title+format or supplied BIB. Existing
duplicate context includes created/status/close reason/title/author/format and
match type. A different patron's matching identifier in the same library tags
the new record as Duplicate suggestion with the existing system note.

## Configuration And Mail

- `lib/config_routes.js` assembles public UI plus common-creator and four
  external-search settings, and library participation messaging.
- `lib/config/workflows.js` resolves fields separately and preserves false
  values; source defaults include limit five, opt-out true, cross-library card
  false, patron-code restriction false, autoPromote false. Timeout defaults are
  disabled, with suggestion 30 days and other families 14 days.
- `lib/config/ui_text.js` derives publication defaults from system ui_settings
  then patron_settings_overrides, custom fields from library overrides, and
  format rules from effective material formats unless overridden. Logo and
  alt inherit separately. The pack intentionally corrects record-level UI-text
  fallback to field-level inheritance and supplies relational destinations.
- `lib/mail.js` composes suggestion_submitted before immediate lookup, using
  the entered bibliographic content and current patron email. Source mutates
  the request email during refresh; the pack instead preserves immutable
submission snapshots and snapshots current destination in the outbox. Mail
  configuration failure never rolls back submission. Target uses the required
  five-state, leased, domain-safe durable outbox and explicit suppression.

Remaining referenced helpers, actual migrations, integration package APIs,
frontend sanitizer, defaults, pickup/CSP rules, and tests must be read during
the focused implementation; these notes do not replace executable inspection.

## Pickup And HTML Boundaries

`lib/polaris/pickup_preference_context.js` tries patron organization, configured
nonzero pickup organization, configured organization, then home library for
pickup choices, stopping at the first nonempty result. It refreshes empty
reference data. Preferred branch precedence is CurrentPreferredPickupBranchID,
RequestPickupBranchID, then PreferredPickupBranchID. Validation requires a
numeric ID present in the current allowed list; missing current preference has
an explicit warning. Effective library ownership is not the pickup-list scope.

`pb_public/patron/js/html.js` is the existing rich-text sanitizer boundary:
DOMParser plus explicit tag/attribute allowlists and dangerous href removal.
It also owns configured barcode/PIN label placeholders. Retain the approved
rich-text path with tests; use DOM construction for runtime plain data rather
than extending arbitrary interpolated HTML.
