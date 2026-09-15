# Slice 9: CI And Release Validation Integration

## Preparation Only

Not dispatched. Follow accepted Slices 0-8 and refresh their actual tests,
artifacts, versions, commands and evidence before implementation. This packet
finishes the fixed sequence's existing CI/release integration, not a new plan.
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

Read root AGENTS, document 10, document 02 Slice 9, all of documents 06 and 08,
document 14's R1-R7/F1-F3 trace, and the accepted Slice 8 release manifest and
deployment contracts. Actual pinned PocketBase files remain the source of
preserved UI behavior; existing focused source notes identify those files.

## Required Work

- Complete the existing `.github/workflows/dotnet.yml` and focused release/live
  workflows without new test-project or application frameworks. PR and main
  builds run .NET 10, DACPAC, real SQL Server 2022 compatibility 160, frontend
  unit tests, actual hosted Playwright journeys and serious/critical axe gates.
  Keep meaningful test-count/skip checks so misconfigured discovery is failure.
- Preserve one `Asap.Tests` project and deterministic recording Polaris/email
  boundaries. Normal CI neither contacts live providers nor requires filesystem
  email output. Node/browser dependencies belong only to test jobs, never
  MSBuild application compile/publish, F5, deployment or IIS runtime.
- Complete targeted patron login/options/submission/expiry/pickup journeys and
  staff anonymous shell, all roles, queue/scope/deep-link, edit/claim/action,
  stale conflict, AdditionalCopy, profile preferences, settings inheritance/
  reset and email/operator recovery journeys. Exercise actual current SQL
  authorization using Testing-only identity evidence, not policy bypass.
- Test auth registration independently from `IsNonProduction`. Retain the real
  persistent-cookie restart/rebind/removed-tenant tests outside fake auth.
  Cover keyboard reachability, focus-visible, modal/tab/view focus, labels,
  live regions and supported Escape behavior at desktop and mobile sizes.
- Map every document 06 R1-R7/F1-F3 case to executable tests and actual results.
  Fill demonstrated coverage gaps in their owning feature with focused changes;
  do not replace real SQL concurrency/recovery assertions with mocks or count
  a traceability document as a passing test. Preserve source-sensitive timeout,
  final hold identity, placement history, eligibility and queue fairness gates.
- Wire immutable same-tag app/DACPAC/deployment and separate self-contained
  win-x64 migration artifacts using the accepted manifest generator. Verify
  native dependency loading and actual source-built binary hashes. Never use
  an old RID build through `--no-build` or identify a migration executable only
  by apphost hash/version. Preserve exact vendor/dependency assets and notices.
- Validate archive contents: no PocketBase runtime/data, Node modules/tools,
  development email previews, secrets, external production config, private
  keys or sensitive migration fixtures/reports. Diagnostic CI artifacts must
  be an explicit safe set, not an upload of the working directory.
- Provide protected GitHub Environment live-Polaris `workflow_dispatch` against
  an existing exact version tag. Use isolated SQL and the designated disposable
  Polaris canary host/patrons/records. Cover actual required reads and mutations,
  disabled hidden retries, exact create/reply evidence and final-ID versus GUID
  mapping, including the defined unavailable-ID fallback. Dirty fixture state
  fails/waits for refresh unless the permitted explicit operator override is
  supplied and recorded. Never mutate arbitrary catalog/patron records.
- An infrastructure-only live-Polaris override needs explicit operator reason
  and authorization. It cannot waive deterministic tests, a product defect or
  the actual artifact identity. Do not add automatic deployment, speculative
  fixture cleanup infrastructure or Windows CI for platform-independent tests.

## Evidence And Boundary

Run the full integrated local suite and actual remote PR pipeline; identify
Windows/IIS-specific tests accurately rather than implying Linux proved them.
Test tag/manifest mismatch and warning/override workflow failure paths. Do not
create a production tag merely to exercise workflow code; disposable local
fixtures and reviewed test mechanisms remain distinct from release artifacts.

Keep Slice 2's manual-resolution evidence/executor-exclusion contract and the
shared workflow guard in regression and live-rehearsal traceability. Synthetic
operator-proof fixtures establish local authorization/version/fencing behavior,
not provider correlation or physical worker termination. Capture the separate
actual operational evidence required by the deployment runbook.

The user-authorized temporary FileEmailSender exception remains limited to the
final transport. Real Rest 3-compatible cancellable Postmark adapter/webhook
work and transport-specific validation remain explicit release/rehearsal
blockers. No live Postmark send is required by the normal release policy, but
fake tests or local files cannot prove that missing adapter is implemented.
Do not stop unrelated implementation or declare production completion because
of that exception. All recipient-domain/outbox deterministic gates still apply.

Return compact test/artifact receipts, retained evidence paths and remaining
external evidence, not a release approval. Follow document 10 for authorized
package/slice commits, pushes, reviews and staged integration. Do not merge
the final port PR, tag, deploy or alter protected environments. Full integrated
validation, holistic review, short Astra acceptance and exact-milestone CI
remain required.
