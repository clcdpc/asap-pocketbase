<!-- generated-by: gsd-doc-writer -->
# Architecture Map: Auto Suggest a Purchase (ASAP)

This document outlines the internal structure, component relationships, and data flow of the Auto Suggest a Purchase (ASAP) system.

## System Overview

ASAP is a web-based application built on [PocketBase](https://pocketbase.io) that allows library patrons to suggest titles for purchase. The system integrates directly with the Polaris ILS (Integrated Library System) to authenticate patrons and automate the hold placement process.

## Component Relationships

### Backend (`pb_hooks/`)
The backend logic is implemented as PocketBase JavaScript hooks.

*   **`main.pb.js`**: The entry point for the backend. It defines custom API routes, cron jobs, and database hooks.
*   **`lib/`**: A modular library system containing the core business logic.
    *   **`routes.js`**: Handles custom API request logic for both patrons and staff.
    *   **`polaris.js`**: Low-level integration with the Polaris PAPI (REST API). Handles HMAC signing, authentication, searching, and hold placement.
    *   **`records.js`**: Abstraction layer for PocketBase collection operations (`title_requests`, `staff_users`, `patron_users`).
    *   **`jobs.js`**: Background tasks for automated processing (hold checks, organization sync, weekly summaries).
    *   **`config.js`**: Manages system and library-specific settings stored in PocketBase.
    *   **`orgs.js`**: Manages the library organization hierarchy and maps Polaris branches to parent libraries.
    *   **`identity.js`**: Logic for parsing and validating patron and staff identities.
    *   **`mail.js`**: Handles email notification templates and delivery via SMTP.

### Frontend (`pb_public/`)
The frontend consists of two separate Single Page Applications (SPAs).

*   **Patron App (`pb_public/patron/`)**: A simplified interface for patrons to log in with their barcode/PIN and submit suggestions.
*   **Staff App (`pb_public/staff/`)**: A comprehensive dashboard for library staff to manage suggestions, configure settings, and view system status.
    *   **`js/api.js`**: Core frontend logic and API communication wrappers.
    *   **`js/grid.js`**: Handles the display and filtering of title requests.
    *   **`js/actions.js`**: Implements staff actions (Reject, Purchase, Already Own, etc.).

## Polaris Integration

The system communicates with the Polaris PAPI REST service using the following pattern:

1.  **Authentication**: Uses HMAC-SHA1 signatures on `Date` and `Authorization` headers for all requests.
2.  **Staff/Admin Context**: Most automated actions use a "system staff" account configured during initial setup.
3.  **Patron Authentication**: Patrons are authenticated against Polaris during login; the system retrieves their `PatronID`, `Barcode`, and library affiliation (`LibraryOrgID`).
4.  **Hold Placement**: Holds are placed using the `HoldRequestCreateData` XML payload sent to the Polaris `holdrequest` endpoint.

## Data Flow: Suggestion to Hold

The typical lifecycle of a request follows this path:

1.  **Submission**: A patron submits a suggestion. A record is created in the `title_requests` collection with a status of `suggestion`.
2.  **Verification**: If an ISBN is provided, a background job (`processPendingIsbnChecks`) searches Polaris. If found, the `bibid` is recorded and a "Multiple Polaris matches" tag is added if necessary.
3.  **Staff Review**: Staff reviews the suggestion in the Staff App and selects an action:
    *   **Reject**: Status becomes `closed` (reason: `rejected`).
    *   **Already Own**: Status becomes `closed` (reason: `manual`) after staff (or system) places a hold on the existing record.
    *   **Purchase**: Status becomes `outstanding_purchase`.
4.  **Auto-Promotion**: For items marked as "Purchase", the `processOutstandingPurchases` job periodically searches Polaris for the ISBN. Once a matching bibliographic record appears in the catalog, the `bibid` is saved and the status moves to `pending_hold`.
5.  **Hold Placement**: The `processPendingHolds` job detects requests in `pending_hold` status and attempts to place a hold in Polaris for the patron. On success, the status moves to `hold_placed`.
6.  **Fulfillment**: The `processCheckedOut` job monitors patron checkouts in Polaris. When the patron checks out the item matching the `bibid`, the request is automatically moved to `closed` (reason: `hold_completed`).

## Directory Structure

```text
pb_hooks/
├── main.pb.js        # Entry point & Route definitions
└── lib/              # Logic Modules
    ├── config.js     # Settings management
    ├── jobs.js       # Cron & background tasks
    ├── polaris.js    # Polaris ILS integration
    ├── records.js    # PocketBase CRUD wrappers
    └── routes.js     # API Route handlers

pb_public/
├── patron/           # Patron SPA
│   └── app.js
└── staff/            # Staff SPA
    ├── app.js
    └── js/
        ├── actions.js # Staff workflow actions
        ├── api.js     # Frontend API client
        └── grid.js    # Request management UI
```

## VERIFY: Infrastructure Claims

<!-- VERIFY: The exact Polaris PAPI base URL pattern used in production environments. -->
<!-- VERIFY: The default timeout values for various stages (Outstanding, Pending Hold, etc.) as configured in specific library environments. -->
