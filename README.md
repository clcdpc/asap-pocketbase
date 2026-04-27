# Auto Suggest a Purchase (ASAP)

ASAP is an automated material suggestion management system for public libraries using the Polaris ILS and PocketBase. It streamlines the lifecycle of patron suggestions—from submission and catalog monitoring to automated hold placement and fulfillment tracking.

Credit to Somalia Jamall for the intial concept and for developing a fine PHP based implementaion: https://forum.innovativeusers.org/t/auto-suggest-a-purchase-code/2894

## 🚀 Getting Started

### 1. Installation
1. **PocketBase**: Download the [PocketBase binary](https://pocketbase.io/docs/) for your OS and place it in the project root. This project is currently tested with PocketBase `0.36.9`.
2. **Files**: Ensure the following directories are in your project folder:
   - `pb_hooks/`: The backend logic and Polaris integration.
   - `pb_public/`: The frontend application files (Staff & Patron).
   - `pb_migrations/`: Database schema and initial configuration.
3. **PocketBase Superuser**: Create one PocketBase superuser account before you manage the database from the PocketBase admin dashboard. You can do this either by launching PocketBase once and following the install prompt in `/_/`, or by running the `superuser upsert EMAIL PASSWORD` command using the PocketBase executable.

   The superuser is for PocketBase itself, not for the ASAP staff login flow. Use it to open the PocketBase dashboard, inspect collections and records, review migrations, and manage the app’s stored data and settings.

### 2. Launch the Server
Run the executable in your terminal with the `serve` flag:
```sh
# Mac/Linux
./pocketbase serve

# Windows
pocketbase.exe serve
```
The application will be available at `http://127.0.0.1:8090`.

### 3. Initial Setup Wizard
1. Open your browser to `http://127.0.0.1:8090/staff/`.
2. You will be greeted by the **Initial Setup Wizard**.
3. Provide your **Polaris PAPI credentials** and create your first **Super Admin Staff Account**.
4. Once completed, you can log in to the Staff Dashboard to configure SMTP (Email) and other preferences.

If this is a brand-new PocketBase database, the ASAP setup flow and the PocketBase superuser are separate:
- The ASAP setup wizard creates the first staff super admin account for the application.
- The PocketBase superuser manages PocketBase’s built-in admin dashboard and database administration tasks.

### 4. Consortia Staff Identity
For library consortia, staff accounts are scoped by Polaris domain, username, and parent library organization. Staff can log in as `DOMAIN\username`, `username@domain`, or a bare username when a default staff domain is configured in settings.

After setup:
- Use **Settings > Polaris** to manually sync the Polaris organization hierarchy when needed. The system also syncs organizations automatically once a day at 2 AM server time.
- Use **Settings > Staff Access** to manage staff roles. `super_admin` users can manage global settings and all libraries; `admin` and `staff` users are scoped to their resolved parent library.

### 5. Consortia & Multi-Library Templates
In a consortia environment, ASAP allows each member library to maintain its own unique communication style:
- **Cascading Templates**: The system follows a "most-specific" logic. If a library has custom email templates, they are used; otherwise, the system automatically falls back to the **System Default** templates defined in global settings.
- **Library Admin Scoping**: Staff with the `admin` role can access the **Email Templates** section of settings but are locked to their own library's data. They can customize their library's messaging without affecting others.
- **Super Admin Oversight**: `super_admin` users have a library selector that allows them to manage templates for any library in the system or update the global system defaults.
- **Pre-population**: When creating a new library override, the system automatically pre-populates the editor with the current system defaults as a starting point.
- **Reset Capability**: Libraries can easily revert to the consortia standard using the "Reset to System Defaults" tool.

---

## ✨ Key Features

- **Polaris Integration**: Real-time patron authentication, BIB/Hold lookups, and automated hold placement via Polaris API (PAPI).
- **Automated Workflow**:
  - **Auto-Promoter**: Periodically searches the catalog for items in "Pending Purchase" and promotes them to "Pending Hold" once a matching BIB ID is found.
  - **Auto-Hold Placement**: Automatically places holds in Polaris for items ready in the "Pending Hold" queue.
  - **Fulfillment Tracking**: Monitors "Hold Placed" items and automatically closes them once the patron has checked out the material.
  - **Auto-Reject**: Configurable background job to reject old, unprocessed suggestions.
- **Staff Dashboard**: A high-density management interface with real-time updates, status tracking, and "Silent Close" capabilities for administrative cleanups.
- **Customizable Experience**: 
  - Upload your library logo.
  - Customize all patron-facing text, including login prompts, submission notes, and success messages.
  - Professional email templates with placeholder support (`{{name}}`, `{{firstName}}`, `{{lastName}}`, `{{title}}`, `{{author}}`, `{{format}}`, and `{{barcode}}`).
- **Smart Logic**: Detects duplicate holds in real-time and prevents staff from accidentally approving requests that the patron already has on hold in Polaris.

---

## 🛠 Workflow Overview

### 1. Submission
Patrons log in with their Library Card and PIN. The system verifies their status via Polaris and checks for weekly suggestion limits.

### 2. Staff Review (Suggestions Tab)
Staff evaluate new requests with one-click actions:
- **Purchase**: Moves the item to **Outstanding Purchase**.
- **Already Own**: Staff perform a BIB lookup; the system then **auto-places a hold** for the patron in Polaris and moves the record to **Hold Placed**.
- **Reject**: Closes the request and notifies the patron via email.
- **Silent Close**: Closes the request immediately without sending any patron notification.

### 3. Catalog Monitoring (Outstanding Purchase Tab)
Items wait here for their BIB ID to appear in the library catalog.
- **Automated Promoter**: The system periodically searches Polaris for these titles. Once a match is found, it automatically moves the record toward fulfillment.

### 4. Fulfillment (Pending Hold & Hold Placed)
The system ensures that as soon as an item is available in the catalog, a hold is placed for the requesting patron. Once the patron checks out the item, the system detects the transaction and moves the record to **Closed**.

---

## ⚙️ Automation Jobs
The system runs request workflow background tasks every hour (configurable with `ASAP_CRON_SCHEDULE`):
- `outstanding_timeout`: Rejects old unprocessed suggestions.
- `process_pending_holds`: Places holds in Polaris for ready items.
- `process_checked_out`: Closes fulfilled requests.
- `process_outstanding_purchases`: Searches for items newly added to the catalog.

The system also runs a Polaris organization sync once a day at 2 AM server time. Override that schedule with `ASAP_ORG_SYNC_CRON_SCHEDULE` if needed.

You can manually trigger these jobs or adjust their settings from the **Settings** tab in the Staff Dashboard.

---

## 🔒 Network & Security

### 1. Internet Accessibility
To make ASAP internet-accessible, you can use **NAT (Network Address Translation)** or, more ideally, a **Reverse Proxy** (e.g., Nginx, Caddy, or Apache).
- **Reverse Proxy Recommended**: Using a reverse proxy is the gold standard for production. It allows you to easily manage SSL/TLS certificates (e.g., via Let's Encrypt), handle load balancing, and add extra security headers.

### 2. Securing the Admin UI
The PocketBase admin dashboard is accessible at `/_/`. **Do not leave this path exposed to the open internet.**
- **Access Control**: Configure your firewall or reverse proxy to restrict access to `/_/` to trusted IP addresses or require a VPN connection for administrative tasks.

### 3. Rate Limiting
PocketBase provides built-in rate limiting to protect your server from brute-force attacks and automated abuse.
- **Enable Rate Limits**: In the PocketBase Admin UI, navigate to **Settings > Application > Rate Limit** and configure appropriate thresholds for your environment. This is especially important if you are exposing the API to the public internet.

---

## 🔐 Emergency Access

### Emergency Override Password
The **Emergency Override Password** is a global setting that provides a local bypass for staff authentication. It is primarily intended for system recovery and troubleshooting.

- **Purpose**: It allows staff users who have logged into ASAP at least once to access the Staff Dashboard even if the Polaris API is unavailable or connectivity is broken.
- **How it works**: If a staff member enters their username and the Emergency Override Password (instead of their Polaris password), ASAP will bypass the Polaris API call and log them in based on their existing local account.
- **When to use**: Use this during initial configuration if you are still resolving Polaris network issues, or during a Polaris outage to maintain access to the ASAP dashboard.
- **Security Warning**: **This password should normally be blank in production.** If enabled, it provides a "backdoor" that applies to all staff accounts. Ensure it is a high-entropy password and only shared with trusted system administrators.

---

## 🚢 Production Delivery Checklist

Before sharing or deploying this project outside a local development machine:

- Ship source files only: `pb_hooks/`, `pb_migrations/`, `pb_public/`, `README.md`, and `COPYING.md`.
- Do not ship local runtime data or binaries: `pb_data/`, `pocketbase`, `pocketbase.exe`, `.env`, logs, or `.DS_Store` files.
- Have each library download its own official PocketBase binary and run migrations against a fresh `pb_data/` folder.
- Complete initial ASAP setup from a trusted network before exposing the service publicly.
- Use HTTPS for the Polaris PAPI host in production. Protected PAPI calls include staff credentials, access secrets, patron PIN validation, patron data, and hold placement.
- Review the [Emergency Access](#-emergency-access) section before launch. The override password should normally be blank in production.

---

## 💻 Technical Architecture

- **Backend**: PocketBase (SQLite + Go VM) with custom JavaScript hooks.
- **Frontend**: Vanilla JavaScript (ES6+), HTML5, and CSS3.
- **No Build Step**: Built with native browser APIs and standard PocketBase hooks, with no `npm install` or frontend build step required.
- **Portable Runtime**: Deploy the source folders with an official PocketBase binary, then let each environment create and own its local `pb_data/`.

---

## 📝 License
This project is licensed under the terms included in the [COPYING.md](COPYING.md) file.

## 🧰 Visual Studio Local Host (No PocketBase Required)

If you want to run ASAP directly from Visual Studio without PocketBase, use the included solution:

- Solution: `visualstudio/Asap.VisualStudio.sln`
- Project: `visualstudio/Asap.VisualStudioHost/Asap.VisualStudioHost.csproj`

### What this mode does
- Serves the existing `pb_public/` frontend directly from ASP.NET Core.
- Uses controller-based API endpoints under `/api/asap/*`.
- Includes a PocketBase compatibility controller for the specific staff UI calls the app makes.
- Stores all data in memory for local testing (no PocketBase runtime, no `pb_data/`).

### Run in Visual Studio
1. Open `visualstudio/Asap.VisualStudio.sln`.
2. Set `Asap.VisualStudioHost` as startup project.
3. Press **F5**.
4. The app launches at `http://localhost:5179` and routes to `/staff/`.

> Note: This mode is intended for local development/testing and does not persist data between runs.
