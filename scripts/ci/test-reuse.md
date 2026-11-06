# Reuse of current test evidence

See [test selection and qualification](test-selection.md) for the current DAG
and dependency audit. Schema 5 is the only supported evidence schema; earlier
populations and the former two-executor workflow cannot qualify current tests.

Reuse is per suite (dotnet, browser, frontend), never a reduced passing count.
A successful original run must belong to the trusted repository/workflow,
correct execution job and runner group. Checkout logs, Git commit/tree/blob
identity, source and resolved test-dependency fingerprints, exact native test identities,
outcomes, SHA-256 artifact digest and the 14-day lifetime are revalidated.
Skipped or failed source steps, corrupt archives, altered population,
untrusted PR metadata and unavailable lookup all fail closed. Successful Full CI
execution (including packaging) and the required reporting gate are mandatory;
a later independent IIS deployment failure does not invalidate their results.

Original artifacts are named by schema, suite and the complete fingerprint.
The repository artifacts API queries that exact name, with at most three pages
of 100 matching originals and the existing time/size bounds. Unrelated, draft
and reuse-only runs cannot hide a baseline in a latest-run search window.
On rerun, upload-artifact overwrites only the same name within that run; the
manifest and API still bind the original run attempt. A deployment-only retry
preserves that successful qualification. Every later attempt (at most ten) is
checked for failed qualification; missing evidence fails closed. Evidence age
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
manifest/digest checks and ValidateOnly always execute. Only explicit force-full
or invalid/missing selection metadata demands fresh suites. PR selection binds
the actual synthetic merge's second parent to the event/current head and proves
its first parent belongs to fetched trusted main history. Advertised base SHAs
must be on that same lineage, but may lag the actual parent or advance while a
run executes. A changed head, untrusted state, wrong parent or unrelated base
still fails closed. Tags are verified
against their peeled commit, including annotated tags; release and deployment
metadata must identify that same tested source. Deployment remains
separately gated and is outside ordinary implementation.

To qualify reuse without duplicate suites, let one successful Full CI run
establish original evidence for the final inputs. A later supported event at
that commit or equivalent tree must report `REUSED` for all three suites at
final verification and pass `Build, test and package`. Do not change relevant
inputs between these runs. A test tag can exercise reuse while retaining its
normal authorized test-IIS activation; creating such a tag can really deploy.
Use `force_full_ci=true` when fresh testing is wanted explicitly.
The planner and final verifier both honor metadata fallbacks and explicit fresh
requirements; a saved plan alone never authorizes skipped tests.

The exact JS/CSS backend exclusions and audited standalone Node contracts are
reviewed in test-reuse.json. The issue-label workflow and its Node contract only
invalidate frontend evidence. Mandatory CI safety contracts stay included in
all suites and execute before lookup. A consumer guard rejects new direct native
or browser references to excluded Node tests. Unknown/new files default to
included. Browser consumers and asset/cache/publication
contracts live in Browser; the complete frontend suite remains required.
Never extend exclusions based only on directory names or unchanged test counts.

Historical receipts and superseded selection designs remain in Git history.
Current sequential-runner measurements and review findings are recorded in
[test-selection.md](test-selection.md#sequential-runner-qualification-record).
The preceding population/dependency audit remains in
[ci-simplification.md](../../docs/implementation/ci-simplification.md).
