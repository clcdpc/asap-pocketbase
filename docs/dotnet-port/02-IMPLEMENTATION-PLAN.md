# ASAP .NET Port Implementation Plan

## 1. Delivery model

The port delivers through one long-lived accepted-slice integration branch, `codex/csharp-port`, and one final draft PR (#264) into `main`. Beginning with Slice 6, temporary slice integration PRs target `codex/csharp-port`; optional coherent work-package PRs target their slice branch. Neither targets `main`, and neither is an independent release. [Document 10](10-CODEX-MULTI-MODEL-TASK.md) is authoritative for topology, thin autonomous supervision, bounded workers, package sizing and review/acceptance mechanics.

The existing PocketBase application remains in place, unchanged, as a behavior/reference implementation until the final cleanup slice. Do not move it to a temporary legacy directory. Freeze ordinary PocketBase feature development during the port; urgent production fixes are allowed but must be deliberately copied/ported into the .NET branch immediately.

At branch start, record the exact PocketBase baseline SHA and keep tracking the exact commit deployed to PocketBase production. The permanent **final PocketBase** Git tag is created/verified after the successful .NET production cutover and must point to the exact PocketBase commit that was frozen for that cutover, including any emergency fix after the .NET merge. This is the long-term historical snapshot; do not maintain a permanent PocketBase branch.

Keep final PR #264 draft and current throughout the work; it cannot leave draft or merge before the final whole-application review and existing completion gates.

## 2. Definition of a completed slice

Every slice must end with:

- solution builds cleanly;
- all existing relevant tests pass;
- new tests cover the slice's behavior;
- real-SQL integration coverage exists for persistence/constraints where relevant;
- critical browser behavior has Playwright coverage where relevant;
- implemented functionality is usable end-to-end;
- functionality not yet ported is absent/disabled rather than knowingly broken;
- PocketBase behavior differences are documented and intentional;
- migration export/import/reconciliation logic for the slice's data is implemented and tested;
- reviewer findings are resolved under the review gate below;
- the reviewed slice PR is integrated only after its validation/review gates pass; the integration-branch milestone after any required acceptance/status commit is accepted only after its own exact-SHA remote CI succeeds.

## 3. Thin autonomous execution

Staged PRs begin with Slice 6. Thin autonomous supervisor mode is preferred/default for the remainder of Slice 6 and later slices unless the user explicitly requests manual/direct execution. A completed bootstrap stays valid.

Slices 0-5 remain accepted under their actual historical policies. Slice 5's accepted milestone is `36727414d02cbe34ba13cd3f6f1bb57980b83a6f`, with successful exact-SHA CI `34952097695`. Slice 6 implementation has not started. Keep those acceptance records in [PORT-STATUS.md](../implementation/PORT-STATUS.md). This documentation transition is not a new product slice and launches no supervisor or workers.

[Document 10](10-CODEX-MULTI-MODEL-TASK.md) defines the authoritative execution, context, evidence and escalation rules:

- Run one GPT-6 Astra Max thin supervisor for the authorized slice. It advances phases, dispatches workers, verifies compact receipts, manages branch/PR state, routes fixes, performs short acceptance, integrates, records the milestone and polls exact-SHA CI without normal phase-by-phase user prompts. It stops at acceptance or a hard-stop report; cross-slice continuation requires explicit authorization.
- Dispatch GPT-5.6 Luna Max in fresh/bounded child contexts for small slices, coherent packages, integration/glue and confirmed fixes. Luna owns ordinary C#, SQL/DACPAC, migration, frontend, tests, debugging, complete required pre-review validation and affected docs. Luna High remains optional.
- Dispatch independent GPT-5.6 Terra High for detailed package review and fresh holistic slice review; it reports findings only. Confirmed findings go to bounded Luna fixes and focused Terra re-review. Reviewer identity retention is not an acceptance invariant.
- Astra may remain alive as a state-machine supervisor, but persistent technical-parent behavior is prohibited. Enforce document 10's context firewall: retain state/SHAs/PRs/compact receipts/findings/CI, not transcripts, full logs, patches or broad source. Technical inspection is limited to bounded contract escalation and otherwise unestablished narrow acceptance invariants.
- Sol High/XHigh is exceptional focused specialist consultation after a concrete unresolved issue, outside the normal lifecycle. Size, SQL, migration, concurrency and security sensitivity alone do not justify it.
- Workers use root instructions, relevant packets/contracts and branch/PR diffs. Keep verbose evidence locally and return compact receipts. Context rotation preserves local fixtures and the supervisor's phase/cycle count. Manual/direct tasks remain an explicit fallback with identical gates.

Packages are optional. Use independently testable/reviewable subsystem, risk or acceptance boundaries, normally 2-4 for a genuinely large slice, never a quota or PR per file. Document 10 records nonbinding remaining-slice defaults: Slices 6 and 10 normally stay cohesive; Slices 7-9 may benefit from packages; choose Slice 11 boundaries from actual remaining work at dispatch.

## 4. Slice review and integration gate

1. Verify the prior accepted milestone and exact-SHA CI; record the slice branch/base under document 10, including any authorized documentation-only integration advance.
2. For a large slice, the supervisor dispatches bounded Luna implementation and independent Terra detailed review per package. Validate proportionately, fix/re-review, then merge clean package PRs into the slice branch. Dependent packages branch sequentially from its updated state.
3. Complete integration/glue and every slice requirement. Run the COMPLETE existing slice pre-review/acceptance matrix on the integrated bytes. Package validation/CI does not substitute.
4. Run fresh holistic Terra review of the COMPLETE delta from the previously accepted milestone to the final slice branch SHA. Assess cross-package gaps, state/authorization/SQL composition, migration/runtime and API/frontend agreement, missing behavior and integration regressions. Use prior package receipts without mechanically rereviewing unchanged code; inspect deeply when confidence or a concrete concern requires it. An unpartitioned slice also requires detailed implementation review.
5. Route confirmed findings to bounded Luna Max fixes, affected validation and focused Terra re-review; use full re-review under document 10's material-broadening triggers. Maximum three fix/re-review cycles after the initial full slice review; broad re-review counts. Stop unresolved without acceptance at the cap.
6. Perform short Astra acceptance of the exact reviewed SHA, current receipts, clean review and branch/PR state. Do not duplicate successful implementation, review or tests. Keep allowed post-review acceptance documentation distinct from the reviewed implementation.
7. Merge the slice PR into `codex/csharp-port`, record status/review/handoff documentation and create a documentation-only milestone/status commit if required. Push and poll actual remote CI for THAT resulting candidate milestone SHA. Only successful exact-SHA CI permits final accepted metadata. Substantive CI failure stops the run with failed SHA/run/step; a clearly transient infrastructure failure permits one safe retry.

A small cohesive slice omits child packages and normally runs in one supervisor task:

```text
BOOTSTRAP -> IMPLEMENTATION -> REVIEW_CANDIDATE -> FULL_REVIEW
  -> [FIX -> FOCUSED_REREVIEW]* -> ACCEPTANCE -> INTEGRATION
  -> ACCEPTANCE_RECORD -> EXACT_SHA_CI -> ACCEPTED
```

Verify completed bootstrap instead of repeating it. Package loops, where justified, occur inside IMPLEMENTATION before complete integrated validation. Normal transitions are automatic; manual/direct phase control is fallback. Stop at the authorized slice boundary unless cross-slice continuation is explicit. Document 10 defines the context firewall, stop conditions and polling rules.

Use package-appropriate builds/tests, real SQL for persistence/concurrency and affected frontend/browser/migration checks; do not multiply full browser/native/publication gates per package without changed-byte justification. Integrated full validation remains mandatory. Review fixes start with the narrowest sufficient gates and broaden when prior evidence is invalidated. Keep compact receipts and inspect raw logs selectively.

A pushed review candidate is scoped and explicitly unaccepted until its required reviews, integration and exact-milestone CI pass. Policy/documentation commits do not create accepted product slices. The separate final whole-application adversarial review below remains mandatory.

## 5. Ordered vertical slices

### Slice 0 - Freeze, branch, skeleton, and engineering baseline

Objectives:

- Record the exact starting PocketBase baseline SHA and the intended final-PocketBase tag convention. Explicitly mark `clc-carousel-manual-import-example/` as unrelated repository pollution at the pinned baseline; do not treat it as ASAP behavior, dependencies, or migration input, and remove it from the port branch during cleanup.
- Create dedicated port branch and draft PR.
- Add `Asap.sln`, `Asap.Web`, `Asap.Database`, `Asap.Migration`, `Asap.Tests`.
- Add .NET 10 pinning/global settings as appropriate.
- Establish SQL Server 2022/compat 160 DACPAC baseline, `[asap].[SchemaVersion]`, and `[asap].[DeploymentState]` for the last successfully deployed DACPAC hash.
- Add external config loading and safe example file, including recipient-domain validation and the independent `Environment.IsNonProduction` email-safety switch from `01-PORTING-SPEC.md` section 14. Configure the environment-specific Data Protection key ring used for both auth cryptography and encryption of SQL-stored reusable integration credentials.
- Add NLog/correlation ID baseline.
- Add `/health/live` and `/health/ready` minimal endpoints.
- Add feature-oriented source layout.
- Add frontend source-copy MSBuild target with no Node dependency.
- Vendor exact current Bootstrap/Font Awesome/Grid.js assets locally.
- Establish CI compile/unit/real-SQL baseline.

Acceptance:

- clean local F5 can create/deploy a missing local database using configured SQL Developer instance;
- data-loss DACPAC change fails explicitly rather than silently resetting;
- generated `wwwroot` is ignored and rebuilt/copied from `Frontend/`;
- application can start far enough to report meaningful schema/config health.

### Slice 1 - Patron login -> title request submission

This is the first real functional vertical slice.

Implement minimum end-to-end dependencies:

- Organization + system/library participation model required by patron flow;
- minimum `WorkflowSettings`/`PatronSettings` plus material-format/publication/custom-field configuration needed by the patron flow, following `13-SETTINGS-SCOPE-INVENTORY.md`;
- `Clc.Polaris.Api` integration for patron authentication/current patron data/reference lookups;
- opaque SQL-backed 1-hour patron bearer sessions and rate limiting; every patron-authenticated request revalidates the session effective Organization as active, final session insertion serializes against library deactivation on the Organization row, and reactivation never revives revoked tokens;
- `/patron` shell, dynamic CSP/frame-ancestor handling, sessionStorage token flow;
- title-request schema with immutable patron identity/contact/library snapshots plus current pickup fields that may change only through the dedicated validated pickup-preference workflow;
- status/material-format/workflow-tag foundations needed at submission;
- duplicate/eligibility/limits/current behavior required to accept/reject a request;
- auto-claim evaluation on submit where required;
- request event creation;
- minimum EmailTemplate/Outbox/Hangfire/Postmark path required for submission-related email behavior, including `01-PORTING-SPEC.md` section 14's shared nonproduction domain predicate, terminal suppression without business rollback, and relevant deterministic tests from `06-TESTING-CI.md` section 4.1;
- relevant legacy deep-link/mapping infrastructure if the flow exposes it.

Migration work in this slice:

- Organization rows needed by requests;
- material formats referenced by migrated requests;
- title requests and required snapshot fields;
- request tags/events needed by submission-era history;
- strict reconciliation for all implemented entities.

### Slice 2 - Core staff workflow

Implement:

- Entra OIDC, issuer/allowed-tenant validation, durable (`tid`,`oid`) identity, and initial super-admin bootstrap from configured tenant/object ID plus readable UPN/email;
- `StaffUser` local allowlist/role/org authorization keyed only by (`EntraTenantId`,`EntraObjectId`), with readable UPN/display plus app-owned notification contact data retained beside it. The auth ticket carries `StaffUserId` and the validated sign-in (`tid`,`oid`) and every authenticated request reloads the row and requires the common current-eligibility predicate in `01-PORTING-SPEC.md` section 7.6: exact ticket tuple equality, current loaded `AllowedTenantIds`, active/role/Organization and applicable participation authorization, so an explicit durable rebind invalidates old cookies immediately. Staff/admin rows reference one non-system library even when that organization is inactive; library deactivation leaves StaffUser lifecycle state untouched, library reactivation restores eligibility for already-active staff, and explicit staff reactivation is rejected while its library remains inactive. Role/organization/deactivation mutations use the shared StaffUser serialization lock and atomically deactivate invalid auto-claim rules, clear invalid open TitleRequest claims with events, clear invalid open AdditionalCopy claims with Notes, preserve closed history, and audit per-type cleanup counts;
- complete current staff profile/preferences: weekly summary enabled/email, purchase-reminder default, additional-copy-reminder default, and mine/unclaimed default filter; preserve them through schema, migration, profile DTO/API, and frontend behavior; define `NotificationEmail` as admin-managed/nullable app contact data that Entra sign-in never auto-repopulates, and make the intentional old-versus-target recipient normalization migration-visible;
- concurrency-safe final-usable-super-admin invariant using the common eligibility predicate and transaction-owned exclusive `sp_getapplock` resource (`ASAP:ActiveSuperAdminInvariant`) before row locks and any mutation that can reduce eligibility, including identity rebind; invariant violation returns 409. Validate staged external tenant-policy changes against the same predicate, and fail startup closed if a direct configuration edit would leave zero usable super-admins;
- anonymous `/staff` shell + explicit Microsoft sign-in;
- staff session/profile behavior;
- scoped request listing, current full-list/client-filtering behavior, deep-link request opening;
- view/edit/claim/unclaim and primary title-request processing actions;
- explicit workflow transitions and rowversion conflict handling;
- current notes + events semantics;
- Polaris calls needed by interactive request actions;
- purpose-specific `HoldPlacementOperation` ambiguity/concurrency journal with one active operation per TitleRequest and unique attempt numbering; acquisition locks/revalidates Organization then TitleRequest in a short transaction and an incomplete operation blocks status/close/reopen, delete, identifier, BIB, AutoHold, and pickup-preference mutations with `409` until reconciled;
- related emails via outbox;
- request deletion + reduced permanent audit where needed for the core workflow.

Migration work:

- StaffUser strict Entra identity mapping: active users require explicit tenant/object-ID bindings from the operator-supplied staff identity map; never derive authorization identity from UPN/email;
- primary/weekly notification recipient migration using the binding precedence in the pack plus a deterministic old-versus-target recipient/eligibility delta report; explicitly surface newly eligible weekly-summary recipients, changed ordinary-notification recipients, and target-null recipients;
- historical request claims and events;
- map every imported operational claimant, then validate the target claimant independently of Organization participation; clear/report unmapped, inactive, or out-of-scope claims for both request types, preserve valid claims and closed attribution, and prove the resulting eligibility predicate in reconciliation (`04-MIGRATION-CUTOVER.md` section 6.3);
- no inferred StaffUser FK from historical actor-name strings.

Core staff/BIB actions that adopt an existing Polaris hold must retain its authoritative final HoldRequestID using the same HoldPlacementOperation and evidence rules as new placement (`01-PORTING-SPEC.md` section 9.2). Do not collapse the provider result to a Boolean or confuse RequestGUID with HoldRequestID. The edit/reopen capability predicate must honor retained legacy placement history, including a protected null BIB, after a separately committed reopen; it never depends on having a runtime provider ID.

### Slice 3 - Additional-copy workflow

Implement as a distinct workflow/review boundary:

- schema and API;
- creation from source title request;
- source claim snapshot copied once only after current claimant eligibility is validated; no substitute assignment;
- independent lifecycle/claim/notes behavior, including StaffUser-serialized claim/reassignment and inclusion of open AdditionalCopy claims in staff deactivation/scope-contraction cleanup without inventing a separate event table;
- staff grid/tab behavior;
- nullable source relationship on source deletion;
- events/email behavior;
- deletion audit.

Migration work:

- full additional-copy import and source-link mapping;
- reconciliation of open/closed state, historical claimant snapshots, and current operational claimant eligibility, not only FK existence.

Reopening is relationship activation: lock Organization -> retained StaffUser -> AdditionalCopyRequest, revalidate the current claim, preserve a valid same-library/super-admin claimant, otherwise clear the effective claim and retain old attribution in Notes atomically. Do not auto-claim for the actor. Cover deactivation, library move, demotion, valid claims, and both race orderings before this slice closes (`01-PORTING-SPEC.md` section 20.1).

### Slice 4 - Administration and configuration

Implement:

- organization discovery/activation/deactivation and manual reference refresh, including transactionally revoking patron sessions on deactivation and preserving the session-issuance race invariant;
- staff account management, durable Entra tenant/object-ID provisioning/rebinding, readable UPN/profile updates, role transitions, activation/deactivation, exact old-cookie invalidation on rebind, and atomic StaffUser-serialized scope-contraction cleanup of invalid auto-claim rules/open TitleRequest and AdditionalCopy claims;
- administrative audit and scoped audit-history UI;
- the complete normative configuration model from `13-SETTINGS-SCOPE-INVENTORY.md`; do not recreate a catch-all `Settings` table or EAV store;
- system-only `SystemSettings` and `PolarisSettings`; field-inheritable `WorkflowSettings`, `PatronSettings`, and `EmailSettings`;
- explicit inherited-vs-overridden UI and **Reset inherited overrides** behavior that never deletes library-owned configuration;
- system-only Polaris configuration/test with reusable secret values protected by Data Protection before SQL persistence;
- system+library Postmark/email configuration with write-only/masked UI/API behavior and Data Protection-protected SQL ciphertext;
- relational external-search providers/overrides, publication-option sets, common-creator sets, patron-code eligibility sets, and patron custom fields/options and per-format custom-field rules;
- branding table/upload/inheritance;
- EmailTemplate system/library inheritance, rejection-template behavior, and preservation of library-owned custom templates on inherited reset;
- MaterialFormat + MaterialFormatOverride management with strongly typed field-behavior columns rather than legacy JSON rule duplication;
- versioned format auto-claim rules as library-owned configuration; active rules require active scope-eligible assignees and rule writers/automatic claim execution lock/re-read the target StaffUser before committing. Migration may preserve missing/ineligible source assignees only as inactive historical rules and reports the normalization;
- patron embed allowed-origin rows/management (super-admin only).

Migration work:

- target-oriented configuration transformation into every domain named by `13-SETTINGS-SCOPE-INVENTORY.md`;
- reusable Polaris credentials plus target-only Postmark credential provisioning; never transform the legacy SMTP password into a Postmark token;
- external-search provider overrides, whole-set publication/common-creator/patron-code configuration, and relational patron custom fields/options and per-format custom-field rules;
- branding bytes from PocketBase file storage;
- email templates;
- material-format overrides/custom formats/rules with consolidated field behavior, including deterministic normalization of source-active auto-claim rules whose assignee is missing/inactive/out-of-scope to inactive historical rules without substitution and explicit reconciliation that every active imported rule has an active scope-eligible target;
- preserve imported current pickup ID/name and the target rule that only the dedicated validated pickup-preference workflow may mutate them after import;
- administrative/business configuration reconciliation with no unmapped source settings.

### Slice 5 - Background workflows and email operations

Implement the complete current background-processing set:

- one canonical five-minute identifier/ISBN processor that combines the dedicated path's BIB persistence/reconciliation/tag/missing-identifier behavior with explicit `Found`/`DefinitiveNotFound`/`TransientFailure`/`OperationalFailure` classification, five scheduled transient attempts with no extra backoff, terminal `error_max_retries`, retry-state reset rules, the scoped **Retry identifier check** recovery action, and an atomic permitted pre-placement identifier-change invalidation path that clears old result/check metadata, identifier-derived tags, and stale BIB authority before downstream work can run;
- one hourly `asap-workflow-processing` orchestrator that executes acquired-hold recovery (including inactive libraries) -> unreviewed-suggestion OutstandingTimeout / pending-hold / hold-pickup / additional-copy timeouts -> purchase promotion -> new hold placement -> fulfillment tracking in that exact order and cannot overlap with another scheduled/manual orchestrator run;
- weekly staff action summaries generated per recipient authorization scope: staff/admin own active library only, super-admin active consortium-wide; do not reuse one consortium-wide payload across ordinary library recipients. Ordinary runs use recipient+period idempotency; every accepted explicit forced run gets one durable `ManualRunId`, intentionally resends under force-run keys, and reuses that ID on retries;
- session cleanup;
- one coherent five-state outbox (`pending`/`sending`/`sent`/`failed`/`suppressed`), including database-enforced filtered uniqueness for non-null deterministic `BusinessKey`, duplicate-key-race-as-success semantics, ordinary recipient+period and forced-`ManualRunId` summary keys, and authorization-sensitive staff original-tuple/current-tenant/scope/address revalidation before every send/retry. Persist `RecipientAddressKind=notification_email|weekly_summary` and resolve the matching current address rule instead of inferring it from `BusinessKey`. Treat every expired `sending` lease as transport-ambiguous under the fixed 30-second call-start deadline, 30-second complete provider timeout, two-minute lease, reclaim-after-expiry rule, and `Status` + `LeaseId` + rowversion/equivalent stale-worker fencing. Delivery remains at-least-once; `failed` retains payload/manual Retry; only terminal `sent`/`suppressed` payload is purge-eligible after 90 days. Missing notification configuration must never roll back the owning ordinary business mutation;
- Postmark webhook/delivery event handling;
- complete the shared nonproduction recipient-domain safety coverage in `06-TESTING-CI.md` section 4.1 across patron, authorization-sensitive staff, ordinary/forced weekly-summary, and Test email paths, including suppression/idempotency, send/retry checks, and the independent application/testing-auth switches;
- Run Now actions with library-admin/super-admin scope;
- Hangfire dashboard super-admin auth;
- implement the authoritative recurring-job schedule and processing-limit contracts from `01-PORTING-SPEC.md`; expose every listed schedule key/default and the complete global/timeout/queue-specific processing-limit shape in `examples/Config.example.json`; preserve current effective operational values through the separate operational-config parity process unless an explicit transform is documented. New participation-dependent result commits/operation acquisitions lock/re-read the owning Organization first and skip if inactive; recovery/completion of already-acquired holds is the explicit section 9.1 exception and infrastructure jobs continue. Already-committed immutable business-event mail may drain after deactivation, while authorization-sensitive staff mail revalidates/suppresses;
- failed-email inspection/retry UI.

Rules:

- sequential oldest-first processing inside finite persisted keyset cycles: implement `QueueProgress` per logical queue/scope, immutable creation/ID order, cycle ID watermark, durable outcome-before-checkpoint, wrap/restart/deletion/insertion/failure semantics from `01-PORTING-SPEC.md` section 23.1. Preserve existing PageSize/MaxPerRun; `HoldRecovery` uses the HoldPlacement limits as a separate bounded phase, not a new configuration key;
- short per-item transactions for local changes/checkpoints; commit before network calls, then reconcile durable operation outcomes in a new transaction;
- use the shared lock ordering `Organization -> StaffUser -> TitleRequest / AdditionalCopyRequest -> dependent rows` whenever multiple categories are needed. Current-state/rowversion and a locked/re-read `Organization.IsActive` gate immediately precede participation-dependent local result commit or durable external-operation acquisition; no SQL transaction spans a Polaris call;
- no blind retry of ambiguous Polaris mutations;
- all hold-placement entry points acquire the same DB-enforced single active operation before any Polaris mutation; acquisition is the durable external-mutation authorization boundary and the incomplete operation is the request-mutation barrier;
- no generic JobRun audit table.

Required completion for this slice: implement the exact hold phases, leased/fenced execution, durable create/reply context, three-evaluation recovery bound, and explicit operator resolution from `01-PORTING-SPEC.md` section 9.1. No timeout/empty lookup may authorize a second create. Recover already-acquired operations before business phases even after Organization deactivation. Use the shared status/timestamp/strict-clock predicates in section 23.2 for timeouts and later-phase candidates; a cap must not let currently timeout-eligible work advance, and OutstandingTimeout must never close/defer an outstanding_purchase solely for its age. Expose blocked/recovery state through the existing scoped operations surface.

Apply stage-aware identifier capabilities/errors from `07-API-FRONTEND-COMPATIBILITY.md` section 14.2; completed holds and closed history do not lose their BIB through identifier editing. Persist recipient identity tuples for sensitive mail and evaluate current loaded tenant trust at intent/delivery/retry.

Migration work:

- meaningful per-request job state (`lastPromoterCheck`, identifier status/result/retry count/last safe error code, `lastChecked`, etc.), including exhaustive migration of every legacy ISBN status; `found` and `found_in_polaris` require supporting BIB state or block/report, and migration ensures canonical found-tag state;
- `effective-legacy-operational-config.json` containing effective legacy cron schedules and queue-processing limits/provenance, reconciled to target external JSON before jobs run;
- explicit transform/reporting for any configured legacy hourly `pending_isbn_checks` queue limit because that old path is retired after its retry semantics are consolidated into the canonical five-minute processor;
- historical delivery events/audit;
- **no** Hangfire infrastructure state;
- **no** pending outbox replay.

For this background slice, implement `01-PORTING-SPEC.md` sections 23.2 and 9.2 and F1/F2 in testing section 10.2. OutstandingTimeout closes only expired unreviewed suggestions as rejected with the configured timeout-rejection intent; the other three families keep their exact existing age/outcome/no-email rules. Positive current checkout stays title-level. Unclaimed/cancelled/expired outcomes require the exact tracked final hold ID plus expected BIB/patron, independent of provider ordering. Missing/ambiguous identity or required provider failure preserves state with a safe diagnostic, not a guessed terminal result. Final-success proof with unavailable identity does not authorize a replay. Ordinary scope/Organization/version revalidation and manual/timeout paths remain unchanged.

### Slice 6 - Analytics

- Keep current analytics feature and authorized scope.
- Reimplement aggregation server-side in SQL/Dapper.
- Preserve metric semantics unless a correctness correction is deliberately documented.
- Avoid loading all request rows into the browser/server merely to aggregate them.
- Add representative real-SQL fixtures and regression tests.

### Slice 7 - Migration system hardening and legacy-link compatibility

Make the migration-time active-bound-super-admin cutover gate explicit: after StaffUser import require at least one active `super_admin` with a valid allowed-tenant durable Entra binding before the application can start. If none exists, provision/promote the configured bootstrap identity inside `Asap.Migration`, report the target-only intervention, revalidate identity uniqueness/tenant allowance, and re-run the gate; never depend on normal startup bootstrap once StaffUser rows have been imported.

The migration executable has been growing alongside prior slices. This slice makes it production-grade as one coherent system:

- explicit `export`, `validate`, `import`, `reconcile` modes/commands as useful;
- direct stopped-PocketBase SQLite + file-storage extraction;
- normalized UTF-8 JSON directory/package + manifest;
- self-contained `win-x64` migration artifact, including SQLite native/runtime dependencies, exercised on a representative old PocketBase server;
- operator-supplied staff Entra identity map for active users (`PocketBaseStaffUserId` -> tenant ID/object ID + readable UPN/email);
- frozen `effective-legacy-runtime-config.json` only for system/global SQL-bound values requiring runtime fallback resolution, plus `effective-legacy-operational-config.json` for legacy cron/queue-limit parity; library-scoped settings remain in organization-aware domain exports; reusable secret plaintext/hashes are never serialized in either snapshot;
- dependency-ordered phases;
- fresh migration-target enforcement: DACPAC/structural seed rows are allowed, but runtime/business/bootstrap data must be empty;
- dry-run/preflight validation;
- deterministic counts/invariant reconciliation;
- temporary `LegacyPocketBaseMapping` deep-link resolver and URL normalization;
- transform report for intentional data reductions/conversions;
- sensitive package handling/cleanup instructions;
- rehearsal automation/documentation.

Migration completion also requires F3 and the F1 configuration fixtures in testing section 10.2: preserve effective OutstandingTimeout system/library values as suggestion creation-age rejection; normalize every dependable placed-stage evidence class in migration section 6.10 before target event mapping; retain known/null historical BIB and deterministic provenance; report explicit reconciliation counts and blockers. All five terminal reasons and ordinary status_changed adoption transitions qualify without a literal hold_placed event. Do not fabricate an operation/provider identity. Prove reopen-then-separate-edit rejection and equivalent fresh-target import determinism.

### Slice 8 - Deployment, health, monitoring, and release artifacts

Implement:

- immutable tagged web/DACPAC/deployment release ZIP;
- separate migration artifact from the same tag;
- release manifest with application version, SchemaVersion, file checksums, DACPAC SHA-256, exact Hangfire storage-package/schema assets and hashes, and both schema compatibility contracts;
- idempotent production deployment PowerShell script that accepts a local release ZIP;
- classify every release for production database mutation: DACPAC hash changes, explicit SQL changes, and required Hangfire/dependency DDL. Existing databases require quiescence and verified backup before any DDL; validate both application and dependency compatibility before starting workers. A proven file-only release skips SQL backup and all database writes, including DeploymentState updates. Persist installed file identity separately (`05-DEPLOYMENT-OPERATIONS.md` sections 9-10);
- explicit first-cutover preparation mode that can deploy schema/files while leaving the app pool stopped so migration occurs before bootstrap/runtime writes;
- disposable production-preflight SQL database/config path so real-hostname/Entra smoke validation never dirties the final migration target;
- explicit code rollback support only when both application SchemaVersion and actual dependency schema remain compatible;
- PRTG readiness PowerShell sensor;
- nonproduction marker/banner from external config;
- operational documentation for IIS, **separate production/nonproduction runtime service accounts**, least-privilege SQL roles, operator-owned Hangfire schema provisioning/upgrades, Data Protection key backup/recovery, logs, and health.

### Slice 9 - CI, Playwright, accessibility, and release-validation integration

CI foundations exist earlier; complete them here:

- one `Asap.Tests` .NET project with unit/integration/real-SQL categories;
- fake Polaris/Postmark normal PR tests;
- SQL Server 2022 CI database;
- Playwright critical patron/staff journeys every PR + push to main;
- `Testing`-only auth handler in `Asap.Web`;
- axe-core checks for serious/critical accessibility violations;
- explicit keyboard/focus tests for important interactions;
- release artifact/tag jobs;
- agreed live Polaris release-validation hooks and GitHub Environment controls.

### Slice 10 - End-of-port synthetic seed/reset tooling

Do this near the end, not during the core implementation:

- explicit operator/developer seed/reset command or tool for synthetic demo/test data;
- never automatic at startup;
- safe guardrails to prevent accidental production execution;
- reusable known scenarios helpful for demos/manual validation/Playwright support where appropriate.

### Slice 11 - Legacy removal, documentation rewrite, final review

Before merge:

- remove PocketBase runtime/source that is no longer required from the port branch;
- remove obsolete PocketBase setup/operations docs from canonical `main` content;
- retain only migration-relevant historical material;
- ensure exact PocketBase production-commit tracking and the final-tag procedure are ready so Git history becomes the archival source after successful cutover;
- verify no PocketBase package/runtime/data paths remain in production artifact;
- verify no Node/npm dependency in normal build/publish/F5/deployment;
- update README/architecture/operations documentation for .NET;
- rewrite repository-level `AGENTS.md` for the completed .NET repository before the port PR merges: remove obsolete PocketBase-only instructions, retain/adapt general simplicity/scope, settings-scope, DOM-safety, accessibility, and behavioral-testing guidance, and describe the actual .NET layout, commands, DACPAC ownership, EF Core, and Dapper/ADO.NET/SQL rules. It must no longer describe .NET as a planned future port.

Then run the final whole-application adversarial review.

## 6. Final whole-application review

Use fresh independent Terra High whole-app contexts, dispatched by the supervisor or run directly in manual mode. Ask whether all accepted slices form one correct, secure, migratable, deployable replacement system. Package and slice reviews supplement this final adversarial gate. Re-examine implementation repeatedly with varied emphasis:

- correctness/integration regressions;
- edge/failure paths;
- migration/data reconciliation;
- concurrency/ordering/external ambiguity;
- authorization/security/trust boundaries;
- deployment/recovery/configuration;
- performance/resource use;
- maintainability/unintended coupling;
- test adequacy.

Stop after **three consecutive passes with no new substantive finding**, maximum six passes. Track a running finding set so later passes do not repeat old issues. Any confirmed blocking defect must be fixed even if this means the nominal pass cap does not itself close the release.

## 7. Merge, exact-artifact rehearsal, and production sequence

The permanent nonproduction environment stays on PocketBase throughout the implementation branch. The final rehearsal is intentionally **post-merge**, because the production artifact must be built from a version tag on the merged `main` commit.

During the interval after the .NET merge but before production cutover, PocketBase may still be authoritative production. If it needs a critical emergency fix, create a temporary branch and/or tag from the exact last PocketBase production commit; do not establish a permanent PocketBase branch. Test and deploy the fix through the normal emergency process, then immediately port the equivalent behavioral correction into .NET `main`. Create a replacement .NET version tag/artifact and repeat the exact-artifact rehearsal required for any changed production artifact. If the PocketBase fix changes schema, stored-data semantics, migration input, or behavior assumed by migration tooling, update the migration contract/tooling and repeat the full required rehearsal before cutover. Remove temporary hotfix branches after successful .NET cutover. The permanent final-PocketBase tag must ultimately point to the actual PocketBase commit frozen at that successful cutover, not an earlier pre-merge candidate.

1. Final whole-app review clean under the rule above.
2. Record the exact commit currently deployed to PocketBase production and confirm it has no behavioral fix missing from the port.
3. Merge port PR to `main`.
4. Create the intended production version tag from that merged commit.
5. Build immutable web/DACPAC/deployment and migration artifacts from that tag.
6. Perform the full permanent-nonproduction cutover rehearsal using those **exact tagged artifacts** and the accumulated nonproduction PocketBase database.
7. Exercise migration/reconciliation, IIS deployment, critical patron/staff workflows, background schedules, health/diagnostics, and applicable same-contract rollback drills.
8. If rehearsal exposes a blocking defect, fix it on `main`, create a **new** version tag/artifact, and repeat the rehearsal with the replacement. Never promote an un-rehearsed rebuild.
9. Only after an exact artifact passes the rehearsal, stage those same artifacts on the new production server.
10. Complete production preflight and hosts-file validation against the real production hostname using a disposable preflight SQL database/config; then stop the app, destroy the disposable database, restore the final production config, and keep the app from starting against the final fresh migration target until cutover import completes.
11. Immediately before the maintenance window, record the exact PocketBase production commit again and confirm every later emergency fix has been ported and, where required, reflected in migration tooling and the successfully rehearsed .NET artifact.
12. Execute the offline PocketBase -> SQL cutover.
13. Switch production hostname/DNS to the new server.
14. Run production smoke/health/diagnostics checks.
15. Once the .NET application accepts production writes, treat it as authoritative and repair-forward; create/verify the permanent final-PocketBase tag against the exact PocketBase commit frozen for this successful cutover.
16. Keep the retired PocketBase deployment stopped for about 30 days as forensic/reference material. Never start the production deployment as-is. Prefer direct database/file inspection; if execution is genuinely necessary, use only an isolated copy with outbound Polaris/email access blocked and recurring production jobs disabled before startup. It must never operate as parallel read/write or fallback production.
17. After production validation, rename repository from `asap-pocketbase` to `asap`.

## 8. Scope-control rule

When a task uncovers an improvement that does not materially reduce port/cutover risk or preserve required behavior, record it in `09-DEFERRED-FOLLOWUPS.md` rather than expanding the current slice.

## Closure-remediation completion gate

The prior seven regression groups R1-R7 in `06-TESTING-CI.md` section 10.1 and final F1-F3 groups in section 10.2 are mandatory in the relevant slices; they are not a deferred design pass. Preserve the exact pinned PocketBase baseline, common lock hierarchy, existing frameworks, and scoped sequential processing. The pinned source has four registered recurring cron triggers; the target has seven recurring Hangfire schedules, including target-only maintenance, as defined by the authoritative matrix in `01-PORTING-SPEC.md` section 23. Retain the eight configured processing queue keys plus the separate logical HoldRecovery scan under sections 23 and 23.1. `14-REMEDIATION-AUDIT.md` is the cross-document traceability checklist, not a claim that unbuilt implementation tests have run.
