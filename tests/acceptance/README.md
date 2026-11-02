# ASAP automated acceptance

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1,d1,q1
```

Run from a tooling checkout containing the selected suite, with PowerShell 7, Git and working Microsoft
WSLC (WSL 2.9.3+). The command resolves one immutable application commit and
captures one tooling version/digest, then builds or reuses one image for all
selected suites. It executes exactly the selected suites once each, sequentially
in caller order, with a fresh disposable container, SQL database and synthetic
fixtures for every suite. Each runs headed Chromium/Playwright on Xvfb, exports
its own evidence and removes its container before the next suite starts.
It needs no application configuration, Entra sign-in or human browser interaction.
See [prerequisites and image design](../../dev/review/README.md).

`smoke-authenticated-review` checks the authorized System super-admin session,
Review Library (2), SQL-backed Suggestion details and an unmasked synthetic screenshot.
`smoke-synthetic-close` confirms Close silently through the real UI, verifies
one action POST, persisted Closed status and a changed SQL rowversion.

The default runs complete functional checks, including allowed writes to
disposable synthetic resources. `-ReadOnly` explicitly prevents scenario writes;
in smoke, the first scenario runs and closure is NOT RUN, making acceptance
incomplete (exit 2). The deprecated `-AllowReviewWrites` switch is accepted for
compatibility, has no additional effect, and conflicts with `-ReadOnly`.

Run the existing feature suite with:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite pr410
```

Issue #403 adds `i403` for measured title-row action overflow. Run it with its
relevant regressions, directly from PowerShell 7 in a tooling checkout containing
the suite:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr <PR_NUMBER> -Suite i403,d1,q1,pr410
```

Its five scenarios cover Closed's sole direct Open details, an empty result,
two and seven eligible commands, real column/container resizing, promotion and
demotion, changed font metrics, Enter/Space/Escape, menu/focus reconciliation,
one guarded Purchase submission, sorting/paging/filtering/refresh/scope retirement,
desktop/mobile/native 200% geometry, forced colors and the existing checkpoint
guards. A claimed Pending hold and fifteen synthetic Suggestions supply minimal
cardinality/pagination/scope fixtures; existing mutation fixtures remain independent.
The Purchase scenario is NOT RUN with `-ReadOnly`, making acceptance incomplete.
`i403-*-geometry.json` and matching PNGs stay in the suite's normal evidence
directory; native captures use the existing verified CDP viewport path.
Zero eligible commands and a sole non-details command additionally have focused
component coverage: actual authorized title rows always retain Open details.
Role/capability revocation, sign-out and retired handler cleanup remain covered
by the retained table/controller tests. D1 and Q1 protect their all-direct toolbars.
Human visual/screen-reader acceptance remains tracked by #398.

For a focused local Chromium regression without SQL/WSLC:

```powershell
node --test tests/acceptance/direct-actions.test.cjs
```

The S1 Workflow Settings suite is entirely read-only:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1
```

Its six scenarios cover real SQL-backed selected IDs (including an unavailable
saved ID), description/ID filtering, keyboard selection/removal, counts,
inheritance and explicit empty browser drafts, a failed catalog GET with retry,
provider draft edits/ordering/stable keys, zero/one/four rows, persistent template
help, and System/library geometry at 1440px, 1000px, 390px and native 200% zoom.
Screenshots and geometry JSON accompany axe/image/overflow checks. Catalog failure
is an injected pre-dispatch GET failure; successful choices come from the real
deterministic backend. Each scenario verifies stored Settings and their version
are unchanged. No Settings write permission or runner guard is broadened.
Persisted Save, Discard, explicit `[]`, inherited `null`, scope/version races and
patron policy remain the retained SQL integration/browser tests' responsibility.

The two responsive-layout scenarios explicitly place the picker halfway behind
an active Unsaved changes footer before focusing it, in both scopes at 100% and
native 200% zoom. Bounding rectangles and hit tests verify the complete input,
dropdown, active option, footer status and controls; overflow-ancestor and text
checks reject clipping. ArrowDown/ArrowUp, Enter, Escape, Tab/Shift+Tab and
empty/no-match queries verify focus and scroll stability. The existing
`s1-layout-{system,2}-native-200-picker.png` captures now show this dirty-footer
condition, with the input/list/footer measurements in matching geometry JSON.

