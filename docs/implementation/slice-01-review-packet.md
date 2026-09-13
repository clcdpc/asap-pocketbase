# Slice 1 Independent Review Packet

## Pass 3 Dispatch State

The same Terra High reviewer returns for full Pass 3 after S1-R6 remediation.
No implementation writer/build is active. HEAD remains `cff0285`; the entire
95-file non-documentation inventory is in
`.git/asap-slice-01-review-state-pass3.json`, SHA-256
`db0e81f75429511494bbb88e68a8bddf4f43a4d5c74015184459928ce7724a16`.
Only the patron CSP middleware, its integration tests and CI test-count gate
changed since Pass 2, but the whole slice and surrounding callers remain the
required review scope. Prior findings and triage are in `slice-01-review.md`.

Sol's Release build has zero warnings/errors; .NET 118/118, npm, browser and
format checks pass. Astra independently reran all 118 tests with no skips in
1m 43s and checked the frozen publication. The new source-built artifacts are
`.artifacts/slice-01-handoff-20260912-pass2-verified/`: 309 Web files, 251 native
migration files, all 17 vendor and seven Hangfire asset hashes valid, and no
development-email or forbidden runtime output. Web DLL SHA-256 is
`0dc74a704bfba3c91c97e45bef90e9eefaa58e4f423b8817febfdf968d97c76f`.
Migration DLL and DACPAC retain their independently accepted Pass 2 hashes.

The pinned executable embed-security oracle now passes 14/14 real-HTTP cases
against that publish, including all four reproduced failures. Evidence is
`.artifacts/acceptance/patron-browser/b5069b1770c24b1ead5a42b7fbdd495a/csp-results.json`.
The published desktop/mobile/SQL journey also passes in run
`e30f3cc1685e410d84e459cf9c02ce95`. Owned processes/databases/certificates were
cleaned up. No milestone, remote Slice 1 CI or release gate is claimed yet.

## Pass 2 Dispatch State

The same Terra reviewer returns for a full-slice Pass 2 after Sol's fixes and
parent acceptance. Read `slice-01-review.md` for the five findings, adjusted
pickup triage, failed-login follow-up regression and corrected stale-RID
publication. No finding is waived and no milestone has been committed.

Current HEAD remains `cff02856ed55dfd634484915d4c8aa852687adf8`. The generated
`.git/asap-slice-01-review-state-pass2.json` inventories 95 changed/new
non-documentation files, SHA-256
`7f48b2e0f6d27bebd56b2fd340e46eee342c78ae6b3780017e6f60595de62d37`.
The whole slice and surrounding callers remain the review scope, not just
files changed since Pass 1. No implementation writer is active during review.

Sol's Release build and complete 114-test run passed, as did npm, browser and
format checks. Astra independently reran all 114 tests with no failures/skips
in 1m 41s and verified the failed-login follow-up. The accepted local publish
is `.artifacts/slice-01-handoff-20260912-pass1-verified/`, rebuilt from source
for win-x64. The actual native executable passed the real-PocketBase-shaped
legacy-only, modern-blank and wildcard fixtures, rejected remote HTTP atomically,
and rejected package substitution and same-count target drift. Verify the
published migration DLL, not just its unchanged apphost: SHA-256
`cd9b585d532ab20d447c564125f116bd27aee2b1f8385e152def629f237115ec`.
These are local acceptance results, not release/rehearsal clearance.

## Pass 1 Dispatch State

Sol handed off the completed slice on 2026-09-12 after a zero-warning/error
Release solution build, 104/104 .NET tests with none skipped, the legacy suite,
browser gate and both publish checks. Astra independently reran the complete
104-test suite successfully in 1m 31s, inspected its ten desktop/mobile states
and three authentication races, verified published vendor/Hangfire hashes and
output exclusions, and exercised the actual self-contained executable through
positive and negative export/import/reconciliation. See `slice-01-evidence.md`.

Dispatch HEAD is `cff02856ed55dfd634484915d4c8aa852687adf8`; all Slice 1 code
remains uncommitted pending this gate. The generated local receipt
`.git/asap-slice-01-review-state.json` records 88 changed/new non-documentation
files relative to the accepted Slice 0 milestone, SHA-256
`9242cf6297740dc39851a8895bbaf961f1acf2cdc09ec03811a7318e4daca450`.
No implementation writer is active during review. Preparation-only later
slice packets are not implemented functionality or part of the milestone.
This packet records prerequisites and scope, not an independent review result.

Retain the same Terra context for full-slice Pass 2 after Sol fixes confirmed
findings. Use Pass 3 if Pass 2 finds a new substantive issue. Blocking
correctness, security, data-integrity, migration or material regression
findings cannot be waived because a nominal pass cap was reached. Terra
reviews and reports; Sol edits; Astra accepts and commits the milestone.

## Scope And Authority

Read root AGENTS, the authoritative execution rules in document 10, the
Slice 1 contract in document 02, `slice-01.md`, and its referenced pack
sections. `temporary-email-transport.md` captures the explicit user exception:
only final provider transport is temporarily file-backed. Outbox/application
correctness is not deferred. Real Postmark, provider webhooks and their
release/rehearsal validation remain outstanding; fake webhook behavior is not
permitted. No architecture re-review or reopening settled choices.

