# Saved test-email operation and acceptance

Settings > Email / Postmark and Operations share one authorized operational-test
outbox, dispatcher and worker. A test uses the saved scope and independently
inherited From address/name, the signed-in administrator's primary notification
email, and the single protected System Postmark credential. Draft values,
authentication addresses and weekly-summary overrides are not substitutes.

The live action is disabled while settings are dirty, loading, saving or
uncertain, and in file-capture or Postmark Sandbox mode. Save or discard changes,
then confirm the displayed scope, From, primary recipient and Live Postmark mode.
A readiness check sends no email and does not establish provider acceptance.

Each explicit click has one durable actor/scope request ID. Replayed requests
recover that intent before charging another cooldown allowance. New tests are
limited to one per administrator per minute and five per scope per minute.
Status observation uses the exact scoped reference, including after a lost
acknowledgement or beyond the Operations list limit; it never resubmits a test.
Observation stops when the panel/session/scope changes or after one minute.
Refresh test status or View email operations to continue checking the same test.
Stopping observation does not cancel a committed outbox entry.

## Recovering an unrecorded client request

If a submission never reaches the server, its original request UUID remains in
this browser session and Send stays disabled. Reloading or opening the other
panel reconciles that same UUID; it does not resend it.

1. Keep the same signed-in actor and scope. Use Refresh test status. Only the
   authorized by-request lookup returning `404` with `not_found` counts as
   absence evidence. A network/authentication/server error or malformed response
   cannot authorize retirement.
2. After the first absence result, wait at least one minute, then refresh again.
   Two successful absence lookups at least a minute apart expose **Retire
   unrecorded request**. Returning to the panel or recreating it requires fresh
   reconciliation evidence. A failed check restarts that evidence window.
3. Read the warning and explicitly confirm retirement. The controller performs
   one fresh status lookup before retiring the client intent. If a record has
   appeared, it follows that record's pending/sending/accepted/suppressed/failed
   result instead. Known durable records and ambiguous provider outcomes cannot
   be retired through this action. A late acknowledgement learned by the other
   panel also prevents a stale retirement or new-send authorization.
4. Expand **Retirement review references** and retain the original UUID, scope,
   actor and UTC review time for the operator's correlation records. References
   remain scoped to that actor and library in session storage, including after
   a subsequent test; they last only for this browser session. If storage cannot
   retain the reference, retirement fails closed.
5. Retirement requests no email. A separate intentional **Send test email** click
   creates a new UUID against the current saved context. Normal server
   authorization, configuration fences, deduplication and cooldown still apply.

Repeated absence and the final lookup establish only that no record was visible
at those checks. They cannot prove an older in-flight request will never commit.
Client retirement does not cancel such a request: a message may still arrive,
including after another explicit test. If that residual risk is unacceptable,
keep the intent unresolved and have an authorized operator correlate the saved
UUID with Email operations/server request records before requesting anything
else. Retain the reference outside the tab before ending the browser session.
Never use outbox Retry, resubmit the original POST, or clear session storage as
the recovery procedure. Existing accepted or ambiguous-provider records retain
their no-replay safeguards.

Queued and submitting states do not establish acceptance. A recorded live
Postmark acceptance means the service accepted the message; inspect the mailbox
and junk/quarantine separately. File capture means no mailbox delivery. Unknown
transport outcomes may follow acceptance and remain guarded against replay.
Errors show safe categories and references, without tokens, bodies, provider
payloads or local capture paths. Historical results with no actual mode evidence
remain unverified; current configuration cannot retrospectively certify them.

## Configuration and backlog review

An operator must explicitly configure `EmailSafety.DeliveryMode` as `postmark`,
save an approved System credential and sender identity, permit the controlled
notification mailbox under the environment's allowed-recipient policy, and
verify the normal Hangfire `asap-email` worker and outbox sweep are running.
The Postmark server must report Live mode. `POSTMARK_API_TEST`, Sandbox, file
capture and recording transports do not meet live acceptance.

Before changing a capture deployment to live mode, review pending operational
tests and the business-email backlog with the deployment owner. Historical
captured messages and suppressed/unknown/failed rows must not be bulk replayed
or relabeled as delivered. New live test intents are fenced to their saved
configuration and intended mode at worker dispatch and at the production adapter.
No upgrade or page load enables live mode, changes credentials, resurrects old
messages, or requires staff to invoke a script/API/Hangfire job for each test.

## Controlled human acceptance — pending

No live provider request or mailbox receipt is established by deterministic
tests. Issue #293 remains open until an authorized human records these results:

1. Confirm an approved controlled notification mailbox, explicit live mode,
   approved sender and System token, saved settings and running email worker.
   Record environment and scope without recording credentials.
2. Open Settings > Email / Postmark. Confirm the saved scope, From, primary
   recipient and live mode. Click Send test email once.
3. Observe automatic queue/worker progress and provider acceptance. Record the
   operation reference and safe acceptance evidence. Do not manually trigger a job.
4. Observe receipt in the intended mailbox or junk/quarantine. Confirm the From
   identity, scope, requested UTC time and matching operation reference. Record
   pass/fail and observation time; provider acceptance alone does not prove receipt.
5. Repeat for System scope, a library inheriting the System sender, and a
   controlled library sender override. All use the one System credential.
6. Record missing prerequisites or failures precisely. Leave the issue open
   until provider acceptance and actual mailbox receipt are documented.
