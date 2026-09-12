# Slice 0 Evidence

Recorded 2026-09-12. This is engineering-baseline evidence, not port or release
completion. No production deployment/source-database access occurred.

## Implementation And Acceptance

Sol High implemented the complete foundation. Astra independently reran:

- `dotnet build Asap.sln --configuration Release`: success, zero warnings/errors.
- `dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --minimum-expected-tests 44`:
  final post-fix run: 44 succeeded, zero failed/skipped, approximately 28 seconds.
- `npm.cmd test`: all retained legacy/frontend tests passed, exit 0. Before the
  slice, one committed documentation-wording assertion failed after dependencies
  were restored. Its narrow wording update retains the behavioral assertions.
- `rg -n 'new Dao|Dao\(' pb_migrations pb_hooks lib`: no matches. This independent
  check compensates for the existing grep-based legacy check on Windows; no
  unrelated test-runner rewrite was introduced.
- Release web publish into `.artifacts/acceptance/web`: success; DACPAC present,
  all 17 manifested vendored files present with matching SHA-256 values.
- Self-contained `win-x64` migration publish into
  `.artifacts/acceptance/migration`: success. Executable `describe-contract`
  reports slice-00, exact pinned source SHA, expected schema version 1, shared
  credential-protection purpose, and no implemented entity capabilities.
- First-party staged whitespace check, excluding unchanged vendored bytes:
  no errors. The upstream Font Awesome SVG contains original trailing spaces
  that are intentionally preserved. Changed-path/credential scan found only
  example/test values and the ephemeral CI SQL password, no operational
  credentials. Generated output/local config remains ignored.

Sol additionally verified a clean-first publish with generated wwwroot absent,
17/17 asset hashes, no PocketBase SDK, and add/modify/delete/restoration plus
unchanged-file timestamp behavior. No app build/publish step invokes Node/npm.

## Corrected Data-Loss Finding

Astra's integration inspection found `DacDeployOptions.CreateNewDatabase=true`
in the actual local-deployment service. Installed DacFx documentation confirms
this drops/recreates an existing database. Sol changed it to false and added a
real SQL regression that deploys from a missing database, inserts owned data
and an extra table, and redeploys twice through the actual service with both
preserved. The separate destructive-schema-change test still proves data-loss
blocking and retained data. This was fixed before independent review.

## Corrected-Service F5 Smoke

Sol used the freshly built Release binaries without concurrent source edits:

- Dedicated disposable database:
  `AsapSlice0F5Final_f0b06640d35b4efcbfe7768b99b34bd5`.
- SQL Server `16.0.1200.5`, Developer Edition, compatibility 160.
- DACPAC SHA-256:
  `5fe8bfe1c35bde36a4d85211fb2c050794efe25039f1d346ed76ec3e8e4d79bb`.
- Fresh startup: live 200, ready 200; expected SchemaVersion 1 and deployment
  hash matched. Forced Version 2: live 200, ready 503. Restored Version 1:
  ready 200.
- Second hidden startup re-ran the corrected service; live/ready 200 and the
  pre-restart data marker remained intact. Successful hosts had empty stderr.
- An initial relative-content-root helper invocation exited before host/database
  creation; using an absolute content root corrected the verification command.
- Owned database/certificate/config/key/log/state artifacts removed; smoke
  processes stopped and no listener remained on its port. No running sessions.

An earlier interrupted test session had no captured result and was not counted
as passing. It no longer existed when Astra checked; the complete independent
post-fix 44-test run above supplies the final result.

## Independent Review

Terra High Pass 1: no substantive findings and no actionable nonblocking notes.
Terra High Pass 2 confirmed one P1 finding from Astra's final staging probe:
with `core.autocrlf=true`, a fresh checkout converted 11/17 manifested vendor
files and their SHA-256 checks failed. Current working-tree checks alone did
not catch this. Sol added scoped `-text` Git attributes and an isolated Git
index/checkout regression, preserving vendor bytes and hashes. CI requires 44
tests. The first regression run passed assertions but Windows rejected cleanup
of read-only Git objects; the narrowly corrected cleanup and full suite then
passed. Astra's separate fixed-checkout probe showed zero hash mismatches and
its final 44-test run passed.

Terra High Pass 3: clean full-slice re-review in the same context, with no
substantive findings or nonblocking notes. The local acceptance/review gate is
passed and the coherent Slice 0 milestone commit is authorized.

Remote CI has not yet run. It will run on the draft PR after the reviewed
milestone push; a remote failure remains blocking and must be fixed/reviewed.