Use a current local tooling checkout containing all three integrated suites. The
launcher discovers suites locally; `-Pr` selects the application revision only.
Run complete D1 and Q1 regressions once each alongside S1:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1,d1,q1
# Equivalent explicit array; a single name remains supported.
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite @('s1', 'd1', 'q1')
```

The five read-only D1 scenarios check direct actions, cancellation/focus, the
combined research row and generated URLs, patron plus placement, Closed and
retained-protection fields, and newest-first Activity with bigint ties. Toolbar
evidence covers wide, medium, narrow, very narrow, short-height and native 200%
views; Closed/locked-field screenshots include desktop, mobile and native 200%.
Each checkpoint retains the existing runtime/reviewer proof, axe, image and
overflow guards. Research destinations are inspected without external navigation.
Geometry and synthetic screenshots stay beside the report under
`$HOME/ASAP-Acceptance/<run>/`.

`d1-close-reopen-refresh` is NOT RUN in explicit read-only mode. By default
it consumes only the existing `close-reopen` fixture and checks
the accessible confirmations, authoritative status/field refresh and Activity
after each mutation. The three extra fixtures are `d1-closed` (rejected with no
hold), `d1-protected` (a successful current hold-operation journal), and `d1-history` (three
fixed events, including timestamp ties and IDs above JavaScript's safe range).
They never reset consumed data. Existing PR410 fixtures and scenario behavior
remain independent. Role variants, incomplete operations, quick-create
completion and LEAP configurations retain focused controller/SQL/browser
coverage; the suite reports those limits. Human visual/screen-reader acceptance
remains open under #398.

`-Commit <revision>` replaces PR selection; omitting both selectors resolves
`HEAD`. `-Rebuild` forces one shared image refresh, including for multiple suites.
Reports record independent application and tooling SHAs, suite, mode, scenario
status and actual container exit code. A build/startup blocker leaves the tested
application SHA empty until runtime verification succeeds.

Q1 and S1 are inherently read-only and need no extra flag. Do not repeat them in
read-only mode. Use `smoke` for infrastructure diagnosis and the complete
`pr410` regression only when a specific Settings reset,
mutation-guard, fixture or retained-scenario risk warrants it.

The five read-only Q1 scenarios exercise Additional copies at 1440, 1280,
1024 and 390 CSS-pixel widths, a 640px container inside a desktop window,
and native 200% zoom. They check six grouped columns, long/missing values,
the five primary sorts, underlying-field searches, claims, paging, refresh,
Open/Closed and library scope, safe row/button/BIB activation, keyboard focus
and Escape. Each screenshot retains the existing runtime, axe, image, page-error,
external-request and overflow guards. BIB links are inspected and clicked with
navigation prevented; the catalog is never contacted.

The review-only SQL seed adds 24 open tasks to the existing two (enough for a
second page), one closed task, and one task in a second synthetic library.
One synthetic claimant supplies claim/wrapping coverage. No Q1 scenario writes
tasks or changes the signed-in actor. Session replacement and reinserted stale
rows also have real Grid.js regression coverage in `staff_copy_table.test.js`.
Desktop screenshots are `q1-desktop-1440/1280/1024.png`; cards are
`q1-narrow-container.png` and `q1-mobile-390.png`; zoom is `q1-native-200.png`.
Matching `*-geometry.json` files record cells, buttons and scroll extents;
`q1-native-200-browser.json` records actual CDP viewport/zoom metrics. Evidence
at native zoom uses a verified CDP viewport capture, avoiding the pinned
Playwright clip bug on scrolled pages. Runtime/identity verification, PNG
dimension checks and all checkpoint guards still apply; a real-browser pixel
regression checks the scrolled capture.
Evidence
remains beneath `$HOME/ASAP-Acceptance`. Human visual and screen-reader review
remains tracked by #398.

## Runtime checks and authorization

The review-only HostingStartup provides local cookie bootstrap and a fixed,
credential-protected read-only runtime/SQL projection. The runner verifies the
built application SHA, tooling SHA, Testing environment, Local authentication,
SQL Server 2022 Developer database `AsapReview`, deterministic provider marker,
email capture and expected fixtures. Staff eligibility and Review Library access
are rechecked before scenarios, mutations and every screenshot. Capture requires
a page bound by the scenario executor to that verifier, the fixed
`https://localhost:8080/staff/` application and its authenticated workspace.
Unverified, external and authentication pages cannot export screenshots.

