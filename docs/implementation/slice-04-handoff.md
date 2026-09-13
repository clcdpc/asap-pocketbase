# Slice 4 Conversation Handoff

## Checkpoint, Not Acceptance

On 2026-09-13 the user explicitly requested committing and pushing the incomplete
Slice 4 work for continuation in a new conversation after repeated unattended
desktop turn cancellations. This checkpoint preserves implementation progress;
it does not declare Slice 4 complete, freeze a review candidate, waive findings,
or authorize merge, deployment, production tags or production readiness.

- Repository: `clcdpc/asap-pocketbase`.
- Existing branch and sole draft port PR: `codex/csharp-port`, #264.
- Full Slice 4 review base: `4769a8a8750c315e319824355d4508073bd43546`.
- PocketBase behavioral pin and freshly fetched `origin/main`:
  `150b30b776565194260cc327eeeffdfb46475e81`.
- Accepted Slices 0-3 remain complete; Slice 4 is first incomplete.
- Luna policy: `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`; Astra-first
  escalation policy: `4769a8a8750c315e319824355d4508073bd43546`.
- Use the checkpoint containing this document plus any later branch history.
  Do not assume the remote head is still the pre-Slice 4 review base.

The interrupted work was actively implementing and verifying the slice, not
intentionally completing it. Desktop cancellation diagnosis is separate from
ASAP implementation; do not change the port to work around an unproven trigger.

## Recover Before Continuing

Use the existing checkout at `C:\Users\mfields\code\asap-pocketbase`. Important
acceptance harnesses and synthetic fixtures remain local-only under `.git`,
and their outputs under `.artifacts`. A fresh clone or separate worktree will
not contain them. They were intentionally excluded from the checkpoint along
with generated binaries, certificates, runtime configuration and logs. If
using another checkout, explicitly recover the required synthetic preparation
or recreate it from the documented contracts; do not claim absent evidence ran.

Fetch origin, inspect PR #264/current head/CI/comments, select the existing
branch, and inspect the working tree before changing anything. Compare current
main with the pin. Read root `AGENTS.md`, `PORT-STATUS.md`, this handoff,
`slice-04.md`, `admin-source-notes.md`, `slice-04-evidence.md`, the prepared
`slice-04-review-packet.md`, and the complete authoritative `docs/dotnet-port/`
contract. Inventory 13 remains normative. The packet was already refreshed;
extend it only for actual changes, without restarting architecture planning
or accepted Slices 0-3 review cycles.

## Ownership And Review

Parent Astra task: `01a09b00-e3f9-7923-9ef6-f86d1d021999`.
Retained primary implementer: GPT-5.6 Luna Max Jason,
`01a09b0c-a918-7640-ac4a-6ed90d644f76`. Luna is paused and confirmed no known
running commands/sessions. Retain this context for implementation, tests,
diagnosis and confirmed-review fixes. If the new conversation cannot access
that agent context, surface the coordination limitation rather than silently
claiming continuity or replacing it merely because work remains difficult.

Astra resolves blocking implementation questions first from the authoritative
pack, accepted implementation, pinned source and tests. Only an unresolved
material question merits bounded Sol High consultation; Sol does not own the
slice. Sol XHigh remains exceptional under the existing escalation policy.

No Terra reviewer or Slice 4 review pass exists yet. Complete implementation
and required testing first, then use fresh Terra High for full Pass 1 and the
same Terra for mandatory full Pass 2 and conditional further passes. The same
Luna implements confirmed fixes and retests. No pass cap waives a blocking
finding. Review all Slice 4 changes relative to `4769a8a...`, including those
already checkpointed, not just the next conversation's edits. Astra then
independently accepts, commits the actual milestone, pushes, and requires green
remote CI for that exact milestone before Slice 5.

## Current Implementation

The checkpoint includes the typed administration service/endpoints, existing
vanilla settings frontend and structured editors, Polaris reference boundary,
affected patron/runtime readers, staff eligibility/lock extensions, shared
branding image validation, EF relationship mappings, migration/export/import/
reconciliation extensions, and associated regression tests. Physical DACPAC
schema remains version 4; migration contract identifies Slice 4. Neither
identifier alone is evidence of slice completion.

