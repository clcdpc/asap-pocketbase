# Auto Suggest a Purchase (ASAP) - ASP.NET Core MVC Edition

ASAP is now a **.NET 8 ASP.NET Core MVC** application backed by **SQL Server** and **Dapper**.  
This refactor removes all PocketBase dependencies and replaces hook-style patterns with a conventional MVC architecture.

## What changed
- Removed PocketBase folders (`pb_hooks/`, `pb_migrations/`, `pb_public/`) and JavaScript hook runtime.
- Added a C# MVC web application in `src/Asap.Web`.
- Added SQL Server-first persistence with Dapper repositories.
- Added SQL schema script in `sql/001_initial_schema.sql`.

## Prerequisites
- .NET 8 SDK
- SQL Server (local, container, or managed instance)

## Configure SQL Server connection
Update `src/Asap.Web/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "AsapSql": "Server=localhost,1433;Database=Asap;User Id=sa;Password=Your_password123;TrustServerCertificate=True"
  }
}
```

## Database setup
Run `sql/001_initial_schema.sql` against your target SQL Server database.

## Run locally
```bash
dotnet restore Asap.Mvc.sln
dotnet run --project src/Asap.Web/Asap.Web.csproj
```

Then open `https://localhost:5001` (or the URL shown by the runtime).

## Current MVC surface
- `GET /` Home page.
- `GET /suggestions` List open suggestions.
- `GET /suggestions/new` Create form.
- `POST /suggestions/new` Inserts suggestion with Dapper.

## Next migration steps
1. Port Polaris integration into C# services.
2. Add authentication/authorization via ASP.NET Core Identity or SSO.
3. Rebuild automation jobs using hosted services (`BackgroundService`) and SQL-backed queues.
4. Expand repository and domain model coverage for full workflow parity.

## License
This project is licensed under [COPYING.md](COPYING.md).
