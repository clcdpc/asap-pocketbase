# Slice 7: Migration Hardening And Legacy Links

## Acceptance Record - 2026-09-16

Slice 7 passed independent Terra review and short Astra High acceptance.
All substantive package and holistic findings are independently resolved.
The reviewed implementation is `bcd5190ef951fcaf93a0843833b78c89b20ab2af`;
PR #267 merge and resulting `codex/csharp-port` integration SHA are both
`333330da11b5fcaeafaca3fc820eba68fc4953d3`. The merge preserves the exact
reviewed tree and expected integration parent
`e1fc18b7b9b4d2d90892e16bf0264da5e2dc6246`.

This documentation-only acceptance commit is the milestone candidate.
At record creation its exact-milestone `.NET baseline` CI was pending.
The [canonical PR #267 state and journal](https://github.com/clcdpc/asap-pocketbase/pull/267#issuecomment-5684606992)
record the resulting milestone SHA, actual CI run/result and final acceptance;
PR #264 holds the concise current summary. No extra repository commit is
required solely to record this commit's own SHA or later CI result.

| Evidence | Exact anchor / result |
| --- | --- |
| Technical review baseline | `d607723e846f633ebe206163f6c232dd79566d6f` |
| Package 1, PR #268 | Merge `307282fc3a53147101d80964a961f8e58e882204`; S7-P1-1 and S7-P1-2 independently resolved |
| Package 2, PR #269 | Merge `002f8e2b0517d9116a658caf103843ae7f40e292`; S7-P2-1 and S7-P2-2 independently resolved |
| Package 3, PR #270 | Merge `919998a363fe78ddb690e07365a8d075607d42b0`; S7-P3-1 independently resolved |
| Integrated candidate | `919998a363fe78ddb690e07365a8d075607d42b0`; no integration glue changes |
| Integrated validation | Build 0 warnings/errors; .NET/real-SQL 312/312, 0 failed/skipped; 175 frontend test-file processes, 0 failed |
| Browser/accessibility | 18 legacy-link, 10 patron, 20 staff states; 0 serious/critical accessibility findings |
| Migration/publication | Remediation oracles, published self-contained win-x64 positive/repeat/negative workflows and Web/native SQLite/pinned DACPAC linkage passed |
| Integrated CI | `35048541380`, success at the integrated candidate |
| Holistic review | [Review 5218483893](https://github.com/clcdpc/asap-pocketbase/pull/267#pullrequestreview-5218483893); sole finding S7-FULL-1, WAL-backed source identity |
| Final focused review | [Independent S7-FULL-1 RESOLVED disposition](https://github.com/clcdpc/asap-pocketbase/pull/267#issuecomment-5696175981) at `bcd5190ef951fcaf93a0843833b78c89b20ab2af` |
| Fix validation | Export 6/6; validation 3/3; MigrationCliTests 25/25; full suite 313/313; build 0 warnings/errors; published WAL/no-WAL, malformed-WAL rejection and fresh import/reconcile passed |
| Fix CI | `35058299692`, success at the final reviewed SHA |
| Full-slice fix cycles | 1/3; zero unresolved substantive findings |

The fix binds main DB and matching WAL identity, checks their byte stability,
and validates WAL metadata without packaging raw source bytes or mutating
the source DB/WAL. SHM remains transient index state. Format, migration
contract, schema and import-report versions remain `1` / `slice-05` / `5` / `4`.
Existing unaffected integrated frontend/browser evidence remained valid;
acceptance repeated no successful expensive local gates or completed reviews.
Restricted evidence remains local, including the integrated gate index
`asap-slice07-integrated-pre-review-20260915/gate-index.json` and the published
WAL-fix artifacts under `asap-slice07-wal-fix-20260916`.

No representative-old-host run, actual production source/deployed-SHA/operator
verification, permanent-nonproduction exact-artifact rehearsal, deployment or
cutover is claimed. Real Polaris/Postmark/provider-webhook and final release
validation remain outside this evidence. PR #264 remains open/draft into main;
Slices 8-11 have not started. After successful milestone CI, review and narrow
their roadmap before dispatching Slice 8, using integrated-slice Terra review
as the default and deferring production-only work outside the current
development-complete target. This record does not redesign those slices.

## Historical Authorized Bootstrap - 2026-09-15

At this checkpoint, the autonomous Slice 7 run was authorized. Initial verification confirmed
`origin/codex/csharp-port` at documentation-policy branch point
`e1fc18b7b9b4d2d90892e16bf0264da5e2dc6246`, with successful exact-SHA
`.NET baseline` CI `34996100667`. PR #264 was open/draft. Fetched `main`
still matched PocketBase `150b30b776565194260cc327eeeffdfb46475e81`.
No prior Slice 7 branch, PR, supervisor journal or worker was found.

The slice branch was `codex/slice-07-migration-hardening`, targeting
`codex/csharp-port`. Its draft PR held the canonical supervisor state and
immutable transition journal. Package selection awaited current-code discovery;
implementation, validation, review and acceptance had not occurred at this
bootstrap checkpoint. Policy-only bytes after the corrected product baseline
are classified separately from the technical review delta.

The bootstrap instructions below preserve the planning context at that
checkpoint; their future-tense actions were subsequently completed as recorded
above. The objective and technical contract sections remain applicable.

### Verified Prior Anchors

Historical Slice 6 accepted milestone:
`7ba59421176ede99ba48488be6bc81010e60f65c` (CI `34976630186` succeeded).
PR #266's exact reviewed correction is
`04f538ef1a04be33b31d5dbdc3a5c1751b163ee4`, merged at
`c0cde6fb744e53c1a72a63f8cb58124e43255b4f`. Prior authorized product/integration
baseline: `d607723e846f633ebe206163f6c232dd79566d6f`, exact-SHA CI
`34988112346` succeeded. The final docs-policy head produced by this policy
task, with its own successful CI recorded in draft PR #264, is the required
Slice 7 branch point. Record these anchors separately at bootstrap; the actual
technical review base is the prior authorized product baseline above, with
later documentation/process bytes classified separately. Do not mechanically
re-review independently reviewed correction bytes.

This packet narrows the existing sequence; it does not start migration from
scratch or replace the authoritative plan. At bootstrap, refresh the schema
version, CLI contracts, import/reconciliation coverage and remaining domain
gaps from the actual implementation. Choose actual package boundaries then;
none are frozen by this policy edit.

Follow [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) for the
staged-PR lifecycle, baseline/bootstrap contract, PR-backed state/recovery,
review modes, context firewall and exact-milestone CI. One GPT-6 Astra High
autonomous supervisor is preferred/default; GPT-6 Astra Max is bounded
material supervisor/contract escalation only and returns control to High.
Manual/direct execution remains fallback. Reading lists below identify
full-slice authority; workers start with relevant excerpts and expand for
concrete concerns.

Future bootstrap verifies all three baseline/CI anchors, creates the slice
branch from the final policy head, and opens the draft slice integration PR
first. It creates the canonical `asap-supervisor-state:v1` comment, appends
sequence 1 `SUPERVISOR_STARTED`, then projects that event in the same comment.
After inspecting current migration code and selecting packages, record
`BOOTSTRAP_COMPLETED` under document 10. Package PRs use the slice PR as the
central journal; Terra findings live where the code is reviewed. Never put
migration packages, patron information, staff identity maps, credentials,
protected reports or raw provider responses into GitHub comments.

When authorized for the complete slice, the same Astra High task continues
through bounded Luna Max implementation/fixes, independent Terra High review,
full validation, short acceptance, integration and exact-SHA CI without manual
phase launches. Stop at accepted Slice 7 or a defined hard stop. This task
only establishes that future execution contract.

## Objective And Authority

Consolidate the migration code developed with each data-owning slice into the
complete stopped-source, two-host export/validate/import/reconcile workflow.
Finish scoped legacy request-link resolution and browser URL normalization.
Prove required source-to-target semantics and deterministic intentional
transforms, not just table counts or post-import target fingerprints.

Read root AGENTS, document 10, document 02 Slice 7, all of document 04,
document 03's imported models, document 13's full settings inventory,
document 06's migration/R1-R7/F1-F3 cases, document 07's deep-link contracts,
and documents 05/08's offline preparation and exact-artifact gates. Reuse the
accepted prior-slice entities, authorization, image validation, credentials,
SQL constraints and targeted data-access patterns. No migration framework,
resume-in-place engine, running-source extraction service or delta sync.

Inspect exact PocketBase `150b30b776565194260cc327eeeffdfb46475e81` schema
migrations and domain writers/resolvers whenever the stored shape is material.
Exclude the removed carousel example. `migration-source-notes.md`, prior
domain source notes and recorded acceptance fixtures are reading aids only.
Verify newly fetched source changes and the actual frozen deployed SHA; do
not label an unverified source or synthetic fixture production evidence.

## Source And Package Boundary

1. Export directly from stopped SQLite and associated file storage without
   starting PocketBase. Require explicit stopped-source confirmation and the
   effective legacy environment needed by the pinned resolvers. Preserve
   source SHA/schema/snapshot identification, frozen export time, UTF-8 JSON,
   record counts and file lengths/hashes in a validated immutable package.
2. Include actual supported branding bytes, not filenames alone. Unsupported
   or invalid images must block with actionable restricted diagnostics. Do
   not mutate the old source to make it fit a target assumption.
3. Keep effective system/global SQL fallback values separate from library
   domain exports and the operational cron/limit artifact. Exercise existing
   blank Staff URL versus missing-record initialization, exact environment
   precedence and the pinned normalization helpers. Secrets in effective
   artifacts have provenance/hasValue only, never plaintext or fingerprints.
4. Preserve all eight logical queue mappings and four legacy schedules with
   global/timeout/queue-specific precedence. Account explicitly for configured
   retired hourly identifier overrides. Compare effective target external
   configuration before workers can start; a changed value needs a reported
   intentional decision, not silent fallback.
5. Keep packages, identity-map working copies and restricted reports out of
   Git, release/CI artifacts and ordinary logs. Document trusted transfer,
   minimal ACLs and deliberate post-success cleanup. Do not add package
   encryption or another secret-storage product beyond the agreed contract.

## Import And Reconciliation

- Enforce a fresh target with only permitted structural/static DACPAC seeds.
  Reject bootstrap/runtime/business data and any target used for hostname
  preflight. Import does not start the web application or Hangfire. Failed
  imports require target reset/recreation, not resume-in-place guessing.
- Validate all active staff against the explicit operator identity map and
  exact target allowed tenants. Never derive durable identity from UPN/email.
  Preserve inactive-library relationships and staff activity independently.
  Report recipient precedence, preference preservation and old-versus-target
  ordinary/weekly recipient eligibility, including newly eligible summaries.
- Prove an active, bound, allowed-tenant system super-admin after staff import.
  With none, explicitly promote the matching configured bootstrap tuple or
  insert the target-only identity, report it and re-run the common predicate.
  Cover both paths and invalid/duplicate bindings; normal startup is no repair.
- Map open claimants before applying current activity/identity/scope rules.
  Preserve otherwise eligible dormant-library claims. Clear only specified
  invalid effective fields, preserve deterministic history/report provenance,
  keep closed attribution and never auto-assign substitutes. Reconcile both
  request types separately and active auto-claim rules independently.
- Account for every populated configuration field under document 13 or an
  explicit intentional-drop rule. Preserve sparse overrides, whole-set
  inheritance/replacement, library-owned records and current effective values.
  Legacy SMTP credentials are not Postmark credentials. Target-only provider
  input uses a secure prompt/environment boundary and Data Protection only.
- Complete exhaustive identifier-state normalization, found/BIB/tag checks,
  request status/close aliases, unknown-event-to-legacy mapping and strict
  actor validation. Preserve required original timestamps; do not fabricate
  missing event dates from export/import time. Frozen export time is allowed
  for deterministic migration annotations, not missing business history.
- Normalize every placement-evidence class before target event mapping:
  current placement, explicit success, transitions into/from placement, and
  all five terminal hold reasons. Preserve deterministic source provenance,
  known/null BIB protection and ambiguity blockers, without fake operations,
  provider success or invented final HoldRequestIDs. Prove protection after
  reopen followed by a separate identifier/BIB edit.
- Reconcile values, identities, relationships, assets, eligibility, scoped
  status/format counts, reductions and every deterministic conversion. Bind
  the report to the exact immutable package. A different validly hashed
  package or same-count target value mutation must fail. Initial semantic
  parity is distinct from subsequent target drift detection.
- Sessions, old auth tokens, outbox, queue cursors and target hold-operation
  journals begin empty. Import historical delivery/audit data without sending
  mail or synthesizing pending work. Never report unsupported domains as
  successfully imported merely because their source files were retained.

## Legacy Links

Inspect pinned `lib/route_utils.js`,
`pb_public/staff/js/app/url-utils.js` and their actual callers. Preserve
`stage`/`status` aliases and the existing `request` parameter. Use temporary
type-qualified LegacyPocketBaseMapping to resolve title versus additional-copy
IDs, then apply current staff eligibility and SQL scope before disclosure.
Mapping is not authorization. Unknown/deleted/out-of-scope links preserve the
normal nondisclosing failure behavior. Replace the browser URL with the new
invariant decimal bigint ID without losing supported navigation parameters;
keep IDs as strings to avoid JavaScript precision loss. Cleanup remains an
explicit operator action after the reference window, not an automatic purge.

## Blocking Acceptance And Release Boundaries

Use real stopped-source packages and disposable SQL targets, with independent
positive and malformed/conflicting fixtures. Test repeat imports into fresh
targets for identical transformations/reports and source timestamp/asset
preservation. Include all document 04 reconciliation populations and R/F
remediation cases, protected credentials, failure rollback/fresh-target rules,
missing-date diagnostics, both bootstrap paths and effective configuration.

Publish one self-contained win-x64 migration artifact including SQLite native
dependencies; it must export on a representative old host and validate/import/
reconcile on the target host without requiring a .NET runtime on the old one.
Local source-build success is not representative-host or final rehearsal
evidence. Exercise behavior through the actual published native executable,
not only its version command or the framework-dependent test assembly. Slice 1
acceptance caught a stale win-x64 `--no-build` publish despite green source
tests; build the intended RID and verify the published DLLs and behavior.
Browser checks cover both link types, normal authorization, aliases,
URL replacement, unknown/deleted records and non-lossy large IDs.

The temporary FileEmailSender exception remains only at final transport.
Real Rest 3-compatible cancellable Postmark integration, provider/webhook
tests and release validation are still release/rehearsal blockers. Actual
production source shape, deployed SHA and operator inputs must be verified;
synthetic fixtures cannot satisfy those gates. Do not merge the final port PR,
tag, rehearse on permanent nonproduction or enable production writes as part
of this slice.

Run the complete integrated slice validation before holistic Terra review.
Package/fix validation, bounded Luna fixes, re-review and staged integration
follow document 10. Return changed paths, compact receipts and remaining risks
at the authorized branch/PR checkpoint. Slice 8 cannot start before acceptance
and successful exact-SHA CI on the integration milestone.
