# Slice 1 Evidence

Recorded 2026-09-12. Slice 1 is closed after local acceptance, full Terra review
and CI-correction closure, and the successful 120-test Linux run 34731687718.
See `slice-01-review.md` and the evidence below. Earlier checkpoint failures
remain preserved chronologically. No production data, provider delivery,
deployment, or release/rehearsal gate was involved.

## Foundation Checkpoint

While Sol paused edits at its first implementation checkpoint, Astra ran:

- `dotnet build Asap.sln --configuration Release`: succeeded, zero warnings
  and errors.
- `dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --no-build --minimum-expected-tests 51`:
  51 passed, zero failed or skipped, approximately 30 seconds. This includes
  real local SQL Server constraint checks, not completed patron workflow tests.
- A separate synthetic console probe invoked the actual `FileEmailSender`
  twice with the same envelope in each of the repository and web-project
  development-email directories. All four successful invocations produced
  distinct HTML files; metadata was encoded and HTML body markup preserved.
  A pre-cancelled invocation threw cancellation. The probe used only
  `example.invalid` addresses and no application/outbox state.
- `dotnet publish src/Asap.Web/Asap.Web.csproj --configuration Release --no-build --output .artifacts/acceptance/slice-01-web`:
  succeeded with 309 files and no development-email files, while the probe
  output existed in both source directories. The DACPAC was present; all 17
  vendor hashes and seven upstream Hangfire operator-asset hashes matched.
- `git check-ignore` confirmed the actual generated HTML files are ignored.
- `npm.cmd test`: retained legacy/frontend suite completed with exit 0 and
  "All tests passed." Its pre-existing grep-based no-Dao check cannot execute
  correctly on this Windows shell; Astra separately ran
  `rg -n 'new Dao|Dao\(' pb_migrations pb_hooks lib`, which found no matches.
- All 19 authoritative documentation-pack payload hashes still match the
  manifest. All 19 initially copied patron/shared frontend assets matched
  their exact pinned PocketBase Git blobs before target adaptation.

Local Chromium inspection of the actual generated file at 1280x900 showed
useful metadata and HTML/plain-text content. The initial narrow-screen preview
prompted a focused wrapping correction from Sol. Astra regenerated the actual
file and verified it with Playwright's explicit 390x844 and 1280x900 viewports:
document scroll width equalled viewport width at both sizes, and inspected
screenshots showed readable metadata and body with no clipping. Raw Chromium
CLI window sizing alone had not established the requested narrow layout
viewport, so it is not the final mobile evidence. This file preview is not
the required SQL-backed application Playwright/browser acceptance journey.
Screenshots and generated email remain ignored local artifacts, not CI or
application release artifacts.

## Integration Corrections

Early Astra inspection identified SQL CHECK constraints that accepted null
sensitive-recipient kinds through SQL's UNKNOWN result, and a sent-payload
constraint that prevented the required terminal payload purge. Sol corrected
these and added regression coverage before the checkpoint test run above.
The application transport envelope now carries its owning organization ID
for later scoped transport configuration without ambient organization state.

The exact Hangfire.SqlServer 1.8.25 operator install script and license assets
are pinned with hashes and byte-preserving Git attributes. Application
configuration disables runtime schema creation and automatic schema-dependent
option detection.

## Hangfire Operator Boundary Probe

Astra's isolated console probe used the selected 1.8.25 package and the exact
vendored SQL script against a newly created local disposable database. Both
initial provisioning and a repeated script execution succeeded, and a separate
query verified schema version 9. A database user without a login received only
SELECT, INSERT, UPDATE, DELETE and EXECUTE through a role on `[HangFire]`.
Connections impersonating that user exercised the real Hangfire enqueue,
single-worker execution, succeeded-state persistence and graceful shutdown
with schema preparation and automatic schema option detection disabled.
`CREATE TABLE` under the same user was denied with SQL error 262. The version
remained 9 and the owned database was removed in cleanup.

