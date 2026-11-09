# Reuse of current test evidence

See [test selection and qualification](test-selection.md) for the current DAG
and dependency audit. Schema 7 is the only supported evidence schema; earlier
populations and former single-/two-executor workflows cannot qualify current tests.

Reuse is per partition (dotnet_a, dotnet_b, browser_a, browser_b) and frontend, never a reduced passing count.
Frontend selection includes root *.test.js files and the explicit portable
acceptance declaration in tests/frontend-tests.json. File receipts must match
discovery exactly once with zero failures/skips; node:test subtest counts do not
replace file identities. Acceptance helpers/suites/registry and the consumed
browser confirmation/Settings helpers invalidate frontend evidence when changed.
An original qualification must belong to the trusted repository/workflow,
correct execution job, exact Ubuntu 24.04 label and GitHub Actions runner group
(name and ID 0). Checkout logs, Git commit/tree/blob
identity, source and resolved test-dependency fingerprints, exact native test identities,
outcomes, SHA-256 artifact digest and the 14-day lifetime are revalidated.
Skipped or failed source steps, corrupt archives, altered population,
untrusted PR metadata and unavailable lookup all fail closed. Successful Full CI
execution (including packaging) and the required reporting gate are mandatory;
a later independent IIS deployment status does not invalidate their results.
Qualification can be reused while deployment is queued/running or after it
fails. The successful executor and reporting gate authorize reuse; the overall
workflow's deployment-dependent status does not.

Original artifacts are named by schema, suite and the complete fingerprint.
The repository artifacts API queries that exact name, with at most three pages
of 100 matching originals and the existing time/size bounds. Unrelated, draft
and reuse-only runs cannot hide a baseline in a latest-run search window.
On rerun, upload-artifact overwrites only the same name within that run; the
manifest and API still bind the original run attempt. A deployment-only retry
preserves that successful qualification. Every later attempt (at most ten) is
checked for failed qualification. A retry must show an explicitly scheduled
deployment-only job or all successful workers plus the reporting gate; empty or still-active
qualification cannot pass. Missing evidence fails closed. Evidence age
uses the job-bound execution timestamp, not the run's original creation date.

Selecting the complete required population is separate from requiring fresh
execution. A valid PR diff containing documentation, CI/build tooling or unknown
paths can reuse any original successful Full CI run when the effective inputs
match, regardless of its originating event or the current trigger. PR, manual,
branch, tag, release and deployment events share the same evidence contract.
An identical literal commit or proven-equivalent input tree is not forced fresh
because the event changed. Included source, CI/test tooling, the complete workflow execution
contract, repository policy and discovered populations must still match. A new
CI implementation needs a fresh baseline; evidence from the earlier logic cannot
qualify it. No run numbers or commit ancestry are special-cased. Native suite
keys contain only that suite's discovered UIDs/methods; every prepare/verify
still checks the complete catalog's disjoint union and reviewed classification.

Throwaway environment differences (VM/kernel/native packages, installed runtime
versions, locale, Docker and the mutable SQL image) remain provenance rather than
reuse keys. The environment key retains the configured policy epoch, resolved
NuGet package contents and Playwright/browser definitions. Tool manifests and
lock files remain included source inputs. Unknown runners, unavailable dependency identity
and ambient runtime extensions still disable reuse. Fresh tests resolve and use
the current SQL image digest. Observational tool probes are best-effort; reuse
does not contact the SQL registry, require Docker, or install Chromium/native
browser packages. These dependencies are prepared only for fresh execution.
The final verifier re-reads resolved NuGet and Playwright identities and ambient
extension guards after lookup.

Five standard GitHub-hosted Ubuntu 24.04 execution jobs independently build the same exact event SHA.
Frontend/CI checks/package, non-browser .NET/SQL A/B and browser/SQL A/B become eligible
concurrently after hosted preflight. Separate builds are accepted; compiled bytes
need not be identical or transferred. Fixed job IDs in the validated executor
map determine suite ownership; an ambient suite-scope flag cannot narrow the
overall requirement. Each worker discovers the complete disjoint population,
plans only its assigned suite and verifies its actual result or re-fetches the
original evidence after a planned skip.

