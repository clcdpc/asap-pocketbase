# Polaris pickup and mutation boundary

The post-campaign remediation for PR #344 uses the restored public
`Clc.Polaris.Api 4.0.0-beta.5` source
[`c89d9e6`](https://github.com/clcdpc/polaris-api-csharp/tree/c89d9e604fdf9c1a21012c639f8baa9e1e80d214)
and its `Clc.Rest.Client 3.0.0-beta.3` dependency source
[`acfd54f`](https://github.com/clcdpc/rest-client/tree/acfd54fd513a7033e8742bcf98ece5f9ad0bc082).
These exact package contracts, rather than a newer checkout, own request
validation, token acquisition, signing, routing, serialization and transport.

## Current pickup identity

The package models `RequestPickupBranchID` as an `int`; omitted JSON also
deserializes to zero. ASAP inspects the original field to retain the established
registered-branch fallback when the field is omitted. That fallback accepts
only a registered organization above system scope 1. An explicit zero or system
1 means no usable current branch and maps to `null`, without invoking the
omission fallback. A positive branch above 1 remains its native integer ID.
Malformed, duplicate or negative fields fail closed; no additional undocumented
sentinel semantics are inferred.

[PAPI pickup choices](https://documentation.iii.com/polaris/PAPI/7.8/PAPIService/PatronPickupBranchesGet.htm)
are branch-level locations. ASAP's system organization 1 cannot be selected as
an operational branch. The provider, hold/pickup service guards and pickup
journal constraint all enforce this distinction. Imported request snapshots
retain source provenance; they do not supply the live preference or authorize
a write. Mutation entry points refresh current provider data.

The prior native journal constraint allowed system 1 in previous/observed
preferences. DACPAC pre-deployment canonicalizes those fields to NULL before
enforcing the stronger constraint. It preserves each original operation,
target branch, state, timestamps and historical label; it neither completes
pending work nor invents an external outcome.

First selection journals `FromPickupBranchId = NULL`. Patron suggestion, staff
suggestion and standalone staff update commit intent before PAPI, then accept
local state in a separate transaction. A retry can reconcile the requested
live value after dispatch finishes; it cannot repeat an uncertain write. The
explicit observed-at-load flag preserves optimistic concurrency when the
observed preference was null.

## Execution evidence

Rest.Client stores request preparation, transport and deserialization exceptions
in `IRestResponse.Exception`. Consequently, no HTTP response alone does not
prove transport was entered. The thin PAPI subclass observes the package's
protected `SendAsync` hook for hold create, hold reply and pickup update.
Entering that hook conservatively means a mutation may have been sent. Staff
authentication is excluded; the package continues to own token caching and
usability. Original authentication defects are preserved when the package
replaces a failed authentication response with a token-required exception.

Caller cancellation propagates. Expected HTTP, timeout and JSON/protocol
failures retain existing safe provider contracts. Unexpected argument,
configuration-invariant and programming exceptions propagate, including those
captured in a response; optional enrichment ignores only expected failures.

Positive evidence that a mutation never entered transport produces an explicit
no-dispatch exception. Hold completion records `local_not_dispatched` and
`provider_transport_not_entered` while preserving the original dispatch marker,
phase and conversation identity. Completion requires the current owner token,
execution epoch and unexpired lease. An unexpected cause then reaches normal
exception handling. Recovery does not replay that completed operation.

After possible dispatch, missing HTTP responses, unsuccessful HTTP alone,
malformed or incoherent bodies remain uncertain. Hold status/GUID/qualifier
normalization remains unchanged. Programming exceptions are never disguised
as ambiguity; the unfinished durable marker still prevents automatic replay.

[PatronRegistrationUpdate](https://knowledge.ag-software.clarivate.com/polaris/PAPI/PAPIService/PAPIServicePatronRegistrationUpdate_v1.htm)
documents invalid patron (`-3000`) and invalid pickup branch (`-3622`) validation
rejections. Only a successful HTTP response with a unique explicit matching
PAPI code and typed result proves these pickup-only writes had no effect.
Other errors remain uncertain. Proven rejection or no-dispatch completes the
pickup journal in state 4 with a failure code, without fabricating provider
success or deleting evidence. A later safe attempt receives a separate intent.

Real SQL and actual-package recording-handler tests cover these boundaries,
including NULL journaling, all three HTTP entry points, disconnect-after-write
reconciliation, null concurrency, authentication failures, programming defects,
cancellation and no-effect completion. Ordinary tests make no live PAPI calls.