The synthetic `reviewer@example.org` account uses the normal secure HttpOnly
staff cookie, SQL authorization and antiforgery. No arbitrary identity headers
are installed on browser requests. Synthetic Entra placeholders never cause a
Microsoft request; production authentication is unchanged. The runtime creates
protected synthetic Polaris settings for the normal readiness checks. No live
Polaris or Postmark calls are authorized.

Only the scenario's allowed request kind and fixture ID may mutate in the
verified disposable runtime. Missing or consumed fixtures block the scenario.
The approved Settings reset scenario may separately authorize one
`POST /api/asap/staff/settings/reset?organizationId=2`, bound to the exact
Settings version it read. Other Settings writes and library scopes are blocked;
read-only scenarios cannot authorize a reset. Runtime and staff checks still
run at the mutation boundary.
Unexpected external requests, writes, native dialogs, page errors, serious or
critical axe findings, missing images and page overflow fail the run. Native
200% zoom uses the existing extension and verifies DPR/CDP zoom with CSS zoom 1.
Dialog checks use the visual viewport, text geometry, ancestor clipping,
keyboard focus and hit testing. Internal vertical scrolling is permitted only
when the message and focused controls remain reachable.

## Retained PR #410 coverage

The twenty existing browser scenarios are retained, with one additional
`settings-reset-approved` scenario: **21** in write mode. Read-only mode runs
**11** scenarios and marks **10** mutation scenarios NOT RUN, including reset;
`settings-discard` remains read-only.

| Requirement | Live suite implementation | Remaining obligations |
| --- | --- | --- |
| #401 shared dialog | Accessible name/description, safe initial focus, keyboard ring, Tab/Shift+Tab, Escape, Cancel, focus return, stacked detail modal | Human visual and screen-reader judgment |
| #407 Purchase | No-BIB reminder defaults/on/off, verified BIB auto-hold on/off, inline-only approval, Cancel, exact one POST/transition and reminder outbox key | Live reviewer execution |
| #407 concurrency | Duplicate click, real stale-version conflict, unverified BIB Purchase review/submission with explicit local error and unchanged SQL state, dirty-editor gate, normalized success presentation | Retained lower-level authority/session tests remain separate |
| #401 rejection | Synthetic template, Cancel, final Reject and close, no second confirmation, Closed/rejected and local outbox evidence | Live mailbox/provider acceptance is separate |
| #401 navigation | Request/Profile drafts, close/status/view/history, Back/Forward, late event-free edit, competing newer intent | Remaining scope/library/sign-out/new-suggestion combinations are explicit report gaps |
| #401 other workflows | Revert preserves unrelated draft, Close silently/Reopen with authoritative claim, additional-copy close/reopen | Combined editor Save, other staff claim, hold/recovery/delete remain explicit gaps |
| #401 Settings/Profile | Discard, combined dirty-reset review with Cancel, approved reset and authoritative reload, Profile uncertain recovery after aborted dispatch | Broader owned configuration/history remains in existing real-SQL tests; admin/logo/diagnostic/roster/email-retirement paths remain explicit gaps |
| Browser safeguard | Unexpected native dialogs fail; real beforeunload is deliberately elicited/dismissed | Browser unload remains intentional |
| #398 accessibility | axe, images/errors/overflow, keyboard, desktop/390px/native 200% | NVDA/VoiceOver, live announcements, long text, forced colors and visual judgment require humans |

The reset scenario reuses the seeded Review Library PatronSettings SuccessTitle
and EmailSettings overrides. Approval discards the local draft, removes those
scalar override rows, changes the aggregate Settings version and restores the
effective system SuccessTitle. It verifies one reset POST and one subsequent
scoped UI reload. The seeded custom rejection template retains its ID, content
and SQL rowversion. `hasOverrides` remains true because the API also counts
library-owned configuration. EmailSettings is a resettable scalar domain, not
a preserved owned collection. No extra synthetic catalog is seeded. Preservation
of custom fields, formats, auto-claim rules and history remains in existing SQL
integration tests; this scenario does not duplicate that coverage.

