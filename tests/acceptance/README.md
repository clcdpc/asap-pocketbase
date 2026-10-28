# On-demand ASAP acceptance

## Start with smoke

Run the two-test smoke suite unattended:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
```

Suites default to **Local** authentication: no Entra registration/configuration,
interactive sign-in, MFA, viewer or Windows GUI session is required. The runner
automatically seeds ASAP Reviewer (`reviewer@example.org`), resolves its actual
SQL StaffUser ID, validates its System super-admin/Review Library access, and
establishes the application's normal staff cookie. Local mode does not test
OIDC/MFA. `-ShowBrowser` optionally opens the viewer.

`smoke-authenticated-review` checks the authorized super-admin session, Review
Library (2) workspace and SQL-backed synthetic Suggestion details. In read-only
mode, `smoke-synthetic-close` is NOT RUN. With explicit write authorization it
confirms Close silently through the UI, verifies one action POST, persisted
Closed status and a changed rowversion. Each invocation uses fresh fixtures.
The launcher handles Docker, certificates, Playwright and Chromium. Omit
`-AllowReviewWrites` for read-only smoke. Reports (`results.json`, `report.md`)
and privacy-safe screenshots are under `.artifacts/acceptance/<timestamp-run-id>/`.
Two PASS results establish automated smoke success; overall human acceptance
stays INCOMPLETE. This does not complete PR #410's feature acceptance.

## Full PR #410 suite

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites
```

Run from this tooling branch with Docker Desktop (local Linux x64 engine),
Compose, PowerShell 7 and Git available. PowerShell manages the application,
SQL/DACPAC/Hangfire, deterministic fixtures, HTTPS, runner image, Playwright,
Chromium and browser viewer. No host Node/Playwright installation or setup
commands are needed. Local mode creates synthetic configuration inside the
disposable environment and never reads or modifies the reviewer's Entra file.
See [review configuration](../../dev/review/README.md).

Use `-Auth Entra` explicitly for real OIDC integration testing:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -Auth Entra -AllowReviewWrites
```

Entra mode retains the external configuration and interactive first-run setup.
Reuse your existing Entra registration with the authorized Web callback
`https://localhost:8080/signin-oidc` and ID tokens enabled. No client secret or
new registration is required. Complete real Entra/MFA in the automatically
opened browser viewer. A successful local form POST, active super-admin session
and Review Library (2) access are required. The authenticated context is reused
across scenarios; each scenario gets its own application tab. Auth state is
ephemeral. `-AuthenticationTimeoutSeconds 900` allows a longer sign-in window.
Ordinary manual review (no suite) defaults to Entra. Explicit `-Auth` overrides
either default; Entra errors never trigger a Local fallback.

Reports and screenshots are written automatically under
`.artifacts/acceptance/<timestamp-run-id>/`. The command displays results and
leaves the app available for manual investigation. No issue comments, labels,
approvals, closures or PR merges are performed. #398's human review remains open.

## Modes, identities and lifecycle

```powershell
# Read-only is the default without explicit write authorization
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410

# Validate container identity/runtime without signing in or opening the viewer
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -CheckOnly

# Validate the combined stacked branch separately
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit HEAD -Suite pr410 -AllowReviewWrites

pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Status
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Stop
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Clean
```

`-Pr` and `-Commit` are exclusive; PR selection fetches the actual HEAD through
the existing Git remote. Application SHA comes from verified runtime/image
evidence; tooling SHA is baked from the independent tooling checkout. Working
changes are flagged. Blocked startup records the requested SHA and leaves the
verified application SHA empty. Tooling HEAD is never substituted for the app.

`-AllowReviewWrites` authorizes synthetic fixture actions only. Without it,
purchase/rejection/close/reopen scenarios are NOT RUN and every attempted server
mutation is blocked. Read-only scenarios still exercise real local draft,
dialog/keyboard/layout, Settings discard and Profile recovery behavior. The
Profile failure is injected before dispatch; no preference save is claimed.
`-ReadOnly` remains an explicit alias. It conflicts with `-AllowReviewWrites`.

