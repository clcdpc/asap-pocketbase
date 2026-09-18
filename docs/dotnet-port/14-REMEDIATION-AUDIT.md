# Porting pack remediation and handoff audit

The dated execution/model-policy entries below are historical. Staged PRs begin with Slice 6; [document 10](10-CODEX-MULTI-MODEL-TASK.md) now makes thin autonomous supervision with bounded workers and a strict context firewall preferred/default, with manual/direct fallback. This supersedes earlier model/process instructions without changing the technical remediation findings, contracts or acceptance evidence recorded here.

## Astra-first escalation refinement - 2026-09-13

Documentation-only execution-policy correction on `codex/csharp-port`, draft
PR #264. At task start, clean local HEAD, fetched implementation branch and PR
head matched `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`; fetched `origin/main`
still matched the pinned PocketBase SHA. Slices 0-3 remain accepted; Slice 4
remains not started.

Luna Max now always consults Astra Max first on material blockers under document
10. Astra inspects governing evidence and gives a concrete ruling whenever it
suffices, without a fixed Luna-failure count. Only Astra may dispatch fresh,
bounded Sol High advice when uncertainty remains or independent reasoning has
material value; Astra evaluates the advice and decides the resolution. Sol
XHigh requires Astra's determination of a remaining Sol High blocker or
exceptional cross-system reasoning. Luna implements/tests, Terra independently
verifies, and neither Sol model owns the slice.

The normal Luna/Terra loop and every review, test, migration, milestone and
exact-commit remote-CI gate are unchanged. Prepared Slice 4-11 packets, prior
Sol implementation history and all earlier audit entries are unchanged.
`FileEmailSender` retains its temporary boundary; real Postmark integration
and required validation remain release/rehearsal blockers. This update changes
no architecture, application, schema, migration, frontend or test implementation.
Validation covers current-policy/history separation, four JSON examples,
affected pack-local references and all 19 regenerated manifest hashes. No
application/release validation is rerun or claimed; PR #264 remains draft.

## Remaining-slice execution-model transition - 2026-09-13

This documentation-only update takes effect at Slice 4 on the existing
`codex/csharp-port` branch and draft PR #264. At task start, local HEAD, the
fetched implementation branch and PR head all matched
`3d4a4d9fb28983fb3ac25489e9a641d9b38abdfe`; the working tree was clean and
fetched `origin/main` matched the unchanged behavioral PocketBase pin
`150b30b776565194260cc327eeeffdfb46475e81`.

Slices 0-3 completed under the prior Sol-based implementation strategy. Their
milestones, Sol attribution and review/CI evidence in
`../implementation/PORT-STATUS.md` remain historical evidence. The requested
stop after Slice 3 was satisfied; implementation is intentionally paused at
that clean boundary, ready to resume with Slice 4. No Slice 4 implementation
is started or certified by this update.

Current policy in README, implementation-plan sections 3-4, document 10,
decision-register section 3, PORT-STATUS, the existing prepared Slice 4-11
packets and PR #264 now uses Astra Max orchestration, fresh Luna Max complete
slice ownership retained through tests/fixes/retesting, and fresh independent
Terra High retained through the slice's full review/re-review sequence. Sol
High is a fresh focused advisor only under the concrete unresolved-issue
conditions in documents 02/10; Sol XHigh is exceptional escalation only.
Difficult subject matter alone does not transfer a slice to Sol.

The existing minimum two full Terra passes, conditional third pass, continued
blocking-finding resolution regardless of nominal cap, and separate final
whole-app review remain intact. Astra refreshes prepared packets against
accepted prior implementation, verifies acceptance, creates/pushes milestones,
and requires actual remote CI success for each exact milestone before the next
slice. All implementation, testing, migration, external-provider, rehearsal and
release gates remain binding; prepared functional content is preserved.

`../implementation/temporary-email-transport.md` is unchanged: FileEmailSender
substitutes only the final provider boundary, preserves SQL outbox and
authorization-sensitive recipient/domain/lease/fencing/idempotency/retry
contracts, and never simulates provider/webhook success or becomes another
subsystem. Real Rest 3-compatible cancellable `Clc.Postmark.Api` and its required
provider/webhook/transport validation remain release/rehearsal blockers.

Documentation validation for this update covers current-policy versus
historical Sol references across the full pack and execution records, all four
JSON examples, affected local references, and every final SHA-256 against the
unchanged 19-payload inventory with manifest self-exclusion. No application,
schema or source code changes, implementation/release validation, or rerun of
Slice 3's previously certified 190 application/SQL/browser tests is claimed.
PR #264 remains draft. The dated audit sections below, including their original
execution-model wording and validation/archive hashes, describe earlier
revisions and are preserved unchanged.

