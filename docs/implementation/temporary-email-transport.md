# Email transport and real test email

The ASP.NET port keeps one durable `EmailOutbox` path for business mail and
administrator test mail. `EmailTransport.Mode` selects the implementation at
startup:

- `capture` uses the explicit local file sender. It is useful for development
  and automated tests, records a `capture` operation, and never claims that a
  mailbox received a message.
- `live` uses the Postmark REST API at `https://api.postmarkapp.com/email`.
  The sender resolves the protected Postmark server token and effective From
  settings from SQL for the selected system/library scope on every provider
  call. There is no silent fallback to capture when live sending fails.

The default checked-in development and test-host templates are `capture`.
Production-like deployments must set `EmailTransport.Mode` to `live` only
after the operator has supplied a protected system token, an allowed From
address, and a recipient-domain allowlist. The UI never returns token values;
blank token input preserves the saved token and the clear checkbox is explicit.

## Administrator test email flow

Settings > Email / SMTP loads server-reported readiness for the selected
scope. The action is disabled while settings are dirty, while the saved
configuration is unavailable, or while the transport is capture. A permitted
click sends a scoped request with an idempotency key. The server revalidates
the current admin and library scope, applies the one-minute per-admin/per-scope
cooldown, writes an `operational_test` outbox intent, commits it, and only then
enqueues the normal worker.

The worker snapshots the recipient, effective sender, message body, scope,
request reference, and selected delivery mode. It resolves the current
protected credential at send time, calls Postmark, and requires HTTP success,
`ErrorCode: 0`, and a non-empty `MessageID` before marking the row `sent`.
Provider rejection is a safe terminal failure; timeouts and unknown outcomes
remain fenced/ambiguous for the existing recovery sweep. Status polling is
scoped and exposes only the operation reference, delivery class/status,
timestamps, attempt count, safe error/suppression code, delivery mode, and
provider message reference.

Provider acceptance is not proof of inbox delivery. Human acceptance must
confirm the provider response and the expected mailbox for both system scope
and at least one library override before release. Do not use Postmark's test
mode or a real credential in automated tests.

The SQL DACPAC adds nullable operational-test metadata (`RequestedByStaffUserId`,
`RequestId`, and `DeliveryMode`) plus the deduplication/cooldown indexes. No
application-data SQL repair or raw-SQL migration is required.
