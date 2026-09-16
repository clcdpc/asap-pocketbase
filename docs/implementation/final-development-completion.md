# Final Development-Completion Batch

Reduced Slice 9 and Slice 11 run as one combined batch on
`codex/final-development-completion`, targeting `codex/csharp-port`.

## Bootstrap

- Verified starting and technical review base:
  `ce0f4693ea3e98ed81efcf93241456b99050fb56`.
- Exact starting `.NET baseline` CI: `35109947096`, successful.
- Slice 8 accepted; PR #264 remains open/draft into `main`.
- Phase A: hosted browser/accessibility CI, then a validated checkpoint.
- Phase B: consumer-led PocketBase cleanup and canonical .NET documentation,
  followed by full validation from the cleaned tree.
- One fresh independent Terra High holistic review follows both phases.
  Confirmed findings use bounded Luna fixes and independent verification,
  with at most three fix cycles.

The combined draft PR holds the canonical supervisor state and append-only
events under [document 10](../dotnet-port/10-CODEX-MULTI-MODEL-TASK.md).
Phase A implementation is recorded below; acceptance remains pending until
every required local and hosted gate has passed.

## Phase A browser and accessibility inventory

The baseline default .NET test run discovers and executes 313 tests, including
the three browser fixture methods. Those methods are ordinary tests, not
opt-in or skipped tests; they require the hosted SQL connection, the Testing
environment's deterministic providers, Node.js, and Chromium. Their existing
fixture lifecycle already starts loopback Kestrel, seeds isolated SQL state,
and disposes the host and browser.

The existing runner coverage is:

- Patron: 10 desktop/mobile states plus 3 authentication and session race
  scenarios, with SQL assertions for successful and duplicate submissions.
- Staff: 20 desktop/mobile states covering scope, recovery, queue, analytics,
  assignment, stale mutations, and keyboard workflows.
- Legacy links: 18 desktop/mobile success and failure states for shared,
  numeric, and type-qualified request links.

Every runner binds requests to the loopback app origin and fails on external
requests, page errors, serious or critical axe findings, horizontal overflow,
or unintended missing images. No material journey gap was found in inventory.
The Phase A gap was CI discoverability and enforcement: the workflow had no
explicit full browser command, no browser-specific test partition, and no
failure-only browser diagnostic upload. Phase A wires `npm run test:browser`
to the three existing tests, runs the remaining 310 tests separately in
hosted CI, and uploads only ignored `.artifacts/browser` evidence on failure.
The legacy-link fixture now writes into that same safe artifact tree.

This preserves the 313-test total without running the browser journeys twice.
The CI checkout already uses `github.sha`, and the existing packaging identity
gate verifies that exact commit before artifact creation. No live Polaris,
Postmark, or IIS dependency was added. Validation results are recorded below
after the local matrix completes.

## Phase A validation record

Local validation on 2026-09-16 reached these results:

- Clean Release build: passed with 0 warnings and 0 errors.
- Real-SQL partitions: non-browser `310/310` passed with 0 failed and 0
  skipped; browser `3/3` passed with 0 failed and 0 skipped. Together these
  cover the baseline 313 discovered tests without duplicate browser runs.
- Frontend and Slice 8 contract checks: all 176 frontend test files passed;
  the test-IIS workflow, digest, isolation, and exclusion contract passed.
- Browser evidence: patron 10 major states plus 3 race assertions, staff 20
  states, and legacy links 18 states. All 48 axe-scanned states had 0 serious
  or critical findings and 0 overflow states; origin, page-error, and image
  assertions passed. Reports are under `.artifacts/browser/`.
- Publish/native checks: Web 338 files and the self-contained win-x64
  migration 251 files passed; all 17 vendor hashes matched, the DACPAC and
  migration executable were present, one `e_sqlite3.dll` was present, and
  forbidden PocketBase/Node/dev-email files were absent.
- A current package was created at
  `.artifacts/phase-a-current-package-20260916-1238.zip` with a matching
  sidecar digest, 333 entries, and a manifest bound to the final checkpoint
  commit. The deployment script's `-ValidateOnly` extraction passed without
  host mutation. No acceptance or independent review is claimed; hosted CI
  remains configured to execute the same gate.

## Boundaries

Slice 10 remains deferred/optional. Test-IIS activation remains
`pending_runner_setup`, supported by the accepted Slice 8 journal and the
skipped IIS job in the starting CI. Repository-variable access was unavailable
to the bootstrap token; no activation or successful deployment is claimed.

Production readiness remains [deferred](deferred-production-readiness.md).
This batch does not activate the IIS runner, merge PR #264, create a production
tag, deploy production, or implement optional seed/reset tooling.
