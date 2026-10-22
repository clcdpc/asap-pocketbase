# Pinned Background Workflow Notes

Read-only preparation against PocketBase
`150b30b776565194260cc327eeeffdfb46475e81`. These observations locate executable
behavior; they do not replace or amend the authoritative port pack. No source
database, live provider, or PocketBase process was accessed. Slice 5 has not
started.

## Scheduling And Queue Visits

- `pb_hooks/main.pb.js` registers four recurring triggers: hourly hold-check,
  daily 02:00 organization sync, Sunday 20:00 weekly summary, and five-minute
  ISBN check, each with its own documented environment override.
- `lib/jobs.js` runs four timeout families, obtains system Polaris auth, then
  hourly ISBN checks, purchase promotion, hold placement, and fulfillment.
  Organization sync also refreshes patron codes. The target intentionally
  removes hourly ISBN work, adds acquired-hold recovery first, and keeps the
  remaining relative business order in one non-overlapping orchestrator.
- `lib/job_queue.js` starts a null in-memory cursor on every invocation, queries
  `(sortField, id)`, and counts each visited row against configured limits. Its
  cursor does not survive restart/run completion. Target finite persisted
  `QueueProgress` cycles are an explicit required correction, not a literal
  port of this helper. Scanning order must not redefine domain timeout age.

## Exact Timeout Behavior

`lib/jobs/timeouts.js` confirms all four predicates and notification differences:

| Family | Source candidate | Age | Outcome | Mail |
| --- | --- | --- | --- | --- |
| Outstanding | suggestion | created | closed/rejected | configured auto-rejection template only |
| Pending hold | pending_hold | updated | closed/rejected | none |
| Hold pickup | hold_placed | updated | closed/hold_not_picked_up | none |
| Additional copy | open task | updated, or created if absent | close independent task | none |

The source uses strict `< cutoff` and `Date.setDate` calendar subtraction. The
target's injected clock, configured business zone, DST gap/overlap rules,
locked current settings/version/activity checks, and incomplete-operation
barrier are binding. An old outstanding purchase is not an OutstandingTimeout
candidate. Additional-copy closure does not transition the source title.

## Identifier And Promotion Paths

- `lib/jobs/isbn_checks.js` contains two materially different implementations.
  The older hourly path keeps retry counts/five-attempt exhaustion but does
  not do the full BIB persistence/reconciliation of the dedicated path. The
  dedicated five-minute path saves BIB, reconciles bibliographic data, adds
  found/not-found/multiple-match tags and check times, and uses
  `skipped_no_isbn`; its broad non-found branch loses failure classification.
  Consolidate exactly as pack section 23 requires; do not preserve a false
  not-found result on provider failure.
- `lib/jobs/purchase_promoter.js` preserves an existing BIB path and identifier
  search path. A found title goes to pending_hold or purchased_no_hold closure
  according to AutoHold. Physical-hold exclusion includes material types 36/41.
  Search can reconcile data and add multiple-match/catalog tags. The target
  must apply current participation/version/barrier checks and its explicit
  outcome classification, not copy source catch-and-proceed behavior blindly.
- `promoteRequestNow` also checks suggestion identifiers. Reuse the target
  canonical services and permitted stage predicates rather than adding a
  second identifier implementation behind the manual action.

## Hold Placement And Fulfillment

- `lib/jobs/hold_placement.js` resolves BIB, patron, existing hold, holdability,
  then refreshes patron pickup immediately before create. Status 5 plus GUID
  invokes reply; source sets success without proving the reply result. The
  target journal/evidence rules explicitly supersede this unsafe assumption.
- Result tags distinguish existing same-patron hold (29), no holdable items
  (6), and known patron/workstation/org/BIB/pickup errors. Preserve useful
  workflow diagnostics without treating a status code alone as the target's
  authoritative final outcome or final HoldRequestID.
- `lib/jobs/fulfillment_tracker.js` first looks for positive checkout by BIB,
  then unclaimed/cancelled/expired hold rows by BIB. Positive title-level
  checkout is retained; terminal hold closure must instead match the exact
  tracked final hold ID plus expected BIB/patron under pack section 9.2.
  Never infer final identity from RequestGUID, historical BIB markers, list
  ordering, or a broad same-title match.
- `staff-source-notes.md` contains the separately verified CLC request/reply
  boundary findings. Read the actual pinned provider helpers and the package
  source again before implementing a boundary the installed typed SDK omits.

## Weekly Summary

`lib/jobs/weekly_summary.js` selects all open suggestions, approved purchases
without BIBs, and open additional-copy tasks, with five most-recent samples
and stage links. It constructs one unscoped payload for every opted-in verified
staff account with a nonblank weekly address. The target deliberately corrects
this privacy behavior: own active library for staff/admin, active consortium
for super-admin, and current identity/scope/address validation at intent/send.

The source's normal run key is period-based; force appends a wall-clock suffix.
Target normal recipient/period keys and one durable ManualRunId per accepted
forced invocation preserve intentional resend while making job retries
idempotent. Weekly override-to-primary recipient fallback is an intentional
normalization requiring old-versus-target migration reporting. Preserve useful
counts/sample/link behavior, not the source's unscoped recipient payload.

## Temporary Provider Exception

The user's file-sender override applies only to final transport. Do not mimic
Postmark delivery events/webhooks. Complete ordinary SQL outbox and email
operations with recording transport tests; record real provider integration,
webhooks and transport/release validation as outstanding release/rehearsal
gates under `temporary-email-transport.md`.
