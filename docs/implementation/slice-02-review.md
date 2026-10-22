# Slice 2 Independent Review

## Scope And Freeze

Reviewer: fresh GPT-5.6 Terra High Ohm,
`01a0994e-4e33-7173-8916-dcb64ccab32d`. Sol XHigh Raman retains implementation
ownership and will fix confirmed findings; Terra does not implement feedback.

Accepted predecessor: `1e36761c771db70d0b669087d0a843b66cf5618b`.
Full Pass 1 covers the 46-file receipt
`.git/asap-slice-02-review-state-pass1.json`, source-built final artifact
`.artifacts/slice-02-final-d7f4c9a182be`, prior-slice callers and surrounding
invariants. See `slice-02-review-packet.md` and `slice-02-evidence.md` for
exact hashes, test commands, parent acceptance findings and source evidence.

## Pass 1

Completed 2026-09-13. Terra independently verified all 46 source receipt files
and all 579 published payload hashes, reviewed the full tracked/untracked slice
and relevant surrounding code, and compared workflow/claim/pickup behavior with
the executable pinned source. It did not rerun shared SQL/builds while frozen;
the 174-test and native acceptance results are explicitly parent evidence.

### S2-T1: Operator Resolution Evidence

P1 confirmed in Pass 1, resolved in full Pass 2. The original `ResolveAsync`
accepts caller-selected evidence kind, a nonblank reference and an
`OriginalExecutorExcluded` checkbox, then can terminalize an uncertain marked
operation and complete request/mail state. Existing test input `8456` is not
itself operation-specific authoritative evidence. Do not confuse leaving the
final hold ID null with proving the operation's success or executor exclusion.

A fresh independent Astra Max Hilbert consultation
(`01a09954-34ef-7632-93d0-dc3561d3356f`) resolved the smallest faithful
manual-operator evidence/quiescence boundary under section 9.1. This does not
reopen the no-inference rule, invent a provider lookup or waive the finding.

### S2-T1 Bounded Advisor Ruling

Hilbert completed the read-only consultation and was closed. The P1 is upheld.
Explicit trusted-super-admin attestation may record actual externally verified
operation-specific authoritative proof. The pack does not require the server
to retrieve or authenticate that external provider record. Missing automated
correlation does not disable every manual resolution; missing conclusive proof
or executor exclusion leaves that operation blocked.

Implement only in the existing operation service/input/form and journal/audit:

- Keep evidence type/reference/reason, with affirmative operation-specific
  attestation and inspectable provider report/response/support provenance. Bind
  acceptance to the server-loaded operation/attempt/frozen identity and supplied
  version; show the operation/attempt in the form.
- For marked uncertain dispatch, separately record actual executor-exclusion
  reference, explanation and attestation: affected/superseded executions,
  termination/completion time and accounting for in-flight provider work.
  Include interactive hosts. Lease expiry, requested cancellation and disabled
  scheduling are not proof. Actual quiescence remains an operator action.
- Under the required workflow guard and ordered Organization -> actor ->
  request -> operation locks, revalidate current authorization, association,
  version, phase and ownership. Atomically fence takeover with a new epoch.
  Server-proven `fenced_never_dispatched` requires acquired phase and no dispatch
  markers; fence old marker writers. That case requires no external proof.
- Keep a separately supplied, explicitly proven final hold ID distinct from
  the evidence reference. Correlated-hold evidence requires proven mapping and
  ID; final-success evidence may complete with null ID/diagnostic. Never parse
  reference text into an ID or expose the internal F2 synthetic-evidence input.
- Persist prior useful evidence plus operation/attempt/epoch, actor/time,
  attestations and before/after states in existing DetailJson/event/audit fields.
  Completion and its normal local state/mail changes are atomic and once-only;
  no-effect completion never starts an immediate replacement attempt.
- Await a current ownership/lease check immediately before create/reply calls.
  This prevents an already-stale executor from dispatching, but cannot eliminate
  a subsequent pause before sending; actual exclusion proof is still required.

The fields record operator-verified external facts, not machine verification or
physical worker termination. Backend validation cannot detect a fabricated
external report merely by validating strings. No generic proof/provider
framework, payload repository or invented provider lookup is authorized.

Required regressions include rejected old `8456` payload, missing proof or
exclusion, live owner/stale version/revoked actor, final-success with unavailable
ID, separately proven exact ID, definitive no-effect and acquired/no-marker
controls. Preserve barrier and event/audit/outbox counts on rejection. Exercise
both create and reply paused across marker/takeover/before provider effect;
distinguish stale SQL writes and requested cancellation from actual exclusion.
Same/new/sole same-BIB rows and empty/delayed/failed reads remain insufficient.
Synthetic tests model externally verified evidence only; they prove no live
provider capability. Sol owns the corrections; Terra independently re-reviews.

Anchors: porting-spec sections 9.1 success/no-effect/operator resolution and
9.2, database-design section 12 DetailJson, and testing R1/F2. The official
pack itself remains unchanged.

`hold-resolution-operator-evidence.md` records the corresponding manual
evidence/quiescence procedure and its explicit external trust boundary. The
deployment slice must incorporate and rehearse it; tests do not substitute for
actual termination or provider evidence.

