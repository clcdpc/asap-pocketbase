# PR #377 functional acceptance campaign

## Starting evidence (2026-10-10)

Branch `codex/contract-integrity-remediation`; HEAD and reviewed baseline
`d6b4309c5902843e1ee3a947c1936587c6eb9fe0`. No commits since the baseline and
no tracked or untracked changes at start. Existing draft PR #377 targets
`codex/staff-next-parity`. Hosted run 38059368827 completed successfully for
the baseline; test-IIS activation was skipped. The previous PR description
records the nine AUDIT corrections and 1,357 non-browser / seven browser cases.

## Initial coverage inventory

This inventory was made before adding tests. Browser fixture cases are not
equivalent to the individual states they scan. HTTP/service tests are not
counted as browser acceptance.

| Workflow | Existing evidence | SQL/provider assertions | Priority gap |
| --- | --- | --- | --- |
| Staff authentication/navigation | `staff.cjs`, `StaffCorrectiveJourneyTests`, staff authority and suggestion races | SQL current user/role/library, HTTP denials, stale versions; some browser session replies are synthetic | Real authority loss combined with pending UI action |
| Settings | `settings.cjs`, SettingsAuthority, SettingsMetadataRoundTrip, SettingsCourseIntegrity | Actual GET/editor POST/reload, persisted overrides/owners/versions/bigints, atomic rejection and sibling preservation | Settings editor → fresh patron form → accepted SQL snapshot |
| Patron | `patron.cjs`: real desktop/mobile login/submit/duplicate/logout; `patron-submit-session.cjs`: simulated deferred POST replies | Two actual requests/pending outboxes; simulated races explicitly assert no durable effects | Configured custom fields, optional clearing, repeated submit, real committed response loss/switch |
| Staff requests | `staff.cjs`, `staff-request-contracts.cjs`, RequestBrowser/RequestActions | Edit/claim/purchase, stale changes, history, SQL outbox/copy/recovery assertions | Real Reject → stale version → notification → Reopen → silent close |
| Pickup/hold | PickupIntentRaces, PickupReceiptAndNativeLockRaces, PickupOutcomeEvidence, HoldOutcomeEvidence, NativeIdentityPickupFollowups; selected staff Operations browser states | Durable journals, no-effect/unknown/success, leases, cancellation, aliases, native mismatch, exact provider invocation counts | Browser-triggered pickup journal and no-repeat evidence |
| Email | EmailOutcomeEvidence, NotificationAuditEvidence, Slice5EmailAndSchedule, production Postmark adapter simulations | Actual rendered outbox, captured transport, accepted/ambiguous/quarantine/cancellation and recipient correlation | Browser-triggered event through rendered captured delivery |
| Migrated runtime | ImportedSnapshotBrowser (inside StaffRequestBrowser), MigrationCli runtime resolver and independent reconciliation tests | Export/import/reconcile then real historical editor; frozen/retired snapshots and owned catalog preservation | Imported effective config/zero defaults/public submission/auto-claim and protected history via HTTP |
| Accessibility | Pinned axe 4.10.3, keyboard/dialog/route checks in browser runners | External-request/page-error/image/overflow/serious-critical guards | Scan new failure and recovery states, no visual redesign |
| Faults/concurrency | CurrentSubmissionConfiguration, native/pickup/hold/outbox barriers, post-commit cancellation; browser route deferrals | Actual SQL rollback/no-change and durable provider outcomes; frontend simulations separately identified | Real HTTP 400/409/401/406 and commit followed by lost browser response |

The harness uses Release-generated `wwwroot` copied from tracked Frontend,
actual ASP.NET Core Kestrel/routing/JSON/DI, and unique disposable SQL Server
2022 databases deployed from the DACPAC. Testing-only staff headers retain
SQL eligibility middleware and mutation authorization. Typed deterministic
Polaris fixtures simulate the provider boundary; production SDK/HTTP parsing
is independently exercised by provider tests. Workers are disabled and outbox
dispatch is recorded; captured sends must be explicitly processed. This does
not establish Microsoft Entra OIDC, live Polaris/Postmark, or IIS acceptance.

