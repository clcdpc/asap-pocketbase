# Reuse of current test evidence

See [test selection and qualification](test-selection.md) for the current DAG
and dependency audit. Schema 4 is the only supported evidence schema; earlier
populations and the former two-executor workflow cannot qualify current tests.

Reuse is per suite (dotnet, browser, frontend), never a reduced passing count.
A successful original run must belong to the trusted repository/workflow,
correct execution job and runner group. Checkout logs, Git commit/tree/blob
identity, source and resolved test-dependency fingerprints, exact native test identities,
outcomes, SHA-256 artifact digest and the 14-day lifetime are revalidated.
Skipped or failed source steps, corrupt archives, altered population,
ambiguous PR metadata and unavailable lookup all fail closed.

Selecting the complete required population is separate from requiring fresh
execution. A valid PR diff containing documentation, CI/build tooling or unknown
paths can reuse an original successful manual Full CI run when the effective
inputs match. Included source, CI/test tooling, the complete workflow execution
contract, repository policy and discovered populations must still match. A new
CI implementation needs a fresh baseline; evidence from the earlier logic cannot
qualify it. No run numbers or commit ancestry are special-cased.

Throwaway environment differences (VM/kernel/native packages, installed runtime
versions, locale, Docker and the mutable SQL image) remain provenance rather than
reuse keys. The environment key retains the configured policy epoch, resolved
NuGet package contents and Playwright/browser definitions. Tool manifests and
lock files remain included source inputs. Unknown runners, unavailable identity
and ambient runtime extensions still disable reuse. Fresh tests resolve and use
the current SQL image digest.

Full CI execution plans and verifies dotnet, browser and frontend independently
in one job on one clc-linux runner. The verifier always evaluates all three
suites; no executor-scope setting can narrow that population. Each original
manifest names Full CI execution and must independently match its own successful
test step and evidence-upload step. A skipped step needs verified original
evidence for that suite. Missing or failed execution cannot be hidden by another
suite's success. The reporting-only Build, test and package gate requires the
consolidated executor to succeed; draft validation retains its distinct result.

The current Release solution is built once. Fresh tests run sequentially:
frontend, non-browser .NET/SQL, then browser. One SQL Server 2022 container is
started only if dotnet or browser needs execution. It has one ephemeral password,
a unique job-owned name and a dynamic loopback port; the suites retain their
independently owned databases and cleanup. Both native suites can reuse evidence
without starting SQL or installing Chromium. Full reuse still runs current-source
build, safety checks, native discovery, evidence revalidation and packaging.

Current-commit build, publication, frontend verification, DACPAC/package content,
manifest/digest checks and ValidateOnly always execute. Release tags,
force-full and test-IIS qualification require fresh suites. Deployment remains
separately gated and is outside ordinary implementation.

To qualify a reuse change without duplicate suites, push the final CI-only
revision on a branch without a PR, manually dispatch it with `force_full_ci=true`
and both deployment inputs false, and wait for the required gate to pass. Then
open a ready PR on that unchanged revision. Its normal PR-triggered run must
report `REUSED` for all three suites at final verification and pass
`Build, test and package`. Do not change relevant inputs between these runs.
The planner and final verifier both honor metadata fallbacks and explicit fresh
requirements; a saved plan alone never authorizes skipped tests.

The exact JS/CSS backend exclusions are reviewed in test-reuse.json. Unknown
files default to included. Browser consumers and asset/cache/publication
contracts live in Browser; the complete frontend suite remains required.
Never extend exclusions based only on directory names or unchanged test counts.

Historical receipts and superseded selection designs remain in Git history.
Current sequential-runner measurements and review findings are recorded in
[test-selection.md](test-selection.md#sequential-runner-qualification-record).
The preceding population/dependency audit remains in
[ci-simplification.md](../../docs/implementation/ci-simplification.md).
