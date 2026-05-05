# Features

## System Architecture
* PocketBase backend (SQLite + Go VM) with custom JavaScript hooks
* Frontend Vanilla JavaScript (ES6+), HTML5, CSS3
* Zero external dependencies in production (no npm install required)

## Authentication & Authorization
* Real-time Patron Authentication via Polaris API (PAPI)
* Staff Authentication via Polaris API
* Role-Based Access Control (RBAC): `super_admin`, `admin`, `staff`
* Consortia Staff Identity scoping by domain, username, and parent library
* Local Emergency Bypass for staff authentication

## Patron Experience
* Patron submission portal (`/patron/`)
* Submission form with weekly limit checking
* Real-time duplicate suggestion checking
* Library-specific branding (logo, title, login prompts) based on `libraryOrgId`
* Professional email notifications (Submitted, Rejected, Hold Placed, etc.)

## Staff Experience
* Staff dashboard portal (`/staff/`)
* Review "Suggestions" with one-click actions:
  * Purchase
  * Already Own (auto-places hold)
  * Reject (sends email)
  * Silent Close (closes without email)
* Catalog Monitoring ("Outstanding Purchase" tab)
* Manual BIB Assist (assigning a BIB ID manually)
* Consortia Support & Branch Overrides
  * Cascading configuration overrides (Library vs System default)
  * Custom Sender Identities (From Email, From Name)
* Profile menu with option to opt-in to weekly action summary emails

## Polaris Integration
* Secure API communication via HMCA-SHA1 signatures
* Real-time BIB/Hold lookups
* Automated hold placement
* Fulfillment tracking

## Automated Workflows (Cron Jobs)
Background jobs are executed automatically based on cron schedules.

* **Outstanding Purchase Promoter** (`asap-hold-check` via `processOutstandingPurchases`): Periodically searches Polaris for items in "Outstanding Purchase". If an ISBN match is found, promotes to "Pending Hold" and assigns BIB ID.
* **Pending Holds Processor** (`asap-hold-check` via `processPendingHolds`): Automatically places holds in Polaris for items in "Pending Hold", then moves to "Hold Placed".
* **Fulfillment Tracker** (`asap-hold-check` via `processCheckedOut`): Monitors "Hold Placed" items and closes them when checked out.
* **Suggestion Timeout Auto-Reject** (`asap-hold-check` via `processOutstandingTimeout`): Background job to reject old, unprocessed suggestions.
* **Hold Pickup Timeout** (`asap-hold-check` via `processHoldPickupTimeout`): Auto-closes stale "Hold Placed" requests when hold is never checked out.
* **Pending Hold Timeout** (`asap-hold-check` via `processPendingHoldTimeout`): Auto-closes requests that remain in "Pending Hold" too long.
* **ISBN Checks** (`asap-isbn-check` via `processPendingSuggestionIsbnChecks` / `processPendingIsbnChecks`): Runs background ISBN checks and tags suggestions with "found" or "not found".
* **Organization Sync** (`asap-organization-sync` via `runScheduledOrganizationSync`): Synchronizes the Polaris organization hierarchy once a day.
* **Weekly Staff Action Summary** (`asap-weekly-staff-action-summary` via `runWeeklyStaffActionSummary`): Sends weekly emails summarizing new suggestions and approved purchases missing BIB IDs to opted-in staff.

## Setup & Configuration
* Initial Setup Wizard
* UI text, workflow, and email template customization
