# Multi-model Codex Task: Port ASAP from PocketBase to .NET 10

## Mission

Port `clcdpc/asap-pocketbase` from PocketBase/SQLite/Goja to the .NET architecture defined by this documentation pack. The final port is delivered through `codex/csharp-port` and draft PR #264 into `main`, with temporary slice/work-package PRs under the execution policy below. Preserve existing ASAP behavior and frontend UX by default; change behavior only when the specification explicitly requires it or when a concrete technical benefit justifies a documented deviation.

The port is not an opportunity for a general rewrite beyond the backend/platform boundaries described here.

## Source baseline

Staged PRs begin with Slice 6. Beginning with the remainder of Slice 6, thin autonomous supervisor mode is preferred/default unless the user explicitly requests manual/direct execution. A completed bootstrap remains valid and must not be repeated.

Slices 0-5 remain accepted under the policies that actually governed them. Accepted Slice 5 milestone: `36727414d02cbe34ba13cd3f6f1bb57980b83a6f`; exact-SHA CI `34952097695` succeeded. Slice 6 implementation has not started. Verify current branch/PR state and [PORT-STATUS.md](../implementation/PORT-STATUS.md) before starting work. Preserve historical Sol-based execution of Slices 0-3, Slice 4's Luna Max/retained-context/full-review sequence, and Slice 5's Astra-led Luna implementation/full-and-focused Terra review. Do not reopen those acceptances or rewrite their history.

This policy changes execution orchestration only. Architecture, behavioral contracts, slice contents, database/security/migration rules, release/cutover design, review independence and objective test acceptance criteria are unchanged. This documentation transition is not a new accepted product slice and does not launch implementation or a supervisor.

The original baseline procedure remains the source-tracking contract; branch/PR creation below was already completed:

1. Fetch the current `main` and compare it with the pack's reference SHA `150b30b776565194260cc327eeeffdfb46475e81`.
2. If `main` has moved, inspect every intervening change and update the slice packets/behavior map so urgent/current PocketBase fixes are not lost. The pinned baseline contains an unrelated `clc-carousel-manual-import-example/` subtree; explicitly exclude it from ASAP behavior/dependency/migration analysis and remove it from the port branch rather than porting it.
3. Record the starting PocketBase SHA and keep tracking the exact commit deployed to PocketBase production. Do not prematurely create a tag called "final" while emergency fixes can still land; after successful .NET production cutover, create/verify the permanent final-PocketBase tag at the exact PocketBase commit that was frozen for that cutover.
4. Create a dedicated port branch (use a clear name such as `codex/port-asap-to-dotnet` if no branch has already been established).
5. Open a draft PR immediately and use it for the entire port.
6. Freeze ordinary PocketBase feature work. If an urgent PocketBase production fix lands during the port, bring the behavior into the port branch immediately.

## Delivery topology

Keep `codex/csharp-port` as the long-lived accepted-slice integration branch and PR #264 as the single final port PR into `main`. Temporary branches and PRs only bound implementation and review; they are not independent releases.

```text
main
  ^
  | PR #264 (final port PR, draft until final whole-app gate)
codex/csharp-port
  ^
  | slice integration PR
codex/slice-N-<name>
  ^
  | package PRs (optional)
  +-- codex/slice-N-<package-a>
  +-- codex/slice-N-<package-b>
  +-- codex/slice-N-<package-c>
```

Package PRs target the slice integration branch. Slice integration PRs target `codex/csharp-port`. Neither targets `main`. A small cohesive slice uses only `codex/slice-N-<name>` and its PR into `codex/csharp-port`.

Keep one accepted milestone per completed slice on `codex/csharp-port`. Package completion is not slice acceptance; package CI cannot replace integrated slice validation, and slice-branch CI cannot replace the accepted milestone's exact-SHA remote CI. No later slice begins until the current slice is accepted. PR #264 cannot leave draft or merge before the separate final whole-application review and its existing gates.

### Optional package sizing

Use packages only for coherent, independently testable/reviewable boundaries: distinct subsystems, correctness/risk domains or acceptance concerns, or a diff whose bounded review materially improves comprehension. Normally 2-4 packages help a large slice; this is guidance, not a quota. Avoid PRs per file/class/service, arbitrary line-count splits, excessive overlapping branches, inseparable packages and ceremonial splits.

### Remaining-slice planning defaults

These are planning defaults, not binding implementation partitions. Choose actual boundaries from the current implementation at slice start.

| Slice | Default |
| --- | --- |
| 6 - Analytics | One cohesive slice PR; child packages only if discovery shows material benefit. |
| 7 - Migration hardening | Likely 2-4 packages: export/package/core mechanics; validation/import/reconciliation/cutover invariants; legacy-link/operator/rehearsal tooling. |
| 8 - Deployment/release | Possible packages: release artifacts/manifests; deployment/preflight/rollback; health/monitoring/operator concerns. |
| 9 - CI/browser/accessibility/release validation | Possible packages: CI/real-SQL integration; Playwright/accessibility; release-validation/environment controls. |
| 10 - Synthetic seed/reset | One cohesive slice PR. |
| 11 - Legacy removal/final preparation | Choose from actual remaining work at dispatch; do not freeze partitions early. |

## Required model roles

### GPT-5.6 Luna Max - bounded implementation owner

