# Temporary Final Email Transport

User-authorized implementation substitution, 2026-09-12, for PR #264. This
overrides only the final Postmark provider transport requirement while the
incompatible CLC package is unavailable. It does not reopen the architecture
or change the slice sequence and review gates.

## Implementation Boundary

Use one minimal `FileEmailSender` behind the same narrow asynchronous
application transport abstraction the eventual Postmark adapter will use.
Honor `CancellationToken` through application orchestration and file I/O.
Each logical invocation writes one uniquely named `.html` file to an ignored
local directory such as `.artifacts/dev-email/`. Include recipient, sender,
subject, outbox/message identifier and email body; escape metadata and retain
the HTML body usefully for browser inspection. Do not use those files as
durable state. Exclude them from Git, application publish and CI artifacts.

Preserve durable SQL outbox states/transitions, deterministic BusinessKey,
staff authorization-sensitive revalidation and RecipientAddressKind,
nonproduction exact-domain protection, leases/start and operation deadlines,
stale-worker fencing, at-least-once semantics, retry/failed/suppressed behavior,
and immutable sender/recipient/content snapshots. Provider substitution must
not alter the business transaction or accommodate failures differently.

Ordinary deterministic tests keep their recording/fake transport boundary;
add only focused file-sender tests. Do not add messaging frameworks, provider
hierarchies, SMTP, local mail servers, delivery/webhook simulation or other
temporary infrastructure. Provider-specific webhook work may remain absent.

## Configuration Outcomes

A bounded fresh Astra Max implementation consultation confirmed that the file
substitution must not erase document 01 section 14's configuration outcomes.
Use a small cancellable local configuration-readiness query on the same narrow
transport boundary, not a provider connectivity test or another subsystem.
The file sender's configured output destination needs no Postmark credential;
directory creation remains part of sending, not durable application state.

The recording/fake boundary must cover unavailable configuration at intent:
commit the business action plus terminal `mail_not_configured` suppression,
with the normal BusinessKey and no enqueue or later resurrection. If a validly
queued message reaches sending and the boundary reports configuration is now
unavailable, use the existing lease/version ownership fence to move it to
`failed` with `mail_not_configured`, clear active lease/scheduling fields and
retain payload/identity for later authorized manual retry. Do not route this
known-not-configured outcome through automatic ambiguous-delivery retries.
Ordinary file I/O failures keep ordinary transport-failure handling.

Postmark-specific token availability, unprotection and current effective
credential resolution on every send/retry are part of restoring the real
adapter and its deferred transport tests. No dummy Postmark credential or
simulated protocol is required by the temporary sender. This preserves the
application outcomes now without pretending provider integration is complete.
Manual email operations remain in their prescribed Slice 5 scope.

## Deferred Release And Rehearsal Blocker

Before production readiness or exact-artifact rehearsal can pass:

1. Obtain/update a maintained Rest 3-compatible `Clc.Postmark.Api`.
2. Verify genuinely cancellable asynchronous sending matching ASAP's boundary,
   including complete-operation cancellation rather than merely bounded waiting.
3. Replace `FileEmailSender` with the real Postmark adapter.
4. Complete real provider integration and webhook handling.
5. Pass all required transport-specific and release-validation tests.

Implementation proceeds through the existing slices without waiting for this
dependency. Temporary file output is not evidence of delivery and cannot
satisfy real provider, rehearsal, release or production-completion gates.
