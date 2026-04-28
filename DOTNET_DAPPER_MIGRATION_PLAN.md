# ASAP Migration Plan: PocketBase → Native ASP.NET Core + Controllers + Dapper + SQL Server

## 1) Goals and Non-Goals

### Goals
- Replace PocketBase runtime and JS hooks with a native ASP.NET Core web application.
- Preserve existing behavior for staff and patron workflows, Polaris integration, RBAC, automation jobs, and emails.
- Use Dapper for all database interactions (no Entity Framework Core).
- Move from embedded SQLite to Microsoft SQL Server as the primary production database.
- Keep existing static front-end apps (`/staff` and `/patron`) operational during migration.

### Non-Goals
- Major UI rewrite in the same migration effort.
- Introducing a new frontend framework.
- Re-platforming Polaris integration semantics unless required for parity.

---

## 2) Target Architecture

### Application shape
- **ASP.NET Core Web App** (`net8.0` or latest LTS) using:
  - **Controllers** for REST endpoints.
  - **BackgroundService + Quartz/Hangfire (or Cronos + hosted services)** for scheduled jobs.
  - **Static file hosting** for `pb_public` assets during transition.
  - **Dapper** repositories over ADO.NET connections.
- **Database**: Microsoft SQL Server as the primary target, with explicit schema, indexing, and operational controls suitable for multi-user production workloads.

### Suggested project layout
- `src/Asap.Web` – ASP.NET host, controllers, middleware, auth.
- `src/Asap.Core` – domain models, services, business rules.
- `src/Asap.Infrastructure` – Dapper repositories, SQL scripts, Polaris clients, email adapters.
- `src/Asap.Jobs` (optional if not in Web host) – background workflows.
- `tests/Asap.*` – unit/integration tests.

---

## 3) Data Migration Strategy (PocketBase/SQLite → SQL Server)

### 3.1 Inventory and schema freeze
1. Inventory PocketBase collections and fields from `pb_migrations` and hook usage.
2. Freeze schema changes in current PocketBase branch while migration is underway.
3. Define canonical SQL Server schema (tables, constraints, indexes, defaults, and naming conventions).

### 3.2 SQL schema mapping (initial)
Map each collection to a relational table:
- `title_requests` → `title_requests`
- `patron_users` → `patron_users`
- `staff_users` → `staff_users`
- `app_settings` → `app_settings`
- `library_settings` → `library_settings`
- `polaris_organizations` → `polaris_organizations`

Add:
- `created_at`, `updated_at` audit columns.
- Unique constraints and indexes aligned with current query patterns.
- Check constraints for status/role enums when feasible.

### 3.3 Data export/import
- Build one-time export tool from PocketBase (JSON/CSV).
- Build deterministic import tool into SQL Server schema.
- Validate row counts, required fields, enum value integrity, and FK integrity.

### 3.4 SQL Server-specific hardening
- Use `datetime2` for temporal columns and UTC everywhere.
- Implement `rowversion` where optimistic concurrency is needed.
- Define explicit clustered/nonclustered indexes for high-volume filters (`status`, `libraryOrgId`, `created_at`).
- Add migration scripts with rollback scripts for every release.
- Establish a backup/restore rehearsal before production cutover.

### 3.5 Dual-run validation
- Run PocketBase and .NET in parallel against copied datasets.
- Compare API responses and job outcomes for representative scenarios.

---

## 4) API and Controller Migration Plan

### 4.1 Endpoint inventory
- Enumerate all custom routes currently implemented in `pb_hooks`.
- Categorize by domain:
  - Auth (patron/staff)
  - Title request lifecycle
  - Settings (global + branch overrides)
  - Polaris organization sync
  - Job controls/trigger endpoints

### 4.2 Controller design
- `AuthController`
- `PatronController`
- `StaffController`
- `TitleRequestsController`
- `SettingsController`
- `OrganizationsController`
- `JobsController`

Use versioned routes from day one (`/api/v1/...`) to decouple future changes.

### 4.3 Service layer
- Implement domain services mirroring existing behavior:
  - Submission limits + duplicate detection
  - Status transitions
  - Hold placement logic and special Polaris error handling
  - Close reason semantics (reject, silent close, hold completed, timeout)

### 4.4 Validation and error contracts
- Use FluentValidation or custom validators.
- Define stable error response format for frontend compatibility.

---

## 5) Dapper Data Access Design

### 5.1 Patterns
- Repository/query-object pattern with explicit SQL.
- Keep SQL in dedicated files or query constants grouped by aggregate.
- Use parameterized queries exclusively.
- Use SQL Server-appropriate syntax and patterns (`OFFSET/FETCH`, TVPs for batch ops where useful).

### 5.2 Transaction boundaries
- Use explicit transactions for multi-step state changes:
  - e.g., approve suggestion + transition status + enqueue email/job record.

### 5.3 Concurrency
- Add optimistic concurrency marker (`rowversion`) for key mutable entities (`title_requests`, settings tables).

### 5.4 Performance
- Add covering indexes for key filters (status, libraryOrgId, created_at).
- Benchmark job queries and pagination endpoints.
- Capture query plans for critical endpoints and optimize hot-path SQL before cutover.

---

## 6) Authentication and Authorization

### 6.1 Staff auth
- Replace PocketBase auth with ASP.NET authentication:
  - Cookie auth for server-hosted pages or JWT for SPA API calls.
- Maintain existing login identifiers (`DOMAIN\\user`, `user@domain`, bare username with default domain logic).

### 6.2 RBAC and scope enforcement
- Policies: `super_admin`, `admin`, `staff`.
- Library scope middleware/filters for admin/staff endpoints.
- Centralize scope checks to avoid controller drift.

