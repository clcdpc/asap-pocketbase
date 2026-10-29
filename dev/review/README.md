# ASAP acceptance with WSLC

```powershell
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke -AllowReviewWrites
```

Run from PR #412's tooling checkout. The command builds or reuses one acceptance
image, starts one disposable container, initializes a fresh SQL Server 2022
database, authenticates a synthetic staff user locally, and runs Chromium and
Playwright automatically. No configuration file or browser interaction is needed.

## Prerequisites

Install PowerShell 7 and Git, and have a working Microsoft WSLC installation.
[Microsoft's prerequisites](https://learn.microsoft.com/windows/wsl/tutorials/wsl-containers)
require WSL 2.9.3 or newer. Verify once:

```powershell
wsl --version
wslc version
wslc run --rm hello-world
```

Use an x64 Windows machine with WSL 2 virtualization already operational. Allow
at least 6 GB of memory and two CPUs for WSLC; 8 GB and four CPUs are recommended
for image builds. The first build needs internet access for Microsoft images,
NuGet, npm and Chromium. No host .NET SDK, Node, npm, SQL Server, Playwright or
certificate installation is needed.

## Running suites

```powershell
# Read-only smoke: authenticated review passes; synthetic closure is NOT RUN.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite smoke

# Existing twenty-scenario feature suite.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites

# Select an exact application revision instead of a PR.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit HEAD -Suite smoke -AllowReviewWrites
```

`-Pr` fetches the PR's exact HEAD from `origin`, independently of the current
tooling checkout. `-Commit` selects a Git commit; the two selectors are exclusive.
The default suite is `smoke`. Omitting `-AllowReviewWrites` enables read-only
mode; `-ReadOnly` makes it explicit. `-Rebuild` forces a fresh image build.

Reports, privacy-safe screenshots, failure traces and sanitized SQL, initializer,
web and Playwright logs appear in `.artifacts/acceptance/<timestamp-run-id>/`.
The launcher prints the report path and removes the container after success,
failure or interruption. A forced termination of the launcher process itself
may require `wslc remove --force <printed-container-name>`.

- Exit 0: selected automated checks succeeded; read-only mutations remain NOT RUN.
- Exit 1: an assertion or browser execution failed.
- Exit 2: environment, startup, configuration, authentication or cleanup blocker.

The two smoke tests exercise authorized SQL-backed fixture review and, when
explicitly authorized, a real UI closure with one POST and a changed rowversion.
Automated success leaves human visual/screen-reader acceptance INCOMPLETE.
See [suite coverage and developer checks](../../tests/acceptance/README.md).

## Container

`Containerfile` publishes the selected application's Web/DACPAC/Hangfire assets
and the current review initializer/providers/bootstrap in an SDK build stage.
The final SQL Server 2022 CU27 Ubuntu 22.04 image contains ASP.NET Core 10.0.13,
Node 24, the locked npm dependencies and matching Playwright/Chromium binaries.
It contains no SDK, real configuration, credentials or initialized database.
Dependency/browser layers remain reusable when application or tooling changes.
Image tags include application SHA, tooling SHA and a tooling content digest;
working changes cannot silently reuse an older image.

The Bash entrypoint generates local secrets and configuration, starts SQL on
localhost, waits for an authenticated connection, runs the existing initializer,
imports Data Protection material, starts ASAP at `https://localhost:8080`, waits
for real readiness, starts Xvfb, and runs the selected suite with headed Chromium.
Child failures and signals produce blockers; shutdown terminates child process
groups and exports diagnostics. SQL data, certificates, cookies and configuration
disappear with the container. Only the artifact directory is mounted from Windows;
no ports are published.

Local authentication seeds `reviewer@example.org`, an active System super-admin
with access to Review Library (2), and uses the existing normal secure staff
cookie. Synthetic Entra GUIDs satisfy configuration contracts without contacting
Microsoft. Deterministic Polaris providers and local email capture are required.
Readiness receives protected synthetic Polaris settings. Authorization,
antiforgery, business rules and rowversion checks remain the application's own.
The production Entra implementation is unchanged.

This image is acceptance tooling only. Deployment topology and operational
readiness remain the separate test deployment pipeline's responsibility.
