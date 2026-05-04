## 2024-05-18 - Prevent Privilege Escalation in Role Updates
**Vulnerability:** A standard `admin` user could downgrade a `super_admin`'s role because the `staffUserRoleUpdate` endpoint did not restrict modifying a `super_admin` record strictly to other `super_admin` users (except for checking that at least one super admin remains).
**Learning:** Role-based access control (RBAC) must check both the user *performing* the action and the *target* user of the action. Even if an `admin` is allowed to change roles generally, modifying elevated roles requires elevated authorization.
**Prevention:** Implement defense-in-depth authorization checks that validate both the actor's privileges and the target object's sensitivity tier before executing state-changing operations.
## 2024-05-18 - Prevent Timing Attacks in Password Overrides
**Vulnerability:** The `staffLogin` endpoint used a strict equality operator (`===`) to compare user input against the configured `overridePassword`. This allowed for timing attacks where an attacker could theoretically guess the override password character by character by measuring microscopic differences in response times.
**Learning:** In PocketBase hooks running on the Goja JS engine, standard string comparisons (`==` or `===`) are vulnerable to timing attacks when verifying secrets (passwords, tokens, API keys).
**Prevention:** Always use the built-in `$security.equal(a, b)` function provided by PocketBase for comparing security-sensitive strings, as it guarantees constant-time comparison.
## 2024-05-18 - Prevent XSS in HTML rendering of dynamically sourced messages
**Vulnerability:** Several places in the patron web application (`pb_public/patron/app.js`) utilized `.innerHTML` to insert text that included dynamically sourced inputs like library setting names, messages from configs, and explicitly, API error responses (e.g., `conflictBody.innerHTML = err.message || ...`). This exposes the application to Reflected and Stored Cross-Site Scripting (XSS).
**Learning:** Even internal configuration texts or error messages from APIs shouldn't be blindly trusted as safe HTML, particularly when using native DOM methods like `.innerHTML`.
**Prevention:** Default to using `.textContent` instead of `.innerHTML` for DOM text replacement whenever HTML rendering is not strictly required. For areas that still need `.innerHTML` or similar behavior with dynamic input (like the 409 conflict error rendering), explicitly escape the content using `escapeHtml()` before insertion.
## 2026-05-01 - Prevent Information Disclosure in Login Errors
**Vulnerability:** The `patronLogin` and `staffLogin` routes bubbled up raw error strings from the Polaris API and internal configuration checks to the end-user. This could leak internal system details, IP addresses, or Polaris error specifics.
**Learning:** Error messages returned to users should be generic to prevent information disclosure. Detailed error information should be logged on the server for staff troubleshooting.
**Prevention:** Sanitize error responses by providing user-friendly, non-descriptive messages while ensuring the full error context is captured in the system logs.
## 2024-05-18 - Prevent Information Disclosure in Authentication APIs
**Vulnerability:** The `staffLogin` route could leak sensitive internal stack traces or configuration errors to end users if the underlying `polaris.staffAuth` external API call threw an exception.
**Learning:** Even internally trusted helper functions or APIs can fail unpredictably. If those exceptions are not caught locally, they bubble up to the HTTP response, potentially exposing system topology or API secrets.
**Prevention:** Always wrap external API calls and authentication routines in `try...catch` blocks within route handlers. Log the full error securely on the server (`e.app.logger().error`) and throw a generic error (e.g., `UnauthorizedError`) to the client.
