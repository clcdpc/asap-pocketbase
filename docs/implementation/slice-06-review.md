# Slice 6 Review And Acceptance

Recorded 2026-09-15 under document 10's thin autonomous supervisor policy.
Astra verified compact worker receipts, exact branch/PR state and independent
review. Final acceptance is pending the documentation milestone's exact-SHA CI.

## Candidate Sequence

| Stage | SHA |
| --- | --- |
| Accepted Slice 5 product milestone | `36727414d02cbe34ba13cd3f6f1bb57980b83a6f` |
| Slice 6 policy/branch base | `5493efee9ce140d6eaa83da2d178e2004fea7c2c` |
| Synchronized bootstrap / implementation start | `e250ef1f40e8cae79d3761d77330a8560d799d3b` |
| Luna full-review candidate | `15b750c4502228e715d0f586bf0e0003f08cd07d` |
| Final Terra-reviewed implementation | `4f68b2ca849630f5399022432d57d137f4638d8e` |
| PR #265 integration merge | `80f5c291373737e225e38d5e6d6cae6f79a0b70d` |

Changes between the accepted product milestone and policy base are
documentation/process changes. Bootstrap was verified and not repeated.
The actual technical full-review delta was policy base to Luna's candidate.

## Independent Review

Fresh Terra High completed detailed implementation and holistic review of the
complete Analytics slice. It confirmed one P2 finding, S6-P2-1: first literal
holds earlier than request creation contributed to the held count but were
omitted from the average-duration denominator. The contract requires those
rows to contribute zero days to the same held population.

Fresh Luna Max corrected the aggregate and extended the existing real-SQL
fixture to assert the corrected scoped/forged mean of 1.5. Fresh Terra High
focused re-review confirmed the correction, fixture adequacy and artifact
linkage, and independently reran the SQL regression successfully (1/1).
Disposition: clean for short Astra acceptance; no substantive findings or
material uncertainty remain. Fix/re-review cycles: 1 of 3. No full-review
broadening trigger was present.

The full review's explicit stale-401/403 probe observation was a nonblocking
coverage gap under the existing shared-wrapper authentication policy, not a
confirmed product defect. No authentication-policy change was made for it.

## Validation

- Complete integrated Release build: 0 warnings, 0 errors. Complete
  .NET/real-SQL suite: 305 passed, 0 failed, 0 skipped.
- Complete Node/frontend suite: 174 scripts, 317 explicitly reported
  subchecks, 0 failures. The Grid.js JSDOM teardown defect was corrected;
  the final suite passed in one attempt, with no retry masking.
- Dedicated Analytics real-SQL fixture passed. The fix has fresh Release
  build (0 warnings/errors), SQL regression (1/1) and Kestrel browser (1/1)
  evidence, with 0 failures/skips.
- Analytics browser journey: 20 states, 12 desktop and 8 mobile, renewed for
  the fix. Scope/range/auth stale-load and keyboard/focus gates passed.
  Serious/critical axe findings: 0/0. The full published-browser matrix
  passed all 13 cases; unchanged surfaces retain that evidence.
- Native export/import/reconciliation passed original, edge, unmapped and
  sender-conflict fixture modes. Self-contained win-x64 migration publication
  passed; schema and migration inputs are unchanged by the aggregate fix.
- Web publication was renewed for the fix. Its tested/published assembly
  SHA-256 is `9fad433ea622d5a46a059dfb7b976b7a431cc231c43ec3f612e69db730a03fc7`.
  Web and migration DACPAC SHA-256 remains
  `269a3710fb3a986cca89e957e154a8b225b8c03a52354d345dbfd5d98e475743`.
  Source/receipt/manifest hashes match, with zero mismatches or forbidden
  published files. The baseline contains 593 Web/native files; the renewed
  Web-only artifact contains 335 files.
- Candidate CI `34972303038` succeeded for the initial candidate and
  `34974910905` succeeded for the fixed candidate. Neither substitutes for
  the final accepted milestone's CI.

The only implementation/test changes after the full review were
`AnalyticsService.cs` and `Slice6AnalyticsTests.cs`, covered by the fresh fix
gates and focused review. Complete prior evidence remains valid for unchanged
inputs; it is not relabeled as fresh validation of the corrected aggregate.

## Preserved Evidence

Complete candidate: `.git/asap-slice-06-candidate-20260915-final4.json` and
`.git/asap-slice-06-source-linkage-final4.json`. Complete Release TRX:
`.artifacts/slice-06-candidate-20260915-b/validation/final-dotnet.trx`.
Frontend: `TestResults/node-full-final.log`. Native and published-browser
receipts: `.git/asap-slice-06-final-fixtures-20260915-final5/receipt.json` and
`.git/asap-slice-06-final-browsers-20260915-final4/receipt.json`.

