# Slice 11: Legacy Removal And Canonical Documentation

## Current combined-batch scope

Reduced Slice 11 completed with Slice 9 as the final development-completion
batch, merged through PR #272 into `codex/csharp-port`. The
[completion record](final-development-completion.md) and its live journal hold
the exact milestone CI and acceptance. No required development slice remains.
Consumer inventory precedes deletion. The target tree contains the shipped
.NET web application, DACPAC, migration executable, current frontend, useful
frontend regression tests, browser fixtures, and concrete migration/recovery
history. Retired implementation is not moved into a new legacy runtime tree;
Git history remains its archive.

## Cleanup contract

- Remove PocketBase hooks, source migrations, backend JavaScript, public source,
  runtime SDK/dependencies, and tests only when the inventory shows no current
  web, migration, test, or deferred-process consumer.
- Keep the SQL `LegacyPocketBaseMapping` table, pinned source identity, native
  SQLite migration path, deterministic fixtures, exact behavior pins, source
  schema notes, accepted porting pack, Slice 8 package/deployment contracts,
  and deferred production references.
- Keep Node tests that exercise the shipped frontend. `npm test` discovers the
  current retained set; removed legacy files do not count toward coverage.
- Audit `README.md`, `ARCHITECTURE.md`, `DESIGN.md`, `PRODUCT.md`, `PLAN.md`,
  `STACK.md`, `RESEARCH.md`, and `TODO.md` individually. Current root docs must
  describe .NET 10, ASP.NET Core 10, SQL Server 2022, DACPAC ownership, Entra,
  EF/feature services, Hangfire, Frontend, and `Asap.Migration` commands.
- Keep test-IIS ZIP digest-before-extract, exact identity, activation gating,
  runner isolation, deployment-script `-ValidateOnly`, and no-checkout-on-IIS
  rules.

The root `AGENTS.md` is rewritten after the cleanup shape is known. It retains
the project norms for simplicity, settings scope, current/history truth, safe
DOM, scoped analytics, stale-result cancellation, accessibility, SQL/data
scope, migration fixtures, and behavioral/concurrency testing. Runtime rules
that applied only to the retired implementation are removed.

## Current retention map

Current consumers are `src/Asap.Web`, `src/Asap.Web/Frontend`,
`database/Asap.Database`, `src/Asap.Migration`, `tests/Asap.Tests`,
`tests/browser`, `tests/run_all.js`, the retained frontend tests, and the
workflow/deployment scripts. The migration consumer reads a stopped legacy
SQLite database directly and does not load the retired source migration files.
Legacy source IDs remain supported by `LegacyPocketBaseMapping` and the
legacy-link resolver.

## Completed review and deferred release boundary

Cleanup and documentation are not production completion. The cleaned-tree
matrix and one fresh Terra High holistic review passed after one focused
documentation correction cycle; zero substantive findings remain. PR #272 is
integrated. Final milestone CI and acceptance are recorded in the completion
journal. No production tag, live provider call, runner or host activation,
deployment, repository rename, or PR #264 merge was performed.

Production operational evidence, real Postmark adapter/webhook validation,
provider and host isolation, recovery rehearsal, and cutover remain in the
[deferred production-readiness backlog](deferred-production-readiness.md).
