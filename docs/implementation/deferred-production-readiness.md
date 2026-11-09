# Deferred production readiness

The current application is .NET 10 with SQL Server 2022/schema 13. Development
qualification, deterministic providers and synthetic acceptance do not establish
production readiness or authorize cutover.

`test_cd_activation: active` records initial test-IIS activation on November 5,
2026 at approximately 7:23 p.m. Eastern for `v1.0.0-test.32`, commit
`46c60a8cdb1218f34622620cbf0b31cc92e6bd07`. The
[receipt](test-iis-activation.md) verifies activation, readiness and state
recording then; current-main deployment and current host health are unverified.
Further deployment/host work remains separately authorized. Production provider validation,
Polaris/Postmark canaries and external mutations, production credentials and
accounts, release/provenance promotion, backups/restore, Data Protection recovery,
host disaster recovery, hostname/certificate/cutover rehearsal, production
deployment and repository rename remain deferred.

PocketBase was an internal proof of concept with no production data. Migration,
source reconciliation and historical development-schema upgrade compatibility
are retired, not deferred. Git history is their archive. Current operation
journals, external-mutation recovery, authorization and SQL data integrity remain
required and must survive every deliberate future schema change.

Current contracts are in AGENTS.md, ARCHITECTURE.md, the database README,
scripts/ci/test-selection.md and test-iis-activation.md. Old porting-pack gates
do not override the product decision recorded in ci-simplification.md.
