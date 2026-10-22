# Slice 4 Conversation Handoff

## Current Slice 4 Stop Boundary

Fresh candidate `corrective-20260914d` passed every complete local final-byte
gate: Release 0 warnings/errors, 234/234 .NET/real-SQL tests with zero skips,
172 Node files, fresh Web/native publications, four native fixture/oracle runs
and 12 published-browser modes (103 states and 14 CSP cases). Astra verified
the complete evidence and final desktop/mobile screenshots. The same Terra
completed full Pass 3 clean; Astra's local acceptance gate is satisfied.
The final replacement milestone SHA and exact-SHA remote CI outcome are
maintained in the
[Slice 4 acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639).
Require success for that exact replacement SHA, not any older checkpoint,
then STOP. Do not rerun completed local review solely because historical
sections below describe earlier pending gates.

Candidate c and clean Pass 2 are historical. Attempted milestone
`c147c3501db2c852781b21af4826ce589dab622c` failed exact CI run 34855842335
(233 passed, one failed, zero skipped): its Development recovery test host
correctly rejected CI's Testing-only SQL authentication. The same Luna fixed
only the fixture to exercise genuine cookie/OIDC/secure antiforgery in Testing,
with every original assertion and normal Development/Production policy intact.
It is not an accepted milestone. After the
replacement exact-SHA CI is green, STOP with PR #264 draft. Do not start or dispatch
Slice 5, merge, tag, deploy or perform cutover. Do not repeat the historical
Step-4 pause or launch another review merely because old notes below say pending.

## Corrective Continuation History

The 2026-09-14 corrective request supersedes the earlier Step-4 pause below.
Work resumed from exact checkpoint `42e65cd3773cf57557e908fe79e01d77d58bac2f`;
PR #264 remains draft on `codex/csharp-port`, and fetched main still equals
the behavioral pin. Retain Luna Max Jason
(`01a09b0c-a918-7640-ac4a-6ed90d644f76`) for all implementation fixes and tests.

The requested corrections and focused regressions are implemented; see
`slice-04-corrective-notes.md`. First isolated candidate `corrective-20260914a`
passed the clean Release build but failed the existing mobile AdditionalCopy
focus-return browser assertion (225/226 .NET tests passed, no skips). That red
evidence is retained. The same Luna corrected the actual asynchronous Grid.js
focus races; the unchanged browser journey and all nine new isolated focus
scenarios pass, as do all 172 Node test files. The failed candidate remains red.

Fresh replacement `corrective-20260914b` was fully published and validated:
226/226 .NET cases, zero skips, 172 Node files, clean Release, complete native
four-fixture exports/oracles/imports/reconciliation, and final published batch
`corrective-20260914b2` (12 modes, 103 browser states, 14 CSP cases). All receipt
paths/hashes retain the candidate-b names in the local receipts. The review
packet now identifies candidate d. Those b gates ran on frozen code;
the freeze was subsequently lifted for the confirmed review finding below.

Astra's fresh corrective review was clear. Fresh Terra High Laplace
(`01a0a00a-8fac-74a0-a536-480f5bde0852`) completed full Pass 1 with one P1:
global-super-admin TitleRequest cleanup can race another library admin's
unclaim/reassignment. Astra confirmed that cross-library distinct-claimant
case and returned it to the same Luna for locking and real-SQL tests. The fix
and eight new SQL cases passed. Fresh candidate `corrective-20260914c` now
passes every complete local gate: Release, 234 .NET cases with zero skips,
172 Node files, four native fixtures/oracles and 12 published browser modes
(103 states, 14 CSP cases). Historical c hashes remain in its committed packet. The same
Terra completed mandatory full Pass 2 clean. No further substantive finding
requires another fix/re-review cycle; the retained contexts were not replaced.

Those candidate-c local gates preceded the exact-CI fixture failure. Fresh
candidate-d evidence and the required additional review/milestone/CI gates
are described at the current stop boundary above. The sections below record
the earlier checkpoints and retained acceptance tooling; they are historical,
not instructions to repeat the old pause after Step 4.

## Checkpoint, Not Acceptance

