# Profile and weekly email (#473)

Base: `be9c7af` from current `origin/main`. Implementation uses the isolated
`codex/i473-profile-weekly-email` worktree; unrelated edits in the original
checkout are preserved. #454 is merged; #469 is open and is not incorporated.

## Baseline contract map (before implementation)

- Profile POST accepts weekly opt-in/address and three other preferences plus
  rowversion, never `NotificationEmail`. `StaffProfileService` normalizes the
  optional address and locks/revalidates organization and staff before saving.
  Staff access owns the primary address. No new setting scope is introduced.
- `SendWeeklyStaffSummaryAsync` builds one scoped report per eligible staff
  member. Its transaction locks organization/staff and checks current identity,
  role, activation, opt-in and manual actor authority. Queue-time selection is
  currently weekly-address-or-primary; domain/configuration failures persist a
  suppressed outbox record.
- Normal identity is `weekly-summary:{staff}:{start}-{end}`. Forced identity is
  `weekly-summary-force:{manualRunId}:{staff}`. A new deliberate forced run has
  a new GUID; retries keep it. `UX_EmailOutbox_BusinessKey` is the durable unique
  constraint. The operation check currently uses that one record's key.
- Each `EmailOutbox` row owns its recipient/authentication/scope/address-kind
  snapshot, content, lease, rowversion, attempts and outcome. Dispatch claims a
  pending row atomically, revalidates staff/address, checks recipient domain and
  transport readiness, then revalidates under locks immediately before provider
  intent. Both address checks currently use weekly-address-or-primary.
- Provider acceptance stores `sent` and the provider ID; it does not prove
  mailbox delivery. Readiness failures can retry the same row. A provider timeout,
  lost acknowledgment or expired post-intent lease retains ambiguous evidence
  and does not automatically resend. Completed rows are not reclaimed.
- Existing weekly rows use address kind `weekly_summary` and one unsuffixed
  operation key. Compatibility must retain their exact recipient, history and
  retry state, without backfilling new copies for an existing operation.
- Existing tests use the DACPAC on real SQL Server and deterministic transport
  boundaries. Retained Profile/weekly browser fixtures cover HTTP, drafts,
  forced operations, accessibility and layout. No live provider is needed.

## Completion matrix

| Issue | Implementation | Pass 1 | Pass 2 | Focused evidence | Status |
| --- | --- | --- | --- | --- | --- |
| #441 | `84d01d7`, `a1ac681` | Luna MAX: forced partial-retry coverage added | Astra HIGH: state matrix and 20 independent probes pass | SQL 68/68; browser 1/1; focused Node/build pass | Technically qualified |
| #440 | `eac9497`; test/evidence correction in this commit | Luna MAX: no actionable findings | Browser scenarios passed; Terra MAX viewport-capture gap corrected | PopulationTests 6/6; real-SQL browser 1/1 | Technically qualified; integrated checks pending |

Requested child models/reasoning will be selected explicitly. The parent
session cannot switch its own model and retains the current Codex orchestration
configuration. Final reconciliation uses Astra EXTRA HIGH.

## #440 primary account information and refresh

- Profile now presents the primary notification email as a selectable `dt`/`dd`
  account value with the existing Staff Access help text. A missing value reads
  “No primary notification email is set.” No input or edit control remains.
- `profileValues()` contains only editable preferences, and the POST payload
  still excludes `NotificationEmail`. The account display refreshes from the
  exact latest `sessionIdentity.preferences()` snapshot even while a Profile
  draft or mutation is present; editable preferences keep their existing draft,
  conflict, uncertain-save and identity guards. Older same-actor snapshots and
  previous identities cannot repaint the account value.
- Browser checks verify present/missing/long values, selection, label/help
  association, no input/tab stop, changed-primary conflict refresh, save payload,
  keyboard flow, and desktop/dark OS preference/mobile/200% zoom layouts. The
  Profile stays light under a dark OS preference; shared Staff dark-theme work is
  deferred under #456, and this flow has no appearance setting.

Focused implementation evidence:

- `dotnet build tests/Asap.Tests/Asap.Tests.csproj -c Release --no-restore`:
  passed, zero warnings/errors.
- `node tests/staff_profile_controller.test.js` and
  `node tests/staff_dotnet_frontend.test.js`: passed. The focused controller
  cases cover safe text, present/missing state, payload exclusion, draft refresh,
  pending mutation, same-actor stale snapshot and identity replacement.
- `node tests/staff_navigation_drafts_ui.test.js`: 271 journeys passed before
  the final conflict-refresh refinement; the focused controller test and the
  real-SQL browser journey passed again after that refinement.
- `python -B -m unittest test_selection.PopulationTests -v` from `scripts/ci`:
  6/6 passed after correcting the reviewed Browser test name.
