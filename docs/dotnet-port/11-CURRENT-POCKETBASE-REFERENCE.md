# Current PocketBase Reference Map

**Pinned source:** `clcdpc/asap-pocketbase@150b30b776565194260cc327eeeffdfb46475e81`  
**Purpose:** implementation/review reference only. The target architecture is defined by `01-PORTING-SPEC.md` and related pack documents.

## 1. Current architecture anchors

The current application is a PocketBase application with:

- PocketBase/SQLite persistence;
- Goja JavaScript hooks under `pb_hooks/` and backend modules under `lib/`;
- vanilla HTML/CSS/ES-module frontends under `pb_public/`;
- PocketBase schema/history under `pb_migrations/`;
- Node used for development tests, not as an application runtime/build dependency.

The port should use this implementation as a behavioral reference, not reproduce PocketBase-specific architecture. **Known baseline exclusion:** `clc-carousel-manual-import-example/` at the pinned SHA is unrelated repository pollution accidentally present in `main`; it is not ASAP behavior, a browser dependency, migration input, or archival requirement. Ignore it during behavior mapping and remove it from the port branch/final application.

## 2. Frontend anchors

### Patron

Primary surface: `pb_public/patron/`.

Important behaviors to preserve:

- barcode/PIN authentication against Polaris;
- library/patron eligibility and effective-organization behavior;
- title request submission and validation;
- material-format-driven field behavior;
- pickup-branch behavior;
- patron request/status behavior that exists in the current application;
- embedded use through `/patron` with request-specific CSP `frame-ancestors`;
- accessibility behaviors such as focus handling and live announcements.

### Staff

Primary surface: `pb_public/staff/`.

The staff UI is a lightweight tabbed application. Important modules include:

- `js/app.js` and `js/app/*` — application startup/state/events;
- `js/http.js` / API helpers — request/auth handling;
- `js/grid*.js` — workflow queue data, filtering, rendering, actions, and events;
- `js/settings*.js` and `js/settings/*` — settings/admin surfaces;
- `js/analytics.js` — analytics rendering;
- modal/action modules for request processing and pickup changes.

The .NET port keeps this general frontend architecture. Do not replace it with React, Blazor, Razor Pages, or another frontend framework as part of the port.

## 3. Backend route/domain anchors

Primary route registration starts in `pb_hooks/main.pb.js`, which delegates to route/domain modules.

Useful reference areas:

- `lib/patron_routes.js` and patron helpers — patron authentication/session/request behavior;
- `lib/staff_routes.js` and `lib/staff/*` — staff auth, queues, actions, settings, analytics, admin behavior;
- `lib/records/*` — current record normalization and persistence helpers;
- `lib/config/*` — system/library settings defaults and effective-value resolution;
- `lib/mail*` / `lib/mail/*` — current email composition/transport behavior;
- `lib/polaris/*` — current Polaris endpoint behavior;
- `lib/jobs/*` — scheduled/manual automation behavior.

The route names and payloads under `/api/asap/...` are the default compatibility baseline, but PocketBase-specific auth/record/file semantics should be removed rather than emulated.

## 4. Current jobs to map to Hangfire

Current `lib/jobs/` includes the major automation families:

- `isbn_checks.js` — identifier/availability processing;
- `purchase_promoter.js` — outstanding-purchase promotion checks;
- `hold_placement.js` — hold placement/reconciliation;
- `fulfillment_tracker.js` — positive title-level checkout and terminal hold tracking; the source same-BIB terminal match is intentionally corrected in target section 9.2 (see section 18 below);
- `timeouts.js` — suggestion-creation-age auto-rejection plus pending-hold, hold-pickup and additional-copy timeouts (see section 18 below);
- `weekly_summary.js` — staff weekly summaries;
- shared helpers.

During the port, identify every registered schedule/manual trigger in `pb_hooks/main.pb.js` and route registration so no background path is omitted. The target implementation is Hangfire-backed, item-resilient, idempotent, and sequential/oldest-first inside the persisted finite cycles defined in `01-PORTING-SPEC.md` section 23.1, rather than copying the source per-invocation cursor reset.

Current queue-limit resolution is also operational behavior: `ASAP_<QUEUE>_PAGE_SIZE/MAX_PER_RUN` overrides timeout-family `ASAP_TIMEOUT_*` values for timeout queues, which in turn override global `ASAP_JOB_*` values. Defaults are PageSize 50 and MaxPerRun 500. Preserve/migrate effective external values separately from SQL settings.

