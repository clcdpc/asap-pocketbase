# ASAP PocketBase -> .NET Porting Documentation Pack

**Status:** Slices 0-6 accepted; post-Slice-6 correction complete with successful exact-SHA CI; Slice 7 not started. Beginning with Slice 7, Astra High is the default thin supervisor with PR-backed state and bounded Astra Max escalation; all objective acceptance/release gates retained.
**Prepared:** 2026-09-12  
**Repository:** `clcdpc/asap-pocketbase`  
**Source snapshot:** `150b30b776565194260cc327eeeffdfb46475e81` (`Improve staff login diagnostics and library-aware email formatting`, 2026-09-09)  
**Target:** .NET 10 / ASP.NET Core 10 / SQL Server 2022 / IIS

## Purpose

This pack is the authoritative implementation baseline for replacing the PocketBase/Goja backend of ASAP with a C#/.NET application while preserving the working patron and staff experience unless the port has a concrete reason to change it.

The design was developed by walking the architecture, data model, authentication, Polaris/Postmark integrations, migration, deployment, testing, CI, operational model, and cutover behavior decision-by-decision. When this pack conflicts with an older PocketBase document, this pack governs the .NET port.

This revision closes four focused cutover/outbox findings: retired production PocketBase is forensic-only and cannot be started as-is after .NET accepts writes; expired email leases use a concrete conservative timeout/reclaim/fencing contract; authorization-sensitive staff mail persists the applicable ordinary-versus-weekly address rule; and post-merge/pre-cutover emergency PocketBase fixes have an explicit temporary-branch, immediate .NET propagation, rehearsal, and final-tag procedure. The pinned baseline and settled architecture remain unchanged, and no generalized messaging or migration framework is added.

Staged PRs began with Slice 6. Beginning with Slice 7, [document 10](10-CODEX-MULTI-MODEL-TASK.md) makes one GPT-6 Astra High thin supervisor preferred/default. It dispatches bounded Luna Max implementation and independent Terra High review, verifies compact receipts, routes fixes, performs short acceptance, integrates and waits for exact-milestone CI under a strict context firewall. One mutable state comment and compact append-only events on the slice PR support restart/recovery; actual Git/PR/review/Actions state outranks comments. Terra uses durable review threads/findings. Astra Max is bounded material supervisor/contract escalation only, returning control to High after its ruling. Manual/direct fallback, temporary PR topology, full integrated validation, review independence and the three-cycle stop rule remain binding. Automatic Codex GitHub review is not a normal temporary PR gate. PR #264 remains the final draft port PR into `main`; Sol remains exceptional specialist advice.

Slices 0-6 retain their actual historical policies and accepted evidence. Historical Slice 6 milestone: `7ba59421176ede99ba48488be6bc81010e60f65c`. Correction PR #266 merged reviewed SHA `04f538ef1a04be33b31d5dbdc3a5c1751b163ee4`; current authorized product/integration baseline before this policy edit is `d607723e846f633ebe206163f6c232dd79566d6f`, exact-SHA CI `34988112346` succeeded. This documentation-only refinement launches no supervisor or workers and creates no new accepted product milestone. Slice 7 must branch from the final docs-policy head and successful CI recorded in PR #264, separately recording its prior product/technical review base at bootstrap. See [PORT-STATUS.md](../implementation/PORT-STATUS.md). Final whole-application review and release/cutover gates remain unchanged.

The temporary `FileEmailSender` decision in `../implementation/temporary-email-transport.md` remains limited to the final provider boundary. Durable SQL outbox, authorization-sensitive recipient checks, recipient-domain safety, lease/fencing/idempotency/retry behavior, and real Rest 3-compatible cancellable `Clc.Postmark.Api` release/rehearsal gates remain binding; local files do not simulate provider/webhook success. The prior R1-R7 and final F1-F3 decisions remain unchanged, including OutstandingTimeout suggestion-age semantics, particular tracked hold identity for terminal fulfillment, and exhaustive retained legacy placed-hold evidence/BIB protection.

The prior hold-recovery, identity/session/lifecycle, Organization/claim, AdditionalCopy, outbox, queue-fairness, deployment/Hangfire, migration-bootstrap, configuration-inheritance, and forced-summary decisions remain binding. `14-REMEDIATION-AUDIT.md` records this four-finding remediation, the execution-model refinement, and the prior handoff cleanup separately from the historical F1-F3/R1-R7 audits and their archive/review hashes. Required regression/release gates remain in `06-TESTING-CI.md` sections 10.1-10.2 and `08-RELEASE-VALIDATION-NOTES.md` sections 2.1-2.2; recipient-domain safety adds testing section 4.1 and release section 6.

## Governing principles

1. Preserve current behavior and internal API contracts by default; change them only for a concrete benefit.
2. Keep the architecture simple. Do not add layers, frameworks, queues, repositories, mediators, distributed infrastructure, or generic migration machinery without a demonstrated need.
3. Treat migration correctness, authorization, data integrity, concurrency, and recovery behavior as blocking concerns.
4. Deliver one coordinated port through `codex/csharp-port` and final draft PR #264. Use optional temporary slice/work-package PRs under document 10; every accepted slice leaves the integration branch runnable and green.
5. Keep the existing frontend architecture and UX as the baseline. The backend/platform port is not a general redesign.
6. Prefer explicit, inspectable operations over hidden automation, especially for SQL deployment and the one-time production migration.
7. Store timestamps in UTC; interpret business dates/schedules in `America/New_York`.
8. Preserve all current StaffUser preferences through migration/profile APIs; user preferences are not settings overrides.
9. Treat outbox business idempotency and the final-usable-super-admin invariant as SQL-backed concurrency contracts, not check-then-write conventions.
10. Freeze both SQL-bound runtime fallback values and external operational/job configuration before migration; preserve the authoritative schedule, processing-limit, workflow-ordering, and identifier-processing contracts.
11. Record useful non-blocking modernization/hardening work for later instead of expanding the initial port indefinitely.

