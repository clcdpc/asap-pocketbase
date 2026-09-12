# ASAP PocketBase -> .NET Porting Documentation Pack

**Status:** Final closure-review Findings #1-#3 resolved in the documentation contract; prior clean/settled architecture retained; implementation/release tests remain required
**Prepared:** 2026-09-12  
**Repository:** `clcdpc/asap-pocketbase`  
**Source snapshot:** `150b30b776565194260cc327eeeffdfb46475e81` (`Improve staff login diagnostics and library-aware email formatting`, 2026-09-09)  
**Target:** .NET 10 / ASP.NET Core 10 / SQL Server 2022 / IIS

## Purpose

This pack is the authoritative implementation baseline for replacing the PocketBase/Goja backend of ASAP with a C#/.NET application while preserving the working patron and staff experience unless the port has a concrete reason to change it.

The design was developed by walking the architecture, data model, authentication, Polaris/Postmark integrations, migration, deployment, testing, CI, operational model, and cutover behavior decision-by-decision. When this pack conflicts with an older PocketBase document, this pack governs the .NET port.

This revision resolves only the three confirmed findings in the latest closure review of the exact input archive: OutstandingTimeout now explicitly governs unreviewed suggestions by creation age; terminal fulfillment requires the particular tracked final Polaris hold identity while positive checkout remains title-level; and migration derives permanent BIB protection from every dependable normalized placement-evidence class, including all five terminal reasons and status_changed adoption transitions. Known/null historical BIB protection remains separate from runtime hold identity. No operation/provider success/ID is fabricated.

The prior hold-recovery, identity/session/lifecycle, Organization/claim, AdditionalCopy, outbox, queue-fairness, deployment/Hangfire, migration-bootstrap, configuration-inheritance, and forced-summary decisions remain binding and were not intentionally redesigned. `14-REMEDIATION-AUDIT.md` separates this final F1-F3 trace from the retained prior R1-R7 record. The required new regression and release gates are `06-TESTING-CI.md` section 10.2 and `08-RELEASE-VALIDATION-NOTES.md` section 2.2.

## Governing principles

1. Preserve current behavior and internal API contracts by default; change them only for a concrete benefit.
2. Keep the architecture simple. Do not add layers, frameworks, queues, repositories, mediators, distributed infrastructure, or generic migration machinery without a demonstrated need.
3. Treat migration correctness, authorization, data integrity, concurrency, and recovery behavior as blocking concerns.
4. Implement one large port branch/PR, but work in complete vertical slices. Every slice must leave the .NET branch runnable and green.
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
| `10-CODEX-MULTI-MODEL-TASK.md` | Ready-to-use orchestration task for implementing the port using Astra/Luna/Terra roles. |
| `11-CURRENT-POCKETBASE-REFERENCE.md` | Current-system map and behavior anchors for implementers and reviewers. |
| `12-DECISION-REGISTER.md` | Compact register of the binding architectural decisions and superseded choices. |
| `13-SETTINGS-SCOPE-INVENTORY.md` | Normative field-by-field settings scope/storage/inheritance/reset/migration contract for the refactored domain-specific configuration model. |
| `examples/Config.example.json` | Safe shape/example for external environment configuration, including durable Entra bootstrap identity fields. |
| `examples/StaffIdentityMap.example.json` | Safe shape/example for the explicit PocketBase-staff -> Entra tenant/object-ID migration mapping. |
| `examples/EffectiveLegacyRuntimeConfig.example.json` | Safe shape/example for the **system/global SQL-bound values that require runtime fallback resolution**; library-scoped configuration remains in the domain exports. |
| `examples/EffectiveLegacyOperationalConfig.example.json` | Safe shape/example for the frozen legacy cron and queue-processing-limit snapshot used to prove external operational-config parity. |
| `14-REMEDIATION-AUDIT.md` | Finding-by-finding closure evidence and mechanical-validation record for this revision. |
| `PACK-MANIFEST.txt` | SHA-256 inventory of the pack payload for handoff/integrity checks. |

The configuration examples contain placeholders and agreed **configuration shapes**, not deployable credentials. Exact option-class/key names may be refined during implementation as long as the documented ownership boundaries and behavior remain unchanged.