Keep inventory 13's explicit relational configuration model and scope rules.
Do not introduce generic EAV, a replacement settings UX, repositories, workflow
engines or other speculative infrastructure. Preserve accepted identity,
current-policy cookie/lifecycle authorization, usable-super-admin protection,
participation/session revocation, hold recovery/fencing, AdditionalCopy,
provenance/reconciliation and SQL lock-order invariants.

`FileEmailSender` replaces only the final Postmark transport boundary. Preserve
all required email configuration and SQL outbox idempotency, snapshots,
recipient revalidation/address kind/domain safety, cancellation, leases,
stale-worker fencing, retries, suppression/failure and retention behavior.
Real cancellable Rest 3-compatible Postmark/provider validation remains a
release/rehearsal blocker, not a Slice 4 implementation blocker.

## Immediate Remaining Work

1. Complete Staff Access in the existing settings frontend. The current panel
   only administers organizations. Adapt the pinned roster/add/per-record
   actions to accepted versioned StaffLifecycle APIs and durable Entra identity.
   Add scoped audit-history UI; its API already exists. These manage global
   records, not settings overrides: no inheritance banner or settings reset.
2. Finish the confirmed browser defects: only the selected settings panel may
   be visible initially; give all 24 format-label inputs and 24 mode selects
   accessible names; give organization list items a semantic list parent; fix
   the 14-pixel desktop overflow in initial/patron views. Preserve current UX,
   safe DOM, dirty-state/scope fencing, keyboard/focus and mobile constraints.
3. Complete meaningful real-SQL coverage for every inheritable scalar: system
   save, library override save, effective read, and one-field reset while a
   different override in the same row remains. Expand whole-set/owned-domain
   reset, reference safety, organization/audit/authorization, coherent version
   reads, lifecycle/concurrency and eligible versioned auto-claim cases.
4. Extend the parent acceptance harness with organization/reference HTTP and
   Staff Access/audit UI journeys. Earlier 17-case functional success is not
   complete coverage of these missing paths. Rerun desktop/mobile accessibility
   and layout on fresh actual published files, with no diagnostic interception.
5. Run the complete required Release/.NET/real-SQL/Node suites and fresh web/
   self-contained native publications. Re-export all four stopped fixtures,
   rerun exact pinned-source oracles, native import/reconciliation and negative
   cases, verify matching DACPAC/artifact/source hashes, then start Terra.

Concrete Staff Access contracts already inspected by Astra:

- `GET/POST /api/asap/staff/users`: super-admin optional organization filter;
  ordinary administrators are restricted to their own library.
- `PATCH /api/asap/staff/users/{id}`: versioned metadata changes.
- `POST /api/asap/staff/users/{id}/role`: versioned role/library changes;
  the accepted lifecycle path also supports reactivation.
- `DELETE /api/asap/staff/users/{id}`: versioned soft deactivation.
- `POST /api/asap/staff/users/{id}/rebind`: super-admin-only explicit confirmed,
  reasoned durable tenant/object rebind under the accepted identity contract.
- `GET /api/asap/staff/audit?organizationId=...`: server-scoped audit, maximum
  200 records; super-admin omission is global, library administrators stay local.

Keep IDs/versions as strings, display actual mutation cleanup results, and guard
stale record/scope responses. Inspect the current request DTOs rather than
inventing payload fields from this summary. Scheduled/manual timeout and full
auto-promotion execution belong to Slice 5; complete their configuration,
resolution and migration here without bringing the later executor forward.

## Verification State

Fresh checkpoint checks on the preserved implementation:

- `dotnet build Asap.sln --configuration Release --no-restore`: passed with
  zero warnings/errors.
- `node tests/run_all.js`: all 168 discovered test files passed.

