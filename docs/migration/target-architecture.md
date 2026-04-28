# ASP.NET Core Migration Target Architecture

## Solution layout

- `src/Asap.Web`: ASP.NET Core host, controllers, middleware, auth setup, and hosted jobs.
- `src/Asap.Core`: domain contracts and business service interfaces.
- `src/Asap.Infrastructure`: Dapper repositories, SQL Server connectivity, and external adapters.
- `scripts/sql`: forward/rollback SQL migrations for SQL Server.

## Vertical slice implemented in this commit

1. `GET /api/v1/title-requests`
   - Supports `status`, `libraryOrgId`, `page`, `pageSize`.
   - Uses explicit Dapper SQL with `OFFSET/FETCH` pagination.
2. `POST /api/v1/title-requests/{id}/reject`
   - Applies status transition to `closed` with close reason/message.
   - Protects against duplicate closures in SQL `WHERE` clause.
3. Outstanding timeout cron worker
   - `OutstandingTimeoutHostedService` runs on `Jobs:OutstandingTimeoutCron`.
   - Calls processor that closes overdue `outstanding` requests.

## Next steps

- Fill out remaining controllers from route parity sheet.
- Add auth/RBAC middleware and policy tests.
- Port Polaris client + signature parity tests.
- Add integration tests against SQL Server container.