## A. Assessment and evidence boundary

**READY FOR HUMAN DEVELOPMENT ACCEPTANCE**, subject to the exact-head closure
receipt attached to PR #377. Confidence is high for the deterministic
application/SQL contracts exercised below. Confidence is bounded for real
identity, providers, worker hosting, usability and cutover. This decision does
not authorize deployment or establish production readiness.

The final receipt in the PR description records the final source SHA, local
commands and outcomes, added commit, ZIP digest, and hosted check run. It must
identify the current PR head; the successful baseline run above cannot close
this campaign. This document is part of that candidate, so it deliberately
does not embed its own commit hash.

## B. Git scope

All changes stay on `codex/contract-integrity-remediation` and the existing
draft PR #377. The starting SHA is recorded above. No pre-existing work was
present to preserve. No replacement branch/worktree/PR, reset, rebase, squash,
merge, issue closure, tag, or host activation belongs to this campaign. Final
commit/push/clean-tree and exact-head CI evidence are recorded in the receipt.

## C. Functional acceptance coverage

| Workflow | Existing evidence | New automation | SQL/provider assertions | Result and remaining gap |
| --- | --- | --- | --- | --- |
| Staff identity/navigation | Actual staff browser, scoped/unregistered denial, queue/history/draft/keyboard paths; SQL authority/revocation races | Stale rejection after a real concurrent edit preserves the complete detail DTO | Current SQL role/library/version remain enforced; recorded state/notes/history unchanged on stale reject | Representative browser + SQL evidence. Live Entra and a browser action overlapping real SQL role revocation remain distinct gaps; existing authority races exercise the owning boundary. |
| System/library Settings | Settings browser/editor/reload, metadata/bigint/sibling/ownership, scoped reset, course integrity | Actual library custom fields/options/rules/publication/automatic-claim editor; two real concurrent editors; invalid required-select save | Winner retained; real HTTP400 leaves the authoritative snapshot unchanged; fresh patron form consumes saved effective rules | Full configuration chain covered. A future editor validation guard may reject before HTTP; the runner records the observed boundary and always checks authoritative no-change. |
| Public patron authentication/submission | Desktop/mobile real login/submit/duplicate/logout; response/session simulations | Required text/select, hidden DVD fields, optional clear, publication clear, opt-out, pickup, actual 400/409/401/406, loss/recovery/session switch | Exactly five accepted requests, native identity, pickup and automatic claimant, immutable custom labels/option display values, optional null/absence | Real browser/HTTP/SQL. Native missing-title validation and required text server rejection are separate observations. No PIN/token enters reports. |
| Patron concurrency/unknown outcome | Auth generations, current-config/native SQL barriers and simulated deferred replies | Actual 201 committed then delayed/lost, logout/login B before A's real response, configuration save after initial validation before pickup dispatch, pre-dispatch network abort | No extra requests, one pickup provider write and completed confirmed journal; no hold calls; exact `submission_configuration_changed` rejection | Real commits are distinguished from simulated replies. A deliberate fresh retry after lost success yields the original duplicate ID. Node directly proves handler no-replay guards. |
| Staff title requests | Actual search/edit/claim/assignment/purchase/optional fields/history and additional-copy actions; SQL mutation tests | Reject template UI, stale reject, accepted reject, queued notice, reopen, silent close; desktop/mobile scans | Exact final status/claim/notes, one reject/reopen/close event each, one template event/outbox, current native recipient and rendered captured envelope, one send across two deliveries | Browser + SQL + capture sender. Queue confirmation correctly says delivery pending; it does not imply sent email. |
| Pickup/hold Operations | Existing staff Operations screens and repeated actions; real-SQL success/no-effect/unknown/cancel/leases/recovery/aliases/native mismatch; raw pinned SDK parsing tests | Browser-triggered confirmed pickup plus rejected current-config write | One positive pickup PUT/journal, no replay or second dispatch; retained hold create/reply/adoption/operator recovery invocation assertions | Typed simulator for new browser flow; raw provider simulations are separate. Live contracts INV-001/002 remain. |
| Email | Real outbox/jobs + Postmark recording HTTP simulations, timeout/hold notices, invalid recipient/suppression/quarantine/cancellation | Five patron submissions and one staff rejection through actual jobs to capture sender | To/from/subject/body checked; exact single sends, sent provider IDs, SQL business keys; retained identity/hold-location regressions | Captured application delivery and simulated Postmark acceptance. Real provider delivery/inbox and deployed scheduling remain manual. |
| Migrated runtime | Safe export/import/reconcile followed by actual historical editor/reset browser | Imported system9 vs library0 defaults, publication/local format/rule, staff scope, branch context rejection, protected hold legacy link, copy legacy link, fresh local-format submission | Literal 5/30/14/14/14, missing historical native ID preserved, protected rejection unchanged across request/events/global outbox/hold/pickup journals, mapped copy/frozen snapshots/capabilities, native7801 branch20 automatic claim/custom snapshot | New checks are HTTP/SQL within the browser-host fixture. Historical edit/reset is browser. Reconciliation ends before normal app mutations. Fresh imported submission checks outbox existence, not captured delivery. |
| Accessibility/interaction | Pinned axe4.10.3, keyboard/dialog/Escape/tab/navigation, external-request/error/image/overflow guards | New failure/success/recovery and mobile states retain those guards | No serious/critical axe violations, external traffic, page errors, visible missing images or horizontal overflow permitted | Automated correctness; human zoom, assistive-technology and workflow clarity exploration remains valuable. |