Pinned schedule registration to preserve:

- `asap-hold-check`: `ASAP_CRON_SCHEDULE` or `0 * * * *`; this umbrella runs timeouts, an additional ISBN pass, purchase promotion, hold placement, and fulfillment tracking;
- `asap-organization-sync`: `ASAP_ORG_SYNC_CRON_SCHEDULE` or `0 2 * * *`; it refreshes organizations and patron codes;
- `asap-weekly-staff-action-summary`: `ASAP_WEEKLY_STAFF_ACTION_SUMMARY_CRON_SCHEDULE` or `0 20 * * 0` (Sunday 20:00);
- `asap-isbn-check`: `ASAP_ISBN_CHECK_CRON_SCHEDULE` or `*/5 * * * *`.

The target preserves the hourly umbrella's relative **business-phase ordering** as one orchestrator, prefixes acquired-hold recovery (including inactive libraries), and removes identifier processing from that hourly sequence. Finite queue progress and later-phase timeout guards are the intentional target correction; see `01-PORTING-SPEC.md` section 23.1. This is not justified as duplicate removal: the source has two different ISBN implementations. `processPendingIsbnChecks()` (hourly path) owns retry counting/max-retry/error behavior; `processPendingSuggestionIsbnChecks()` (dedicated five-minute path) owns BIB persistence/reconciliation, workflow tags, `lastChecked`, and `skipped_no_isbn`. The source `searchBibs()` path can also collapse failed subrequests into an eventual `not_found` result when no successful match remains; the target deliberately fixes that ambiguity so provider failures cannot become false catalog conclusions. The .NET canonical five-minute processor must intentionally merge/correct these semantics as defined by `01-PORTING-SPEC.md`.

## 5. Polaris integration anchors

Current `lib/polaris/` contains endpoint-specific behavior for:

- API authentication/signing;
- patron authentication and patron data;
- bib lookup/reconciliation;
- organizations;
- patron codes;
- pickup-branch and pickup-preference behavior.

The .NET port does **not** translate these modules one-for-one. It uses the current/prerelease `Clc.Polaris.Api` package directly and extends that package when required rather than creating a second protocol client.

Initial .NET behavior deliberately differs from the current staff-Polaris identity path: all PAPI operations use configured application/system credentials and the configured system Polaris user ID for mutation attribution, while ASAP's own event/audit records identify the actual Entra staff actor.

## 6. Request/workflow data anchors

### Title requests

Current `title_requests` carries, among other fields:

- patron relation and patron snapshots;
- library/patron/pickup organization snapshots;
- title, author, identifier, publication, exact-publication-date, custom fields;
- material format;
- workflow status and close reason;
- BIB ID and autohold state;
- claim ID/display/timestamp/type/rule;
- notes;
- `legacyId`;
- ISBN/promoter/background-processing state;
- timestamps.

Pinned source fidelity note for pickup state: `lib/staff/title_request_actions.js` exposes a dedicated pickup-preference mutation that validates the live Polaris patron/pickup context, updates Polaris, and only after success writes the request's `preferredPickupBranchId`/`preferredPickupBranchName` plus its note/event. A changed-since-load live pickup is rejected and a failed Polaris mutation leaves the request fields unchanged. The target therefore treats these two fields as the request's **current recorded pickup preference**, mutable only through that dedicated validated workflow; they are not generic editable patron snapshots.

The source `isbnCheckStatus` select legally permits: `pending`, `found`, `not_found`, `error`, `error_max_retries`, `skipped_no_isbn`, and `found_in_polaris`. The old hourly checker actively writes `error` for a missing identifier; `found_in_polaris` remains a historical schema value even though the pinned code no longer has a normal writer for it. These values require the exhaustive conditional migration map in `04-MIGRATION-CUTOVER.md`; do not feed them directly into the narrower target SQL constraint.

Canonical target workflow status codes remain:

- `suggestion`
- `outstanding_purchase`
- `pending_hold`
- `hold_placed`
- `closed`

Known close-reason codes to preserve/normalize deliberately include:

- `rejected`
- `Silently Closed`
- `hold_completed`
- `hold_not_picked_up`
- `hold_unclaimed`
- `hold_cancelled`
- `hold_expired`
- `duplicate_hold`
- `manual`
- `purchased_no_hold`

