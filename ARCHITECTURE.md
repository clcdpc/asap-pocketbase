# ASAP Architecture

## Runtime

ASAP is an ASP.NET Core 10 application targeting .NET 10. `Asap.Web` owns the
HTTP host, Entra authentication, authorization, feature services, JSON APIs,
static frontend delivery, email outbox orchestration, and Hangfire recurring
jobs. External infrastructure/bootstrap configuration is loaded from an
ACL-controlled JSON file through `Asap:ConfigFile`; ordinary application data
and settings live in SQL Server.

The frontend is a vanilla JavaScript, HTML, and CSS application with patron
and staff entry points under `src/Asap.Web/Frontend`. MSBuild copies that
tracked source to ignored `wwwroot` for local runs, builds, and publishes.
Node/npm are development and CI tooling only. Browser assets are pinned under
`Frontend/vendor` with their manifest and license files.

## Persistence and schema ownership

SQL Server 2022 is the application datastore. The SDK-style
`database/Asap.Database` project targets `Sql160` and is the source of truth
for the `[asap]` schema. Hangfire owns its `[HangFire]` schema and its versioned
SQL asset is kept under `scripts/hangfire`.

The web layer uses EF Core directly for normal aggregates and feature services.
Where a feature needs reporting, queue, or reconciliation queries, it uses the
existing parameterized Dapper/ADO.NET boundaries. Transactions, constraints,
rowversion checks, scope predicates, and outbox/event writes are part of the
observable contract rather than in-memory substitutes.

Schema 13 supports fresh deployment and safe current-version redeployment.
Pre-production upgrade paths are retired. Unsupported versions fail before DDL;
deployment never resets a configured database automatically.

## Feature boundaries

- Patron endpoints authenticate and scope the effective library context before
  loading options or accepting a suggestion.
- Staff endpoints enforce Entra identity, role, library scope, rowversion
  mutation barriers, and stale-result protections for concurrent workflows.
- Workflow services coordinate purchase promotion, hold placement,
  fulfillment, timeout, identifier, and summary jobs through Hangfire and the
  SQL outbox.
- Settings are split by system, library, and system-default-with-library-
  override scope. Effective values are resolved in one service and reset is an
  explicit operation that preserves library-owned records.
## Safety boundaries

The abandoned PocketBase proof of concept has no operational or migration role.
Current native SQL identities and durable operation journals own links and recovery.
Deterministic provider fakes and real SQL are used in
ordinary CI; live Polaris, Postmark, production infrastructure, and IIS host
activation remain separately authorized release work.
