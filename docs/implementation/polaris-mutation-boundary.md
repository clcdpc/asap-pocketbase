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
sentinel semantics are inferred. The snapshot also retains whether the source
field was omitted, explicit-invalid, or a current positive preference, so hold
placement cannot confuse the legacy projection with a supplied value.

[PAPI pickup choices](https://documentation.iii.com/polaris/PAPI/7.8/PAPIService/PatronPickupBranchesGet.htm)
are branch-level locations. ASAP's system organization 1 cannot be selected as
an operational branch. Before a new hold, the service refreshes the patron and
pickup list; a positive current preference must appear in that list. Only a
genuinely omitted preference may fall back to the registered branch, and only
when that branch appears in the current eligible list. Explicit zero/system 1
does not fall back and prevents the create marker. Branch identity is not
library authorization: the registered branch supplies the PAPI hold member
context, while active code-2 library scope and current policy govern permission.
Imported request snapshots retain source provenance; they do not supply the
live preference or authorize a write.

Existing-hold adoption treats the hold-list `PickupBranchID` as optional
metadata. When present, the raw value must be one unique integer matching the
pinned SDK model; omission and explicit 0/system-1 values are stored as unknown
(`NULL`), while malformed, duplicate, coerced or negative values reject the
hold read. The current patron registration cannot establish an earlier hold's
original `RequestingOrgID`, so adoption leaves that request-context snapshot
NULL.

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

## PR #377 residual provider investigations

INV-001 remains an external contract uncertainty. The pinned SDK's
[`OrganizationsGetAsync`](https://github.com/clcdpc/polaris-api-csharp/blob/c89d9e604fdf9c1a21012c639f8baa9e1e80d214/src/polaris-api-csharp/Methods/OrganizationsGet.cs)
performs a single organization-scoped GET. The vendor's
[`OrganizationsGet` contract](https://documentation.iii.com/polaris/PAPI/7.1/PAPIService/PAPIServiceOrganizationsGet.htm)
describes type filtering and the returned row count, but establishes no
deletion/tombstone or deployed access-filtering guarantee. This evidence does
not establish that omission is a reliable loss-of-authority signal. Both
manual sync and scheduled refresh preserve an omitted row's prior
classification, activity and LastSyncedUtc; SQL staff authority can therefore
continue without a new observation. Operators can explicitly deactivate a
library, which revokes sessions and fences new mutations; observed
reclassification also removes library authority while preserving references.
No automatic omission deactivation is implemented. A safe policy requires the
deployed service/version and access-filter contract, plus a confirmed full
snapshot followed by omission. This is a release/operator evidence requirement,
not a newly proven implementation defect.

INV-002 also remains a documented remote-observation risk. SQL authority at
pickup intent uses the last provider-verified patron snapshot. Public and staff
suggestion final acceptance refresh again, but that refresh follows pickup
processing. `ProviderRegistrationChangedDuringReadinessIsRejectedAtFinalAcceptance`
changes actual provider home, code and native identity during readiness and
checks both no-PUT rejection and truthful partial outcomes after a known PUT.
It does not assert that remote authorization is atomic. Branch selection uses
the earlier verified eligible list; later eligibility can change remotely.
The pinned
[`PatronUpdateAsync`](https://github.com/clcdpc/polaris-api-csharp/blob/c89d9e604fdf9c1a21012c639f8baa9e1e80d214/src/polaris-api-csharp/Methods/PatronUpdate.cs)
addresses a barcode and supplies pickup data, without an expected native ID,
registration version or conditional identity argument. Another read could
narrow this gap but cannot eliminate it. The deployed barcode reassignment and
former-alias resolution semantics, server-side policy checks, and any supported
conditional/stable-identity API must be established before changing this
policy. Deterministic tests are evidence of local boundary behavior, not of
deployed Polaris semantics. No unsupported conditional write is invented.
