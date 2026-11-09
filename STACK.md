# ASAP Stack

- **Language/runtime:** C# on .NET 10, ASP.NET Core 10.
- **Web:** ASP.NET Core endpoints, Entra authentication, vanilla JavaScript,
  HTML, and CSS served from generated `wwwroot`.
- **Data:** SQL Server 2022 with an SDK-style `Sql160` DACPAC; EF Core is the
  default data access boundary, with selective parameterized ADO.NET and raw
  SQL for established feature queries. There is no Dapper package or implementation.
- **Jobs:** Hangfire with the pinned SQL schema asset and explicit business
  timezone/schedule configuration.
- **Frontend dependencies:** pinned browser assets under
  `src/Asap.Web/Frontend/vendor`, with `vendor-manifest.json` and licenses.
- **Development/CI tooling:** Node.js 24, npm, jsdom tests, Playwright, and
  axe-core. These tools never enter the application publish or IIS package.
- **Qualification:** exact discovered .NET Browser/non-browser populations,
  the complete retained frontend suite, and current-commit package checks.
  [Three hosted workers](scripts/ci/test-selection.md) own independent builds,
  serial fixture commands and guarded evidence reuse.
- **Deployment:** Web publish plus DACPAC, with identity/digest validation and
  nonmutating `-ValidateOnly`. IIS activation uses its separate authorized runner.
  [Initial test-IIS activation](docs/implementation/test-iis-activation.md) succeeded
  November 5, 2026; that receipt establishes neither current installed source nor
  current host health. Production readiness and deployment remain deferred.
