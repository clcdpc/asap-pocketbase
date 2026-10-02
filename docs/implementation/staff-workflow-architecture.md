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
