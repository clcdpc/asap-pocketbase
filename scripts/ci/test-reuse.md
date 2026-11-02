# Reuse of current test evidence

See [test selection and qualification](test-selection.md) for the current DAG
and dependency audit. Schema 3 is the only supported evidence schema; earlier
migration/upgrade populations cannot qualify current tests.

Reuse is per suite (dotnet, browser, frontend), never a reduced passing count.
A successful original run must belong to the trusted repository/workflow,
correct execution job and runner group. Checkout logs, Git commit/tree/blob
identity, source and toolchain fingerprints, exact native test identities,
outcomes, SHA-256 artifact digest and the 14-day lifetime are revalidated.
Skipped or failed source steps, corrupt archives, altered population,
ambiguous PR metadata, environment drift and unavailable lookup all fail closed.

Full CI execution verifies dotnet/frontend; Browser qualification verifies
browser. Both jobs independently plan and verify evidence for their assigned
suites. The required gate waits for both successful jobs, including when a suite
is reused. A missing browser job cannot be supplied by another job's plan.

Current-commit build, publication, frontend verification, DACPAC/package content,
manifest/digest checks and ValidateOnly always execute. Release tags,
force-full and test-IIS qualification require fresh suites. Deployment remains
separately gated and is outside ordinary implementation.

The exact JS/CSS backend exclusions are reviewed in test-reuse.json. Unknown
files default to included. Browser consumers and asset/cache/publication
contracts live in Browser; the complete frontend suite remains required.
Never extend exclusions based only on directory names or unchanged test counts.

Historical receipts and superseded selection designs remain in Git history.
Current measurements and review findings are recorded in
[ci-simplification.md](../../docs/implementation/ci-simplification.md).