The supervisor dispatches Luna Max in a fresh/bounded child context for complete small slices, individual packages, integration/glue work and confirmed review fixes. Each task ends at a concrete branch/PR/review checkpoint. Luna owns ordinary C#, SQL/DACPAC, migration, frontend, tests, debugging, complete required pre-review validation and directly affected documentation. Successful completion returns only a compact receipt. Luna High may be chosen intentionally, but is neither the default nor a required first tier. Direct Luna tasks remain available in manual mode.

Luna implements the existing contract rather than redesigning it. Do not add repositories, MediatR, generalized workflow or messaging infrastructure, migration frameworks, layering, speculative hardening or other machinery the pack does not require. Resolve routine implementation choices from the applicable contracts and current code.

### GPT-5.6 Terra High - bounded independent reviewer

The supervisor dispatches Terra High in a fresh/bounded child context independent of implementation. Terra reports findings only and never implements fixes. Confirmed findings return to a bounded Luna Max fix task with only the findings and necessary context, followed by focused Terra re-review. A fresh focused Terra context is acceptable; retained model identity is not an acceptance invariant. Use the two distinct review modes below; manual mode preserves the same independence.

#### Work-package review

Question: **Is this bounded component/behavior correct?**

Review the complete package diff, affected callers, relevant invariants, failure paths, concurrency/state implications and directly relevant tests. This is the detailed local review. Begin from the package contract and diff rather than rediscovering the entire port architecture. After bounded fixes, use focused Terra re-review unless the correction materially broadens the regression surface.

#### Slice integration review

After packages and integration/glue are complete and full slice validation passes, use a fresh Terra High context for the COMPLETE slice delta:

- base: previously accepted slice milestone;
- head: exact final slice integration branch SHA.

Question: **Do the reviewed components form one correct implementation of the complete slice?**

This is a holistic review of gaps between packages, cross-package invariants, state-transition composition, authorization/scope composition, SQL/concurrency ordering, migration/runtime agreement, API/frontend agreement, integration regressions, missing acceptance behavior and unnecessary complexity across packages.

Use package review receipts to avoid mechanically repeating line-by-line review of unchanged, independently reviewed code. Inspect deeply when code changed after package review, findings cross packages, integration exposes a concern, shared invariants need direct inspection or confidence otherwise requires it. This review has a different purpose, not reduced rigor. For an unpartitioned slice, the full-slice review includes the detailed implementation review as well as this holistic assessment.

### GPT-6 Astra Max - thin autonomous supervisor

Astra may retain one supervisor context for the entire authorized slice. It operates as a state-machine controller with the binding context firewall below. It owns phase progression, worker dispatch, branch/PR state, compact receipt verification, confirmed finding/fix routing, short acceptance gating, integration, milestone documentation, CI polling and final status. Normal transitions require no new user prompt.

Persistent technical-parent behavior is prohibited: the supervisor does not normally implement, debug, inspect source, conduct detailed review or analyze complete test output. Bounded workers retain technical ownership. Complete any missing bootstrap in a bounded task and consume its packet/receipt; verify an existing bootstrap instead of repeating it. Honor an explicitly bootstrap-only task's stop boundary.

For a genuine product/security/migration/architecture ambiguity, pause progression. The worker returns exact state, governing sources, conflict and required decision. Astra inspects only necessary authoritative material, issues a bounded ruling when the contract resolves it and resumes the relevant worker. If authoritative sources do not resolve a material decision, stop for user input. Ordinary implementation/test failures stay with Luna; their debugging transcripts do not cross into supervisor context.

Acceptance remains SHORT: verify the exact reviewed candidate, clean independent review, no unresolved substantive findings, complete current integrated validation, allowed documentation-only changes since review, branch/PR state and acceptance invariants. Do not redo Terra review or rerun successful expensive gates. Record the exact reviewed implementation SHA separately from later acceptance/status commits. Final acceptance still requires successful CI on the exact candidate milestone SHA after integration and any required status commit.

### Sol - exceptional specialist consultation only

Sol High/XHigh remains outside the normal lifecycle. After a concrete unresolved issue has been identified, bounded Astra contract analysis may seek focused specialist advice where it materially helps. Sol XHigh is exceptional when bounded Sol High advice leaves a blocker or exceptional cross-system reasoning is needed. Astra issues the ruling; Luna implements/tests and Terra verifies. Size, migration, SQL, concurrency or security sensitivity alone do not justify Sol.

## Supervisor context firewall

This is a binding efficiency rule. Retained supervisor context consists primarily of current state, exact SHAs, PR numbers, compact worker receipts, confirmed findings, acceptance state, CI state and the fix-cycle count. Never accumulate or reproduce worker transcripts, complete logs, full patches, broad source dumps, browser traces or migration dumps. The repository and retained evidence remain authoritative for details.

Successful workers return a short structured receipt with the gates appropriate to their task. Illustrative shape only; these counts are not acceptance evidence:

```yaml
phase: implementation_complete
branch: codex/slice-N-name
head: <exact-sha>
build: { result: pass, warnings: 0, errors: 0 }
dotnet_sql: { passed: 318, failed: 0, skipped: 0 }
frontend: { passed: 181, failed: 0 }
browser: pass
axe: { serious: 0, critical: 0 }
native: pass
publish: pass
docs: pass
artifact_hash_receipt: <path>
evidence: <local-evidence-path>
known_issues: none
next: full_terra_review
```

The supervisor normally checks SHA, branch/PR, required gate presence, pass/fail/skip counts, artifact/hash receipts where required and clean status. Read raw evidence only for a failed gate, conflicting counts/hashes, an incomplete receipt, an acceptance invariant that cannot otherwise be established or a material security/concurrency/migration concern requiring specific evidence. Do not reread successful logs merely for confidence.