## Current four-finding cutover/outbox remediation - 2026-09-12

This documentation-only remediation closes four confirmed review findings without reopening the settled port architecture or redesigning R1-R7/F1-F3. The pinned PocketBase baseline remains `150b30b776565194260cc327eeeffdfb46475e81`.

1. **Post-cutover PocketBase retention:** `02-IMPLEMENTATION-PLAN.md` section 7, `04-MIGRATION-CUTOVER.md` section 13, `05-DEPLOYMENT-OPERATIONS.md` section 13, the cutover safety note in `08-RELEASE-VALIDATION-NOTES.md`, `10-CODEX-MULTI-MODEL-TASK.md`, and `12-DECISION-REGISTER.md` section 30 now prohibit starting retired production PocketBase as-is after .NET accepts writes. Retention is forensic/reference only; direct inspection is preferred, and any genuinely necessary execution uses an isolated copy with outbound Polaris/email blocked and recurring production jobs disabled. It is never parallel read/write or fallback production.
2. **Expired EmailOutbox leases:** `01-PORTING-SPEC.md` section 14, `03-DATABASE-DESIGN.md` section 11, `02-IMPLEMENTATION-PLAN.md` slice 5, `06-TESTING-CI.md` sections 3 and 10, release validation, the implementation handoff, source-reference deviation summary, and decision register now treat every expired sending lease as transport-ambiguous. The fixed initial policy is a 30-second call-start deadline, 30-second complete provider timeout, two-minute lease, reclaim only after expiry, and final-update fencing by status + expected `LeaseId` + rowversion/equivalent ownership. At-least-once remains explicit; exactly-once is not claimed and no generalized messaging framework is introduced.
3. **Sensitive staff recipient rule:** the same outbox contracts add target-only `RecipientAddressKind` with only `notification_email` and `weekly_summary`. Send/retry resolves the current kind-specific destination and compares it with `ToAddress`; it never infers the rule from `BusinessKey`. `06-TESTING-CI.md` section 10 and the deterministic release gate cover primary/weekly changes and fallback. `04-MIGRATION-CUTOVER.md` section 6.13 keeps the new outbox empty, so no legacy outbox transformation is invented.
4. **Post-merge emergency PocketBase hotfixes:** `01-PORTING-SPEC.md`, `02-IMPLEMENTATION-PLAN.md` section 7, `04-MIGRATION-CUTOVER.md` section 9.3, `05-DEPLOYMENT-OPERATIONS.md` section 12, release validation, the implementation handoff, and decision register now allow only a temporary branch/tag from the exact last PocketBase production commit, require normal emergency test/deploy plus immediate equivalent .NET `main` work, and require replacement exact-artifact rehearsal. Schema/stored-data/migration-input/assumption changes update migration contracts/tooling and force the full rehearsal. No permanent PocketBase branch is retained; the permanent final tag points to the exact commit frozen at successful cutover.

Mechanical validation for this revision covers all 19 payload files plus the self-excluded manifest: the four JSON examples parse, pack-local file/section references resolve, stale contradictory wording is absent, and every manifest SHA-256/inventory entry matches the final bytes. These checks do not claim that the specified .NET, SQL, worker, provider, migration, rehearsal, or release tests have run.

## Execution-model handoff refinement - 2026-09-12

This documentation-only refinement updates the planned Codex roles to GPT-6 Astra Max for orchestration and fresh architectural/deviation advice, fresh GPT-5.6 Sol High/XHigh contexts for primary slice implementation and confirmed-finding fixes, and fresh Terra High contexts for independent review with the same context retained through each slice's review/fix/re-review cycle. Astra may use Luna only for narrowly bounded mechanical/support delegation that does not replace Sol's slice ownership or Terra's independent review. This is not an R/F remediation finding and changes no architecture, implementation requirement, migration behavior, test/release gate, or pinned PocketBase baseline.

## Final handoff cleanup - 2026-09-12

This documentation-only cleanup on the existing `codex/csharp-port` branch closes the recipient-domain safety contract, agent-instruction scope, and stale closure/schedule wording. The pinned PocketBase baseline remains `150b30b776565194260cc327eeeffdfb46475e81`. No settled architecture, schema, migration, workflow, authentication, hold-recovery, or deployment behavior is redesigned; R1-R7 and F1-F3 remain required initial-port work.

