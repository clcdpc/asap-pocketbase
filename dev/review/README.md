# ASAP acceptance with WSLC

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1,d1,q1
```

Run directly from PowerShell 7 in a current local tooling checkout. The command
resolves the application once, builds one invocation-owned acceptance image, then runs
exactly the requested suites once each in caller order. Each suite starts its own
disposable container and fresh SQL Server 2022 database, authenticates a synthetic
staff user locally, and runs Chromium/Playwright automatically. Containers are
removed between suites; SQL state is never shared. The image is removed after
the invocation's final container teardown. No configuration file or
browser interaction is needed.

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
# Full synthetic smoke, including a real UI closure.
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite smoke

# Native array binding, directly in PowerShell 7 (both forms preserve order).
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite s1,d1,q1
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite @('s1', 'd1', 'q1')

# Feature suite: 21 scenarios, including approved inherited Settings reset.
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite pr410

# Select an exact application revision instead of a PR.
.\scripts\review\Invoke-AsapReview.ps1 -Commit HEAD -Suite smoke

# Explicit restriction: closure is NOT RUN; acceptance is incomplete (exit 2).
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite smoke -ReadOnly

# Optional absolute output root; paths containing spaces are supported.
.\scripts\review\Invoke-AsapReview.ps1 -Pr 415 -Suite pr410 -ArtifactsRoot 'D:\ASAP Acceptance'
```

`-Pr` fetches the PR's exact HEAD from `origin`, independently of the current
tooling checkout. `-Commit` selects a Git commit; the two selectors are exclusive.
Omitting both selectors tests resolved `HEAD`. The default suite is `smoke`;
no smoke or extra repetitions are added to an explicit selection. Add `smoke`
explicitly when useful, for example `-Suite smoke,s1,d1,q1`. `-Rebuild` forces
one shared build with `--no-cache`, rather than one build per suite. Normal builds
reuse BuildKit layers, not a retained finished-image tag. Each tag includes the
application SHA, tooling digest and a unique invocation ID so concurrent runs
cannot remove each other's image. Full tooling SHA/dirty state remain in metadata.

All suites are validated against `tests/acceptance/suites/registry.json` in the
local checkout before resolution or execution. Empty, unsafe, duplicate
(case-insensitive) and unknown names are rejected with available suites and an
update hint. The PR supplies application source only; the launcher never fetches
PR tooling to satisfy a missing suite. Discover suites with:

```powershell
Get-Content ./tests/acceptance/suites/registry.json
```

The comma-array syntax above requires direct invocation in PowerShell 7. It is
not a CSV argument and is not portable across arbitrary shell or `pwsh -File`
boundaries. When launching PowerShell from another shell, put the native array
inside a properly quoted `pwsh -Command` script.

Complete functional checks, including allowed synthetic writes, run by default.
`-ReadOnly` explicitly prevents all scenario mutations and records required
skips as NOT RUN; such a partial suite returns exit 2. Entirely read-only suites
can still pass under that restriction. `-AllowReviewWrites` remains a deprecated
compatibility option with no extra permission and conflicts with `-ReadOnly`.