## D. Confirmed production defects and corrections

All three are P2 user-visible correctness defects. No backend/provider/schema
change was justified by this campaign.

1. **Pickup changes reopened a pending submission.** In
   `Frontend/patron/js/auth.js:116,122`, pickup selection overwrote button
   disabled state without considering an active/unknown submission.
   `submit.js:60` also lacked the pending-handler guard. Reproduction on the
   original shipped modules produced two POSTs after changing pickup while the
   first was pending; the second conflict could replace the first success.
   Shared `submitInProgress` now guards both the button and handler; unknown
   status remains locked, logout clears state, and stale identity completions
   cannot clear a newer session's busy state. Direct-handler and pickup-change
   regressions, plus the real committed-response browser flow, cover it.
   Same-root search covered every patron submit-button disabled assignment and
   auth cleanup; remaining unconditional enable is owned by auth reset.

2. **Optional choices were submitted without selection and could not be
   cleared.** `custom-fields.js:33`, `form-ui.js:110` and the static publication
   select in `patron/index.html:170` omitted a blank option. Optional custom
   selects implicitly selected their first enabled option. Both select paths
   now expose a blank option; required controls retain native required
   validation and empty authoritative publication lists stay empty. Node
   regression fails when either blank option is removed; actual patron UI
   select-then-clear persists no optional custom value and null publication.
   Same-root search found these two patron data-select constructors; format
   and pickup selectors have separate required/default contracts.

3. **Format-specific custom labels disappeared.**
   `form-rules.js:38` normalized custom rules to mode alone, dropping the
   effective API's `label` before the DOM renderer could consume it. A saved
   Book-specific label therefore showed the base label to patrons while server
   errors/snapshots used the override. Normalization now retains the label.
   Node rollback fails at the exact label assertion; the real Settings/form
   flow checks the rendered label, validation message and accepted SQL
   snapshot. Same-root search traced config normalization to
   `custom-fields.js`'s existing label override renderer; ordinary field labels
   and server effective-rule resolution were already preserved.

Harness corrections are not application defects: selectors must read DOM
properties rather than absent HTML value attributes; styled opt-out controls
are operated through their actual label; imported source IDs resolve through
the mapping table rather than being invented as LegacyId; the deterministic
provider's branch20 must match the imported catalog; final silent-close reason
is the owning service's `Silently Closed` contract. Patron titles are compared
against the service's pinned title-case normalization. Optional publication is
explicitly configured in Settings; Book defaults require publication.