Draft validation also uses Ubuntu 24.04; DEVWEB/IIS deployment keeps its local
Windows runner. Environment epoch `github-hosted-ubuntu-24.04-2026-11-08-v1`
requires a fresh hosted baseline: local-runner results cannot qualify hosted tests.
Schema 7 rejects whole-suite evidence from the former three-worker layout.

Each manifest names its owning job and must match that job's successful test,
verification and evidence-upload steps and independently verified checkout.
Every originating qualification must also contain all five successful workers,
the successful package publish/ZIP/ValidateOnly/upload steps and the successful
Build, test and package reporting gate. A successful partial worker never
qualifies the entire run. Non-deployment later attempts must independently show
all five successful workers and the reporting gate; a deployment-only retry
preserves the original qualification. Worker decisions/verification artifacts
include job, run and attempt; original lookup names retain suite/fingerprint
identity. Reused results never become new original evidence.

Each native job starts its own digest-resolved SQL Server 2022 container only
when its suite executes fresh, with ephemeral credentials, a unique
job/run/attempt/random name and a dynamic loopback port. Each retains serial
fixture/test-command execution and owned database/process/container cleanup.
Browser installs Chromium only for fresh browser execution. Full reuse still
runs all current-source builds, mandatory safety checks, complete native
discovery, final evidence revalidation and current-commit packaging.
The hosted reporting gate requires all five job successes, each suite's explicit
verification output and validated/uploaded packaging. Missing, failed, cancelled
or unexpectedly skipped workers or verification fail; draft reporting stays
distinct. Package creation may finish before native tests; deployment still
requires the complete gate and existing authorization.

Current-commit build, publication, frontend verification, DACPAC/package content,
manifest/digest checks and ValidateOnly always execute. Only explicit force-full
or invalid/missing selection metadata demands fresh suites. PR selection binds
the actual synthetic merge's second parent to the event/current head and proves
its first parent belongs to fetched trusted main history. Advertised base SHAs
are metadata and may lag or advance to a commit not present on the runner.
They cannot change the authorization of an unchanged tested merge. A changed
head, untrusted state or wrong actual parent still fails closed. Tags are verified
against their peeled commit, including annotated tags; release and deployment
metadata must identify that same tested source. Deployment remains
separately gated and is outside ordinary implementation.

To qualify reuse without duplicate suites, let one successful Full CI run
establish original evidence for the final inputs. A later supported event at
that commit or equivalent tree must report `REUSED` for all five suites at
final verification and pass `Build, test and package`. Do not change relevant
inputs between these runs. A test tag can exercise reuse while retaining its
normal authorized test-IIS activation; creating such a tag can really deploy.
Use `force_full_ci=true` when fresh testing is wanted explicitly.
The planner and final verifier both honor metadata fallbacks and explicit fresh
requirements; a saved plan alone never authorizes skipped tests.

The short directory/file ownership policy lives in `source_inputs` in
test-reuse.json; the first matching rule wins. Directory paths cover descendants
and filename globs match one directory level. Application/database/build/CI
inputs, native tests and shared fixtures are common. Browser runtime directories
belong to Browser; root Node contracts and their helpers belong to Frontend.
The test runner, browser entrypoint and both mandatory CI safety contracts remain
common execution inputs. Native keys use the independently validated projected
classification rather than another suite's population.

Authored frontend JS/CSS belongs to Browser/Frontend. Vendor assets, HTML,
manifests and unknown file types stay conservative. This ownership follows the
reviewed Browser classification of every asset/publication/cache and Chromium
consumer. A consumer guard rejects direct native/browser imports of frontend
test files or their root runner. New/unclassified roots remain included in every
suite until deliberately classified; they cannot silently evade qualification.

Narrative `docs/`, root Markdown and declared operator-documentation locations
are ignored. Required deployment tests exercise actual scripts, generated
configuration and structured fixtures, rather than substrings in prose.
There is no global README/Markdown/text exclusion: packaged vendor license files
and runtime/test fixtures remain inputs, regardless of their extension.

Historical receipts and superseded selection designs remain in Git history.
Historical sequential-runner measurements and review findings are recorded in
[test-selection.md](test-selection.md#sequential-runner-qualification-record).
The preceding population/dependency audit remains in
[ci-simplification.md](../../docs/implementation/ci-simplification.md).
