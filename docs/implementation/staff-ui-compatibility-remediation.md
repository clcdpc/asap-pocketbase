# PR 311 staff UI compatibility remediation

Behavioral reference: PocketBase commit
`150b30b776565194260cc327eeeffdfb46475e81`.

| Corrected path | Pinned source | Preserved behavior / .NET adaptation |
| --- | --- | --- |
| BIB search | `lib/staff/lookup_routes.js`, `lib/polaris/bib/search.js`, `lib/polaris/bib/holdings.js` | Exact BIB lookup and identifier, title, author, title/author searches; ISBN/UPC/LCCN fallback, boolean/title fallback, material exclusions, scoring, ten results, and rendered bibliographic metadata. Exact lookups include library/consortium ownership counts and holdability; the editor's optional barcode triggers a scoped, read-only duplicate-hold check. Uses the existing cancellable CLC Polaris client. Upstream failures use the port's service-error response rather than silently claiming no matches. |
| Patron lookup | `lib/staff/lookup_routes.js`, `lib/polaris/patron.js` | Barcode-like queries attempt direct lookup; a not-found query falls back to quoted `PATNF` name search. An explicit barcode selection does not fall back. Resolve actual membership and patron code before exposing at most ten candidates; preserve selected/multiple/404 responses. |
| Staff creation scope | `lib/staff/admin_routes.js`, `lib/staff/effective_library.js` | Resolve the actual patron on every submission. Compare actual home library with the effective staff library unless cross-library cards are enabled. Enforce patron-code eligibility before pickup reads/updates or suggestion writes. Session fields do not supply patron membership evidence. |
| Staff creation limit | `lib/staff/admin_routes.js`, `lib/records.js` | Explicit `staffSubmission` service option bypasses only the public submission-count check, matching `skipLimits`. Public submissions retain that check. Duplicate, format, identifier, custom-field, pickup, and other validation remain in the existing service. |
| Email settings | Target settings inventory and existing SQL email contract | Replace SMTP credentials with Postmark token configuration. Token/sender settings use system defaults with library overrides. Blank token preserves, replacement updates, explicit clear removes the scoped token and restores inheritance for a library. Only a configured-token boolean reaches the browser. Keep existing section IDs for navigation compatibility. Save the loaded settings version required by the .NET mutation contract. |

`/api/asap/staff/email-status` reports effective Postmark configuration for the
authorized scope. A missing token or sender address reports unconfigured.
It is a configuration check, not a live provider connectivity probe. The
[temporary file transport](temporary-email-transport.md) remains intentional;
the deferred real Postmark adapter is not implemented by this remediation.
Per-submission confirmation-email behavior is unchanged.

## Validation limits inherited from the pinned UI

A supplemental axe scan of the Polaris results modal found low-contrast text
on `#polaris-search-status`, `.polaris-warning`, and the additional-copy button.
The corresponding markup and
styles exist in the pinned source and are deliberately unchanged. The populated
legacy Staff access table also contains unlabeled role selectors and low-contrast
Remove buttons from that source. Browser journeys collect accessibility findings
and fail the gate after exercising all workflows, so these old defects do not
prevent verification of the remaining changes. No axe rules or findings are
suppressed. Existing states plus Polaris search and the changed Postmark panel
are scanned.

The Postmark panel also exposes existing contrast defects in the reset button,
the retained Save & Send Test button, the successful-save status text, and toast
styling. Their classes/styles match the pinned implementation. The new token
indicator uses accessible text styling; the inherited shared controls are left
unchanged as requested.

The pinned new-suggestion form does not render library custom fields and uses
the currently loaded publication options. The compatibility settings response
now includes those effective publication options where this form reads them.
The browser fixture loads its library settings before creating a DVD suggestion;
its book format requires a custom
field unavailable in that legacy form. The server continues rejecting invalid
publication values and missing required custom fields. This remediation does
not redesign those pre-existing controls or weaken their server validation.
Saving the Postmark panel sends only email transport/sender fields, so it does
not rewrite templates, publication choices, or other settings from that panel.

These tests use real SQL Server and deterministic Polaris/transport boundaries;
they do not contact live Polaris or Postmark services.

## Validation recorded on 2026-09-21

- Complete CI non-browser .NET partition: 337 passed, none failed or skipped,
  including 18 new compatibility cases. The CI discovery guard is updated to 337.
- Complete retained JavaScript suite (`npm test`): all 20 test files passed.
- Release solution build and Web publish: succeeded; build had zero warnings/errors.
- Existing Playwright workflow (`npm run test:browser`): two journeys passed and
  the staff journey failed the unchanged serious/critical axe gate. All eleven
  primary staff states and the added BIB, barcode/name lookup, suggestion creation,
  and Postmark token/sender workflows completed before that final assertion.
  Final failures were legacy contrast in the Polaris result status/additional-copy
  button and Postmark reset/test buttons, save status, and toast. Earlier runs also
  exposed the populated Staff access issues described above.
- Final diff/whitespace review: no unrelated changes or generated output included.

The remaining browser gate failure is deliberately retained legacy behavior,
not a suppressed check. The separately deferred live transport remains a release
blocker, not part of this compatibility change.