Each run has new project-specific volumes and fixtures. A private input copy
uses `Seed: Review`, preserving the external
configuration's manual-review seed preference. The previous acceptance
app is stopped, with all data/logs/reports retained. Existing manual data is
never reset or stopped automatically. If it owns port 8080, explicitly Stop it.
Lifecycle operations select the latest environment; `-Project <printed-name>`
selects a retained environment. Clean explicitly removes only that environment's
disposable volumes/state and keeps reports. No manual fixture cleanup is needed
between acceptance invocations. Missing/ambiguous/consumed fixtures block their
scenarios; the runner never repairs or resets them in place.

- Exit 0: selected automated mode completed without failures/blockers.
- Exit 1: assertion, browser guard or runner execution failed.
- Exit 2: configuration, identity, authentication, runtime or fixture blocker.

Partial reports and read-only/check-only completion remain INCOMPLETE; successful
automation never establishes complete human acceptance.

## Isolation and mutation protection

The review-only HostingStartup implements `POST /_review/auth/session`. It is
unavailable outside the explicit Testing/Local/deterministic review runtime or
when `-Auth Entra` is used. A separate random 256-bit per-run credential is
mounted read-only only in web and acceptance. The endpoint checks it in constant
time, rejects caller identity inputs and validates isolated SQL/provider evidence
before resolving the fixed synthetic reviewer through `StaffEligibilityService`.
It issues the existing secure HttpOnly staff cookie, not another session system.
Direct `X-ASAP-Test-*` identity headers are rejected in both Docker review modes;
lower-level Testing authentication remains supported. No review assembly is
included in production publish output.

Only Playwright's trusted request context sends the bootstrap credential; normal
browser/API requests use cookies and the existing antiforgery policy. The
credential is never browser JavaScript state or a URL, and is revoked after the
run. It cannot target another email, tenant, role or organization.

Harness 0.3.0 adds an optional one-off service to the existing Compose project.
PowerShell checks local Docker, services and actual immutable image labels,
completed initialization, project networks/volumes, deterministic settings,
private SQL, loopback-only HTTPS and viewer, and absence of unsafe mounts. It
renews sanitized read-only host evidence while the browser container runs.
The evidence must be less than 15 seconds old and is revoked on failure/exit.
Keep the launcher running throughout the suite.

The container correlates host evidence with live HTTPS readiness, baked
application/tooling identity, project/hostname and a credential-protected
review-only GET endpoint. That endpoint reuses the existing fixed SQL probe
under the runtime login: SQL Server 2022 Developer, `AsapReview`, Review mode,
capture mail, actual deterministic provider marker and finite synthetic fixture
projections. It accepts no SQL/reset input and returns no configuration, staff
identity, patron details or email bodies. SQL admin credentials, private TLS,
runtime application config and Docker socket are not mounted in the runner.

Identity, provider evidence and authenticated reviewer authority are rechecked
before scenarios and at each write boundary. The original request-kind/ID
allowlist, rowversions, antiforgery and server authorization remain authoritative.
Unexpected external requests, mutations, native dialogs, page errors, serious
or critical axe findings, missing images and page overflow fail the run. A
session change/expiry blocks rather than restarting authentication capture.

Fixtures retain `acceptance-pr410-*` LegacyIds, independent mutating records,
verified/unverified BIB variants and the custom rejection template. The Pending
hold fixture retains its deliberate deterministic pre-dispatch hold-query
failure, preserving purchase-routing evidence without a live hold. Email uses
local file capture. No live Polaris/Postmark traffic is authorized.

The runner image pins Playwright/Chromium 1.62.1. Its real browser extension
continues to verify native 200% zoom, doubled DPR and CDP zoom, with CSS zoom 1.
The localhost TCP forwarder preserves HTTPS, hostname checks and full OIDC POST
callbacks. Container Chromium trusts only the environment's public review root;
no TLS verification bypass or Windows certificate installation is used.

## Retained PR #410 coverage

The existing twenty scenarios are unchanged. Their actual outcomes are recorded
per run, with explicit issue/requirement mappings.