The supervisor may inspect a small source area only for a genuine contract ambiguity, conflicting worker reports or a narrow acceptance invariant not established by receipts. Terra owns routine code review. Workers use branch/PR diffs, root instructions, relevant packets and contracts; expand context only for concrete concerns and inspect current code before editing. Keep successful handoffs compact: repository/branch/PR, base/head, completed/remaining scope, rulings, findings, validation and next action. Do not attach lengthy implementation narration.

Context rotation does not force checkout/worktree rotation. Preserve required local-only fixtures, harnesses and evidence. If an environment change is unavoidable, explicitly recreate and rerun unavailable required evidence before relying on it; do not claim evidence that cannot be inspected.

## Validation and evidence placement

| Stage | Required validation |
| --- | --- |
| Package | Clean relevant build, directly affected tests, real SQL for persistence/concurrency, affected frontend/browser/migration tests, `git diff --check`, and automatic CI where configured. Run the whole slice browser/native/publication matrix only if the package invalidates it. |
| Integrated slice | The COMPLETE existing slice pre-review/acceptance matrix on the integrated source/artifact; package validation never substitutes. |
| Review fixes | Narrowest sufficient affected gates first; broaden when changed bytes invalidate earlier evidence. |
| Accepted milestone | Actual successful remote CI associated with the exact resulting SHA on `codex/csharp-port`; package or slice-branch CI never substitutes. |

Full integrated validation retains all required Release builds, complete .NET/real-SQL and Node/frontend suites, browser/axe, native migration/export/import/reconciliation, publication/exclusion and DACPAC/source/artifact checks, plus applicable release gates. This policy does not change the test acceptance criteria.

Retained evidence records exact commands/results, passed/failed/skipped counts, source/artifact/DACPAC hashes, log paths and warnings/errors. Compact receipts identify that evidence and every required gate for the intended source/artifact. Supervisor inspection follows the context firewall; efficiency never waives objective validation.

## Binding architecture

Use the relevant authoritative contracts under the context rules above. The following remain non-negotiable unless a blocking technical fact proves otherwise:

