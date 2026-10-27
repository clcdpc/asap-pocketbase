# On-demand ASAP acceptance

This runner exercises a repository-managed disposable Docker review app using
headed Playwright Chromium and normal Entra/MFA authentication. It is separate
from `npm test`, `npm run test:browser` and automatic PR CI. The first registry
entry is `pr410`, for issues #401/#407 and the manual-review notes on those
issues. It does not implement #389 / PR #411.

## Prerequisites and first run

Use Windows, PowerShell 7, Git and Docker Desktop's local Linux engine, plus the
review harness [configuration and HTTPS setup](../../dev/review/README.md).
Use `"Seed": "Review"` and a nonproduction Entra registration permitting
`https://localhost:8080/signin-oidc`. The reviewer must sign in as the active
review super-admin with access to Review Library (organization 2).

Acceptance additionally needs optional Node 24 and the existing pinned
development dependencies:

    npm ci
    npx playwright install chromium

No Node dependency is added to Web, migration or deployment packages.

From the acceptance tooling branch, start PR #410's exact application revision:

    $parent = '792b039de3010c832ad6aaa1d65ec09807e3410c'
    $config = '<absolute-path-to-external-review.json>'
    pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit $parent -Config $config -NoBrowser
    pwsh ./scripts/review/Invoke-AsapAcceptance.ps1 -Suite pr410 -ExpectedCommit $parent -CheckOnly
    pwsh ./scripts/review/Invoke-AsapAcceptance.ps1 -Suite pr410 -ExpectedCommit $parent -AllowReviewWrites

Click **Sign in with Microsoft** in Chromium and complete Entra/MFA normally.
Automation resumes after verifying the ASAP staff session, role and library
access. One authenticated browser context is reused across scenarios; each gets
its own application tab. The default authentication timeout is five minutes;
`-AuthenticationTimeoutSeconds 900` permits a longer interactive session.
Closing Chromium, cancelling authentication or Ctrl+C produces blocked results.

The mutation switch explicitly authorizes this run's synthetic request actions.
The suite opens no production/IIS endpoint and uses no authentication bypass
headers. It never comments on issues, changes labels, approves/merges PRs or
marks acceptance complete.

## Separate application and tooling revisions

`Invoke-AsapReview.ps1` materializes the requested application SHA and copies
the current review harness into it. Run both scripts from this tooling checkout.
The runner reads this checkout's `.review/current.json`; running from a
different checkout does not adopt or reset another reviewer's environment.

Application SHA comes from verified live image evidence. Tooling SHA comes
from the runner checkout; uncommitted changes are flagged separately. A blocked
run records the requested application SHA and leaves the verified SHA empty.
It never silently substitutes tooling HEAD for the application.

After the parent run, explicitly clean that disposable environment when its
review is finished. Then validate the combined stacked branch:

    pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Clean
    $combined = (git rev-parse HEAD).Trim()
    pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit $combined -Config $config -NoBrowser
    pwsh ./scripts/review/Invoke-AsapAcceptance.ps1 -Suite pr410 -ExpectedCommit $combined -AllowReviewWrites

Check that the first report has application=parent SHA and tooling=stacked SHA;
the second should have application=tooling=stacked SHA. These are separate
review environments and separately recorded runs.

## Read-only checks and exit codes

    pwsh ./scripts/review/Invoke-AsapAcceptance.ps1 -Suite pr410 -ExpectedCommit $parent -ReadOnly

`-ReadOnly` runs the shell, dialog/keyboard/layout, local-draft navigation,
Settings discard/reset review, Profile recovery and unload checks. It blocks
server mutations and reports purchase/rejection/close/reopen scenarios
`NOT RUN`. Profile recovery injects a network failure **before dispatch**;
it tests the real recovery UI without saving reviewer preferences.

`-CheckOnly` verifies the environment without launching a browser. All scenarios
are `NOT RUN`. Neither switch constitutes full functional acceptance.

