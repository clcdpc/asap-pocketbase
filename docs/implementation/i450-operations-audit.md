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
| #430 | Operations-only link removed; Settings link, saved context, exact recovery identity and focusable log retained | Focused email-test and Operations Node files PASS; saved-email real-SQL/Chromium journey 1/1 PASS with six checkpoints | Luna MAX independent review and closure audit PASS; no actionable findings | Issue-specific commit introducing this section |
| #431 | Pending | Pending | Luna MAX / Astra HIGH pending | Pending |
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

## Integration qualification

Pending final implementation and adversarial audit. Test results below will
record actual execution, rather than discovery or agent summaries alone.
Expensive SQL/browser and WSLC acceptance suites run sequentially.

## Boundaries

No deployment, production tag, PR merge, live Polaris mutation, or live Postmark
send is part of this work. Human visual/screen-reader acceptance remains #398;
live Postmark validation remains #293. Broader Settings work (#438), Request
Activity (#429), and extra weekly-summary recipients (#441) remain separate.