The successful probe used unpooled impersonated connections. An initial probe
attempt with pooling failed at session reuse, was not counted as passing, and
also cleaned up its database. This probe isolates the runtime permission
boundary; checked-in application integration and deployment/recovery tests
remain required. It does not prove upgrades, existing-database backup gates,
artifact rollback, or release readiness.

## Provider Response Probe

Astra exercised `Clc.Polaris.Api` 4.0.0-beta.3 through a synthetic HTTP handler,
without external network access. HTTP 503, 401 and 429 return a REST response
with null typed data, not an `HttpRequestException`; its underlying response
retains HTTP status and success. HTTP 200 with `{}` creates a default typed
search result with PAPI error code zero and zero rows, while malformed JSON
returns null data. Valid successful empty search and the API-defined no-record
response remain distinct from these failures. Sol was given these concrete
results to correct the thin adapter's classification and add regression tests.
This probe is not proof that the application adapter tests have passed.

Further synthetic package checks exercised pinned pickup fixtures. The CLC
typed model reads ID rows but returns zero IDs for OrganizationID/OrgID rows
and null typed data for the source's nested PickupBranchRow fixture. Raw
response Content preserves those supported source fields. The pinned helper
normalizes them, preserves labels and sorts labels. Sol received these results
for a narrow adapter correction and regressions, not a new protocol client.

With PapiSettings OrganizationId 7, UserId 42 and WorkstationId 99, the CLC
PatronUpdate convenience method still sent path organization 1 and default
body LogonBranchId/LogonUserId/LogonWorkstationId values of 1. The pinned
pickup update explicitly supplies configured system/application values.
This concrete request-shape difference was returned to Sol; it is not an
external dependency blocker. No real patron update or provider call occurred.

## In-Progress Integration Findings

The following concrete checkpoint findings were sent to Sol before the full
slice review. Final fixes and regression results still need verification:

- Preserve independent system/library logo and alt-text inheritance, and
  per-format custom-field modes with hidden-by-default behavior.
- Align initial workflow defaults with the pinned executable resolver, and
  do not recreate administrator-removed publication options on redeployment.
- Acquire/revalidate auto-claim staff eligibility before request/dependent
  locks; nullable display metadata must not become an authorization test.
- Fence immediate identifier results by current organization participation,
  expected request/identifier state and successful update before applying tags.
- Preserve pickup-context fallback/preference precedence, e-material exclusion,
  identifier result selection, reconciliation, notes and multiple-match behavior.
- Preserve existing email placeholder/HTML rendering without interpreting
  placeholder-like patron values as further template input.
- Apply recipient-domain safety at both intent creation and delivery, with
  terminal initial suppression that cannot revive after a policy change.
- Revalidate authorization-library activity for sensitive staff email, check
  the provider start deadline after asynchronous preflight, and prevent a
  timeout retry from starting before the original lease safety boundary.
- Preserve auto-claim assignment/skipped events and metadata, identifier-first
  duplicate matching/context and committed cross-patron duplicate notes.
- Use the configured business timezone for the source's calendar-day weekly
  cutoff and public date rendering rather than substituting a UTC 168-hour
  window at daylight-saving boundaries.
- Return effective public external-search slots, duplicate-status labels and
  system participation messages consumed by the preserved frontend.
- Keep ordinary integration tests on a recording final sender with deliberate
  worker control; do not let an incidental Hangfire tick write email files.

These checks complement, and do not replace, the fresh Terra full-slice
review/fix/re-review gate.

## Second Integration Checkpoint

After Sol paused with the patron endpoints, application services and outbox
worker wired, Astra rebuilt the complete solution successfully with zero
warnings/errors and reran the existing .NET suite: 51 passed, zero failed or
skipped, approximately 32 seconds. These tests do not yet cover the new
workflows. Sol reported the checkpoint findings addressed in code; complete
regression verification is pending. Additional checks were returned for the
deadline after the final asynchronous ownership read, fail-closed business
startup on application-schema/initialization failure, and required rate-limit
Retry-After headers. Frontend adaptation, real migration and the expanded
SQL/provider/browser tests remain in progress, not deferred to later slices.