## E. Added and extended regressions

- `PatronJourneyTests.FunctionalAcceptanceBrowser.cs` /
  `ConfiguredPatronAcceptanceBrowserVerifiesRealFailuresCommitsAndRecovery`
  and `browser/functional-acceptance.cjs` add one discovered case with ten
  explicit outcome checkpoints, real editor/form/HTTP, server barrier, SQL,
  provider journal and captured delivery assertions.
- `patron_submission_identity_race.test.js` adds initial/cleared optional
  selections, retired option exclusion, label preservation, in-flight pickup
  locking and direct repeated-handler assertions. Existing identity/bigint/
  malformed reply cases stay present.
- `staff.cjs` and its existing .NET wrapper add three scanned states to the
  original 58, stale/no-change rejection, final events/claim and rendered
  captured notification assertions.
- `PatronJourneyTests.ImportedFunctionalRuntime.cs` and
  `ImportedSnapshotBrowser.cs` extend the existing imported browser fixture
  after pre-activation reconciliation. These add source configurations, hold
  evidence and a copy, then verify actual runtime endpoints and SQL. They do
  not create another migration or schema owner.
- `browser/run.cjs` selects eight cases with minimum8; CI excludes exactly
  those eight from its minimum1357 main partition. No prior case was removed.

## F. Independent adversarial review

Three independent read-only agents first inventoried gaps, then reviewed the
final changes. Staff/settings reviewer traced actual UI requests/DTOs, mutation
authority and SQL/email assertions; patron reviewer ran the focused Node test
and scratch-copy rollback mutations of each fix; migration reviewer traced
source mappings, runtime/reconciliation lifecycle and nine retained AUDIT
guards. None edited the checkout or ran competing .NET/database tests.

Review found one decisive test-quality defect: awaiting a repeated handler
before checking request count allowed a regressed unresolved POST to make
Node exit0. The test now checks cardinality after an event-loop turn before
awaiting completion. Independent pending-guard and unknown-guard rollback
mutants both exit1 (`2 != 1`, `14 != 13`); current source exits0 with completion
log. Pickup, optional custom/publication and label rollback mutants also fail.
Enter in the browser is only an additional interaction, not independent proof
that a disabled-button form invokes the handler.

Other review corrections require full stale-reject DTO equality, exact
configuration conflict code, manual reopen claim with no rule, and direct
captured envelope comparison. The imported-copy runtime read and global
no-change journal/outbox assertions close identified evidence gaps. No further
confirmed production blocker or weakened prior audit guard was found.

Retained AUDIT-001 through009 cover native notification recipient correlation,
atomic Settings read metadata, independently derived bootstrap authority,
excluded pre-activation rows, current config acceptance, frozen hold pickup
name, literal legacy zero defaults, recursive decoded duplicate JSON property
rejection and typed Postmark acceptance/quarantine. Full validation retains
their tests; no relevant backend implementations were modified.

## G. Exact candidate validation protocol

Discovery reports **1,365 .NET cases**, split **1,357 main + eight selected
browser cases**; frontend discovery reports **53 test files**. These are cases
and files, not counts of UI states or internal assertions. Main partition is
the CI filter and includes some embedded browser witnesses; it is not a claim
that every case bypasses a browser.

The exact-head receipt records outcomes for these commands:

```powershell
dotnet restore Asap.sln
dotnet build Asap.sln --configuration Release --no-restore
dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --no-restore --list-tests
$mainFilter = [regex]::Match((Get-Content .github/workflows/dotnet.yml -Raw), "'FullyQualifiedName!~[^']+'").Value.Trim("'")
dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --no-restore --minimum-expected-tests 1357 --filter $mainFilter
npm ci
npm test
npm run test:browser
dotnet publish src/Asap.Web/Asap.Web.csproj -c Release -o <owned-artifact-root>/web-publish
dotnet publish src/Asap.Migration/Asap.Migration.csproj -c Release -r win-x64 --self-contained true -o <owned-artifact-root>/migration-publish
```