Do not silently map an unknown historical business value into an unrelated valid value during migration.

### Events

`title_request_events` is the historical event trail. The current model primarily records request, event type, status/close reason, actor type/name, message, metadata, and timestamps; it does not provide a trustworthy StaffUser relation for historical staff actors.

Target migration consequence:

- historical staff events preserve `ActorName` but do not infer `StaffUserId` from display text;
- new .NET staff events write both StaffUser FK and actor-name snapshot;
- unknown historical event type becomes constrained `legacy` with original value in metadata;
- unknown historical actor type blocks import pending explicit correction/mapping.

### Notes

Current system-generated workflow information can exist in both request notes and events. Preserve that observable behavior for the initial port rather than trying to deduplicate historical presentation during migration.

## 7. Additional copies

`additional_copy_requests` is a separate workflow, not a special title-request status in persistence.

Important behavior:

- can be created from a source TitleRequest;
- stores its own bibliographic/library/request state;
- has independent Open/Closed lifecycle and claim behavior;
- source claimant information is copied at creation where current behavior does so, then evolves independently; target open AdditionalCopy claims therefore participate independently in StaffUser deactivation/scope-contraction cleanup, using a concise system Notes entry plus administrative cleanup counts while closed claimant snapshots remain historical;
- source deletion must not delete the additional-copy record; target FK uses nullable/set-null semantics;
- source deep links may include `stage` + `request`.

The .NET port keeps this as a separate table/domain feature and implements it as its own vertical slice.

## 8. Staff identity and authorization anchors

The current staff profile has user-owned preference fields that are part of observable behavior and must not be mistaken for legacy auth machinery:

| Current PocketBase field | Current use | Target |
|---|---|---|
| `weekly_action_summary_enabled` | opt in/out of weekly action summary | `StaffUser.WeeklyActionSummaryEnabled` |
| `weekly_action_summary_email` | weekly summary recipient and, in several current paths, a broader staff-notification address | `StaffUser.WeeklyActionSummaryEmail` plus migration fallback source for primary `NotificationEmail` |
| `purchase_reminder_default` | default purchase-reminder choice in staff workflow | `StaffUser.PurchaseReminderDefault` |
| `additional_copy_reminder_default` | default additional-copy reminder choice | `StaffUser.AdditionalCopyReminderDefault` |
| `default_mine_unclaimed_filter` | default staff queue mine/unclaimed filter | `StaffUser.DefaultMineUnclaimedFilter` |
| `email` | PocketBase auth/contact field; may be generated placeholder | candidate source for target `StaffUser.NotificationEmail` only when it is a real non-`@staff.asap.local` address |

These preference fields are returned in the current public staff JSON/profile model and updated through the staff profile endpoint. They survive migration. Current notification behavior is less clean: the weekly-summary job requires both `weekly_action_summary_enabled = true` and a nonblank `weekly_action_summary_email`, while `weekly_action_summary_email` is also used by purchase/additional-copy reminder paths and some assignment notifications fall back to `staff_users.email`. Generated `@staff.asap.local` values in `email` are placeholders. The target intentionally normalizes this to nullable primary `NotificationEmail` plus optional weekly-only `WeeklyActionSummaryEmail`. This can change effective recipients—including making a summary-enabled user with blank legacy weekly email newly eligible through the primary-address fallback—and the migration delta report in `04-MIGRATION-CUTOVER.md` makes that change explicit.


Pinned `lib/jobs/weekly_summary.js` currently builds one consortium-wide action summary: title-request queries are filtered by workflow status rather than library, additional-copy queries are likewise unscoped, and the same resulting counts/samples/links are sent to every opted-in staff recipient. The .NET target intentionally does **not** preserve that cross-library exposure: staff/admin summaries are constrained to their own active library, while super-admin summaries may cover the active consortium.

The pinned protected manual summary route also forwards a `force` option, and the source job distinguishes forced executions from the ordinary completed-period run. The target preserves that explicit resend behavior without weakening normal recipient+period idempotency: each accepted forced invocation gets one durable `ManualRunId`, retries of that same Hangfire invocation reuse it, and a later explicit force receives a different run identity.

Current `staff_users` includes legacy PocketBase/Polaris identity fields. Do not carry those fields forward merely because they exist.

Target staff identity is:

