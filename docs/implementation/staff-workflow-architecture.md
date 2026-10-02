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
