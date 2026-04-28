## 2024-05-18 - Prevent Privilege Escalation in Role Updates
**Vulnerability:** A standard `admin` user could downgrade a `super_admin`'s role because the `staffUserRoleUpdate` endpoint did not restrict modifying a `super_admin` record strictly to other `super_admin` users (except for checking that at least one super admin remains).
**Learning:** Role-based access control (RBAC) must check both the user *performing* the action and the *target* user of the action. Even if an `admin` is allowed to change roles generally, modifying elevated roles requires elevated authorization.
**Prevention:** Implement defense-in-depth authorization checks that validate both the actor's privileges and the target object's sensitivity tier before executing state-changing operations.