- Microsoft Entra authenticates and supplies validated `tid` + `oid`;
- SQL `StaffUser` authorizes;
- normalized staff email is the durable local lookup/authorization key;
- Entra tenant/object IDs remain nullable last-observed metadata; display and notification values remain independently readable and app-owned;
- roles remain `staff`, `admin`, `super_admin`;
- normal staff/admin are library-scoped; super-admin is system organization 1. In the target, the staff relationship remains valid when a library is inactive, but authorization requires both the StaffUser and Organization to be active;
- no general JIT creation;
- target staff tickets retain StaffUser ID, normalized authentication email, and sign-in tenant. Every request compares current email and tenant allowance; authentication-email changes invalidate old cookies while OID changes do not.

Migration uses each PocketBase staff user's real email directly. Active missing/invalid/placeholder emails and duplicate normalized emails block import; Entra metadata remains null until successful sign-in.

The old hourly identifier processor can write `isbnCheckStatus = found` without persisting `bibid`, while the newer dedicated processor persists the BIB and reconciliation/tag state. Therefore target migration cannot treat every legacy `found` as canonical: `found` without a supporting BIB is reported and blocks until resolved. Likewise, current generic staff editing can change `identifier` and `bibid` independently without automatically invalidating all identifier-derived fields/tags; the target intentionally corrects this by making identifier changes atomic invalidation boundaries.

## 9. Organization/reference-data anchors

Current `polaris_organizations` caches the Polaris hierarchy and uses Polaris organization IDs as the external identity.

Known organization codes:

- system: `1`
- library: `2`
- branch: `3`

Target persistence changes:

- organization ID 1 is the real Polaris system organization and remains permanently active;
- persist system + participating libraries + historically referenced libraries required for FKs/history;
- branches and other live reference lists are memory-cached from Polaris rather than persisted as a full mirror;
- newly discovered libraries start inactive until a super-admin enables participation;
- inactive historical libraries remain available for historical integrity.

Other reference data such as patron codes/material types/pickup branches should be treated as live Polaris reference data with short memory caching unless explicitly modeled elsewhere in the target design.

## 10. Material formats and auto-claim anchors

Current material-format behavior supports system and library-scoped definitions/overrides, enabled state, ordered display, field rules/labels, and custom library formats.

Target model improves identity semantics:

- stable `MaterialFormat` row/ID;
- separate nullable library `MaterialFormatOverride` for system formats;
- library custom formats are owned rows;
- request and claim-rule references use stable IDs rather than free-form codes;
- code is immutable after creation; label/config remains editable;
- system-disabled formats can be re-enabled by a library override;
- library custom formats can be hard-deleted only if unreferenced.

Auto-claim behavior to preserve:

- one active rule per library + format;
- no retroactive sweep when a rule is saved;
- evaluate on submission and format change;
- never overwrite a manual/legacy claim;
- disabled/unavailable target causes a skip rather than an unsafe reassignment; the pinned runtime defensively tolerates a rule whose staff target is missing/inactive/out-of-scope instead of assigning to someone else;
- migration preserves that safety deterministically: a source-active rule remains active only when its mapped target StaffUser is active and scope-eligible, otherwise it is imported as inactive historical configuration and the normalization is reported;
- .NET version records exact versioned rule history.

## 11. Settings/template/branding anchors

Current settings are split across multiple PocketBase collections. The target intentionally **does not** reproduce either that collection layout or a catch-all SQL settings row. It maps configuration into domain-specific system-only, inheritable-scalar, whole-set, and library-owned tables according to `13-SETTINGS-SCOPE-INVENTORY.md`.

Important effective-value behavior:

- system defaults plus nullable library field overrides in `WorkflowSettings`, `PatronSettings`, and `EmailSettings`;
- whole-set inheritance/replacement for publication options, common-creator terms, and allowed patron codes;
- library settings UI should expose inherited vs overridden state;
- reset removes the override and re-exposes system value;
- true system-only application configuration lives in `SystemSettings`; Polaris configuration is isolated in system-only `PolarisSettings`;
- email/Postmark configuration supports system defaults plus library overrides; only sender/configuration semantics migrate from the SMTP-era source, while the new Postmark token is target-only cutover configuration.
- built-in material-format field behavior becomes typed relational columns and custom-field/format behavior becomes `MaterialFormatCustomFieldRule`, eliminating the competing legacy JSON rule representation.

### Email templates