- `01-PORTING-SPEC.md` section 14 and `12-DECISION-REGISTER.md` section 16 explicitly bind the existing application switch/allowlist to all outgoing recipients, fail-closed matching/validation, terminal suppression, normal business commits/idempotency, and unrestricted production delivery under this predicate. `06-TESTING-CI.md` section 4.1 and `08-RELEASE-VALIDATION-NOTES.md` section 6 require deterministic provider-fake evidence without a live Postmark send. The implementation plan/task link those requirements into the existing slices.
- Repository-level `AGENTS.md` now scopes PocketBase-only rules to legacy work and recognizes the .NET DACPAC/EF Core/Dapper/ADO.NET/SQL contract. `02-IMPLEMENTATION-PLAN.md` and `10-CODEX-MULTI-MODEL-TASK.md` explicitly require its full replacement/adaptation in Slice 11 before merge; that post-port rewrite is not performed now.
- `09-DEFERRED-FOLLOWUPS.md` requires both R1-R7 and F1-F3 while retaining the deferred-work boundary. The implementation-plan closure gate distinguishes four pinned-source cron triggers from seven target schedules (including maintenance), eight configured processing queue keys, and the separate logical HoldRecovery scan, referring to the authoritative matrix.
- Pack `README.md` reports this cleanup. The operational application template lives in the self-contained test-host bootstrap, while `examples/Config.example.json` remains its test-enforced documentation-pack representation. The manifest retains its self-hash exclusion and inventories the 19 pack payload files; repository-root `AGENTS.md` is outside that payload.

Mechanical validation for this cleanup: all four JSON examples parse; pack-local file and numbered-section references resolve; the pack-wide wording search leaves no claim of seven original PocketBase cron triggers; local and remote `main` match the pinned SHA. The historical audit bodies (including archive/review hashes) and all unrelated example values are unchanged. The regenerated SHA-256 manifest is verified against the final payload bytes and exact inventory of 19 payloads plus the self-excluded manifest.

These are documentation checks, not execution of the required target .NET/SQL/provider tests. No new ZIP or external review is produced or claimed; the archive/review hashes and validation statements below describe their historical revisions only.

## Historical record - Final F1-F3 review

The following F1-F3 record and the prior R1-R7 appendix are retained as historical evidence; references to "current", "this revision", and packaging within them refer to those earlier reviews.

## Current scope and evidence boundary

This revision addresses only Findings #1-#3 of the current comprehensive closure review (F1-F3 below). The earlier seven-finding audit is retained as a historical appendix; its R1-R7 numbers are not the latest review's finding numbers. Previously clean architecture is not intentionally redesigned. The only changes to adjacent contracts are direct consequences or cross-references for timeout semantics, terminal-hold identity, and migration BIB-protection evidence.

- Input ZIP: `asap-dotnet-porting-pack-2026-09-11-final-remediation(2).zip`.
- Input ZIP SHA-256: `83ed48f5c23e433ddcc62236f2df3ad27a875da9cfbf07d1d23a6908269dbb96`, matching the reviewed archive despite the upload suffix.
- Current review: `ASAP-porting-pack-comprehensive-closure-review(2).md`; SHA-256 `0cee2717c5cdc2ce3c5c5bcfe425b39b17b8248942854e2228738ce4c1cda9c1`.
- Pinned source: `clcdpc/asap-pocketbase@150b30b776565194260cc327eeeffdfb46475e81`, unchanged and verified through the repository connector. Executable anchors and provider-reference boundaries are in `11-CURRENT-POCKETBASE-REFERENCE.md` section 18.

**Validation distinction:** the required implementation tests, migration fixtures and release gates listed below are specified, not executed by this documentation task. No target .NET build, real SQL concurrency/import run, live Polaris call, Postmark delivery or infrastructure deployment was performed. Provider-version capability verification and real exported-data reconciliation remain implementation/release evidence, with deterministic safe behavior already specified for missing identity and ambiguous source evidence; they are not unresolved product/design choices.

## Finding #1 - OutstandingTimeout semantics (F1)

Resolved by:
- `01-PORTING-SPEC.md` sections 23 and 23.2: correct hourly phase label and four explicit state/age/closure/notification predicates; strict boundary, injected clock, same scheduled/manual path and current-state revalidation. No approved-purchase expiration policy.
- `03-DATABASE-DESIGN.md` WorkflowSettings and `13-SETTINGS-SCOPE-INVENTORY.md` section 4: existing names and inheritance retained with suggestion/creation-age meaning.
- `04-MIGRATION-CUTOVER.md` section 6.6: system/default and library values and rejection-template selection retain that meaning. Plan, decision register, implementation task and release notes carry the same contract.