Astra also deployed the built DACPAC snapshot with SHA-256
`eea0c62072cd1af6ebb27e98f9102be5b9ccf67a43d27a9691d0c3734a61c230`
to a dedicated disposable database. Initial workflow values matched the pinned
resolver. After changing configured scalar values and template content and
deleting one publication option, two deployments of the same artifact preserved
all three choices. The owned database was removed. This confirms the concrete
seed correction, not migration acceptance or a release deployment gate.

## Third Integration Checkpoint

Astra built the integrated solution with zero warnings/errors and ran the
full .NET suite: 55 passed, zero failed/skipped, 35.6 seconds. New HTTP/SQL
coverage includes duplicate-identifier precedence/context, cross-patron tag
and note persistence, and effective public configuration. This remains a
partial test set, not a completed slice.

An independent snapshot used DACPAC SHA-256
`d228492113983f8ace4c7c0d8885152efd08a35f84a3fdb7bedcadf4c5e75f18`
and copied built application/frontend files, a disposable synthetic SQL
database, temporary certificate/key directory and the Testing-only patron
provider. Empty allowed-recipient domains prevent transport invocation.
The first probe configuration accidentally retained the example's rejected
client-secret placeholder; correcting the synthetic input allowed startup.
This was a probe setup error, not an application acceptance result.

Actual Chromium navigation then exposed a blocking redirect loop:
`GET /patron/?libraryOrgId=2` selected the `/patron` redirect endpoint,
repeatedly returning 302 to `/patron/` and losing the query. ASP.NET's route
trailing-slash matching prevented the static entry page from being served.
The finding and required slash/query regression tests were returned to Sol.
Direct-index probing can investigate the remaining UI while this is fixed,
but cannot establish that the normal patron entry journey passes.
All owned probe databases, app processes and temporary certificates were
cleaned up after each completed run; retained files are ignored local evidence.

Direct-index exploration then completed login, bad-PIN feedback, heading focus,
SQL-backed session restoration, submission, duplicate feedback and logout at
1280x900 and 390x844. All ten major states had zero serious/critical axe 4.10.3
violations; no uncaught browser exceptions or unexpected external resource
requests were observed. The result still failed acceptance: mobile form identity
text overflowed (404px scroll width for a 390px viewport), and success/duplicate
buttons overflowed their columns. Browser SQL postconditions were not reached
after the aggregate layout failure. Sol received screenshots and exact paths.

Visual inspection also found absent initial prompt/note and default branding.
The exact pinned `lib/config/ui_text.js` supplies those defaults and `/jpl.png`;
the target seed/resolver returned empty text/no image. This parity finding and
the altered seeded submission-email subject/body from `lib/config/defaults.js`
were returned for correction with regression coverage. Do not treat the direct
index, isolated defaults, or automated axe result as a waiver of the complete
normal-route/browser/behavioral gate.

A separate actual-browser interleaving confirmed an authentication-context
race: delay delivery of the SQL-backed restore response for patron A, sign in
successfully as patron B, then release A's response. The screen returned to
A's barcode while sessionStorage retained B's bearer token. Sol received the
reproducer for a scoped stale-result fix and regression including superseded
restore failures/logout. No production identities or external providers were
used. The owned application/database/certificate were cleaned up afterward.
A second variant first revoked A on the server, delayed the real restore 401,
then signed in B. Releasing the old 401 cleared B's token and hid B's form.
The request wrapper's global token clearing is part of the required fix,
not only the restore function's success rendering.

## Fourth Integration Checkpoint

After the route/layout/default/auth corrections, Astra rebuilt successfully
with zero warnings/errors. The expanded .NET suite ran 79 tests: 78 passed,
one failed, zero skipped (37.0 seconds). The failing domain-policy test expected
no dispatcher enqueue for an initially suppressed intent but observed one.
The distinction between a harmless rejected delivery job and a provider call
was returned to Sol without waiving the required persisted terminal suppression,
zero provider invocation or the full green-suite gate.

