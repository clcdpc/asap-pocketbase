# Slice 10: Explicit Synthetic Seed And Reset

## Preparation Only

Not dispatched. Start after the accepted Slice 9 implementation milestone.
Refresh the completed schema, CLI/developer reset paths and test fixtures before
choosing the smallest implementation. Astra Max normally dispatches fresh
Luna High for the complete slice.
[Document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) governs bounded Luna Max
escalation, optional context rotation with concise handoffs, compact evidence
receipts, and Astra-first specialist escalation; Sol remains advisory only.
After required tests, fresh Terra High independently reviews the whole slice.
A clean Pass 1 needs no ceremonial full Pass 2; fixes receive focused re-review
unless document 10's full-review triggers apply. Astra verifies acceptance,
commits/pushes the milestone and requires actual remote CI success for that exact
commit before dispatching the next slice.
Prior exact-milestone CI must be green before this slice is dispatched.

Read root AGENTS, document 10, document 02 Slice 10, document 06 sections 9/13,
the accepted development/deployment configuration boundaries, and document 09's
scope exclusions. This is the required explicit end-of-port utility, not an
automatic bootstrap change or a generalized scenario-generation framework.

## Contract And Acceptance

- Provide an explicit developer/operator command for synthetic demo/test data
  and its reset using the existing project/tooling layout. Do not add a project
  or framework merely to host a small command. Use current entity/schema
  ownership and parameterized SQL/EF APIs appropriate to the existing tool.
- Never invoke it from normal startup, F5, deployment, schema publish, migration
  import or ordinary structural-default initialization. Normal developer reset
  creates schema/defaults only; synthetic data requires its separate invocation.
- Require clear nonproduction target identification and deliberate destructive
  reset confirmation, using the accepted external configuration/tooling where
  possible. Fail closed for production configuration, invalid/ambiguous target
  or incompatible schema, before a write. Document precisely what is replaced.
  Do not equate Testing authentication with permission to reset an arbitrary DB.
- Use only synthetic names, contacts, patrons and records. Preserve domain
  constraints and explicit staff durable identities; readable emails never
  synthesize bindings. No source production data or reusable provider secret
  enters fixtures, logs, reports, source control or CI artifacts.
- Seed known useful patron/staff/AdditionalCopy/settings/history/operational
  states using the actual completed models. Keep examples few and reproducible.
  Do not issue provider mutations, send mail, start jobs or fabricate genuine
  provider evidence while preparing data. Explicit fake test scenarios must
  remain distinguishable from production migration/provider history.
- Reset is bounded to its explicitly selected nonproduction target and obeys
  active-worker/operation safety requirements. It must not delete external
  configuration, key rings, backups or files outside its documented ownership.
  Handle interruption/failure with truthful status, not partial-success claims.
- Add focused real-SQL tests for successful seed/reset, deterministic scenarios,
  refusal before writes on unsafe targets, schema mismatch and failure behavior.
  Verify a usable manual/browser journey against seeded state. Confirm normal
  startup/reset still does not manufacture demo data and repeat invocation has
  the documented behavior without accidental duplication or unsafe provider use.

This command is not the migration importer or production recovery mechanism.
Preserve fresh final-cutover target semantics and permanent-nonproduction's
PocketBase state until the actual tagged rehearsal. Do not run this tool on a
real deployment as part of implementation acceptance. Return code/tests/docs
for independent review using compact evidence receipts; Astra owns the milestone
and progression.
