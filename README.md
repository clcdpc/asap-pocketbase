# Auto Suggest a Purchase (ASAP)

ASAP is a self-hosted material-suggestion system for public libraries using
the Polaris ILS. Patrons submit suggestions through the public portal; staff
review, purchase, place holds, and track fulfillment from the staff portal.

The shipped application is ASP.NET Core 10 on .NET 10 with SQL Server 2022.
Schema is owned by the SDK-style DACPAC in `database/Asap.Database`. The web
application uses Entra authentication, direct EF Core for ordinary persistence,
feature services with selective parameterized Dapper/ADO.NET where the feature
contract needs it, and Hangfire for recurring work. The tracked frontend lives
under `src/Asap.Web/Frontend` and is copied to generated `wwwroot` by MSBuild.

Development work is complete; the combined final batch is integrated on
`codex/csharp-port`. See the [completion record](docs/implementation/final-development-completion.md)
for exact-milestone CI and acceptance. Production readiness and test-IIS runner
activation remain separate deferred work; PR #264 remains draft.

## Prerequisites

- .NET SDK 10.0.303 or the compatible SDK selected by `global.json`.
- SQL Server 2022 for development and real-SQL tests. SQLite is used only by
  the stopped-source migration tool.
- Node.js 24 and Chromium for the frontend and browser test suites.
- An Entra test identity/configuration for local authenticated journeys.

## Local development

1. Copy `src/Asap.Web/Development.local.example.json` to the ignored
   `src/Asap.Web/Development.local.json` and replace local paths, connection
   strings, and test credentials. Keep this file outside commits.
2. Start the application with the configured `https` launch profile:

   ```powershell
   dotnet run --project src/Asap.Web/Asap.Web.csproj --launch-profile https
   ```

   Checked-in `appsettings.json` contains the permanent IIS default, while
   `appsettings.Development.json` overrides only `Asap:ConfigFile` with
   `Development.local.json`. That ignored file is the actual local operational
   configuration. The application serves the staff and patron portals and
   exposes `/health/live` and `/health/ready`.

3. Apply the current DACPAC to a development SQL database using the normal
   SQL Server deployment tooling before exercising persistence features.

Operational configuration, Data Protection keys, and logs belong outside the
application directory. The application does not create or overwrite those
files during deployment.

## Build and test

```powershell
dotnet restore Asap.sln
dotnet build Asap.sln --configuration Release
npm ci
npm test
```

The Node suite discovers the retained frontend regression files under
`tests/*.test.js`. The complete .NET suite uses real SQL Server and deterministic
Polaris/Postmark boundaries:

```powershell
dotnet test --project tests/Asap.Tests/Asap.Tests.csproj --configuration Release --minimum-expected-tests 400
```

Set `ASAP_TEST_SQL_CONNECTION_STRING` when the local SQL connection is not the
default integrated localhost connection. The browser fixture methods are part
of the .NET test project and are also selected by the normal browser command:

```powershell
npm run test:browser
```

That command runs the patron, staff, and legacy-request-link Playwright
journeys against loopback Kestrel with real SQL. No live Polaris, Postmark, or
IIS host is required for ordinary development CI.

## Migration

`src/Asap.Migration` is a separate self-contained `win-x64` executable for a
controlled cutover from a stopped legacy SQLite source and its file storage.
It exports normalized, hashed JSON, validates the package, imports into a
fresh SQL target, and reconciles the result. Read its
[migration README](src/Asap.Migration/README.md) and the preserved source notes
before handling real data. Native SQLite loading is intentional; the web app
does not use SQLite as a persistence substitute.

The migration source identity is pinned in code and in the package manifest.
`database/Asap.Database/Tables/LegacyPocketBaseMapping.sql` preserves the
source-to-target mapping needed for legacy request links and migration audit.

## Test-IIS package

The repository workflow builds and validates the Web publish, DACPAC,
self-contained migration output, and the test-IIS ZIP. The package uses the
exact tested commit and a digest-before-extract gate. The deployment script's
`-ValidateOnly` mode checks extraction and manifest integrity without mutating
an IIS host. See [test-IIS activation](docs/implementation/test-iis-activation.md)
for the separately pending runner/host task. The IIS job remains skipped until
`test_cd_activation: pending_runner_setup` is changed by that authorized task.

## Scope and release boundary

Production provider validation, live Polaris/Postmark mutations, production
tagging, cutover, backup/recovery rehearsal, and IIS activation are deferred.
The temporary email transport is a deterministic development/test boundary,
not production evidence. A stopped legacy source is forensic/migration input
only and is never documented as a writable fallback after cutover.

See [ARCHITECTURE.md](ARCHITECTURE.md), [DESIGN.md](DESIGN.md),
[PRODUCT.md](PRODUCT.md), [STACK.md](STACK.md), and the current
[port status](docs/implementation/PORT-STATUS.md) for the maintained project
contracts. The full historical porting pack remains under `docs/dotnet-port`.

## License

This project is licensed under the terms included in
[COPYING.md](COPYING.md).
