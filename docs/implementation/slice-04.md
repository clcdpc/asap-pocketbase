# Slice 4: Administration And Configuration

## Gate And Ownership

Preparation only, not dispatched. Start only after the completed Slice 3
milestone, tests and independent review gate. Fresh Sol XHigh implements the
whole slice; fresh Terra High performs full-slice Pass 1 and the required
same-reviewer fix/re-review cycle. Read the actual prior-slice code and evidence
before extending it. The FileEmailSender exception changes only final transport;
real Postmark integration/webhooks remain a release/rehearsal blocker.

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
