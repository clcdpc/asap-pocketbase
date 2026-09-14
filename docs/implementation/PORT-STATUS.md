# ASAP .NET Port Execution Record

The implementation replaces PocketBase with .NET 10 / ASP.NET Core 10, SQL
Server 2022, and IIS under the authoritative `docs/dotnet-port/` pack. This
single draft PR will deliver the complete port as reviewed vertical
slices. It is not eligible for merge or production deployment yet.

## Baseline

- Recorded: 2026-09-12.
- Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
- Fetched `origin/main`: `150b30b776565194260cc327eeeffdfb46475e81`.
- Existing implementation branch: `codex/csharp-port`.
- Single draft PR: https://github.com/clcdpc/asap-pocketbase/pull/264
- Starting branch commit: `c7637a351dad711484c4a0df9613050a1e6b2636`.
- Starting working tree: clean.
- Changes between pin and starting branch: repository instructions and port
  documentation only. There are no intervening `main` fixes to propagate.
- Exact deployed PocketBase production commit: **unverified**. The repository
  has no GitHub deployment records. Do not infer deployment from `main`.
- Final historical tag convention: `pocketbase-final-YYYYMMDD`, created or
  verified only after successful .NET cutover, at the exact frozen production
  commit. No final tag has been created.
- Excluded baseline subtree: `clc-carousel-manual-import-example/`; unrelated
  to ASAP behavior, dependencies, migration, and archival requirements. Remove
  it in Slice 0.
- All 19 payload SHA-256 values match `docs/dotnet-port/PACK-MANIFEST.txt`.
- Astra has read root `AGENTS.md`, the pack README, all numbered documents,
  all four examples, and the manifest before implementation changes.

Ordinary PocketBase feature work is frozen for this port. Fetch and inspect
`main` before subsequent milestones; immediately propagate any urgent source
behavior change and its migration implications. Keep tracking the actual
deployed source independently. After the .NET merge, emergency PocketBase
fixes use a temporary branch from the deployed source, immediate equivalent
.NET changes, and a replacement tagged artifact with repeated rehearsal.

## Execution And Evidence

### Pre-Slice-5 Documentation Cleanup - 2026-09-14

Started from `5c3553254c31f7b41d61025318a410de6f809a05`, with local/remote/PR
heads matching, no later commits and its exact-SHA CI green. Accepted Slice 4
`417c72430652a35bc8fc1da549ae270eabc86429` remains in history; fetched `main`
still matches the PocketBase pin. The deployed production SHA remains unverified.

Document 10 now permits a user-directed pushed review candidate after all
pre-review gates, labeled `implemented / ready for independent review`, with
Terra not yet run. It is not acceptance or permission to start a later slice;
the accepted milestone still needs review/fixes, Astra acceptance and its own
exact-SHA CI. `slice-05.md` enumerates the complete pre-review suites,
publications, native/browser/accessibility and artifact checks. Context rotation
preserves the checkout and required local harnesses/evidence under document 10.
The PR description is a concise current summary; historical execution evidence
remains in this repository. Slice 5 is not started or dispatched, and no review
candidate is created by this documentation-only cleanup. PR #264 remains draft;
no application acceptance suite, merge, tag, deployment, rehearsal or cutover
is performed by this task.

### Current Execution Policy - Beginning With Slice 5 (2026-09-14)