- .NET 10 / ASP.NET Core 10 / SQL Server 2022 compatibility 160 / IIS.
- Simple solution: `Asap.Web`, `Asap.Database`, `Asap.Migration`, `Asap.Tests`.
- Feature-oriented code in one web project; EF Core direct feature services; Dapper/ADO only targeted heavy paths; no repositories/MediatR/layer explosion.
- DACPAC schema source of truth; `[asap]`; Hangfire `[HangFire]` outside DACPAC and provisioned/upgraded by the deployment operator so the runtime identity needs no schema DDL rights.
- Exact application SchemaVersion startup contract plus independent DACPAC hash and actual Hangfire/dependency schema change/compatibility detection. Any application or dependency DDL against an existing production database requires stopped workers and a verified backup before DDL/files; only proven file-only deployment skips SQL backup and DB writes. Do not give runtime schema rights or move Hangfire objects into the DACPAC.
- Entra OIDC + local StaffUser allowlist/roles; durable identity is (`tid`,`oid`) only. Authentication tickets retain the sign-in tuple and every authenticated request requires exact equality with the current StaffUser binding and membership in the currently loaded AllowedTenantIds, so explicit rebind invalidates old cookies. `UserPrincipalName`/`DisplayName` are refreshable readable Entra metadata; `NotificationEmail` is separate app-owned nullable contact data and never refreshes from sign-in; real Entra for Development/F5.
- Keep staff-account lifecycle separate from library participation: staff/admin reference one non-system Organization even when it is inactive; authorization requires both StaffUser and Organization active; library deactivation must not rewrite `StaffUser.IsActive`; library reactivation restores eligibility for already-active staff; explicit StaffUser reactivation while its library is inactive returns a conflict. Role/organization mutations that contract or move authorization scope are atomic lifecycle operations: deactivate out-of-scope active `FormatAutoClaimRule` rows, clear out-of-scope open TitleRequest claims with events, clear open AdditionalCopy claims with system Notes/admin-audit counts, and preserve closed history. StaffUser is the common serialization point with claim/rule writers, which must lock/re-read current target eligibility before commit. Promotion to super-admin broadens scope and needs no cleanup.
- Preserve every current StaffUser preference (`weekly_action_summary_enabled`, `weekly_action_summary_email`, `purchase_reminder_default`, `additional_copy_reminder_default`, `default_mine_unclaimed_filter`) through SQL, migration, profile DTO/API, frontend behavior, and tests.
- Enforce the common current staff-eligibility predicate from `01-PORTING-SPEC.md` section 7.6 on every authenticated staff request, sensitive-mail delivery, usable-super-admin count, and candidate external configuration. Serialize any StaffUser mutation reducing usable-super-admin eligibility using `ASAP:ActiveSuperAdminInvariant` before row locks and re-read; return 409 if zero would remain. Removed-tenant cookies must fail next use after restart with the same keys; configuration cannot silently strand the installation without a usable administrator.
- Use one SQL lock order whenever multiple row categories are needed: `Organization -> StaffUser -> TitleRequest/AdditionalCopyRequest -> dependent claim/rule/operation rows`, with stable key ordering inside a category. Use short `UPDLOCK,HOLDLOCK`/equivalent transactions and never hold them across Entra/Polaris/Postmark calls.
- Testing-only auth handler registered only in `Testing`.
- Enforce `01-PORTING-SPEC.md` section 14's one recipient-domain predicate through the existing outbox: `Environment.IsNonProduction` independently governs all Postmark recipients, with case-insensitive exact matching, explicit-only subdomains, missing/empty-list suppression, malformed-config rejection, terminal `recipient_domain_not_allowed` suppression without business rollback, and preserved deterministic idempotency. Production does not apply the restriction; testing authentication remains controlled only by `ASPNETCORE_ENVIRONMENT=Testing`. Complete `06-TESTING-CI.md` section 4.1 and `08-RELEASE-VALIDATION-NOTES.md` section 6 without requiring a live Postmark send.
- Patron opaque 1-hour bearer tokens with SHA-256 token hash in SQL; no persistent PatronUser profile. Every patron-authenticated request must also require the session's effective Organization to remain active. Final session insertion serializes against library deactivation on that Organization row; deactivation transactionally revokes matching sessions, and reactivation never resurrects them.
- `Clc.Polaris.Api` and `Clc.Postmark.Api` own protocol clients.
- Polaris system/application credentials for all initial PAPI calls; no per-staff Polaris identity machinery.
- Reusable Polaris/Postmark secrets are Data Protection ciphertext in SQL; production/nonproduction use separate key rings and separate least-privilege runtime service accounts, never `db_owner`.
- `13-SETTINGS-SCOPE-INVENTORY.md` is normative for configuration scope/storage/inheritance/reset/migration. Do not create a catch-all `Settings` or EAV table. Use system-only `SystemSettings`/`PolarisSettings`, field-inheritable `WorkflowSettings`/`PatronSettings`/`EmailSettings`, the specified relational whole-set/provider/custom-field/custom-field-rule tables, and the specialized branding/template/material-format/auto-claim models. Organization `1` is the real system/default scope.
- **Reset inherited overrides** must preserve library-owned custom fields, custom formats, custom rejection templates, and auto-claim rules/history.
- Durable SQL email outbox + Hangfire with exactly `pending`/`sending`/`sent`/`failed`/`suppressed`: every expired `sending` lease is potentially transport-ambiguous; the provider call starts within 30 seconds of claim and has a 30-second complete-operation timeout; the lease is two minutes; reclaim/retry begins only after expiry; and final writes compare `Status`, expected `LeaseId`, and post-claim rowversion/equivalent ownership so late workers cannot overwrite newer attempts. Preserve explicit at-least-once semantics with no exactly-once claim, `failed` manual retry with payload retained, payload purge only for terminal `sent`/`suppressed` after 90 days, and missing enqueue-time notification configuration represented non-fatally rather than rolling back business state. Authorization-sensitive staff mail stores recipient/scope/original Entra tuple plus `RecipientAddressKind=notification_email|weekly_summary` and revalidates current binding/allowed-tenant/authorization plus the kind-specific effective address against `ToAddress` immediately before every send/retry; stale authorization/address becomes terminal suppression.
- Inactive organizations gate participation-dependent business automation and library-scoped Run Now work. Immediately before a participation-dependent local result or external-operation acquisition, lock/re-read the owning Organization first and require it active; no new identifier/promotion/hold/fulfillment/timeout mutation or ordinary library summary begins after deactivation wins that serialization point. Already-acquired hold recovery continues even while the Organization is inactive; this cannot authorize a new operation. System/infrastructure jobs continue; immutable committed business-event mail may drain, while authorization-sensitive staff mail suppresses if current scope is lost. Weekly summaries follow recipient authorization scope: staff/admin own active library only; super-admin active consortium-wide.
- Staff recipient contract: `NotificationEmail` is the nullable primary ordinary-notification destination; `WeeklyActionSummaryEmail` is a weekly-summary-only optional override/fallback. Persist which rule applies on each authorization-sensitive outbox row rather than infer it from its business key. Reject legacy `@staff.asap.local` placeholders, preserve migration precedence, permit authorized Staff Access to clear the primary email, never repopulate it from Entra sign-in, and emit migration reconciliation deltas for newly eligible weekly-summary users and every ordinary-recipient change. The target fallback normalization is deliberate; migration still starts with an empty outbox.
- Database-enforce idempotent outbox creation with a filtered unique index on non-null `EmailOutbox.BusinessKey`; deterministic keys are globally namespaced, ordinary weekly staff summaries are idempotent per recipient/reporting period, and duplicate-key races resolve as successful idempotent enqueue attempts. Preserve explicit forced weekly-summary resend by generating one durable `ManualRunId` per accepted forced invocation and using force-run recipient keys; retries of that same run reuse the ID, while a later force is a new business event.
- Hold placement is DB-guarded by one active `HoldPlacementOperation` per TitleRequest across every entry point before any Polaris mutation. Acquisition follows Organization -> TitleRequest locking and becomes a durable mutation barrier: status/close/reopen, delete, identifier, BIB, AutoHold, and recorded pickup changes cannot commit behind an incomplete operation. The pickup action checks the barrier before Polaris; hold placement re-resolves live pickup immediately before its call. Deactivation after successful acquisition does not cancel the already-started operation, which must reconcile safely.
- Apply the stage-aware identifier matrix in `07-API-FRONTEND-COMPATIBILITY.md` section 14.2: only legal pre-placement changes invalidate old result/retry/error/check/tag/BIB authority; changed/cleared placed/closed identifiers are rejected, unchanged normalized inputs are no-ops, combined BIB edits cannot bypass protection, and successful/legacy placed BIB remains the title authority for positive checkout through reopening; terminal unclaimed/cancelled/expired closure additionally requires the particular tracked final HoldRequestID under porting-spec section 9.2. A retained historical BIB marker never supplies that identity. SQL still rejects canonical found without a BIB.
- Explicit C# workflow transitions/current-state truth; append-only request events; no workflow engine/event sourcing.
- Stable relational material formats/rules/tags as specified; built-in material-format field behavior uses typed columns, custom-field per-format rules are relational, and there is no competing legacy `patronFormatRules` JSON source.
- Preserve vanilla frontend and Grid.js; exact existing browser library versions vendored locally; no Node/npm in app build/publish/runtime.
- Preserve internal API contracts/routes by default, with explicit rowversion/HTTP/auth changes.
- Real SQL tests, Playwright critical journeys, axe-core serious/critical gate.
- Migration direct from stopped PocketBase SQLite/file storage -> normalized JSON package -> SQL import/reconciliation; migration ZIP is self-contained `win-x64`, active StaffUsers receive explicit operator-supplied Entra tenant/object-ID mappings rather than UPN-derived identity, inactive organizations preserve staff/history FKs without forcing StaffUser deactivation, every populated source setting must map through `13-SETTINGS-SCOPE-INVENTORY.md` or an explicit intentional-drop rule, legacy SMTP transport credentials are never converted to Postmark credentials, the target Postmark token is supplied separately through secure target-only import input and persisted only as protected ciphertext, staff recipient changes are reported old-versus-target, and every legal legacy `isbnCheckStatus` is handled by the exhaustive conditional map: both `found` and `found_in_polaris` require supporting BIB state or block/report. After StaffUser import, hard-gate on at least one active super-admin with a valid allowed-tenant durable Entra binding; if none exists, `Asap.Migration` explicitly provisions/promotes the configured bootstrap identity while the app remains stopped, reports it, and re-runs the gate. Never rely on ordinary startup bootstrap after StaffUser rows exist. Source-active auto-claim rules remain active only when the migrated assignee exists, is active, and is scope-eligible; otherwise preserve them inactive without substitution and report the normalization, using nullable inactive-rule `StaffUserId` only when the source assignee no longer maps. `PreferredPickupBranchId`/`PreferredPickupBranchName` are current request pickup fields mutable only through the dedicated validated pickup workflow.
- Migration freezes two distinct parity artifacts: `effective-legacy-runtime-config.json` only for system/global SQL-bound values that require DB/environment/code fallback resolution, and `effective-legacy-operational-config.json` for legacy cron schedules plus global/timeout/queue-specific processing limits. Reconcile both before cutover; library-scoped SQL configuration stays in the domain export files. Explicitly test the persisted-blank/environment-derived Staff URL case and its `ASAP_STAFF_URL`/`ASAP_PUBLIC_URL`/initialization-helper `ASAP_BASE_URL` behavior.
- Hard reconciliation gate: no unexplained discrepancies.
- One full offline production migration window; no delta sync.
- Permanent nonproduction stays PocketBase until the .NET implementation is complete and a merged/tagged release candidate is ready.
- Exact tagged artifact that passes the final permanent-nonproduction rehearsal is the production artifact.