Reports, unmasked synthetic screenshots, state/geometry evidence, failure traces and
sanitized logs appear together in flat directories under `$HOME/ASAP-Acceptance/`
by default, for example `pr-415_s1_2026-11-01_13-30-00` or
`commit-454d8676e666_smoke_2026-11-01_13-30-00`. Each suite captures its start
using the host's local date and 24-hour time. Atomic allocation never overwrites
evidence: collisions get `-02`, `-03`, etc. Full SHAs, digest, internal random
run/container identity, UTC timestamps and host-local timestamps with UTC offset
are retained in `results.json`, including across daylight-saving transitions.
The default uses the current user's profile, independently of the checkout or
working directory. `-ArtifactsRoot <absolute-path>` selects a different parent;
an invalid or unwritable destination blocks the run without falling back.
The launcher prints each run directory before its container starts, then a
consolidated suite/status/duration table, artifact paths, counts and overall
result. `results.json`, `report.md`, screenshots and `diagnostics/` belong to
that suite. `container.log` is collected even after early container failures.
Shared `invocation-<id>.log`/`.json` and, when built, `build-<id>.log` files are
directly under the artifact root, with no batch/PR subdirectories. Build logs
are also copied into suites that start. A failed shared prerequisite/build
marks all unstarted suites NOT RUN and prints its diagnostic path without
inventing suite execution evidence. Ordinary suite failures/blockers continue
to the next suite; an unconfirmed container cleanup stops later starts.
The launcher removes each container after success, failure or interruption, then
removes only its own image tag after all suite teardown, including failed or
interrupted builds. Image removal is never forced. Cleanup failures make the
invocation BLOCKED (exit 2), retain suite outcomes and evidence, and print the exact
recovery command. `invocation-<id>.json` records `imageCleanup` separately from
suite results. A forced termination of the launcher process itself may require
`wslc remove --force <printed-container-name>` followed by
`wslc image remove --no-prune <printed-image-tag>`.

This does not prune shared build cache, base images, historical acceptance tags,
or other workloads. Shared layers remain while referenced. BuildKit can retain
unreferenced build records, so removing a tag does not guarantee all its layers
are freed. Previously accumulated cache/images require separate deliberate
maintenance; reclaiming free space inside WSLC does not automatically compact
the Windows VHDX. The launcher never shuts down WSL or compacts its disks.

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

- Exit 0: every required check in every requested suite passed.
- Exit 1: functional checks failed, with no blocked or incomplete suites.
- Exit 2: blocked/incomplete execution, including skipped required checks or NOT RUN suites.

The table distinguishes PASS, FAIL, BLOCKED (started but incomplete) and NOT RUN
(never started). Mixed FAIL/BLOCKED outcomes preserve both and return exit 2.

The two smoke tests exercise authorized SQL-backed fixture review and a real UI
closure with one POST and a changed rowversion in the disposable database.
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
Image tags include application SHA, tooling content digest and invocation ID;
working changes cannot silently reuse an older image. Build layers remain reusable
after the invocation-owned tag is removed.

The Bash entrypoint generates local secrets and configuration, starts SQL on
localhost, waits for an authenticated connection, runs the existing initializer,
imports Data Protection material, starts ASAP at `https://localhost:8080`, waits
for real readiness, starts Xvfb, and runs the selected suite with headed Chromium.
Child failures and signals produce blockers; shutdown terminates child process
groups and exports diagnostics. SQL data, certificates, cookies and configuration
disappear with the container. Only the artifact directory is mounted from Windows;
no ports are published.

Normal runs allow unrestricted application mutations inside the verified disposable
app/database; scenario write metadata is for selection/reporting. The runner has
no per-endpoint, record, scope or request grants. Normal SQL authorization,
antiforgery, validation and rowversion checks still apply. Explicit read-only mode
skips required write scenarios and blocks accidental browser mutations.

No host repository or persistent data volume is mounted. No published ports
limits inbound exposure; it does **not** establish outbound-network isolation.
Browser routes block external requests, Polaris providers are deterministic with
synthetic `.invalid` credentials, and email is captured locally. Other container
processes retain outbound capability. This trusted-code acceptance runner is not
a firewall or hostile-code sandbox. Never point it at shared data.

Local authentication seeds `reviewer@example.org`, an active System super-admin
with access to Review Library (2), and uses the existing normal secure staff
cookie. Synthetic Entra GUIDs satisfy configuration contracts without contacting
Microsoft. Deterministic Polaris providers and local email capture are required.
Readiness receives protected synthetic Polaris settings. Authorization,
antiforgery, business rules and rowversion checks remain the application's own.
The production Entra implementation is unchanged.

This image is acceptance tooling only. Deployment topology and operational
readiness remain the separate test deployment pipeline's responsibility.
