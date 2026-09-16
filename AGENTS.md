# ASAP Repository Instructions

## Scope and simplicity

The shipped application is the .NET 10 / ASP.NET Core 10 solution under
`src/`, `database/`, and `tests/`. Implement the smallest straightforward
change that satisfies the current requirement and matches the surrounding
code. Keep ownership boundaries clear; avoid speculative abstractions,
compatibility layers, generalized infrastructure, and unrelated cleanup.

Read the directly relevant current source and contract before editing. Preserve
existing user changes. Use `rg`/`rg --files` for discovery, `apply_patch` for
manual edits, and focused tests scaled to the behavioral risk.

In C#, always use braces for control-flow blocks, even for one statement. Keep
nesting minimal with guard clauses and small helpers when that makes the path
clearer. Make cancellation explicit across async boundaries: thread the
`CancellationToken` through calls, honor it promptly, and do not swallow or
misreport cancellation as an ordinary failure.

## Repository ownership

- `src/Asap.Web` owns the HTTP host, Entra authentication, authorization,
  feature services, JSON APIs, Hangfire registration, and static frontend
  delivery.
- `src/Asap.Web/Frontend` is tracked browser source. MSBuild copies it to the
  ignored generated `wwwroot` directory during build, publish, and F5.
- `database/Asap.Database` is the source of truth for the SQL Server 2022
  `Sql160` DACPAC and the `[asap]` schema. Hangfire owns `[HangFire]`.
- `src/Asap.Migration` is a separate self-contained migration executable. It
  uses native SQLite loading only for a stopped legacy source, then validates,
  imports, and reconciles a SQL target.
- `tests/Asap.Tests` owns backend, SQL, migration, and browser-fixture
  behavior. Retained Node tests cover shipped frontend contracts; Node/npm are
  development and CI tooling only.

## Data access and configuration

Use direct EF Core for ordinary aggregates and existing feature services. Use
selective Dapper/ADO.NET and parameterized SQL where the feature boundary
already requires reporting, queue, reconciliation, or migration operations.
Keep authorization, scope predicates, transactions, constraints, rowversion
checks, and outbox/event writes in the owning data/service layer. Do not use
in-memory or SQLite stand-ins for SQL behavior that needs real SQL Server.

The DACPAC owns schema changes. Migration source inspection and migration
package operations may use native SQLite because that is their explicit input
contract; do not add a second schema owner or a web-time migration path.

Keep infrastructure/bootstrap configuration in the external JSON file selected
by `Asap:ConfigFile`. Keep ordinary application settings in their owning SQL
tables. Never commit connection strings, credentials, private keys, Data
Protection keys, local logs, generated `wwwroot`, or development email output.

### Settings scope

Before adding a setting, declare whether it is system-only, library-only, or a
system default with a library override. Its default/load path, editor
population, save path, and runtime resolution must use that same scope model.

For system-default-with-library-override settings, resolve the library value
first and fall back to the system value only when no override exists. Save and
reset intentionally to the selected scope; do not treat a missing override and
a blank override as equivalent unless the contract says so.

Reset-inherited-overrides must preserve library-owned items, including custom
fields, custom formats, rejection templates, auto-claim rules, and their
history. Reset only the inherited override values that the operation owns.

System-only controls remain disabled in library context and library saves must
not submit system-only keys. Global records such as staff users are not
settings overrides: scope controls filter or preselect records, while
authorization and record fields govern create/update/delete.

## History and audit notes

Keep current state separate from accepted history, user draft comments,
pending generated audit text, and successful audit entries. Do not show a
past-tense system entry in an editable notes field before the action succeeds.
Append system history only after the action completes successfully.

Preserve exact source identities, migration provenance, deterministic fixture
inputs, behavior pins, and deferred operational contracts when they remain
needed for migration, recovery, legacy links, or future release work. Git
history is the archive for retired implementation, not a reason to keep an
unconsumed runtime in the working tree.

## Frontend DOM and accessibility

Prefer DOM construction APIs: `document.createElement`, `textContent`, safe
attributes, `append`/`replaceChildren`, and `classList`. Never interpolate
patron, staff, API, settings, or other runtime data into arbitrary HTML. Static
developer-authored markup may use `innerHTML` only when the reason is
documented nearby and the rendered path is tested.

Use the established frontend request helpers. Pass plain objects for JSON
bodies and let the helper own serialization, headers, parsing, cache mode,
abort signals, and normalized errors. Keep authentication/session policy in
the feature-specific wrapper.

When scoped loads can overlap, prevent an older response from rendering over a
newer status, tab, workflow, analytics range, selected library/settings
context, or authentication context. Reuse the existing abort or stale-result
pattern where a real race exists, and keep cancellation visible at the caller
boundary. Route mutation follow-up loads through explicit refresh helpers.

Every interactive flow must remain keyboard reachable with visible focus,
correct labels, sensible dialog/tab/view focus, supported Escape behavior, and
useful live-region announcements. Keep desktop and mobile layouts free of
horizontal overflow and verify serious/critical axe findings are zero.

## Analytics and authorization

Define scope for every metric before implementation. Library users see only
their library; super-admin views may select one library or an explicitly
authorized all-library view. Apply scope in the query/data layer, not only in
the UI. Recheck role, active identity, effective library, and rowversion at
the mutation boundary.

## Testing norms

Test observable behavior and real failure modes. Use real SQL Server 2022 for
constraints, transactions, authorization, concurrency, settings scope,
outbox/jobs, and migration import/reconciliation. Use deterministic provider
boundaries with no live Polaris or Postmark calls in ordinary CI.

Keep the full .NET test guard, retained frontend suite, and Playwright browser
journeys. Browser checks must fail on external requests, page errors, serious
or critical axe findings, unexpected missing images, and horizontal overflow.
Keep compact deterministic fixtures and focused migration regression/oracle
checks. A test count must represent current discovered tests; removed files
must not be counted as coverage.

## Deployment boundaries

The test-IIS workflow builds the exact tested commit, publishes Web/DACPAC and
the separate migration artifact, validates ZIP identity and digest before
extraction, and keeps build/test/package jobs isolated from self-hosted
activation. `-ValidateOnly` must not mutate a host. Do not check out or build
on IIS, activate a runner, contact a host, create a production tag, deploy
production, or merge the draft PR as part of ordinary implementation.

`test_cd_activation: pending_runner_setup` remains the documented state until
a separately authorized task records a real successful test-IIS deployment.
Production readiness, live providers, cutover, backup/recovery rehearsal, and
repository rename remain deferred. A stopped legacy source is migration or
forensic input only, never a writable fallback after cutover.
