# Polaris catalog research and direct queue contracts

The staff Search Polaris dialog uses these scoped search, exact-BIB, holdings
and direct-queue contracts. Its bibliographic selection updates the verified
BIB and reconciles Title while preserving other editor drafts.

## Catalog lookup

Continue using `POST /api/asap/staff/bib-lookup` with the staff request helper
and antiforgery token. Request IDs are decimal Int64 **strings**. BIB IDs are
positive Int32 **numbers**. A supplied `requestId` always means a title request.
Additional-copy research should supply its authorized `libraryOrgId` without
a title-request ID.

```json
{"requestId":"9007199254740993","mode":"title","query":"Catalog title"}
```

Modes remain `title`, `author`, `identifier`, `title_author`, and `bib`. The
paired mode uses `title` and `author`; the other text modes use `query`. Exact
lookup uses `bibId`. Searches return at most ten results, `mode`, `libraryOrgId`,
`requestId`, `version`, `totalMatches`, and `totalMatchesKind`.

`totalMatches` comes from Polaris, including when filtered/deduplicated rows
leave fewer results. `totalMatchesKind: provider` means a single provider query.
For existing alternative ISBN/UPC/LCCN and title/author searches,
`provider_maximum` means the largest reported provider total across attempts.
Those result sets overlap; the API does not claim to know their union size and
never substitutes the number of returned rows. The UI should label alternative
search totals accordingly instead of presenting them as an exact union count.
Provider failure remains an error, distinct from a definitive empty search.

Each result retains `bibId`, `title`, `author`, `publication`, `identifier`, and
`format`, with additive `materialTypeId`, `materialTypeSearchCode`, and
`formatIconUrl`. Format labels resolve through Polaris MARC material definitions,
using a bounded, serialized reference read and an in-memory cache for the host
and library (24 hours; one minute before retrying failed reference reads).
Existing textual labels are a fallback; unresolved numeric codes display
`Unknown`. Optional presentation failure does not erase trustworthy BIB results.

The result layout places material artwork and its full readable format label in
a left rail, bibliographic/holdings details in the center, and aligned BIB
actions in a separate right column. Actions stack below at narrow widths.
Catalog material types never set ASAP Material formats.

### Catalog presentation configuration

Settings → Getting started → Polaris catalog and material icons edits **PAC base
URL** and **material-type icon URL pattern** as system defaults with library
overrides (`catalog.pacBaseUrl`, `catalog.formatIconUrlPattern`). The system icon
default remains `SystemSettings.MaterialTypeIconUrlPattern`; its existing
`systemSettings.formatIconUrlPattern` API write name remains supported. No
stored icon patterns are migrated or discarded. LEAP remains system-only.

For either field, SQL/API `null` means inherit; an explicit empty string means
disabled at that scope. An omitted write leaves the value untouched. The editor
shows effective values and marks ownership; **Use system value** clears that
field's override. **Reset inherited overrides** clears both catalog overrides
while preserving the library-owned **PAC organization ID** mapping.

PAC base URLs are absolute HTTP(S) catalog application roots, optionally with a
path, without query, fragment, credentials or placeholders. Example:
`https://catalog.example.org/polaris`. Icon patterns accept HTTP(S) URLs or
existing single-slash local paths, without credentials. Supported tokens are:

| Token | Substitution / requirement |
| --- | --- |
| `{pacBaseUrl}` | Effective PAC base, without trailing slash; only at the pattern's start, followed by `/`. |
| `{pacOrgId}` | Explicit positive library PAC organization ID. Required only when referenced. Never inferred from ASAP, requesting or pickup IDs. |
| `{id}`, `{MARCTypeOfMaterialID}` | Provider material-type ID. |
| `{id2}`, `{MARCTypeOfMaterialID2}` | Same ID padded to at least two digits. |
| `{SearchCode}` | Provider definition's search code, URL-encoded. Required only when referenced. |

Tokens cannot form a URL scheme or host. Unsupported/unresolved placeholders
and unsafe URLs are rejected. Existing literal and absolute/local patterns
remain usable without a PAC base. A blank PAC base disables only patterns that
reference it. Preview uses the same server resolution/substitution code with
synthetic IDs and the current selected settings scope/drafts; it never queries
Polaris or saves settings. An inherited system pattern using `{pacOrgId}` can
only render in a library context with that explicit mapping.

