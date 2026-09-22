# Slice 1: Patron Login Through Submission

## Gate And Ownership

The Slice 0 gate passed at milestone
`00967778001e7ec8198ab4498d0fbd15ded4d984`, including 44 .NET/real-SQL tests,
legacy tests, the Terra review/fix/re-review cycle, and remote CI. The verified
CLC package incompatibility in `clc-package-probe.md` remains unresolved, but
the user's 2026-09-12 instruction explicitly removes it as an implementation
blocker. Apply `temporary-email-transport.md`: only final provider sending uses
a minimal cancellable `FileEmailSender`. All SQL outbox and application
contracts remain binding. Real Postmark transport and provider webhook work
remain release/rehearsal blockers; they do not prevent this slice's temporary
transport acceptance or progression to subsequent slices.

A fresh Sol XHigh context owns this
complete slice because session serialization, outbox leases and historical
migration are material correctness work. A fresh Terra High reviews the full
slice twice, with Sol fixes and required re-review before the milestone.

## Objective

Deliver the existing patron journey end to end: library-specific public form,
Polaris login, pickup choices, validation/duplicates/limits, suggestion with
snapshots/event/tags/auto-claim, immediate identifier result, and durable safe
submission mail. Preserve the existing browser UX. No nonfunctional staff or
administration screens; their later slices remain absent.

## Read Before Implementing

Read root AGENTS, these focused pack sections, and executable source anchors:

- `02-IMPLEMENTATION-PLAN.md`: Slice 1 and completion rule.
- `01-PORTING-SPEC.md`: 4-6, 7.5-8, 9 (integration boundary), 10-19, relevant
  outbox/identifier processing and participation rules in 23, and 25-27.
- `03-DATABASE-DESIGN.md`: 3-8, 11, 13-14; use the canonical table/column
  contracts, not a parallel configuration or generic JSON model.
- `04-MIGRATION-CUTOVER.md`: 1-8, including exhaustive ISBN classification,
  staff/claim mapping prerequisites, normalized placement-evidence guard,
  strict configuration mapping and effective legacy configuration artifacts.
- `05-DEPLOYMENT-OPERATIONS.md`: runtime/operator and Data Protection rules
  relevant to Hangfire provisioning and protected integration credentials.
- `06-TESTING-CI.md`: unit/real SQL/provider fake/browser requirements and
  submission/outbox nonproduction safety tests in 4.1. Do not require live
  providers for deterministic acceptance or silently skip real SQL tests.
- `07-API-FRONTEND-COMPATIBILITY.md`: 1-3, 6-10, 14, 14.2-14.4, 15-17.
- `13-SETTINGS-SCOPE-INVENTORY.md`: all scope/inheritance/reset rules and each
  patron/submission-relevant row. Every introduced field needs aligned default,
  override, persistence and runtime paths, including migration.
- `10-CODEX-MULTI-MODEL-TASK.md`: review/model/milestone rules.

The exact source pin is `150b30b776565194260cc327eeeffdfb46475e81`.
`patron-source-notes.md` and `patron-schema-notes.md` are reading aids, not
substitutes for `git show <pin>:<path>`. Read `lib/patron_routes.js`,
`lib/records/{suggestions,duplicates,helpers}.js`, `lib/format_rules.js`,
`lib/custom_fields.js`, `lib/format_claim_rules.js`, `lib/patron_codes.js`,
`lib/polaris/pickup_preference_context.js`, `lib/route_utils.js`,
`lib/jobs/purchase_promoter.js`, the actual Polaris search/reference helpers,
`lib/config_routes.js`, relevant config helpers and migrations, `lib/mail.js`
and its rendering/transport helpers. Inspect the complete patron frontend and
shared request/sanitizer helpers before porting them. Exclude carousel.

## Prior Contracts

Retain the four-project .NET 10/DACPAC foundation, external startup-only
configuration and certificate-protected persistent keys, exact application
schema-version readiness gate, separate deployment/hash tracking, SQL 2022
tests, and locally vendored exact browser dependencies. Frontend remains
tracked under the chosen Frontend tree with generated wwwroot. No Node build
dependency. Runtime cannot provision or upgrade SQL or Hangfire schemas.

## Required Observable Behavior

1. Relational Organization/system sentinel and active participation, minimum
   scoped patron/workflow/system/Polaris/email configuration, material formats
   and typed rules, publication/eligibility/common-creator sets, custom fields,
   branding/text and public-search options needed by the form. Preserve custom
   formats. Effective library controls ownership/configuration; pickup choices
   use actual patron Polaris context. Scope trust never comes from submission
   payload. Rich text uses the approved sanitizer; plain runtime data uses DOM.
2. Use the verified current CLC Polaris package for protocol mechanics. Keep
   ASAP adapters thin and test orchestration with fakes. No parallel PAPI
   signing or custom Postmark protocol. The explicit temporary transport
   decision permits only the minimal file sender behind the same narrow async
   transport boundary the eventual CLC Postmark adapter will use. Observe complete-operation timeout and
   cancellation bounds; provider failure must not become definitive not-found.
3. Login supports source aliases and experience/home/effective-library logic,
   including actual patron-code omission behavior unless the pack overrides it.
   Apply 20/IP/5-minute login limiting. Mint random 256-bit opaque token, store
   only SHA-256, use SQL one-hour absolute expiry and sessionStorage. No durable
   PatronUser and no PIN/plain token storage/logging. Every bearer use checks
   expiry/revocation/current effective Organization activity. Final session
   insert locks/rechecks Organization against deactivation; deactivation revokes
   sessions atomically, and reactivation never revives them. No Development
   authentication shortcut. Only automated Testing may use test providers.