Current templates include built-in workflow templates and rejection-template behavior. Target uses relational `EmailTemplate` with system/library inheritance, hiding/overriding, custom rejection templates, and self-reference to source/system templates when useful.

### Branding

Current logo is a PocketBase file field in `ui_settings`; the URL resolves through PocketBase file storage. Therefore the one-time export must have access to both the stopped PocketBase SQLite database **and PocketBase file storage**. Target stores branding bytes in SQL and validates accepted image types/dimensions.

## 12. Email anchors

Current email is largely synchronous transport behavior plus delivery/audit records. Two source behaviors are important when translating it: missing mail/sender configuration is treated as a skipped/failed notification rather than a reason to roll back the owning suggestion/workflow mutation, and staff assignment/reminder messages contain request-specific title/author/format/link data rather than content-free nudges. The target therefore preserves non-fatal business behavior while treating delayed staff mail that carries scoped request data as authorization-sensitive at delivery time.

The target deliberately changes reliability mechanics:

- Postmark via `Clc.Postmark.Api`;
- durable SQL outbox with `pending`/`sending`/`sent`/`failed`/`suppressed` state;
- Hangfire delivery/retry;
- sender/recipient/content snapshot at business transaction time for deliverable messages; a terminal suppressed intent may omit the specific sender/recipient value whose absence caused suppression;
- effective transport credential resolved at send/retry;
- transient retry bounds + visible `failed` state/manual retry with payload retained while retryable; every expired sending lease is conservatively transport-ambiguous and fenced from stale-worker updates; authorization-sensitive staff rows persist the ordinary-versus-weekly recipient-address rule, revalidate current scope and that rule's effective address, and become terminal `suppressed` when stale; only terminal sent/suppressed payload is retention-purge eligible;
- historical delivery/audit data migrated, but no historical email is placed into the new outbox for replay.

When porting each workflow, include its email side effect in that vertical slice rather than postponing all email behavior to the end.

## 13. Analytics anchors

Current analytics endpoint loads scoped title/additional-copy records and aggregates metrics in application JavaScript, including summary counts, stage counts, close reasons, aging, exceptions, and first-hold-placement timing derived from events.

The target preserves the user-facing analytics feature and authorization scope but moves aggregation to SQL-side queries through Dapper/ADO. Do not reproduce the current load-all-and-loop implementation merely for structural parity.

## 14. Patron embed/CSP anchor

Current `/patron` response sets a request-sensitive Content-Security-Policy `frame-ancestors` value and removes conflicting frame behavior so approved external library sites can embed the patron application.

Target rules:

- `/patron` remains a dynamic HTML endpoint specifically so the CSP can be set correctly;
- ordinary static assets remain static;
- embed-origin allowlist remains system-wide and super-admin controlled;
- library admins may see effective state but may not change this trust boundary.

## 15. Migration history as evidence, not target design

`pb_migrations/` is valuable for understanding historical data shapes and invariants, including later-added features such as weekly staff summaries, scoped lookup/settings data, workflow tags, deletion audit, additional copies, and later security/eligibility changes.

Do **not** replay PocketBase migrations into SQL or mechanically mirror every historical field. `Asap.Migration` targets the final frozen PocketBase schema at the tagged source version and performs explicit typed transformations into the normalized SQL model.

## 16. Reference rule for implementation

For configuration migration, do not assume raw persisted PocketBase fields are the effective runtime values. In particular, the pinned Staff URL resolver uses persisted `staffUrl`, then `ASAP_STAFF_URL`, then `ASAP_PUBLIC_URL`, then localhost; the missing-record/default helper separately considers `ASAP_BASE_URL`. The migration snapshot must call or equivalently reproduce the actual pinned resolution path and record provenance.


When a slice needs to determine legacy behavior:

1. inspect the current route/domain implementation at the pinned PocketBase tag/SHA;
2. inspect its tests and migrations where they clarify invariants;
3. compare with the binding target design in this pack;
4. preserve behavior by default;
5. if a deliberate target decision conflicts with PocketBase, implement the target decision and test/document the deviation;
6. never add a compatibility shim merely to preserve a PocketBase implementation detail that is not part of the observable contract.

## 17. Earlier seven-finding target corrections at the pinned baseline

The source baseline remains `clcdpc/asap-pocketbase@150b30b776565194260cc327eeeffdfb46475e81`; no newer commit is substituted. The following earlier R1-R7 corrections remain binding. The latest three findings are separate F1-F3 entries in section 18; they refine only timeout semantics, terminal-hold identity and migration placement evidence.

