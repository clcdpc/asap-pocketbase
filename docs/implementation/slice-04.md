# Slice 4: Administration And Configuration

## Gate And Ownership

Refreshed by Astra Max on 2026-09-13 for the explicitly authorized resume.
Slice 3's milestone, tests, independent review and exact-milestone remote CI are
complete under the prior Sol model. The recovery and accepted-code refresh
below authorize fresh Luna Max to implement the complete Slice 4.
Retain that Luna context through tests, confirmed-review fixes and retesting.
After implementation and required tests, fresh Terra High performs full-slice
Pass 1 and the same context completes the unchanged required review/re-review
gate in document 10. Sol is escalation-only under that document's conditions.
Astra verifies acceptance, commits/pushes the milestone, and requires actual
remote CI success for that exact commit before dispatching Slice 5.
The FileEmailSender exception changes only final transport;
real Postmark integration/webhooks remain a release/rehearsal blocker.

## Accepted-State Refresh - 2026-09-13

- Fetched repository, selected the existing `codex/csharp-port` branch and
  verified a clean worktree before this refresh. Local HEAD, remote branch and
  draft PR #264 head agree at `4769a8a8750c315e319824355d4508073bd43546`.
  No alternate implementation branch or PR is authorized.
- Luna policy `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3` and subsequent
  Astra-first escalation policy `4769a8a8750c315e319824355d4508073bd43546`
  are present. Slices 0-3 remain accepted; Slice 4 is first incomplete.
  PR #264 is draft/open, has no review threads/comments, and the recovered
  head passed remote build-test run `34760402589`. That run does not satisfy
  the future Slice 4 milestone's exact-SHA CI gate.
- Fetched `origin/main` still equals behavioral pin
  `150b30b776565194260cc327eeeffdfb46475e81`. The deployed production
  commit remains unverified. All 19 authoritative pack payload hashes match.
- Extend `Features/Staff/StaffLifecycleService.cs`, its endpoints and
  `StaffEligibilityService.cs` for Staff Access; preserve their original-tuple
  checks, serialized last-admin invariant and separate cleanup counts.
  Assignment candidate DTOs intentionally contain only ID/display name.
  Slice 3 retained-claim eligibility and AdditionalCopy note/history contracts
  are accepted, not a new review cycle for this slice.
- Existing `Configuration.sql`, `ConfigurationSets.sql` and related format,
  template/rule tables already establish explicit relational domains.
  Accepted `SchemaVersion` is 4 in Web, migration and post-deployment SQL;
  migration contract is `slice-03`. Advance these together if schema changes
  require it, preserving additive DACPAC ownership and fresh-target checks.
- `Features/Patron/PatronConfigurationService.cs` already resolves scoped
  scalar settings, whole sets, formats, providers and branding. Complete the
  editor/runtime agreement against inventory 13, including sparse template
  subject/body fallback and SystemSettings misconfiguration-message use.
  Do not add a second independent effective-settings authority.
- `MigrationConfigurationImporter.cs` already imports system, Polaris, email,
  workflow, patron/sets/formats/custom-field configuration and branding.
  Extend the existing exporter, validator, importer and reconciler where
  administration exposes missing semantics. Use `migration-source-notes.md`
  for staff-URL provenance and keep missing historical event timestamps a
  hard source-data blocker. Target-only Postmark input is not source SMTP.
- Target staff entrypoint currently starts `js/workflow.js`; the accepted
  target UI has Requests, Additional copies and Profile, with no settings
  module yet. Port the pinned vanilla settings organization and workflows
  into this frontend using `staff/js/http.js` (`authorizedJson`, plain object
  request bodies, antiforgery and session policy) and the shared latest-load
  guard. Preserve scoped navigation, dirty-form protection and existing
  workflow/profile behavior; do not introduce a new settings UX.
- Existing real-SQL/Kestrel/browser fixture support is in
  `tests/Asap.Tests/Integration/PatronJourneyTests.cs`; CI currently enforces
  190 .NET tests plus the Node regression suite. Keep these tests and add
  the full Slice 4 behavior coverage below. A final clean Release build,
  complete real-SQL tests, frontend regressions, fresh published web and
  self-contained win-x64 migration artifacts, native export/import/reconcile
  fixtures, and desktop/mobile browser/accessibility evidence are required.
  Coordinate shared builds and artifact paths; never accept stale RID output
  or synthetic source fixtures as evidence about deployed production data.

## Objective And References

Complete organization/reference administration, Staff Access and scoped audit,
and every configuration domain in normative inventory 13 through API, existing
vanilla settings frontend, typed SQL, migration and observable tests. Do not
redesign the settings UX or reopen the architecture.

Read root AGENTS, `02-IMPLEMENTATION-PLAN.md` Slice 4,
`13-SETTINGS-SCOPE-INVENTORY.md` in full, `01-PORTING-SPEC.md` sections 7/7.6,
14 and administration/configuration contracts, database design for these
domains, migration mapping/identity/configuration sections, API compatibility
settings/version/scope contracts, and tests including R2-R4 and F1.
`10-CODEX-MULTI-MODEL-TASK.md` defines the review and milestone gate.

Behavioral source is pinned at `150b30b776565194260cc327eeeffdfb46475e81`.
Use `admin-source-notes.md` and `staff-source-notes.md` for exact inspected
paths; read executable implementations/callers/tests when resolving behavior.
Important paths include `lib/staff/settings_{routes,save,ui,email,logo_routes}.js`,
`lib/config/settings.js`, workflows/UI/email/Polaris resolvers, custom_fields,
format_rules and existing settings frontend load/populate/serialize/save,
context-switch/refresh, Staff Access, formats/templates/custom-field modules.