Review the entire current Slice 1 implementation and surrounding callers,
not just the latest fixes. The last accepted application milestone is
`00967778001e7ec8198ab4498d0fbd15ded4d984`; later pre-implementation commits
recorded the package blocker and execution documentation. Much of the slice
is currently untracked, so `git diff` alone is not the review scope. Enumerate
and read new files as well as tracked diffs; the local receipt provides the
concrete non-documentation file inventory for this dispatch.

## Implementation Surface

- All `src/Asap.Web/Features/Patron/` and `Features/Email/`, their Program.cs
  registrations, endpoints, current effective configuration, startup/health
  middleware, testing-only provider and Hangfire registration/storage helpers.
- Domain entities and AsapDbContext mappings, SchemaVersion and every new or
  modified DACPAC table/index/constraint/seed script. Inspect both C# and SQL
  enforcement; do not infer one from the other.
- `src/Asap.Web/Frontend/patron/`, shared helpers, branding asset, source-copy
  and publish behavior. Preserve exact vendored assets and existing UX.
- Entire `src/Asap.Migration/` implementation, shared credential contract,
  actual stopped-SQLite/file extraction, package validation, transformations,
  secure target configuration, fresh-target import and reconciliation.
- All new/modified .NET, legacy-regression and browser tests; browser fixture
  integration, dependency lock, workflow CI and artifact exclusions.
- Operator-owned `scripts/hangfire/1.8.25/` assets, hashes/licenses and publish
  inclusion. Runtime must not own/install dependency schema DDL.

Use exact PocketBase `150b30b776565194260cc327eeeffdfb46475e81` for behavior
questions, following the source anchors in `slice-01.md`. Exclude the removed
carousel example. Read actual CLC package/source when protocol-result shape
or cancellation is material. Parent source notes and intermediate probes are
reading aids, not independent-review clearance.

## High-Risk Contracts

1. Patron token hashing/absolute expiry/revocation/current participation and
   Organization serialization during final session insertion. Check both race
   orders, transaction disposal and propagation of cancellation. No production
   or Development auth/provider bypass; rate limiting remains configured and
   independent from public configuration/liveness.
2. Source home/experience/effective-library semantics, current patron/pickup
   refresh, validation/duplicates/weekly limits and racing submissions. Verify
   real SQL scope/locking, immutable snapshots, claim/rule current eligibility
   and lock ordering. No network call inside a SQL transaction.
3. Full Polaris call chains, not only their initial request. Non-2xx/null/
   malformed/partial required data must not become invalid credentials or
   definitive not-found incorrectly. Preserve CLC protocol ownership, actual
   response-body mapping, configured application identity, pickup failure and
   complete-operation cancellation/deadlines.
4. Exactly five outbox states, atomic event intent, deterministic BusinessKey
   uniqueness/races, sender/recipient/content snapshots, missing optional
   configuration, failure/retry/terminal behavior and retained payload.
   Provider start deadline, full-call timeout, two-minute lease, reclaim only
   after expiry, and final Status/LeaseId/post-claim-version fencing apply to
   success, failure, suppression and configuration loss, including late work.
5. Current staff binding/allowed-tenant/role/activity/library scope and the
   stored RecipientAddressKind determine sensitive delivery authorization and
   current address. Exact-domain nonproduction protection applies at intent
   and every send/retry independently of Testing auth. Immutable patron event
   mail is distinct from authorization-sensitive staff mail.
6. FileEmailSender stays deliberately small: cancellable unique HTML preview
   per invocation, encoded metadata/useful HTML body, ignored local output,
   no filesystem-as-business-state assumption, no output in app/CI artifacts.
   Ordinary deterministic application tests use recording/fake boundaries.
7. Typed settings and sparse/whole-set inheritance agree across seed, read,
   form DTO, use and migration. Cover both legacy plain-label and JSON option
   representations, actual logo bytes, source fallback configuration and no
   silently dropped populated setting. Library branding rows are not the
   source of system publication-option defaults.
8. Migration must preserve required source state, strict identity/claims,
   canonical ISBN/BIB/tag invariants and all placement-evidence classes.
   Distinguish explicit-null historical BIB protection from no evidence.
   Unknown/conflicting business history blocks; no fabricated provider journal
   or hold ID. Preserve original required event timestamps; export time is
   for migration annotations only, not invented historical dates.
9. Reconciliation proves values, hashes, relationships, current eligibility,
   transformation/provenance and by-scope counts, not only aggregate row
   totals. Actual file extraction, protected credentials, absent runtime
   state, equivalent fresh-target determinism and meaningful safe error
   reports need direct tests. Unsupported later-slice entities remain honest;
   this is not a production-cutover-ready claim.
10. Browser state cannot be overwritten by old configuration/session/login
    completions or an old 401 after a new login. Check the three recorded auth
    interleavings, normal `/patron/` routing, focus/keyboard behavior, responsive
    layout, local assets and serious/critical axe checks against the real app.

## Evidence And Reporting

`slice-01-evidence.md` records intermediate passes AND failures. No historical
snapshot substitutes for tests against the completed dispatch state. Parent
acceptance found useful regressions in route redirects, mobile layout,
authentication restore/startup races, provider response classification,
transport configuration and real-source migration. Verify the final fixes
and surrounding behavior independently; do not assume every old finding
remains open or every reported fix is complete.

Report actionable findings ordered by severity, with file/line, concrete
failure path, violated behavior/contract and focused test expectation. Separate
confirmed defects from genuine unresolved questions and nonblocking cleanup.
Do not implement your own findings, commit, push or change the port pack.