Read-only inspection of the supplied public catalog on November 13, 2026 found
`/polaris/themes/shared/formats/formatid01.gif` and `formatid50.gif`. The
[Polaris material-type documentation](https://documentation.iii.com/polaris/7.6/PolarisStaffHelp/Public_Access_Admin/PDOpower/Type%20of%20Material%20Filters.htm)
describes `themes/shared/formats/formatidNN.gif`; a suitable pattern is
`{pacBaseUrl}/themes/shared/formats/formatid{id2}.gif`. These shared icons do not
consume PAC context. The
[PAC customization guide](https://documentation.iii.com/polaris/PolarisPDFGuides/PolarisPACCustomizationGuide_7.3.pdf)
(page 29) identifies `ctx` as organization, language, then three unused legacy
components: in `27.1033.0.0.12`, 27 is PAC organization and 1033 is English.
Operators must confirm any library-specific mapping; ASAP supplies no guessed
context values. Context-specific paths may use `{pacOrgId}` with literal
language/legacy components as required by that catalog's configured convention.

Runtime resolves presentation per lookup using the already authorized request
library or specific servicing library, including exact-BIB lookup and ownership
enrichment. An All libraries queue does not supply catalog scope. Missing
configuration, required mappings/identifiers, unavailable definitions and failed
images preserve useful labels and a decorative generic icon. No image-sync job
or server image cache is used; browsers cache artwork. The existing host/library
material-definition cache stores provider reference data only; URLs are rebuilt
from current SQL configuration on each lookup, preventing stale cross-library
presentation after edits.

## Holdings and action preview

For each displayed BIB, use exact lookup with `includePatronHolds:false`:

```json
{"requestId":"9007199254740993","mode":"bib","bibId":9001,"includePatronHolds":false}
```

The owning library is derived from the persisted request. Omit `libraryOrgId`
when launching from an All libraries queue; a conflicting explicit library is
rejected. Research without a request requires an authorized operational library
and yields disabled mutation descriptors.

Exact lookup retains bibliographic fields, `holdingsSummary`,
`holdingsUnavailable`, and optional `patronHasHold`. It adds request/version and
library context, material/icon fields when available, `holdingsStatus`,
`holdingsErrorCode`, and `queueActions`.

| Holdings status | Meaning |
| --- | --- |
| `owned_by_library` | Confirmed items at the request's library; counts remain available. |
| `owned_by_consortium` | Confirmed consortium holdings outside that library. |
| `no_holdings` | Successful lookup confirms zero items. |
| `not_holdable` | Items exist, but Polaris reports none holdable. |
| `unavailable` | Holdings cannot be established; summary is null, never zero. |

`holdingsErrorCode` distinguishes `holdings_timeout` from
`holdings_unavailable`. Invalid BIBs return 404; BIB/search provider failures
return 502. Existing scope/identity errors retain their 400/401/403/404 responses.
Unresolved holdings owners are unavailable rather than inferred consortium
ownership. Caller cancellation remains cancellation.

The UI coordinates up to ten independent lookups with a concurrency limit of
three, deduplicates by request/library/BIB, and cancels and discards responses
when search, request, version, library, or session changes.
The backend does not fan out catalog calls. Suppressing optional patron-hold
enrichment avoids repeating that read for every result; the hold processor
performs its own authoritative patron checks.

`queueActions` contains `requestId`, `version`, `libraryOrgId`, `bibId`, `currentAutohold`,
`openAdditionalCopyCount`, `emailPurchaseReminderDefault`, and two descriptors:
`useBib` and `additionalCopy`. Each supplies `action`, `allowed`,
`blockingReason`, `enablesAutomaticHold`, and `targetStatus`. These are previews;
the mutation repeats authorization, version, workflow and provider checks.
The UI renders eligibility reasons without implementing a second workflow policy.
An existing open copy for the source request blocks another combined copy.
Other open tasks for the library/BIB remain informational, preserving ordinary
additional-copy semantics.

## Direct actions

Use the existing `POST /api/asap/staff/title-requests/{id}/action` route. Create
one nonempty GUID for each intended direct action, retain the complete payload,
and use it unchanged for any outcome recovery:

```json
{
  "requestType":"title_request",
  "version":"CURRENT_BASE64_ROWVERSION",
  "operationId":"d496b049-a27a-4bd0-b5fc-166a280680f9",
  "action":"additionalCopy",
  "bibid":9001,
  "staffSelectedBibId":9001,
  "emailPurchaseReminder":false
}
```

Use `action: catalogFound` for **Use BIB & Queue Now**, and `additionalCopy`
for **Buy another copy + Queue Now**. Direct actions require explicit matching
BIB fields, typed title-request identity, operation ID, and current rowversion.
They accept no editor field changes or client automatic-hold assertion.
An optional `status` may be `pending_hold`; the server determines the result.

Both require authorized Suggestion or Outstanding purchase state, an active
actor in the request's library, unlocked BIB/workflow capabilities, and fresh
valid, holdable Polaris evidence. Additional copy also requires positive
confirmed consortium ownership. Incomplete pickup/hold operations, protected
hold history, and duplicate open patron requests retain their existing guards.
The usual automatic-hold opt-out retains the existing duplicate/close policy.

`catalogFound` preserves the persisted automatic-hold preference. With it on,
the initial committed status is Pending hold; with it off, the existing policy
closes the request with `purchased_no_hold`. It creates no copy task.

`additionalCopy` creates exactly one open task for the same source request,
library and selected BIB, **persists `AutoHold = true` even when initially
false**, and commits the normal Pending hold transition. The confirmation must
explain task creation, queueing, and the automatic-hold override explicitly.
An omitted/null reminder choice uses the current actor's additional-copy
reminder default; true and false explicitly select or opt out. The recipient is
the authenticated staff member, resolved under the existing identity locks.

The verified catalog title and identifier are reconciled server-side. Missing
catalog identifiers produce the terminal no-identifier state, never a fabricated
found result or stranded suggestion-only pending check. Source author, workflow
publication timing, custom fields, notes and ASAP Format are preserved. Existing
editor Apply to Form remains a draft-only operation with its own protections.

Queueing does not synchronously place a hold. The existing durable hold
processor checks patron identity and live holds, adopts a matching existing
hold, or runs its established placement/reconciliation protocol. New direct
actions do not introduce another hold-placement or retry subsystem.

## Commit and recovery

The existing organization/staff/request lock order and SQL transaction contain
the request transition, automatic-hold override, identifier/tag reconciliation,
claim policy, additional-copy task, audit events, reminder outbox, and an action
receipt in `TitleRequestEvent.MetadataJson` (`polaris_queue_completed`).
Ordinary additional-copy preview/create stage rules remain unchanged. There is
no schema or external configuration change.

The receipt records operation ID, a fingerprint of the frozen command, actor,
and committed result. Concurrent identical operations recover one receipt;
different operations using the same stale version cannot create another task.
Reusing an operation ID with different payload/actor yields `operation_conflict`.
Recovery rechecks current staff identity and access before returning evidence.
An accepted receipt can be recovered even while Polaris is unavailable.

Success returns the current authoritative request DTO with `committed:true`,
`operationId`, string `additionalCopyRequestId` (or null), `replayed`,
`queueCommittedStatus`, and notification outcomes. `status`, `version`, and
`finalStatus` reflect the current loaded request; `queueCommittedStatus` retains
the original action's committed state if background processing has since
advanced it. If the optional post-commit read fails, the response still reports
the committed outcome with `request:null` and `refreshUnavailable:true`.

A timeout or SQL failure with an unconfirmed commit returns 503,
`code:request_outcome_unconfirmed`, `operationId`, `requestId`, and
`recovery:retry_same_operation`. A lost HTTP response is also uncertain. Retain
the original ID, version and payload; repeat that exact action to recover its
receipt or safely execute after a rollback. Do not generate a new ID, substitute
a newer version, or display success based on a holdings preview. Recheck access
after session changes before any user-initiated recovery.

Stable failures include `invalid_queue_action`, `invalid_version`,
`stale_version`, `invalid_transition`, `bib_not_found`, `bib_not_holdable`,
`bib_not_owned`, `holdings_unavailable`, `bib_validation_unavailable`,
`additional_copy_exists`, `duplicate_open_request`, `operation_conflict`, and
the existing scope/pickup/hold-protection codes. Reminder outbox business keys
remain tied to the one task; dispatch occurs after commit and the delivery
sweep recovers failed dispatch without duplicate reminder creation.

## Validation boundary

The regression suites use the real SQL Server DACPAC, deterministic Polaris
providers, and production pinned PAPI parsing through controlled HTTP handlers.
They exercise forced automatic hold, task association, reminders/defaults,
authorization and version races, concurrency/replays, same-BIB identifier tags,
executed-write rollback, commit acknowledgement loss through service and HTTP,
hold adoption, search modes/counts/materials/icons, holdings and cancellation.
They do not claim live Polaris/Postmark validation.

Browser journeys cover dialog layout, badges/icons, confirmation copy,
asynchronous result ownership, draft integration, keyboard focus and native
200% browser zoom. Automated checks do not complete human visual or
screen-reader acceptance. No live deployment or provider mutation is part of
these checks.

## Validation results — 2026-10-27

| Check | Result |
| --- | --- |
| Exact CI non-browser guard, real SQL Server 2022 | 1,525/1,525 passed; zero failed/skipped; 42m 07s. |
| Retained SQL/Kestrel/Playwright browser partition on the final build | 17/17 passed; zero failed/skipped; 7m 25s. |
| Retained frontend suite | All 61 test files passed; deployment contract rechecked after updating the discovered-test guard. |
| Focused catalog/queue/holdings and retained cancellation checks | 106/106 passed. |
| Release build | Zero warnings/errors. |
| Publishing | Web/DACPAC and separate self-contained win-x64 migration passed; all 73 frontend assets passed source identity verification. |

The October 27 discovery snapshot contained 1,542 .NET cases: 1,525 non-browser and 17 browser,
including 86 new P1 cases. SQL validation used local SQL Server 2022 Developer
Edition `16.0.1210.3`, with deterministic provider boundaries and controlled PAPI
HTTP responses. An initial run of the unchanged grid-sort browser journey
timed out behind its loading overlay; its isolated retry and the complete final
browser rerun both passed. No known unresolved backend blocker remains.