## Binding closure-remediation implementation requirements

Implement the following within the existing slices and models, not as another architecture review:

1. Complete `HoldPlacementOperation` exactly as `01-PORTING-SPEC.md` section 9.1: owner token/epoch and bounded lease, durable one-way create/reply markers, persisted provider GUID/qualifiers before reply, evidence-classified terminal outcomes, observation-only uncertainty, and super-admin operator resolution. Recovery runs before business phases and includes already-acquired inactive-library work; no new inactive-library acquisition or unsupported replay.
2. Enforce AdditionalCopy retained-claim validation under Organization -> StaffUser -> task locking on reopen and inherited-claim creation. Clear invalid effective fields, preserve Notes history, and do not auto-assign. Import both request types with map-then-current-eligibility checks, all conversion reasons/counts, valid inactive-organization relationships, and preserved closed history (`04-MIGRATION-CUTOVER.md` section 6.3).
3. Use the same current identity/binding/tenant/activity/scope predicate everywhere named above; persist the sensitive-mail recipient tuple and suppress stale authorization. A re-added tenant does not undo independent revocation/rebinding/scope changes. No Graph/session-store/key-rotation solution.
4. Implement the finite `QueueProgress` cycles in section 23.1 of the porting spec: per queue/scope scalar keyset, fixed ID watermark, post-durable-outcome checkpoints, wrap/restart/deletion/backdated insertion/failure semantics, and the existing non-overlap guard. Keep sequential PageSize/MaxPerRun limits, eight configured queue keys/seven schedules, and a separate bounded HoldRecovery phase reusing HoldPlacement limits. Later phases must enforce only applicable status-specific timeout guards from porting-spec section 23.2, never an OutstandingTimeout expiry for outstanding_purchase.
5. Use the database-changing classifier and failure/restart rules from `05-DEPLOYMENT-OPERATIONS.md` sections 9-10, including Hangfire-only DDL and compatibility-aware rollback.

