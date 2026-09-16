# Final Development-Completion Batch

Reduced Slice 9 and Slice 11 run as one combined batch on
`codex/final-development-completion`, targeting `codex/csharp-port`.

## Current state

The Phase A browser/accessibility checkpoint is complete. Phase B consumer-led
cleanup and canonical documentation are implemented, and the cleaned-tree
local matrix is green through publish and focused migration checks. The full
local implementation validation is recorded at
`74c1b1008963bfb460d89aa374fd245789dd89d4`; its Slice 8 package has 333
entries and SHA-256
`570d2a7257b96de132f5cfbb0be7a41e043d451befa29c18fb4749917e6fc9ee`, bound to
that implementation checkpoint. The prose-only head
`a4fa224864004279cb679738fd1f155c795645dc` subsequently passed hosted PR CI
`35129263256`, including browser, publish, and package checks; IIS was skipped.
The hosted run is current-head evidence; the local package remains bound to
`74c1b1008963bfb460d89aa374fd245789dd89d4`.

Initial independent Terra review of `a4fa224864004279cb679738fd1f155c795645dc`
found only FDC-1 [P2]. This documentation-only correction is awaiting focused
independent verification and does not self-resolve the finding. Integration,
final milestone, and supervisor acceptance remain pending. Later
documentation-only head/CI updates are tracked in the
[canonical live PR #272 supervisor state](https://github.com/clcdpc/asap-pocketbase/pull/272#issuecomment-5700413998).

- Technical review base: `ce0f4693ea3e98ed81efcf93241456b99050fb56`.
- Phase A checkpoint: `ec609e38588edcdde8cf45d5c75cc790f359cc1d`.
- Phase A PR CI: `35124500838` succeeded; IIS was skipped.
- Full local implementation validation: `74c1b1008963bfb460d89aa374fd245789dd89d4`;
  its local Slice 8 package has 333 entries and SHA-256
  `570d2a7257b96de132f5cfbb0be7a41e043d451befa29c18fb4749917e6fc9ee`.
- Hosted current-head PR CI: `a4fa224864004279cb679738fd1f155c795645dc` /
  `35129263256` succeeded, including browser, publish, and package checks;
  IIS was skipped.
- Draft PR #264 remains open into `main`; this batch does not merge it.
- FDC-1 remains pending fresh focused Terra verification; no integration,
  final milestone, or supervisor acceptance is claimed here.

## Phase B consumer and retention record

The inventory was completed before deletion and classified actual consumers:

- `src/Asap.Web` and `src/Asap.Web/Frontend` own shipped HTTP, feature, and
  browser behavior; MSBuild copies tracked frontend source to generated
  `wwwroot`.
- `database/Asap.Database` owns the SQL Server 2022 DACPAC and intentionally
  retains `LegacyPocketBaseMapping` for source IDs, legacy links, and migration
  audit.
- `src/Asap.Migration` directly reads a stopped legacy SQLite source, uses its
  pinned source SHA/schema contract, and imports/reconciles SQL. It does not
  consume the retired source migration files.
- `tests/Asap.Tests`, `tests/browser`, `tests/run_all.js`, and the retained
  frontend tests own current backend, migration, browser, and frontend
  regression coverage.

The cleanup removed the unconsumed PocketBase runtime/hooks/source migrations,
legacy public/backend JavaScript, runtime SDK material, 158 legacy Node tests,
benchmark/mock helpers, and obsolete planning/design artifacts. It retained 18
Node tests for shipped frontend contracts, all C# and browser fixtures, pinned
vendor assets/notices, migration/source schema notes, the `docs/dotnet-port`
pack, accepted Slice 8 package/IIS contracts, and deferred production
references. No legacy implementation was moved into a new runtime subtree.

The root-doc audit was individual: `README.md`, `ARCHITECTURE.md`, `PLAN.md`,
and `STACK.md` were rewritten for the actual .NET application and commands;
`DESIGN.md` and `PRODUCT.md` remain current domain/design contracts;
`RESEARCH.md` and `TODO.md` were obsolete source-path lists and were removed.
Obsolete `.planning`, `.jules`, `.opencode/plans`, and `docs/superpowers` files
were removed after link/consumer inspection. Concrete migration, schema,
operator, accepted-slice, and deferred-release notes remain.

## Phase A validation receipt

The checkpoint retained these results and evidence:

- Release build: 0 warnings and 0 errors.
- Real SQL: 310 non-browser plus 3 browser tests, all passed with 0 failures
  and 0 skips; total 313.
- Frontend: 176 test files passed before cleanup. Current validation must use
  only the retained set and must not count removed files.
- Browser: patron 10 states plus 3 races, staff 20 states, legacy links 18
  states; 48 axe states with 0 serious/critical findings and 0 overflow.
- Publish: Web 338 files; native win-x64 migration 251 files including one
  `e_sqlite3.dll`; vendor 17/17; required DACPAC/executable present and
  forbidden files absent.
- Slice 8 package: 333 entries, exact checkpoint identity, SHA-256 sidecar,
  and deployment-script `-ValidateOnly` extraction passed without host
  mutation.

Safe evidence paths are `.artifacts/browser/`,
`.artifacts/slice-08-validation/web/`,
`.artifacts/slice-08-validation/migration/`, and the retained
`.artifacts/phase-a-current-package-20260916-1238.zip` with its `.sha256`
sidecar. These are compact artifacts only; no raw logs or transcripts are
part of the receipt.

## Required cleaned-tree validation

The cleaned-tree local implementation validation record is for
`74c1b1008963bfb460d89aa374fd245789dd89d4`:

- Release build: 0 warnings and 0 errors.
- Real SQL: non-browser `310/310` plus browser `3/3`, for `313/313` total,
  with 0 failures and 0 skips.
- Frontend: current retained `18/18` Node tests passed; no removed test files
  are included in the count.
- Browser: all patron/staff/legacy-link runners passed with their existing 48
  axe-scanned states, 3 race scenarios, origin/page-error/image/overflow
  guards; no serious or critical findings were reported.
- Publish: Web `338` files; self-contained win-x64 migration `251` files,
  exactly one `e_sqlite3.dll`, required DACPAC/executable present, forbidden
  publish files absent; source and published vendor hashes `17/17`.
- Focused migration regression/oracles: `25/25` passed with 0 failures/skips.
- Slice 8 package: `333` entries with `330` packaged Web files; the manifest
  and ZIP SHA-256
  `570d2a7257b96de132f5cfbb0be7a41e043d451befa29c18fb4749917e6fc9ee` are
  bound to this checkpoint. Digest-before-extract, runner isolation, and
  deployment `-ValidateOnly` passed with no host mutation.
- Hygiene: 82 Markdown files have 0 missing relative links; no removed roots or
  current legacy imports, no tracked generated/secret paths, the Slice 8
  workflow/deployment contract passes, and `git diff --check` is clean.

The prose-only head `a4fa224864004279cb679738fd1f155c795645dc` then passed
hosted PR CI `35129263256`, including the browser, publish, and package checks;
IIS was skipped. This hosted evidence validates the current head but does not
rebind the local package recorded above.

## Boundaries

Slice 10 remains deferred/optional. `test_cd_activation:
pending_runner_setup` remains unchanged; no runner/host activation is
performed. No live Polaris/Postmark calls, production tag/deploy, checkout on
IIS, PR #264 merge, repository rename, or production readiness is claimed.
The stopped legacy source is migration/forensic input only, never a writable
fallback after cutover.
