# ASAP Stack

- **Language/runtime:** C# on .NET 10, ASP.NET Core 10.
- **Web:** ASP.NET Core endpoints, Entra authentication, vanilla JavaScript,
  HTML, and CSS served from generated `wwwroot`.
- **Data:** SQL Server 2022 with an SDK-style `Sql160` DACPAC; EF Core is the
  default data access boundary, with selective parameterized Dapper/ADO.NET
  for established feature queries.
- **Jobs:** Hangfire with the pinned SQL schema asset and explicit business
  timezone/schedule configuration.
- **Migration:** `Asap.Migration`, a self-contained `win-x64` .NET executable
  using native SQLite loading for stopped legacy-source export and SQL import.
- **Frontend dependencies:** pinned browser assets under
  `src/Asap.Web/Frontend/vendor`, with `vendor-manifest.json` and licenses.
- **Development/CI tooling:** Node.js 24, npm, jsdom tests, Playwright, and
  axe-core. These tools never enter the application publish or IIS package.
- **Deployment:** Web publish plus DACPAC and a separate migration publish;
  repository-side test-IIS packaging and validation are implemented, while
  runner activation and production deployment remain deferred.
