<!-- generated-by: gsd-doc-writer -->
# ASAP Test Map

This document maps test coverage and execution commands for ASAP.

## Overview

ASAP keeps production runtime dependency-light while maintaining a broad Node-based development test suite.

### Testing Strategy
- **Isolation:** test files mock PocketBase host APIs and Polaris integration boundaries.
- **Built-in assertions:** tests use Node-native assertions and lightweight helpers.
- **No frontend bundler dependency:** UI behavior is tested through module-level and DOM-behavior tests.

## Test Execution

### Prerequisites
- Node.js

### Recommended command (full suite)

```bash
node tests/run_all.js
```

### Targeted execution examples

```bash
# Single test
node tests/config_ui_text_patron_options_scope.test.js

# Multiple focused tests
node tests/settings_staff_scope_banner.test.js
node tests/staff_analytics.test.js
```

## Core Test Categories

### 1. Backend Logic and Routing (`*.test.js`)

Covers route registration, authorization, settings scope behavior, job behavior, record helpers, Polaris integration helpers, and security-related logic.

### 2. UI/Module Behavior (`*.test.js`)

Covers staff/patron module behavior, scope-banner behavior, settings form behavior, and regression cases around context switching.

### 3. Benchmarks (`benchmark_*.js` and `*_benchmark.js`)

Tracks performance-sensitive flows such as relinking, publication option handling, workflow tagging, and checkout or duplicate-related paths.

## Runner Notes

- `tests/run_all.js` is the canonical suite runner and should be kept in sync with newly added test files.
- Individual test execution remains supported for focused debugging.
- When adding tests for scoped settings or analytics, ensure cases include system context, library context, and fallback behavior.
