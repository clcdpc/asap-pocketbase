# Development Completion And Deferred Production Readiness

Development work on the .NET port is complete and the combined final batch is
integrated. The [completion record](final-development-completion.md) and its
live journal record exact-milestone CI and acceptance. Accepted behavior,
migration correctness, hosted browser/accessibility CI and canonical .NET
documentation are preserved. This is not production readiness or cutover
authorization; no required development slice remains.

## Completed Development And Remaining Work

- Slices 0-8: accepted implementation and repository-side test-IIS CI/CD;
  runner activation remains separate.
- Combined Slice 9 + Slice 11: development work complete and integrated;
  hosted browser/accessibility, cleanup/docs and one final independent review.
  Exact milestone certification is maintained in the completion journal.
- Slice 10: deferred/optional synthetic seed-reset convenience tooling.
- Future phase: production readiness and separately authorized release/cutover.

`test_cd_activation: pending_runner_setup` is compatible with Slice 8 repository
acceptance and development completion. It must not become `active` until a separate
activation task records a real successful IIS deployment and exact installed SHA.

## Preserved Future Work

Defer production deployment automation, backup/restore orchestration, automatic
database rollback, elaborate database-change classification, production IIS/SQL
accounts and infrastructure provisioning, Data Protection recovery and replacement
host disaster recovery, PRTG and production-only diagnostics/operator tooling.
Defer production release/provenance/artifact promotion, permanent-nonproduction
rehearsal, production hostname/certificate/cutover rehearsal, actual PocketBase
production-source/operator evidence, live Polaris release canaries and provider
mutations, production Postmark adapter/webhook/live transport validation, production
cutover/rollback, repository rename and the multiple-pass adversarial final review.
Other requirements solely proving production operational readiness likewise remain
future work. Small pieces concretely required for test deployment correctness are
still in scope; accepted application security/data/migration invariants remain.

The temporary email transport does not prove production Postmark behavior.
No test pipeline operation authorizes production deployment or production tags.

## Authoritative Future References

Retain the detailed contracts in `docs/dotnet-port/01-PORTING-SPEC.md`,
`02-IMPLEMENTATION-PLAN.md`, `04-MIGRATION-CUTOVER.md`,
`05-DEPLOYMENT-OPERATIONS.md`, `06-TESTING-CI.md`,
`08-RELEASE-VALIDATION-NOTES.md`, `10-CODEX-MULTI-MODEL-TASK.md` and
`14-REMEDIATION-AUDIT.md`, plus the original Slice 8/9/11 packets in Git at
`88fd92cf1fdd856dccba6ef3538182d80325e98a`. These remain authoritative for their
deferred production concerns when that phase resumes. Current execution sequence
and acceptance are governed by the revised packets and document 10's current
development-completion policy, not historical production-only gating language.

Preserve all accepted-slice evidence. No deferred operational evidence is claimed
as passed, simulated, waived for production, or supplied by repository-side tests.
