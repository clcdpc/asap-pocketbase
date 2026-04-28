# ASAP MVC Architecture

## Runtime stack
- ASP.NET Core MVC (`net8.0`) for routing, controller/action composition, Razor views, and request handling.
- SQL Server as the sole persistence layer.
- Dapper for lightweight data access and SQL-first repository implementations.

## Project layout
- `src/Asap.Web/Program.cs` boots the MVC app and registers dependencies.
- `src/Asap.Web/Controllers` contains MVC controllers for pages and form actions.
- `src/Asap.Web/Models` holds view models and core data models.
- `src/Asap.Web/Repositories` encapsulates SQL access patterns using Dapper.
- `src/Asap.Web/Data` provides SQL Server connection factories.
- `sql/` stores SQL Server schema migrations/scripts.

## Design principles
1. **PocketBase removed**: No PocketBase runtime, hooks, migration files, or static app bundles are required.
2. **SQL-first data access**: Repository methods use explicit SQL statements reviewed and versioned in source control.
3. **MVC boundaries**: Controllers orchestrate HTTP flow only; repositories handle persistence concerns.
4. **Dependency injection**: All infrastructure components are registered in `Program.cs` and injected into controllers/repositories.
