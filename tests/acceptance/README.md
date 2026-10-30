# ASAP automated acceptance

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
```

Run from PR #412's tooling checkout with PowerShell 7, Git and working Microsoft
WSLC (WSL 2.9.3+). The command builds or reuses one disposable image, initializes
a fresh SQL Server database, authenticates locally as a synthetic staff user,
runs headed Chromium/Playwright on Xvfb, exports a report and removes the container.
It needs no application configuration, Entra sign-in or human browser interaction.
See [prerequisites and image design](../../dev/review/README.md).

`smoke-authenticated-review` checks the authorized System super-admin session,
Review Library (2), SQL-backed Suggestion details and an unmasked synthetic screenshot.
`smoke-synthetic-close` confirms Close silently through the real UI, verifies
one action POST, persisted Closed status and a changed SQL rowversion.

Omit `-AllowReviewWrites` for read-only smoke. The first scenario still runs;
the closure is NOT RUN and cannot dispatch a mutation. Each invocation starts
with fresh, equivalent fixtures. `-ReadOnly` is explicit read-only mode and
conflicts with `-AllowReviewWrites`.

Run the existing feature suite with:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites
```

`-Commit <revision>` replaces PR selection; `-Rebuild` forces an image refresh.
Reports record independent application and tooling SHAs, suite, mode, scenario
status and actual container exit code. A build/startup blocker leaves the tested
application SHA empty until runtime verification succeeds.

Run Q1 from its tooling checkout against the implementation PR:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr <Q1_PR_NUMBER> -Suite q1 -ReadOnly
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr <Q1_PR_NUMBER> -Suite smoke -ReadOnly
```

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

Only the scenario's allowed request kind and fixture ID may mutate, with
`-AllowReviewWrites`. Missing or consumed fixtures block the scenario.
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

Each unique `$HOME/ASAP-Acceptance/<timestamp-run-id>/` contains `results.json`,
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
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites -ArtifactsRoot 'D:\ASAP Acceptance'
```

Paths containing spaces work; invalid or unwritable roots fail without a fallback.
The launcher prints the resolved directory before environment startup and prints
that directory, the full `report.md` path, automated result and scenario counts
at completion. Reports, screenshots, state/geometry evidence, diagnostics and
any build/container logs stay together. Default artifacts survive deletion of
the originating worktree. Keep custom roots outside repositories/worktrees for
the same persistence. Runs are retained until explicitly deleted; earlier
worktree-local `.artifacts/acceptance/` evidence is not automatically migrated.
The temporary `.review/build` image-build context remains disposable.

Optionally open the newest default run directory in File Explorer:

```powershell
$root = Join-Path $HOME 'ASAP-Acceptance'
$latest = Get-ChildItem -LiteralPath $root -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($latest) { Invoke-Item $latest.FullName }
```

Exit 0 means selected automated checks succeeded, exit 1 means test assertion or
execution failure, and exit 2 means an environment/authentication/startup blocker.
Read-only skipped mutations remain NOT RUN. Reports distinguish PASS, FAIL,
BLOCKED, NOT RUN and HUMAN REVIEW REQUIRED. Human visual and screen-reader
review remain required; overall acceptance stays INCOMPLETE. Automation does not
establish deployment readiness, close issues, approve or merge PRs.

## Developer checks and new suites

Repository developers may use their existing .NET/Node/Chromium installation:

```powershell
dotnet build Asap.sln -c Release
dotnet build dev/review/src/Asap.ReviewInit/Asap.ReviewInit.csproj -c Release
dotnet build dev/review/src/Asap.ReviewProviders/Asap.ReviewProviders.csproj -c Release
dotnet build dev/review/src/Asap.ReviewBootstrap/Asap.ReviewBootstrap.csproj -c Release
node --test tests/acceptance/runner.test.cjs tests/acceptance/authentication.test.cjs tests/acceptance/launcher.test.cjs tests/acceptance/transport.test.cjs tests/acceptance/infrastructure.test.cjs
npm test
```

Plumbing tests check revision selection, tooling snapshots, reports, mutation
guards, TLS trust, normal cookie transport, HostingStartup loading, native zoom
and headed tab lifecycle. They do not replace live WSLC acceptance. Existing
SQL/.NET and browser journeys retain their CI entry points.

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
