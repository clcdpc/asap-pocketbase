<!-- generated-by: gsd-doc-writer -->
# ASAP Test Map

This document maps the test suite for the Auto Suggest a Purchase (ASAP) project, detailing coverage areas, test types, and execution instructions.

## Overview

The ASAP project follows a "Zero Dependencies" philosophy for its production runtime. However, it includes a robust suite of tests for its backend logic (PocketBase JS Hooks) that are designed to be run in a **Node.js environment** during development.

### Testing Strategy
- **Isolation**: Each test file is a standalone Node.js script that mocks the PocketBase environment (`app`, `records`, `settings`) and the Polaris PAPI integration.
- **Built-in Assertions**: Tests use the native Node.js `assert` module.
- **Mocking**: The suite uses custom mocks for the Goja-based PocketBase host objects (e.g., `$app`, `$security`, `Record`) to ensure logic can be verified without a running PocketBase instance.

## Test Execution

### Prerequisites
- Node.js (any modern version)

### Running Tests
Tests are executed individually using the Node.js CLI. There is no central test runner (e.g., Jest or Vitest) to maintain the zero-dependency goal.

```bash
# Run a specific test
node tests/mail.test.js

# Run all logic tests (macOS/Linux)
for f in tests/*.test.js; do node $f; done
```

## Core Test Categories

### 1. Functional & Logic Tests (`*.test.js`)
These tests verify business logic, data transformation, and security sanitization.
- **Suggestion Lifecycle**: Duplicate detection, weekly limits, and status transitions.
- **Email System**: Template rendering, SMTP configuration secrets, and recipient resolution.
- **Security**: XML injection prevention, XSS escaping, and credential protection.
- **Configurations**: Scoped settings resolution for library consortia.

### 2. Performance & Benchmarks (`benchmark_*.js`)
These scripts measure the efficiency of critical algorithms and data processing paths.
- **Data Relinking**: Benchmarking optimized vs. unoptimized organization relinking.
- **N+1 Query Prevention**: Measuring performance improvements in job loops.
- **Polaris Lookups**: Simulating high-volume patron and BIB search operations.

## Test Inventory

| Test File | Component | Description |
|-----------|-----------|-------------|
| `mail.test.js` | `lib/mail.js` | Validates email template rendering and sending logic. |
| `records_duplicateContext.test.js` | `lib/records.js` | Verifies duplicate detection across different request states. |
| `records_enforceWeeklyLimit.test.js`| `lib/records.js` | Ensures patrons cannot exceed configured suggestion limits. |
| `identity.test.js` | `lib/identity.js` | Tests staff and patron identity parsing and normalization. |
| `polaris_checkouts.test.js` | `lib/polaris.js` | Tests fulfillment detection via Polaris items-out responses. |
| `xml_security.test.js` | `lib/polaris.js` | Ensures character escaping in SOAP/XML payloads sent to Polaris. |
| `format_rules.test.js` | `lib/format_rules.js`| Validates material format normalization and validation. |
| `crypto.test.js` | `lib/crypto.js` | Tests PAPI HMAC-SHA1 signature generation. |
| `weekly_summary.test.js` | `lib/jobs.js` | Validates the aggregation logic for staff weekly action summaries. |
| `orgs.test.js` | `lib/orgs.js` | Tests library hierarchy resolution and parent organization mapping. |
| `xss_escaping.test.js` | `lib/routes.js` | Verifies HTML escaping for patron-supplied content. |
| `config_scopedRows.test.js` | `lib/config.js` | Tests resolution of system vs. library-specific settings. |

## Mocking Environment

The tests rely on a global mocking pattern to simulate the PocketBase environment. Key mocks include:

- **`global.__hooks`**: Points to the local `pb_hooks` directory to allow `require` to work in Node.js.
- **`mockApp`**: Simulates the PocketBase `$app` object, providing methods like `findRecordById`, `save`, and `newMailClient`.
- **`MockRecord`**: A helper class that implements the PocketBase `get()` and `set()` interface.
- **`polaris.adminStaffAuth`**: Intercepted to return mock tokens, preventing actual network calls to the Polaris ILS during testing.

## Performance Benchmarks

Benchmarks provide timing data and comparison metrics:
- **`performance_benchmark.js`**: Compares unoptimized N+1 query patterns against the current cached implementation.
- **`benchmark_workflowTagsForRequest_optimized.js`**: Measures the efficiency of bulk tag assignments during automation cycles.
