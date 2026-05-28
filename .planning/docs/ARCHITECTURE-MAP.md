<!-- generated-by: gsd-doc-writer -->
# Architecture Map: Auto Suggest a Purchase (ASAP)

This document outlines internal structure, component relationships, and request/data flow for ASAP.

## System Overview

ASAP is a PocketBase-based application for patron purchase suggestions, staff review workflows, and Polaris-integrated hold automation.

## Component Relationships

### Backend (`pb_hooks/` + root `lib/`)

- **`pb_hooks/main.pb.js`**: PocketBase hook entrypoint.
- **`lib/route_registry.js`**: Central registry that wires patron/staff/setup/job route installers.
- **Route facades**:
  - `lib/staff_routes.js` (staff facade)
  - `lib/patron_routes.js`
  - `lib/setup_routes.js`
  - `lib/job_routes.js`

#### Staff route facade composition (`lib/staff/*`)
- `auth_routes.js`
- `users_routes.js`
- `lookup_routes.js`
- `title_request_*` modules
- `settings_routes.js` + `settings_*` helpers
- `analytics_routes.js`
- `admin_routes.js`

#### Domain libraries
- **Config**: `lib/config/*`
- **Jobs**: `lib/jobs/*`
- **Polaris integration**: `lib/polaris/*` plus `lib/polaris.js`
- **Records/data access**: `lib/records/*` plus `lib/records.js`
- **Cross-cutting utilities**: `route_utils.js`, `http_utils.js`, `authz.js`, `html_utils.js`, `identity.js`, `orgs.js`, `mail.js`

### Frontend (`pb_public/`)

- **Patron SPA (`pb_public/patron/`)**: barcode/PIN auth and suggestion submission.
- **Staff SPA (`pb_public/staff/`)**: moderation, settings, analytics, and operational tooling.

## Data Flow: Suggestion to Hold

1. **Submission**: create `title_requests` record with `suggestion` status.
2. **Staff decision**: reject, close, manual hold/already-own, or move to purchase lifecycle.
3. **Automation jobs**: search Polaris for acquired items, place holds, track fulfillment, and run timeout closures.

## Directory Structure (Current)

```text
pb_hooks/
└── main.pb.js                # PocketBase hook entrypoint

lib/
├── route_registry.js         # Route wiring source of truth
├── staff_routes.js           # Staff facade
├── patron_routes.js          # Patron facade
├── setup_routes.js           # Setup facade
├── job_routes.js             # Job facade
├── staff/                    # Staff route implementations
├── config/                   # Scoped settings/config logic
├── jobs/                     # Scheduled and manual workflows
├── polaris/                  # Polaris API helpers
└── records/                  # Data access helpers

pb_public/
├── patron/
└── staff/

tests/
└── *.test.js + benchmark_*.js + run_all.js
```

## Notes

- Keep architecture docs aligned with root-level `lib/` ownership; avoid reintroducing `pb_hooks/lib` references.
- Route registry and facade boundaries are the intended extension points for new APIs.