Source behavior checked:
- `lib/jobs/timeouts.js`: `processOutstandingTimeout`, `processPendingHoldTimeout`, `processHoldPickupTimeout`, `processAdditionalCopyTimeout`.
- `lib/jobs.js`: `runScheduledHoldCheck`; `lib/additional_copies.js`: `closeTask`.

Validated by:
- Required `06-TESTING-CI.md` section 10.2 F1 fixtures: 31-day suggestion versus equally old outstanding purchase with an enabled 30-day threshold; nonexpired, disabled and inactive cases; exact/either-side clock boundary; durable notification uniqueness and disabled/missing mail; effective defaults/overrides/template migration; scheduled/manual parity; stale/concurrent state and scan-order independence; the other three timeout families.
- Required `08-RELEASE-VALIDATION-NOTES.md` section 2.2 F1 release gate and configuration reconciliation.

Stale contradictory wording searched:
- Entire pack: OutstandingTimeout keys/queue, outstanding-purchase timeout/expiry language, hourly ordering, later-phase guards, settings, migration timestamps, task/decision/testing summaries and retained earlier audit. Source descriptions and explicit prohibitions are distinguished from target eligibility.

Remaining ambiguity:
- none

## Finding #2 - Specific terminal hold correlation (F2)

Resolved by:
- `01-PORTING-SPEC.md` section 9.2: request/transaction identity versus final HoldRequestID; existing-operation capture on real placement/adoption; latest successful operation authority; no first-same-BIB selection; missing/ambiguous/error diagnostics; positive title-level checkout and manual/timeout paths preserved.
- `03-DATABASE-DESIGN.md` TitleRequest/TitleRequestEvent contracts and section 12: roles of existing `PolarisRequestGuid` and `PolarisHoldId`, immutable known identity and evidence-backed null-to-known enrichment, without another journal/table.
- `07-API-FRONTEND-COMPATIBILITY.md` section 14.2: safe diagnostic and current authorization/request-version behavior, distinct from permanent history protection. `05-DEPLOYMENT-OPERATIONS.md` adds only the directly related diagnostic cross-reference.
- `02`, `08`, `10`, `11` section 18 and `12` propagate implementation, source-deviation and validation requirements.

Source behavior checked:
- `lib/jobs/fulfillment_tracker.js`: `processCheckedOut`, same-BIB terminal loops and closure events; blob `d090e86c1c49a59f06175f2a1b622777d7e2800b`.
- `lib/jobs/hold_placement.js` and `lib/polaris/bib/holdings.js`: create/reply and RequestGUID/transaction context.
- `lib/staff/title_request_bib_actions.js`: live existing-hold detection/adoption. Official PAPI create and patron hold-list references establish separate identifier roles, not a guaranteed lookup on the unsupplied deployed version.

Validated by:
- Required `06-TESTING-CI.md` section 10.2 F2: H200/BIB 42 with old H100 unclaimed/cancelled/expired; exact H200 closure once; wrong BIB/ID, mixed order and multiple holds; missing/legacy identity, conflicting evidence and provider failure; new/adopted hold identity capture; current Organization/scope/version validation; positive checkout, manual closure and configured timeouts. Proven success with unavailable ID and later evidence-backed enrichment never replay placement or business side effects.
- Required `08-RELEASE-VALIDATION-NOTES.md` section 2.2 F2 and version-matched live identity/result validation or the specified null-identity fallback.

Stale contradictory wording searched:
- Entire pack: fulfillment/BIB authority, same-BIB matching, RequestGUID/HoldRequestID/PolarisHoldId, succeeded/adoption/recovery completion, API capabilities, legacy history and prior R2 references. BIB-only authority remains only for positive checkout or descriptions of the source defect.

Remaining ambiguity:
- none

## Finding #3 - Exhaustive retained migration evidence (F3)

Resolved by:
- `04-MIGRATION-CUTOVER.md` section 6.10: normalized current state, all five terminal reasons, dedicated events and entered/left-placed status history; existing-hold adoption; best-effort omissions; known/null BIB and deterministic provenance on one existing legacy-event marker. Ambiguous/conflicting evidence blocks with reports; closure alone never proves placement.
- `04-MIGRATION-CUTOVER.md` section 8: explicit evidence/protection/provenance/ambiguity counts and fresh-target repeat reconciliation, separate from runtime identity and with zero fabricated operations/provider identities.
- `01-PORTING-SPEC.md`, `03-DATABASE-DESIGN.md`, and `07-API-FRONTEND-COMPATIBILITY.md` section 14.2: permanent marker-presence validation even for null BIB and after a committed reopen followed by a separate edit; capabilities/backend agree.
- `02`, `08`, `10`, `11` section 18 and `12` carry the same evidence and implementation gates.