### 6.3 Emergency override password
- Preserve behavior with stronger safeguards:
  - hash-at-rest
  - explicit audit logging
  - optional env-flag kill switch

---

## 7) Polaris Integration Port

### 7.1 Client implementation
- Build typed HTTP client(s) with resilience:
  - retries with jitter for transient failures
  - timeout and circuit-breaker policy
- Port HMAC signing logic exactly to avoid authentication regressions.

### 7.2 XML handling
- Keep secure XML encoding behavior for hold request payloads.
- Add unit tests for escaping and signature generation parity.

### 7.3 Regression suite
- Golden-request tests comparing .NET-generated auth headers/signatures to current implementation outputs.

---

## 8) Background Jobs Migration

### Jobs to implement
- Outstanding timeout
- Pending holds processor
- Checked-out fulfillment processor
- Outstanding purchase promoter
- Daily Polaris org sync

### Scheduling approach
- Start with hosted services + Cron parser (simple deployment).
- Optionally move to Hangfire if dashboard/retry visibility is required.

### Idempotency and safety
- Ensure each job can be re-run without duplicate side effects.
- Use state guards and transaction-scoped updates.

---

## 9) Email and Template System

- Port template placeholders exactly: `{{name}}`, `{{firstName}}`, etc.
- Preserve system-default + branch override cascading behavior.
- Add preview/test endpoint for admins.
- Keep SMTP config in secure config store (env vars/secret manager), with DB-backed overrides if needed.

---

## 10) Frontend Transition Strategy

### Option A (lowest risk)
- Keep existing `pb_public` static assets and adapt API base URL to new controllers.
- Implement compatibility endpoints where payload shape has drift.

### Option B (incremental cleanup)
- Minimal JS refactor to standardize API client and error handling while preserving UI.

Recommendation: **Option A first**, then Option B after parity is achieved.

---

## 11) Observability, Security, and Operations

- Structured logging (Serilog or built-in JSON logs).
- Correlation IDs across API + job execution.
- Audit log table for privileged actions (settings, role changes, emergency auth).
- Health checks:
  - SQL Server connectivity
  - Polaris dependency check (optional degraded status)
  - SMTP reachability (optional)
- Rate limiting and request size limits.
- Secure headers and reverse-proxy hardening.
- Database operations:
  - SQL Server backup retention policy and restore drills
  - Least-privilege SQL login for app runtime
  - Connection resiliency and pool monitoring

---

## 12) Testing and Validation Plan

### Unit tests
- Domain transition rules.
- Polaris signature and XML encoding.
- Scope/RBAC policy checks.

### Integration tests
- Controller + Dapper against isolated SQL Server test database (LocalDB/Containerized SQL Server).
- End-to-end workflow tests for each request status path.

### Parity/UAT tests
- Scenario matrix comparing PocketBase behavior to .NET behavior.
- Staff/patron acceptance tests on staging with sanitized production-like data.

### Non-functional
- Load tests for list endpoints and hourly job cycle.
- Failure injection for Polaris timeout/retry behavior.

---

## 13) Rollout Plan (Phased)

### Phase 0: Discovery (1–2 weeks)
- Complete endpoint inventory, schema map, and parity acceptance criteria.
- Produce OpenAPI spec for target controllers.

### Phase 1: Foundation (1–2 weeks)
- Scaffold ASP.NET solution, auth, configuration, logging, Dapper base layer.
- Create SQL Server schema and migration scripts.

### Phase 2: Core APIs + Read paths (2–3 weeks)
- Implement read-heavy endpoints first (dashboard/settings/orgs).
- Hook frontend to read endpoints in staging.

### Phase 3: Write paths + workflow transitions (2–3 weeks)
- Implement submission, approvals, reject/close flows.
- Add transactional guarantees and auditing.

### Phase 4: Jobs + Polaris parity (2–3 weeks)
- Implement all scheduled processors and org sync.
- Complete regression suite for Polaris behavior.

### Phase 5: Cutover prep (1–2 weeks)
- Run dual-write or shadow-read checks (as feasible).
- Complete UAT and operational runbooks.

### Phase 6: Production cutover
- Freeze writes briefly.
- Final data migration delta.
- Switch traffic to ASP.NET app.
- Monitor with rollback window.

---

## 14) Risks and Mitigations

- **Behavior drift from hook logic** → Mitigate with explicit parity matrix and golden tests.
- **Polaris edge-case regressions** → Mitigate with replay tests and staged rollout.
- **Job duplicate actions** → Mitigate with idempotency keys/state guards.
- **Auth/RBAC mismatch** → Mitigate with policy tests + scoped integration tests.
- **Migration downtime** → Mitigate with rehearsed cutover and validated rollback.

---

## 15) Deliverables Checklist

1. Target architecture doc + OpenAPI spec.
2. SQL Server schema + migration scripts.
3. Data export/import tooling.
4. ASP.NET controllers and Dapper repositories.
5. Polaris .NET client with parity tests.
6. Background job processors + scheduler.
7. Security hardening + audit logging.
8. Test suite (unit/integration/parity/load).
9. Cutover and rollback runbook.

---

## 16) Suggested Immediate Next Actions

1. Build a **route-by-route parity spreadsheet** from `pb_hooks`.
2. Draft SQL DDL for all collections and review constraints/indexes.
   - Include SQL Server index strategy, `rowversion`, and backup/restore runbook.
3. Scaffold ASP.NET solution and implement one vertical slice:
   - `GET title requests`
   - `POST reject request`
   - one cron job (e.g., outstanding timeout)
4. Validate slice end-to-end with existing frontend against staging data.