| Requirement | Live suite implementation | Remaining obligations |
| --- | --- | --- |
| #401 shared dialog | Accessible name/description, safe initial focus, keyboard ring, Tab/Shift+Tab, Escape, Cancel, focus return, stacked detail modal | Human visual and screen-reader judgment |
| #407 Purchase | No-BIB reminder defaults/on/off, verified BIB auto-hold on/off, inline-only approval, Cancel, exact one POST/transition and reminder outbox key | Live reviewer execution |
| #407 concurrency | Duplicate click, real stale-version conflict, unverified BIB and dirty-editor gates, normalized success presentation | Retained lower-level authority/session tests remain separate |
| #401 rejection | Synthetic template, Cancel, final Reject and close, no second confirmation, Closed/rejected and local outbox evidence | Live mailbox/provider acceptance is separate |
| #401 navigation | Request/Profile drafts, close/status/view/history, Back/Forward, late event-free edit, competing newer intent | Remaining scope/library/sign-out/new-suggestion combinations are explicit report gaps |
| #401 other workflows | Revert preserves unrelated draft, Close silently/Reopen with authoritative claim, additional-copy close/reopen | Combined editor Save, other staff claim, hold/recovery/delete remain explicit gaps |
| #401 Settings/Profile | Discard, combined dirty-reset review with Cancel, Profile uncertain recovery after aborted dispatch | Reset approval/full owned history, admin/logo/diagnostic/roster/email-retirement paths remain explicit gaps |
| Browser safeguard | Unexpected native dialogs fail; real beforeunload is deliberately elicited/dismissed | Browser unload remains intentional |
| #398 accessibility | axe, images/errors/overflow, keyboard, desktop/390px/native 200% | NVDA/VoiceOver, live announcements, long text, forced colors and visual judgment require humans |

The report retains PASS, FAIL, BLOCKED, NOT RUN and HUMAN REVIEW REQUIRED.
It separates environment, authentication mode/status, automated functional checks, human
obligations, uncovered requirements and overall acceptance (always INCOMPLETE).
Local PASS records `mode: local`, `role: super_admin`, `library: 2` and never
claims OIDC verification. Entra PASS records `mode: entra` and the verified real
form POST callback.

## Artifacts and privacy

Each run writes `results.json`, `report.md`, scenario screenshots and safe failure
step/response diagnostics. Early Docker/startup failure gets a launcher BLOCKED
report with browser checks NOT RUN. No raw Playwright/network trace, cookie,
storage export, authentication screen, credential, request header or arbitrary
response body is exported. During Entra authentication only the local callback's
method/path/status is retained; no Entra URL or payload is recorded. Screenshots
mask staff identity, audit actors and profile emails. Inspect before sharing.

The viewer is loopback-only, protected by a random per-run WebSocket token.
The token stays out of report/console output. Raw VNC is container-local. The
browser profile and viewer logs live in the one-off container, with temporary
browser files in tmpfs. Completion/cancellation removes it. After a forced
launcher kill, run Stop/Clean to remove any surviving browser container before
sharing the machine; the next acceptance invocation also removes it.

## Developer validation and adding suites

The reviewer command needs only Docker/PowerShell/Git. Repository developers
may run focused tests with their existing development Node/.NET/Chromium tools:

```powershell
dotnet build Asap.sln -c Release
dotnet build dev/review/src/Asap.ReviewInit/Asap.ReviewInit.csproj -c Release
dotnet build dev/review/src/Asap.ReviewProviders/Asap.ReviewProviders.csproj -c Release
dotnet build dev/review/src/Asap.ReviewBootstrap/Asap.ReviewBootstrap.csproj -c Release
node --test tests/acceptance/authentication.test.cjs tests/acceptance/runner.test.cjs tests/acceptance/launcher.test.cjs tests/acceptance/transport.test.cjs tests/acceptance/infrastructure.test.cjs
npm test
```

These check fail-closed safety/reporting, actual PowerShell helpers, real Git PR
HEAD resolution, certificate idempotence/trust, IPv4/IPv6 HTTPS POST forwarding,
HostingStartup loading, real headed tab lifecycle and native zoom/accessibility plumbing. Synthetic
transport/browser plumbing is not real Entra or PR #410 acceptance. The full
SQL/.NET guard and retained browser journeys keep their existing CI entry points.

For Docker smoke validation use `scripts/review/Test-AsapReviewSmoke.ps1` with no
recorded environment. Live container NSS trust, noVNC access protection and
interactive Entra callback/suite execution must also be exercised on a working
engine. Never report them passed from unit/plumbing tests alone.

Add future suites as `suites/<id>.cjs` and register their ID in `registry.json`.
Both launcher and runner use that registry and the same command. Reuse the
existing helpers, per-scenario write/fixture allowlist, finite fixtures, human
obligations and uncovered-requirement reporting. Do not add a test language or
authentication bypass. The compatibility acceptance script forwards to the
single launcher; it contains no independent orchestration.
