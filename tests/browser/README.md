# Browser acceptance suite

`npm run test:browser` is the full browser-suite entry point. It selects the
seventeen integration cases and runs them through the same Testing Kestrel
fixtures used by the .NET test suite (sixteen use real SQL):

- `patron.cjs`: 10 desktop/mobile patron states and 3 authentication/session
  race scenarios.
- `staff.cjs`: 61 desktop/mobile staff states covering scope, recovery,
  queue, analytics, assignment, stale mutations, draft guards, route/history
  round trips, repeated Operations actions, keyboard workflows, current-actor
  revisions, operational catalog retirement/reactivation after Settings commits,
  and stale rejection, queued notification, reopen and silent close.
- `legacy-links.cjs`: 18 desktop/mobile success and failure states covering
  shared, numeric, and type-qualified request links.
- `patron-submit-session.cjs`: controlled frontend response/session races.
  These simulate suggestion replies; they do not prove a server commit.
- `staff-request-contracts.cjs`: real request
  editing and imported historical snapshots. The owning fixture exports,
  imports and reconciles before starting the app. It also checks imported
  effective configuration, staff authority, protected holds, additional-copy
  links and new submission/automatic claiming through HTTP and SQL.
- `settings.cjs`: real Settings
  GET/editor POST/reload, ownership, override metadata and lossless identity;
  the Campaign 2 super-admin/library-admin cases cover editable new keys,
  collisions, placeholder focus ownership, conditional fields and scope guards.
- `functional-acceptance.cjs`: real Settings editor to patron form to SQL,
  required/hidden/optional fields, stale Settings writes, validation failures,
  one pickup write, committed response loss, patron switching, a current
  configuration change at a server barrier, weekly limit, revoked session,
  recovery and captured outbox delivery. Responses are forwarded from the
  actual server before being delayed or lost; pre-dispatch failure aborts
  the request. No fabricated JSON replaces an endpoint in this runner.
- `operations-dto-boundary.cjs`: SQL UTC timestamps, local-time/DST rendering,
  exact queue bigint strings and zero/null cycle states across scopes.
- `email-defaults.cjs`: all five built-in seeds, saved text and sparse inheritance.
- `staff-presentation.cjs`: header Profile routing and focus, lookup order,
  quiet completed loads, heading gaps and desktop/mobile tab geometry with
  overlay and persistent scrollbars. CSS zoom is automated reflow evidence;
  it does not establish human browser-zoom acceptance.
- `leap-consumers.cjs`: native-ID links in queues, details and verified patron
  context, safe plain-text fallbacks and a separate malformed-response probe.
- `saved-email-test.cjs`: saved sender/recipient/mode context, draft guards,
  one click through the normal automatic worker and recording Postmark adapter,
  durable status/reference reload and scoped Operations handoff. No live mail.
- `staff-workflow-completion.cjs`: queue/detail quick-create, barcode-only
  authoritative verification, explicit All scope, live pickup context and real
  HTTP/SQL creation with deterministic provider boundaries.

- `frontend-cache.cjs`: published staff/patron files served through Program,
  with a controlled deployment, real Chromium cache, stale-module negative
  control, normal navigation/reload and unchanged-resource 304/vendor reuse.
  Request routing is deliberately disabled because routing disables caching.

The sixteen selected methods are named explicitly in `run.cjs`; the two Settings
role DataRows make seventeen discovered cases. Several methods own more
than one Node runner. Other .NET cases also contain embedded browser checks.
Counts refer to discovered .NET cases, not scanned states or assertions.

Each integration journey provisions its isolated SQL fixture, starts the Testing app with
deterministic providers, and tears down the host and browser in test cleanup.
The runners reject external requests, page errors, serious or critical axe
violations, horizontal overflow, and unintended missing images. Hosted CI
uploads `.artifacts/browser` only when the job fails; the directory contains
synthetic screenshots and JSON reports, not credentials or provider traffic.

The command requires the Release build, SQL Server 2022, installed npm
dependencies, and a Chromium browser. It is the CI-facing entry point; the
individual runners below are lower-level contracts for fixture-owned
integration tests.

## Patron runner

The patron browser journey is a plain Node.js + Playwright regression runner
for the hosted `Asap.Web` application. It does not start the application or
seed a database. The real-SQL integration test owns that lifecycle and passes
the runner a loopback base URL plus an isolated artifact directory.

The direct runner contract is:

```text
node tests/browser/patron.cjs <baseURL> <artifactDirectory>
```

The staff and legacy-link runners use the same loopback/artifact contract
followed by their fixture identifiers; their integration tests supply those
arguments. Run `npm run test:browser` instead of reproducing those argument
lists by hand.

The runner normalizes `baseURL` to its origin and aborts every browser request
to another origin. It exits nonzero for any failed browser assertion, serious
or critical axe violation, horizontal overflow, visible-image load failure,
uncaught page error, or unexpected external request. It never skips a missing
dependency or browser.

The exact local development dependencies are `playwright@1.62.1` and
`axe-core@4.10.3`. By default, Playwright resolves its own Chromium browser.
The parent machine may instead provide an existing executable through
`ASAP_TEST_CHROMIUM_EXECUTABLE_PATH`; no user-specific path is built into the
runner.

The patron journey uses these synthetic fixture barcodes, all with PIN `1234`,
library context `libraryOrgId=2`, and pickup branch `101`:

| Purpose | Barcode(s) |
| --- | --- |
| Desktop and mobile journey | `20000000000801`, `20000000000802` |
| Normal session-restore race | `20000000000901`, `20000000000902` |
| Startup config/login race | `20000000000911`, `20000000000912` |

The parent fixture provides deterministic wrong-PIN rejection, branch `101`,
publication option `Coming soon`, duplicate submission for the same
journey patron/title, and successful lookup for identifier `9780000000001`.
The synthetic `@example.org` recipients use the normal allowed-domain intent.

The artifact directory receives desktop/mobile screenshots for the ten major
states, three authentication-race screenshots, and
`browser-results.json`. Keep it under `.artifacts/` for repository
integration; it is ignored by the repository and is retained only as
intentional browser-test evidence.