At native 200%, the pinned Playwright full-page capture passes the reduced CSS
document width (about 633px in the live fixture) to Chromium's capture clip,
which uses device-independent pixels. This crops the rendered image despite
the dialog fitting the actual visual viewport. The zoom scenario now exports
a complete viewport `confirmation-desktop-mobile-zoom-zoom.png` and a
zoom-corrected `-zoom-dialog.png`, both showing the actual synthetic UI.
`-zoom-geometry.json` records DOM/text/focus bounds, viewport information,
CDP metrics and capture dimensions so reviewers can distinguish capture
artifacts from UI clipping. The real Chromium plumbing regression reproduces
the full-page crop, checks the reliable captures, permits internal scrolling
and rejects clipped message/action content even without page overflow.

Ordinary checkpoints capture the configured browser viewport (1280 × 900 on
desktop, 390 × 844 on mobile). The helper permits explicit `fullPage: true`
for a genuinely necessary page capture; the native-zoom scenario always uses
viewport capture plus its corrected crop. PNG dimensions must match the
viewport/crop. Checkpoints validate required headings, controls and messages
against viewport/ancestor clipping and hit testing, reject unrelated open More
menus, and check confirmation bounds and buttons. Primary action selection
leaves More closed; secondary actions use its normal open/select/dismiss flow.

Settings evidence scrolls through the existing page without changing its layout.
`settings-discard-discard-review.png` shows discard consent;
`settings-discard-reset-review.png` and `settings-reset-approved-review.png`
show complete reset warnings and controls. `settings-discard.png` shows the
draft retained by Cancel. `settings-reset-approved-complete.png` shows the
success message; `settings-reset-approved-complete-setting.png` separately
shows the authoritative inherited Success title. Profile evidence includes
the retained draft (`-draft.png`) and the restored form after Back/Forward.
Existing screenshot names remain indexed in the report. Capture/required
evidence failures fail or block the scenario and preserve sanitized diagnostics.

Checkpoint audit (all captures show the surrounding application viewport):

| Checkpoint | Intended visible evidence and readiness |
| --- | --- |
| `staff-shell` | Loaded queue heading and synthetic staff identity after queue loading completes |
| Five `purchase-*-review` captures | Purchase heading, consequence, reminder choice where applicable and final Purchase/Cancel controls; review loaded, scrolled into view, More closed |
| Five `purchase-*-complete` captures | Request title and resulting status after the response, UI badge and authoritative SQL/outbox/duplicate-submit assertions |
| `reject-template-review` / `-complete` | Selected rejection template and Reject and close/Cancel; then request title and Closed after authoritative rejection/outbox checks and UI refresh |
| `confirmation-desktop-mobile-zoom-{desktop,mobile,zoom}` / `-zoom-dialog` | Stacked discard dialog, warning and controls after keyboard/focus checks; viewport dimensions and zoom-corrected crop checked |
| `profile-navigation-and-history-draft` / base name | Retained preference after Cancel; then restored Profile after Back/Forward finishes |
| `settings-discard-discard-review` / `-reset-review` / base name | Discard warning; combined inherited-reset warning; retained draft after Cancel, scrolled to its field |
| `settings-reset-approved-review` / `-complete` / `-complete-setting` | Reset warning/buttons; successful reset message after authoritative reload; separate inherited setting and its scope label |
| `unverified-bib-blocked` | Still-open Purchase review after the specific BIB error, zero submissions and unchanged SQL assertions |
| Both smoke checkpoints | Synthetic request title and Suggestion/Closed badge after their corresponding authoritative assertions |
| `*-failure` | Best-effort diagnostic viewport through the same runtime/reviewer/local-page guard; failures remain FAIL/BLOCKED even if diagnostic capture is unavailable |


## Reports and privacy

