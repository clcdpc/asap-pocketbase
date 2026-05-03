<!-- generated-by: gsd-doc-writer -->
# Quality Map

This document outlines the coding standards, technical debt, and quality metrics for the Suggest-a-Purchase project.

## Coding Standards

### Backend (PocketBase Hooks)
The backend logic is implemented in JavaScript (running in the PocketBase `goja` environment).

- **Module System**: Uses CommonJS-style `require` and `module.exports`.
- **JS Version**: Follows ES5 standards for compatibility with the embedded JS engine.
- **Naming Conventions**:
  - Constants: `UPPER_SNAKE_CASE`
  - Functions and Variables: `camelCase`
  - Collections and Fields: `snake_case` (matching PocketBase schema)
- **Data Normalization**: Centralized in `pb_hooks/lib/records.js` to ensure consistent data types across the system.
- **Error Handling**: Uses `try...catch` blocks with custom error codes (e.g., 409 for duplicates, 406 for limits).
- **Audit Logging**: All destructive or significant state changes are audited via `auditDeletedRequest` and `recordEvent`.

### Frontend (Staff UI)
The staff interface is a single-page application built with modern JavaScript.

- **Module System**: Uses ES6 `import/export`.
- **JS Version**: Uses ES6+ features including `async/await`, template literals, and arrow functions.
- **Modularity**: Logic is split into specialized modules (e.g., `api.js`, `grid.js`, `modals.js`) using standard ES6 imports/exports.
- **State Management**: Leverages PocketBase's `authStore` for authentication and user context.

## Technical Debt

### Identified Debt
- **Manual Implementations**: Due to the limited environment of PocketBase hooks, several low-level utilities (HMAC-SHA1, UTF-8 byte counting, XML escaping) are manually implemented in `pb_hooks/lib/crypto.js` and `pb_hooks/lib/polaris.js`.
- **Sync Logic**: Organization synchronization and ISBN checking rely on multiple cron jobs and manual triggers, which may lead to race conditions if not carefully monitored.

### Refactoring Priorities
1. **Centralized Testing**: Implement a unified test runner or CI integration for the Node.js-based unit tests.

## Quality Metrics

### Testing Coverage
The project maintains an extensive suite of tests located in the `tests/` directory:
- **Unit Tests**: Coverage for core logic including crypto, identity, mail, and data normalization.
- **Integration Tests**: Tests for PocketBase record operations and Polaris API interactions (mocked).
- **Performance Benchmarks**: Dedicated scripts for benchmarking critical paths such as audience group lookups, organization relinking, and duplicate suggestion detection.

### Reliability and Security
- **Data Redaction**: Sensitive patron data is automatically redacted from logs and internal notes using `redactPayload` in `polaris.js` and record hooks in `main.pb.js`.
- **Input Validation**: Strict normalization of identifiers (ISBN/ISSN) and barcodes before processing.
- **API Resilience**: Detailed error handling for the Polaris API, including retry-safe signature generation and status code mapping.
- **XSS Prevention**: Centralized XML and HTML escaping utilities to prevent injection attacks.
- **Auditing**: Comprehensive audit trail for deleted requests, capturing snapshots of the data before removal.
