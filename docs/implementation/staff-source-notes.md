# Pinned Staff Workflow Reading Notes

Read directly using `git show` at PocketBase commit
`150b30b776565194260cc327eeeffdfb46475e81`. These are focused reading aids for
the next packet, not an alternative contract or evidence that Slice 2 passed.
The port pack overrides legacy authorization/recovery failures explicitly.

## Source Reads

- `pb_hooks/main.pb.js` and `lib/staff_routes.js`: explicit route inventory and
  delegates. Preserve title-request list/action/claim/unclaim/assign, profile,
  patron/BIB lookup and dedicated pickup routes. Replace legacy staff password
  login/setup with Entra and cookie endpoints. Additional-copy/admin/jobs/
  analytics are separate later slices, not broken links in the first staff UI.
- `lib/staff/title_request_action_context.js`: retains starting status, format,
  identifier/BIB and explicit action before mutation. Nonblank changed
  identifier/BIB triggers immediate lookup. Target must validate both original
  and proposed stage so combined payloads cannot bypass the pack's barrier.
- `lib/staff/title_request_actions.js`: action validation -> BIB handling ->
  request persistence -> actor auto-claim -> format-rule evaluation -> final
  side effects. Ordinary actions other than `assign` claim/transfer to the
  actor; format changes then run the configured format rule. Catalog selection
  adds identifier-found tagging and an explanatory note. Target composes local
  changes/events/intents atomically and uses current staff serialization.
- The same file's pickup endpoints return live allowed branches and current
  preference, request pickup snapshot, refresh time, warning and read-only
  state. `hold_placed`/`closed` are read-only. Save checks selected branch,
  refreshes once on an invalid selection, and rejects changed-since-load live
  preference unless the desired branch already equals the live value. A
  provider failure returns 502 with no local pickup change. Successful change
  records note/event and snapshot; unchanged provider preference can still
  refresh the local snapshot. Target adds version/operation barrier checks and
  uses configured system Polaris credentials, not per-staff identity.
- `lib/staff/title_request_bib_actions.js`: another open request for the same
  barcode/BIB (not restricted to a library in this source check) marks duplicate
  hold context and can return 409. `closeDuplicate` closes with the canonical
  duplicate reason. BIB reconciliation is skipped for closure. AutoHold=false
  closes eligible BIB transitions as `purchased_no_hold`; `additionalCopy` is
  exempt. Live existing-hold detection may move pending to placed. The source
  Boolean is insufficient for target adoption: use the exact operation/evidence
  contract and authoritative HoldRequestID when available, never infer identity.
- `lib/staff/title_request_side_effects.js`: purchase entering
  `outstanding_purchase` sends purchase-approved mail; final `pending_hold`
  sends none; final `hold_placed` sends already-owned mail. Purchase reminder
  requires final outstanding-purchase status and the explicit user choice;
  skipping that queue explains why no reminder was sent. AlreadyOwn attempts
  a hold unless AutoHold=false and sends already-owned mail. Rejection uses the
  selected template. Source best-effort direct hold/email calls become the
  mandated journal/outbox, without preserving unsafe replay or false success.
- `lib/staff/title_request_claims.js`: competing claimant yields 409. Ordinary
  staff may clear their own claim; admins may clear other claims in authorized
  scope. Explicit assignment requires an active eligible assignee/open item,
  records the actor/assignee event, and notifies after commit. Target revalidates
  target eligibility under the shared StaffUser lock and snapshots the correct
  sensitive-mail recipient tuple/address kind.
- `lib/staff/title_request_list.js`: returns the complete authorized set for
  client filtering. Super-admin defaults to all; ordinary staff ignores a
  requested foreign scope. Rows sort descending by phase-entry timestamp, then
  updated, created and ID. Latest event entering the current phase supplies
  phase entry, with creation fallback for suggestions and updated/created for
  other states. Legacy patron fallback enriches DTOs; target reads immutable
  imported/request snapshots rather than recreating a durable PatronUser.
- `lib/staff/auth_routes.js` and `lib/records/staff.js`: all five profile fields
  are user-scoped. Legacy identity is domain/username and placeholder email may
  end in `@staff.asap.local`; none of that supplies target Entra authorization.
  The source profile endpoint does not edit primary contact. Source login
  refresh/upsert behavior must not revive target deactivated/rebound accounts.