## Prior Contracts And Invariants

1. Extend existing feature services/EF Core, targeted parameterized SQL,
   Data Protection, authorization, outbox and migration code. No EAV/settings
   document, repository layer, Graph/session store, messaging framework or
   alternate configuration authority. DACPAC alone owns application schema.
2. Organization 1 is the real system scope. System-only fields are constrained
   there and editable by super-admin only; own-library admins cannot submit
   them through a combined DTO. Runtime and forms use the same scope state.
3. Participation and StaffUser lifecycle remain distinct. Deactivation locks
   Organization first, revokes patron sessions atomically and cannot race final
   session issuance. It does not rewrite StaffUser.IsActive or erase claims.
   Reactivation never restores revoked sessions. Newly discovered libraries
   are inactive and explicit activation immediately inherits system defaults.
4. Reuse the common identity/current-tenant/activity/scope predicate and atomic
   lifecycle operations from Slice 2/3. Rebind invalidates original-tuple
   cookies; Entra never refills cleared NotificationEmail. Serialize reductions
   in usable super-admin eligibility with ASAP:ActiveSuperAdminInvariant before
   Organization -> StaffUser -> request/task -> dependent row locks. Preserve
   closed history, and atomically clear invalid open claims/deactivate active
   rules with request events, task Notes and separate audit counts.
5. Reference refresh and Polaris test use cancellable Clc.Polaris.Api and
   system/application credentials, without holding SQL locks across calls.
   Operational sync state is not inheritable business configuration.

## Configuration Acceptance

- WorkflowSettings, PatronSettings and EmailSettings resolve each field
  library -> system -> defined seed. Blank ordinary override text becomes
  no override where specified. DTOs distinguish effective/system/override
  values without leaking secrets. Blank secret input preserves existing
  ciphertext; explicit clear/reset removes only that override.
- Whole-set publication/common-creator/patron-code domains use meaningful
  library-set existence, not item merging or override-to-empty. Clearing the
  whole set resumes inheritance; preserve stable option IDs, enabled flags
  and ordering. External-search slots use provider identities and scoped
  overrides, not unauthorized library-created provider identities.
- Complete SystemSettings/PolarisSettings save, reload and use, including
  verified format-icon/misconfiguration save-gap corrections. System-only
  controls in library context use system-level wording, stay disabled and
  are absent from library save payloads. Context switch protects drafts.
- Preserve typed material-format behavior, custom formats and library-only
  custom fields/options/rules; missing custom-field format rule means hidden.
  No legacy JSON rule copy competes with typed SQL. Version auto-claim changes
  and lock/revalidate staff eligibility before committing each active rule.
- Template lineage distinguishes built-in override, rejection hide/override
  and owned custom template. Validate selected auto-reject template scope and
  prevent deletion/hiding while referenced. Image and alternate-text overrides
  inherit and reset independently; serve uploaded branding bytes safely.
- Individual reset touches one override. Reset inherited overrides removes
  exactly inventory section 6's inherited domains and preserves every named
  library-owned field/format/template/rule/history and business record.
- Keep staff administration separate from inheritance banners. Scope audit
  history at the data layer. Use current target auth/version request helpers,
  safe DOM rendering, existing stale-response guards and explicit refresh
  helpers. Do not populate stale values after switching settings context.
- Email configuration retains protected target-oriented fields and sender
  inheritance, with masked/write-only secrets and nonfatal missing mail
  configuration. Temporary file transport must not bypass outbox recipient
  revalidation/domain protection or simulate provider test/webhook success.

## Migration And Verification

Extend actual stopped-SQLite/file export -> hashed normalized package -> fresh
SQL import/reconciliation alongside this slice. Cover every settings inventory
domain, branding bytes, system effective fallback/provenance, meaningful
library overrides, whole-set replacement and built-in/custom format-rule
consolidation using pinned runtime precedence. Report deliberate UI inheritance
corrections and block unexplained populated source fields. Preserve owned
configuration and historical rule lineage; source-active invalid/missing
assignees become inactive with explicit reasons, never substitute identities.
Reusable Polaris secrets and separately supplied target-only Postmark secrets
become Data Protection ciphertext. Never import SMTP credentials as Postmark.
Keep operational/runtime parity artifacts distinct and validate staff-URL
persisted-blank/environment/initialization fallback. Extend identity, recipient
delta, bootstrap-super-admin and current-eligibility reconciliation tests.

For every inheritable scalar, test system save, library save, effective read,
and clearing only that field while another override remains. Use compact
table-driven coverage where appropriate. Real SQL tests must prove system-only
constraints, reset preservation, set semantics, write-only secret inheritance,
auto-claim version/race behavior, staff lifecycle races and last-admin guard,
participation/session/worker serialization, template references and migration
unmapped-field failure. Preserve all prior-slice tests and relevant existing
frontend regressions. Verify actual settings/admin browser journeys at mobile
and desktop, switching scope during overlapping loads, dirty-form reset/cancel,
keyboard/focus behavior and no serious/critical axe violations.

No milestone commit or next-slice implementation until Astra verifies end-to-end
tests and Terra full-slice review/fix/re-review passes. Do not merge/tag/deploy
or claim production-ready while deferred real transport gates remain open.