6. Implement OutstandingTimeout as unreviewed-suggestion creation-age auto-rejection, with strict injected-clock boundary, ordinary participation/version/barrier checks, and the configured timeout rejection-email/template behavior. An old outstanding_purchase awaiting BIB is not expired by this setting. Preserve all four explicit family predicates and existing field/queue names; fair scan ordering cannot redefine age.
7. Apply porting-spec section 9.2 to new placement, runtime existing-hold adoption, and fulfillment. Capture authoritative final HoldRequestID on the existing HoldPlacementOperation when available, separately from RequestGUID/reply qualifiers. Only the tracked hold plus expected BIB/patron can authorize unclaimed/cancelled/expired closure; historical same-BIB rows, ambiguous identity, and provider failures preserve state with diagnostics. Preserve positive title-level checkout and normal manual/timeout closure. Do not invent a final ID, replay a successful mutation to obtain one, or add a second journal.
8. Implement migration section 6.10's exhaustive normalized placement evidence, including all five hold-terminal close reasons and status_changed -> hold_placed adoption history; preserve deterministic provenance and known/null BIB in the existing legacy marker without fabricated operations/provider success/IDs. Protect across reopen, commit, then separate identifier/BIB edit; API capabilities and backend agree. Reconcile explicit protection/evidence/ambiguity counts and identical equivalent fresh-target imports.

All R1-R7 cases in `06-TESTING-CI.md` section 10.1 and F1-F3 cases in section 10.2 are blocking. `14-REMEDIATION-AUDIT.md` maps each finding to contracts and tests. Preserve prior decisions and the pinned SHA; do not add unrelated abstractions, product changes, or deferred hardening.

## Slice lifecycle and review fixes

### Normal state machine

```text
BOOTSTRAP -> IMPLEMENTATION -> REVIEW_CANDIDATE -> FULL_REVIEW
  -> [FIX -> FOCUSED_REREVIEW]* -> ACCEPTANCE -> INTEGRATION
  -> ACCEPTANCE_RECORD -> EXACT_SHA_CI -> ACCEPTED
```

The supervisor advances automatically when each state's objective gates pass. IMPLEMENTATION includes any package loops and the complete integrated pre-review validation. REVIEW_CANDIDATE requires a pushed exact SHA and complete compact receipt. FULL_REVIEW always uses fresh independent Terra context. A required full re-review replaces the focused review step when the broadening rules apply. Preserve phase, reviewed/candidate/milestone SHAs and cycle count across context rotation; do not reset the budget by rotating workers.

An explicitly authorized supervisor run includes normal worker dispatch, fixes, validation, review, acceptance, slice integration, status commit where required and CI waiting. It ends at ACCEPTED or a hard-stop report. A documentation-only policy task does not launch that run. Stop after the authorized slice; starting the next slice requires explicit cross-slice continuation authorization as well as acceptance.

### Large slice

1. Verify the exact accepted prior milestone and its successful exact-SHA CI. Create `codex/slice-N-<name>` from that accepted integration state and record the base SHA. If an explicitly authorized documentation-only transition follows the accepted product milestone, retain it in the branch base and record both the current integration HEAD and prior accepted review-base SHA; verify there is no implementation/test drift. This policy transition does not become a new accepted product slice.
2. For each coherent package, normally sequentially: branch from the current slice branch; dispatch bounded Luna Max; run package-appropriate validation; commit/push; open/update its PR into the slice branch; dispatch independent Terra detailed package review; route confirmed fixes to Luna and focused re-review to Terra; merge the clean package. The supervisor verifies compact receipts at each checkpoint. Dependent packages start from its updated state; parallel packages require actual independence.
3. Dispatch bounded Luna Max for remaining integration/glue if needed. Luna completes every slice requirement and runs the FULL existing integrated slice pre-review matrix before pushing the final candidate and returning its receipt. Package gates never replace this matrix.
4. Dispatch fresh Terra High holistic review from the previously accepted product milestone to the exact final slice branch SHA. Route fixes and appropriate re-review automatically under the safety cap below.
5. Perform short Astra acceptance on the clean reviewed SHA and current receipts. Implementation changes after review require affected validation/re-review; allowed acceptance/status documentation alone does not require another full code review.
6. Merge the reviewed slice PR into `codex/csharp-port`, complete ACCEPTANCE_RECORD and EXACT_SHA_CI as below, then mark ACCEPTED only on successful exact-milestone CI. Stop unless cross-slice continuation is explicitly authorized.

If integration state or reviewed implementation changes unexpectedly before merge, stop and report the actual base/diff/evidence mismatch; do not accept an unreviewed merge. Do not create child packages merely to exercise this machinery.

### Small cohesive slice

1. Verify completed bootstrap or complete it once if missing; retain the slice branch/PR into `codex/csharp-port`.
2. Dispatch one bounded Luna Max task for the complete slice, tests, ordinary debugging and full integrated pre-review matrix. Luna pushes the exact review candidate and returns a compact receipt.
3. Verify receipt/state and dispatch fresh Terra High full-slice review, including detailed implementation and holistic review.
4. On confirmed substantive findings, dispatch bounded Luna Max with only those findings and necessary context; run directly affected validation, expanding when evidence is invalidated; dispatch focused Terra High re-review or required full re-review. Continue automatically within the cap. On a clean result, proceed.
5. Perform short Astra acceptance, merge the slice PR into `codex/csharp-port`, record acceptance/status documentation, commit if required, and push.
6. Wait/poll for CI on the exact candidate accepted milestone SHA. On success, mark accepted in PR metadata and stop; on failure follow the hard-stop rules. The user does not launch each phase individually.

### Acceptance record and exact-SHA CI

After integration, use the existing status/review/handoff documentation pattern. If a documentation-only acceptance/status commit is required, create it on `codex/csharp-port` and push; its SHA becomes the candidate accepted milestone. Otherwise use the slice merge SHA. Record the exact Terra-reviewed implementation SHA separately from the integration SHA and any later status commit. Label final CI pending until it succeeds; neither a review candidate nor the merge alone certifies acceptance.