Sol's first focused run reproduced four weak-proof/shared-guard/fencing
failures. The corrected service passes four operator-resolution/shared-guard
tests, one create/reply pre-dispatch ownership test and both late-original
create/reply result tests. A broader hold run initially passed 7/8 because one
older fixture omitted the new request version; the payload is corrected and
must be rerun. The first extended browser run failed to locate the Resolution
select after rendering the operator context; explicit accessible names are
corrected and await rerun. Guard resource cleanup/cancellation checks include
acquisition failures and release exceptions. These are interim fix results,
not full-suite/artifact acceptance or second-pass review clearance.

Subsequent local full-suite runs pass 181/181 with zero skips (2m22s and an
exact-floor 181 rerun in 2m24s). Frontend/npm checks pass and the CI minimum
floor is raised from 120 to 181 only. Astra inspected the nine-state run
`staff-0348c2b203024c7599cff5732afc0b15`: desktop/mobile upper resolution form
and report are clean. Before final publication, extend the same browser
journey to the lower evidence/exclusion controls and actual form submission
with resulting SQL state/evidence/count assertions. Rendering and API tests
alone do not establish the new form's end-to-end payload wiring.

The extended browser report `staff-79d815d1ffb74d4e8be5d2197ca4b250` now
contains twelve clean states, including lower desktop/mobile evidence controls
and an accepted resolution. Astra inspected both lower-form PNGs: long labels
wrap, controls do not overlap and the focused Resolve button is within the
scrolled viewport. The browser verifies missing proof attestation emits zero
resolution requests, submits the full proven-ID/operator-evidence payload,
receives 200 and shows Hold placed without a remaining resolution form. The
hosted test additionally asserts exact stored evidence, distinct ID, epoch and
one event/audit/outbox row. The final full-suite result and fresh freeze still
govern acceptance; this is not the independent Pass 2 result.

### S2-T2: Metadata Actor Authorization Race

P1 confirmed in Pass 1, resolved in full Pass 2. The original
`StaffLifecycleService.UpdateMetadataAsync`
authorizes from the earlier `CurrentStaff` snapshot and only fences the target
rowversion. A concurrent actor deactivation/demotion/move can win before the
metadata commit without invalidating that target version. Use the existing
ordered lifecycle/current-actor transaction, including current participation,
identity/role/scope checks and atomic audit. Add coordinated real-SQL race
coverage; no metadata/audit write may survive a rejected authorization.

Sol reports the targeted tests reproduced two actual failures before the fix:
deactivation did not wait for an in-progress metadata transaction, and a stale
actor binding still returned `updated`. The corrected metadata transaction and
shared current-actor predicate pass two focused metadata tests and three
lifecycle tests, with a zero-warning/error Release build. Tests include both
serialization orderings, stale binding and inactive participation. Create,
reactivation, rebind, role/scope change and deactivation also use the corrected
locked-organization predicate. This is local fix evidence, not Terra clearance;
the complete fresh suite and same-reviewer full Pass 2 remain required.

## Pass 2

The same Terra context received the full current 48-file slice and new frozen
artifact for Pass 2, including the bounded advisor ruling and operator procedure.
Astra independently passed the exact-floor 181-test suite, zero failures/skips,
2m31s. All 43 current frontend source files match the frozen Web publication.
Final native original/expanded SQL acceptance and pinned recipient oracles
passed, with unchanged deterministic reports. The published-Web patron
regression also passed with all owned runtime resources cleaned up.

Terra approved full Slice 2 Pass 2 with no actionable findings. S2-T1 and S2-T2
are closed; no new substantive issue was identified. The reviewer remained
independent and did not implement fixes. No ceremonial Pass 3 is required.
Artifacts and evidence remain intentionally retained; cleanup refers to owned
runtime processes/databases/certificates, not deletion of evidence.

Terra's durable closure confirms review of all 48 receipt files and surrounding
auth/tenant/cookie, lifecycle/claim, hold/pickup/identifier, outbox, migration and
frontend contracts. Personally executed checks were 48/48 code and 579/579
payload hash verification, Web DLL hash verification, `git diff --check` and
static source/test/contract inspection. Builds, full tests, native SQL and
published-Web browser runs are explicitly Astra's runtime evidence, not
independent Terra executions.

T1 closure rests on the explicit operator trust boundary, operation-bound proof
and separate exclusion basis, distinct ID, ordered current-state checks/epoch
fencing and preserved atomic history/mail. The acquired/no-marker exception is
server-fenced, not external attestation. T2 closure rests on the shared invariant
and ordered current actor/target eligibility checks, with both race orders and
binding/participation controls. Unsupported provider GUID correlation remains
unavailable; the manual procedure and live provider behavior need release
validation. No additional findings or local follow-up defects were left open.

## Gate

The required full-slice Pass 1 -> Sol fixes -> full Pass 2 gate is satisfied.
All confirmed findings are resolved and local acceptance is green. Astra may
create the coherent Slice 2 milestone and verify its actual remote CI before
dispatching Slice 3. Retain this reviewer and Sol only for any CI regression
that requires reviewed correction; do not waive a new blocking defect.

Live Entra/Polaris behavior, actual executor quiescence, real maintained
cancellable Postmark integration, whole-app review and exact-artifact
rehearsal/release gates are not certified by this local slice closure.
