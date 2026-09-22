# Manual Hold Resolution Evidence

Slice 2 implementation support under porting-spec sections 9.1/9.2. This is the
operator trust boundary for the existing purpose-specific operation journal,
not a new provider integration or permission to force retry. Final deployment
runbooks must incorporate this procedure and verify it during rehearsal.

## Required Evidence

Resolution is limited to a currently authorized super-admin and the current
version of one existing operation. Identify its request, numbered attempt and
frozen patron/BIB association before collecting evidence. Keep secrets and raw
patron payloads out of application evidence, screenshots and logs.

For succeeded resolution, obtain an inspectable documented final provider
response or authoritative report/support record that proves this exact attempt
finalized the hold. Record its provenance and causal connection to the operation.
A generic successful HTTP response, pending policy prompt, same-BIB hold, sole
new hold, BIB number or bare hold ID is insufficient. A proven final success may
have no obtainable final HoldRequestID; resolve with null identity in that case.
Supply a separate final ID only when the evidence proves the operation-to-ID
mapping. Never use a conversation RequestGUID, qualifier or BIB as that ID.

For not-performed resolution of a marked dispatch, obtain a documented
correlated final result proving no hold can result, no required reply is still
pending and later acceptance is excluded. Empty searches, timeouts, visibility
lag, requested cancellation and elapsed time do not prove no effect. When that
proof is unavailable, leave the operation `operator_required`.

An acquired operation with no create/reply dispatch markers is different:
the application can establish and fence never-dispatched state in SQL. That
specific proof does not require an external provider report or terminating a
worker that can no longer acquire its fenced dispatch marker.

## Executor Exclusion

A dispatch marker can be committed before the actual provider invocation.
Expiry or takeover fences later SQL writes but cannot recall a call already
sent or prevent a paused executor from later reaching an external call.

For an uncertain marked operation, establish that every potentially responsible
execution has actually ended or been terminated. Account for current and
superseded Hangfire executions, interactive request hosts and overlapping IIS
worker processes. A disabled schedule, stop request, cancellation token or
expired lease is not evidence of process termination.

When completion cannot otherwise be established, use the deployment operator's
quiescence procedure to stop the affected application/workers and verify all
responsible processes, including overlapping old workers, have exited. Record
the affected execution/process identities, completion/termination time, method
and inspectable operations reference. Do not assume that returning from a stop
command proves every process has exited. Provider work already sent must still
be accounted for through definitive outcome evidence; killing a local worker
alone does not prove provider no-effect.

Restart only the validated artifact/configuration under the ordinary startup
gates. Sign in again, refresh the operation/version and verify the evidence
still applies. Submit the explicit operation-specific proof and exclusion
attestations with their separate references/explanations. A newly live owner,
changed operation or changed authorization requires re-evaluation, not repeated
submission of stale input.

## Application Boundary

These are operator-verified external facts. The application authorizes and
versions their acceptance, checks server-verifiable ownership/phase/association,
and records the decision in the existing journal/event/administrative audit.
It does not independently authenticate an external support report or physically
terminate workers because an operator checks a box.

Rejected or inconclusive resolution leaves the mutation barrier and business
state intact. Accepted success completes local request/event/mail work once.
Accepted no-effect resolution does not immediately create a replacement hold;
a later ordinary eligible visit requires a new active-library acquisition and
fresh prechecks. No resolution authorizes guessing a final ID or replaying an
uncertain create/reply. The selected CLC adapter still has no established
automatic GUID-to-final-ID correlation capability.

Synthetic tests exercise this local acceptance/fencing contract with modeled
operator evidence. They do not prove live provider guarantees or rehearsal
worker quiescence. Real provider, Entra, operations and release validation gates
remain distinct, including the explicitly deferred real Postmark transport.
