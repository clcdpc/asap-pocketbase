# Auto Suggest a Purchase (ASAP) Architecture Document

## 1. High-Level Overview

Auto Suggest a Purchase (ASAP) is a self-hosted, lightweight material suggestion management system for public libraries. It serves as a bridge between library patrons, staff collection development teams, and the Polaris Integrated Library System (ILS).

The project is built on **PocketBase**, which acts as both the database (SQLite) and the backend server (Go). Custom business logic is implemented using **PocketBase Hooks (JavaScript via the Goja engine)**. The application adheres to a "Zero Dependencies" philosophy for runtime: there is no frontend build step and no production Node runtime requirement.

### Core Components
1. **Frontend (Vanilla JS/HTML/CSS):** Served statically from `pb_public/`. It is split into two Single Page Applications (SPAs):
   - `/patron/`: patrons log in with barcode/PIN and submit suggestions.
   - `/staff/`: staff manage suggestions, configure settings, and oversee jobs.
2. **Backend (PocketBase + JS Hooks):** Hook entrypoint in `pb_hooks/main.pb.js`, with modular backend code in root-level `lib/`.
3. **Database (SQLite):** Managed via PocketBase migrations (`pb_migrations/`).

---

## 2. Data Models

The system relies on several core PocketBase collections:

- **`title_requests`**: Primary suggestion record.
  - **Fields:** `barcode`, `title`, `author`, `format`, `status`, `bibid`, `closeReason`, `libraryOrgId`, etc.
  - **Statuses:** `suggestion`, `outstanding_purchase`, `pending_hold`, `hold_placed`, `closed`.
- **`patron_users`**: Cached patron profiles.
- **`staff_users`**: Staff accounts with RBAC (`staff`, `admin`, `super_admin`).
- **`system_settings`, `polaris_settings`, `smtp_settings`**: global integration/system config.
- **`workflow_settings`, `ui_settings`, `email_templates`, `patron_settings_overrides`**: scoped settings and override records.
- **`polaris_organizations`**: cached Polaris organization hierarchy.

---

## 3. Request, Route, and Job Architecture

### 3.1 Hook Entrypoint + Route Registry

`pb_hooks/main.pb.js` boots the application, then delegates route wiring to the root-level route registry (`lib/route_registry.js`). This keeps endpoint registration centralized and makes route availability explicit.

### 3.2 Route Facades

- **Staff route facade:** `lib/staff_routes.js` re-exports grouped route installers from `lib/staff/*` (auth, users, lookup, title-request actions, settings, analytics, admin).
- **Patron/setup/job facades:** `lib/patron_routes.js`, `lib/setup_routes.js`, and `lib/job_routes.js` expose cohesive registration surfaces for their domains.

### 3.3 Shared Backend Modules

Root-level `lib/` is organized by domain:

- `lib/config/*`: config defaults, normalization, scoped resolution, SMTP/email/polaris config helpers.
- `lib/jobs/*`: automation pipelines (ISBN checks, purchase promotion, hold placement, fulfillment tracking, timeouts, weekly summary).
- `lib/polaris/*`: auth helpers and endpoint-specific Polaris clients.
- `lib/records/*`: title request, patron, staff, duplicate, and tagging data access helpers.
- Route and utility modules such as `route_utils.js`, `http_utils.js`, `authz.js`, and `html_utils.js`.

### 3.4 Workflow Summary

1. **Patron submission:** authenticate against Polaris, validate, de-duplicate, enforce limits, create `suggestion`.
2. **Staff review:** reject, close, place manual hold/already-own, or promote for purchase flow.
3. **Automations (cron + manual triggers):** promote to `pending_hold`, place holds, track fulfillment, and timeout stale states.

---

## 4. Integration Details (Polaris PAPI)

The application communicates with Polaris REST APIs using HMAC-SHA1 signatures.

- **Auth pattern:** signed requests using API key + access credentials + request metadata.
- **Representative endpoints:** patron/staff auth, patron basic data, bib search, hold placement, and patron checkout retrieval.
- **Payload safety:** XML payload generation escapes unsafe characters before hold request submission.

---

## 5. Security, Scope, and Access Control

- **RBAC + scope:** route handlers enforce `super_admin` (global), `admin` (library-scoped admin), and `staff` (library-scoped ops).
- **Library scoping:** runtime queries and analytics are constrained by effective library scope, not UI-only filtering.
- **Settings scoping:** system-level defaults plus library overrides are resolved explicitly at read-time; writes target an intentional scope.
- **Operational safety:** logging and notes paths avoid leaking sensitive patron and credential data.
