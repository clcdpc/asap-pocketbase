# ASAP Development Plan

## Current development

Develop the .NET 10 / ASP.NET Core 10 application on `main`, with SQL Server
2022 and the schema-13 DACPAC. See [current status](docs/implementation/PORT-STATUS.md)
and [CI qualification](scripts/ci/test-selection.md) for maintained contracts.
The original porting slices and [completion record](docs/implementation/final-development-completion.md)
are historical evidence. The PocketBase proof of concept, migration executable
and migration/oracle gates are retired; Git history preserves them.

## Maintained development gates

1. Build the complete solution in Release with zero warnings and errors.
2. Discover the complete native .NET test population and its disjoint Browser
   and non-browser partitions. Require exact discovered identities, complete
   passing outcomes, nonzero expected-test guards and zero skips. Use real SQL
   Server 2022 and deterministic provider boundaries.
3. Run every retained root frontend test and the portable acceptance files
   declared in `tests/frontend-tests.json`. Keep complete per-file receipts.
   Run the Browser partition's Playwright journeys and frontend asset contracts,
   preserving axe, origin, page-error, image and overflow guards.
4. Use the three hosted qualification workers: Frontend, CI checks and package;
   Non-browser .NET/SQL; and Browser/SQL. Each builds the exact tested source.
   Keep fixture commands serial within each worker. Guarded evidence reuse must
   revalidate matching inputs, exact discovered coverage and successful original
   execution; current-head checks and packaging remain required.
5. Publish Web with its DACPAC, verify frontend/vendor bytes and publish
   exclusions, and validate the test-IIS ZIP's exact identity and digest before
   extraction. Keep build/test/package separate from the IIS activation runner.
   Deployment-script `-ValidateOnly` must not mutate an IIS host.
6. Check documentation links, removed imports/paths/scripts, secrets,
   generated output, and whitespace from the cleaned tree.

## Deferred work

Initial test-IIS activation is recorded as `test_cd_activation: active` in the
[November 5, 2026 receipt](docs/implementation/test-iis-activation.md). It proves
that dated deployment, readiness and state recording; it does not establish
current-main activation or current host health. Further host/runner access and
deployment require separate authorization.

Live providers, production readiness/tags/deployments, cutover, backup/recovery
rehearsal and repository rename remain deferred. Optional synthetic seed/reset
tooling is outside the maintained application qualification gates.