Changes limited to allowed review/acceptance documentation, with no implementation/test/schema/runtime behavior change, do not require another full code review. Verify the changed-file scope and documentation integrity. Any implementation change invalidates the relevant validation/review evidence and must go through the required gates.

The supervisor owns CI waiting. Poll at reasonable intervals (normally 30-60 seconds, backing off while unchanged), without repeatedly downloading logs for running jobs. On success consume the conclusion and required compact step/count evidence. Only actual successful CI associated with the exact candidate milestone SHA completes acceptance; package, slice-branch, earlier merge or earlier status-commit CI cannot substitute. Record the accepted SHA/run/result in PR metadata, without another repository commit merely to record green CI.

### Review-loop safety cap and failure handling

Default maximum: **3 fix/re-review cycles after the initial full slice review**. A full re-review required by broadening counts as a cycle. If the third cycle still leaves a substantive finding, repeated regression, an expanding review surface or a problem that cannot be bounded confidently, stop the supervisor and report unresolved findings/state. Never accept the slice or waive a finding because the cap was reached. This operational stop limit does not change the separate final whole-app review rules.

- Ordinary implementation/test failure: Luna diagnoses and corrects it inside its bounded task. Return a compact blocker receipt if the task cannot finish; the supervisor does not ingest the debugging transcript or repeatedly redispatch unchanged work.
- Contract ambiguity: pause, inspect only necessary authority, issue a bounded Astra ruling and resume when resolved. Stop for user input when a material decision remains unresolved by authoritative sources.
- Infrastructure failure: an obviously transient external/CI operation may be retried once when safe. Do not relabel substantive application/test failures as infrastructure failures. Stop if the retry fails or safe retry is unavailable.
- Exact accepted-milestone CI failure: substantive failure means NOT accepted. Stop with failed SHA/run/step and a concise corrective recommendation; do not weaken CI or start the next slice. A clearly transient GitHub/runner failure may be retried once.
- Unexpected branch/PR/implementation state, unavailable worker dispatch or an unresolved receipt/evidence mismatch prevents progression. Report the checkpoint and missing decision/capability/evidence rather than guessing acceptance.

### Manual/direct fallback

Use direct bounded tasks when the user explicitly wants phase-by-phase control, supervisor dispatch is unavailable, orchestration itself needs diagnosis or an unusual partial state benefits from manual resumption. State that fallback explicitly. Manual mode keeps the same Luna responsibilities, Terra independence, validation, review/broadening rules, short acceptance, cycle stop limit and exact-SHA CI. Manual phase handoffs are not required in normal autonomous mode.

### Re-review scope and blocking findings

Focused re-review covers corrections, affected callers/invariants, regression surface, changed tests and interactions among fixes. Require another full holistic review when a correction materially broadens the surface: systemic shared infrastructure, broad authorization or concurrency/locking changes, migration architecture changes, several unrelated areas changed, a new unrelated substantive defect discovered in focused review, or a regression surface that cannot be bounded confidently.

Do not require ceremonial full passes for narrow corrections. Repeat fixes/re-review within the operational cap; reaching it stops work without acceptance and never permits a known blocking correctness, security, data-integrity, migration, authorization, concurrency or material-regression defect. Only nonblocking leftovers may be deferred. Package and slice reviews supplement the separate final whole-application review.

The user-authorized `../implementation/temporary-email-transport.md` decision remains binding: `FileEmailSender` substitutes only the final provider boundary. It does not weaken the durable SQL outbox, authorization-sensitive recipient checks, recipient-domain safety, or lease/fencing/idempotency/retry behavior; it does not simulate Postmark webhook/provider success or become another subsystem. Real Rest 3-compatible cancellable `Clc.Postmark.Api` integration and required provider/webhook/transport tests remain release/rehearsal blockers. This model-policy update executes no implementation or release validation and starts no slice.

## Required implementation slice sequence

Follow `02-IMPLEMENTATION-PLAN.md` as the canonical sequence:

- **Slice 0:** freeze/branch/skeleton/engineering baseline, including removal/exclusion of the unrelated carousel-example subtree and DeploymentState/DACPAC-hash foundation.
- **Slice 1:** patron login -> title request submission.
- **Slice 2:** core staff Entra + request workflow.
- **Slice 3:** additional-copy workflow.
- **Slice 4:** administration/configuration.
- **Slice 5:** background workflows + complete email operations.
- **Slice 6:** analytics.
- **Slice 7:** migration hardening + legacy-link compatibility.
- **Slice 8:** deployment/health/monitoring/release artifacts.
- **Slice 9:** CI/Playwright/accessibility/release validation integration.
- **Slice 10:** end-of-port explicit synthetic seed/reset tooling.
- **Slice 11:** remove legacy PocketBase implementation, rewrite canonical docs and repository-level `AGENTS.md`, and run final review. Before the port PR merges, remove obsolete PocketBase-only agent instructions, retain/adapt general simplicity/scope, settings-scope, DOM-safety, accessibility, and behavioral-testing guidance, and make `AGENTS.md` describe the completed .NET repository, including its DACPAC/EF Core/Dapper/ADO.NET/SQL rules, rather than a planned future port.

Migration export/import/reconciliation code is developed **alongside** every data-owning slice; the migration-hardening slice consolidates and productionizes it rather than starting it from scratch.

## Per-slice completion invariant