Source behavior checked:
- `lib/records/helpers.js`: full CLOSE_REASON set, normalization behavior, relational event status fields and best-effort `recordEvent`.
- `lib/staff/title_request_bib_actions.js`: `maybePromoteExistingPolarisHold`; `lib/records/suggestions.js`: `updateTitleRequest` status_changed event.
- `lib/jobs/hold_placement.js`, `lib/jobs/fulfillment_tracker.js` and `lib/jobs/timeouts.js`: placed events, terminal reasons, source current-stage outcomes independent of complete event recording.

Validated by:
- Required `06-TESTING-CI.md` section 10.2 F3: every terminal reason without a literal placement event; current placed state; dedicated/status_changed/adoption histories; absent event and raw canonical code with null derived relation; known/null BIB; never-placed closed control; ambiguous/conflicting evidence; separate reopen/commit/edit; capability/backend consistency; deterministic equivalent fresh imports and no duplicate equivalent metadata.
- Required `08-RELEASE-VALIDATION-NOTES.md` section 2.2 F3 and migration section 8 reconciliation totals/results, including no manufactured operation, provider success or hold identity.

Stale contradictory wording searched:
- Entire pack: two-reason-only protection lists, literal-event-only tests, imported success/identity assumptions, null marker versus absent history, reopen/identifier-edit bypasses, capability conditions and earlier R2 audit. Historical protection and runtime correlation are explicitly separate.

Remaining ambiguity:
- none

## Current mechanical validation record

Final verification for this revision covers all 19 payload files (15 Markdown documents and four JSON examples) plus `PACK-MANIFEST.txt`:

- All three current findings were re-read and checked against their explicit F1-F3 test/release requirements before packaging.
- All four standalone JSON examples parse with duplicate-key rejection. Fenced JSON examples, if present, are parsed as well. Configuration shapes and all four example files are byte-identical to the input.
- The deferred-followups document is byte-identical. No new journal/history subsystem, orchestration framework, settings schema or unrelated security/deployment machinery is introduced. The deployment document changes only the directly related safe diagnostic.
- Pack-local links, filename references and the newly affected section references are checked. Stale-wording searches cover the entire pack, not just modified paragraphs.
- The pinned SHA remains unchanged; original ZIP/review hashes are rechecked after packaging.
- The regenerated manifest inventories all 19 final payloads and intentionally omits a self-hash. Every SHA-256 is verified against both working files and archived bytes; ZIP inventory must match the payload inventory plus the manifest, with no extras or duplicate paths, and ZIP CRC validation must pass.

**Current disposition:** Findings #1-#3 are resolved at the documentation/design-contract level. Implementation and live/release validation remain required, not claimed complete.

## Historical appendix - Prior R1-R7 audit

The following records the preceding revision and its input artifacts. It is retained for traceability, not a second current remediation scope or a new verification of those previously clean areas. References to placed-BIB fulfillment in prior R2 are now qualified by current F2: title-level positive checkout is distinct from terminal identity. Prior R2 migration evidence is completed by current F3; current F1 supplies the exact timeout predicates for prior R6 ordering. The current sections above supersede only those directly affected interpretations.

### Scope and evidence boundary

This is the targeted remediation of Findings #1-#7 from the attached comprehensive closure review, not another architecture review. All seven confirmed findings were re-read against the final coordinated contracts before packaging. Previously settled architecture, source behavior outside these corrections, and deferred-work boundaries remain authoritative.

- Input ZIP: `asap-dotnet-porting-pack-2026-09-11-closure-remediated(2).zip`.
- Input ZIP SHA-256: `d0a9cb05581d32e78a21bfee04428b8d392b323804ca185216257f6fbc1d9503`. Its bytes match the exact archive hash recorded by the review despite the upload filename suffix.
- Review: `asap-comprehensive-closure-review-2026-09-11(2).md`; SHA-256 `813ef62a45b19fd54a140429857563a586e6e0f9e50b561384ff15705f6cd938`.
- Pinned behavioral source remains `clcdpc/asap-pocketbase@150b30b776565194260cc327eeeffdfb46475e81`.
- Connected-source inspection used that exact SHA for `lib/jobs/hold_placement.js`, `lib/polaris/bib/holdings.js`, `lib/additional_copies.js`, `lib/job_queue.js`, and the relevant validation portion of `lib/staff/title_request_actions.js`. No newer source commit was substituted.