The independent snapshot's DACPAC SHA-256 was
`6a94ca03dd4f3c34e1d8dc844103ea953cb317c10b54aca502383a0075f2fd60`.
Normal `/patron/?libraryOrgId=2` browser navigation now passed login/error,
keyboard/heading focus, session restoration, form/submission, duplicate and
server logout at both viewports. All ten major states had zero serious/critical
axe findings, no horizontal overflow and loaded images. Inspected mobile
screenshots confirmed readable wrapped identity/feedback controls and restored
default branding/text. SQL postconditions proved exactly two requests with
found/BIB 9001 and two terminal recipient-domain-suppressed intents, with no
file-sender output. These are deterministic local checks, not provider delivery.

Both earlier stale restore-success and restore-401 interleavings now preserved
the new patron's displayed identity and token. An additional actual-browser
ordering found a remaining regression: delay initial configuration, start a
foreground login B, then release configuration so background restore A starts
after B. The restore advanced the auth generation and caused B's eventual
success to be discarded. Sol received the reproducer and requirement that
background initialization cannot supersede the latest user action. This must
be fixed while retaining both already-passing race regressions.

All owned probe processes/databases/certificates were cleaned up. Sol resumed
implementation immediately after snapshot capture. Migration, complete provider/
outbox/race coverage, checked-in browser/CI integration and independent Terra
review remain unfinished; no milestone or production-readiness gate passed.

## Fifth Integration Checkpoint

Astra's Release build passed with zero warnings/errors. The full .NET suite
ran 81 tests: 80 passed, one failed, zero skipped (39.6 seconds). The earlier
domain-policy test now passes. The remaining failure was
`MigrationCliTests.UnknownCommandFails` (expected exit 2, actual 1) after export
became an implemented command; Sol received it to preserve a real unknown-
command assertion while continuing migration. The new stopped-SQLite export
tracer and focused provider tests are not complete import/reconciliation gates.

The copied browser snapshot is
`.artifacts/acceptance/patron-browser/eb9e0d0f39a3452a808d926e0e9d74dc/app`.
Its DACPAC retains SHA-256
`6a94ca03dd4f3c34e1d8dc844103ea953cb317c10b54aca502383a0075f2fd60`.
The normal desktop/mobile patron journey again passed with the same SQL
postconditions, ten serious/critical-axe-clean states, no layout overflow,
loaded images and zero file-sender output. Astra inspected mobile form and
desktop success screenshots. Sol resumed immediately after the immutable copy;
the following probes did not build from or run the changing workspace.

All three actual-browser auth interleavings now pass: late old restore 200,
late old restore 401 after server revocation, and delayed initial configuration
followed by an in-flight foreground login. Each preserves the latest patron's
identity and token. The startup probe originally waited for the old restored
form, a symptom of the bug; that synchronization timed out on the corrected
app. It was changed to wait for configuration delivery/render turns while
holding the new login response. The corrected probe passes the fifth snapshot
and still fails the fourth snapshot with the old identity/token, establishing
an independently replayed failing-before/passing-after regression.

All owned application processes, SQL databases and temporary certificates were
cleaned up. Migration validation/import/reconciliation, remaining required
concurrency/provider/startup tests and checked-in browser/CI integration remain
unfinished. No formal Terra pass, Slice 1 milestone, or release gate is claimed.

A separate real-HTTP run against the same frozen snapshot confirmed that the
first twenty wrong-PIN login attempts reached authentication (401), attempt 21
returned generic 429 with a positive Retry-After no greater than 300 seconds,
and a forged forwarding header did not change the direct-host IP partition.
Liveness and public configuration remained available. This verifies the default
limit only, not configurable limits, IIS proxy behavior or the remaining rate-
limiting tests. The owned database/process/certificate were cleaned up.

## Checked-In Browser Runner

A bounded Luna High support task consolidated the independently verified
browser scripts into `tests/browser/patron.cjs` plus its README and exact
development-only `playwright` 1.62.1 / `axe-core` 4.10.3 dependencies. No app,
schema, migration, C# fixture or CI file was delegated. Sol retains complete
slice ownership and now owns runner integration. Existing jsdom dependency
resolution was unchanged; no frontend build or runtime Node dependency was
introduced.