- MTP real-SQL browser run: 1/1 passed. The first attempt failed because
  Playwright could not find the native definition by `getByRole('definition')`;
  the test now checks the semantic `dt`/`dd` markup and explicit ARIA
  associations directly. Final command:
  `dotnet tests/Asap.Tests/bin/Release/net10.0/Asap.Tests.dll --filter FullyQualifiedName~WeeklyProfileBrowserShowsPrimaryAccountInfoAndPreservesPreferenceContractsAcrossAccessibleLayouts --report-trx --report-trx-filename i440-weekly-profile-pass2-remediation-20261105-01.trx --results-directory .artifacts/test-results/i440-weekly-profile-pass2-remediation-20261105-01`.
  Receipt: `.artifacts/test-results/i440-weekly-profile-pass2-remediation-20261105-01/i440-weekly-profile-pass2-remediation-20261105-01.trx` (1/1 passed, 34.7 seconds).
- Browser report and screenshots:
  `.artifacts/browser/weekly-profile-4ae23e22ef71436f8c1622bb4c6ccabe/`.
  All six states (missing, long mobile, desktop, dark OS preference, mobile and
  native 200% zoom) had zero serious/critical axe findings, no overflow and
  loaded images; external requests and page errors were zero. Native 200% visual
  evidence includes `native-200-viewport-{top,form,bottom}.png` and
  `native-200-visual-report.json`. Automated checks do not replace human
  screen-reader acceptance.

Pass 1 and Pass 2 reconciliation:

- Fresh Luna MAX Pass 1 found no actionable issues and independently passed the
  profile-controller and frontend-structure Node tests.
- Luna MAX Pass 2 exercised browser refresh, stale snapshots, identity changes,
  pending/conflict recovery, text selection and SQL save/lost-response behavior;
  no product defect remained. Terra MAX found that the full-page native 200%
  screenshot cropped the right/lower portion of the form. The test now retains
  its existing axe/overflow scan and adds real CDP viewport captures at the top,
  form and bottom, with horizontal bounds checks for all Profile controls.
- The focused browser scan is named `desktop-dark-preference` to describe the
  tested OS preference without implying that a Profile dark theme exists.
- Pass 2 also found a stale native-browser method name in
  `scripts/ci/reviewed-test-categories.json`; the reviewed Browser population now
  names the current method exactly.

## #441 recipient and recovery contract

- Each new staff/report operation commits its primary row at the existing
  unsuffixed business key and, when distinct, an additional row at
  `{operation}:additional`. Both rows commit atomically under existing locks.
  The existing unique index prevents duplicate operations; `Changed` still
  counts staff/report operations, not recipient copies.
- DACPAC-owned kinds `weekly_summary_primary` and `weekly_summary_additional`
  bind each row to its original address slot. Queueing trims and validates
  addresses, compares normalized addresses without case, and applies existing
  recipient-domain/configuration restrictions separately. Both copies contain
  the same authorized report. A suppressed primary never blocks an eligible
  additional copy, or vice versa.
- Both dispatch authority checks require current opt-in, active identity,
  authorization and the original slot/address. Changes suppress that original
  row; worker or queue retries never redirect it or add another recipient.
  Each row retains its own lease, attempts, provider identity and outcome.
  Certified no-send failures can retry independently. Uncertain provider
  results retain ambiguity evidence and cannot automatically resend. `sent`
  records provider acceptance, not mailbox delivery.
- Existing stored `WeeklyActionSummaryEmail` values now mean an additional
  copy for new operations; no data rewrite or new preference is needed.
  Existing unsuffixed operations block backfill regardless of status. Legacy
  `weekly_summary` rows keep their original override-or-primary authority,
  exact destination and retry/history state. No completed period gains a
  retroactive copy. This applies to scheduled and forced operations.
- The Profile field remains optional and uses the existing save, normalization,
  rowversion and draft contracts. Staff access still owns the primary address;
  #440 displays it as account information. Other notifications are unchanged.

| Required cases | SQL/provider evidence (`WeeklyRecipient…` methods) |
| --- | --- |
| 1–9, 24: recipient combinations, normalization, opt-out, invalid/domain restrictions, either/both suppressed, stored preference | `CombinationPersistsIndependentIdentityAndOutcome` |
| 10–12: either copy succeeds while its sibling fails; retry/restart | `PartialSuccessRetriesOnlyTheUnacceptedCopyAfterRestart`, `DefinitiveNoSendFailureRetriesOnlyTheFailedCopy` |
| 13: uncertain response and retry refusal | `UncertainProviderOutcomeRetainsEvidenceAndPreventsAutomaticResend` |
| 14, 21–23: duplicate/concurrent queue attempts, normal deduplication, deliberate forced runs and same-operation retries | `ConcurrentQueueAttemptsAndForcedRunsKeepDurableOperationIdentity` |
| 15: competing delivery workers | `ConcurrentWorkersKeepOneLiveLeasePerCopy` |
| 16–20: deactivation, opt-out, primary/additional changes/removal, identity, library and role contraction | `AuthorityChangeSuppressesOriginalCopyWithoutReplacement`, before validation and at the readiness/intent barrier |
| 5, 24: Profile validation, blank/normalized preference, primary preservation, stale rowversion | `PreferenceValidationPreservesPrimaryAndRowversion` |
| 25: legacy pending/completed scheduled/forced operations | `LegacyOperationKeepsOriginalRecipientAndNeverBackfills` |

