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
## 2026-05-02 - Prevent Privilege Escalation in Role Deletions
**Vulnerability:** Similar to role updates, a standard `admin` user could delete a `super_admin`'s account because the `staffUserDelete` endpoint only verified that at least one super admin remains, rather than strictly requiring `super_admin` privileges to delete a `super_admin` account.
**Learning:** Role-based access control (RBAC) must check both the user *performing* the action and the *target* user of the action. Modifying or deleting elevated roles requires elevated authorization.
**Prevention:** Ensure all endpoints that perform state-changing operations on user accounts strictly validate the actor's privileges against the target object's sensitivity tier.

## 2026-05-18 - Prevent Information Disclosure in Login Errors
**Vulnerability:** The `staffLogin` route in `lib/staff/auth_routes.js` lacked a top-level try...catch block. If `polaris.staffAuth` or another service threw an unexpected error, it bubbled up to the PocketBase router which would return the raw error message to the client, leaking internal details.
**Learning:** Error messages returned to users should be generic to prevent information disclosure. Detailed error information should be logged on the server for staff troubleshooting.
**Prevention:** Wrap all complex authentication endpoints with a global try/catch to sanitize error responses by providing user-friendly, non-descriptive messages while ensuring the full error context is captured in the system logs.
