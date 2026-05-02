<!-- generated-by: gsd-doc-writer -->
# Project Context: Auto Suggest a Purchase (ASAP)

## Project Intelligence (High-Level Purpose)
Auto Suggest a Purchase (ASAP) is an automated material suggestion management system specifically designed for public libraries using the Polaris Integrated Library System (ILS) and PocketBase. The system's primary goal is to automate the labor-intensive lifecycle of patron material suggestions—from the initial request through catalog monitoring and hold placement, to final checkout and fulfillment tracking. 

ASAP follows a "Zero Dependencies" philosophy, utilizing a lightweight, portable architecture (PocketBase + Vanilla JavaScript) that requires no complex frontend build steps or Node.js runtime in production.

## User Types
The system employs Role-Based Access Control (RBAC) to support different organizational needs, particularly in library consortia:

*   **Patrons:** Authenticate via their library barcode and PIN. They can submit new suggestions, view the status of their requests, and receive automated email notifications throughout the process.
*   **Staff (scoped to Library):** The primary users of the management dashboard. They review suggestions, approve purchases, manually resolve catalog matches, and track fulfillment for their specific branch or library system.
*   **Library Admin (scoped to Library):** Manage branch-specific overrides for branding (logos), patron experience text, email templates, and workflow timeout thresholds without affecting the global system or other libraries.
*   **Super Admin (System-wide):** Responsible for global infrastructure settings, including Polaris PAPI credentials, SMTP configuration for email delivery, staff role management, and system-wide default settings.

## Core Workflows

### 1. Suggestion & Review
*   **Submission:** Patrons submit requests through a simplified portal. The system validates their account status in Polaris and enforces configurable submission limits (e.g., 5 per week).
*   **Triage:** Staff review requests in the "Suggestions" tab. They can mark items for purchase, reject them (with notification), or manually place holds if the item is already owned.

### 2. Automated Catalog Monitoring (Auto-Promoter)
*   For items marked as **Outstanding Purchase**, the system runs a periodic background job (Cron) that searches the Polaris catalog using identifiers (ISBN/UPC). 
*   When a matching BIB record is found, the system automatically assigns the BIB ID and promotes the request to the next stage.

### 3. Fulfillment Automation
*   **Auto-Hold Placement:** Once a BIB ID is associated with a request, the system automatically attempts to place a hold for the patron in Polaris.
*   **Fulfillment Tracking:** The system monitors the patron's active checkouts. When the requested item is checked out to the patron, the ASAP record is automatically closed as fulfilled.

### 4. System Maintenance & Cleanup
*   Automated "Timeout" jobs handle stale requests:
    *   **Outstanding Timeout:** Rejects old suggestions that haven't been processed.
    *   **Hold Pickup Timeout:** Closes requests where a hold was placed but never checked out.
    *   **Pending Hold Timeout:** Cleans up requests stuck in the hold placement queue.

## Current Roadmap Status
The project is currently in a mature, feature-rich state supporting complex consortia environments. Recent development has focused on:

*   **Consortia Scalability:** Robust cascading overrides for library-specific branding and communications.
*   **Staff Reporting:** Implementation of weekly action summaries to keep collection development teams informed of pending tasks.
*   **Security & Audit:** Enhanced security tightening, staff last-login tracking, and audit trails for deleted requests.
*   **Workflow Optimization:** Improved duplicate detection (real-time hold checks) and background ISBN/identifier verification to reduce manual staff effort.
*   **Stability:** Continued alignment with the latest PocketBase versions (currently tested with 0.36.x).
