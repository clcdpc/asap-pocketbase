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

# Feature suite: 21 scenarios, including approved inherited Settings reset.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites

# Select an exact application revision instead of a PR.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Commit HEAD -Suite smoke -AllowReviewWrites

# Optional absolute output root; paths containing spaces are supported.
pwsh ./scripts/review/Invoke-AsapReview.ps1 -Pr 410 -Suite pr410 -AllowReviewWrites -ArtifactsRoot 'D:\ASAP Acceptance'
```

`-Pr` fetches the PR's exact HEAD from `origin`, independently of the current
tooling checkout. `-Commit` selects a Git commit; the two selectors are exclusive.
The default suite is `smoke`. Omitting `-AllowReviewWrites` enables read-only
mode; `-ReadOnly` makes it explicit. `-Rebuild` forces a fresh image build.

Reports, unmasked synthetic screenshots, state/geometry evidence, failure traces and
sanitized logs appear together in `$HOME/ASAP-Acceptance/<timestamp-run-id>/`
by default (for example, `C:\Users\<username>\ASAP-Acceptance\<timestamp-run-id>\`).
The default uses the current user's profile, independently of the checkout or
working directory. `-ArtifactsRoot <absolute-path>` selects a different parent;
an invalid or unwritable destination blocks the run without falling back.
The launcher prints the full run directory before startup, then the directory,
`report.md` path, automated result and scenario counts at completion. It removes
the container after success, failure or interruption. A forced termination of the launcher process itself
may require `wslc remove --force <printed-container-name>`.

Default artifacts survive worktree deletion. Historical runs remain until you
explicitly delete them; earlier artifacts under `.artifacts/acceptance/` in
existing worktrees are not migrated automatically. Keep any custom output root
outside repositories and worktrees for the same persistence. The disposable
image-build context remains under `.review/build` in the tooling checkout.

Open the newest default run in File Explorer (optional):

```powershell
$root = Join-Path $HOME 'ASAP-Acceptance'
$latest = Get-ChildItem -LiteralPath $root -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($latest) { Invoke-Item $latest.FullName }
```

- Exit 0: selected automated checks succeeded; read-only mutations remain NOT RUN.
- Exit 1: an assertion or browser execution failed.
- Exit 2: environment, startup, configuration, authentication or cleanup blocker.

The two smoke tests exercise authorized SQL-backed fixture review and, when
explicitly authorized, a real UI closure with one POST and a changed rowversion.
The feature suite retains all twenty original scenarios and adds an approved
Review Library reset with authoritative before/after state and preserved custom
rejection-template content/rowversion. Read-only feature runs execute eleven
scenarios and skip all ten mutation scenarios. Native 200% confirmation evidence
includes complete viewport and dialog captures plus DOM/CDP geometry; the pinned
full-page capture's reduced CSS-width crop is covered by a plumbing regression.
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
