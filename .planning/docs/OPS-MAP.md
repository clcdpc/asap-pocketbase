<!-- generated-by: gsd-doc-writer -->
# Operations Map: Auto Suggest a Purchase (ASAP)

This document describes the deployment, maintenance, and security operations for the Auto Suggest a Purchase (ASAP) system.

## Deployment Overview

ASAP is designed for simplicity and portability, following a single-binary deployment model.

### 1. Architectural Components
- **Server Engine:** PocketBase (Go binary).
- **Database:** SQLite (stored in `pb_data/data.db`).
- **Logic:** JavaScript hooks and libraries (stored in `pb_hooks/`).
- **Static Assets:** Patron and Staff SPAs (stored in `pb_public/`).

### 2. Deployment Pattern
Deployment typically involves placing the `pocketbase` executable and the associated `pb_hooks`, `pb_migrations`, and `pb_public` directories on a target server.

- **Automated Migrations:** PocketBase automatically detects and executes pending migrations in `pb_migrations/` upon startup.
- **Service Configuration:** The application is usually run as a systemd service or similar process manager.
- **Port Binding:** Defaults to `:8080` (HTTP) or `:443` (HTTPS with built-in Let's Encrypt support).

## Maintenance Procedures

### 1. Backup & Restore
Since the system uses SQLite, backups are straightforward:
- **Automatic Backups:** PocketBase provides a built-in backup utility via the Admin UI or CLI (`./pocketbase backup create`).
- **Manual Backups:** Copy the entire `pb_data/` directory. For a consistent backup while the server is running, use the SQLite `VACUUM INTO` command or the PocketBase backup tool.
- **Restoration:** Replace the `pb_data/` directory with a backup copy and restart the service.

### 2. Monitoring & Logging
- **Application Logs:** PocketBase logs all HTTP requests and application-level errors. These are viewable in the Admin UI under "Logs" or via stdout.
- **System Logs:** Background job execution (Cron) and Polaris API errors are logged to the PocketBase logger (visible in the Admin UI).
- **Status Checks:**
    - `/api/asap/setup/status`: Checks if the system is initialized and if Polaris is configured.
    - `/api/asap/staff/email-status`: Verifies SMTP and email delivery health.

## Security Map

### 1. External Integration Security (Polaris PAPI)
The system communicates with the Polaris Integrated Library System using its REST API (PAPI).
- **HMAC-SHA1 Signatures:** All requests to Polaris are signed using an HMAC-SHA1 signature generated from the HTTP method, URI, Date, and the API Key/Secret.
- **Credential Storage:** Polaris API keys and staff credentials are stored in the `polaris_settings` collection, restricted to `super_admin` access.
- **Redaction:** Sensitive patron data (Barcode, Password, Name) is redacted from logs before being recorded.

### 2. Internal Authentication & Authorization
- **Internal Tokens:** PocketBase uses JSON Web Tokens (JWT) for session management. These tokens are signed using the **HS256 (HMAC-SHA256)** algorithm with a secret key generated during initial setup.
- **Role-Based Access Control (RBAC):**
    - `super_admin`: Full system access.
    - `admin`: Management of a specific library or branch.
    - `staff`: Operation of the suggestion dashboard.
- **Identity Parsing:** The system supports `DOMAIN\username` and `username@domain` formats, normalizing them into internal identity keys for consistent lookup.

### 3. Data Protection & Tightening
- **Collection Rules:** Security tightening migrations (`202604300007_security_tightening.js`) restrict direct access to sensitive collections (`system_settings`, `polaris_settings`, `smtp_settings`) to `super_admin` users only.
- **Field Redaction:** Custom hooks (`onRecordViewRequest` in `main.pb.js`) redact internal staff fields (e.g., internal notes, editor names) from records when viewed by a patron.
- **CORS:** PocketBase enforces CORS policies to prevent unauthorized cross-origin requests.

### 4. Audit & Compliance
- **Deleted Request Audit:** A dedicated migration (`202604300008_deleted_request_audit.js`) ensures that even when a request is deleted, an audit trail is preserved.
- **Staff Activity Tracking:** The system tracks the `lastLogin` of staff users to monitor account usage.

## VERIFY: Infrastructure Claims

<!-- VERIFY: The location and rotation policy for ASAP_CRON_SECRET environment variables. -->
<!-- VERIFY: Production backup schedule and offsite storage location. -->
<!-- VERIFY: Exact SMTP TLS requirements (STARTTLS vs. SSL/TLS) for the target environment. -->
