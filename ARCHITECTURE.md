# Auto Suggest a Purchase (ASAP) Architecture Document

## 1. High-Level Overview

Auto Suggest a Purchase (ASAP) is a self-hosted, lightweight material suggestion management system for public libraries. It serves as a bridge between library patrons, staff collection development teams, and the Polaris Integrated Library System (ILS).

The project is built on **PocketBase**, which acts as both the database (SQLite) and the backend server (Go). Custom business logic is implemented using **PocketBase Hooks (JavaScript via the Goja engine)**. The application adheres to a "Zero Dependencies" philosophy: there is no `package.json`, no Node.js runtime required in production, and no frontend build steps. Everything is written using native APIs.

### Core Components
1.  **Frontend (Vanilla JS/HTML/CSS):** Served statically from `pb_public/`. It is split into two Single Page Applications (SPAs):
    *   `/patron/`: For patrons to log in with their barcode/PIN and submit suggestions.
    *   `/staff/`: For staff to manage suggestions, configure settings, and oversee automated jobs.
2.  **Backend (PocketBase + JS Hooks):** Located in `pb_hooks/`. It defines custom REST API endpoints, handles Polaris PAPI integration, executes scheduled background jobs, and sends emails.
3.  **Database (SQLite):** Managed via PocketBase migrations (`pb_migrations/`).

---

## 2. Data Models

The system relies on several core PocketBase collections:

*   **`title_requests`**: The primary entity. Represents a single suggestion made by a patron.
    *   **Fields:** `barcode`, `title`, `author`, `format`, `status`, `bibid`, `closeReason`, `libraryOrgId`, etc.
    *   **Statuses:** `suggestion` (new), `outstanding_purchase` (approved, waiting for BIB), `pending_hold` (BIB found, waiting for hold placement), `hold_placed` (hold successfully placed in Polaris), `closed` (fulfilled or rejected).
*   **`patron_users`**: Cached profiles of patrons who have logged in. Used for tracking limits and linking requests.
*   **`staff_users`**: Staff accounts. Supports Role-Based Access Control (RBAC): `staff`, `admin` (library-scoped), and `super_admin` (system-wide).
*   **`app_settings`**: Global configuration (Singleton record `settings0000001`). Stores Polaris PAPI credentials, SMTP settings, default email templates, and system-wide UI text.
*   **`library_settings`**: Branch-level configuration overrides. Allows individual libraries in a consortium to define their own email templates, UI text, and workflow timeouts.
*   **`polaris_organizations`**: Cached synchronization of the Polaris organization hierarchy.

---

## 3. Workflows

### 3.1. Patron Submission Workflow
1.  **Authentication:** Patron enters Barcode and PIN in the frontend. The backend calls the Polaris `authenticator/patron` endpoint.
2.  **Validation:** Upon success, the backend retrieves basic patron data and determines the patron's home library (`LibraryOrgID`).
3.  **Limits & Duplicates:** When submitting, the backend checks for duplicate requests (same title/format) and enforces weekly submission limits (configurable, default 5).
4.  **Creation:** The record is saved as `status: suggestion`. An automated "Suggestion Submitted" email is sent to the patron.

### 3.2. Staff Review Workflow
1.  **Evaluation:** Staff review items in the "Suggestions" tab.
2.  **Actions:**
    *   **Reject:** Sets status to `closed`, reason to `rejected`. Sends a rejection email.
    *   **Already Own:** Staff performs a BIB lookup. If a BIB ID is provided, the backend immediately calls Polaris to place a hold. Status becomes `closed`, reason `hold_completed`. Sends an "Already Owned/Hold Placed" email.
    *   **Silent Close:** Closes the request immediately (`closeReason: Silently Closed`) without sending any notification.
    *   **Purchase:** Moves the status to `outstanding_purchase`. No email is sent immediately.

### 3.3. Automated Workflows (Cron Jobs)
Background jobs are executed via PocketBase `cronAdd` hooks.

*   **Outstanding Purchase Promoter:** Searches Polaris for newly acquired titles. It takes records in `outstanding_purchase`, uses their `identifier` (ISBN/UPC) to query the Polaris `search/bibs` endpoint. If a match is found, it updates the record with the `bibid` and moves the status to `pending_hold`.
*   **Pending Holds Processor:** Takes records in `pending_hold`. It uses the Polaris `holdrequest` endpoint to automatically place a hold for the patron. If successful (or if Polaris returns error code 29/6 indicating the hold already exists), it moves the status to `hold_placed` and sends a notification email.
*   **Fulfillment Tracker:** Monitors records in `hold_placed`. It queries the Polaris `itemsout/all` endpoint for the patron's barcode. If the `bibid` of the request is found in the patron's current checkouts, the record is moved to `closed` (`hold_completed`).
*   **Timeouts:**
    *   *Outstanding Timeout:* Rejects `suggestion` records older than X days.
    *   *Hold Pickup Timeout:* Closes `hold_placed` records older than Y days (`hold_not_picked_up`) if the patron never checked the item out.

---

## 4. Integration Details (Polaris PAPI)

The application communicates with the Polaris REST API (PAPI).

*   **Authentication mechanism:** The integration uses HMAC-SHA1 signatures built from the API Key, Access ID, HTTP method, URI, and date. For endpoints requiring staff context, an `AccessSecret` and `AccessToken` (obtained via `/authenticator/staff`) are included.
*   **Key Endpoints Used:**
    *   `POST /authenticator/staff`: Staff login.
    *   `POST /authenticator/patron`: Patron login/PIN validation.
    *   `GET /patron/{barcode}/basicdata`: Patron profile and library org.
    *   `GET /search/bibs/keyword/ISBN`: Used by the Auto-Promoter.
    *   `POST /holdrequest`: Placing holds. Uses an XML payload (`HoldRequestCreateData`).
    *   `GET /patron/{barcode}/itemsout/all`: Checking fulfillment status.
*   **XML Security:** The `buildXml` utility function in `polaris.js` automatically applies strict character escaping (`&`, `<`, `>`, `"`, `'`) to prevent XML injection vulnerabilities when sending payloads to Polaris.

---

## 5. Security & Access Control

*   **RBAC:** Handled at the API route level.
    *   `super_admin`: Can access system settings, SMTP, global Polaris config, and view all branches.
    *   `admin`: Can configure `library_settings` overrides for their specific branch.
    *   `staff`: Read/write access to `title_requests` scoped to their specific branch.
*   **Consortia Scoping:** When a staff member logs in, their Polaris `BranchID` is resolved to a `LibraryOrgID`. Subsequent API requests strictly filter database queries to only return/modify records belonging to that `LibraryOrgID`.
*   **Emergency Bypass:** An `overridePassword` can be configured globally to allow staff to log in locally even if the Polaris API is offline.