Each suite's flat `$HOME/ASAP-Acceptance/pr-415_s1_2026-11-01_13-30-00/`
(or `commit-454d8676e666_s1_2026-11-01_13-30-00/`) contains `results.json`,
`report.md`, unmasked synthetic screenshots, sanitized failure traces and a diagnostics
directory with SQL, initializer, web, display and Playwright logs. Early failures
still export a BLOCKED report. SQL passwords, bootstrap credentials, cookies,
private certificates, raw network traces and browser profiles are not exported.
Screenshots display synthetic staff identities, library/patron/request details,
history and settings normally, with no masks or blurring. No real patron/staff
data is authorized in this disposable runner. Runtime secrets remain outside
the rendered/exported evidence; raw network traces and full Settings documents
are still excluded. Artifacts are local and are never automatically uploaded
to GitHub issues or PR comments. Inspect before sharing.
`settings-reset-approved-state.json` contains the chosen synthetic before/after
values, versions, reset response, reload order, mutation evidence and owned
template fingerprints. It excludes the full Settings document and template bodies.

The default root is the current user's profile directory, such as
`C:\Users\<username>\ASAP-Acceptance`, regardless of which checkout or worktree
launches acceptance. Override it with an absolute path if needed:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite pr410 -ArtifactsRoot 'D:\ASAP Acceptance'
```

Paths containing spaces work; invalid or unwritable roots fail without a fallback.
The launcher prints each resolved directory before its container starts and
consolidates suite status, duration, artifact directory, counts and overall exit
at completion. Reports, screenshots, state/geometry evidence, diagnostics and
any build/container logs stay together. Default artifacts survive deletion of
the originating worktree. Keep custom roots outside repositories/worktrees for
the same persistence. Runs are retained until explicitly deleted; earlier
worktree-local `.artifacts/acceptance/` evidence is not automatically migrated.
The temporary `.review/build` image-build context remains disposable.

Names use each suite's start in host-local date/24-hour time; UTC timestamps and
host-local timestamps with UTC offsets remain inside results to disambiguate
daylight-saving transitions. Atomic allocation claims a new directory without
reusing existing evidence. Same-name collisions append `-02`, `-03`, etc. The
random run ID remains in metadata and container names, never the run directory.
Historical directories remain untouched. No PR or batch parent directory is added.

Shared diagnostics (`invocation-<id>.log`, its aggregate `.json`, and a
`build-<id>.log` when building) live directly under the artifact root. Build logs
are copied into every suite that starts. A fatal shared build/prerequisite failure
leaves requested suites NOT RUN, without fabricated execution reports; its shared
logs explain the blocker. For a started suite, inspect `report.md`, `results.json`,
`container.log`, `diagnostics/` and scenario failure traces beside the screenshots.
Per-suite failures/blockers continue to later suites when cleanup succeeds.
Unconfirmed container cleanup stops later starts and prints a recovery command.

Optionally open the newest default run directory in File Explorer:

```powershell
$root = Join-Path $HOME 'ASAP-Acceptance'
$latest = Get-ChildItem -LiteralPath $root -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($latest) { Invoke-Item $latest.FullName }
```

Exit 0 requires every required scenario in every requested suite to pass. Exit 1
means functional checks failed with no blocked/incomplete execution. Exit 2 means
blocked or incomplete acceptance, including required read-only skips or suites
that never started. The aggregate distinguishes PASS, FAIL, BLOCKED (could not
complete) and NOT RUN (never started). Mixed FAIL/BLOCKED preserves both and exits
2; scenario rows retain functional failures even when infrastructure also fails.
Per-suite reports retain NOT RUN and HUMAN REVIEW REQUIRED. Human visual and screen-reader
review remain required; overall acceptance stays INCOMPLETE. Automation does not
establish deployment readiness, close issues, approve or merge PRs.

## Developer checks and new suites

The native comma-array examples are for direct invocation in PowerShell 7,
not arbitrary shell or `pwsh -File` argument boundaries. No CSV parsing is used.
All selections are checked against the local `suites/registry.json` before any
revision fetch or execution. Unknown/empty/unsafe/duplicate (case-insensitive)
names fail with available names and a local-checkout update hint. Discover with
`Get-Content ./tests/acceptance/suites/registry.json`. Application PRs never supply
tooling automatically. Default omission still selects only `smoke`; select
`-Suite smoke,s1,d1,q1` explicitly when infrastructure diagnosis is useful.

Default writes preserve the same runtime, staff, fixture, endpoint, library and
rowversion guards. Never use real/shared data, live providers or external services.
Containers expose no ports and mount only their evidence directory, never the
host repository or persistent SQL volumes. Application PR source runs only in
the image/container. Cleanup retains evidence after failures and interruption.

Repository developers may use their existing .NET/Node/Chromium installation:

```powershell
dotnet build Asap.sln -c Release
dotnet build dev/review/src/Asap.ReviewInit/Asap.ReviewInit.csproj -c Release
dotnet build dev/review/src/Asap.ReviewProviders/Asap.ReviewProviders.csproj -c Release
dotnet build dev/review/src/Asap.ReviewBootstrap/Asap.ReviewBootstrap.csproj -c Release
node --test tests/acceptance/runner.test.cjs tests/acceptance/authentication.test.cjs tests/acceptance/launcher.test.cjs tests/acceptance/transport.test.cjs tests/acceptance/infrastructure.test.cjs
node --test tests/acceptance/workflow-settings.test.cjs
npm test
```

Plumbing tests check native multi-suite binding, complete selection validation,
single shared resolution/build (including forced rebuild), sequential fresh
containers, mixed outcomes, pipeline interruption, atomic timestamp collisions,
local-offset metadata, default/read-only/legacy modes, revision selection, tooling snapshots, reports, mutation
guards, TLS trust, normal cookie transport, HostingStartup loading, native zoom
and headed tab lifecycle. They do not replace live WSLC acceptance. Existing
SQL/.NET and browser journeys retain their CI entry points.
The focused Workflow browser regression uses the shipped DOM/CSS/editor with a
deterministic catalog, checks 100%/150%/200%, mobile and short-height layouts,
long labels, list scrolling, resize/scroll and footer-size changes. It also
proves the intersection assertion rejects the original obscured layout.

After building an image, run real failure/cleanup checks with its image tag and
application SHA (both appear in the build log):

```powershell
pwsh ./tests/acceptance/runtime.test.ps1 -Image <acceptance-image> -ApplicationSha <application-sha>
```

This deliberately fails a browser assertion, removes the DACPAC before startup,
interrupts initialization, and kills SQL unexpectedly. Each case must export
the correct nonzero result and diagnostics and leave no container behind.

Add a future suite as `suites/<id>.cjs` and add its name to `registry.json`.
Reuse the existing scenarios/helpers, fixture allowlist and report contract.
The runner test derives its expected registry from sorted suite modules, checks
exact JSON/export coverage, and keeps the individual suites' scenario and write
invariants. Adding a suite does not require editing an expected suite count/list.

## PR #420 and #421 integration contract

Keep the two PR branches independent until #420 merges. At that point #421 must
update from `main` and retain this combined content:

| Shared file | Required combined result |
| --- | --- |
| `suites/registry.json` | `["d1", "i390", "i403", "pr410", "q1", "s1", "smoke"]`, each once, sorted |
| `runner.test.cjs` | Dynamic module discovery and exact registry coverage; all i390 assertions (four read-only scenarios), all i403 assertions (five scenarios, only keyboard/guarded dispatch writes), and every existing suite invariant; current discovery-based CI/browser guards |
| `README.md` | Both issue-suite descriptions, commands, evidence and authorization limits |
| `../browser/staff-native-zoom.cjs` | Both `formatRuleGeometry` and `actionGeometry` imports; collapsed/expanded Format rules scans plus direct/overflow action geometry, scoped axe, Escape/focus and full table scans; retain named state/viewport coverage |
| `../../dev/review/src/Asap.ReviewInit/AcceptanceFixtures.sql` | Both idempotent i390 custom fields/rules (`i390_note`, `i390_retired`) and all 16 `acceptance-i403-*` title rows, including the claimed Pending hold; preserve the existing book and claimant identities and every prior fixture |

The expected textual conflicts are the single-line registry, adjacent issue
documentation additions, and native-zoom imports. Resolve by retaining both
additions. Runner assertions, SQL fixture blocks and CSS changes must also be
reviewed even when Git merges them automatically. Do not duplicate fixture keys,
replace another suite's data, or broaden runtime/scenario/endpoint/rowversion
write authorization. This records a future resolution; neither branch includes
the other PR's implementation or fixture additions.

After the update, run `i390,i403,s1,d1,q1,pr410` sequentially against the integrated
head with separate evidence, then complete full CI and closure review. Full CI
qualification and the merge decision remain separate from draft validation;
human visual/screen-reader acceptance remains tracked by #398.