**Validation distinction:** the finding entries below identify the required implementation/regression and release-validation evidence now specified by the pack. This documentation task did not execute a .NET implementation, real SQL concurrency/migration tests, live Polaris/Postmark calls, Entra sign-ins, or IIS/Hangfire deployments. Mechanical file/archive checks were executed separately as recorded at the end. Missing live provider negative-result guarantees do not leave a design choice open: ambiguous marked mutations require operator resolution and cannot be replayed automatically.

### Prior finding R1

Resolved by:
- `01-PORTING-SPEC.md` section 9.1, Hold placement journal: exclusive owner token/epoch/lease; durable pre-create and pre-reply markers; persisted RequestGUID, normalized transaction qualifiers, status and reply context; explicit positive/no-effect/ambiguous evidence; three-evaluation automatic recovery bound; evidence-based operator resolution without forced replay.
- `01-PORTING-SPEC.md` sections 23 and 23.1: acquired-hold recovery is the first existing workflow phase, independent of Organization activity, using a bounded fair logical queue. Only previously acquired authorization may finish while inactive; a new numbered acquisition remains prohibited.
- `03-DATABASE-DESIGN.md` section 12 and `07-API-FRONTEND-COMPATIBILITY.md` section 14.2, Hold recovery operations: durable fields/constraints, terminal-state invariants, mutation barrier, safe operation DTOs and authorized Reconcile/Resolve actions.
- `02-IMPLEMENTATION-PLAN.md` slices 2/5, `05-DEPLOYMENT-OPERATIONS.md` diagnostics, and `10-CODEX-MULTI-MODEL-TASK.md` binding closure requirements: implementation and operations use the same lifecycle, not unique-row-as-executor or Hangfire retry as mutation permission.

Validated by:
- `06-TESTING-CI.md` section 10.1 R1: every requested crash boundary, original-versus-recovery and two-recovery races, restart/inactive Organization, durable reply context, final/no-effect/ambiguous evidence, no second unsupported create or uncertain reply, and operator-required outcome.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: selected SDK response/reply/negative-evidence capabilities and disabled mutation replay; absent authoritative proof selects the conservative operator path.

Stale contradictory wording checked:
- Hold journal/state tables, mutation barriers, workflow order/inactive filters, API recovery actions, implementation task and decision register; removed hold no-hold-after-grace permission and undefined reconcile/resume authority.
- Distinct existing at-least-once email semantics remain email-specific and do not authorize Polaris hold replay.

Remaining ambiguity:
- none

### Prior finding R2

Resolved by:
- `01-PORTING-SPEC.md` section 23 and `07-API-FRONTEND-COMPATIBILITY.md` section 14.2: stage-aware normalized changed/cleared/unchanged identifier matrix; pre-placement invalidation preserved; protected/closed changes rejected; combined status/BIB inputs cannot bypass validation.
- `03-DATABASE-DESIGN.md` sections 7/12: successful operation BIB snapshot stays immutable and aligned with the request/positive-checkout reference after local completion; current F2 additionally requires particular-hold identity for terminal outcomes.
- `04-MIGRATION-CUTOVER.md` section 6.10: existing legacy-event metadata preserves the recorded source placed-BIB guard through reopening without fabricating an external success or target operation. A historical unknown BIB is not invented.
- `02-IMPLEMENTATION-PLAN.md`, `10-CODEX-MULTI-MODEL-TASK.md`, and `12-DECISION-REGISTER.md`: all edit summaries now qualify invalidation as permitted pre-placement work; DTO capabilities, errors, audit and fulfillment agree.

Validated by:
- `06-TESTING-CI.md` section 10.1 R2: changed/cleared/unchanged/combined BIB in every status and closed reason, placement/edit races, rejected no-side-effect checks, UI capabilities/accessibility, and actual placed-BIB positive checkout after attempted editing; current F2 supplies the distinct terminal-hold oracle.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: stage and fulfillment regression is a blocking deterministic gate.

Stale contradictory wording checked:
- Identifier processor, schema BIB constraints, request/API edit matrices, frontend capabilities, migration snapshots, testing summaries and decision register; no unconditional identifier-reset instruction remains for placed/closed requests.

Remaining ambiguity:
- none

