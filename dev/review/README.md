# ASAP Docker review

Run the two-test smoke suite unattended from PR #412's tooling checkout:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
```

Acceptance suites default to **Local** authentication. No Entra registration,
client/tenant IDs, configuration file, sign-in, MFA or browser interaction is
required. The runner seeds `reviewer@example.org` (ASAP Reviewer), an active
System super-admin, and establishes the normal staff cookie. Authorization
still uses the actual application and SQL Server database. Local mode does
**not** test OIDC, MFA or Entra configuration.

The first test verifies the authorized staff session, the Review Library (2) staff
workspace and SQL-backed synthetic Suggestion details. The second confirms
Close silently in the UI and verifies exactly one action POST, persisted Closed
status and a changed rowversion. It is NOT RUN without `-AllowReviewWrites`.
Each run gets fresh fixtures. Omit `-AllowReviewWrites` for read-only smoke.
The viewer is optional in Local mode; add `-ShowBrowser` to observe the run.
Find `results.json`, `report.md` and privacy-safe screenshots under
`.artifacts/acceptance/<timestamp-run-id>/`. Two PASS results validate the smoke
workflow; human acceptance stays INCOMPLETE and PR #410 still needs its full
acceptance review. The launcher handles all container/browser/certificate setup.

## Full feature review

From PR #412's tooling checkout, run:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites
```

Have **Docker Desktop with a local Linux x64 engine, Docker Compose v2,
PowerShell 7 and Git** installed and running. The command fetches PR #410's
actual HEAD, builds the review stack, initializes SQL/DACPAC/Hangfire and
synthetic fixtures, creates HTTPS material, starts headed Chromium on the
container's virtual display, authenticates automatically, and writes a report
under `.artifacts/acceptance/`.
The application remains running after the suite finishes.

There is no host Node, npm, Playwright, Chromium, .NET SDK or SQL installation
step. No separate bootstrap, TLS command, Compose command, fixture seed, commit
lookup, environment variable or configuration-file construction is required.

## Real Entra integration

Select Entra explicitly to test the actual OIDC integration:

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -Auth Entra -AllowReviewWrites
```

Ordinary manual review without a suite also defaults to Entra. Explicit `-Auth`
overrides either default. Entra failures never fall back to Local.
Only Entra mode reads the external configuration. On first use it asks for the Entra application/client ID, allowed
tenant ID(s), initial review super-admin email and optional display name
(default `ASAP Reviewer`). It saves the existing review JSON format outside
Git at `~/ASAP-Review/review.local.json`. Valid existing configuration is reused;
the established bootstrap workspace and legacy local-config location are also
recognized. `-Config <external-file>` supports explicitly configured/unattended
use and validates required values without prompting or overwriting that file.

Your existing Entra application registration, including a production
registration, can be reused. An authorized administrator must have registered
this **Web redirect URI**, with ID tokens enabled:

```text
https://localhost:8080/signin-oidc
```

ASAP uses its existing ID-token-only flow. No client secret, signing credential,
Graph permission, alternate OAuth flow or additional registration is introduced.
The launcher never changes the registration. Complete normal Entra sign-in/MFA
in the automatically opened viewer. The suite resumes when it verifies the real
POST callback and the active review super-admin's library access.

The launcher prints the automated result, PASS/FAIL/BLOCKED/NOT RUN counts,
verified application SHA, report location, remaining human-review obligations
and environment status. Automation never establishes complete human acceptance.

## Commands and retained environments

```powershell
# Ordinary application review (no acceptance image/profile)
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410

# Read-only scenarios (server mutation scenarios are NOT RUN)
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410

# Direct application commit; mutually exclusive with -Pr
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit <application-sha>

pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Status
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Stop
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Action Clean
```

Lifecycle commands select the most recently recorded environment. Each retained
environment also has its own `.review/**/state.json`; use
`-Project <printed-project-name> -Action Status|Stop|Clean` to select an older one.
Stop retains data. Clean explicitly removes that project's disposable volumes
and local state; acceptance reports remain under `.artifacts/acceptance/`.
Status and cleanup work after failed startup as well as successful startup.

Each acceptance invocation creates a separate project and fresh database. A
run-specific input copy selects Review fixtures. Local mode generates valid
synthetic GUIDs inside that environment; reviewer-maintained Entra files are
never read or modified. Entra mode preserves the external manual
configuration's `Seed: None` preference. A
previous acceptance environment is stopped automatically to release port 8080;
its data, logs and reports are retained. No existing database is reset. Retained
manual review is never automatically stopped or destroyed: if it owns port 8080,
explicitly Stop it before acceptance. `-AllowReviewWrites` authorizes only the
synthetic fixture actions. Repeated runs need no fixture cleanup.

Ordinary review reuses an environment with matching application, tooling and
authentication mode or restarts matching stopped data. `-KeepData` remains accepted; retained data is always preserved until
explicit Clean. A different application/tooling revision requires deliberate
lifecycle selection/cleanup. `-Rebuild`, `-NoBrowser`, and a manually registered
alternate `-Port` remain available. Acceptance uses port 8080 for the exact
registered callback. `Invoke-AsapAcceptance.ps1` forwards its legacy options to
this launcher and requires no host Node.

## HTTPS and browser viewer

Harness **0.3.0** extends the same Compose file: `sql`, `db-init`, `web`, plus
the optional one-off `acceptance` service. Review-only images are separate from
published Web/migration artifacts. SQL has no published port. Web publishes
`127.0.0.1:8080 -> 8443`. Acceptance does not start during ordinary review.

Local authentication is confined to the review-only HostingStartup assembly,
which is absent from production Web publish output. `POST /_review/auth/session`
requires Testing, explicit Local mode, the expected project, live deterministic
providers, isolated Review SQL evidence, and a random 256-bit per-run credential.
Only web and acceptance mount it read-only, separately from the evidence token.
The endpoint compares credentials in constant time, accepts no identity inputs,
rechecks the fixed reviewer's SQL eligibility, and issues the existing secure,
HttpOnly session cookie. Direct `X-ASAP-Test-*` impersonation is rejected by the
review boundary in both modes. Application antiforgery and rowversion checks
remain in effect. The runner sends the credential only through its trusted
Playwright request context. It is absent from browser JavaScript, URLs, logs
and reports, and revoked on completion/cancellation. The browser profile is
removed; the application/database remain available for investigation.

The initializer creates a root/server pair once per environment. It discards
the root signing key, keeps the server PFX/password in `tls-private`, and puts
only the public root in `tls-public`. Web mounts the private volume read-only;
acceptance receives only the public volume and trusts it in Chromium's ephemeral
NSS store and Node's extra CA store. Normal hostname/chain verification remains
enabled. Expired/incomplete material fails closed rather than rotating during
an active environment. No Windows trust is needed for acceptance.

For ordinary manual Windows browser review, the launcher invokes the existing
TLS helper automatically and asks before trusting the public review root in
CurrentUser/Root. `-TrustReviewCertificate` explicitly authorizes that trust;
Clean removes the selected environment's public trust. The old standalone TLS
helper remains available for older review workspaces.

The acceptance image pins Playwright/Chromium **1.62.1**, matching the lockfile.
Xvfb, Fluxbox and noVNC provide the interactive display. Its dynamically selected
viewer port binds only to IPv4 loopback. A random 256-bit per-run token protects
the WebSocket; it is passed to the normal browser in the URL fragment, omitted
from reports and console output, and never persisted as application auth state.
Raw VNC listens only inside the container's loopback namespace. There is no
published remote debugging endpoint. The one-off container is removed after
completion/cancellation, including its ephemeral authentication profile.

A tiny TCP forwarder inside that container maps both localhost address families
at port 8080 to `web:8443`, preserving encrypted bytes, the Host header and the
OIDC form POST. Chromium uses the existing localhost origin throughout.

## Identity, isolation and troubleshooting

Git fetches `refs/pull/<number>/head` from the existing origin, never the
synthetic merge ref. The exact application SHA and separate tooling SHA/version
are baked into images, recorded in state and correlated with running images.
Working-tree tooling changes are explicitly reported.

PowerShell verifies the local Docker endpoint, exact services/images, completed
initializer, private database/config/TLS volumes, deterministic provider settings
and loopback application/viewer publication. During acceptance it renews a
read-only, sanitized proof file. No SQL password or arbitrary environment value
is forwarded. The container also verifies live trusted health, protected fixed
SQL/provider evidence, application/tooling/project/hostname identity, fixtures
and reviewer authorization before scenarios and mutations. Missing, changed or
stale evidence blocks writes. The runner has no Docker socket, application
configuration, server private keys or SQL administration credentials.

- Docker timeout: check the existing local Linux engine. This tooling does not
  install/configure Docker Desktop or the operating system.
- Entra failure: verify the exact Web callback above, ID tokens, tenant and
  initial review email. No bypass or additional client credential is needed.
- Port conflict: Stop the current manual environment, retaining its data.
- Failed startup: use Status and the printed `.review` log/state directory;
  explicit Clean removes only the selected disposable environment.
- Consumed fixture: a new acceptance invocation automatically gets fresh data.
- Browser startup/report failure: inspect `report.md` and safe diagnostics;
  human review remains required. Do not share raw container/viewer logs.

Detailed coverage, privacy, developer validation and adding suites are in
[tests/acceptance/README.md](../../tests/acceptance/README.md). A review-only
Compose smoke check is `pwsh ./scripts/review/Test-AsapReviewSmoke.ps1`; it uses
synthetic Entra configuration, verifies the challenge and lifecycle, and does
not claim interactive Entra acceptance.
