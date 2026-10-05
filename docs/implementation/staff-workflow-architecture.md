# Staff workflow architecture

Implementation campaign: [#351](https://github.com/clcdpc/asap-pocketbase/issues/351).

The dedicated branch is `refactor/staff-workflow-architecture`, based on
`codex/staff-next-parity` at `4a3eac507a61768e722a96df88e94a75a65b1007`.
Child issues #353–#365 are implemented sequentially, with independently
validated completion commits and execution evidence on each issue.

The ownership contract is explicit: session identity owns the actor, the router
owns browser history, controllers own feature state and DOM, and local draft
scopes own mutation admission. Reads are disposable; submitted mutation
outcomes and actor-bound recovery evidence have independent lifetimes.

Issue #345 remains the overall PR #344 closure gate. Neither PR is merged by
this campaign. Test-IIS activation, live providers and production operations
remain separately authorized work.

## Characterization baseline (#353)

The application journeys in `staff_navigation_drafts_ui.test.js` cover parent
replacement, inline drafts, transient dialogs, rapid/rejected history, Recent
Requests, Settings traversal, Profile, Operations, actor-scoped Copy recovery
and destructive ledgers. `staff_request_actions_ui.test.js` covers rowversions,
pickup/hold recovery and authoritative outcomes after access loss or failed
refresh. Research, Suggestion, Analytics and dialog-focus suites retain deferred
completion and replacement-context assertions.

Seven additional application journeys pin editor Revert with a competing draft,
Copy reminder cancellation, definitive inline failure, clean transient disposal,
and Recent Requests from Copy/Settings. At this baseline an accepted editor
Revert replaces competing inline forms, and a Copy reminder is not registered
as a dirty draft. These are explicit current limitations, rather than desired
future contracts; the owning extraction phases will correct them and update
their tests. Controller dispose/recreate contracts are tested when each actual
controller API is introduced, without inventing an API in characterization.

## Final ownership

| Owner | Responsibilities and lifetime |
|---|---|
| `session-identity.js`, `session.js` | Accepted actor epoch, separate preference revision, session reads, sign-out attempt, access-loss lifecycle fan-out and composed unload guards. |
| `router.js`, `navigation.js` | Browser history and accepted entries; guarded route validation, owner leave policies and named activation/detail intents. Navigation never inspects feature form controls. |
| `shell.js` | Workspace frames, identity, status, scoped readiness reads, Recent Requests and actor-bound outcome receipt presentation. |
| `queues.js` | Independent Title and Copy data, Grid.js instances, filters, read slots, accepted context evidence and live opener focus ports. |
| `detail-host.js` | Exclusive mounted lease, dialog frame, close/Escape dispatch and live focus handoff. |
| `title-detail.js`, `request-editor.js`, `request-workflows.js`, `custom-fields.js` | Authoritative Title/version, local drafts, configuration, edit/BIB verification, assignment, choices, pickup, hold recovery and command results. Children receive immutable context and bound commands. |
| `copy-detail.js`, `copy-creation.js` | Copy detail/commands; parent-owned creation UI, independent submitted attempt, actor-scoped stored recovery and separate list review evidence. |
| `suggestion-controller.js` | Servicing library, patron/configuration reads, configured fields, verification, true draft baseline, create attempt and conflict UI. Emits named created/existing Title intents. |
| `bulk-delete.js` | Disposable preview/modal and independent frozen sequential destructive runner/ledger. No replay of attempted items or new reload persistence promise. |
| `profile-controller.js` | Profile draft, save/review attempt and accepted same-actor preference update. |
| `operations-controller.js` | Local visible reads and separately retained supported operation identity/body/scope. Leaving the page does not discard an attempt. |
| `settings.js`, `settings-domains.js` | Independent Settings scope, main/domain drafts and separate staff profile/access/create drafts, roster reads, command truth and scope-specific review. |
| `analytics.js` | Own scope/range, cancellable reads, formatting and DOM. |
| `research.js` | Injected parent-bound Polaris dialog service and stateless verification/formatting helpers. |
| `draft-scope.js`, `mutation-outcome.js`, `url-utils.js`, `recent-requests.js`, `ui.js` | Small local admission primitive and stateless contract, routing, serialization and rendering helpers. |

`workflow.js` locates roots, constructs services/controllers, wires named ports,
fans out available-library information, and starts/disposes the application.
It contains no endpoints, forms, recovery serialization, mutation error
classification, history mechanics or feature reset inventory. There is no
replacement global feature store, event bus, framework or import cycle.

## Ownership transfer and command authority (#367)

Navigation owns a single-use prepared departure. `prepareDeparture` inspects
applicable owners and obtains consent without discarding state. Its opaque
handle captures the actor epoch, source context, intent revision, owner
lifetimes and dirty stamps. `revalidate` inspects again, rejects changed owners
or contexts and pending commands, and obtains fresh consent for new or changed
drafts. `commit` repeats that check and discards each applicable source once.
Repeated commits are rejected. A newer intent supersedes an older prepared
departure without destroying its source. Preference revisions of the same
actor do not replace the actor epoch.

Any target that needs authority validation is prepared before departure
commits. Catalog admission, initial Settings entry, Settings scope replacement,
history traversal and Title/Copy detail loading retain the current source
through failed, unavailable, cancelled or superseded reads. Target detail and
configuration reads do not acquire a host lease. Queue projections needed for
detail alignment are staged independently, then accepted only at commit.
Router alone accepts the resulting route. Cached, already-authoritative views
and synchronous close/context intents can commit immediately; subsequent list
refreshes populate those admitted owners without changing command authority.

Owner replacement is transactional. After successful admission, the previous
owner is retired before its replacement mounts, exactly once. Mounted leases,
render revisions, connected roots and disposable read tickets fence late DOM,
draft registration, event and focus work. Failed authoritative command review
can retire an already-invalid actionable snapshot while preserving the
captured command outcome and an explicit unavailable-review presentation.
That command consequence is distinct from cancelling an uncommitted transfer.

Sign Out captures a departure without discarding drafts or attaching a read
AbortSignal to its POST. Its pending command and session review protect unload
and navigation. Authoritative review of the same active actor retains the
current source and exact draft, reports uncertainty and permits deliberate
retry. Confirmed loss of session/access, identity replacement or unavailable
or malformed review uses Session Coordinator's existing revocation fan-out.
Revocation retires owners regardless of old discard consent; late results
cannot revoke a replacement actor.

Operations captures immutable actor, tenant-bound storage key, scope, path,
body/version and operation identity before dispatch. Its outcome and retry
evidence are separate mutable records. Retained storage keeps the existing
supported flat format and actor-key/legacy-key reader; restoration always
requires fresh review. Retry evidence belongs to the exact retained object,
captured actor and scope. A visible all-library projection may additionally
review a still-active captured library under its exact scope; broad reads alone
do not authorize that narrower command. Inactive or unavailable captured
authority leaves Retry blocked and retains the recovery record for later
authoritative review. No automatic broadening or speculative retirement occurs.

Projection invalidation retires choices, tables and their callbacks. Catalog
retirement clears review evidence, while retaining command identity, body,
scope and outcome. Same-actor preference refresh preserves valid evidence;
actor replacement cannot use it. Reads can be cancelled or superseded by their
owner. Submitted commands never inherit those read signals, and their outcomes
and receipts remain authoritative until explicitly resolved under the captured
identity.

Small contract tests cover preparation, fresh consent, blocked/stale/disposed
owners, actor versus preference changes, supersession and exactly-once commit.
Application journeys cover catalog, Recent Requests, Sign Out and Settings
scope admission, including uncached entry, failed history targets and duplicate
Suggestion opening. The caller audit also removed the destructive Settings
scope setter, staged Copy replacement, fenced retired grid/email callbacks and
configuration caches, and made command review completion depend on whether a
replacement actually committed. A committed Settings scope explicitly refreshes
its active Staff roster under the accepted scope. Controller tests cover
Title/Copy replacement and late read/event/focus work, and exact Operations
review, retry and body/version
authority. Real SQL/Kestrel browser checks additionally scan Profile and
Settings after failed detail transfer and uncertain same-session Sign Out.

## Closure audit (#365)

The audit started from #364 completion
`2ecd331ca60bb8d58c8ed272618939a1b0f7e8fe`, after fetching and re-reading #351,
#365 and prior execution evidence. The remote parity base remained
`4a3eac507a61768e722a96df88e94a75a65b1007`.

The fresh review found and corrected:

- Staff roster/create controls were excluded from the main Settings snapshot
  without owning a separate draft. Metadata, access and create now have opaque
  handles; even another command in the same row cannot consume an unrelated
  dirty draft. Exact baseline restoration is clean. Reload consent and edits
  made during pending reads are checked before authoritative replacement.
- A committed Title/Copy status or server-accepted queue scope could disagree
  with the visible tab and URL. Named Navigation intents now synchronize them.
  A command settling while Back is validating updates the accepted source
  entry; the obsolete target cannot discard that source. Exact IDs, unrelated
  parameters, hashes and supported unchanged legacy aliases remain preserved.
- Main Settings refresh could clear a staff-command receipt or uncertainty
  before successful roster review. Cleanup now requires the captured actor,
  scope and review type. A failed roster read cannot authorize blind retry.
- Retired Settings lifecycle calls and a detached detail host could still
  affect focus/presentation. Public lifetime guards and live mounted focus
  checks now reject those calls. Redundant queue clearing during route
  alignment was removed so the owning context transition governs grid lifetime.

Regression cases failed against the previous behavior, then passed with the
fixes. Existing action journeys retain pending-command protection; reaching a
different request after a committed stage change now explicitly selects that
request's stage instead of assuming the old queue remains visible.
The action and navigation fixtures now dispose their applications in `finally`; abandoned reads
and grids cannot survive into the next fixture's replacement browser globals.
The focused run then completed without the late Grid.js errors observed before
that fixture-lifetime correction.

| Invariant | Independent evidence |
|---|---|
| 1. Sole history owner | Repository search and frontend structure guard; only Router reads/writes browser history. |
| 2. URL/visible synchronization | Pure route tests, navigation journeys, legacy-link browser cases and new authoritative status/scope/validation-race regressions. |
| 3. Exclusive state/DOM ownership | Owner factories, scoped roots, immutable child contexts and injected intent ports reviewed across callers. |
| 4. No global mega-store | Composition structure guard; each feature closes over its own state. Shell retains presentation receipts only. |
| 5. Explicit draft lifetime | Local opaque scopes in mounted Title/Copy, Suggestion, Profile and Settings; baseline, release and replacement tests. Polaris application and Settings domain commands advance draft revisions without dirtying authoritative population. |
| 6. Explicit consumption | Admission rejects missing/foreign/disposed handles; commands supply their handle or explicit null. Staff metadata/access/create tested separately. |
| 7. Competing drafts | Editor/inline, reminder/parent, Suggestion and Settings sibling regressions prove no dispatch/storage write after rejection. |
| 8. Disposed owners cannot submit | Direct controller recreation/retired-control tests and disconnected-handle pruning. |
| 9. Parent/child disposal | Title render disposes editor/workflows/Copy child before replacing DOM; stale Polaris, assignment, pickup and reminder tests. |
| 10. Commands independent of read cancellation | All write paths inspected; captured command tests assert no signal. Read slots and DOM listener aborts remain local. |
| 11. Authoritative outcome survives presentation changes | Direct late actor/disposal tests plus committed/uncertain, failed refresh and postcommit 401/403 application journeys. Outcome precedes presentation gate. Lost Sign Out responses receive authoritative session review; unavailable review revokes the workspace and retains outcome receipts. |
| 12. Actor/tenant recovery isolation | Actor-key serialization, accepted epoch guards, cross-user/tenant storage and exact-record cleanup regressions. |
| 13. Same-user recovery | Copy and Operations reload/review/retry journeys preserve captured evidence and supported identity. |
| 14. Pickup fencing | Request/operation/version evidence unchanged; pickup partial/reconciliation journeys and real-SQL provider/journal gates. |
| 15. Hold fencing | Captured request/operation versions, proof and executor exclusion remain backend-owned; hold regression and real-SQL gates. |
| 16. Copy recovery | Source-version/BIB/library evidence, reminder intent, uncertain response, review and exact storage cleanup tests. |
| 17. Operations retention | Captured supported identity/path/body/scope, single-flight and same-actor preference revision tests. |
| 18. Bulk ledger | Frozen exact-string snapshot, sequential dispatch, attempted/unresolved/not-attempted outcomes, access loss and no replay tests. |
| 19. Independent Settings | Own local reads/drafts/commands, scoped overrides/domain editors and dedicated Settings suites. |
| 20. Independent Analytics | Local factory and scope/range/route replacement tests. Ordinary view deactivation cancels reads while preserving session selections; loss/replacement resets them. |
| 21. Epoch versus revision | Session identity WeakMap snapshots, coordinator-owned authoritative staff refresh, Profile/Staff Access revisions, both typed deletion conflicts and retained Operations tests. Metadata refresh preserves the epoch and receipts; identity/access changes and retired owners cannot update preferences. |
| 22. Stale completion isolation | Per-controller current-read/actor/context gates; old startup/read/command/recreation and Settings edit-during-read tests. Deferred Polaris navigation consent, invalidated in-flight Title configuration, reactivated Analytics and late Sign Out review are covered. |
| 23. Live focus targets | Host lease/current/connected checks, owned child return focus and nine genuine delayed Grid.js focus cases. |
| 24. Accessibility | Browser journeys enforce axe severity, keyboard/dialog behavior, overflow, missing images, page errors and external request restrictions. |
| 25. Exact bigint strings | Maximum and above-safe-integer controller, route, activity, bulk, recent and browser fixtures; numeric conversions limited to native Int32 library/Polaris values. |
| 26. No import cycles | Frontend entry-point dependency walk checks all relative ES module imports. |
| 27. No peer state writes | Feature import guard, repository imports/DOM searches and injected lifecycle contracts reviewed. Research helper imports are stateless. |
| 28. Narrow coordination | Named detail, scope, created/existing/recent, Closed-review, preference and receipt ports at composition. Confirmed Settings configuration commits call Title's scoped `invalidateConfiguration` port before presentation refresh; successful configuration review remains a second invalidation. Staff roster review does not invalidate configuration. |
| 29. No global read singleton | Repository search plus structure guard; `createLatestLoad()` exists only inside disposable owners. |
| 30. Actual composition root | Structure guard rejects endpoints, history, serialization, draft machinery and shared feature state in `workflow.js`. |

## Reopened closure remediation (2026-10-03)

An additional closure review of `a06d80b39857155d637998a69725f155bc5c8b4a`
found four substantive gaps. #365 and #351 were reopened on the same branch
and draft PR #366. This audit supersedes the prior closure conclusion.

- Request Editor and Staff Suggestion Polaris application now touch their
  owning draft scopes after applying fields and verification. Deferred Back
  validation tests approve the first discard, select a new BIB through the
  actual Polaris dialog, and reject the required second consent. The newer
  values, source entry and unload protection remain. The original clean-to-dirty
  during-validation regression remains.
- Analytics deactivation supersedes reads while keeping the selected library
  and range. A view round trip first requests `scope=2&range=last90`, renders
  those selections and rejects the prior activation's response/focus. Session
  loss and actor replacement reset defaults; same-actor preference revision
  preserves selections. Disposal clears the owned results.
- Authoritative Settings configuration review calls Title Detail's named cache
  invalidation port at composition. Library review invalidates that library;
  system review (including the API's system organization ID `1`) invalidates all
  libraries. Roster/access review preserves the
  cache. Invalidated in-flight configuration cannot render or repopulate old
  values, and unrelated libraries retain their cache entries.
- Uncertain Sign Out, including response loss, timeout and server failure,
  receives a fresh cancellable session read. Unauthenticated/access-denied
  state revokes the workspace; the same authorized actor permits explicit
  retry. Unavailable or malformed review hides the workspace and explicitly
  reports uncertainty while retaining other outcome receipts. Late command
  and review results cannot affect a replacement actor. Definite errors still
  follow the existing session/error boundary.

The same-root-cause source/caller audit covered every draft owner, all feature
deactivation paths, cached configuration/reference data and committing error
paths. Settings domain add/delete/reorder callbacks and logo-draft removal now
advance the local draft scope; navigation uses that revision instead of a
reusable value snapshot. Normal input/change events also advance it. Rendering
and authoritative baseline population remain clean. Hold-resolution Revert also
touches after its programmatic reset; deferred navigation with a competing dirty
editor proves prior consent cannot survive that reset. Profile population,
Copy reminder defaults and inline picker initialization establish baselines;
their other programmatic updates are already part of stamped input/change or
registration/release lifetimes. Queue filters, Settings scope/panel and
Operations scope/retained attempts preserve their established session lifetimes.
Other configuration reads are mounted/staged or refreshed by their owning
mutation/review paths; other committing owners already retain uncertainty
instead of claiming rollback. No additional substantive instance remained in
the fresh source review. Controller decomposition and backend/provider policy
are unchanged.

The browser fixture also waits for the old Title editor controls to retire
after committed Copy creation before opening another child. The commit receipt
is authoritative before the asynchronous parent refresh completes; clicking a
retired control is correctly rejected by the application.
The delayed Grid focus fixture drains its released render and disposes the
owning application before closing its DOM. It retains all nine focus cases and
the guard that rejects late Grid render errors.

Final completion SHA, local gate results, exact package identity/digests and
the final PR CI run are recorded on reopened #365 and parent #351 only after
the complete gate and final re-review pass.

## Second reopened closure remediation (2026-10-05)

The independent review of `e146d959f5f8bdb9195f13d688bd919ab7717187`
found three remaining ownership gaps. #365 and parent #351 were reopened on
the existing branch and draft PR #366. The earlier closure conclusion is
superseded until the complete gate and final review pass.

- Session Coordinator now exposes `refreshCurrentStaff(owner)`. A committed
  Staff Access command that affects the current user reconciles the authoritative
  session before accepting roster review. Same actor metadata updates advance
  the preference revision, including rowversion and all DTO preferences, update
  shell identity and a clean Profile, and preserve actor-bound receipts. A dirty
  Profile remains owned by its draft. Both single Title and Additional Copy
  destructive paths call this port after `actor_changed_since_preview`; only a
  subsequent deliberate retry submits the new actorVersion. Actual access/key
  changes retain the session-loss path. A retired owner cannot update or revoke
  a replacement actor, including a late direct preference completion or a raw
  session-review 401/403.
- Settings emits `onConfigurationCommitted(owner, scope)` as soon as each
  configuration-changing write confirms. Library saves, reset and format
  deletion invalidate their library; system writes invalidate every library.
  Failed presentation refresh and partial format follow-up cannot preserve a
  valid stale cache. The confirmed-but-unreviewed form remains inert, cannot
  submit old values, and permits navigation. Re-entry performs authoritative
  review instead of reusing the stale snapshot. Staff-only commands/reviews
  preserve the Title cache.
- Test Polaris retains its POST API contract but uses an owned, single-flight
  cancellable diagnostic read. Consent must precede discarding/reloading dirty
  Settings, and a failed reload cannot start the test. Connected, HTTP 502
  unavailable and transport-failure outcomes do not create mutation receipts,
  uncertainty, awaiting-reload state or departure guards. Session/access checks
  remain active; suspended/replaced contexts reject late diagnostic results.

The same-root source/caller audit inspected every StaffUser write, persistent
frontend cache, read-like POST and confirmed command follow-up. Staff roster,
logo, organization sync and participation writes now retain their confirmed
review obligation after a failed refresh; stale controls cannot submit another
command, and each review clears only its owning domain. Logo commits invalidate
their configuration scope, organization synchronization invalidates all scopes,
and participation invalidates the affected organization. Profile already reviews
its authoritative session; startup accepts sign-in/bootstrap revisions. Bulk
Delete captures a fresh authoritative actor version for each new preview and
stops its frozen ledger on actor conflict. Research and patron lookup POSTs
already use disposable reads; provider/Operations/Suggestion commands retain
their committing semantics. Mounted child configuration and queue/detail
refresh paths already retire stale actionable snapshots. No additional
architecture redesign or backend/provider/schema change was needed.

Regression evidence includes twelve new application journeys, eight session
refresh/boundary cases, a Profile revision/draft case, four diagnostic cases,
and extended format partial-success and stale-roster assertions. The new
real-SQL/Kestrel browser flow edits the current actor's metadata through Staff
Access, observes the actual advanced rowversion and unchanged identity key,
and checks both typed deletes. Separate-session SQL metadata writes cause real
backend actor conflicts; explicit retries use the newly accepted version without
replay. Two new browser states retain accessibility/layout/image/error/traffic
guards. The three configuration-commit failure journeys, metadata presentation
and diagnostic-unavailable tests fail against the independently audited baseline.

Final completion SHA, gate results, exact artifact identity/digests and PR CI
are recorded on #365/#351 only after the full gate and fresh re-review pass.

## Authoritative operational projection closure

The review of `d692a9d4ddca21ac47d584fa7329b5ae7f5bfd51` found that
commit-time Title configuration invalidation did not retire persistent queue
DTOs or the operational organization catalog. The preceding three closure
fixes remain intact. Settings now emits separate configuration, Staff Access
and organization-catalog commit intents through the composition root.

Both queue owners expose scoped `markStale`. Affected snapshots, Grid
containers, claim fields, workflow/timeout contexts, format labels and tag
options retire immediately; a same-scope entry must read authoritative data.
System configuration affects every library and the all-library projection;
library configuration affects that library and the all-library projection.
Late pre-commit reads and failed refreshes cannot restore retired rows.
Ordinary staff-roster review preserves unrelated queues. Review of an
uncertain Settings/Staff Access command also retires its affected projections
without changing the command's recorded outcome to confirmed success.

Organization sync, activation/deactivation and system Settings saves (which
replace the enabled-library set) retire operational choices and Operations
tables. Navigation owns catalog revalidation and scope canonicalization;
Router owns the accepted URL. Confirmed deactivation immediately retires the
selected scope, and a queue entry reviews the catalog before presenting it.
Failed catalog review keeps the source view with an error and retired data;
failed queue review leaves the valid accepted scope with unavailable rows.
New Suggestion and Operations receive only refreshed operational choices.
Activation/sync refresh available choices and labels without a page reload.
Settings participation controls and Navigation/Operations catalog filters use
the administration API's `isActive` field. Fixtures use that same contract;
inactive libraries cannot pass review because an assumed `active` field is absent.

Shell independently retires and reviews affected email readiness on a
configuration commit, even when Settings' own reload fails. Analytics already
replaces its projection with Loading on every entry and recovers an invalid
library scope through its own authorized all-library read. Current-actor
preference revisions, command receipts, drafts, provider fencing and recovery
remain under their existing owners.

Thirty-one additional application journeys cover claims, Mine/Unclaimed
filters, metadata, scoped/system DTOs, failed and uncertain reviews,
participation, activation/sync, operational choices and email readiness.
Twenty-two of the first twenty-six fail against the audited HEAD; roster-only
and unrelated-library controls pass. Queue-owner tests additionally prove
late-read retirement, failed same-scope entry and explicit retry. A real-SQL
Kestrel journey deactivates the selected library after caching both queues,
forces failed queue reads, checks canonical URLs and choices, then reactivates
it through Settings. Its two additional scanned states retain all browser
accessibility, layout, image, page-error and external-traffic guards.

## Closure validation contract

Issue #365's execution-state evidence records actual final-SHA results and CI.
Closure requires all 49 current frontend files (including 228 navigation/draft
journeys and nine genuine Grid focus cases), a zero-warning Release build,
the unchanged 784-test non-browser minimum and all three real-SQL Kestrel
browser journeys with zero skips. The .NET gates include migration import,
reconciliation, native upgrades, provider fences, transactions, authorization,
concurrency, outbox/jobs and deployment contract behavior.

The exact tested source is published as Web/DACPAC and a separate self-contained
win-x64 migration executable. Publish exclusions, vendor hashes, native
`describe-contract`, manifest identity/payload digest, deployment ZIP SHA256 and
the actual packaged deployment script's `-ValidateOnly` are verified. Final PR
CI must pass its complete build/test/package job before #365 and #351 close.

`test_cd_activation: pending_runner_setup` remains unchanged. No IIS host,
runner activation, live Polaris/Postmark calls, production readiness/tag/
deployment, cutover, backup/recovery rehearsal, repository rename or merge is
authorized by this campaign. #345 remains open for the overall PR #344 closure
review and merge decisions remain human-owned.
