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
| 1 | Patron login and submission | Milestone `c1b8655` plus reviewed CI correction; 120 local tests passed, Linux rerun pending |
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

Slice 1 uses its existing packet and pinned behavior/schema notes. On
2026-09-12 the user explicitly authorized a minimal `FileEmailSender` at only
the final provider transport boundary. This supersedes the original Slice 1
Postmark dependency gate, not any SQL outbox or application behavior contract.
See `temporary-email-transport.md` and the retained compatibility evidence in
`clc-package-probe.md`. Continue subsequent slices without waiting for Postmark;
the real provider remains a release/rehearsal blocker.

Slice 1's local handoff passed a zero-warning/error Release build, 104/104
.NET tests with none skipped, legacy tests, real-SQL/Kestrel/Playwright patron
journeys and published-artifact checks. Astra independently reran the full
.NET suite and the published self-contained migration executable's positive/
negative import and reconciliation checks. Fresh Terra High Pass 1 has reported
findings; Sol's fixes passed 114/114 tests and corrected native-artifact checks.
The same reviewer's full Pass 2 resolved those findings and found one remaining
request-specific CSP issue. That fix now passes 118/118 tests, including an
independent Astra rerun, and all fourteen pinned-source/real-HTTP CSP cases
against a new verified publish. The same Terra completed full Pass 3 with no
substantive finding; all six findings are resolved. See `slice-01-review.md`.
This coherent milestone closes Slice 1's local acceptance/review gate. Remote
CI for the new milestone must pass before Slice 2 dispatch; earlier remote CI
does not certify the Slice 1 implementation.

Slice 1 milestone: `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44`, pushed to the
same draft PR. Its [remote CI run](https://github.com/clcdpc/asap-pocketbase/actions/runs/34730692517)
failed with 77 passing and 41 failing tests, none skipped. Migration certificate
lookup opened an unsupported Unix machine store; patron application setup also
rejected the SQL-authenticated Linux CI database. Sol's narrow corrections and
security-boundary regressions pass 120/120 local tests, independently rerun by
Astra, and fresh native/browser artifact checks. The same Terra's full closure
review is clean. The correction is cleared for commit/push and a new Linux run;
Slice 2 remains gated on that actual result. Future-slice preparation documents
were excluded from the Slice 1 commit.

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

The temporary file sender is not production transport. Release/rehearsal cannot
pass until a Rest 3-compatible `Clc.Postmark.Api` supports cancellable async
sending, replaces `FileEmailSender`, and passes provider integration/webhook,
transport-specific and release-validation tests. No simulated webhook or local
file may stand in for those gates. The port is not production complete while
this work remains outstanding.

Permanent nonproduction remains PocketBase until the complete reviewed port
is merged and tagged. Production-hostname preflight uses only a disposable
SQL database. The final migration target stays fresh and stopped until import,
usable-super-admin provisioning/validation, and reconciliation succeed.

After .NET accepts production writes, it is authoritative. Retired PocketBase
is forensic-only and cannot start as-is. Any necessary execution uses an
isolated copy with outbound Polaris/email blocked and recurring jobs disabled.
