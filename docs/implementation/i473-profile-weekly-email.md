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
| #441 | Implemented; focused qualification below | Pending: Luna MAX | Pending: Astra HIGH state matrix | SQL and Profile browser | Awaiting independent reviews |
| #440 | Pending | Pending: Luna MAX | Pending: Luna MAX browser interaction | Pending | Waiting for #441 |

Requested child models/reasoning will be selected explicitly. The parent
session cannot switch its own model and retains the current Codex orchestration
configuration. Final reconciliation uses Astra EXTRA HIGH.

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
  its read-only Profile input is left for #440. Other notifications are unchanged.

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

Focused evidence (Release, local SQL Server 2022, deterministic providers):

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

These automated checks do not constitute human screen-reader or live-mailbox
acceptance. Full CI and integration/acceptance qualification follow #440.