Astra ran the checked-in runner against the fifth frozen app, using a fresh
owned SQL database/certificate and the explicit local Chromium executable
override. All ten major states and three auth interleavings passed; the report
contains zero accessibility/layout failures. SQL proved exactly two found/BIB
9001 requests and two recipient-domain-suppressed outbox rows, with no email
files. Evidence is under ignored
`.artifacts/acceptance/patron-browser/aa75bbc516974b7eba985689dc85c5ee/`.
All owned running resources were cleaned up. Missing dependencies/browser are
errors, not skipped tests. C# fixture and CI integration remain with Sol.

`npm.cmd test` then completed successfully with the new dependency lock and
auth regression. As previously recorded, the pre-existing Windows-incompatible
`no_dao_usage` grep invocation prints errors yet reports success; Astra again
ran the equivalent `rg -n 'new Dao|Dao\(' pb_migrations pb_hooks lib` and found
no matches. This caveat is not concealed by the overall legacy runner result.

## Real PocketBase-Shaped Migration Fixture

Astra downloaded the repository-documented PocketBase 0.36.9 Windows amd64
release and verified its archive SHA-256 against GitHub release metadata:
`1067687b97b9fcd533f3f86750904f6ee0d0320b19885fd11b72cee61cce7fab`.
Only an isolated migration command was run under `.git/asap-real-pb-source/`;
no application hooks, HTTP server or production database was involved.

The exact pinned initial migration first failed because several fixed seed
IDs are shorter than the runtime's 15-character validation requirement. Its
exit code was zero despite the printed migration error, so command exit alone
was not treated as success. The repository legacy files remain unchanged.
In the isolated fixture copy only, initial `saveRecord` uses the documented
`app.saveNoValidate` to preserve the pinned IDs and schema. All pinned
migrations then applied, followed by a separate synthetic organization,
super-admin, title request and event using ordinary validated Record APIs.
This adjustment is not an exact-source startup or release-rehearsal pass.

A standalone read-only SQLite inspector confirmed the actual resulting
columns: title requests have explicit created/updated fields, but staff,
organizations, request events, email templates, material formats and UI
settings have neither. This shape was provided to Sol for the required
deterministic timestamp handling. Full export/import/reconciliation against
this independent fixture remains pending the completed migration handoff.

A second ordinary Record-API fixture migration adds the five hold-terminal
reasons, status_changed transitions to/from hold_placed, current placed state
with known/null BIB, and a never-placed rejected row. The resulting source
has 12 requests and four original events. Its independent expected result is
ten unique protection markers (eight known BIB, two explicit null), two
no-evidence requests, and zero fabricated hold operations/provider IDs.
These are acceptance expectations, not an import/reconciliation pass yet.

A pinned PNG was then attached through the PocketBase File field API. Astra
copied the current migration-only sources to an isolated `.git` scratch build
(no shared output or app-project build) and compiled with zero warnings/errors.
The CLI exported 59 records across 17 JSON files and accepted that package's
hash validation. No logo asset bytes were exported despite the populated file
field. Import into a fresh disposable schema-2 database failed on the pinned
plain-newline publicationOptions representation (`source_json_invalid`). The
transaction left zero requests/staff/outbox rows; the owned database was
removed. Sol received both concrete gaps. Evidence is under
`.git/asap-migration-acceptance/runs/5c943b21535f4178a0030de40422a3b4/`.
This is an independent failing acceptance probe, not a migration gate pass.

Separate source variants were retained rather than rewriting the failed
package. The JSON-options variant initially lacked a referenced branch; that
fixture defect was corrected by creating Organization 101 through Record APIs
in both source databases. Its undated events then produced the expected
`request_event_created_missing` failure with no committed requests/staff/mail.
A fresh narrow Astra Max consultation confirmed that historical dates cannot
be invented from export time; see `migration-source-notes.md`. This was not a
formal Terra review and is not proof of the deployed production data shape.

