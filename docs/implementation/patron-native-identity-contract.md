# Patron native identity contract

`NativePatronId` is the stable Polaris principal for a patron session and for a
newly created title request. The barcode is a current source identifier and may
rotate. Login may use a Polaris username that differs from the returned
barcode; the authenticated positive native patron ID and provider-verified
current barcode are stored in the session. A username is never inferred to be a
barcode alias.

Refresh and recovery accept only a provider-verified current or former barcode
alias. A public session must still resolve to its persisted positive native ID,
and the saved barcode must remain among the verified aliases. Staff operations
use a request's positive native ID when present; historic requests with a null
identity may use a verified barcode lookup without receiving a guessed identity
snapshot.

Fulfillment correlates checkout or terminal hold evidence to a fresh,
provider-verified patron refresh for the request barcode. The live positive
native ID must match every non-null native ID stored on the request and its
latest successful hold operation, and it is checked again after the evidence
read before closure. The locked SQL rows are checked against that same native
ID before the request is closed. A known mismatch leaves the request and hold
journal unchanged. Historical rows with null native IDs retain the verified
barcode lookup path; fulfillment does not write a guessed ID into either
snapshot. Research links likewise omit a patron link when a saved positive
native ID differs from the provider's current lookup. ID-only PAPI projections
remain usable when they contain no barcode alias fields, while any supplied
current or former barcode must agree with the requested barcode.

The requested barcode is matched to a provider-verified current or former alias
case-insensitively (`OrdinalIgnoreCase`), consistent with `KnownBarcodeAliases`
and the service's alias predicates. This ingress comparison does not relax the
separate protocol check: a supplied raw JSON alias must still match the pinned
Clc/Newtonsoft model's alias exactly (`Ordinal`) after cleanup. Alias matching
never invents a missing native ID or treats an unverified username as a barcode.

New request duplicate and limit checks match the native patron ID and include
legacy null-ID rows only when their barcode matches a verified current or former
alias. Existing request history is not rewritten when aliases rotate. Pickup and
hold operations retain their original journal identity and may recover through a
verified former alias without repeating an uncertain provider write.

Public submission rechecks the current patron ID, current home library, saved
experience library, and current `AllowAnyRegisteredCardLogin` policy both when a
pickup write intent is acquired and when a request is accepted. A request is
allowed when the current home library is the effective library, or when the
saved experience library is the effective library and its current
`AllowAnyRegisteredCardLogin` setting permits cross-library use. The current
policy and organization rows are locked in the SQL transaction; provider calls
remain outside SQL transactions.

Schema upgrades preserve null native IDs for historical sessions and requests.
They do not infer an identity from a barcode. Unbound legacy sessions fail
authorization and require a fresh verified login.

Fulfillment may record a rejected identity observation in queue progress so the
worker can stop and report the reason. That progress row is worker-owned
bookkeeping. Request state, hold journals, pickup intents, events, outbox rows,
and provider writes remain business mutations and require their existing locked
authority and identity checks; a recorded progress outcome does not authorize
any of those mutations.
