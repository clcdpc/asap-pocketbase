# Deferred production readiness

The current application is .NET 10 with SQL Server 2022/schema 13. Development
qualification, deterministic providers and synthetic acceptance do not establish
production readiness or authorize cutover.

test_cd_activation: pending_runner_setup

Test-IIS runner activation remains separate and requires a real successful
deployment with exact installed-source evidence. Production provider validation,
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