The separately augmented positive source provides an explicit created field
and known timestamps for its four synthetic events. CLI import reported
success, and independent SQL checks verified twelve requests, one bound staff
user, empty outbox, and ten unique markers (eight known BIB and two null).
Equivalent fresh-target runs produced the same sorted marker metadata hash:
`bca1d8353e9e13feb899186f6a8c091c5a7512cc62391c8c413e4080cb886b37`.
However, both independent probes failed their source-PNG/SQL-LogoData equality
check despite CLI reconciliation claiming success. That missing asset and
incomplete reconciliation remain assigned to Sol. Evidence directories end
`931945eb1f8143628f18d9faeedb03bf` and `641eff254f4c46e88b8b38338c02b82e`.
Every owned SQL database and CLI process was cleaned up. Original event-time
preservation distinct from both UpdatedUtc/export time is the next fixture
assertion; no final migration gate is claimed.

The distinct-time positive fixture then passed that independent assertion:
all four original events retained `2030-01-02T09:10:11Z` while all ten markers
used the frozen `2030-01-04T00:00:00Z` export time. The marker metadata hash
remained identical. Logo equality still failed as expected against the same
isolated intermediate migration snapshot. Evidence directory ends
`077e0bf476b447e493b1e7e0141ef716`; the owned database was removed.

### Second Isolated Migration Snapshot

The migration-only sources copied at 19:00 local compiled with zero warnings
or errors. Its new export validates 61 records across 21 files, including
both effective-configuration artifacts, branding metadata and the actual PNG.
The independent positive CLI/SQL probe now passes: twelve requests, one bound
staff user, empty outbox, the same ten markers/eight known/two null and the
same metadata hash, four original event timestamps preserved distinctly from
ten annotation times, JSON zero/false/string/array values preserved, and exact
source-PNG/SQL-LogoData equality. Evidence directory ends
`19287bd477d545bb9e5ab7f7116222c9`.

The original plain-label/undated-event variant now reaches the expected
`request_event_created_missing` blocker instead of failing JSON parsing. It
commits no requests/staff/outbox rows; evidence directory ends
`d3b83a81937c43b5bdecc4a64b95c733`. Earlier plain-options/logo findings are no
longer open in this second snapshot. Both owned SQL databases were removed.
This remains narrow intermediate acceptance, not the complete migration,
solution, independent-review or release gate.

The second snapshot's standalone `reconcile` command then failed an
independent package-binding test. Original package/target/report reconciled
successfully. A separate package copy changed a source request title and
updated that domain file's manifest length/hash while preserving the header
Git SHA, schema version and export time. Reconcile incorrectly accepted it
against the original target/report. Evidence directory ends
`9b454cfdf98e44a08e89d586e77548be`; the owned database was removed. Sol received
the reproduction and required exact-package binding regression. A fingerprint
captured from the imported target is a subsequent drift check, not evidence
that the initial target matched all required source values and transformations.
Full source-to-target semantic reconciliation remains part of the slice gate.

The second snapshot also passed independent operational-capture checks.
Default schedules and all eight effective queue entries match the pack
example and exact pinned job names. A target with one changed effective
OutstandingTimeout page size is rejected with
`operational_configuration_mismatch`. A separate export with nondefault cron,
global, timeout-family and queue overrides matches the actual pinned
`normalization.js`/`workflows.js` jobLimits resolver for every queue; matching
target JSON validates. The explicit retired hourly ISBN override remains
recorded, while the dedicated identifier queue retains its own effective
limits. The local probe is `.git/asap-real-pb-source/operational-parity.cjs`.
Its initial expected error-code spelling and optional-value shape assertions
were corrected to the actual CLI/capture contracts; no application defect is
claimed for those probe assumptions. This does not exercise scheduled jobs,
real-host operational activation or release rehearsal.

### Third Isolated Migration Snapshot

The migration-only source snapshot taken after the reconciliation fixes built
with zero warnings/errors. Two fresh-target independent CLI runs passed the
original positive assertions, including exact marker source-ID membership,
preserved timestamps/typed JSON and source-logo bytes. Original package/report
reconciliation succeeds; a different validly rehashed source package fails
with `reconciliation_report_mismatch`. A same-row-count target title mutation
also fails reconciliation. Evidence directories end
`4a41c566c39349cd82daeb94a28a8d84` and `6c9549cf5fce4dc8a061da794a4cc87d`.
All owned databases and CLI processes were cleaned up.

