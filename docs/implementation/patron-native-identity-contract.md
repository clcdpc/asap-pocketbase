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