| Finding | Pinned source anchor / boundary | Binding target contract |
|---|---|---|
| 1 | `lib/jobs/hold_placement.js` uses create then status-5 reply; `lib/polaris/bib/holdings.js` uses provider RequestGUID and TxnGroupQualifer/TxnGroupQualifier plus TxnQualifier. Source does not establish durable replay safety. | `01-PORTING-SPEC.md` section 9.1: durable context, exclusive phase ownership, evidence-safe recovery, inactive acquired-work exception, operator-required ambiguity |
| 2 | `lib/staff/title_request_actions.js` rejects explicit BIB replacement in hold_placed; source identifier editing is not proof that the target may clear the placed BIB. | Stage-aware edits/DTOs in `07-API-FRONTEND-COMPATIBILITY.md` section 14.2; preserved recorded legacy guard in migration section 6.10 |
| 3 | `lib/additional_copies.js` reopen clears closure fields but retains claimant fields. | Revalidate on activation; preserve eligible claim or clear with Notes, no replacement (`01-PORTING-SPEC.md` section 20.1) |
| 4 | A mapped legacy claimant can still be ineligible after role/library changes. | Map then validate every effective open claim, preserve closed attribution and valid dormant-library relationships (`04-MIGRATION-CUTOVER.md` section 6.3) |
| 5 | Entra/cookie authorization is a target replacement, not PocketBase parity. | Current loaded tenant/binding/activity/scope predicate and usable-admin/config checks (`01-PORTING-SPEC.md` section 7.6) |
| 6 | `lib/job_queue.js` resets its cursor each invocation while counting unresolved scanned rows. | Persist finite keyset-cycle progress while retaining sequential caps and phase ordering (`01-PORTING-SPEC.md` section 23.1) |
| 7 | SQL Server/DACPAC/Hangfire deployment is target-only. | All production DB mutation, including dependency-owned DDL, enters backup/quiescence/compatibility flow (`05-DEPLOYMENT-OPERATIONS.md` sections 9-10) |

The hold, provider helper, AdditionalCopy, queue helper, and title-action source anchors above were inspected at this SHA during the earlier seven-finding remediation. This does not assert the deployed production SHA or live Polaris negative-result/visibility guarantees; absent provider proof, automatic mutation replay is forbidden by the target contract.

## 18. Final three-finding source-fidelity corrections

These checks use executable source at the unchanged pin, not newer branch behavior or planning comments. F1 preserves the source business rule, F2 deliberately fixes a source defect, and F3 makes the existing target migration guard recognize the actual source representations. The prior R1-R7 mechanisms remain in place.

### F1 - Timeout meaning is preserved

`lib/jobs/timeouts.js` implements the following strict age tests (`timestamp < cutoff`, equality does not expire):

| Source function | Eligible source state / age field | Source closure / notification | Target authority |
|---|---|---|---|
| `processOutstandingTimeout` | `suggestion` / `created` | `closed/rejected`; configured outstanding-timeout rejection email/template through `mail.autoRejected` | `01-PORTING-SPEC.md` section 23.2; effective keys remain `OutstandingTimeout*` |
| `processPendingHoldTimeout` | `pending_hold` / `updated` | `closed/rejected`; no timeout notification | Same section 23.2 |
| `processHoldPickupTimeout` | `hold_placed` / `updated` | `closed/hold_not_picked_up`; no timeout notification | Same section 23.2 |
| `processAdditionalCopyTimeout` | AdditionalCopy `open` / `updated`, falling back to `created` when absent | Independent task `closed` through `lib/additional_copies.js` `closeTask`; no TitleRequest close reason or timeout notification | Same section 23.2 |

Each source family checks its effective enabled setting and subtracts configured calendar days. The target makes the clock/business-timezone interpretation explicit, retains the already-settled active-Organization gate and durable mail behavior, and keeps age eligibility independent of finite-cycle scan order. Source `lib/jobs.js` `runScheduledHoldCheck` runs these four timeouts before identifier work, promotion, placement and fulfillment. The target's existing separate canonical identifier job/recovery prefix is unchanged. No source `OutstandingTimeout` predicate expires an approved `outstanding_purchase` awaiting a BIB.

### F2 - Terminal matching is an intentional correction