## Documents

| File | Purpose |
| --- | --- |
| `01-PORTING-SPEC.md` | Target architecture, runtime, auth, integrations, workflows, configuration, and behavior. |
| `02-IMPLEMENTATION-PLAN.md` | Ordered vertical slices, acceptance criteria, review gates, and branch/PR workflow. |
| `03-DATABASE-DESIGN.md` | SQL schema design, table catalog, constraints, concurrency, and ownership rules. |
| `04-MIGRATION-CUTOVER.md` | PocketBase export/import design, transformation rules, reconciliation, rehearsal, and production cutover. |
| `05-DEPLOYMENT-OPERATIONS.md` | IIS/SQL topology, configuration, deployment script contract, health, logging, monitoring, and rollback boundaries. |
| `06-TESTING-CI.md` | .NET, real-SQL, frontend, Playwright, accessibility, CI, and release testing strategy. |
| `07-API-FRONTEND-COMPATIBILITY.md` | Frontend preservation rules, API compatibility policy, auth behavior, CSP, vendored assets, and intentional changes. |
| `08-RELEASE-VALIDATION-NOTES.md` | Live Polaris/release-validation decisions that should remain separate from ordinary PR testing. |
| `09-DEFERRED-FOLLOWUPS.md` | Explicitly deferred hardening, modernization, and operational improvements. |
| `10-CODEX-MULTI-MODEL-TASK.md` | Thin autonomous supervision, bounded Luna/Terra workers, context firewall, staged PRs, review modes, stop rules and exact-SHA acceptance; manual fallback. |
| `11-CURRENT-POCKETBASE-REFERENCE.md` | Current-system map and behavior anchors for implementers and reviewers. |
| `12-DECISION-REGISTER.md` | Compact register of the binding architectural decisions and superseded choices. |
| `13-SETTINGS-SCOPE-INVENTORY.md` | Normative field-by-field settings scope/storage/inheritance/reset/migration contract for the refactored domain-specific configuration model. |
| `examples/Config.example.json` | Documentation-pack representation of the test-host application template, automatically checked against the operational bootstrap template. |
| `examples/EffectiveLegacyRuntimeConfig.example.json` | Safe shape/example for the **system/global SQL-bound values that require runtime fallback resolution**; library-scoped configuration remains in the domain exports. |
| `examples/EffectiveLegacyOperationalConfig.example.json` | Safe shape/example for the frozen legacy cron and queue-processing-limit snapshot used to prove external operational-config parity. |
| `14-REMEDIATION-AUDIT.md` | Finding-by-finding closure evidence and mechanical-validation record for this revision. |
| `PACK-MANIFEST.txt` | SHA-256 inventory of the pack payload for handoff/integrity checks. |

The configuration examples contain placeholders and agreed **configuration shapes**, not deployable credentials. Exact option-class/key names may be refined during implementation as long as the documented ownership boundaries and behavior remain unchanged. The test-host operational application template, including durable Entra bootstrap identity fields, is readable JSON embedded in `scripts/deployment/Initialize-AsapTestHost.ps1` so the copied bootstrap is self-contained. `examples/Config.example.json` keeps this documentation pack independently usable; automated tests compare its complete structure and values with bootstrap-generated `application.json` after normalizing only the host-derived paths. The bootstrap never reads the example file at runtime, and operators do not copy it to the host.

The embedded test-host template retains `Environment.IsNonProduction=true` and an explicit recipient-domain entry. A parent domain never permits its subdomains implicitly. The full fail-closed recipient-domain contract is in `01-PORTING-SPEC.md` section 14.

## Review corrections incorporated

This revision incorporates all prior post-pack corrections plus the email-identity decision: Entra authenticates and enforces allowed tenants, while ASAP locates and authorizes StaffUser by unique normalized email; `tid`/`oid` are last-observed metadata only, and migration uses validated source staff emails without an identity map. The existing deployment, outbox, workflow, security, and settings-scope contracts remain binding.

The accumulated review corrections also remain binding: preserve StaffUser preferences, lifecycle locking and scope cleanup; keep recipient semantics explicit; revalidate authorization-sensitive staff mail against the recipient ID, normalized authentication email, current scope/participation, and exact destination; and require at least one usable email-authenticated system super-admin before enabling the migrated application.

## Source-of-truth hierarchy during implementation

1. This porting pack. For settings scope/storage/inheritance/reset semantics, `13-SETTINGS-SCOPE-INVENTORY.md` is the most specific controlling document.
2. Current behavior at the pinned PocketBase source snapshot.
3. Current automated tests and explicit business behavior encoded in the PocketBase application.
4. Older repository architecture/design documents, only where they do not conflict with this pack.

If `main` moves after the pinned SHA because of an urgent PocketBase production fix, the responsible bounded worker must compare that change and deliberately bring the relevant behavior into the port before continuing, escalating material contract questions for a bounded Astra ruling under document 10.

## What success means

The first .NET production release is successful when the complete current business application is running on the new IIS server and SQL Server database; patrons and staff retain their expected workflows; Entra replaces staff password authentication; Polaris and Postmark use the agreed .NET integrations; background work runs through Hangfire; the PocketBase dataset has been reconciled with no unexplained differences; deployment/health/monitoring are operational; and the exact production artifact has already passed the permanent nonproduction rehearsal.