- Exit 0: the selected automated mode completed without failures/blockers.
- Exit 1: a browser assertion, browser guard or runner execution failed.
- Exit 2: configuration, identity, authentication, runtime or fixture blocker.

Human visual and assistive-technology acceptance remains required even when
the selected automated checks pass.

## Isolation and fixture lifecycle

Acceptance requires harness 0.2.0. It verifies the Ready state, exact image SHA,
local Docker context, live container identity/network/volumes, IPv4 loopback
HTTPS publication, private SQL port, trusted HTTPS health, deterministic Testing
configuration, completed initializer and fixed SQL evidence from `AsapReview`
on SQL Server 2022. A runtime-local marker confirms the review provider startup
actually executed. The probe uses the runtime SQL login and returns only a
fixed safe projection; it has no arbitrary SQL/reset input.

Before every scenario and again before a server write, runtime identity is
rechecked. Only POST actions for that active scenario's resolved request kind
and ID can be dispatched. SQL rowversions and the application's authorization,
session, antiforgery and admission checks remain authoritative.

An exclusive `acceptance.lock` under the managed revision directory prevents
overlapping runs. A stale lock blocks execution; after confirming the old
runner has exited, remove only that revision's lock or explicitly Clean/Start.

Fixtures use logical `LegacyId` keys `acceptance-pr410-*`, resolved to current
SQL identifiers by the evidence probe. Purchases, rejection, stale action and
close/reopen have independent records. Draft-only checks reuse a fixture
without saving it. Missing/ambiguous/consumed fixtures block their scenarios.
Restarting with `-KeepData` never repairs a consumed fixture.

The purchase Pending hold fixture deliberately gets a deterministic
pre-dispatch hold-query failure if a worker runs; this preserves purchase
routing evidence without making an external hold. Email uses local file
capture, including reminders. No Polaris/Postmark call is authorized.

For a fresh mutating run, finish the current review and explicitly execute:

    pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Clean
    pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit $parent -Config $config -NoBrowser

Clean removes that disposable database. The acceptance runner never invokes
Clean, resets a database or changes an existing review environment itself.

## Coverage and interpretation

Each scenario maps issue/requirement text into `results.json` and `report.md`.
These are the implemented browser groups; execution status is determined by
each actual run.

| Requirement | Live suite implementation | Existing deterministic evidence / remaining work |
| --- | --- | --- |
| #401 shared dialog | Accessible name/description, safe initial focus, keyboard ring, Tab/Shift+Tab, Escape, Cancel, focus return, stacked detail modal | 21 controller cases also cover teardown/disposal/competing ownership |
| #407 Purchase | No-BIB reminder on/off and staff default, verified BIB auto-hold on/off, inline-only approval, Cancel, one POST/transition, exact reminder outbox key, double click, real stale-version conflict, unverified/dirty block | Existing action/SQL tests retain payload/authorization/session/cancellation coverage |
| #401 rejection | Selected synthetic template, Cancel, final Reject and close, one POST, authoritative Closed/rejected, local outbox delivery mode | No real email; provider/mailbox acceptance is separate |
| #401 navigation | Dirty request close/status/view, dirty Profile view/history, Back/Forward Cancel/approve, late event-free edit, newer intent/stale control | Remaining library/scope/sign-out/new-suggestion combinations are explicit report gaps |
| #401 editor/other actions | Revert with unrelated inline-draft preservation, Close silently, Reopen with authoritative manual claim, additional-copy close/reopen | Combined auto-claim Save, clear another claim, hold/recovery/permanent delete remain explicit gaps with retained lower-level tests |
| #401 Settings/Profile | Settings discard, one combined dirty-reset review with Cancel, Profile uncertain recovery using an aborted pre-dispatch save | Reset approval/full owned-history preservation, all administration/logo/diagnostic/roster paths and email retirement remain explicit gaps |
| #401 browser safeguard | Unexpected native alert/confirm/prompt fail; genuine beforeunload is explicitly elicited/dismissed | Browser-managed unload is the intentional exception |
| #398 accessibility | axe serious/critical, page errors, external requests, visible images, page overflow, dialog control bounds, desktop/390px/native 200% | Actual NVDA/VoiceOver, live announcements, visual judgment, long text and forced colors require human observation |