Package validation reproduces the existing `.github/workflows/dotnet.yml`
publish/vendor manifest/forbidden runtime/ZIP identity steps, verifies the ZIP
digest before extraction and calls the packaged `Deploy-AsapTest.ps1` with
`-ValidateOnly`, exact SHA/digest/label. DACPAC is Sql160/schema12 and Hangfire
schema9. Native published migration smoke uses `describe`, stopped SQLite
`export`, package `validate`, DACPAC import and pre-activation `reconcile` on a
fresh owned SQL database. All setup, transport and teardown failures are
failures, not skips. Local logs/keys/capture artifacts remain ignored.

No local SQL environment blocker remains: installed SQL Server2022 replaces
the unavailable local Docker daemon. Hosted CI independently uses SQL2022 in
Docker. Real Entra/providers/IIS/cutover are intentionally unexecuted external
gates and remain bounded below.

## H. Prioritized human/deployed acceptance

| Priority/category | Perform and expected result | Why automation is insufficient | Gate |
| --- | --- | --- | --- |
| 1 Usability/visual | Explore a realistic librarian session on desktop/mobile, zoom and assistive technology; verify terminology, notices, focus and dense Settings/request history remain understandable | Axe/keyboard/layout assertions cannot judge task clarity or every real assistive-technology combination | Before human development acceptance/integration into PR344 |
| 2 Microsoft Entra | Use registered staff, library admin and super-admin plus unregistered/disabled account, tenant/account switch and sign-out in a test deployment; verify expected access and revocation | Testing headers exercise SQL authority but bypass real OIDC/cookies/browser/account policy | Before deployed acceptance; production configuration before release |
| 3 Polaris | With approved test patrons, verify deployed reads/alias/native identity and full catalog vs omission INV-001; separately authorize controlled pickup/create/reply with reassignment/eligibility checks INV-002; expect truthful known/unknown results and no blind replay | Typed/raw fixtures cannot prove vendor deployment/access filtering or make barcode writes conditional on native identity | Test-provider acceptance before operational use; confirmed operator policy before production |
| 4 Postmark | Send controlled notices to owned inboxes using approved test configuration; check template rendering, sender/domain, acceptance, receipt and suppression | Capture sender/raw simulations cannot establish real delivery/domain/provider-account configuration | Before enabling deployed email; production configuration before release |
| 5 IIS/deployment | Separately authorize exact tested ZIP deployment, health/schema/key protection, recycle and background jobs; expect retained sessions/durable recovery and correct assets | ValidateOnly proves artifact safety/identity, not host bindings, rights, certificates or runner activation | Before deployed acceptance. `test_cd_activation: pending_runner_setup` remains |
| 6 Migration/cutover | Rehearse stopped-source export, backup/restore, identity/config readiness, import/reconcile before app activity, controlled activation and recovery; verify provenance/counts and operator boundaries | Synthetic source cannot establish real data quality, backup/recovery capability or operational timing | Before cutover/production; separately authorized task |

## I. Closure and integration recommendation

Recommend PR #377's implementation closure after exact-head validation and
human development acceptance, then integration into PR #344 through its normal
review process. No merge or issue closure occurs in this task. No confirmed
implementation defect remains in the reviewed candidate. Coverage gaps above
are explicit rather than silently represented as automated success.

INV-001 does not establish that a catalog omission means loss of library
authority; prior classification remains until observed reclassification or
explicit operator deactivation. INV-002 retains the gap between verified
native patron/eligible branches and a later barcode-addressed remote write
without native conditional authorization. Existing fences, journals and final
acceptance checks bound those risks but cannot make the remote operation
atomic. See `polaris-mutation-boundary.md` for the pinned contract/evidence.
Both remain provider/operator release requirements, not speculative new fixes.

Production readiness, live provider acceptance, backup/recovery/cutover,
repository rename and test-IIS activation remain deferred. The stopped legacy
source is migration/forensic input; this campaign adds no writable fallback.