The two runs produce identical marker metadata SHA-256
`3c23298a53f48d0d86948d69874f03e2f4ef74da262478fb9f2a454cd334343c`.
The difference from the second snapshot is the corrected source-field
provenance `statusRef` for current-placement evidence; the protected request
set and known/null BIB values are unchanged. This verifies the earlier
package-binding regression on the newer isolated snapshot, not the final
integrated solution, Terra review, representative-host or release gates.

## Completed Handoff Acceptance

Sol's final four-project Release build passed with zero warnings/errors; the
complete .NET run passed 104/104 with none skipped, and `npm.cmd test` passed.
Astra independently reran the complete .NET command with
`--no-build --minimum-expected-tests 104`: 104 passed, zero failed/skipped,
1m 31s. The test-owned SQL/Kestrel/Playwright gate produced
`.artifacts/browser/819db08e57014459891d1c564a7a0b0e/`: ten major states,
zero serious/critical axe findings, expected desktop/mobile widths, loaded
images and all three authentication-race assertions. Astra inspected the
mobile form screenshot. The pre-existing Windows legacy no-Dao shell-test
limitation remains covered by the independent successful `rg` scan.

Final handoff publishes are under `.artifacts/slice-01-handoff-20260912/`.
Astra verified 309 Web files, 251 self-contained win-x64 migration files,
all 17 vendor hashes, all seven Hangfire operator-file hashes, the DACPAC,
included .NET runtime metadata, and zero dev-email/PocketBase/Node runtime
leakage. Web DLL SHA-256 is
`26e5d1916275c2fb5c0d64131e3667fc1bc9fef8b804889c4d6c645454c5ee8a`;
DACPAC SHA-256 is
`6a94ca03dd4f3c34e1d8dc844103ea953cb317c10b54aca502383a0075f2fd60`;
migration executable SHA-256 is
`9a583ef82473f0c4cb60ab092678f114d3418319c375449dd7c3c8b212b9ff62`.

Astra invoked the published executable directly, without `dotnet`, against
both synthetic stopped-source variants. The undated-event variant correctly
blocked with zero committed requests/staff/outbox. The separately dated source
was exported by that executable and passed fresh-target import, exact source
asset/JSON/event-time/placement assertions, original-package reconciliation,
different-package rejection and same-count target-value drift rejection.
Evidence directories end `13ea73eea43c43308eed3bb91f6b1e88` (expected failure)
and `ca0dc5f7514449429ece7aace088f740` (positive acceptance). Owned SQL databases
and subprocesses were removed. This is local exact-build verification, not a
tagged release or representative production-host rehearsal.

Fetched `origin/main` still equals the pinned PocketBase SHA. PR #264 remains
open/draft on `codex/csharp-port` against `main`. Remote CI success currently
applies only to HEAD `cff0285`, not the uncommitted slice. `git diff --check`
passes; authoritative pack files remain unchanged. No Slice 1 milestone or
independent Terra clearance is claimed by these acceptance results.

## Post-Fix Acceptance

Terra Pass 1 findings and their complete source-based triage/fixes are recorded
in `slice-01-review.md`. After the failed-login guard follow-up and correction
of a stale RID publication, the accepted local artifacts are under
`.artifacts/slice-01-handoff-20260912-pass1-verified/`. The fixed native CLI
passes actual-PocketBase-shaped legacy and origin fixtures; invalid remote
HTTP remains an atomic blocker. Existing package substitution/drift rejection,
historical timestamps, JSON values, branding and placement assertions survive.

Sol's final Release build has zero warnings/errors, full .NET suite 114/114
with no skips, npm/browser/format checks pass. Astra independently reran the
complete 114-test suite in 1m 41s with no failures/skips and verified the
failed-login regression fix. The receipt and corrected artifact identities are
in the review packet/record. These results supersede the earlier handoff as
current local acceptance but do not constitute Terra Pass 2 or release gates.

## Remaining Gates