4. Preserve `/patron`, dynamic configured frame-ancestors, and internal API
   contracts except documented auth/status changes. Logout revokes server
   session and clears browser state; public navigation and expiry work. Preserve
   field visibility, informational formats, effective options, duplicate/limit
   error views, labels, validation, focus and keyboard behavior across viewports.
5. Submission refreshes current patron/pickup data and validates allowed branch;
   changed pickup uses the existing idempotent provider update and failure leaves
   local state unchanged. Never hold a SQL transaction across a provider call.
   The final short SQL write rechecks participation and duplicate/limit rules
   under appropriate serialization; two racing submissions cannot bypass them.
6. Persist immutable submission identity/contact/library snapshots, bibliographic
   inputs/custom-field snapshots and current pickup. Preserve source autohold,
   title normalization, duplicate tagging and creation event. Initialize the
   exact identifier state. Apply source auto-claim rules with current target
   eligibility locked/re-read using Organization -> StaffUser -> request order.
   Introduce only the staff/rule schema and mapping needed for this prerequisite;
   leave OIDC/management workflow to Slice 2. Do not manufacture placeholder
   Entra identities or silently discard historical claims.
7. Preserve immediate identifier lookup behavior without undoing submission on
   failure. Use the canonical four-result classification and SQL invariant that
   found requires BIB. No provider ambiguity becomes not-found. Persist the
   fields needed by later scheduled retries; no substitute retry schedule now.
8. Capture immutable submission-mail intent with the successful business action.
   Exact outbox states: pending/sending/sent/failed/suppressed. Missing optional
   notification configuration is nonfatal, with explicit suppression where
   required. Deterministic namespaced business keys have a filtered SQL unique
   index, and duplicate-key races succeed idempotently. Provider send begins
   within 30 seconds of claim, has a 30-second whole-call timeout, and owns a
   two-minute lease. Reclaim only after expiry. Completion compares sending,
   LeaseId and post-claim rowversion; late executors cannot overwrite new work.
   Expired sending is potentially ambiguous, not exactly-once. Failed retains
   payload; sent/suppressed are terminal. Purge/manual administration belongs to
   Slice 5, but do not contradict its retained-payload contract.
9. Nonproduction exact-domain policy applies immediately before every transport
   send independently of Testing auth. Empty allowlist suppresses everyone;
   explicit subdomains only; case-insensitive domain match. Blocked destinations
   are terminal recipient_domain_not_allowed with zero provider calls and intact
   business state. Do not send test mail externally. If this slice creates any
   authorization-sensitive staff mail, implement the complete stored recipient
   tuple/address-kind and current-binding/scope/address revalidation contract.
   The temporary sender must not bypass those contracts: one uniquely named
   HTML file per logical invocation, useful escaped envelope/identifier metadata
   and inspectable HTML body, cancellation propagated, and an ignored local
   output directory excluded from application publish and CI artifacts. Files
   are not durable application state. Keep ordinary tests on recording/fake
   boundaries and add focused file-sender tests. Do not simulate provider
   delivery/webhooks or add temporary messaging infrastructure.
10. Add minimum Hangfire-backed outbox processing with operator-provisioned
    separate schema and runtime without DDL. Do not register unimplemented
    business jobs or claim complete job/operations administration. Health remains
    minimal and accurate about implemented infrastructure dependencies.

## Migration Is Part Of The Slice

Implement stopped-SQLite export -> normalized UTF-8 JSON/hash manifest ->
fresh-target SQL import -> strict reconciliation for all entities this slice
owns, not merely handcrafted target seed data. This is target .NET migration
work, so the legacy PocketBase raw-SQL prohibition does not apply. Do not run
or mutate the source PocketBase production database.

Map Organizations, formats/rules/configuration, request snapshots/statuses,
events/tags, required staff/claim prerequisites, templates/historical email and
legacy link mappings. Apply the exhaustive source ISBN map now. Normalize all
historical placement evidence and deterministic known/null BIB provenance in
the existing legacy marker; never fabricate runtime operations or provider IDs.
Require explicit active-staff Entra maps whenever staff prerequisites import;
map then test claimant eligibility, preserving legitimate inactive-library
history. Unknown populated data must be reported/blocking, not silently lost.

Secure target-only Postmark token input is distinct from export; no SMTP token
conversion, plaintext package/CLI exposure, or credential logging. Imported
outbox, sessions, jobs, operations and cursors start empty. Preserve deterministic
timestamps, mapping/report ordering and equivalent fresh-target results. Mark
later-slice entities unsupported honestly; never label a partial package or
import as production cutover ready. No generic resumable migration framework.

## Acceptance Evidence

Return changed paths and actual build/test/publish commands with results. Cover
real SQL session/deactivation and submission races, scoped option fallbacks,
custom formats/fields, pickup failure, limit/duplicate outcomes, immutable
snapshots, auto-claim eligibility, found/BIB constraint, outbox duplicate races,
lease/late-writer/provider-timeout failures, recipient safety and migration
mapping/reconciliation negative cases. Run the patron browser journey against
the real SQL-backed app with deterministic provider fakes, desktop/mobile
screenshots and serious/critical axe gate. Keep existing relevant tests green.

No commit/push/merge/tag/deployment: Astra handles milestone progression after
full-slice Terra Pass 1, Sol fixes, and full-slice Pass 2 or required Pass 3.
Do not edit PORT-STATUS.md or this packet while Astra owns them. Record actual
scope and unresolved concrete risks, not a future completion claim.
