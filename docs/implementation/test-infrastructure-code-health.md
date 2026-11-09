# Workstream 5 — test infrastructure and operational records

Scope: issue #487 R12/R13/R14/R15/R19/R21. The baseline is merged #494,
`19388141b65d13b1067c35b8db76e8a2c0d9673f`. Settings #489/#491, background #492,
staff #493, patron #494 and hosted three-worker #490 prerequisites are preserved.
The application behavior and R11/D01/D02 designs are unchanged.

## Portable qualification

`tests/frontend-tests.json` declares the two mandatory safety files and exactly
the portable authentication/runner acceptance subset. `run_all.js` and native
CI selection consume that declaration. Normal/filter/safety/remaining execution
keeps stable identities relative to `tests/`; the remaining pass excludes fresh
safety execution and aggregates its verified receipts. The existing frontend
worker retains ownership. Counts describe files, separately from subtests.

`reuse.py` requires a complete unique passing receipt for every selected file,
in addition to counters and current discovery. Disposable executable probes in
`scripts/ci/test_selection.py` demonstrate normal discovery, exactly-once split
execution, deliberate assertion failure, missing files, filtered execution and
interruption. `test_reuse.py` rejects omitted/duplicate/nonpassing receipts and
fingerprints changed acceptance helpers, suites/registry, all four consumed
browser helpers and the discovery declaration. An actual runner module-load
witness checks every loaded local module's frontend ownership. Native source separation is
retained. No dependency-graph system or new evidence schema is introduced.

Acceptance runner tests check exported current behavior/metadata, not historical
Git blobs or parsed scenario source. A disposable copy with reorganized module
scope and no `.git` directory still passes the registry contract. Runtime
identity, read-only/write boundaries, external rejection, safe diagnostics,
cleanup and distinct functional/human outcomes remain covered. Other launcher,
runtime, layout and TLS checks retain the separate prerequisites documented in
`tests/acceptance/README.md`; their execution is not implied here.

## Process and SQL ordering proof

`OwnedTestProcess` is shared by the nine unbounded browser launch sites,
Campaign 2's explicit four-minute launch and the publication/cache wrapper.
The requested executable, argument array, working directory and environment
pass through an owned launcher. Windows assigns its job before releasing the
Node gate. Linux uses a dedicated Python 3 subreaper that owns only this command's
descendants, including Chromium's detached process groups, without changing the
test application's adoption policy. It exits only after reaping every child;
target exit, cancellation or loss of its parent triggers cleanup. Cleanup uses
that ownership even after the requested launcher exits.

Exit plus concurrent stdout/stderr drains share the operation deadline (four
minutes by default). Cancellation remains `OperationCanceledException` with the
caller token; expiry becomes `TimeoutException` retaining the triggering error.
Termination/reaping/drains have a separate ten-second budget. Partial captured
output and cleanup failure accompany the original failure. Capture retains up
to four Mi characters per stream while continuing to drain excess output.
Existing America/New_York overrides stay at their callers.

Twelve deterministic native process cases cover argument/environment/timezone,
working directory, normal/nonzero/startup failure, both streams beyond pipe
capacity, cancellation, deadline and three-generation PID cleanup in attached
and detached groups. The fixture has independent final cleanup. An exited
target can complete cleanup before its deadline, including descendants holding
redirected handles; successful return must still remove every observed PID.

Both named staff races keep their actors, input versions, blockers and outcome,
state/event/audit assertions. Each of four scenarios observes
`blocker SPID <- first operation SPID <- second operation SPID` through the
existing database-scoped DMV helper before releasing the blocker. Observation
is bounded; failure releases the transaction, cancels and joins started service
operations before fixture teardown. No production locks or unrelated delays
were changed. TRX stdout records each observed chain.

## Coverage mapping for replaced assertions

