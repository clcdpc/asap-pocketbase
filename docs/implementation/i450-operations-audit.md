# Operations and administration audit (#450)

This change implements #430, #431, #432, and #434 serially on
`codex/i450-operations-audit`, starting at `c168c3856e43fa15fd44382ca56a33286c05ced4`.
Merged #420 (`50f1483a30e2b738b0082e91c452081011f502c6`) and #421 are ancestors
of that base. The existing checkout's unrelated browser edits are preserved in
place; implementation uses an isolated managed worktree.

The authoritative visual reference is
[`operations-audit-mockup.html` at `d85d22f`](https://github.com/clcdpc/asap-pocketbase/blob/d85d22f2373bf38080e80d0a893c8daa49a504ee/docs/design/operations-audit-mockup.html).
Only the production application is changed; reference data and preview tools
are not application inputs.

## Qualification matrix

| Issue | Acceptance evidence | Tests | Independent reviews | Commit |
| --- | --- | --- | --- | --- |
| #430 | Operations-only link removed; Settings link, saved context, exact recovery identity and focusable log retained | Focused email-test and Operations Node files PASS; saved-email real-SQL/Chromium journey 1/1 PASS with six checkpoints | Luna MAX independent review and closure audit PASS; no actionable findings | `ac57372` |
| #431 | One refresh; truthful log/status; guarded Settings handoff; exact identity/recovery; latest current test visible beyond 500 failed rows | SQL 23/23 then targeted 6/6 PASS; final browser 1/1 and five focused Node invocations PASS | Luna MAX and Astra HIGH final R3 closure PASS; all findings resolved | Issue-specific commit introducing this row |
| #432 | Pending | Pending | Luna MAX / Astra HIGH pending | Pending |
| #434 | Pending | Pending | Luna MAX / Astra HIGH / Terra MAX pending | Pending |

Implementation and functional-review agents use the requested available models:
Luna MAX for #430 and functional reviews; Sol MAX for #431/#432/#434;
Astra HIGH for security/data-integrity reviews; Terra MAX for visual review.
The final combined adversarial audit uses Astra XHIGH. Orchestration remains in
the parent Codex session. Review findings must be resolved and independently
verified before the next issue starts.

### #430 qualification

- `dotnet build Asap.sln -c Release --nologo`: PASS, zero warnings/errors.
- `node tests/staff_email_test.test.js`, `node tests/staff_operations_ui.test.js`,
  `node tests/staff_operations_controller.test.js`: PASS.
- `dotnet test --project tests/Asap.Tests/Asap.Tests.csproj -c Release --no-restore
  --filter FullyQualifiedName~SettingsEmailTestBrowserDispatchesAutomaticallyThroughRecordingPostmarkAndObservesSameReference`:
  PASS, 1/1. Six actual desktop/mobile checkpoints; zero external requests, page
  errors, serious/critical axe findings, missing images or page overflow.
- The first browser wrapper run exposed its old five-checkpoint assertion after
  the added sixth scan. The assertion now reflects the actual six checkpoints;
  the complete focused journey was rerun successfully.
- The SQL fixture's recording Postmark handler recorded one provider request
  from the explicit new send; the deliberately abandoned submission, navigation,
  retirement, reconciliation and reload did not create another provider request.
- Independent Luna MAX reviewed the complete code/test diff, inspected all six
  browser states and their report, and independently reran the Operations UI
  Node file successfully. It found no actionable defects or #430 acceptance gaps.

### #431 qualification and review resolutions

Settings retains saved context, Send, and always-available guarded navigation.
Both screens remove individual status refresh and routine terminal results.
Main refresh reconciles by reference or original operation identity without
writes, independently of table failures. Session recovery, explicit retirement,
late acknowledgement and scope/account ownership remain enforced. The log uses
recorded delivery-mode evidence; provider acceptance never means mailbox receipt.

Independent Luna MAX and Astra HIGH reviewed actual source/tests after each
remediation. Their final R3 closure passes have no actionable findings.

| Finding | Resolution and regression |
| --- | --- |
| Luna P2: retirement focuses disabled Send | Choose enabled Send, main Refresh, or focusable log; test blocked/unavailable context and busy Refresh |
| Astra P2: historical result hides current configuration block | Retain readiness guidance separately from history; test both panels, response orders and unresolved outcomes |
| Astra P2: table failure skips exact reconciliation | Run independent reads with owning-load cancellation; test queue/list/catalog failure, known/unknown reference and zero writes |
| Astra validation: 20ms acknowledgement race | Controlled window clock verifies acknowledgement before expiry and clearing at expiry; the independently observed initial failure is resolved |
| Luna P2: 500 failures hide newly submitted test | Keep bounded failed-first query; prepend current authorized exact test only if absent, without retry authority; SQL tests seed 501 failures |
| Luna P2: delayed table renders older queued observation | Resolve operation-bound latest-observation accessor after tables finish; test newer poll, identity/storage replacement, scope/account/deactivation and cancellation |

Executed qualification (counts describe separate runs, not unique totals):

- Focused real SQL: 23/23 PASS; subsequent targeted SQL: 6/6 PASS, including
  pending/sent/suppressed beyond the list bound, actual-mode evidence, HTTP
  idempotency and the retained failed-first contract. No skips or failures.
- Final `staff_email_test`, `staff_operations_controller`,
  `staff_operations_ui`, selected email-navigation (2 journeys), and
  `staff_operations_postcommit_401_ui` Node invocations: PASS.
- Final saved-email SQL/Chromium journey: 1/1 PASS; six desktop/mobile scans
  with zero serious/critical axe findings, page errors, external requests,
  missing images or page overflow. Two deliberate client submissions include
  one never reaching the server and one committed with a lost acknowledgement;
  five exact by-intent reads preserve the same reference. SQL verifies one
  recording-provider request and one worker job.
- Final reviewers independently reran focused Node checks and adverse probes,
  including post-poll cancellation and storage loss. Astra also completed 262
  navigation/draft journeys during remediation; the final focused navigation
  rerun passed. An accidentally broad Luna navigation invocation was stopped
  and is not counted as a complete run.

Exact commands, TRX, browser reports and images are retained outside Git under
`Documents/Codex/2026-11-02/i450/`; latest receipts use `i431-r3-*`.

## Integration qualification

Pending final implementation and adversarial audit. Test results below will
record actual execution, rather than discovery or agent summaries alone.
Expensive SQL/browser and WSLC acceptance suites run sequentially.

## Boundaries

No deployment, production tag, PR merge, live Polaris mutation, or live Postmark
send is part of this work. Human visual/screen-reader acceptance remains #398;
live Postmark validation remains #293. Broader Settings work (#438), Request
Activity (#429), and extra weekly-summary recipients (#441) remain separate.
