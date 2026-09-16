# Slice 11: Legacy Removal And Canonical Documentation

## Current Reduced Scope: Future Cleanup And Review

Slice 11 is not implemented by reduced Slice 8. Start after the reduced
development-completion implementation sequence is ready; optional Slice 10
does not become a mandatory prerequisite. Do not remove the legacy
implementation or rewrite repository instructions in this task.
Refresh the actual tree, dependencies and external release blockers at dispatch.
Follow [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) for the
staged-PR lifecycle, optional package boundaries, PR-backed durable state,
review modes, context firewall and exact-milestone CI. GPT-6 Astra High is the
default thin supervisor, dispatching bounded Luna Max and independent Terra
High workers. GPT-6 Astra Max is bounded escalation only, returning control to
High. The slice PR holds the central state/event journal under document 10;
package PRs do not duplicate it. Manual/direct execution remains fallback. Reading lists
below identify full-slice authority; workers start with directly relevant
excerpts and expand for concrete concerns. Prior-slice acceptance and its
exact-SHA CI must be complete before this slice starts.
Package guidance in document 10 is provisional; choose actual boundaries at
slice start.
The reduced slice ends with complete integrated validation and ONE fresh Terra
High holistic review, with bounded Luna fixes and focused Terra re-review as
required. The old multi-pass whole-application review is deferred to production
readiness.

Read root AGENTS before replacing it, document 10 in full, document 02 Slice 11
and final sequence, documents 04/05/08 release/recovery boundaries and document
14's complete regression map. Preserve the pack, baseline history and explicit
temporary Postmark decision. No new architecture review or UI redesign.

## Required Work

- Inventory actual runtime/build/test/documentation consumers before deletion.
  Remove obsolete PocketBase hooks, migrations, backend JS, public source and
  runtime dependencies from the target branch only after their behavior and
  useful regression tests have been carried into the accepted .NET application.
  Do not move them into a new legacy runtime subtree. Retain only concrete
  migration-relevant historical material; Git history is the archival source.
- Audit `README.md`, `ARCHITECTURE.md`, `DESIGN.md`, `PRODUCT.md`, `PLAN.md`,
  `STACK.md`, `RESEARCH.md`, `TODO.md`, obsolete design/operations documents and
  links against the current repository rather than blanket-deleting filenames.
  Canonical instructions must describe actual .NET setup, testing, deployment,
  migration and recovery, not a future proposed platform or runnable PB server.
- Rewrite root `AGENTS.md` for the completed layout/commands and architecture:
  DACPAC owns schema, direct EF feature services, selective Dapper/ADO.NET and
  parameterized SQL are permitted as specified. Remove Goja/hook-only warnings
  and the legacy raw-SQL prohibition. Retain/adapt general simplicity/scope,
  explicit settings scope/reset, current-state/history truth, safe DOM APIs,
  frontend request/stale-result conventions, scoped analytics, accessibility
  and behavioral/concurrency/migration testing guidance.
- Keep all accepted feature and artifact behavior working without the deleted
  source. Useful Node tests target retained frontend modules; Node remains
  development/CI-only. Generated `wwwroot` still comes from `Frontend/` through
  existing MSBuild logic. Exact vendored libraries/notices stay intact.
- Search the final source/build/publish/deploy paths for obsolete PB imports,
  SDK/auth-store use, runtime executables/data, broken relative links, old setup
  routes and leaked local fixtures/email. Verify actual build, native migration
  publish, CI and browser journeys from the cleaned tree, not stale binaries.
- Preserve exact deployed PocketBase SHA tracking and emergency-fix propagation
  procedures. No premature permanent final tag or permanent PB branch. Once
  .NET accepts production writes, retired PB remains stopped/forensic-only;
  execution requires an isolated copy with outbound mutations/mail blocked and
  jobs disabled. Never document it as a writable fallback after cutover.

## Development-Completion Review And Deferred Release Gates

Complete full integrated validation, one holistic Terra slice review and
required focused re-review, short Astra acceptance and exact-milestone CI under
document 10. The review covers the complete development-completion delta.
Bounded Luna Max fixes confirmed blockers; the three-consecutive-clean-pass
production review remains deferred and a pass cap never waives a known defect.
Package and slice reviews do not replace the one integrated slice review.

Repository cleanup is not production completion. Actual deployed PocketBase
identity, required real Postmark adapter/webhook/transport tests,
provider/host/isolation evidence, complete operational artifacts and all
production deterministic gates remain in the deferred production-readiness
backlog. A temporary file sender must remain prominently identified as a
release/rehearsal blocker if unresolved.

Only in that future phase may the project record source production identity,
merge the complete PR, create a production tag, build immutable correlated
artifacts, rehearse exact artifacts, perform cutover or rename the repository.
None of those external actions is performed by the slice implementer merely
because cleanup tests pass.