### Prior finding R3

Resolved by:
- `01-PORTING-SPEC.md` sections 7.5/7.6 and 20.1: reopening is claimant activation, including unchanged retained FK; Organization -> StaffUser -> task locking and post-lock eligibility validation; preserve valid claims or clear effective fields with historical Notes, never substitute an assignee.
- `03-DATABASE-DESIGN.md` sections 3/9 and `07-API-FRONTEND-COMPATIBILITY.md`, AdditionalCopy reopen result: closed history is untouched until reopen, new rowversion/effective claim and clearing reason are returned, mine/unclaimed UI refreshes.
- `02-IMPLEMENTATION-PLAN.md` slice 3 and `12-DECISION-REGISTER.md` section 20: retained-claim and ordinary writer/lifecycle rules agree.

Validated by:
- `06-TESTING-CI.md` section 10.1 R3: deactivated claimant, A -> B move, super-admin demotion, still-valid same-library/cross-library super-admin, no claimant, both lifecycle race orders, stale reopen conflict and unduplicated Notes.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: operational relationship activation is part of release closure.

Stale contradictory wording checked:
- Closed-history preservation, AdditionalCopy create/reopen/claim contracts, lifecycle cleanup, task instructions and UI result behavior. Source retained-claim behavior remains described only as source, with the target correction explicit.

Remaining ambiguity:
- none

### Prior finding R4

Resolved by:
- `04-MIGRATION-CUTOVER.md` section 6.3: map every operational claimant, then validate current target activity/identity/trust and role/library scope; exact unmapped/inactive/out-of-scope conversions; valid same-library and system super-admin preservation; closed attribution retained.
- `04-MIGRATION-CUTOVER.md` sections 6.11 and 8, Operational-claim and placement reconciliation gates: both request types, deterministic report/history counts, and eligibility proof rather than FK-only reconciliation.
- `01-PORTING-SPEC.md` section 7.6, `03-DATABASE-DESIGN.md` sections 3/9, and `12-DECISION-REGISTER.md` section 28: stored relationship validation is distinct from Organization participation; inactive Organization alone never clears a valid claim.

Validated by:
- `06-TESTING-CI.md` section 10.1 R4: both request types, all requested claimant classes, every actionable TitleRequest status, closed equivalents, inactive Organization with valid/invalid claimant, exact conversion counts and deterministic fresh-target reruns.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: no invalid effective operational claimant may survive reconciliation.

Stale contradictory wording checked:
- Orphan-only migration repair, rule-versus-claim normalization, open/closed claimant treatment, import/bootstrap order, settings participation language, reconciliation checklist and implementation task. Active identity-map hard gates are not bypassed by claim clearing.

Remaining ambiguity:
- none

### Prior finding R5

Resolved by:
- `01-PORTING-SPEC.md` sections 7.3-7.6 and 14: one current predicate for ticket/recipient authentication-email equality, loaded tenant trust, active StaffUser, current role/organization/scope and required participation; every request and sensitive-mail send/retry uses it.
- `03-DATABASE-DESIGN.md` sections 3/11 and `07-API-FRONTEND-COMPATIBILITY.md` authentication/delivery contracts: persist mail recipient StaffUser ID and normalized authentication-email snapshot; removed-tenant cookie rejection/expiry and API 401; scope/participation denial remains the ordinary authorization response; stale mail suppresses terminally.
- `05-DEPLOYMENT-OPERATIONS.md` configuration/startup gates, `13-SETTINGS-SCOPE-INVENTORY.md` section 12, and `12-DECISION-REGISTER.md` sections 7/8: stopped-app candidate validation and fail-closed startup prevent zero usable super-admins, with the same predicate and no Graph/session store/key rotation.

Validated by:
- `06-TESTING-CI.md` section 10.1 R5: real cookie middleware, persistent key ring, tenant removal, authentication-email change, queued-mail suppression, independent deactivation/scope invalidity, and zero-usable-admin failures.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: current loaded trust rather than successful cookie decryption is the release boundary.

Stale contradictory wording checked:
- Per-request auth enumerations, sender eligibility, usable-admin counts and invariant mutations, external-config restart/bootstrap distinctions, source-reference target notes, implementation task, settings scope and persistent-key recovery statements.

Remaining ambiguity:
- none

### Prior finding R6

