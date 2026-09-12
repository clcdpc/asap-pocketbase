# ASAP .NET Port Execution Record

The implementation replaces PocketBase with .NET 10 / ASP.NET Core 10, SQL
Server 2022, and IIS under the authoritative `docs/dotnet-port/` pack. This
single draft PR will deliver the complete port as reviewed vertical
slices. It is not eligible for merge or production deployment yet.

## Baseline

- Recorded: 2026-09-12.
- Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
- Fetched `origin/main`: `150b30b776565194260cc327eeeffdfb46475e81`.
- Existing implementation branch: `codex/csharp-port`.
- Single draft PR: https://github.com/clcdpc/asap-pocketbase/pull/264
- Starting branch commit: `c7637a351dad711484c4a0df9613050a1e6b2636`.
- Starting working tree: clean.
- Changes between pin and starting branch: repository instructions and port
  documentation only. There are no intervening `main` fixes to propagate.
- Exact deployed PocketBase production commit: **unverified**. The repository
  has no GitHub deployment records. Do not infer deployment from `main`.
- Final historical tag convention: `pocketbase-final-YYYYMMDD`, created or
  verified only after successful .NET cutover, at the exact frozen production
  commit. No final tag has been created.
- Excluded baseline subtree: `clc-carousel-manual-import-example/`; unrelated
  to ASAP behavior, dependencies, migration, and archival requirements. Remove
  it in Slice 0.
- All 19 payload SHA-256 values match `docs/dotnet-port/PACK-MANIFEST.txt`.
- Astra has read root `AGENTS.md`, the pack README, all numbered documents,
  all four examples, and the manifest before implementation changes.

Ordinary PocketBase feature work is frozen for this port. Fetch and inspect
`main` before subsequent milestones; immediately propagate any urgent source
behavior change and its migration implications. Keep tracking the actual
deployed source independently. After the .NET merge, emergency PocketBase
fixes use a temporary branch from the deployed source, immediate equivalent
.NET changes, and a replacement tagged artifact with repeated rehearsal.

## Execution And Evidence

Astra owns packets, integration, gates, milestone commits, and this PR. Each
slice uses a fresh Sol High/XHigh implementer and a fresh Terra High reviewer.
Keep that Terra context through at least two full-slice review passes; Sol
fixes confirmed findings. Tests and independent review must pass before the
milestone commit or next slice. Blocking findings cannot be waived by a pass
cap. Migration code and reconciliation grow with every data-owning slice.

| Slice | Scope | Status |
| --- | --- | --- |
| 0 | Branch, skeleton, engineering baseline | Complete: reviewed milestone `0096777`, remote CI passed |
| 1 | Patron login and submission | Packet prepared; awaiting required Postmark source/compatible build |
| 2 | Staff Entra and core request workflow | Not started |
| 3 | Additional-copy workflow | Not started |
| 4 | Administration and configuration | Not started |
| 5 | Background workflows and complete email operations | Not started |
| 6 | Analytics | Not started |
| 7 | Migration hardening and legacy links | Not started |
| 8 | Deployment, health, monitoring, release artifacts | Not started |
| 9 | CI, browser, accessibility, release integration | Not started |
| 10 | Explicit synthetic seed/reset tooling | Not started |
| 11 | Legacy removal, canonical docs, final review | Not started |

Slice 1 preparation includes pinned behavior/schema notes and a draft packet.
The CLC package probe found a real Postmark/Rest binary incompatibility with
the current Polaris prerelease. See `clc-package-probe.md`; the required fresh
Astra consultation confirms the focused actual-package fix. Source access or a
corrected maintained package has been requested; no custom transport or weaker
timeout is approved. Slice 0 is independent of that prerequisite.

See `slice-00-evidence.md` for actual build, SQL, startup and publish checks.
Milestone `00967778001e7ec8198ab4498d0fbd15ded4d984` passed the complete
[remote CI run](https://github.com/clcdpc/asap-pocketbase/actions/runs/34703502557).
No business/provider/entity-migration/browser/rehearsal/release gate is claimed
passed by the documentation pack or the engineering baseline.
Slice evidence belongs beside each focused packet. Final completion requires
the specified whole-application review, complete operational artifacts, exact
tagged-artifact permanent-nonproduction rehearsal, and release-readiness gates.

Local prerequisites verified: .NET SDK 10.0.303; SQL Server 2022 Developer
Edition (64-bit), version 16.0.1200.5, default local instance with working
Windows authentication. These checks are not application acceptance tests.

## Release Boundaries

Permanent nonproduction remains PocketBase until the complete reviewed port
is merged and tagged. Production-hostname preflight uses only a disposable
SQL database. The final migration target stays fresh and stopped until import,
usable-super-admin provisioning/validation, and reconciliation succeed.

After .NET accepts production writes, it is authoritative. Retired PocketBase
is forensic-only and cannot start as-is. Any necessary execution uses an
isolated copy with outbound Polaris/email blocked and recurring jobs disabled.
