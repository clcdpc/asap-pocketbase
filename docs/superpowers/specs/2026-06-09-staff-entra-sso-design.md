# Staff Entra SSO Design

## Summary

Add Microsoft Entra single sign-on as an optional staff login method using PocketBase's native OAuth2 support on the existing `staff_users` auth collection. Staff access remains pre-provisioned only: OAuth can authenticate a person, but ASAP authorizes only existing active `staff_users` records.

The existing Polaris username/password login and emergency override path remain available as fallback login methods.

## Goals

- Let staff sign in with Microsoft when their real staff email has been added by an ASAP admin.
- Use PocketBase's native OAuth2 redirect, code exchange, external auth linking, and token generation.
- Prevent OAuth from auto-provisioning staff users.
- Keep role, library, active status, and scope authorization in `staff_users`.
- Make `staff_users.email` the real staff contact and SSO matching email.
- Let admins create and edit staff emails from Staff Access.
- Use staff email as the default staff notification recipient, with `weekly_action_summary_email` as an optional override.

## Non-Goals

- Entra tenant restriction in v1.
- Entra group-to-role or group-to-library mapping.
- Just-in-time staff provisioning.
- Removing Polaris username/password login.
- Moving OAuth client secrets into ordinary staff or library settings.
- Replacing PocketBase's OAuth implementation with custom Microsoft callback routes.

## Scope Model

Microsoft staff SSO is a system-level login capability.

- Default value: OAuth is unavailable until the `staff_users` Microsoft provider is configured in PocketBase.
- Library override source: none.
- Save path: v1 does not save OAuth provider credentials through ASAP settings. Super admins configure the provider in PocketBase admin.
- Runtime resolution: staff login checks PocketBase auth methods for `staff_users` and shows the Microsoft button only when a Microsoft provider is available.

Staff email is not a settings override. It is a global `staff_users` record field managed through Staff Access with existing staff authorization rules.

## Architecture

Enable Microsoft OAuth2 on the existing `staff_users` auth collection. The staff frontend adds a secondary "Sign in with Microsoft" button that calls PocketBase's native `authWithOAuth2` flow for `staff_users`.

Add an `onRecordAuthWithOAuth2Request` hook for the `staff_users` collection. The hook runs after provider token exchange and before PocketBase links or creates an external auth. It maps the Microsoft account to an existing active staff record by email, assigns that record to the auth event, updates SSO login timestamps, and allows PocketBase to issue the normal auth token.

If no active staff record matches, the hook rejects the request. It must not let PocketBase create a new `staff_users` record.

The existing `/api/asap/staff/login` Polaris credential route remains unchanged except where shared staff JSON or email behavior is reused.

## Data Model

Use `staff_users.email` as the real staff contact email and SSO matching key when it contains a real email address. Existing fake generated emails such as `domain.user@staff.asap.local` may remain on old records until an admin replaces them. Placeholder emails mean SSO and staff email notifications are disabled for that staff record.

Add `lastSsoLogin` to `staff_users` as a date field.

Keep existing fields:

- `username`
- `domain`
- `identityKey`
- `displayName`
- `role`
- `active`
- `libraryOrgId`
- `libraryOrgName`
- `scope`
- `lastLogin`
- `lastPolarisLogin`
- `weekly_action_summary_email`

`lastLogin` records any successful staff login. `lastPolarisLogin` records Polaris/override login. `lastSsoLogin` records Microsoft OAuth login.

## Staff Email Management

Staff Access adds email controls for admin-created staff records.

Admins can provide a real email when creating a staff user. If they leave email blank, the backend keeps or creates the generated PocketBase placeholder email and the staff user can still use the existing Polaris login path. Super admins can edit staff email for any staff record they can manage. Library admins can edit staff email only for staff records in their library and must continue to follow existing restrictions around super admin records and cross-library users.

Email validation rules:

- Trim and lowercase for matching.
- Require a syntactically valid email for any nonblank admin-entered staff email.
- Reject duplicate real emails across `staff_users`.
- Treat generated `@staff.asap.local` emails as placeholders, not notification recipients.

`staffPublicJson` includes safe email fields needed by the staff UI, including the real email, whether the email looks like a placeholder, and `lastSsoLogin`.

## SSO Matching

On Microsoft OAuth login:

