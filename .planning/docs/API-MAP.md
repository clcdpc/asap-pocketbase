<!-- generated-by: gsd-doc-writer -->
# API Map

This document maps the internal and external API endpoints used by Auto Suggest a Purchase (ASAP).

## Internal API (PocketBase Hooks)

The internal API is served by PocketBase custom routes defined in `pb_hooks/main.pb.js`. All routes under `/api/asap/staff/` and `/api/asap/patron/suggestions` require a valid PocketBase auth token in the `Authorization` header.

### Public & Setup Endpoints
| Method | Path | Description | Auth Required |
|--------|------|-------------|---------------|
| GET | `/api/asap/setup/status` | Returns whether initial setup is required. | No |
| POST | `/api/asap/setup` | Performs initial system setup and creates first admin. | No (only if no staff exist) |
| POST | `/api/asap/setup/test-polaris` | Tests Polaris connection during setup. | No |
| GET | `/api/asap/config` | Returns UI text and basic workflow configuration for the frontend. | No |

### Patron Endpoints
| Method | Path | Description | Auth Required |
|--------|------|-------------|---------------|
| POST | `/api/asap/patron/login` | Authenticates a patron against Polaris and ASAP. | No |
| POST | `/api/asap/patron/suggestions` | Submits a new suggestion for the authenticated patron. | Yes (Patron) |

### Staff Endpoints
| Method | Path | Description | Auth Required |
|--------|------|-------------|---------------|
| POST | `/api/asap/staff/login` | Authenticates staff against Polaris and ASAP. | No |
| GET | `/api/asap/staff/title-requests` | Lists suggestions filtered by library scope. | Yes (Staff) |
| POST | `/api/asap/staff/title-requests/{id}/action` | Updates suggestion status or triggers actions (Purchase, Reject, etc.). | Yes (Staff) |
| POST | `/api/asap/staff/suggestions` | Creates a suggestion on behalf of a patron. | Yes (Staff) |
| POST | `/api/asap/staff/patron-lookup` | Looks up patron info from Polaris by barcode. | Yes (Staff) |
| POST | `/api/asap/staff/bib-lookup` | Looks up bibliographic info from Polaris by BIB ID. | Yes (Staff) |
| GET | `/api/asap/staff/settings/library` | Retrieves library-specific settings. | Yes (Admin) |
| POST | `/api/asap/staff/settings/library` | Updates library-specific settings. | Yes (Admin) |
| GET | `/api/asap/staff/email-status` | Checks if SMTP and templates are ready for an org. | Yes (Staff) |
| GET | `/api/asap/staff/users` | Lists staff users (scoped to library or system). | Yes (Admin) |
| POST | `/api/asap/staff/users` | Creates a new staff user. | Yes (Admin) |
| POST | `/api/asap/staff/users/{id}/role` | Updates a staff user's role. | Yes (Admin) |
| DELETE | `/api/asap/staff/users/{id}` | Deletes a staff user. | Yes (Admin) |
| POST | `/api/asap/staff/profile` | Updates current staff user's email and summary settings. | Yes (Staff) |
| POST | `/api/asap/staff/organizations/sync` | Manually triggers a Polaris organization sync. | Yes (Super Admin) |
| POST | `/api/asap/staff/test-polaris` | Tests the current Polaris configuration. | Yes (Super Admin) |
| POST | `/api/asap/staff/test-smtp` | Sends a test email to verify SMTP settings. | Yes (Super Admin) |

### Background & Job Endpoints
| Method | Path | Description | Auth Required |
|--------|------|-------------|---------------|
| POST | `/api/asap/jobs/hold-check` | Manually triggers the hold status check background job. | Yes (Super Admin) |
| POST | `/api/asap/jobs/promoter-check` | Manually triggers the suggestion promoter job. | Yes (Super Admin) |
| POST | `/api/asap/jobs/weekly-staff-action-summary` | Triggers the weekly summary email job. | Yes (Secret/Super Admin) |

## PocketBase Collection APIs

The frontend interacts with several PocketBase collections directly via the SDK for data that doesn't require custom logic:

- `staff_users`: Staff account details and preferences.
- `patron_users`: Cached patron information and auth records.
- `title_requests`: The primary record for every suggestion.
- `polaris_organizations`: Hierarchical list of libraries and branches.
- `workflow_settings`: Behavioral configuration (limits, timeouts).
- `ui_settings`: Display configuration (text, labels, logo).
- `email_templates` / `rejection_templates`: Content for automated notifications.

## External API (Polaris PAPI)

ASAP integrates with the Polaris Web Services (PWS) REST API for authentication and catalog interactions.

### Base Configuration
- **Host**: Configured via system settings (e.g., `https://polaris.example.org`) <!-- VERIFY: {host} -->
- **Authentication**: PWS HMAC-SHA1 signature using Access ID and API Key.
- **Paths**: `/PAPIService/REST/[type]/v1/[lang]/[app]/[org]/`

### Common Endpoints
| Endpoint | Method | Use Case |
|----------|--------|----------|
| `protected/.../authenticator/staff` | POST | Staff login and API session generation. |
| `public/.../authenticator/patron` | POST | Patron login verification. |
| `public/.../patron/[barcode]/basicdata` | GET | Fetching patron name, email, and library scope. |
| `public/.../patron/[barcode]/itemsout/all`| GET | Checking if a patron already has a title checked out. |
| `public/.../search/bibs/keyword/KW` | GET | Searching for ISBN/UPC matches in the catalog. |
| `public/.../bib/[bibId]` | GET | Fetching specific title/author info for a BIB ID. |
| `public/.../holdrequest` | POST | Placing a hold on a bibliographic record. |
| `public/.../holdrequest/[GUID]` | PUT | Confirming/replying to a hold request. |
| `public/.../organizations/[kind]` | GET | Syncing the library organization hierarchy. |