This documentation-only refinement starts from accepted Slice 4 milestone
`417c72430652a35bc8fc1da549ae270eabc86429`. After fetching origin, local HEAD,
`origin/codex/csharp-port` and draft PR #264 all matched that SHA, with no later
commits. Its exact-SHA [CI run 34861199803](https://github.com/clcdpc/asap-pocketbase/actions/runs/34861199803)
completed successfully. Slices 0-4 remain accepted; no re-review is required
because the future execution policy changes.

Beginning with Slice 5, GPT-6 Astra Max remains orchestrator and acceptance
owner. GPT-5.6 Luna High owns complete ordinary implementation, tests, diagnosis,
documentation and confirmed-review fixes; normally start each slice fresh.
Luna Max is reserved for bounded implementation problems that materially benefit
from additional reasoning. Size, SQL, authentication, migration, test volume or
elapsed time alone are insufficient reasons. Prefer returning to Luna High.
Luna always escalates first to Astra; only Astra may request focused independent
Sol High advice or exceptional Sol XHigh escalation.

Keep Luna context while useful; Astra may rotate when it becomes materially
large/repetitive, using document 10's concise handoff and current-code inspection
before editing. Context identity is not an acceptance invariant. Retain detailed
logs locally and return compact command/result/count/hash/path/warning receipts.
Astra verifies every required gate ran and passed, selectively inspecting raw
evidence for failures, mismatches, relevant findings, security/concurrency/
migration concerns or ambiguity. Successful expensive gates are not rerun solely
for Astra to consume the same output again.

Every slice still requires fresh independent GPT-5.6 Terra High full Pass 1.
A clean Pass 1 needs no ceremonial full Pass 2. Confirmed findings return to
the current Luna context; Terra reports findings only. After fixes/tests, Terra
reviews fixes, affected callers/invariants, regression surface, tests and fix
interactions. Repeat full-slice review when document 10's systemic/broad-change
or unbounded-regression triggers apply. No pass cap permits a known blocker.
All objective acceptance gates, exact-milestone remote CI and the separate final
whole-app/release/rehearsal gates remain binding.

The complete policy is in `../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md`.
Earlier dated execution/policy entries below and the Slice 0-4 files preserve
what actually happened, including prior model roles, retained contexts, full
Terra pass counts and duplicated acceptance evidence; they do not govern future
slices. This update changes no application/schema/migration/frontend behavior
and does not rerun application acceptance. Slice 5 is not started or dispatched;
PR #264 remains draft. No merge, tag, deployment or cutover is performed.

### Slice 4 Replacement Candidate D - 2026-09-14

The bounded CI fixture correction below is implemented with no product,
schema, migration, workflow or configuration-policy change. Fresh candidate
`corrective-20260914d` passed clean Release (zero warnings/errors), 234/234
.NET/real-SQL tests with zero skips, 172/172 Node files, fresh Web/native
publications and hashes, four native fixture/oracle runs (161/164/3/3 checks),
and all 12 published-browser modes (103 states and 14 CSP cases).
Astra verified the complete source/artifact/native/browser evidence and final
desktop/mobile screenshots. The same Terra completed full Pass 3 clean;
all local acceptance gates are satisfied. The replacement milestone and that
exact SHA's remote CI outcome are maintained in the acceptance record below.
Require its successful exact-SHA run before final certification and then STOP.
No earlier local-green candidate or failed milestone certifies Slice 4.

### Slice 4 Exact-Milestone CI Correction

Attempted milestone `c147c3501db2c852781b21af4826ce589dab622c` was committed
and pushed after the local gates and clean full Terra Pass 2 below. Exact
[CI run 34855842335](https://github.com/clcdpc/asap-pocketbase/actions/runs/34855842335)
failed with 233 passing .NET tests, one failure and zero skips, so it is NOT accepted Slice 4.
The recovery test switched its host to Development while Linux CI uses SQL
authentication, which the unchanged application correctly permits only in
Testing. Invalid startup configuration caused a strict fixture service lookup
to fail before the cookie assertions. This is a test-host mismatch, not
permission to weaken Development/production configuration or authentication.

The retained Luna corrected only the fixture to select genuine cookie and
OIDC handlers in its CI-compatible host, retaining secure antiforgery and all
recovery assertions. Fresh complete candidate-d gates and the retained Terra's
additional full review subsequently passed. A replacement milestone's own
exact-SHA green CI is required; c147c35 remains a failed attempted milestone.
The acceptance record linked below remains the final-SHA/run record.
Slice 5 is not started; PR #264 remains draft.

### Slice 4 Candidate C Local Gate (Historical)

All corrective and Step-5 local gates passed on candidate c. The retained
Terra High completed full Pass 2 clean and closed the Pass-1 cleanup race;
the same Luna implemented and retested every confirmed finding. Astra verified
the complete source, publication, SQL, migration, browser and review evidence.
No substantive finding remains. See `slice-04-review.md` and the current
`slice-04-review-packet.md` for exact evidence and residual provider boundaries.

The subsequent coherent milestone failed its exact CI as recorded above.
Final Slice 4 certification requires the replacement milestone's remote CI
green, not checkpoint CI. The final SHA and actual run result are maintained in the
[Slice 4 acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639),
so recording the result does not create a different, untested milestone SHA.
Stop after that gate. PR #264 stays draft; Slice 5 is not started or dispatched.
No merge, tag, deployment or production cutover is authorized.

### Slice 4 Corrective Pass - 2026-09-14

The authorized pre-Slice-5 corrective pass started from clean local/remote/PR
head `42e65cd3773cf57557e908fe79e01d77d58bac2f`. Fetch and PR inspection confirmed
`codex/csharp-port`, draft PR #264, and `origin/main` still exactly equal to
behavioral pin `150b30b776565194260cc327eeeffdfb46475e81`; no source change needed
propagation. Full Slice 4 review still starts at `4769a8a8750c315e319824355d4508073bd43546`.

The same Luna Max implementation context has corrected lifecycle activity and
destination authorization, locked workflow privileges, durable identity and
readable metadata, existing-cookie recovery, business-library email readiness,
complete canonical settings versions and hidden submission templates. The
bounded related sweep also corrected profile participation serialization,
unusable identity assignment/invariant predicates, settings lock ordering and
stale sign-in metadata after rebind. See `slice-04-corrective-notes.md` for
implementation and focused regression evidence. Migration mapping and schema
are unchanged by this corrective pass; FileEmailSender remains authorized.

The first isolated candidate `corrective-20260914a` built Release with zero
warnings/errors but failed one of 226 tests (225 passed, zero skipped): the
existing mobile AdditionalCopy dialog focus-return browser assertion. This is
retained red evidence, not a certified publication. The retained implementer
reproduced and corrected both delayed-opener and replaced-opener focus races
using the pinned Grid.js. The unchanged SQL-backed browser journey passed
(18 accessibility/layout/image states), all nine new isolated focus scenarios
passed, and the complete Node suite passed all 172 files. No existing browser
assertion was weakened. A replacement isolated candidate was required.

Replacement candidate `corrective-20260914b` passed clean Release, 226/226 .NET
tests with no skips, 172 Node files, fresh Web/native publications and matching
source/DACPAC/asset hashes. All four fresh stopped exports, pinned oracles and
native positive/negative checks passed. Published batch `corrective-20260914b2`
passed 12 modes, 103 browser states and 14 CSP cases. Astra's post-validation
corrective review found no remaining substantive issue. See the review packet
and evidence for exact receipts and the retained failed-harness history.

Fresh Terra High Laplace (`01a0a00a-8fac-74a0-a536-480f5bde0852`) completed full
Pass 1 with a P1 distinct-claimant TitleRequest cleanup race for global
super-admin scope contraction. Astra confirmed the cross-library case and
returned it to the retained Luna for held request locking and real-SQL tests.
The fix and eight SQL ordering cases passed. Fresh candidate c then passed
clean Release, 234/234 .NET tests with zero skips, all 172 Node files, all four
fresh native fixtures/oracles and 12 published browser modes (103 states,
14 CSP cases). Candidate b remains historical green. The same Terra's full
Pass 2 is now clean and Astra's acceptance audit is complete. Milestone push
and exact-SHA CI remain the final gate recorded above.
The Step-4 entries below are historical checkpoints, not final acceptance.
Slice 5 has not started; no merge, tag, deployment or cutover is authorized.

### Slice 4 Step 1-4 WIP Checkpoint - 2026-09-14

Implementation resumed from PR #264 checkpoint
`414e2f515e203f0fae6f1368828ee48affc68eb7` and intentionally stopped after
steps 1 through 4 from `slice-04-handoff.md`. This is a WIP/checkpoint state,
not the accepted Slice 4 milestone. PR #264 remains draft on
`codex/csharp-port`. No Terra review, final Slice 4 freeze, native migration
acceptance, accepted milestone, exact-milestone remote CI or Slice 5 work has
started.

Completed at this checkpoint: existing-settings Staff Access UI and scoped audit
history; accepted versioned staff lifecycle flows, cleanup-result display and
super-admin-only confirmed/reasoned rebind; browser accessibility/layout fixes;
focused real-SQL coverage for every inheritable scalar's system save, library
override save, effective library read and per-field reset; scoped audit/system
authorization coverage; and retained local parent browser harness coverage for
Staff Access, audit, stale staff/scope loads and organization activation/version
HTTP behavior.

Checkpoint validation:

- `dotnet build Asap.sln --configuration Release --no-restore`: passed with
  zero warnings/errors.
- `node tests/run_all.js`: all 170 discovered test files passed. Existing
  Windows `grep`/`true` noise in `no_dao_usage.test.js` remains nonfatal and
  that test reports PASS.
- `dotnet test tests\Asap.Tests\Asap.Tests.csproj --no-restore --filter "FullyQualifiedName~AdministrationInheritableScalarsSaveResolveAndResetPerField|FullyQualifiedName~AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole"`:
  passed, 2/2.
- Fresh published Web snapshot
  `.artifacts/asap-slice-04-web-precheck-slice-04-step4-20260914c` and parent
  browser run
  `.artifacts/acceptance/patron-browser/f6e7cf022b314288a53c0a0e4c16de06/admin-settings-results.json`:
  23/23 scenarios passed, 17 browser states captured, diagnostic mode off,
  zero external browser requests, and the aggregate serious/critical
  accessibility, horizontal-overflow and image-rendering gate passed.
- Published DACPAC SHA-256:
  `8ad550e2b75a5f24d24a4af086207e01a64daaa5c80727491b2ef7edce454034`.

Remaining Slice 4 work begins at step 5: complete final Release/.NET/real-SQL/
Node validation on final bytes, publish the self-contained native migration
artifact, re-export and rerun all four stopped fixtures/oracles/native
acceptance modes, verify final source/artifact hashes, then start Terra review.

### Incomplete Slice 4 Checkpoint - 2026-09-13

The user requested a commit and push of the current work so implementation can
continue in a new conversation. This is an explicitly authorized work-in-progress
checkpoint, not the accepted Slice 4 milestone. Slices 0-3 remain complete;
Slice 4 remains the first incomplete slice. The same Luna context is paused,
and no Terra review has started. PR #264 remains draft on `codex/csharp-port`.

Start the next conversation with [the Slice 4 handoff](https://github.com/clcdpc/asap-pocketbase/blob/codex/csharp-port/docs/implementation/slice-04-handoff.md).
It records remaining implementation, known browser failures, verification
limits, retained agent identity, and local-only acceptance harnesses. Use the
existing checkout to retain those harnesses. The full Slice 4 review base stays
`4769a8a8750c315e319824355d4508073bd43546`, not this intermediate checkpoint.

Checkpoint checks: `dotnet build Asap.sln --configuration Release --no-restore`
passed with zero warnings/errors; `node tests/run_all.js` passed all 168 test
files. The full .NET/real-SQL suite and published browser/native acceptance were
not rerun for this checkpoint. Staff Access/audit UI, accessibility/layout,
broader inventory/race coverage, final verification and independent reviews
remain open. Any checkpoint CI result is diagnostic only; acceptance still
requires the completed milestone's actual exact-SHA remote CI before Slice 5.

### Implementation Resumed - 2026-09-13

The user explicitly resumed implementation after the completed Slice 3 stop.
Earlier pause and preparation entries below remain historical evidence.
Astra fetched the repository, selected `codex/csharp-port`, and verified a
clean worktree at PR #264 head `4769a8a8750c315e319824355d4508073bd43546`.
The Luna execution-policy commit `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`
and subsequent Astra-first policy commit `4769a8a8750c315e319824355d4508073bd43546`
are both present. Slices 0-3 remain accepted and Slice 4 is first incomplete.
Fetched `main` still equals the behavioral pin, and all 19 pack hashes match.
The draft PR has no review threads/comments; recovered-head remote CI run
`34760402589` succeeded. The deployed source SHA remains unverified.

Astra read the complete authoritative pack and refreshed `slice-04.md` against
the accepted staff/auth/request, AdditionalCopy, schema, runtime configuration,
migration and frontend contracts. Slice 4 now enters implementation with a
fresh Luna Max context; independent Terra High review has not started.
Acceptance, milestone commit and exact-milestone remote CI remain pending.
Normal progression after that gate is authorized without a new user prompt.

### Historical Slice 4 Execution Policy - 2026-09-13

Slices 0-3 were completed under the prior Sol implementation model. Beginning
with Slice 4, GPT-5.6 Luna Max is the default primary implementer; GPT-5.6 Terra
High remains the independent reviewer and GPT-6 Astra Max remains orchestrator.
GPT-5.6 Sol High is a focused escalation advisor only; Sol XHigh is exceptional
escalation only. Sol is outside the normal slice lifecycle.

Astra owns progression, slice boundaries, focused packets, cross-slice
integration, acceptance verification, milestone commits, PR/status maintenance,
and escalation decisions. Before every remaining slice, refresh its existing
packet against accepted prior implementation and dispatch a fresh Luna Max
context. Luna owns the complete slice, including C#, SQL/DACPAC, migration,
frontend, tests, test-failure diagnosis and directly affected documentation.
After implementation and required tests, dispatch a fresh Terra High context.
Retain the same Luna for all confirmed-review fixes/retesting and the same
Terra for the entire slice's full review/re-review sequence. Terra reports
findings; it never implements its own fixes.

Keep at least two full-slice Terra passes, with a third when Pass 2 finds a new
substantive issue. A clean Pass 2 needs no ceremonial third pass. Confirmed
findings return to the same Luna, followed by affected/full required tests and
full same-Terra re-review. Record only nonblocking leftovers after Pass 3;
blocking correctness, security, data-integrity, migration or material-regression
findings cannot be waived by a pass cap. Astra verifies acceptance, commits and
pushes the milestone only after all existing gates pass, then requires actual
remote CI success for that exact milestone before dispatching the next slice.
Migration code and reconciliation continue with every data-owning slice.

Luna Max always escalates first to Astra Max for focused consultation on a
material blocker: conflicting/undetermined behavior, a Terra finding that
cannot be resolved confidently, repeated failures caused by unclear invariants,
cross-slice design questions, provider limitations affecting a contract, an
architectural deviation, or a material slice-boundary/deferral question. No
fixed number of Luna failures is required. Luna must not bypass Astra or
dispatch Sol directly.

Astra owns the complete contract, accepted decisions, cross-slice state,
packet scope, current implementation, progression and acceptance. Astra
inspects the authoritative pack, accepted implementation, pinned source and
tests/evidence, decides which contract governs, narrows the problem and gives
Luna a concrete implementation ruling whenever the evidence suffices. The same
Luna implements/tests; the same Terra verifies through normal review/re-review.

Only Astra may dispatch fresh Sol High after focused analysis determines that
material uncertainty remains or independent/deeper specialist advice has
material value under document 10's criteria. Sol analyzes only the bounded
problem and provides root cause, alternatives, the smallest faithful resolution
and affected invariants/tests. Astra evaluates the advice against the pack and
accepted implementation and decides the resolution; Luna implements/tests and
Terra independently verifies. Sol XHigh requires Astra's determination that
bounded Sol High advice still leaves a blocking issue unresolved or exceptional
cross-system reasoning is required. Neither Sol model takes slice ownership.
Code size, difficult SQL, migration, concurrency, authentication, configuration,
deployment, an external integration or a routine Luna question alone does not
justify Sol. The authoritative hierarchy is Luna -> Astra -> optional Sol by
Astra's decision -> Astra ruling -> Luna implementation/tests -> Terra review.
The complete policy is in `../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md`.

Prepared packets and source notes are committed as clearly labeled documentation
checkpoints. They do not certify a slice, replace its independent review, or
authorize early implementation. Refresh each packet against accepted prior
code before dispatch; keep application milestone commits scoped to one slice.

The prior 2026-09-13 instruction to finish Slice 3's tests, independent Terra
review/fix/re-review, milestone and remote CI, then stop with a clean Git working
tree was successfully satisfied. Implementation is intentionally paused at the
clean Slice 3 boundary, ready to resume with Slice 4 under the new model. This
documentation-only update does not dispatch or start Slice 4. Prepared later
packets remain preparation only; the port and deferred Postmark/rehearsal/release
gates are not complete.

At the prior Luna-transition update's start, local HEAD, fetched implementation
branch and draft PR #264 all matched `3d4a4d9fb28983fb3ac25489e9a641d9b38abdfe`;
the working tree was clean and fetched `origin/main` still matched the behavioral
pin. At this Astra-first refinement's start, all three implementation heads
matched `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`, the Luna-transition policy
commit; the working tree was clean, PR #264 was draft and fetched `origin/main`
still matched the pin. Prepared Slice 4-11 packets are unchanged and Slice 4
remains not started. Validation for this refinement is documentation-only: JSON
examples, local references, current-policy/history separation and final pack hashes. Slice 3's
190 application/SQL/browser tests below are retained prior evidence, not rerun
or claimed as new implementation/release validation by this update.

### Accepted Slice History

The following table and chronological execution entries preserve Slices 0-3's
original evidence, including Sol ownership and interim states that were later
closed. They do not prescribe the current remaining-slice model.

| Slice | Scope | Status |
| --- | --- | --- |
| 0 | Branch, skeleton, engineering baseline | Complete: reviewed milestone `0096777`, remote CI passed |
| 1 | Patron login and submission | Complete: `c1b8655` plus reviewed correction `1e36761`; 120 tests and remote Linux CI passed |
| 2 | Staff Entra and core request workflow | Complete: reviewed milestone `9f946ae`; 181 local and remote Linux CI tests pass, no skips |
| 3 | Additional-copy workflow | Complete: reviewed milestone `85539b0`; 190 local/remote tests, native/published-browser checks and full Terra Pass 5 clear |
| 4 | Administration and configuration | Candidate-d final local gates and retained Terra full Pass 3 clean; final milestone and exact-SHA CI result in the [acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639) |
| 5 | Background workflows and complete email operations | Not started |
| 6 | Analytics | Not started |
| 7 | Migration hardening and legacy links | Not started |
| 8 | Deployment, health, monitoring, release artifacts | Not started |
| 9 | CI, browser, accessibility, release integration | Not started |
| 10 | Explicit synthetic seed/reset tooling | Not started |
| 11 | Legacy removal, canonical docs, final review | Not started |

Slice 1 uses its existing packet and pinned behavior/schema notes. On
2026-09-12 the user explicitly authorized a minimal `FileEmailSender` at only
the final provider transport boundary. This supersedes the original Slice 1
Postmark dependency gate, not any SQL outbox or application behavior contract.
See `temporary-email-transport.md` and the retained compatibility evidence in
`clc-package-probe.md`. Continue subsequent slices without waiting for Postmark;
the real provider remains a release/rehearsal blocker.

Slice 1's local handoff passed a zero-warning/error Release build, 104/104
.NET tests with none skipped, legacy tests, real-SQL/Kestrel/Playwright patron
journeys and published-artifact checks. Astra independently reran the full
.NET suite and the published self-contained migration executable's positive/
negative import and reconciliation checks. Fresh Terra High Pass 1 has reported
findings; Sol's fixes passed 114/114 tests and corrected native-artifact checks.
The same reviewer's full Pass 2 resolved those findings and found one remaining
request-specific CSP issue. That fix now passes 118/118 tests, including an
independent Astra rerun, and all fourteen pinned-source/real-HTTP CSP cases
against a new verified publish. The same Terra completed full Pass 3 with no
substantive finding; all six findings are resolved. See `slice-01-review.md`.
This coherent milestone closes Slice 1's local acceptance/review gate. Remote
CI for the new milestone must pass before Slice 2 dispatch; earlier remote CI
does not certify the Slice 1 implementation.

Slice 1 milestone: `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44`, pushed to the
same draft PR. Its [remote CI run](https://github.com/clcdpc/asap-pocketbase/actions/runs/34730692517)
failed with 77 passing and 41 failing tests, none skipped. Migration certificate
lookup opened an unsupported Unix machine store; patron application setup also
rejected the SQL-authenticated Linux CI database. Sol's narrow corrections and
security-boundary regressions pass 120/120 local tests, independently rerun by
Astra, and fresh native/browser artifact checks. The same Terra's full closure
review is clean. The correction is cleared for commit/push and a new Linux run;
Slice 2 remains gated on that actual result. Future-slice preparation documents
were excluded from the Slice 1 commit.

The reviewed correction `1e36761c771db70d0b669087d0a843b66cf5618b` passed
[remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34731687718):
120 .NET/SQL tests, zero failures/skips, frontend tests and both publication
checks. Slice 1 is closed. The runner's upstream Node-action deprecation
annotation is nonblocking and does not add Node to application build/runtime.
Fetched `origin/main` still equals the PocketBase pin. Slice 2 proceeds using
its existing prepared packet and a fresh Sol XHigh context.

Slice 2's current implementation checkpoint has a clean Release build and
passing focused real-SQL/hosted profile, lifecycle and request-version/stage
tests. Entra/current-policy authorization, profile and lifecycle endpoints,
scoped request views/mutations and the initial staff Polaris adapter are in
progress toward the complete slice. Focused pickup/create/reply/ambiguity tests
now pass; broader concurrency/recovery, full browser/testing and independent
Terra gates remain open. Astra's isolated native-import fixtures identified
S2-A1 recipient-report parity, S2-A2 claim-history/attribution preservation and
S2-A3 historical-rule claim eligibility corrections. All three now pass on a
fresh source-built native snapshot, including the pinned-function recipient
oracle and byte-identical equivalent fresh-target imports; see
`slice-02-evidence.md`. This is interim acceptance, not Slice 2 completion.
Parent inspection also identified S2-A4: same-patron/BIB list differences do
not prove operation correlation for ambiguous hold recovery or null-ID
enrichment. Sol is correcting that path and its tests under the existing safe
fallback contract; this correctness finding remains open until verified.

The completed Slice 2 handoff is now frozen with 46 changed code/schema/test
files and 579 verified published payload files. Astra independently passed all
174 tests with zero skips, final native original/expanded staff fixtures and
their executable pinned-source recipient oracles, plus the published Web
patron browser/SQL regression. S2-A4's corrected no-inference/F2 tests and
S2-A5's scoped editor/barrier/mobile-grid fixes pass locally. Fresh Terra High
`Ohm` is performing full Pass 1 against that snapshot. The review/fix/re-review
cycle, coherent milestone and actual remote CI remain open; no Slice 2 commit
or production-completion claim has been made.

Terra Pass 1 completed with two P1 findings: staff metadata can commit after
actor authorization changes, and manual hold resolution does not establish
the claimed operation-specific evidence/executor exclusion. Sol is fixing the
confirmed metadata race; a fresh bounded Astra Max consultation is specifying
the smallest faithful manual hold-evidence boundary. Neither finding is
waived. See `slice-02-review.md`; the same Terra will perform full Pass 2 after
the corrections and fresh test/artifact gates.

The bounded ruling is recorded: manual resolution may accept explicit,
operation-specific operator attestation of actual external proof, with separate
executor-exclusion evidence and server-side version/phase/ownership fencing.
It may not turn an arbitrary reference, expired lease, or cancellation request
into proof. Sol's metadata fix passed red/green real-SQL race tests and related
lifecycle regressions; the hold API/form/dispatch correction is in progress.
`hold-resolution-operator-evidence.md` records the operational trust boundary,
which later deployment/rehearsal work must verify rather than simulate.

Both Pass 1 corrections are implemented. Astra independently passed the full
181-test suite with zero skips, verified the new 579-payload artifact and all
frontend sources/compressed variants, and reran original/expanded native SQL
migration plus the pinned-source recipient oracles. The twelve-state staff
browser journey includes actual operator-resolution submission and exact SQL
evidence/ID/epoch/once-only event/audit/outbox verification. The same Terra is
performing full Pass 2 against this corrected 48-file freeze. No Slice 2
milestone or production approval is claimed before that review gate.

Terra's full Pass 2 is approved with no actionable findings: S2-T1 and S2-T2
are closed. Local Slice 2 acceptance/review gates are satisfied; the coherent
milestone and its actual remote CI are next. A fresh fetch still has no change
from the pinned PocketBase baseline. Later-slice preparation stays out of the
Slice 2 commit. No merge, production tag, deployment or production-completion
claim follows from this local milestone.

Slice 2 milestone `9f946aed4b091a82407ac929345819d0a0c87b10` is pushed to
the same draft PR: 57 coherent files, including 48 reviewed implementation/CI
files and current evidence/docs. Future-slice preparation is excluded. Await
actual CI for this exact commit before starting fresh Sol Slice 3; no previous
green run is substituted for that gate.

That exact milestone passed [remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34744506275)
on 2026-09-13: zero-warning/error Release build, 181 .NET/real-SQL/browser
tests passed with zero failures/skips, legacy/frontend tests and publish checks.
Slice 2 is closed. Fresh Sol XHigh now implements Slice 3 from its focused
packet; the retained Slice 2 implementation/reviewer contexts are closed.

See `slice-00-evidence.md` for actual build, SQL, startup and publish checks.
Milestone `00967778001e7ec8198ab4498d0fbd15ded4d984` passed the complete
[remote CI run](https://github.com/clcdpc/asap-pocketbase/actions/runs/34703502557).
No business/provider/entity-migration/browser/rehearsal/release gate is claimed
passed by the documentation pack or the engineering baseline.
Slice evidence belongs beside each focused packet. Final completion requires
the specified whole-application review, complete operational artifacts, exact
tagged-artifact permanent-nonproduction rehearsal, and release-readiness gates.

Local prerequisites verified: .NET SDK 10.0.303; SQL Server 2022 Developer
Edition (64-bit), version 16.0.1200.5, default local instance with working
Windows authentication. These checks are not application acceptance tests.

## Release Boundaries

Slice 3's final source and 666-file published candidate passed independent
acceptance: 190 .NET/SQL/browser tests with zero failures/skips, four native
migration fixture variants, published desktop/mobile lifecycle and delayed
candidate/mutation regressions, and frontend/vendor/compression checks. The same
Terra completed five full-slice review passes; all confirmed findings are fixed
and Pass 5 is clear. See `slice-03-review.md` and `slice-03-evidence.md`. A fresh
fetch still matches the PocketBase pin and all 19 pack hashes match. Milestone
`85539b0ba34937e31396181adfc87c383b8a0cd8` is pushed to the same draft PR and
passed [remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34755432713)
on 2026-09-13: zero-warning/error Release build, 190 tests with zero failures or
skips, legacy/frontend tests and both publication checks. Slice 3 is complete.
That was the accepted Slice 3 stop boundary. Slice 4 subsequently completed
the corrective/local acceptance gates recorded above; its exact-milestone CI
gate is recorded separately. No merge, tag, deployment or production approval
follows from a slice milestone.

The temporary file sender is not production transport. Release/rehearsal cannot
pass until a Rest 3-compatible `Clc.Postmark.Api` supports cancellable async
sending, replaces `FileEmailSender`, and passes provider integration/webhook,
transport-specific and release-validation tests. No simulated webhook or local
file may stand in for those gates. The port is not production complete while
this work remains outstanding.

Permanent nonproduction remains PocketBase until the complete reviewed port
is merged and tagged. Production-hostname preflight uses only a disposable
SQL database. The final migration target stays fresh and stopped until import,
usable-super-admin provisioning/validation, and reconciliation succeed.

After .NET accepts production writes, it is authoritative. Retired PocketBase
is forensic-only and cannot start as-is. Any necessary execution uses an
isolated copy with outbound Polaris/email blocked and recurring jobs disabled.
