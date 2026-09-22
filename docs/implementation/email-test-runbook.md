# Live email test runbook

1. Confirm `EmailTransport.Mode` is `live`, restart the web application, and
   verify the deployment can reach `https://api.postmarkapp.com`.
2. At system level, save the approved From address/name and the protected
   Postmark server token. Confirm `EmailSafety.AllowedRecipientDomains`
   contains the administrator's mailbox domain exactly.
3. Open Settings > Email / SMTP, confirm the read-only transport card reports
   `Live Postmark` and `Ready`, and confirm the displayed recipient is the
   signed-in administrator's primary notification email.
4. Click Send test email. Record the outbox reference and wait for the scoped
   operation to reach `sent`. Check that Postmark returned a message ID.
5. Confirm receipt in the expected mailbox and compare the subject/body and
   sender with the saved settings. Provider acceptance alone is insufficient.
6. Repeat at a selected library scope after saving a deliberate library From
   override. Confirm the library uses its override and that switching back to
   system scope does not leak the library value.
7. If the token must be removed, use the explicit clear action, save, reload
   Settings, and verify the test action is disabled with a safe readiness code.

The Settings action does not save drafts, retry a provider call, or send with
unsaved values. Use Operations for scoped status and authorized manual retry of
terminal failures. Never put a Postmark token, full provider response, message
body, or another library's data in issue comments or logs.
