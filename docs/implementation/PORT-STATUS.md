# ASAP .NET Port Execution Record

The implementation replaces PocketBase with .NET 10 / ASP.NET Core 10, SQL
Server 2022, and IIS under the authoritative `docs/dotnet-port/` pack. This
record preserves accepted-slice evidence while the current target is a
development-complete port. Production readiness and cutover remain deferred.

## Current Status - Final Development-Completion Batch - 2026-09-16

Slices 0-7 remain accepted under their recorded review and CI evidence. Slice 7
passed independent review and short Astra High acceptance.
[PR #267](https://github.com/clcdpc/asap-pocketbase/pull/267) is merged into
`codex/csharp-port` at `333330da11b5fcaeafaca3fc820eba68fc4953d3`, preserving
the exact reviewed tree at `bcd5190ef951fcaf93a0843833b78c89b20ab2af`.
All substantive findings have independent Terra resolution; the full-slice
fix count is 1/3. See the [Slice 7 acceptance evidence](slice-07.md#acceptance-record---2026-09-16).

Reduced Slice 8 repository-side test CI/CD is implemented and passed independent
review and short Astra High acceptance. Its start/technical review base was the
accepted Slice 7 milestone `88fd92cf1fdd856dccba6ef3538182d80325e98a`
(successful exact-SHA CI `35086949437`). [PR #271](https://github.com/clcdpc/asap-pocketbase/pull/271)
integrates `codex/slice-08-test-deployment` into `codex/csharp-port` at
`a68611e3d839f31d340c3d684046e15165b5b931`. That merge preserves the exact
reviewed tree at `c5cc3ba427e447c073c66d0d729b694e6514b63c`.

The one holistic Terra review found S8-FULL-1; a bounded Luna fix and fresh
focused Terra verification [resolved it independently](https://github.com/clcdpc/asap-pocketbase/pull/271#discussion_r4027205366),
including native thread resolution. Zero substantive findings remain; fix cycle
count is 1/3. Local gates passed: Release build with zero warnings/errors,
.NET/real-SQL 313/313 with zero failures/skips, 176 frontend test-file processes,
Web and native migration publication, 589-file exclusion and 17-vendor-hash
checks, deployment package validation/tamper rejection, workflow isolation,
PowerShell parsing and diff checks. Exact reviewed-candidate CI `35108769162`
and PR CI `35108776174` succeeded; the IIS job was skipped in both.

This documentation commit is the candidate acceptance milestone. Final Slice 8
acceptance is effective only after its own exact-SHA `.NET baseline` CI succeeds.
The resulting SHA, CI run/result and final **Accepted - repository-side test
CI/CD implemented** certification are maintained in the
[canonical Slice 8 state](https://github.com/clcdpc/asap-pocketbase/pull/271#issuecomment-5698135527)
and PR #264, without a self-referential follow-up commit. At record creation,
that final exact-milestone CI gate had not yet run.

The current target is the combined reduced Slice 9 + Slice 11 final
development-completion batch: accepted behavior and migration correctness,
normal Release/real-SQL/frontend/publish gates, focused browser and
accessibility coverage, consumer-led legacy cleanup, canonical .NET
documentation, and one integrated final review. Production deployment,
cutover, and operational-readiness evidence remain deferred in
[deferred-production-readiness.md](deferred-production-readiness.md).

Phase A checkpoint `ec609e38588edcdde8cf45d5c75cc790f359cc1d` passed PR CI
`35124500838`; the IIS job was skipped. Phase B inventory and cleanup now
classify the actual consumers: `Asap.Web`/Frontend own shipped behavior,
`Asap.Database` owns the DACPAC and intentional legacy-ID mapping,
`Asap.Migration` owns stopped-source native SQLite migration, and the current
C#/browser/frontend suites own regression coverage. The obsolete PocketBase
runtime/source/public tree and dependent legacy tests/docs are removed. The
cleaned-tree local matrix is green through publish, focused migration checks,
and the exact package identity/validate-only gate. That package gate is
repeated after the receipt-only final amend so the pushed SHA remains its
manifest identity.

PR #264 remains open/draft into `main`; its current summary is synchronized by
the supervisor. `test_cd_activation: pending_runner_setup` is the only allowed
Slice 8 activation state until a separate task records a real IIS deployment.
No runner installation, registration, host contact, live IIS deployment,
production tag, PR #264 merge, review, or acceptance is claimed here. Runner
activation is a separate later operational task and need not precede this
batch; see [the activation checklist](test-iis-activation.md). Slice 10 is
optional/deferred. The combined Slice 9 + Slice 11 batch is in progress pending
cleaned-tree validation, exact candidate CI, integration, and supervisor review.

## Slice Outcome Summary

This is the authoritative quick status view. Exact accepted anchors are retained
here; chronological entries below preserve checkpoint-time execution evidence.

| Slice | Scope | Outcome | Accepted milestone / CI |
| --- | --- | --- | --- |
| 0 | Branch, skeleton, engineering baseline | Accepted | `00967778001e7ec8198ab4498d0fbd15ded4d984` / `34703502557` |
| 1 | Patron login and submission | Accepted | Reviewed corrective milestone `1e36761c771db70d0b669087d0a843b66cf5618b` / `34731687718`; original candidate retained in chronology |
| 2 | Staff Entra and core request workflow | Accepted | `9f946aed4b091a82407ac929345819d0a0c87b10` / `34744506275` |
| 3 | Additional-copy workflow | Accepted | `85539b0ba34937e31396181adfc87c383b8a0cd8` / `34755432713` |
| 4 | Administration and configuration | Accepted | `417c72430652a35bc8fc1da549ae270eabc86429` / `34861199803` |
| 5 | Background workflows and complete email operations | Accepted | `36727414d02cbe34ba13cd3f6f1bb57980b83a6f` / `34952097695` |
| 6 | Analytics | Accepted; later post-acceptance correction completed | Historical milestone `7ba59421176ede99ba48488be6bc81010e60f65c` / `34976630186` |
| 7 | Migration hardening and legacy links | Accepted | `88fd92cf1fdd856dccba6ef3538182d80325e98a` / `35086949437` |
| 8 | Reduced test-environment CI/CD to IIS | Repository implementation complete; review/short acceptance passed; activation pending runner setup | Exact milestone / CI / final acceptance: [canonical state](https://github.com/clcdpc/asap-pocketbase/pull/271#issuecomment-5698135527) |
| 9 | Focused development CI, browser and accessibility completion | Combined with Slice 11; Phase B cleanup/docs in progress pending validation/review/CI | - |
| 10 | Synthetic seed/reset convenience tooling | Deferred / optional | - |
| 11 | Reduced .NET cleanup, canonical docs and one integrated final review | Combined with Slice 9; Phase B in progress pending validation/review/CI | - |

The separate post-Slice-6 reviewed correction is
`04f538ef1a04be33b31d5dbdc3a5c1751b163ee4`; corrected product/integration
baseline `d607723e846f633ebe206163f6c232dd79566d6f` passed CI `34988112346`.
It is not a new slice and does not replace the historical Slice 6 milestone.

## Historical Checkpoints

All entries below preserve checkpoint-time status and evidence. Their status,
next actions and deferred claims are qualified to the checkpoint date and do
not override the current summary above. Technical contracts retain their
historical meaning unless the current policy block explicitly narrows the
remaining execution scope.

## Slice 7 Integration And Acceptance Checkpoint - 2026-09-16

The three independently reviewed packages merged as PR #268 at
`307282fc3a53147101d80964a961f8e58e882204`, PR #269 at
`002f8e2b0517d9116a658caf103843ae7f40e292`, and PR #270 at
`919998a363fe78ddb690e07365a8d075607d42b0`. Integrated validation at the last
SHA passed: build 0 warnings/errors, .NET/real-SQL 312/312 with no failures or
skips, 175 frontend test-file processes with no failures, 18 legacy-link,
10 patron and 20 staff browser states, and zero serious/critical accessibility
findings. Migration remediation, published self-contained win-x64 workflows
and Web/native SQLite/pinned DACPAC linkage passed; CI `35048541380` succeeded.
No integration glue changes were required.

Holistic Terra review from technical baseline
`d607723e846f633ebe206163f6c232dd79566d6f` found only `S7-FULL-1` (WAL-backed
source identity). Luna fixed it at `bcd5190ef951fcaf93a0843833b78c89b20ab2af`;
fresh focused Terra [explicitly resolved it](https://github.com/clcdpc/asap-pocketbase/pull/267#issuecomment-5696175981).
Affected evidence passed at that SHA: export 6/6, validation 3/3, migration CLI
25/25, full suite 313/313, build 0 warnings/errors, published WAL/no-WAL,
malformed-WAL rejection and fresh import/reconcile gates. Exact-SHA CI
`35058299692` succeeded. No expensive successful local gates were repeated
during acceptance; unaffected frontend/browser evidence remained valid.

The stale FIX projection was advanced by sequence 24 `FIX_COMPLETED`, then
independent re-review and short acceptance completed at sequences 25 and 26.
Full-slice fix count remained 1/3, with zero unresolved substantive findings.
PR #267 merge and resulting integration SHA were both
`333330da11b5fcaeafaca3fc820eba68fc4953d3`; its tree matched the reviewed SHA.
The guarded integration preserved the expected first parent
`e1fc18b7b9b4d2d90892e16bf0264da5e2dc6246` and required no ready transition.
The later documentation-only milestone required its own successful CI;
its exact SHA/result and final acceptance were to be recorded in PR #267's
journal and PR #264's Current State. Release/rehearsal gaps remained explicit.

### Historical Slice 7 Bootstrap - 2026-09-15

The authorized supervisor created draft PR #267 on
`codex/slice-07-migration-hardening` from
`e1fc18b7b9b4d2d90892e16bf0264da5e2dc6246`. Initial refs, all three baseline
CI anchors, clean-start conditions and the unchanged PocketBase source pin
were verified. The corrected product baseline
`d607723e846f633ebe206163f6c232dd79566d6f` was the technical review base;
later documentation/process bytes were classified separately. At bootstrap,
package selection, implementation and acceptance had not yet occurred.

### Pre-bootstrap Policy Checkpoint - 2026-09-15

Historical Slice 6 acceptance remains at
`7ba59421176ede99ba48488be6bc81010e60f65c` (CI `34976630186` succeeded).
[Correction PR #266](https://github.com/clcdpc/asap-pocketbase/pull/266) is
merged and complete. Its exact Terra-reviewed correction is
`04f538ef1a04be33b31d5dbdc3a5c1751b163ee4`, merged at
`c0cde6fb744e53c1a72a63f8cb58124e43255b4f`. The corrective integration/current
authorized product baseline before this policy edit is
`d607723e846f633ebe206163f6c232dd79566d6f`; its exact-SHA
[.NET baseline CI 34988112346](https://github.com/clcdpc/asap-pocketbase/actions/runs/34988112346)
succeeded. No substantive corrective finding remains.

At this policy checkpoint, Slice 7 had not started. The documentation/process
refinement preceded bootstrap and created no new accepted product slice.
[Document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md) now makes GPT-6
Astra High the normal thin supervisor, GPT-6 Astra Max bounded escalation
only with control returning to High, and the slice PR the durable state/event
journal. Bounded Luna Max implementation and independent Terra High review,
compact receipts, the context firewall, full validation, three-cycle cap and
exact-SHA acceptance gates remain binding. Terra owns substantive finding
verification/resolution; comments must never contain sensitive migration data.

The GitHub-state policy landed at `c7fca3c2eda178bfb24c826f446086f31278ce1a`,
with successful exact-SHA CI `34992493001`. This follow-up documentation-only
cleanup clarifies historical status and independent Terra dispositions when
native thread-resolution tooling is unavailable. It does not start a slice.
The latest authorized docs-cleanup head was the future Slice 7 branch point.
Its exact SHA and CI run/result are recorded in
[draft PR #264](https://github.com/clcdpc/asap-pocketbase/pull/264), after push
and CI, without another repository commit merely to record its own hash or
green CI. Bootstrap must separately record the prior product baseline and
actual technical review base under document 10. No Slice 7 branch, PR,
supervisor comment or worker dispatch is part of this policy task.

Desired external configuration: automatic Codex GitHub review disabled for
this repository, explicit/manual review retained. The policy task could not
access that setting: available tools expose no setting control and the
available browser reached signed-out ChatGPT. One external/manual setting
change remains; this record does not claim it is disabled. Document 10 defines
the fallback for any unexpected automatic review.

The next action at that checkpoint, after successful exact-SHA CI, was one GPT-6
Astra High autonomous Slice 7 bootstrap/execution task using the GitHub-backed
supervisor journal, with GPT-6 Astra Max reserved for bounded escalation.
PR #264 remains draft; no merge, tag, deployment, rehearsal or cutover.

## Post-Slice-6 Correction Pending Exact-SHA CI - 2026-09-15

Slice 6 was accepted at its historical milestone
`7ba59421176ede99ba48488be6bc81010e60f65c`, with successful exact-SHA
`.NET baseline` run `34976630186`. That acceptance and milestone are preserved.

Subsequent review identified two Analytics issues: inactive libraries lost
their request populations from all-library aggregates, and a retained invalid
library selection could strand the frontend. Both were corrected before
Slice 7 in [PR #266](https://github.com/clcdpc/asap-pocketbase/pull/266).
The exact Terra-reviewed correction is
`04f538ef1a04be33b31d5dbdc3a5c1751b163ee4`; its corrective integration merge is
`c0cde6fb744e53c1a72a63f8cb58124e43255b4f`. The merge tree matches the reviewed
candidate, and its first parent is the historical Slice 6 milestone.

At this checkpoint, independent Terra review and one Luna fix/Terra focused
re-review cycle were complete, with no substantive finding remaining. Candidate CI `34986700290`
succeeded. The [review record](slice-06-review.md) distinguishes fresh fix
validation from retained complete-suite/browser evidence. Migration, schema,
and DACPAC surfaces are unchanged; native migration fixtures were not rerun.

At this checkpoint, the documentation-only status commit was the post-Slice-6
corrective milestone candidate. Its own exact-SHA remote CI was the remaining
acceptance gate. PR #266 metadata was to record the resulting `codex/csharp-port`
SHA and CI run/result without another repository commit solely for the CI result.
On success, that SHA would become the authorized integration head for a later
Slice 7 bootstrap. Slice 7 had not started and PR #264 was still draft.
Earlier entries below are historical checkpoints.

## Slice 6 Acceptance Pending Exact-SHA CI - 2026-09-15

Slice 6 Analytics passed implementation validation, full independent Terra
review and one Luna fix/Terra focused re-review cycle. At this checkpoint, no
substantive finding remained. The exact reviewed implementation is
`4f68b2ca849630f5399022432d57d137f4638d8e`.

[PR #265](https://github.com/clcdpc/asap-pocketbase/pull/265) was merged into
`codex/csharp-port` at `80f5c291373737e225e38d5e6d6cae6f79a0b70d`;
the merge tree exactly matches the reviewed implementation. The accepted
prior product milestone is Slice 5 at
`36727414d02cbe34ba13cd3f6f1bb57980b83a6f`, and the Slice 6 policy base is
`5493efee9ce140d6eaa83da2d178e2004fea7c2c`.

Complete local evidence includes Release 0 warnings/errors, 305/305
.NET/real-SQL tests with 0 failures/skips, 174 frontend scripts, 20
desktop/mobile browser states, 13 published-browser cases, zero serious or
critical axe findings, native export/import/reconciliation and Web/native
publication. The aggregate correction has renewed SQL, browser, Release and
Web artifact linkage evidence. See the [review and acceptance record](slice-06-review.md)
for the exact candidates, finding disposition and preserved evidence.

At this checkpoint, the documentation-only acceptance/status commit was the
candidate accepted milestone. Slice 6 acceptance was pending its own successful
exact-SHA remote CI. PR metadata was to record that milestone SHA and CI result
without a further repository commit. Slice 7 had not started; PR #264 was draft.
Earlier dated entries below are historical checkpoints.

## Thin Autonomous Supervisor Policy - 2026-09-15

At this checkpoint, Slice 5 was accepted at
`36727414d02cbe34ba13cd3f6f1bb57980b83a6f`.
Before Slice 6 implementation, the execution policy was refined again from
`1c3b46d13a42bb1a2887136385f3dce18b610c8c` (policy CI `34954502445` succeeded).
At that policy checkpoint, [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md)
made a thin GPT-6 Astra Max autonomous state-machine supervisor preferred/default, with bounded
Luna Max implementation and independent Terra High review workers, compact
receipts and a strict context firewall. Manual/direct phase control remained
fallback at that checkpoint. Technical ownership, complete integrated validation, review
independence, release requirements and exact-milestone CI are unchanged.

Slice 6 was bootstrapped in draft [PR #265](https://github.com/clcdpc/asap-pocketbase/pull/265)
into `codex/csharp-port` and was implemented and ready for independent
review at this checkpoint. Its existing bootstrap was preserved and rebased onto policy
`5493efee9ce140d6eaa83da2d178e2004fea7c2c`; the [handoff](slice-06-handoff.md)
retains original and rebased anchors, with the final synchronization head in
PR #265's receipt. PR #264 was still the final draft port PR at this checkpoint.
Slice 6 was not accepted; Terra review was the next action.
No Slice 7 continuation was authorized at this checkpoint. Earlier policy/state entries are history.

## Slice 6 Analytics Implementation Candidate - 2026-09-15

At this checkpoint, Slice 6 was implemented on `codex/slice-06-analytics` from
starting HEAD `e250ef1f40e8cae79d3761d77330a8560d799d3b`. The candidate was ready
for independent review and remained unaccepted; its exact pushed SHA was
recorded in then-draft PR #265 because a commit cannot embed its own hash. The compact local
candidate receipt is `.git/asap-slice-06-candidate-20260915-final4.json`, with
published source/artifact linkage in
`.artifacts/slice-06-candidate-20260915-final4/artifact-manifest.json`.

Local pre-review evidence includes the preserved successful Release 305/305
real-SQL report, complete Node/frontend 174-file and 317-subcheck coverage,
the dedicated Analytics SQL fixture, 20-state desktop/mobile Kestrel browser
coverage, four native fixture modes, and Web/self-contained migration
publication checks. The Grid.js focus failure was fixed as an ordinary
isolated-JSDOM teardown race; the final candidate suite has no retry masking.
At this checkpoint, PR #265 and PR #264 were draft. Terra had not run.
The next state at this checkpoint was full independent Terra review.
No merge, acceptance, Slice 7, tag, deployment, rehearsal or cutover was
authorized by this checkpoint.

## Historical Slice 6 Bootstrap - 2026-09-15

At this checkpoint, Slice 6 Analytics was bootstrapped on `codex/slice-06-analytics` from
`1c3b46d13a42bb1a2887136385f3dce18b610c8c`, the documentation-policy transition
after accepted Slice 5 product milestone `36727414d02cbe34ba13cd3f6f1bb57980b83a6f`.
No child packages — one Slice 6 integration PR into `codex/csharp-port`.
Draft [PR #265](https://github.com/clcdpc/asap-pocketbase/pull/265) holds the
exact bootstrap head receipt; see the [handoff](slice-06-handoff.md).
The [packet](slice-06.md) named the accepted helpers, schema and fixtures at
this checkpoint. Implementation had not started; direct Luna Max implementation
was the next action at this checkpoint. PR #264 was still the final draft port
PR. This bootstrap authorized no Slice 7, merge, tag, deployment, rehearsal or cutover. Earlier state entries are history.

## Historical Direct-Model Policy Transition - 2026-09-15

At this checkpoint, Slice 5 was reviewed and accepted at
`36727414d02cbe34ba13cd3f6f1bb57980b83a6f`. Its exact-SHA
[CI run 34952097695](https://github.com/clcdpc/asap-pocketbase/actions/runs/34952097695)
succeeded: Release zero warnings/errors, 302/302 .NET/real-SQL tests with zero
failures/skips, 173/173 frontend files and Web/native publication verification.
The [Slice 5 acceptance receipt](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5677892874)
preserves the accepted product milestone record. At this checkpoint, Slice 6
had not started.

At this checkpoint, the staged-PR/direct-model policy was to begin with Slice 6.

At this policy checkpoint, [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md)
governed direct
Luna Max implementation, direct Terra High package and holistic slice reviews,
and short Astra Max bootstrap/contract/acceptance tasks. Optional coherent
work-package PRs target a temporary slice branch; the slice PR targets
`codex/csharp-port`. PR #264 was still the single final draft port PR into `main`
at this checkpoint.
Full integrated slice validation, exact-SHA CI on the resulting integration
milestone, slice ordering and the final repeated whole-app review remain binding.

This docs-only policy transition started from the accepted Slice 5 SHA above.
It created no new accepted product slice and changed no implementation or
technical acceptance contract. Slices 0-5 retain their actual historical policies
and evidence below. Packet updates only point to the new execution mechanics;
package guidance was provisional at this checkpoint. Root `AGENTS.md` needed
no change for that transition. No slice/package branch was created or model
dispatched by this transition. The next authorized work at this checkpoint was
Slice 6 bootstrap under that policy.

## Baseline

- Recorded: 2026-09-12.
- Behavioral PocketBase pin: `150b30b776565194260cc327eeeffdfb46475e81`.
- Fetched `origin/main`: `150b30b776565194260cc327eeeffdfb46475e81`.
- Implementation branch at this checkpoint: `codex/csharp-port`.
- Single draft PR at this checkpoint: https://github.com/clcdpc/asap-pocketbase/pull/264
- Starting branch commit: `c7637a351dad711484c4a0df9613050a1e6b2636`.
- Starting working tree: clean.
- Changes between pin and starting branch: repository instructions and port
  documentation only. There were no intervening `main` fixes to propagate at
  this checkpoint.
- Exact deployed PocketBase production commit at this checkpoint: **unverified**.
  The repository had no GitHub deployment records at this checkpoint. Do not infer deployment from `main`.
- Final historical tag convention: `pocketbase-final-YYYYMMDD`, created or
  verified only after successful .NET cutover, at the exact frozen production
  commit. No final tag had been created at this checkpoint.
- Excluded baseline subtree: `clc-carousel-manual-import-example/`; unrelated
  to ASAP behavior, dependencies, migration, and archival requirements. Its
  removal was planned for Slice 0 at this checkpoint.
- At this checkpoint, all 19 payload SHA-256 values matched `docs/dotnet-port/PACK-MANIFEST.txt`.
- Astra had read root `AGENTS.md`, the pack README, all numbered documents,
  all four examples, and the manifest before implementation changes.

Ordinary PocketBase feature work is frozen for this port. Fetch and inspect
`main` before subsequent milestones; immediately propagate any urgent source
behavior change and its migration implications. Keep tracking the actual
deployed source independently. After the .NET merge, emergency PocketBase
fixes use a temporary branch from the deployed source, immediate equivalent
.NET changes, and a replacement tagged artifact with repeated rehearsal.

## Execution And Evidence

### Slice 5 Acceptance - 2026-09-15

At this checkpoint, Astra had accepted the Slice 5 implementation and
independent-review gates.
Accepted Slice 4 base: `417c72430652a35bc8fc1da549ae270eabc86429`.
Original review candidate: `eb74e00477f28525359fbd851026199a733b4413`.
First fix candidate: `cd47a8d6f0501759d10401d28b0a6c0e32c2ca74`.
Final reviewed implementation candidate: `1da861efc17e23e9f8d97b823044f4494025a036`.

Terra's full Pass 1 and two focused re-reviews were complete at this checkpoint.
Both confirmed P1 findings were resolved at this checkpoint: ordinary
weekly-summary Run Now honored `force=false`, and the ordinary manual job
retained initiating-actor evidence and revalidated authorization at execution
and the locked outbox boundary. Terra's final
recommendation was clean, ready for Astra acceptance at this checkpoint; no
directly related regression, material test gap, unresolved substantive finding
or full-review trigger remained.

Final validation: weekly-summary 10/10, shared manual-job authorization 4/4,
complete .NET/real-SQL 302/302 with zero failures/skips, Release zero
warnings/errors, and Node/frontend 173/173 files. Required earlier publications,
native migration fixtures, browser/accessibility and artifact gates remained
valid for the unchanged inputs at this checkpoint. Candidate [CI run 34950716124](https://github.com/clcdpc/asap-pocketbase/actions/runs/34950716124)
succeeded, including Release, real-SQL/frontend tests and publish verification.

At this checkpoint, the documentation commit introducing this acceptance was
the Slice 5 milestone candidate; the reviewed implementation candidate above
was not that milestone. Final certification still required successful remote
CI associated with the exact new milestone SHA. Its SHA and final CI result
were to be recorded in PR #264's state and conversation acceptance record,
without a follow-up repository commit.
See [slice-05-review.md](slice-05-review.md) for the compact acceptance evidence.
Implementation/test bytes were unchanged after the final Terra review at this
checkpoint. Slice 6 had not started; PR #264 was still draft. The next authorized
task at this checkpoint was the separate orchestration-policy documentation
rework for Slices 6-11. Earlier entries below
preserve their historical status.

### Slice 5 Manual Weekly Summary Authorization Fix - 2026-09-15

The second review-fix candidate started from
`cd47a8d6f0501759d10401d28b0a6c0e32c2ca74`. Terra Pass 1 found the original
weekly-summary Run Now `force=false` dispatch defect; the first Luna fix
resolved it; fresh focused Terra re-review confirmed that resolution but found
that ordinary manual execution no longer retained initiating-actor evidence
for execution-time authorization. This candidate added a purpose-specific
manual ordinary job entry point that carries `StaffJobEvidence`, performs the
same current authorization check as the forced path, and passes the evidence
into the service's locked outbox-creation check. Scheduled ordinary execution
and forced execution remain unchanged in their respective semantics.

Focused weekly-summary tests pass 10/10 with zero skips; shared manual-job
authorization tests pass 4/4 with zero skips. The complete .NET/real-SQL suite
passes 302/302 with zero skips, the clean Release build has 0 warnings/errors,
and the normal Node/frontend suite passes 173/173 test files. Detailed logs and
receipts are retained under
`.artifacts/slice-05-manual-weekly-auth-fix-20260915/`.

At this checkpoint, focused Terra re-review of this new candidate was still
required. Slice 5 remained unaccepted, Slice 6 had not started, and PR #264 was draft.

### Slice 5 Review Candidate - 2026-09-14

The interrupted Slice 5 work resumed from
`7a3919994d182ec1de3552b2861e657817933f69` in the existing checkout. The
implementation and required pre-review validation were complete at this
checkpoint on the fresh candidate bytes, which are recorded in
`.artifacts/slice-05-candidate-review-candidate-20260914l/` and the paired
native/browser receipts under `.git/`. The candidate includes the schema-5
QueueProgress migration, nine finite fair scans, timeout and fulfillment
boundaries, actor/recipient concurrency protection, email operations, and the
scoped admin UI journeys.

At this checkpoint, Slice 5 was a **review-fix candidate; focused Terra
re-review was pending**.
Terra Pass 1 found exactly one substantive P1 issue: the weekly-summary Run Now
endpoint ignored `force=false` and always dispatched the forced path with a new
`ManualRunId`. At this checkpoint, the corrected endpoint bound `force`, dispatched the
ordinary path with no manual run ID when false, and preserved the existing
forced path when true.
Endpoint dispatch tests pass 2/2, the weekly-route ordinary-staff authorization
regression passes 1/1, directly affected weekly-summary tests pass 5/5, and the
complete .NET/real-SQL suite passes 297/297 with zero skips. The clean Release
solution build had 0 warnings/errors. Focused Terra re-review was still required
at this checkpoint; Slice 5 remained unaccepted and Slice 6 had not started.
CI for this WIP candidate was diagnostic only. PR #264 was draft at this
checkpoint; no merge, tag, deployment, rehearsal, cutover, or accepted milestone
was created.

### Slice 5 Review-Fix Checkpoint - 2026-09-14

The starting review candidate was
`eb74e00477f28525359fbd851026199a733b4413`. The confirmed Terra Pass 1 finding
was fixed locally at this checkpoint. Focused Terra re-review had not been
performed and was still required. The focused/full .NET receipt is retained at
`.artifacts/slice-05-run-now-fix-20260914/full-dotnet-real-sql.log`. At this
checkpoint, Slice 5 remained unaccepted; Slice 6 had not started.

### Slice 5 Interrupted WIP Checkpoint - 2026-09-14

At this checkpoint, Slices 0-4 were accepted. Slice 5 was **in progress**, not
review-ready and not an accepted milestone; Terra review had not started. The user stopped the run
and requested preservation only. The WIP checkpoint containing
[`slice-05-handoff.md`](slice-05-handoff.md) has parent
`ae706931da09d2781add4b96bbca9cfc4f768011`; its exact SHA is recorded in PR #264.
The handoff records implemented drafts, incomplete/unrun tests, failed full-run
results and local evidence to preserve. Any WIP CI is diagnostic only.
No acceptance gates or implementation fixes were run for checkpointing.
At this checkpoint, Slice 6 had not started. PR #264 was draft; no
merge/tag/deploy/rehearsal or cutover occurred. Earlier dated entries below are historical.

### Pre-Slice-5 Documentation Cleanup - 2026-09-14

Started from `5c3553254c31f7b41d61025318a410de6f809a05`, with local/remote/PR
heads matching, no later commits and its exact-SHA CI green. Accepted Slice 4
`417c72430652a35bc8fc1da549ae270eabc86429` remained in history; fetched `main`
still matched the PocketBase pin at this checkpoint. The deployed production
SHA was unverified at this checkpoint.

At this policy checkpoint, document 10 permitted a user-directed pushed review candidate after all
pre-review gates, labeled `implemented / ready for independent review`, with
Terra not yet run. It is not acceptance or permission to start a later slice;
the accepted milestone still needs review/fixes, Astra acceptance and its own
exact-SHA CI. `slice-05.md` enumerates the complete pre-review suites,
publications, native/browser/accessibility and artifact checks. Context rotation
preserves the checkout and required local harnesses/evidence under document 10.
The PR description is a concise current summary; historical execution evidence
remains in this repository. At this checkpoint, Slice 5 had not started or been
dispatched, and no review candidate was created by this documentation-only
cleanup. PR #264 was draft; no application acceptance suite, merge, tag,
deployment, rehearsal or cutover was performed by that task.

### Historical Execution Policy - Beginning With Slice 5 (2026-09-14)

This documentation-only refinement started from accepted Slice 4 milestone
`417c72430652a35bc8fc1da549ae270eabc86429`. After fetching origin, local HEAD,
`origin/codex/csharp-port` and draft PR #264 all matched that SHA, with no later
commits. Its exact-SHA [CI run 34861199803](https://github.com/clcdpc/asap-pocketbase/actions/runs/34861199803)
completed successfully. At this checkpoint, Slices 0-4 remained accepted;
no re-review was required solely because the future execution policy changed.

Under the policy at this checkpoint, GPT-6 Astra Max was to remain orchestrator
and acceptance owner beginning with Slice 5. GPT-5.6 Luna High was to own complete
ordinary implementation, tests, diagnosis, documentation and confirmed-review
fixes, normally starting each slice fresh. Luna Max was reserved for bounded
implementation problems that materially benefited from additional reasoning. Size, SQL, authentication, migration, test volume or
elapsed time alone were insufficient reasons under that policy. Returning to
Luna High was preferred at this checkpoint. Luna always escalated first to
Astra under that policy; only Astra could request focused independent Sol High
advice or exceptional Sol XHigh escalation.

The context-retention instruction at this checkpoint was: keep Luna context
while useful; Astra may rotate when it becomes materially
large/repetitive, using document 10's concise handoff and current-code inspection
before editing. Context identity is not an acceptance invariant. Retain detailed
logs locally and return compact command/result/count/hash/path/warning receipts.
Astra verifies every required gate ran and passed, selectively inspecting raw
evidence for failures, mismatches, relevant findings, security/concurrency/
migration concerns or ambiguity. Successful expensive gates are not rerun solely
for Astra to consume the same output again.

Under the policy at this checkpoint, every slice required fresh independent
GPT-5.6 Terra High full Pass 1.
A clean Pass 1 needs no ceremonial full Pass 2. Confirmed findings return to
the current Luna context; Terra reports findings only. After fixes/tests, Terra
reviews fixes, affected callers/invariants, regression surface, tests and fix
interactions. Repeat full-slice review when document 10's systemic/broad-change
or unbounded-regression triggers apply. No pass cap permits a known blocker.
All objective acceptance gates, exact-milestone remote CI and the separate final
whole-app/release/rehearsal gates remain binding.

That checkpoint's complete policy was recorded in document 10; the current
`../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md` governs future execution.
Earlier dated execution/policy entries below and the Slice 0-4 files preserve
what actually happened, including prior model roles, retained contexts, full
Terra pass counts and duplicated acceptance evidence; they do not govern future
slices. This update changed no application/schema/migration/frontend behavior
and did not rerun application acceptance. At this checkpoint, Slice 5 had not
started or been dispatched; PR #264 was draft. No merge, tag, deployment or
cutover was performed.

### Slice 4 Replacement Candidate D - 2026-09-14

At this checkpoint, the bounded CI fixture correction below was implemented with no product,
schema, migration, workflow or configuration-policy change. Fresh candidate
`corrective-20260914d` passed clean Release (zero warnings/errors), 234/234
.NET/real-SQL tests with zero skips, 172/172 Node files, fresh Web/native
publications and hashes, four native fixture/oracle runs (161/164/3/3 checks),
and all 12 published-browser modes (103 states and 14 CSP cases).
Astra verified the complete source/artifact/native/browser evidence and final
desktop/mobile screenshots. The same Terra completed full Pass 3 clean;
all local acceptance gates were satisfied at this checkpoint. The acceptance
record below preserves the replacement milestone and its exact-SHA CI outcome.
At this checkpoint, final certification still required that successful
exact-SHA run, followed by the authorized stop.
No earlier local-green candidate or failed milestone certifies Slice 4.

### Slice 4 Exact-Milestone CI Correction

Attempted milestone `c147c3501db2c852781b21af4826ce589dab622c` was committed
and pushed after the local gates and clean full Terra Pass 2 below. Exact
[CI run 34855842335](https://github.com/clcdpc/asap-pocketbase/actions/runs/34855842335)
failed with 233 passing .NET tests, one failure and zero skips, so it was not
accepted as Slice 4.
The recovery test switched its host to Development while Linux CI uses SQL
authentication, which the unchanged application correctly permits only in
Testing. Invalid startup configuration caused a strict fixture service lookup
to fail before the cookie assertions. This is a test-host mismatch, not
permission to weaken Development/production configuration or authentication.

The retained Luna corrected only the fixture to select genuine cookie and
OIDC handlers in its CI-compatible host, retaining secure antiforgery and all
recovery assertions. Fresh complete candidate-d gates and the retained Terra's
additional full review subsequently passed. A replacement milestone's own
exact-SHA green CI was still required at this checkpoint; c147c35 remains a
failed attempted milestone. The acceptance record linked below preserves the
final-SHA/run record. At this checkpoint, Slice 5 had not started; PR #264 was draft.

### Slice 4 Candidate C Local Gate (Historical)

All corrective and Step-5 local gates passed on candidate c. The retained
Terra High completed full Pass 2 clean and closed the Pass-1 cleanup race;
the same Luna implemented and retested every confirmed finding. Astra verified
the complete source, publication, SQL, migration, browser and review evidence.
No substantive finding remained at this checkpoint. See `slice-04-review.md` and the
`slice-04-review-packet.md` for exact evidence and residual provider boundaries.

The subsequent coherent milestone failed its exact CI as recorded above.
At this checkpoint, final Slice 4 certification still required the replacement
milestone's remote CI green, not checkpoint CI. The final SHA and actual run result are maintained in the
[Slice 4 acceptance record](https://github.com/clcdpc/asap-pocketbase/pull/264#issuecomment-5665538639),
so recording the result does not create a different, untested milestone SHA.
The authorized stop at this checkpoint was after that gate. PR #264 was draft;
Slice 5 had not started or been dispatched. No merge, tag, deployment or
production cutover was authorized at this checkpoint.

### Slice 4 Corrective Pass - 2026-09-14

The authorized pre-Slice-5 corrective pass started from clean local/remote/PR
head `42e65cd3773cf57557e908fe79e01d77d58bac2f`. Fetch and PR inspection confirmed
`codex/csharp-port`, draft PR #264, and `origin/main` still exactly equal to
behavioral pin `150b30b776565194260cc327eeeffdfb46475e81`; no source change needed
propagation. At this checkpoint, full Slice 4 review still started at
`4769a8a8750c315e319824355d4508073bd43546`.

The same Luna Max implementation context had corrected lifecycle activity and
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
14 CSP cases). Candidate b remains historical green. At this checkpoint, the
same Terra's full Pass 2 was clean and Astra's acceptance audit was complete.
Milestone push and exact-SHA CI were still the final gate recorded above.
The Step-4 entries below are historical checkpoints, not final acceptance.
At this checkpoint, Slice 5 had not started; no merge, tag, deployment or
cutover was authorized.

### Slice 4 Step 1-4 WIP Checkpoint - 2026-09-14

Implementation resumed from PR #264 checkpoint
`414e2f515e203f0fae6f1368828ee48affc68eb7` and intentionally stopped after
steps 1 through 4 from `slice-04-handoff.md`. This was a WIP/checkpoint state,
not the accepted Slice 4 milestone. PR #264 was draft on `codex/csharp-port`.
At this checkpoint, no Terra review, final Slice 4 freeze, native migration
acceptance, accepted milestone, exact-milestone remote CI or Slice 5 work had
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

At this checkpoint, remaining Slice 4 work began at step 5: complete final Release/.NET/real-SQL/
Node validation on final bytes, publish the self-contained native migration
artifact, re-export and rerun all four stopped fixtures/oracles/native
acceptance modes, verify final source/artifact hashes, then start Terra review.

### Incomplete Slice 4 Checkpoint - 2026-09-13

The user requested a commit and push of the then-current work so implementation
could continue in a new conversation. This was an explicitly authorized
work-in-progress checkpoint, not the accepted Slice 4 milestone. At this
checkpoint, Slices 0-3 were complete and Slice 4 was the first incomplete slice.
The same Luna context was paused, and no Terra review had started. PR #264 was
draft on `codex/csharp-port`.

The next conversation at this checkpoint was to start with
[the Slice 4 handoff](https://github.com/clcdpc/asap-pocketbase/blob/codex/csharp-port/docs/implementation/slice-04-handoff.md).
It records remaining implementation, known browser failures, verification
limits, retained agent identity, and local-only acceptance harnesses. The
instruction at this checkpoint was to use the existing checkout to retain those
harnesses. The full Slice 4 review base remained
`4769a8a8750c315e319824355d4508073bd43546`, not this intermediate checkpoint.

Checkpoint checks: `dotnet build Asap.sln --configuration Release --no-restore`
passed with zero warnings/errors; `node tests/run_all.js` passed all 168 test
files. The full .NET/real-SQL suite and published browser/native acceptance were
not rerun for this checkpoint. Staff Access/audit UI, accessibility/layout,
broader inventory/race coverage, final verification and independent reviews
remained open at this checkpoint. Any checkpoint CI result was diagnostic only;
acceptance still required the completed milestone's actual exact-SHA remote CI
before Slice 5.

### Implementation Resumed - 2026-09-13

The user explicitly resumed implementation after the completed Slice 3 stop.
Earlier pause and preparation entries below remain historical evidence.
Astra fetched the repository, selected `codex/csharp-port`, and verified a
clean worktree at PR #264 head `4769a8a8750c315e319824355d4508073bd43546`.
The Luna execution-policy commit `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`
and subsequent Astra-first policy commit `4769a8a8750c315e319824355d4508073bd43546`
were both present at this checkpoint. Slices 0-3 were accepted and Slice 4 was
first incomplete. Fetched `main` still equaled the behavioral pin, and all 19
pack hashes matched. The draft PR had no review threads/comments; recovered-head
remote CI run `34760402589` succeeded. The deployed source SHA was unverified
at this checkpoint.

Astra read the complete authoritative pack and refreshed `slice-04.md` against
the accepted staff/auth/request, AdditionalCopy, schema, runtime configuration,
migration and frontend contracts. Slice 4 was entering implementation with a
fresh Luna Max context at this checkpoint; independent Terra High review had
not started. Acceptance, milestone commit and exact-milestone remote CI were
pending. Normal progression after that gate was authorized at this checkpoint
without a new user prompt.

### Historical Slice 4 Execution Policy - 2026-09-13

Slices 0-3 were completed under the prior Sol implementation model. At this
policy checkpoint, GPT-5.6 Luna Max became the default primary implementer
beginning with Slice 4; GPT-5.6 Terra High remained the independent reviewer
and GPT-6 Astra Max remained orchestrator. GPT-5.6 Sol High was a focused
escalation advisor only; Sol XHigh was exceptional escalation only. Sol was
outside the normal slice lifecycle under that policy.

Under the policy at this checkpoint, Astra owned progression, slice boundaries,
focused packets, cross-slice
integration, acceptance verification, milestone commits, PR/status maintenance,
and escalation decisions. The dispatch instruction at this checkpoint was to
refresh each remaining slice's packet against accepted prior implementation
and dispatch a fresh Luna Max context. Luna owned the complete slice under that policy, including C#, SQL/DACPAC,
migration, frontend, tests, test-failure diagnosis and directly affected
documentation. At this checkpoint, the instruction was to dispatch a fresh
Terra High context after implementation and required tests, retaining the same
Luna for all confirmed-review fixes/retesting and the same Terra for the
entire slice's full review/re-review sequence. Terra reports
findings; it never implements its own fixes.

The review instruction at this checkpoint was: keep at least two full-slice
Terra passes, with a third when Pass 2 finds a new
substantive issue. A clean Pass 2 needs no ceremonial third pass. Confirmed
findings return to the same Luna, followed by affected/full required tests and
full same-Terra re-review. Record only nonblocking leftovers after Pass 3;
blocking correctness, security, data-integrity, migration or material-regression
findings cannot be waived by a pass cap. Astra verifies acceptance, commits and
pushes the milestone only after all existing gates pass, then requires actual
remote CI success for that exact milestone before dispatching the next slice.
Migration code and reconciliation continue with every data-owning slice.

Under the policy at this checkpoint, Luna Max always escalated first to Astra
Max for focused consultation on a
material blocker: conflicting/undetermined behavior, a Terra finding that
cannot be resolved confidently, repeated failures caused by unclear invariants,
cross-slice design questions, provider limitations affecting a contract, an
architectural deviation, or a material slice-boundary/deferral question. No
fixed number of Luna failures is required. Luna must not bypass Astra or
dispatch Sol directly.

Under the policy at this checkpoint, Astra owned the complete contract,
accepted decisions, cross-slice state,
packet scope, current implementation, progression and acceptance. Astra
inspects the authoritative pack, accepted implementation, pinned source and
tests/evidence, decides which contract governs, narrows the problem and gives
Luna a concrete implementation ruling whenever the evidence suffices. The same
Luna implements/tests; the same Terra verifies through normal review/re-review.

The specialist instruction at this checkpoint was: only Astra may dispatch
fresh Sol High after focused analysis determines that
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
justify Sol. The authority hierarchy at this checkpoint was Luna -> Astra -> optional Sol
by Astra's decision -> Astra ruling -> Luna implementation/tests -> Terra review.
That checkpoint's complete policy was recorded in document 10; the current
`../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md` governs future execution.

At this checkpoint, prepared packets and source notes were committed as clearly
labeled documentation checkpoints. They do not certify a slice, replace its independent review, or
authorize early implementation. Refresh each packet against accepted prior
code before dispatch; keep application milestone commits scoped to one slice.

The prior 2026-09-13 instruction to finish Slice 3's tests, independent Terra
review/fix/re-review, milestone and remote CI, then stop with a clean Git working
tree was successfully satisfied. At this checkpoint, implementation was
intentionally paused at the clean Slice 3 boundary, ready to resume with Slice 4
under the new model. This documentation-only update did not dispatch or start
Slice 4. Prepared later packets were preparation only; the port and deferred
Postmark/rehearsal/release gates were not complete at this checkpoint.

At the prior Luna-transition update's start, local HEAD, fetched implementation
branch and draft PR #264 all matched `3d4a4d9fb28983fb3ac25489e9a641d9b38abdfe`;
the working tree was clean and fetched `origin/main` still matched the behavioral
pin. At this Astra-first refinement's start, all three implementation heads
matched `fc31c5637f69eb22510bfbac927cb3de1ee5d2e3`, the Luna-transition policy
commit; the working tree was clean, PR #264 was draft and fetched `origin/main`
still matched the pin. Prepared Slice 4-11 packets were unchanged and Slice 4
had not started at this checkpoint. Validation for this refinement was
documentation-only: JSON examples, local references, current-policy/history
separation and final pack hashes. Slice 3's 190 application/SQL/browser tests
below were retained prior evidence, not rerun or claimed as new
implementation/release validation by this update.

Current authoritative slice outcomes are summarized in [Slice Outcome Summary](#slice-outcome-summary)
near the top; the entries below preserve checkpoint-time execution evidence.

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
negative import and reconciliation checks. Fresh Terra High Pass 1 had reported
findings at this checkpoint; Sol's fixes passed 114/114 tests and corrected native-artifact checks.
The same reviewer's full Pass 2 resolved those findings and found one remaining
request-specific CSP issue. At this checkpoint, that fix passed 118/118 tests,
including an
independent Astra rerun, and all fourteen pinned-source/real-HTTP CSP cases
against a new verified publish. The same Terra completed full Pass 3 with no
substantive finding; all six findings were resolved at this checkpoint. See `slice-01-review.md`.
At this checkpoint, this coherent milestone closed Slice 1's local
acceptance/review gate. Remote CI for the new milestone still had to pass
before Slice 2 dispatch; earlier remote CI did not certify that implementation.

Slice 1 milestone: `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44`, pushed to the
same draft PR. Its [remote CI run](https://github.com/clcdpc/asap-pocketbase/actions/runs/34730692517)
failed with 77 passing and 41 failing tests, none skipped. Migration certificate
lookup opened an unsupported Unix machine store; patron application setup also
rejected the SQL-authenticated Linux CI database. Sol's narrow corrections and
security-boundary regressions pass 120/120 local tests, independently rerun by
Astra, and fresh native/browser artifact checks. The same Terra's full closure
review was clean at this checkpoint. The correction was cleared for commit/push
and a new Linux run; Slice 2 remained gated on that actual result. Future-slice preparation documents
were excluded from the Slice 1 commit.

The reviewed correction `1e36761c771db70d0b669087d0a843b66cf5618b` passed
[remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34731687718):
120 .NET/SQL tests, zero failures/skips, frontend tests and both publication
checks. Slice 1 was closed at this checkpoint. The runner's upstream Node-action deprecation
annotation is nonblocking and does not add Node to application build/runtime.
Fetched `origin/main` still equaled the PocketBase pin at this checkpoint.
Slice 2 was proceeding with its existing prepared packet and a fresh Sol XHigh
context.

At this Slice 2 implementation checkpoint, there was a clean Release build and
passing focused real-SQL/hosted profile, lifecycle and request-version/stage
tests. Entra/current-policy authorization, profile and lifecycle endpoints,
scoped request views/mutations and the initial staff Polaris adapter were in
progress toward the complete slice at this checkpoint. Focused
pickup/create/reply/ambiguity tests passed; broader concurrency/recovery, full
browser/testing and independent Terra gates remained open at this checkpoint. Astra's isolated native-import fixtures identified
S2-A1 recipient-report parity, S2-A2 claim-history/attribution preservation and
S2-A3 historical-rule claim eligibility corrections. At this checkpoint, all
three passed on a
fresh source-built native snapshot, including the pinned-function recipient
oracle and byte-identical equivalent fresh-target imports; see
`slice-02-evidence.md`. This was interim acceptance, not Slice 2 completion.
Parent inspection also identified S2-A4: same-patron/BIB list differences do
not prove operation correlation for ambiguous hold recovery or null-ID
enrichment. Sol was correcting that path and its tests under the existing safe
fallback contract at this checkpoint; the correctness finding remained open
until verification.

At this checkpoint, the completed Slice 2 handoff was frozen with 46 changed code/schema/test
files and 579 verified published payload files. Astra independently passed all
174 tests with zero skips, final native original/expanded staff fixtures and
their executable pinned-source recipient oracles, plus the published Web
patron browser/SQL regression. S2-A4's corrected no-inference/F2 tests and
S2-A5's scoped editor/barrier/mobile-grid fixes pass locally. Fresh Terra High
`Ohm` was performing full Pass 1 against that snapshot at this checkpoint.
The review/fix/re-review cycle, coherent milestone and actual remote CI were
still open; no Slice 2 commit or production-completion claim had been made.

Terra Pass 1 completed with two P1 findings: staff metadata can commit after
actor authorization changes, and manual hold resolution does not establish
the claimed operation-specific evidence/executor exclusion. At this checkpoint,
Sol was fixing the confirmed metadata race; a fresh bounded Astra Max
consultation was specifying the smallest faithful manual hold-evidence boundary.
Neither finding was waived. See `slice-02-review.md`; the same Terra was to
perform full Pass 2 after the corrections and fresh test/artifact gates.

The bounded ruling is recorded: manual resolution may accept explicit,
operation-specific operator attestation of actual external proof, with separate
executor-exclusion evidence and server-side version/phase/ownership fencing.
It may not turn an arbitrary reference, expired lease, or cancellation request
into proof. Sol's metadata fix passed red/green real-SQL race tests and related
lifecycle regressions; the hold API/form/dispatch correction was in progress
at this checkpoint.
`hold-resolution-operator-evidence.md` records the operational trust boundary,
which later deployment/rehearsal work must verify rather than simulate.

At this checkpoint, both Pass 1 corrections were implemented. Astra independently passed the full
181-test suite with zero skips, verified the new 579-payload artifact and all
frontend sources/compressed variants, and reran original/expanded native SQL
migration plus the pinned-source recipient oracles. The twelve-state staff
browser journey includes actual operator-resolution submission and exact SQL
evidence/ID/epoch/once-only event/audit/outbox verification. The same Terra was
performing full Pass 2 against this corrected 48-file freeze at this checkpoint.
No Slice 2 milestone or production approval was claimed before that review gate.

At this checkpoint, Terra's full Pass 2 was approved with no actionable
findings: S2-T1 and S2-T2 were closed. Local Slice 2 acceptance/review gates were
satisfied; the coherent milestone and its actual remote CI were the next action.
A fresh fetch still had no change from the pinned PocketBase baseline.
Later-slice preparation was kept out of the Slice 2 commit. No merge, production tag, deployment or production-completion
claim follows from this local milestone.

Slice 2 milestone `9f946aed4b091a82407ac929345819d0a0c87b10` was pushed to
the same draft PR: 57 coherent files, including 48 reviewed implementation/CI
files and checkpoint evidence/docs. Future-slice preparation was excluded.
At this checkpoint, actual CI for this exact commit was still required before
starting fresh Sol Slice 3; no previous green run substituted for that gate.

That exact milestone passed [remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34744506275)
on 2026-09-13: zero-warning/error Release build, 181 .NET/real-SQL/browser
tests passed with zero failures/skips, legacy/frontend tests and publish checks.
Slice 2 was closed at this checkpoint. Fresh Sol XHigh was implementing Slice 3
from its focused packet; the retained Slice 2 implementation/reviewer contexts
were closed.

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
Terra completed five full-slice review passes; all confirmed findings were fixed
and Pass 5 was clear at this checkpoint. See `slice-03-review.md` and `slice-03-evidence.md`. A fresh
fetch still matched the PocketBase pin and all 19 pack hashes matched at this
checkpoint. Milestone `85539b0ba34937e31396181adfc87c383b8a0cd8` was pushed to
the same draft PR and
passed [remote Linux CI](https://github.com/clcdpc/asap-pocketbase/actions/runs/34755432713)
on 2026-09-13: zero-warning/error Release build, 190 tests with zero failures or
skips, legacy/frontend tests and both publication checks. Slice 3 was complete
at this checkpoint.
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