The report separates automated functional results, environment/integration,
real Entra sign-in, human visual/screen-reader obligations and overall readiness.
Supported scenario outcomes are PASS, FAIL, BLOCKED, NOT RUN and HUMAN REVIEW
REQUIRED. Overall acceptance stays **INCOMPLETE**. Do not convert unexecuted
coverage or a successful environment-only check into acceptance.

## Artifacts and privacy

Every invocation creates a timestamp/UUID directory in
`.artifacts/acceptance/` with `results.json` and a GitHub-ready `report.md`.
Application checkpoints add screenshots; failures add assertion source/operator
and safe scalar expected/actual details plus a
`<scenario>-failure-trace.json` application step/response trace.

These intentionally use network-free diagnostic traces. Raw Playwright trace
archives can include cookies, antiforgery headers and session responses even
after sign-in, so they are not exported. No Entra traffic, login screenshots,
request headers, credentials, storage state or arbitrary response bodies are
recorded. Screenshots mask staff identity, audit actors and profile emails while
keeping dialog headings visible. An ephemeral Chromium profile is removed on
normal completion/cancellation; a forced OS/process kill may require manual
removal of that run's `asap-acceptance-*` temporary directory.

Artifacts are Git ignored. **Inspect screenshots and diagnostics before
sharing**, particularly after reviewer-added data. Paste a reviewed report
summary into #401/#407 manually; retain #398's human requirements.

## Runner validation

Safety/reporting tests require only Node; no Docker, SQL or sign-in:

    node --test tests/acceptance/runner.test.cjs

Build the review components and run separate local plumbing checks:

    dotnet build Asap.sln -c Release
    dotnet build dev/review/src/Asap.ReviewInit/Asap.ReviewInit.csproj -c Release
    dotnet build dev/review/src/Asap.ReviewProviders/Asap.ReviewProviders.csproj -c Release
    node --test tests/acceptance/infrastructure.test.cjs
    npm test

The plumbing tests exercise .NET HostingStartup loading and native Chromium
zoom/accessibility screenshots with a synthetic local HTML page. They are not
Entra, SQL or PR #410 acceptance. Full SQL/browser guards still use their
existing CI commands and real SQL Server.

## Troubleshooting and adding a suite

- Missing/malformed/stopped state: run the existing Status command and repair or
  deliberately Start the managed environment. The runner will not infer Ready.
- SHA/version mismatch: launch from the tooling branch with the exact application
  SHA. Older images require a rebuild; incompatible old library seeds need the
  explicit fresh-database procedure.
- Docker engine/context blocker: use Docker Desktop's local Linux engine.
  Remote tcp/ssh/UNC endpoints are rejected.
- HTTPS blocker: initialize/trust review TLS using the harness. PowerShell uses
  Node's system CA support; do not disable certificate verification.
- Entra blocked: verify redirect URI, active account and super-admin access.
  No session/bypass-header workaround is provided.
- Fixture blocker: see the scenario key in the report, finish the review and
  explicitly Clean/Start when appropriate.
- Native zoom failure: install the pinned Chromium build and permit its local
  test extension; CSS transforms/zoom are not a substitute.
- Failed assertion: use its report requirement, sanitized source/scalar detail,
  response trace and screenshot. Reproduce application defects on the parent
  application SHA; keep harness failures separate.

To add a suite, export its ID, capabilities, mutation flag, explicit scenarios,
human obligations and coverage gaps from a module in `suites/`, then register
it in `suites/index.cjs`. Reuse authentication, environment checks, fixture
resolution, accessibility/assertions and reporting. Declare writes per scenario
and register each resolved fixture with `allowFixture` before acting; additional
write endpoints require a narrow reviewed guard change. Keep scenario setup
finite and independent, with no general test-definition language or plugin layer.
