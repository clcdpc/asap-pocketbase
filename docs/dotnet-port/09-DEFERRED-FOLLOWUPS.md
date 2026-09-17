# Deferred Follow-ups and Hardening

These items are intentionally outside the initial port unless implementation exposes a concrete blocker. Recording them here prevents them from being forgotten without expanding the rewrite.

## Identity / security

- Split production and nonproduction into separate Entra app registrations.
- Evaluate gMSA for the already-separated production/nonproduction runtime identities once deployment/operations are stable.
- Consider replacing the built-in Data Protection approach with an external enterprise secret-management system only if later operational requirements justify the added dependency.
- Consider a stricter nonproduction patron allowlist if the public test environment needs additional protection.

## Email

- Split production/nonproduction into separate Postmark server/token.
- Add more aggressive retention/archival rules for delivery metadata if database growth warrants it.

## Deployment / operations

- Build production/promotion CI/CD for automatic main -> permanent nonproduction and/or production. The reduced Slice 8 test-IIS tag/manual workflow is the limited current deployment path.
- Automate housekeeping of old application directory backups and deployment-created SQL backups once retention policy is known.
- Consider centralized log aggregation if local 30-day files become insufficient.
- Revisit multi-instance/high-availability concerns only if ASAP moves beyond one IIS instance.

## Frontend

- Upgrade Bootstrap, Font Awesome, Grid.js independently of the port.
- Re-evaluate whether Grid.js still earns its complexity after .NET stabilization.
- Introduce server-side queue paging/search only if measured real datasets justify it.
- Broader UI/UX redesign/modernization is a separate project.

## Database / reporting

- Add explicit reporting views/contracts if SSRS or another system needs durable direct access.
- Re-evaluate heavy Dapper SQL for stored-procedure/view extraction only where independent reuse/operations justify it.
- Add automated data-retention policies only after business retention requirements are established.

## Migration cleanup

- Remove `LegacyPocketBaseMapping` only after the legacy-link/reference window has passed and stakeholders no longer need old IDs.
- Remove the runnable old PocketBase deployment after the agreed reference period; retain final tag/backup as required.

## Release validation

- Improve disposable Polaris fixture provisioning/cleanup if nightly refresh is not reliable enough.
- Consider Windows-specific CI runners only for tests that genuinely require IIS/Windows behavior.

## Developer/test tooling

- End-of-port synthetic/demo seed/reset utility is required, but deeper scenario-generation tooling can evolve after cutover.

## Required now, not deferred

Both the prior R1-R7 and final F1-F3 closure requirements are initial-port requirements. R1-R7 cover complete purpose-specific hold recovery/operator resolution, stage-aware identifier/placed-BIB protection, AdditionalCopy reopen and imported operational claimant eligibility, current tenant trust for existing cookies/sensitive mail/usable-admin checks, persisted fair bounded scans, and dependency-aware database deployment safety. F1-F3 additionally require OutstandingTimeout unreviewed-suggestion creation-age semantics, the particular tracked Polaris hold identity for terminal fulfillment, and exhaustive retained legacy placed-hold evidence/BIB protection. They do not reopen the settled architecture or justify a generic distributed-operation framework, JobRun model, staff-session database, Graph calls, parallel queues, or moving Hangfire tables into the application DACPAC. See `14-REMEDIATION-AUDIT.md` for the binding completion trace.