Resolved by:
- `01-PORTING-SPEC.md` sections 23/23.1: deterministic finite keyset cycles per logical queue/scope with immutable creation/ID order and committed-ID watermark; no reset at cap/restart; post-durable-outcome checkpoints; explicit wrap, deletion, insertion, failure and scoped-run semantics plus a bounded eventual-access argument.
- `03-DATABASE-DESIGN.md` section 12: small QueueProgress table, unique queue/scope key, scalar cursor without source FK, rowversion and safe last outcome; no general JobRun framework.
- `02-IMPLEMENTATION-PLAN.md` slice 5, `05-DEPLOYMENT-OPERATIONS.md` diagnostics and `13-SETTINGS-SCOPE-INVENTORY.md` section 12: existing caps/precedence/eight configuration keys remain; HoldRecovery reuses HoldPlacement limits as a separate bounded logical phase.
- `12-DECISION-REGISTER.md` section 23: recovery precedes existing timeout/promotion/placement/fulfillment order, with later-phase expiry checks so capped timeout scans cannot be bypassed.

Validated by:
- `06-TESTING-CI.md` section 10.1 R6: low valid cap with unresolved oldest rows/later actionable work, restart, deleted cursor, forward/backdated inserts, eligibility changes/late commits, handled item failures and uncommittable SQL, scoped manual runs, cap changes and timeout order.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: finite progress and scoped timeout-safe execution are blocking gates.

Stale contradictory wording checked:
- Per-invocation oldest-first reset semantics, per-row five-minute retry assumptions, configured limit inventory, global/scoped Run Now rules, workflow phase summaries and migration of scheduler state. Source reset behavior is retained only as the behavior deliberately corrected.

Remaining ambiguity:
- none

### Prior finding R7

Resolved by:
- `05-DEPLOYMENT-OPERATIONS.md` sections 8-10: release metadata/read-only dependency detection; DatabaseChanging includes app DACPAC, required dependency DDL and explicit DB changes; quiesce -> verified existing-DB backup -> DDL -> both-schema validation -> files/start; failure defaults stopped.
- `03-DATABASE-DESIGN.md` section 2 and `01-PORTING-SPEC.md` section 5: dependency-aware successful deployment metadata and actual-schema compatibility; genuine file-only deployment skips backup/DB writes and uses installed-file metadata, not a misleading DeploymentState update.
- `02-IMPLEMENTATION-PLAN.md` slice 8, `04-MIGRATION-CUTOVER.md` runbook and `12-DECISION-REGISTER.md` sections 11/29: Hangfire stays outside DACPAC; rollback/restart validates dependency state as well as application SchemaVersion, including partial upgrade failure.

Validated by:
- `06-TESTING-CI.md` section 10.1 R7: true no-DDL file-only fast path, changed DACPAC with same SchemaVersion, unchanged DACPAC plus Hangfire DDL, failed backup preventing DB/files changes, and dependency upgrade failure preventing incompatible workers restarting.
- `08-RELEASE-VALIDATION-NOTES.md` section 2.1: exact package/schema asset/compatibility evidence and restored-database upgrade rehearsal before production.

Stale contradictory wording checked:
- App-DACPAC-only backup branches, manifest and deployment-state semantics, operator/runtime schema ownership, fresh-target versus existing-dataset cutover, readiness/startup and old-code rollback. Ordinary compatible post-start runtime DML is not classified as deployment DDL.

Remaining ambiguity:
- none

### Mechanical validation record

The final validation checks cover all 19 payload files (15 Markdown documents and four standalone JSON examples), plus the regenerated integrity manifest:

- Four standalone JSON examples parse successfully with duplicate-key rejection; Markdown was scanned for fenced/inline JSON examples (none present). All four example files remain byte-identical to the input pack because their configuration shapes did not change.
- Pack-local filenames, Markdown links/anchors where present, and explicit file/numbered-section references resolve. The source-reference paths are repository anchors, not missing pack files.
- All seven stale-wording families were searched across the complete revised pack; source-only descriptions and distinct email at-least-once semantics were distinguished from target hold recovery.
- The pinned PocketBase SHA remains the same in every recorded baseline location; no newer SHA was substituted.
- PACK-MANIFEST.txt is regenerated from final UTF-8 payload bytes and every payload SHA-256 is verified. The manifest intentionally does not hash itself.
- The new archive has the complete payload plus manifest, passes ZIP CRC validation, and each archived file matches the final working file and its manifest hash.
- The uploaded original ZIP and review remain unmodified; the original ZIP SHA-256 still matches the review.

**Disposition:** Findings #1-#7 are resolved at the documentation/design-contract level. No finding is deferred or left unresolved; implementation and live/release proof remain the required gates stated above.