## Review corrections incorporated

This revision incorporates all prior post-pack corrections: durable Entra authorization by (`tid`,`oid`) while retaining human-readable UPN/display/email fields; explicit active-staff Entra identity mapping for migration; disposable production-preflight SQL isolation; database-change classification covering DACPAC hashes independently of SchemaVersion and dependency-owned DDL; recoverable lease-based email outbox processing with explicit at-least-once semantics; DB-enforced hold-placement operation concurrency; self-contained `win-x64` migration tooling for the old server; exclusion/removal of the unrelated carousel-example subtree at the pinned baseline; separate production/nonproduction runtime service identities; least-privilege runtime SQL access with schema deployment/backup/Hangfire DDL reserved for the deployment operator; Data Protection encryption for reusable Polaris/Postmark secrets stored in SQL with separate environment key rings and portable X.509 key-encryption certificates for replacement-host recovery; and a clean comment-prefixed integrity manifest. It also replaces the catch-all SQL `Settings` table with domain-specific `SystemSettings`, `PolarisSettings`, `WorkflowSettings`, `PatronSettings`, and `EmailSettings` plus relational set/configuration tables, with the complete settings-scope inventory normative in `13-SETTINGS-SCOPE-INVENTORY.md`.

The earlier accumulated review corrections also remain binding: preserve all current StaffUser preferences end-to-end; enforce non-null `EmailOutbox.BusinessKey` uniqueness in SQL with deterministic keys and duplicate-race-as-success behavior; freeze/reconcile system/global SQL-bound runtime fallbacks; preserve external operational configuration separately; serialize final-usable-super-admin removal with a transaction-owned SQL Server application lock and a defined `409 Conflict` invariant response; separate StaffUser lifecycle from Organization participation so inactive libraries retain staff relationships while participation-dependent authorization requires both states active plus the current binding/tenant predicate; keep the hourly workflow as one ordered orchestrator; consolidate the two legacy ISBN processors into one five-minute canonical identifier processor with explicit provider-result classification, five-attempt retry/recovery semantics, exhaustive legacy-status migration where `found` requires BIB state, and atomic invalidation of old BIB/result/tag authority on permitted pre-placement identifier edits, with post-placement/closed protection; and make `NotificationEmail` versus `WeeklyActionSummaryEmail` recipient semantics, deliberate fallback normalization, admin-clear/no-Entra-refill behavior, migration precedence, and recipient-delta reporting explicit. The final authorization-scope hardening is equally binding: scope-contracting StaffUser role/library changes atomically clean out-of-scope active auto-claim rules plus open TitleRequest and AdditionalCopy claims; patron bearer authorization rechecks active library participation on every request with race-safe final session issuance; participation-dependent automation and ordinary library summaries skip inactive libraries using the Organization-row serialization boundary; already-committed immutable business-event mail may drain while authorization-sensitive staff mail revalidates current binding/allowed tenant/scope/address and suppresses when stale; weekly summaries are scoped to each recipient's authorized active library (or active consortium for super-admins) instead of reusing the PocketBase global payload; and migration cannot enable the app until at least one currently usable, validly Entra-bound system-organization super-admin exists, using explicit migration-time bootstrap provisioning/promotion if necessary.

## Source-of-truth hierarchy during implementation

1. This porting pack. For settings scope/storage/inheritance/reset semantics, `13-SETTINGS-SCOPE-INVENTORY.md` is the most specific controlling document.
2. Current behavior at the pinned PocketBase source snapshot.
3. Current automated tests and explicit business behavior encoded in the PocketBase application.
4. Older repository architecture/design documents, only where they do not conflict with this pack.

If `main` moves after the pinned SHA because of an urgent PocketBase production fix, the orchestrator must compare that change and deliberately bring the relevant behavior into the port branch before continuing.

## What success means

The first .NET production release is successful when the complete current business application is running on the new IIS server and SQL Server database; patrons and staff retain their expected workflows; Entra replaces staff password authentication; Polaris and Postmark use the agreed .NET integrations; background work runs through Hangfire; the PocketBase dataset has been reconciled with no unexplained differences; deployment/health/monitoring are operational; and the exact production artifact has already passed the permanent nonproduction rehearsal.