| Removed source assertion family | Observable retained/replacement proof |
| --- | --- |
| Historical file equality, parsed old scenarios and incidental totals | Current runner behavior, registry/discovery agreement, unique callable scenarios, independent report IDs/write classifications, completeness/status tests, reorganized disposable module probe with its complete imported browser modules in `acceptance/runner.test.cjs` |
| Frontend helper names, expression spelling and URL-literal counts | `staff_dotnet_frontend.test.js` imports both detail controllers and captures one scoped candidate GET each; `staff_research.test.js` captures the exact bigint request ID sent to BIB lookup |
| Workflow controls, routes, refresh, dialogs and profile property names | `staff_request_details`, `staff_request_actions_ui`, `staff_navigation_drafts_ui`, `staff_copy_controllers`, `staff_analytics_race_dotnet`, `staff_profile_controller`, `staff_operations_controller` and real browser journeys exercise capability restrictions, actions, scoped reads, body fields, focus, history, recovery and preferences |
| Suggestion service/frontend function names and implementation expressions | `staff_suggestion_contract` captures lookup/configuration/create methods, paths, numeric serving scope, body, read/write cancellation ownership and exact/invalid acknowledgement IDs; retained suggestion controller/full-app tests cover stale identities, duplicate and pickup recovery, configuration races and verified BIBs |
| Suggestion server authority/persistence source strings | Real SQL/HTTP `StaffSuggestionJourneyTests` covers forged library scope, current actor/participation rechecks, eligibility, duplicate prevention, authoritative scope/actor/verified BIB persistence, notification status and partial pickup truth; new `StaffSuggestionHttpRejectsMissingAndInvalidAntiforgeryWithoutDurableEffects` rejects missing/invalid tokens on lookup and creation with zero durable effects; retained patron-code and cancellation boundary tests cover policy/reverification and cancellation |
| Rewritten export syntax / `new Function` in URL and recent tests | Actual ES module imports retain all route round trips, invalid IDs, exact bigint IDs, account/tenant/rebound identity separation, malformed storage and sanitization checks; persisted recent records are checked structurally |
| Retired push branch literals | Parsed trigger contracts require only `main`, retained supported tags/manual/PR events; executable workflow policy/gate and actual PowerShell/archive/digest/ValidateOnly regressions remain |

Unsafe runtime HTML, retired PocketBase state, peer-owner import boundaries,
composition/history ownership, admin-only assignment endpoint prohibition,
antiforgery, negative scope/authorization and stale-identity coverage remain.
Independent expectations are retained; no successful path substitutes for a
negative authority case.

## Equivalent fixtures and deliberately retained differences

The hidden-identities, direct-inheritance and organization-catalog-guard tests
use the existing staff-controller fixture's document, localhost origin, fresh
module tree, realm constructors and confirmation policy. Their seeds, controls,
network doubles and assertions stay local. Each controller/editor disposes in
`finally`; the helper closes the DOM, restores owned global descriptors/fetch and
removes module copies even after failure. `staff_controller_fixture.test.js`
proves fresh module/DOM identity, correct constructors and failure cleanup.

Multi-DOM, actor-replacement, full-app/Grid.js, special-origin and staged-auth
fixtures keep their distinct setup and lifetimes. Existing deferred-response,
cancellation, session ownership and serial SQL/owned-data assertions are retained.

Email seeds remain separate by lifecycle: recipient-authority snapshots in
`PatronJourneyTests.EmailRecipientAuthorityEvidence.cs`, terminal timestamps and
leases in retention tests, zero-send configuration retry, ambiguous-send recovery,
and safe history/redaction/DST/bigint identities have different preconditions.
`NewCleanupOutbox` already serves both `Slice5EmailAndScheduleTests` cleanup and
`Slice5RetentionTests` boundary coverage; it was reused by #492 and needs no
second extraction. No email family or scenario was deleted to reduce counts.

## Operations and qualification boundary

Schema 6, epoch `github-hosted-ubuntu-24.04-2026-11-08-v1`, fixed suite/job owners,
three independent exact-event builds, native partition accounting, fresh-or-valid
reuse, provenance and final verification, reporting-only required gate and
current ZIP/digest/ValidateOnly are preserved. Retired branch push filters and
active-sounding #420/#421/#427 instructions are reconciled; old performance
receipts remain labeled with their original topology and source.

The November 5, 2026 [activation receipt](https://github.com/clcdpc/asap-pocketbase/actions/runs/42135741938/job/127625203451)
verifies `v1.0.0-test.32` at `46c60a8cdb1218f34622620cbf0b31cc92e6bd07`, including
activation, HTTPS readiness and state recording at approximately 7:23 p.m.
Eastern. It does not establish current-main deployment or current host health.
No host was accessed or deployed for this work. Live providers, production,
cutover/recovery and human acceptance remain separate. R11's current probes,
D01's explicit partial-save contract and D02's guarded reuse are retained for
their separate decisions; this task supplies no authorization for redesign.

Focused/final execution receipts, independently reviewed SHA, final discovered
populations and EXECUTED/REUSED dispositions are recorded in the PR and only
Workstream 5's progress in #487 after qualification. Discovery is authoritative;
historical counts are not acceptance thresholds.