Implementation evidence at `84d01d7` (Release, local SQL Server 2022,
deterministic providers):

- `dotnet build Asap.sln --configuration Release --no-restore`: passed with
  zero warnings/errors. Initial restore was required for the new worktree.
- `node tests/staff_profile_controller.test.js`: passed; new optional label,
  description, blank/populated save, validation, opt-out and discard assertions
  join retained stale-response, identity, draft and uncertain-mutation checks.
- Initial SQL matrix: 37/37 passed. Expanded retained run: 61/64 passed;
  three existing cancellation assertions still expected one outbox row.
  Those tests now verify two atomic pending copies and recover both through
  the sweep. Original receipts remain in `.artifacts/i473-focused-sql/`.
- `WeeklyProfileBrowserPreservesAdditionalPreferenceValidationDraftsAndAccessibleLayouts`:
  1/1 passed. Real HTTP/SQL optional-input validation, save/reload, opt-in,
  discard and rowversion conflict; keyboard and accessible description checks.
  Desktop, dark, 390px mobile and native Chromium 200% zoom scans passed with
  zero serious/critical axe findings, external requests, page errors, missing
  images or horizontal overflow. Mobile screenshot inspected. Receipt:
  `.artifacts/i473-focused-browser/weekly-profile-browser.trx`.
- Final expanded SQL rerun: 66/66 passed, zero failed/skipped (76.6 seconds),
  including 41 new recipient-state cases and retained workflow/Profile tests.
  Receipt: `.artifacts/i473-focused-sql/weekly-focused-final.trx`; filter:
  `(FullyQualifiedName~WeeklyRecipient|FullyQualifiedName~WeeklySummary|FullyQualifiedName~StaffProfilePersistsAllPreferencesAndEnforcesAntiforgeryAndVersion)&TestCategory!=Browser`.

Pass 1 — independent requirements review by Luna MAX:

- One supported P2 coverage gap; no implementation defect identified. The
  original forced-run test replayed after both recipients succeeded, while
  partial-success retry tests exercised scheduled operations only.
- Parameterized the certified no-send failure scenario for both scheduled and
  forced operations, with either recipient failing. A fresh worker replays the
  same durable operation before retry. Assertions retain both recipient IDs,
  keys, address kinds and destinations; only the certified failed row resends,
  and the accepted sibling keeps its provider ID, timestamp and rowversion.
- Product code unchanged. Release build passed with zero warnings/errors.
  Targeted SQL/provider rerun: 4/4 passed, zero failed/skipped (23.2 seconds).
  Receipt: `.artifacts/i473-focused-sql/pass1-forced-partial.trx`; filter:
  `FullyQualifiedName~WeeklyRecipientDefinitiveNoSendFailureRetriesOnlyTheFailedCopy`.

These automated checks do not constitute human screen-reader or live-mailbox
acceptance. Full CI and integration/acceptance qualification follow #440.

Pass 2 — independent adversarial verification by Astra HIGH:

- Current SQL/provider matrix: 68/68 passed, zero skipped. Twenty independently
  constructed temporary probes also passed, zero skipped. No actionable integrity
  finding remained. Earlier zero-test CLI invocation is excluded from evidence.
- Probe source was archived under ignored `.artifacts/i473-pass2/`, removed from
  the test project, and a restored clean Release build passed with zero warnings
  or errors. No product changes were required.

| Adversarial transitions | Persisted outcome verified |
| --- | --- |
| Primary/additional/normalized duplicate; opt-out; invalid/domain restrictions | Exact independent recipients and suppressed states |
| Account/address/library/role changes before validation and at intent | Original row suppressed without redirect or backfill |
| Either sibling accepted; safe failure; same scheduled/forced replay after restart | Stable keys and accepted sibling rowversion; only failed copy retries |
| Provider timeout/lost acknowledgment; expired post-intent lease | Ambiguous evidence retained; unsafe resend refused |
| Concurrent queue/delivery workers; same/new forced IDs | Unique operation identities and fenced per-recipient leases |
| Legacy pending/completed/failed normal and forced operations | Original destination/history and safe recovery, no retroactive copies |
| Slot swaps/convergence, opt-out/domain contraction after partial success | Changed copy suppressed; accepted sibling unchanged |
| Old worker resumes after fresh pre/post-intent lease recovery | Fencing prevents duplicate provider sends |

The matrix and actual commands are retained in
`.artifacts/i473-pass2/review-result.txt` with three TRX receipts and probe source.
Worker restart is modeled by fresh workers over persisted SQL/lease state,
without terminating the OS process. Both issue-level review passes are complete;
final integration is evidence reconciliation, not another review pass.