Earlier full Debug .NET/SQL result: 192/192, zero failures/skips, before later
administration edits. It does not certify the checkpoint bytes. The full .NET/
real-SQL suite was not rerun for this checkpoint. No final acceptance freeze,
Terra review or accepted-milestone remote CI has occurred.

Latest independent browser report:
`.artifacts/acceptance/patron-browser/ba0332479ebd420f870712fc58e477a1/admin-settings-results.json`.
It uses unmodified publication `asap-slice-04-web-precheck-20260913b`, passes
17 functional scenarios, and fails the aggregate accessibility/layout gate
with twelve entries across system/patron desktop/mobile views. All images
rendered. The earlier dirty-after-successful-save defect is fixed and verified;
do not reclassify that historical reproduction as a current unresolved failure.

Provisional native migration: original 158/158 checks, expanded edge 161/161,
unmapped source setting 3/3 rejection checks, competing template sender 3/3
rejection checks. Positive cases import/reconcile and detect same-count drift;
negative cases leave no imported business/mapping rows or successful report.
These used an earlier isolated native publication and must run on final bytes.
Detailed run IDs and hashes remain in `slice-04-evidence.md`.

## Local Acceptance Preparation

The following paths were confirmed present when making the checkpoint:

| Path | Purpose |
| --- | --- |
| `.git/asap-slice-04-web-precheck.ps1` | Immutable source snapshot and isolated Web publication; requires a fresh `-Name` |
| `.git/asap-patron-browser-probe/Probe.csproj` and `Program.cs` | Isolated SQL/DACPAC, test certificate, app process and headless browser harness |
| `.git/asap-patron-browser-probe/admin-settings.cjs` | Current functional/API/browser probe, aggregate accessibility gate and screenshots |
| `.git/asap-admin-migration-acceptance/Probe.csproj` and `Program.cs` | Native executable import/reconcile, semantic SQL assertions, drift and negative cases |
| `.git/asap-admin-migration-acceptance/config-oracle.cjs` | Twelve exact pinned configuration modules, four scopes and text/Goja-byte JSON parity |
| `.git/asap-real-pb-source/admin-settings-data` | Stopped original synthetic PocketBase fixture |
| `.git/asap-real-pb-source/admin-settings-edge-data` | Stopped sparse/provider/template edge fixture |
| `.git/asap-real-pb-source/admin-settings-unmapped-data` | Stopped unexplained populated setting rejection fixture |
| `.git/asap-real-pb-source/admin-settings-sender-conflict-data` | Stopped ambiguous template-sender rejection fixture |
| `.git/asap-slice-04-verification.cjs` | Final combined web/native source and payload receipts; review base remains pre-Slice 4 |
| `.artifacts/asap-slice-04-web-precheck-20260913b` | Latest provisional Web snapshot, not final acceptance |
| `.artifacts/slice-04-native-precheck-4769a8a-20260913a` | Earlier provisional native migration publication |

Do not overwrite old snapshots, package manifests, receipts or baseline
evidence. Use new unique names and re-export stopped synthetic source through
the native executable. Never use raw SQL against PocketBase data; source
fixtures were prepared through PocketBase Record/Collection APIs. Real SQL
assertions are for the target SQL Server databases only.

For the administration browser harness, set
`ASAP_PROBE_SCRIPT=admin-settings.cjs` and run the built
`.git/asap-patron-browser-probe/bin/Release/net10.0/Probe.dll` with repository
root and the fresh published Web root as its two arguments. Inspect/build the
harness first if it changes. Keep `ASAP_ADMIN_SNAPSHOT_DIAGNOSTIC` unset for
acceptance: diagnostic interception is not evidence for published application
bytes. The harness owns cleanup of its app, unique SQL target and certificate.

The native probe takes repository root, native executable, matching DACPAC,
fresh exported package, source-oracle JSON, and optional `edge`, `unmapped` or
`sender-conflict` mode. Inspect the retained harness and fixture preparation
before invoking it. Keep external providers blocked/deterministic and use
only disposable test targets. The checkpoint does not require starting any
runtime or fixture before the next conversation deliberately resumes work.