A fresh Astra Max advisor was used only for the narrow temporary-transport
configuration question, not an architecture or formal full-slice review. The
bounded resolution and subsequent implementation cover configuration-unavailable
intent suppression and dynamically unavailable transport producing a fenced
retained-payload failure; the complete test runs above include those tests.
See `temporary-email-transport.md`. The consultation itself is not clearance.
Real token/provider work remains deferred; the file sender needs no dummy token.

Full Pass 2 resolved S1-R1 through S1-R5 and found S1-R6, request-specific CSP
wildcard expansion. Astra reproduced four expected failures among fourteen
real-HTTP cases against the frozen pre-fix publish using the executable pinned
source as oracle. Sol's fix passes all fourteen cases against a new source-built
publish plus the complete build, 118-test .NET, npm/browser and formatting gates.
Astra independently reran 118/118 with no skips in 1m 43s and verified artifact
hashes/exclusions. See `slice-01-review.md` and the Pass 3 dispatch packet for
the exact state/evidence. The same Terra completed full Pass 3 with no
substantive finding, verified all 95 receipt files and artifact identities/
exclusions, and independently passed five focused real-SQL CSP tests. S1-R1
through S1-R6 are resolved. Slice 1's test/review gate is satisfied; milestone
and remote CI status are recorded in `PORT-STATUS.md`. The final parent browser
run is `.artifacts/browser/e38cd0ffde2f4ce3a763bd985f722b6a/`, with ten clean
states, three authentication races, loaded images and no layout overflow;
Astra also inspected its mobile form screenshot.

Final staging exposed whitespace warnings in the exact hash-pinned upstream
Hangfire NOTICES/install.sql and four whitespace-only lines retained from the
PocketBase patron HTML. The earlier unstaged `git diff --check` did not include
these then-untracked files. Source bytes are intentionally preserved, not
silently reformatted; all other staged files pass `git diff --cached --check`.
These are nonblocking upstream-format exceptions, not application failures.

The real Rest 3-compatible, cancellable `Clc.Postmark.Api` integration remains
a release/rehearsal blocker. File output does not prove provider delivery,
webhooks, provider-specific tests, or production readiness. See
`temporary-email-transport.md` for the explicit deferred work.

## Remote CI Failure

The reviewed milestone `c1b86558ad3b2a7270c6cf1d1bfa7830911d7f44` was pushed
to PR #264. Run 34730692517 built successfully on Linux and installed browser
dependencies, but .NET/SQL tests finished with 77 passed, 41 failed and no skips
in 1m 36s. Frontend and publication steps did not run. The failed log is retained
locally as `.git/asap-slice-01-ci-34730692517.log`; see `slice-01-review.md` for
the two concrete root causes and correction boundary. The earlier Windows
acceptance is not evidence that Linux CI passed. Sol and Terra remain assigned
to close this before Slice 2; no production or release gate is claimed.

The narrow correction subsequently passed Sol's zero-warning/error Release
build, 120/120 .NET tests, 20 focused configuration tests, credential-protected
bootstrap migration, npm, published browser/SQL, 14-case CSP and native migration
acceptance. Astra independently reran all 120 tests with no skips in 1m 39s and
verified the fresh source-built artifacts' exact hashes/exclusions. See the CI
closure dispatch in `slice-01-review-packet.md` for receipt, artifact and run
identities. Same-Terra review and actual Linux CI remain required before closing
the correction; Windows results alone do not satisfy that gate.

Same-Terra full Slice 1 closure review is now clean. The reviewer independently
verified the 96-file receipt/artifact evidence and passed the 20 configuration
tests plus the real-SQL protected-bootstrap migration test. The coherent CI
correction may be committed; its new actual Linux CI result is still required.

Correction commit `1e36761c771db70d0b669087d0a843b66cf5618b` then passed
remote Linux run 34731687718 in 3m 42s: build, 120/120 .NET/SQL tests with zero
skips, frontend tests, and publication checks. The actual log confirms the
counts. Together with the same-Terra closure this satisfies Slice 1's gate;
real Postmark and all later implementation/rehearsal gates remain outstanding.
