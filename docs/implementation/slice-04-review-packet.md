# Slice 4 Independent Review Packet

## Gate State

Preparation only. No completed Slice 4 candidate is frozen and no Terra pass
has started. GPT-5.6 Luna Max Jason (`01a09b0c-a918-7640-ac4a-6ed90d644f76`)
retains implementation, tests and confirmed-fix ownership. Astra owns acceptance,
this packet, the milestone and exact-SHA remote CI. The same Luna context has
resumed interrupted work without replacement.

The user-authorized incomplete checkpoint is described in
`slice-04-handoff.md`. It does not satisfy this review gate or move the review
base forward. Include the entire Slice 4 implementation, not only changes
made after the checkpoint, in every required full-slice review pass.

Review base: `4769a8a8750c315e319824355d4508073bd43546`, containing the accepted
Slices 0-3 implementation plus the Luna and Astra-first policy updates.
Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
Branch remains `codex/csharp-port`, single draft PR #264. Do not reopen prior
accepted architecture or historical review attribution.

Replace this gate state with the actual final candidate, complete source and
artifact receipts, required test results and hashes before dispatching fresh
Terra High. Record the review outcomes separately in `slice-04-review.md`.

## Controlling Material

Read root `AGENTS.md`, `slice-04.md`, `slice-04-evidence.md`,
`admin-source-notes.md`, current `PORT-STATUS.md`, and the authoritative pack
sections linked by the slice packet. Inventory 13 is normative for every
configuration domain and individual/full reset behavior. Consult exact pinned
PocketBase callers/resolvers whenever they settle an implementation question.

Keep the accepted auth, lifecycle, AdditionalCopy, hold recovery, outbox,
SQL lock ordering, migration provenance and reconciliation contracts. Scheduled
timeout/auto-promotion execution belongs to Slice 5; complete configuration,
effective resolution, template selection and migration belong to this slice.
The temporary file email sender substitutes only the final provider boundary.
Real Postmark/Rest 3 provider validation remains a release/rehearsal blocker,
not a reason to omit configuration or weaken durable outbox invariants.

## Full-Slice Scope

Review every changed/new source, schema, frontend, test and CI path in the final
receipt, plus affected callers. Do not limit the pass to the latest correction.
The source receipt excludes documents from its code hash list, not from review.

- Administration endpoints/services: own-library and system authorization,
  original/current identity revalidation, coherent read/version pairing,
  mandatory mutation versions, transaction rollback, scoped audit and lock order.
- Organization/reference administration and Staff Access: cancellable provider
  calls outside SQL transactions, explicit participation, inactive discovery,
  patron session revocation, existing usable-super-admin/lifecycle contracts.
- All typed scalar and secret domains: configured-system/raw-override/effective
  metadata, independent fallback, preserve-on-blank secrets, explicit clear,
  system-only payload exclusion and complete runtime consumers.
- Whole sets, providers, formats, custom fields/options/rules and auto-claim:
  stable identities, ordered/disabled members, sparse overrides, eligible
  versioned rules, reference-safe deletes and concurrent submission/lifecycle.
- Templates and branding: named rejection lineage/hides/custom ownership,
  independent content/image/alt inheritance, effective scoped references,
  decimal-string SQL IDs, image type/signature/dimensions/size and original bytes.
- Reset inherited overrides: exactly inventory 13 section 6, preserving all
  owned configuration, active/history rule rows and business/audit records.
- Existing vanilla frontend: actual usable editors, safe DOM, auth/scope/load
  and mutation-completion fencing, clean post-save state, dirty cancel/reset,
  keyboard/focus, narrow viewports, image rendering and accessibility.
- Native migration/export/import/reconciliation: actual stopped source,
  hashed packages, every inventory domain, declared corrections, sender
  ambiguity, unmapped populated values, encrypted target credentials,
  reconciliation drift, accepted identity/recipient/provenance contracts.

## Evidence Requirements

`slice-04-evidence.md` distinguishes intermediate results from final gates.
Provisional native checks are green. The latest provisional published-Web run
passes 17 functional scenarios, including the corrected dirty-after-save path,
but fails the aggregate accessibility/layout gate. Staff Access and scoped
audit-history UI remain incomplete. The checkpoint's clean Release build and
168 passing Node test files do not replace these open acceptance gates.

Before review, require the complete table-driven inheritable-scalar coverage
(system save, library save, effective read, one-field reset while another
same-row override remains), structured/reset/reference/authorization/race
coverage, real SQL tests, Node regressions, browser/accessibility results,
clean Release build and immutable web/native publications. Verify source and
artifact hashes, frontend/vendor/compressed assets and matching DACPAC copies.
An older green run does not certify newer bytes.

## Review Rules

Fresh Terra High reports findings with concrete file/line references,
reproduction/impact and controlling invariant. Terra never implements fixes
or writes shared build outputs. Same Luna fixes confirmed findings and reruns
required tests; same Terra performs full Pass 2 even if Pass 1 was clean.
If Pass 2 finds a new substantive issue, require full Pass 3 after fixes.
No nominal pass cap can waive a blocking finding. Astra independently verifies
acceptance before one coherent milestone commit, push and actual remote CI
success for that exact SHA. No merge, deployment or production tag is authorized.