- `lib/mail.js` and `lib/mail/templates.js`: ordinary reminders/assignment use
  legacy weekly/primary combinations, intentionally normalized by the pack to
  primary NotificationEmail. Weekly override stays weekly-only. Templates
  preserve placeholders, drop "by {{author}}" when author is blank, HTML-escape
  substituted values and turn body newlines into breaks. Source refreshes patron
  destination before sending; target must not overwrite submission snapshots.
- `lib/polaris/patron.js`: current pickup comes from RequestPickupBranchID, not
  a synthesized patron-branch fallback inside that raw-response mapper. The
  complete callers then invoke `orgs.attachPatronScope`, which does provide a
  PatronOrgID fallback when the request preference is missing; preserve that
  distinction and allowed-branch selection. Raw alternate preference aliases
  are discarded, not given precedence over RequestPickupBranchID.
  Hold rows distinguish BibID from hold
  identity; the legacy active-hold helper returns only a Boolean and is not a
  sufficient target final-ID mapper. Checkout reads exclude e-content in source.
- `pb_public/staff/js/http.js`, `app/auth.js`, `grid-policy.mjs`: app-specific
  auth wrapper sits above shared transport. Preserve profile dialog controls,
  initial Mine/unclaimed preference, sign-out dialog/menu clearing, normalized
  flags and safe status feedback. Remove PB SDK/authStore, preserve Grid.js and
  latest-response protection; no new frontend framework or persisted workflow
  library selection.

## Remaining Implementation Reads

The Slice 2 implementer still inspects actual callers/helpers and regression
tests, including `lib/records/suggestions.js`, `lib/records/helpers.js`,
`lib/format_claim_rules.js`, `lib/staff/{assignment_policy,claim_utils,
assignment_notifications,lookup_routes,public_json,users_routes}.js`,
`lib/polaris/bib/{detail,holdings,search}.js`, existing hold jobs and the complete
staff shell/action/modal/grid paths it ports. This list is not permission to
infer behavior from these notes when executable source resolves a question.

## Selected CLC Package Probe

Independent fake-HTTP probe on 2026-09-12 used `Clc.Polaris.Api 4.0.0-beta.3`
and `Clc.Rest.Client 3.0.0-beta.2`; the source checkout is commit
`9a1670b798b4b546d3527fd335578e137b9a0dee` from
`clcdpc/polaris-api-csharp`. Command:
`dotnet run --project .git/asap-staff-provider-probe/Probe.csproj`, exit 0.
All requests terminate in the recording handler; no live service was called.

- A status-3/value-5 create response causes exactly one request, no automatic
  reply. The provider receives a cancellable token.
- The typed create model retains RequestGuid/status but does not map the source
  misspelling `TxnGroupQualifer`. The typed reply model lacks StatusType,
  StatusValue and final HoldRequestID fields.
- `IRestResponse.Response.Content` retains raw response JSON, including unknown
  fields and aliases. `BodyString` is the outgoing request body, NOT the raw
  provider response. The thin adapter can normalize response evidence using
  the structured JSON parser while leaving protocol/signing to CLC.
- Explicit `HoldRequestReplyAsync` with `Yes` and
  `AcceptEvenWithExistingHolds` sends answer 1/state 3 to the exact RequestGuid,
  with normalized transaction qualifiers and one additional cancellable request.
- Patron hold-list models expose separate HoldRequestID/BibID. A final ID
  injected into a fake create/reply body proves only response preservation, not
  that the real deployed PAPI server supplies that field or a GUID-to-ID mapping.
  Production capability must still be verified; the defined null-ID fallback
  remains mandatory. Neither raw HTTP success nor default typed values prove
  finalized placement.

These are package-boundary observations, not completed Slice 2 adapter tests,
live provider validation, or a reason to expand the architecture.

## Recipient Reconciliation Precision

The source paths differ: assignment uses weekly override then primary email;
purchase/additional-copy reminders use only the weekly override; weekly
recipient selection requires enabled/nonblank override/verified and sends only
that override. Do not reuse the target fallback when computing legacy recipient
or eligibility deltas. The independent real-PocketBase-shaped fixture/report
finding is recorded as S2-A1 in `slice-02-evidence.md` and assigned within the
current slice. `purchase_reminder_default` has runtime readers/writers but no
pinned migration declaration; synthetic populated-field tests explicitly add
the field to their isolated source, never infer the production schema.