On 2026-09-13 the user explicitly requested committing and pushing the incomplete
Slice 4 work for continuation in a new conversation after repeated unattended
desktop turn cancellations. This checkpoint preserves implementation progress;
it does not declare Slice 4 complete, freeze a review candidate, waive findings,
or authorize merge, deployment, production tags or production readiness.

On 2026-09-14 implementation resumed from checkpoint head
`414e2f515e203f0fae6f1368828ee48affc68eb7` and completed only the first four
items from the immediate work list below. The work is intentionally paused
after step 4 and before step 5. Do not treat this as final Slice 4 acceptance,
do not dispatch Terra from this checkpoint, do not freeze final artifacts and
do not begin Slice 5.

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

Steps 1 through 4 are complete in the 2026-09-14 WIP checkpoint:

1. Staff Access now uses the existing settings frontend with roster/add/
   per-record action workflows, accepted versioned StaffLifecycle APIs, durable
   Entra tenant/object IDs, scoped audit history, ordinary-admin own-library
   scope and super-admin-only confirmed/reasoned rebind. Staff records remain
   global records, not settings inheritance data.
2. The confirmed published-browser defects are fixed: initial panel visibility,
   accessible names for all 24 format-label inputs and 24 mode selects,
   semantic organization list parent and desktop horizontal overflow.
3. Focused real-SQL coverage now proves every inheritable scalar's system save,
   library override save, effective library read and single-field reset while a
   peer override in the same row remains; additional scoped audit/system-only
   authorization and write-only secret preserve/clear coverage was added.
4. The retained parent browser harness was extended locally under `.git` with
   Staff Access HTTP/UI, scoped audit, stale Staff Access load and organization
   activation/version/scope coverage. Fresh published Web run
   `f6e7cf022b314288a53c0a0e4c16de06` passed 23/23 scenarios and the aggregate
   accessibility/layout/image gate without diagnostic interception.

Remaining work begins at step 5 only:

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

Fresh step-1-through-4 checkpoint checks on the preserved implementation:

- `dotnet build Asap.sln --configuration Release --no-restore`: passed with
  zero warnings/errors.
- `node tests/run_all.js`: all 170 discovered test files passed. The existing
  Windows `grep`/`true` noise inside `no_dao_usage.test.js` remains nonfatal
  and that test still reports PASS.
- `dotnet test tests\Asap.Tests\Asap.Tests.csproj --no-restore --filter "FullyQualifiedName~AdministrationInheritableScalarsSaveResolveAndResetPerField|FullyQualifiedName~AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole"`:
  passed, 2/2.
- `node --check` passed for `src\Asap.Web\Frontend\staff\js\settings.js`,
  `src\Asap.Web\Frontend\staff\js\settings-domains.js`, and the retained local
  `.git\asap-patron-browser-probe\admin-settings.cjs`.
- `dotnet build .git\asap-patron-browser-probe\Probe.csproj --configuration Release --no-restore`:
  passed, zero warnings/errors.
- `git diff --check`: no whitespace errors; only line-ending normalization
  warnings for edited text files.

Latest independent browser report:
`.artifacts/acceptance/patron-browser/f6e7cf022b314288a53c0a0e4c16de06/admin-settings-results.json`.
It uses fresh isolated publication
`.artifacts/asap-slice-04-web-precheck-slice-04-step4-20260914c`, whose receipt
is `.git/asap-slice-04-web-precheck-slice-04-step4-20260914c-receipt.json`.
The published DACPAC SHA-256 is
`8ad550e2b75a5f24d24a4af086207e01a64daaa5c80727491b2ef7edce454034`.
The run passed 23/23 scenarios, captured 17 browser states, made zero external
browser requests, ran with diagnostic mode off, and passed the aggregate
serious/critical accessibility, horizontal-overflow and image-rendering gate.

Earlier full Debug .NET/SQL result: 192/192, zero failures/skips, before later
administration edits. It remains historical only. The complete final .NET/
real-SQL suite, native migration acceptance, final freeze, Terra review and
accepted-milestone remote CI have not occurred.

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
| `.artifacts/asap-slice-04-web-precheck-slice-04-step4-20260914c` | Latest step-4 published Web snapshot, not final acceptance |
| `.artifacts/acceptance/patron-browser/f6e7cf022b314288a53c0a0e4c16de06` | Latest passing step-4 browser/API/accessibility/layout evidence |
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
