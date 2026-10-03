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
| 5. Explicit draft lifetime | Local opaque scopes in mounted Title/Copy, Suggestion, Profile and Settings; baseline, release and replacement tests. |
| 6. Explicit consumption | Admission rejects missing/foreign/disposed handles; commands supply their handle or explicit null. Staff metadata/access/create tested separately. |
| 7. Competing drafts | Editor/inline, reminder/parent, Suggestion and Settings sibling regressions prove no dispatch/storage write after rejection. |
| 8. Disposed owners cannot submit | Direct controller recreation/retired-control tests and disconnected-handle pruning. |
| 9. Parent/child disposal | Title render disposes editor/workflows/Copy child before replacing DOM; stale Polaris, assignment, pickup and reminder tests. |
| 10. Commands independent of read cancellation | All write paths inspected; captured command tests assert no signal. Read slots and DOM listener aborts remain local. |
| 11. Authoritative outcome survives presentation changes | Direct late actor/disposal tests plus committed/uncertain, failed refresh and postcommit 401/403 application journeys. Outcome precedes presentation gate. |
| 12. Actor/tenant recovery isolation | Actor-key serialization, accepted epoch guards, cross-user/tenant storage and exact-record cleanup regressions. |
| 13. Same-user recovery | Copy and Operations reload/review/retry journeys preserve captured evidence and supported identity. |
| 14. Pickup fencing | Request/operation/version evidence unchanged; pickup partial/reconciliation journeys and real-SQL provider/journal gates. |
| 15. Hold fencing | Captured request/operation versions, proof and executor exclusion remain backend-owned; hold regression and real-SQL gates. |
| 16. Copy recovery | Source-version/BIB/library evidence, reminder intent, uncertain response, review and exact storage cleanup tests. |
| 17. Operations retention | Captured supported identity/path/body/scope, single-flight and same-actor preference revision tests. |
| 18. Bulk ledger | Frozen exact-string snapshot, sequential dispatch, attempted/unresolved/not-attempted outcomes, access loss and no replay tests. |
| 19. Independent Settings | Own local reads/drafts/commands, scoped overrides/domain editors and dedicated Settings suites. |
| 20. Independent Analytics | Local factory and scope/range/route replacement tests. |
| 21. Epoch versus revision | Session identity WeakMap snapshots, Profile updates, Copy actor-version and retained Operations tests. |
| 22. Stale completion isolation | Per-controller current-read/actor/context gates; old startup/read/command/recreation and Settings edit-during-read tests. |
| 23. Live focus targets | Host lease/current/connected checks, owned child return focus and nine genuine delayed Grid.js focus cases. |
| 24. Accessibility | Browser journeys enforce axe severity, keyboard/dialog behavior, overflow, missing images, page errors and external request restrictions. |
| 25. Exact bigint strings | Maximum and above-safe-integer controller, route, activity, bulk, recent and browser fixtures; numeric conversions limited to native Int32 library/Polaris values. |
| 26. No import cycles | Frontend entry-point dependency walk checks all relative ES module imports. |
| 27. No peer state writes | Feature import guard, repository imports/DOM searches and injected lifecycle contracts reviewed. Research helper imports are stateless. |
| 28. Narrow coordination | Named detail, scope, created/existing/recent, Closed-review, preference and receipt ports at composition. |
| 29. No global read singleton | Repository search plus structure guard; `createLatestLoad()` exists only inside disposable owners. |
| 30. Actual composition root | Structure guard rejects endpoints, history, serialization, draft machinery and shared feature state in `workflow.js`. |

## Closure validation contract

Issue #365's execution-state evidence records actual final-SHA results and CI.
Closure requires all 46 current frontend files (including 136 navigation/draft
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
