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
- `tests/Asap.Tests` owns backend, SQL, and browser-fixture
  behavior. Retained Node tests cover shipped frontend contracts; Node/npm are
  development and CI tooling only.

## Data access and configuration

Use direct EF Core for ordinary aggregates and existing feature services. Use
selective Dapper/ADO.NET and parameterized SQL where the feature boundary
already requires reporting, queue, reconciliation, or operational recovery.
Keep authorization, scope predicates, transactions, constraints, rowversion
checks, and outbox/event writes in the owning data/service layer. Do not use
in-memory or SQLite stand-ins for SQL behavior that needs real SQL Server.

The DACPAC owns schema changes. Only current schema 13 is supported. Older
pre-production schemas fail explicitly; never automatically reset a configured
database. Deliberate future upgrades belong in the DACPAC.

PocketBase was an internal proof of concept, never production, with no data to
migrate. Its migration implementation, historical compatibility, and frozen
source contracts are retired. Git history is the archive. Current native SQL
request links and durable hold/pickup operation journals remain required.

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

Preserve deterministic fixture inputs, current behavior pins and durable recovery
evidence. Successful hold operations, existing-hold adoption, ambiguous provider
outcomes, idempotency and duplicate prevention are current safety contracts.

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
and outbox/jobs. Use deterministic provider
boundaries with no live Polaris or Postmark calls in ordinary CI.

Keep the full .NET test guard, retained frontend suite, and Playwright browser
journeys. Browser checks must fail on external requests, page errors, serious
or critical axe findings, unexpected missing images, and horizontal overflow.
Keep compact deterministic fixtures and focused current-behavior regression
checks. A test count must represent current discovered tests; removed files
must not be counted as coverage.

Full CI uses three concurrent standard GitHub-hosted Ubuntu 24.04 jobs after trust/current-head
preflight: frontend/CI safety/package, non-browser .NET/SQL, and browser/SQL.
Each independently builds the exact event SHA in Release with pinned configuration;
compiled outputs need not be byte-identical or transferred. The frontend job owns
the deployment ZIP. Each native job owns its SQL Server 2022 instance, ephemeral
credentials, dynamic loopback port, test databases, diagnostics and unconditional
cleanup. Keep fixture/test commands serial within each worker. Verify each fixed
job's assigned suite, and require all three verified suites plus package validation
at the hosted gate. Preserve independent evidence reuse and final revalidation;
reused native suites need neither SQL startup nor Chromium installation.
Build, test and package remains the reporting-only required full-validation
check; draft validation on Ubuntu 24.04 cannot substitute for it. Preserve the
hosted environment epoch and runner provenance checks. Keep DEVWEB/IIS deployment
on its existing local runner with unchanged authorization. See `scripts/ci/test-selection.md`.

Acceptance tooling is owned by the local checkout, independently of the selected
application PR. Validate all suites locally, pin one application SHA/tooling
snapshot/image, and run only the requested suites sequentially with fresh
containers and SQL state. Synthetic writes are enabled by default only behind
the existing disposable-runtime and scenario guards; explicit `-ReadOnly` must
prevent writes and required skips must never pass acceptance. Retain each suite's
evidence outside the worktree in flat local-time directories, allocate names
atomically, and clean up containers even after interruption. Keep generated
acceptance artifacts out of Git. Automation does not complete human visual or
screen-reader acceptance. See `tests/acceptance/README.md`.

Normal frontend qualification additionally runs exactly the portable acceptance
authentication/runner contracts declared in `tests/frontend-tests.json`, with
matching discovery, per-file receipts and reuse inputs. Other acceptance tests
retain their separate launcher/runtime/browser/TLS prerequisites and invocation.

## Deployment boundaries

The test-IIS workflow builds the exact tested commit, publishes Web/DACPAC, validates ZIP identity and digest before
extraction, and keeps build/test/package jobs isolated from self-hosted
activation. The deployment script's `-ValidateOnly` must not mutate a host. Do not check out or build
on IIS, activate a runner, contact a host, create a production tag, deploy
production, or merge the draft PR as part of ordinary implementation.

Initial test-IIS activation is recorded as `test_cd_activation: active`: the
[November 5, 2026 receipt](https://github.com/clcdpc/asap-pocketbase/actions/runs/42135741938/job/127625203451)
verifies `v1.0.0-test.32` at `46c60a8cdb1218f34622620cbf0b31cc92e6bd07`.
This historical receipt does not establish current-main deployment or host health.
Production readiness, live providers, cutover, backup/recovery rehearsal, and
repository rename remain deferred.
