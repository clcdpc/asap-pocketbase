# Slice 4 Independent Review Packet

## Gate State

Current candidate `corrective-20260914d` is frozen from attempted milestone
`c147c3501db2c852781b21af4826ce589dab622c` on 2026-09-14. The original corrective
start remains `42e65cd3773cf57557e908fe79e01d77d58bac2f`. Every fresh complete
Release/.NET/real-SQL/Node, publication, native fixture/oracle and published
browser gate has passed. Astra independently checked the complete evidence.
The retained Terra completed full Pass 3 clean, not only a review of the CI fix.
The final milestone and its exact-SHA remote CI outcome belong in the linked
acceptance record; no earlier run substitutes for that remaining remote gate.

Candidate c and clean full Pass 2 remain historical local-green evidence.
Attempted milestone c147c35 failed exact CI run 34855842335: 233 passed, one
failed, zero skipped. The Development recovery test host could not use CI's
Testing-only SQL authentication. The retained Luna corrected only the test
host to select genuine cookie/OIDC handlers in Testing, retain secure
antiforgery and every recovery assertion, and verify normal Development and
Production authentication registration. Product policy is unchanged.
The candidate-c packet and hashes remain in the
[historical packet](https://github.com/clcdpc/asap-pocketbase/blob/c147c3501db2c852781b21af4826ce589dab622c/docs/implementation/slice-04-review-packet.md).

Terra Pass 1's cross-library global-super-admin TitleRequest cleanup race is
closed by ordered held request locks and eight real-SQL ordering cases. Full
Pass 2 was clean; the additional test change and CI failure required this fresh
full pass, which is now clean. No earlier candidate or CI run
certifies the replacement. GPT-5.6 Luna Max
Jason (`01a09b0c-a918-7640-ac4a-6ed90d644f76`)
retains implementation, tests and confirmed-fix ownership. Astra owns acceptance,
this packet, the milestone and exact-SHA remote CI. The same Luna context has
resumed interrupted work without replacement.

The previous user-authorized incomplete checkpoint is described in
`slice-04-handoff.md`. The current corrective request additionally authorizes
specific repairs to accepted Slices 2-4 before final Slice 4 acceptance. Neither
checkpoint satisfies this review gate or moves the full-slice review base
forward. Include the entire Slice 4 implementation and these prior-slice
corrections, not only changes made after the checkpoint, in every pass.

Review base: `4769a8a8750c315e319824355d4508073bd43546`, containing the accepted
Slices 0-3 implementation plus the Luna and Astra-first policy updates.
Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
Branch remains `codex/csharp-port`, single draft PR #264. Do not reopen prior
accepted architecture or historical review attribution.

Terra's full review/re-review outcomes and Astra's completed local acceptance
audit are recorded separately in `slice-04-review.md`. The coherent milestone
and exact-SHA remote CI result are recorded in the
[acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639)
after push. A prior checkpoint run cannot certify this milestone.

## Candidate Receipts

Candidate root: `.artifacts/slice-04-candidate-corrective-20260914d`.
Source snapshot: `.git/s4-candidate-corrective-20260914d`.

- Source/build/publication receipt: `.git/asap-slice-04-candidate-corrective-20260914d.json`,
  SHA-256 `16a7b698a55ff0a8aeb77d98c4b0bef3997918b5c9e3143d7531a5eac0686eb9`.
  All 746 input files remained unchanged during the gates; tested Web assembly
  equals the published assembly. The 590-file inventory includes validation receipts.
- Full-slice code receipt: `.git/asap-slice-04-review-state-corrective-20260914d.json`,
  SHA-256 `ee29a846313739760554339d43183e424ecbe054f0bed3a13a0d9ba99f190f08`;
  52 changed/new code paths from the full review base. Documents remain in review.
- Artifact receipt: `.git/asap-slice-04-artifact-inventory-corrective-20260914d.json`,
  SHA-256 `c1acf2943bb088c9e1f38c60a6e073ee1d0f4e55e815d8ccd4d36fe0428da553`.
  All 45 frontend files, 76 compressed payloads and 17 vendored files verified;
  no PocketBase/Node/development-email runtime payload was published.
- Both DACPAC copies: `8a34724436e04d5328c0515197e7041322a3ce0e9efb2daaaf36c20dc0295e09`.
- Web assembly: `2f61f96ace38091ae7f16b4f1ae46fb9cc1cb4623ad100f24adf37498e60d96b`.
- Native executable: `6e07a579a3efc88b3edf70f79a636fafde89723e62fae270dfe2f0022facc11d`.
- Native DLL: `b872a1f5d11752581bfc8049c389d98804f25dd602c2740a81a9bb115f2fe5fd`.

The candidate-d DACPAC differs from c only in `Origin.xml` build metadata;
model, post-deployment SQL, DAC metadata and content types are byte-identical.
No product, schema, migration mapping or CI workflow changed after c147c35.

Validation in the candidate's `validation/` directory: clean Release with zero
warnings/errors; complete .NET 234/234, zero skipped (146 integration, 88 unit,
including 19 migration cases), with `final-dotnet.trx`; complete Node 172/172
files. The real SQL race coverage includes 32 lifecycle/workflow interleavings.
The new pinned-Grid.js focus coverage includes nine isolated scenarios without
changing the existing browser assertions. Its final setup also passed 12 full
repetitions (108/108) after the observed Grid.js fixture race was corrected.

Fresh stopped-fixture receipt:
`.git/asap-slice-04-final-fixtures-corrective-20260914d/receipt.json`, SHA-256
`eb21a4870381b9fe72151c828fcc25ad7844d3a6fb8d11d82b59cdaafb1a4445`.
All four exports use the candidate native executable, matching DACPAC and exact
behavioral pin. Each passes the 12-module/four-scope configuration oracle and
four-module runtime/eight-queue oracle. Original/edge imports passed 161/164
checks for 115/116 records, including deliberate reconciliation drift rejection;
unmapped-setting and conflicting-sender packages each passed three negative
checks. These are configuration/runtime oracles, not Slice 5 job execution.

| Fixture | Stopped source database SHA-256 | Fresh package manifest SHA-256 |
| --- | --- | --- |
| Original | `78ba75b8869257ffcc96107d4333d031dfbcf02bea03a89cd418c051637709de` | `e22528dc6e32adfbab758676051225f2d4154f867e78395e7c52f1a49c8b474a` |
| Edge | `e871d84ac043cf21f988ce6d51d12c8875eef5090d50bd4916b2af9cc3696b0d` | `c529c4f6eb8f8e410ca59e2828b2fcd5ae640ab5964f40ad8ebf75e70b722681` |
| Unmapped | `307966b73acabb6ea1fb310792a6bbd735221b0dd154ea7f72dfcfce894b01a9` | `7d67547489377a50d0d379c9c68d8e3f57e6710b3d49c4edf01635de0cc0d33b` |
| Sender conflict | `8bd6d612a7bc9cce1a9d5e0e5d9571c20466926e719d379255bdd45b7ff1065b` | `b9655e765a2e9bcf65c1dc97d1f419e429f182f54d35d42e9bff54027a6b4d91` |

Historical candidates a/b/c and failed local-harness/CI evidence are retained in
`slice-04-evidence.md` and `slice-04-corrective-notes.md`; no failed run is counted
as acceptance. Fresh complete published batch `corrective-20260914d` used the
candidate-d bytes throughout. Receipt:
`.git/asap-slice-04-final-browsers-corrective-20260914d/receipt.json`, SHA-256
`3e115ea23474edbfba8668371d367cf56c0de1881a41c6d9feb43ddb5f52799c`.
All 12 modes passed: administration (23 cases/17 states), real-cookie recovery
(4 cases/10 states), five patron template configurations (10 states each),
AdditionalCopy lifecycle (10 states), ordinary-staff assignment (6 states),
assignment races (4 states), committed-mutation races (6 states), and 14 exact
pinned CSP cases. All 103 browser states passed the applicable serious/critical
axe, overflow and image checks. Diagnostic interception was off; no external
browser assets or development email output were observed. Each run's reports,
SQL assertions, scripts and screenshots are attributed and hashed in the receipt;
Astra independently checked all 164 referenced evidence files and all 103 states.

Astra visually inspected final desktop/mobile Staff Access, mobile
participation-loss recovery, different-authorized-account workspace and patron
form screenshots, using readable viewport images for Staff Access.
The cookie flow uses synthetic protected tickets at the identity boundary plus
the real cookie/antiforgery handlers and Microsoft challenge with
`prompt=select_account`; it is not a live Entra login. Enabled patron template
content is verified even where nonproduction recipient policy suppresses final
delivery; real-SQL integration also proves enabled template delivery through
the recording transport. Hidden templates persist lineage and suppress intent
without rolling back submissions.

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
The previous Step-4 publication passed 23 scenarios and 17 browser states after
the earlier functional/accessibility corrections. Corrective prechecks have
also passed the updated Staff Access workflow and the real application-cookie
participation-loss recovery path. These are intermediate publications, not
final-byte certification. Preserve the historical results and receipts; the
final complete suites, fresh native fixtures, publications and fresh Terra
sequence remain required.

Before review, require the complete table-driven inheritable-scalar coverage
(system save, library save, effective read, one-field reset while another
same-row override remains), structured/reset/reference/authorization/race
coverage, real SQL tests, Node regressions, browser/accessibility results,
clean Release build and immutable web/native publications. Verify source and
artifact hashes, frontend/vendor/compressed assets and matching DACPAC copies.
An older green run does not certify newer bytes.

## Corrective Review

The final full-slice passes must explicitly cover the user-requested findings:

1. Role/library edits preserve locked `IsActive`; re-add is the explicit
   reactivation path. Both existing and requested destination scope use current
   locked actor authorization. Preserve atomic claim/rule cleanup and audit.
2. Privileged TitleRequest/AdditionalCopy decisions use locked StaffUser role
   and organization. Real SQL tests prove both lifecycle/workflow orderings.
3. Durable identity is the allowed nonempty tenant/object tuple; readable UPN
   is required metadata, never an authorization key. Own-library administrator
   rebind, inactive duplicate ownership, rowversion, new label normalization,
   unchanged NotificationEmail and old-cookie invalidation remain explicit.
4. Authenticated-but-forbidden library users retain session/antiforgery,
   sign-out and Microsoft challenge recovery, without ordinary app access.
   Invalid bindings remain 401; participation/resource denial remains 403.
5. Every implemented notification readiness call uses its business library,
   outside SQL locks, with locked resource/version revalidation and unchanged
   outbox scope, idempotency, suppression and final transport substitution.
6. Aggregate versions canonically include all represented configuration,
   especially origins, participation and inherited branding. New dependencies
   must not reverse Organization-before-Staff locking.
7. Hidden source or override templates suppress submission notification;
   enabled sparse content inherits without erasing persisted lineage.
8. The bounded current-authorization sweep includes the confirmed profile-save
   participation race and no unrelated authorization redesign. Historically
   unusable identity rows must not count as usable administrators or assignees.
9. Existing migration identity mapping, readable metadata, recipient separation
   and deterministic reconciliation remain unchanged and are reverified on
   fresh native imports.

The published recovery probe uses synthetic protected ASP.NET authentication
tickets to exercise the real application cookie handler and authorization,
and performs an actual Microsoft OIDC challenge. It does not claim a live
Entra account login or external email delivery.

## Review Rules

Fresh Terra High reports findings with concrete file/line references,
reproduction/impact and controlling invariant. Terra never implements fixes
or writes shared build outputs. Same Luna fixes confirmed findings and reruns
required tests; same Terra performs full Pass 2 even if Pass 1 was clean.
If Pass 2 finds a new substantive issue, require full Pass 3 after fixes.
No nominal pass cap can waive a blocking finding. Astra independently verifies
acceptance before one coherent milestone commit, push and actual remote CI
success for that exact SHA. No merge, deployment or production tag is authorized.