At each milestone the .NET branch must build, tests must be green, and all features already claimed as implemented must work end-to-end. Do not leave deliberately broken placeholders. Features scheduled for later may be absent/disabled.

## Commit policy

Keep coherent work-package and slice changes together. Bounded implementation workers may commit/push and open the temporary PR required by their authorized checkpoint. The authorized supervisor advances clean package PRs into their slice branch and the reviewed, Astra-approved slice PR into `codex/csharp-port`. Manual mode performs the same gated operations through direct tasks. Do not manufacture tiny PRs per file or mix unrelated slices.

A review-candidate checkpoint is pushed on the package/slice branch after its applicable pre-review gates as part of an authorized autonomous run or direct task. Label its scope and exact SHA `implemented / ready for independent review`, state which review has not run, and never call it accepted. The supervisor proceeds with the corresponding package/holistic review and acceptance gates. Candidate CI cannot authorize progression.

The candidate accepted milestone is the integration-branch head after the slice merge and any required documentation-only acceptance/status commit. Only its own successful exact-SHA remote CI completes acceptance. Keep the reviewed implementation SHA distinct and record the final SHA/run result in PR metadata or an acceptance conversation record without creating an extra unvalidated repository commit merely to record CI. Policy/documentation maintenance commits are not new accepted product slices.

## Final whole-app review

After legacy cleanup and all deterministic tests are green, use fresh independent Terra High whole-app review passes, dispatched by the supervisor or run directly in manual mode. Ask: **Do all accepted slices form one correct, secure, migratable, deployable replacement system?** Work-package and slice reviews do not replace this final adversarial gate. Vary focus across correctness/integration, failure paths, migration/data, concurrency, APIs/contracts, security, performance/resources, maintainability/coupling, deployment/recovery, and test adequacy.

Stop after **3 consecutive passes with no new substantive finding**, maximum **6 passes**. Maintain a running confirmed-finding set so later passes do not repeat old issues. Fix all blocking findings regardless of pass count.

## Merge, tag, rehearsal, production

The PR must include everything required for real cutover:

- complete app;
- DACPAC;
- migration console + transforms/reconciliation;
- deployment PowerShell;
- health/diagnostics;
- PRTG sensor;
- CI/Playwright/accessibility;
- external config example/docs;
- complete recurring-job/processing-limit contract from the pack: one non-overlapping hourly ordered workflow orchestrator; one canonical five-minute identifier processor with explicit Found/DefinitiveNotFound/TransientFailure/OperationalFailure classification, five scheduled transient attempts/no extra backoff, reset rules and manual terminal recovery; exhaustive migration handling for every legacy ISBN status; Sunday 20:00 weekly summaries generated per recipient authorization scope plus explicit forced runs with durable `ManualRunId`; inactive-library participation gating for business jobs/manual runs; continued system/infrastructure jobs and immutable business-event outbox draining with authorization-sensitive staff suppression; outbox/cleanup jobs; and global/timeout/queue-specific processing-limit precedence, all using the configured business timezone;
- release artifact/manifest generation;
- migration and deployment/cutover runbooks.

Do not merge a "code complete, ops later" port.

After the final whole-app review:

1. Record the exact commit currently deployed to PocketBase production and confirm it has no urgent fix missing from the port.
2. Merge the complete port to `main`.
3. Create the intended production version tag from the merged commit.
4. Build immutable app/deployment and self-contained `win-x64` migration artifacts from that exact tag.
5. Perform the full permanent-nonproduction PocketBase -> SQL migration and IIS deployment rehearsal using those exact artifacts, including the explicit staff Entra identity map and fresh-target checks.
6. Exercise critical workflows, scheduled Hangfire jobs, migration reconciliation, health/diagnostics, Entra, Polaris, and safe nonproduction Postmark behavior.
7. If a blocking defect is found, fix it on `main` through the normal reviewed process, create a **new tag/artifact**, and repeat the exact-artifact rehearsal.
8. Only an artifact that passes this rehearsal is eligible for production.
9. Stage the same artifact on production; perform real-hostname/Entra preflight only against a disposable production-preflight SQL database, destroy it afterward, then execute the documented offline cutover with the final target kept fresh and the app pool stopped until migration/reconciliation succeeds.
10. At the successful cutover, record the exact frozen PocketBase commit and create/verify the permanent final-PocketBase tag at that commit. Once .NET accepts production writes, never start the retired production PocketBase deployment as-is: retain it only for forensic/reference use, prefer direct database/file inspection, and permit execution only from an isolated copy with outbound Polaris/email access blocked and recurring production jobs disabled. It is never parallel read/write or fallback production.

If PocketBase production needs a critical fix after step 2 but before production cutover, branch and/or tag temporarily from its exact last deployed PocketBase commit; do not create a permanent PocketBase branch. Test/deploy through the normal emergency process, immediately port the equivalent behavior into .NET `main`, build a replacement versioned artifact, and repeat the exact-artifact rehearsal. If source schema, stored-data semantics, migration input, or a migration-tool assumption changes, update the migration contract/tooling before the repeated rehearsal. Remove temporary hotfix branches after successful cutover; the permanent final tag must identify the actual last frozen PocketBase commit.

## Scope control

Whenever you discover cleanup/modernization that is not needed for correctness or cutover, add it to `09-DEFERRED-FOLLOWUPS.md` instead of expanding the port. In particular do not opportunistically add frontend frameworks, dependency major upgrades, server-side paging, an external secret-vault product beyond the required Data Protection SQL-secret protection, distributed caching, automatic CD, or generalized migration/workflow frameworks.
