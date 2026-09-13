# Patron browser acceptance

This is a plain Node Playwright runner for the Slice 1 patron journey. It does
not start the application, use `@playwright/test`, add application endpoints,
or create application email files itself. It does mutate the running app through
normal login, submission, duplicate-submission, and logout requests; it does
not provision a database or seed fixture data through SQL. The application must
already be running with the deterministic Testing provider and the parent
SQL-backed fixture.

Run it with exactly two positional arguments:

```text
node tests/browser/patron.cjs <baseURL> <artifactDirectory>
```

For repository integration, pass an ignored directory such as:

```text
npm run test:browser -- http://127.0.0.1:5000 .artifacts/browser
```

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

The journey uses these synthetic fixture barcodes, all with PIN `1234`,
library context `libraryOrgId=2`, and pickup branch `101`:

| Purpose | Barcode(s) |
| --- | --- |
| Desktop and mobile journey | `20000000000801`, `20000000000802` |
| Normal session-restore race | `20000000000901`, `20000000000902` |
| Startup config/login race | `20000000000911`, `20000000000912` |

The parent fixture must provide the deterministic provider behavior already
used by the integration tests: wrong PIN rejection, branch `101`, publication
option `Coming soon`, duplicate submission for the same journey patron/title,
and successful lookup for identifier `9780000000001`. Across the desktop and
mobile journeys, the parent should expect exactly two created requests: one
successful first submission for each journey fixture; the second identical
submission for each fixture is a duplicate/conflict. The synthetic
`@example.org` recipients use the normal allowed-domain intent. Parent-side
domain-policy checks should keep blocked destinations terminal as
`recipient_domain_not_allowed` with zero provider calls; this runner does not
provision or seed that SQL state.

The artifact directory receives desktop/mobile screenshots for the ten major
states (`login`, `invalid-login`, `form`, `success`, and `duplicate`), three
authentication-race screenshots, and `browser-results.json`. Keep that
directory under `.artifacts/` for repository integration; it is ignored by the
repository and may be retained as intentional CI browser-test evidence. Only
generated dev-email output must be excluded from application publish and CI
artifacts.