`lib/jobs/fulfillment_tracker.js` `processCheckedOut` first accepts a current checkout with the request's BIB. It then checks unclaimed, cancelled and expired lists and closes on the first same-BIB row, without a HoldRequestID match. Preserve the positive checkout rule; do not preserve the unsafe terminal association. `01-PORTING-SPEC.md` section 9.2 requires the tracked final hold identity plus expected BIB/patron, independent of provider row order, and preserves state on missing/ambiguous identity or required provider-read failure.

`lib/polaris/bib/holdings.js` `placeHold`/`replyToHold` use `RequestGUID` and transaction qualifiers for the create/prompt exchange. Those values are not established by that source as the final persisted `HoldRequestID`. The public PAPI create contract documents request/transaction messaging fields; `PatronHoldRequestsGet` separately exposes `HoldRequestID` and `BibID`. These are distinct roles, not interchangeable names. The selected target package/server version must prove any mapping it uses; these reference pages do not establish a deployed version or guarantee an authoritative GUID-to-hold lookup.

New placement and live existing-hold adoption capture a supplied/proven final hold ID on the existing HoldPlacementOperation. A genuinely proven placement without an available final ID is uncorrelated for terminal tracking, not permission to guess an ID or replay creation. Migration does not create such an operation; historical BIB protection alone never authorizes terminal closure. Manual closure and the existing configured timeout paths remain available.

### F3 - Placement evidence is normalized before transformation

`lib/records/helpers.js` defines all five terminal reasons: `hold_completed`, `hold_not_picked_up`, `hold_unclaimed`, `hold_cancelled`, and `hold_expired`. Some raw canonical reasons written by jobs are not accepted by every branch of the source close-reason normalization helper; a null derived relation does not erase a recognized raw code. Resolve exported taxonomy relations and known aliases without copying a lossy helper fallback.

`lib/jobs/hold_placement.js` writes dedicated `hold_placed` events. `lib/staff/title_request_bib_actions.js` `maybePromoteExistingPolarisHold` instead moves a request with an existing hold into `hold_placed`; `lib/records/suggestions.js` `updateTitleRequest` records that through ordinary `status_changed` with `toStatus`. `recordEvent` stores status references as relations and catches event-write failures. Thus an event literally named `hold_placed` is neither the only valid evidence nor a mandatory companion to a current placed state or terminal close reason. Other explicit entered/left-placed transitions are also dependable stage evidence. The source opt-out path can record `hold_skipped` with `toStatus = hold_placed`; stage-history protection is not a claim of external hold creation.

`04-MIGRATION-CUTOVER.md` section 6.10 defines the complete union, known/null historical BIB, deterministic existing-event metadata/provenance and reconciliation counts. Closed status, a BIB, or free-text hints alone do not establish placement. Conflicting/ambiguous evidence follows the existing blocker/reporting path; no operation, provider success or final hold ID is fabricated. Permanent edit/reopen protection is explicitly separate from current external-hold correlation.

### Executable and provider references checked

All repository paths below are relative to [the pinned commit](https://github.com/clcdpc/asap-pocketbase/tree/150b30b776565194260cc327eeeffdfb46475e81); no newer implementation was substituted:

- [timeouts.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/jobs/timeouts.js), [jobs.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/jobs.js), and [additional_copies.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/additional_copies.js).
- [fulfillment_tracker.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/jobs/fulfillment_tracker.js), [hold_placement.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/jobs/hold_placement.js), and [polaris/bib/holdings.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/polaris/bib/holdings.js).
- [title_request_bib_actions.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/staff/title_request_bib_actions.js), [records/suggestions.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/records/suggestions.js), and [records/helpers.js](https://github.com/clcdpc/asap-pocketbase/blob/150b30b776565194260cc327eeeffdfb46475e81/lib/records/helpers.js).
- Official PAPI [HoldRequestCreate](https://documentation.iii.com/polaris/PAPI/current/PAPIService/PAPIServiceHoldRequestCreate.htm) and [PatronHoldRequestsGet](https://documentation.iii.com/polaris/PAPI/current/PAPIService/PAPIServicePatronHoldRequestsGet.htm), checked September 12, 2026. Reference semantics only; version-matched package/server tests remain required.

Required target proof is `06-TESTING-CI.md` section 10.2 F1-F3 and `08-RELEASE-VALIDATION-NOTES.md` section 2.2, not an assertion that this documentation task executed the target application.
