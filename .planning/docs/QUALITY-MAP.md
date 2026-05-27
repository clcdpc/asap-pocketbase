<!-- generated-by: gsd-doc-writer -->
# Quality Map

This document summarizes quality standards, current debt, and verification practices for ASAP.

## Coding Standards

### Backend (PocketBase Hooks + Root `lib/`)

- **Runtime target:** JavaScript compatible with PocketBase hooks and shared with Node test execution.
- **Architecture:** `pb_hooks/main.pb.js` delegates to root `lib/` modules; route registration is centralized in `lib/route_registry.js`.
- **Module boundaries:** facade modules (`*_routes.js`) expose installers; feature code lives in domain folders (`lib/staff`, `lib/config`, `lib/jobs`, `lib/polaris`, `lib/records`).
- **Naming:** `camelCase` for functions/variables, `UPPER_SNAKE_CASE` for constants, `snake_case` for collection fields.
- **DOM safety:** new frontend rendering should prefer DOM APIs over dynamic `innerHTML` with runtime data.

### Frontend (Staff + Patron SPAs)

- **Module system:** native ES modules for browser code.
- **State and scope correctness:** selected system/library context must be a single source of truth across load, form-populate, save, and runtime read paths.
- **UI reliability:** route/view switches should preserve valid scoped values and avoid stale cross-library leakage.

## Current Technical Debt

1. Some legacy docs and comments still refer to older `pb_hooks/lib` layouts.
2. Manual low-level helpers (crypto/signing, XML plumbing, normalization guards) require disciplined test coverage.
3. Scope-heavy settings flows require ongoing regression coverage for system vs library behavior.

## Quality Metrics and Verification

### Automated Tests

- Extensive Node-based test suite in `tests/` covering config scoping, route registration, staff workflows, Polaris integration behavior, and UI module behavior.
- Benchmark scripts validate performance-sensitive paths (workflow tagging, relinking, publication option resolution, etc.).

### Reliability and Security Focus

- Input normalization for IDs, formats, and options before persistence/use.
- Scoped authorization checks in route and data layers (including analytics scope behavior).
- Redaction and safe rendering patterns to prevent sensitive-data leakage and injection issues.

### Execution Standard

- Use the repository test runner (`node tests/run_all.js`) as the default verification command.
- Run targeted tests for touched areas when making scoped behavior changes.