1. Read the provider user email first.
2. If email is missing, read the provider username or UPN.
3. Normalize the candidate value by trimming and lowercasing.
4. Find an existing `staff_users` record whose `email` matches the normalized value.
5. Require `active = true`.
6. Set the auth event record to that staff record.
7. Update `lastLogin` and `lastSsoLogin`.
8. Allow PocketBase to link the external auth and return the normal auth response.

No role, library, scope, or display name values are copied from Microsoft into authorization fields. ASAP treats Entra as authentication only.

If no active record matches, reject the login with a generic access-denied response. Server logs may include normalized provider email/UPN and rejection reason for admins, but the browser must not reveal whether the email exists.

## Frontend Login Flow

The staff login screen keeps the current Polaris username/password form.

When PocketBase reports a Microsoft OAuth provider for `staff_users`, show a secondary "Sign in with Microsoft" button. The button starts the native PocketBase OAuth popup flow. On success, save the returned token and record to the existing `pb.authStore` and call the current `checkAuth()` path.

On failure, show a generic message:

> Your Microsoft account is not enabled for staff access. Ask an ASAP administrator to add your staff email.

If Microsoft OAuth is not configured, the button remains hidden for normal users. Admin-facing settings can show read-only status that SSO is not configured.

## Notification Email Resolution

`weekly_action_summary_email` becomes an optional override. Runtime recipient resolution uses:

1. `weekly_action_summary_email` when set;
2. otherwise the real `staff_users.email`;
3. otherwise no recipient.

Generated placeholder emails such as `@staff.asap.local` are never used for staff notifications.

The staff profile UI should show the effective staff email as the default summary recipient. If a staff member enters a separate summary email, save it to `weekly_action_summary_email`. If they clear the override, fall back to `staff_users.email`.

Purchase reminder and additional-copy reminder defaults continue to use the existing staff preference booleans, while reminder recipient lookup can share the same effective staff email helper where staff email is needed.

## Configuration

For v1, configure Microsoft OAuth in PocketBase admin on the `staff_users` collection. ASAP Settings may show read-only SSO status for super admins, but it does not store or edit OAuth client secrets.

Deployment documentation should tell admins to register the PocketBase redirect URL with Microsoft:

```text
https://<asap-host>/api/oauth2-redirect
```

For local development:

```text
http://127.0.0.1:8090/api/oauth2-redirect
```

Use the PocketBase provider name reported by `listAuthMethods()` rather than hard-coding a display-only label. The UI can treat provider names such as `microsoft` as Microsoft SSO.

## Security And Privacy

The trust boundary is proof of control of a Microsoft work account email or UPN that exactly matches a pre-created active staff email.

No Entra account may create its own ASAP staff user. No OAuth login may change staff authorization fields. Inactive staff records cannot log in through SSO even if their Microsoft authentication succeeds.

Login errors must avoid account enumeration. Detailed rejection reasons belong in server logs, not browser responses.

Provider tokens from Microsoft must not be exposed through staff JSON or stored outside PocketBase's native external auth handling.

## Route And Hook Constraints

Do not refactor `pb_hooks/main.pb.js` route registration as part of this work. Keep literal `routerAdd(...)` route declarations.

The OAuth guard should be added as an explicit hook registration, not as a dynamic route registry. Any hook behavior that depends on PocketBase runtime event semantics must be verified in the actual PocketBase runtime, not only Node tests.

## Testing

Automated coverage should include:

- Active staff record matches by `staff_users.email`.
- Missing email/UPN rejects login.
- No matching staff record rejects login.
- Inactive matching staff record rejects login.
- OAuth guard does not auto-create `staff_users`.
- OAuth success updates `lastLogin` and `lastSsoLogin`, not `lastPolarisLogin`.
- Admin create/update routes accept and validate real staff email.
- Duplicate real staff emails are rejected.
- Library admins cannot edit staff email outside their library.
- Staff public JSON includes real email and `lastSsoLogin` but no provider secrets.
- Weekly summary uses override email first, real staff email second, and ignores placeholder emails.
- Profile UI stores summary email only as an override.
- Microsoft login button visibility and error handling.

Manual PocketBase verification should include:

- Start PocketBase with project hooks and migrations.
- Confirm existing `GET /api/asap/setup/status` still works.
- Confirm existing `GET /api/asap/config` still works.
- Configure a Microsoft OAuth provider on `staff_users`.
- Log in with a Microsoft account whose email matches an active staff record.
- Confirm a non-provisioned Microsoft account is rejected without creating a staff record.
- Confirm an inactive matching staff record is rejected.
- Confirm Polaris login still works.

## Open Decisions

No open product decisions remain for v1.
