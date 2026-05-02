<!-- generated-by: gsd-doc-writer -->
# Tech Stack

This document outlines the technologies, libraries, and tools used in the Auto Suggest a Purchase (ASAP) project.

## Backend

The backend is built as a self-contained service using [PocketBase](https://pocketbase.io/).

- **Core Engine:** PocketBase v0.36.9+ (Go-based)
- **Database:** SQLite (Embedded)
- **Custom Logic:** JavaScript via PocketBase Hooks (executed by the Goja engine)
- **API Style:** REST (PocketBase standard API + custom endpoints in `pb_hooks/main.pb.js`)
- **Authentication:** 
  - Staff: Built-in PocketBase Auth (managed via the `staff_users` collection)
  - Patron: Custom integration with Polaris PAPI
- **Security:** HMAC-SHA1 signatures for Polaris PAPI (via bundled `crypto-js`)
- **Background Jobs:** PocketBase Cron hooks (`cronAdd`)

## Frontend

The project features two separate Single Page Applications (SPAs) following a "no-build" philosophy (Vanilla JS, no transpilation or bundling).

### Staff Application (`/staff/`)
- **Language:** Vanilla JavaScript (ES6 Modules)
- **CSS Framework:** Bootstrap 4.1.3
- **Iconography:** Font Awesome 4.7.0
- **Data Grid:** [Grid.js](https://gridjs.io/) 6.2.0
- **Client SDK:** PocketBase JS SDK (UMD)
- **Modals:** Native HTML `<dialog>` element

### Patron Application (`/patron/`)
- **Language:** Vanilla JavaScript (ES6 Modules)
- **CSS Framework:** Bootstrap 4.1.3
- **Iconography:** Font Awesome 4.7.0
- **API Interaction:** Native `fetch` API (no external SDKs for a lightweight, zero-dependency feel)

## Integrations

- **Polaris (SirsiDynix/Innovative):** Deep integration with the Polaris REST API (PAPI) for:
  - Patron authentication and profile lookup
  - Bibliographic record search
  - Automated hold placement
  - Fulfillment tracking (checking items out)
- **SMTP:** Integration with standard mail servers for:
  - Patron submission confirmations
  - Automated rejection/status notifications
  - Weekly staff action summaries

## Testing

- **Framework:** Custom Node.js test runner
- **Assertion Library:** Native Node.js `assert` module
- **Scope:** Unit tests for library logic (`pb_hooks/lib/`), specifically for Polaris PAPI signing, XML building, and record processing rules.

## Development & Deployment

- **Version Control:** Git
- **Deployment:** Single binary (the `pocketbase` executable) containing the database, server, hooks, and static files.
- **Dependency Management:** Zero external package managers (no `package.json`). Dependencies are either built into PocketBase or loaded via CDN.