Fix: `.git/asap-slice-06-fix1-20260915.json`,
`.git/asap-slice-06-source-linkage-fix1-20260915.json` and
`.artifacts/slice-06-fix1-20260915/artifact-manifest.json`.
Fresh TRXs: `TestResults/analytics-fix1/analytics-fix1.trx` and
`TestResults/staff-browser-analytics-fix1/staff-browser-analytics-fix1.trx`.

## Acceptance And Stop Boundary

Short Astra acceptance verified the exact reviewed SHA, complete integrated
and affected fix receipts, clean independent review, resolved finding, cycle
cap, clean worktree and both draft PRs before authorized integration. PR #265
was then marked ready and merged using the repository's merge-commit method
with an exact-head guard. Its parents are the policy base and reviewed SHA;
its tree is identical to the reviewed implementation.

The documentation-only acceptance/status commit introducing this record is
the candidate accepted Slice 6 milestone. Only its own successful remote
`.NET baseline` CI completes acceptance. Required checks include build,
real-SQL tests, frontend tests and publication. Record its exact SHA/run/result
in PR metadata without another repository commit solely for the CI result.
Implementation/test bytes are unchanged after the final technical review.
Documentation integrity and `git diff --check` must pass before push.

Until that exact CI succeeds, Slice 6 is not accepted. On success, record
Slice 6 accepted and stop. Slice 7 has not started, PR #264 remains draft,
and no release, deployment, rehearsal, cutover or final whole-app review is
performed by this slice acceptance.

## Post-acceptance Corrections

On 2026-09-15, a bounded correction candidate addressed two confirmed
Analytics findings while preserving the acceptance history above. Aggregate
population queries now retain inactive-library history for authorized
all/system super-admin scope while keeping active-library choices, explicit
scope resolution, ordinary staff ownership, and direct Organization 1
exclusion unchanged. The focused real-SQL fixture covers exact before/after
metric deltas, inactive selection rejection, active explicit scope, forged
staff scope, and the system exclusion.

The frontend now performs one guarded recovery when a retained selected
super-admin library returns only `400 invalid_scope`: it resets local scope to
all, retries once, preserves the selected range and usable controls, and
retains focus. Other 400 responses and the shared 401/403 behavior are
unchanged; generation and cancellation guards prevent stale recovery results
from overwriting newer loads or looping.

Correction validation passed with a 0-warning/0-error Release build, focused
real-SQL 1/1, full .NET 305/305/0, full Node 174 scripts with 0 failures,
published Kestrel Analytics 1/1 across 20 states, and axe serious/critical
findings 0/0. No migration, schema, or DACPAC source/artifact surface was
changed; the existing native fixtures and retained publication evidence remain
authoritative. This candidate is for independent review only; PR #264 remains
draft and Slice 7 remains outside scope.

## Cycle 1 Test-Adequacy Disposition

On 2026-09-15, bounded Luna Max fix worker cycle 1 of 3 addressed the two
confirmed Terra test-adequacy findings in `Slice6AnalyticsTests.cs` only.

- C6-T1: B now has uniquely suffixed open and closed additional-copy rows,
  including an aged open row. The fixture asserts exact all/system aggregate
  metrics and populations before and after B deactivation, while retaining
  active A scope and forged ordinary-staff identity/ownership assertions.
- C6-T2: the fixture adds uniquely suffixed Organization 1 additional-copy
  rows and exact-compares both `all` and `system` aggregates before and after
  insertion. `TitleRequest` system ownership remains schema-prohibited; the
  persisted copy path is exercised without schema changes. Explicit `scope=1`
  rejection remains covered.

Fresh gates: Release build 0 warnings/0 errors, focused real-SQL Analytics
1 passed/0 failed/0 skipped, and `git diff --check` clean. Fresh evidence is
under `TestResults/analytics-correction-fix1-20260915/`. Retained evidence
continues to cover unchanged surfaces: complete .NET 305/305/0, Node 174
scripts with no failures, focused frontend 3/3, published Kestrel Analytics
1/1 across 20 states with axe serious/critical 0/0, and the existing native
fixture/publication evidence.

Disposition is findings-covered, pending the supervisor's independent Terra
re-review and integration decision. This worker does not claim acceptance;
PR #266 remains draft, PR #264 remains draft, and Slice 7 is untouched.
