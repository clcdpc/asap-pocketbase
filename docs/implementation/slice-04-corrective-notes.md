# Slice 4 Corrective Implementation Checkpoint

## Scope And State

This records the 2026-09-14 bounded corrective pass against reviewed checkpoint
`42e65cd3773cf57557e908fe79e01d77d58bac2f` on `codex/csharp-port`.
It supersedes affected implementation behavior, not historical test receipts.
It is not final Slice 4 acceptance. Astra owns final isolated publications,
native/pinned-source/browser acceptance, independent review and milestone CI.
No Slice 5 execution, Postmark integration, schema change or migration mapping
change is included in this pass.

## Confirmed And Fixed

- Staff lifecycle role/library edits preserve the locked account's active
  state. Both existing and destination scope are authorized using the locked
  actor. Explicit re-add is the activation path; its destination must be active.
- Provisioning/rebind require allowed nonempty tenant/object IDs and a nonblank
  readable UPN label. Rebind updates the tuple and normalized label atomically,
  preserves NotificationEmail, rejects inactive duplicate bindings, and allows
  own-library admins to manage only own-library staff/admin accounts.
  Successful sign-in metadata refresh also requires the original validated
  ID/tenant/object tuple. A rebind-first stale sign-in cannot overwrite the new
  label or login timestamp; rowversion fences changes after the matching read.
- Empty GUID identities do not count toward the last-usable-super-admin
  invariant or qualify for explicit/automatic assignments. Auto-claim settings
  lock requested staff IDs before dependent configuration rows and validate
  the common current identity/scope predicate. Existing unusable rules cannot
  assign new patron submissions.
- Title and additional-copy unclaim/delete decisions use locked current roles.
  Profile writes lock Organization then Staff and check participation before
  committing; their response reflects the authority serialized for that commit.
- Lifecycle title cleanup holds ordered update locks through commit, matching
  the existing additional-copy cleanup pattern. A distinct other-library admin
  cannot change a global super-admin claimant's title between cleanup read/save.
- Valid forbidden cookies retain recovery access only to session, sign-in and
  sign-out. Session returns `authenticated: true`, `accessAllowed: false`, an
  antiforgery token and no staff DTO. Protected workflow remains 403; invalid
  bindings remain 401. The UI hides the workspace and retains recovery controls.
  Sign-in explicitly requests `prompt=select_account` using the existing OIDC
  challenge properties, enabling a different-account choice under Microsoft SSO.
  See the [Microsoft authorization-code contract](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).
- Email intent readiness uses the business library, including system actors
  working in another library. Readiness remains outside SQL transactions, and
  locked resource/version checks precede commit. Suppressed purchase reminders
  no longer enqueue delivery jobs. FileEmailSender remains credential-independent.
- Settings version inputs are canonically ordered and include editable embed
  origins, system participation rows and both inherited/local branding. System
  settings and branding mutations lock Org1, enumerate/lock remaining
  organizations in order, then lock Staff; audit-only reads do not acquire the
  stronger all-organization lock set.
- Submission mail respects both system-source and library-lineage hiding.
  Sparse content still inherits independently; missing/hidden templates suppress
  mail without undoing the business submission.
- Dialog focus follows the logical title/copy opener through asynchronous
  Grid.js rendering, including replacement of a briefly rendered old row.
  The scoped observer is cancelled by newer navigation, modal, authentication
  changes or deliberate focus elsewhere; existing request/load fences remain.

## Tracked Coverage

All new .NET methods are in the existing real-SQL `PatronJourneyTests` fixture,
split into four partial files for the bounded corrective cases:

| File | Coverage |
| --- | --- |
| `StaffCorrectiveJourneyTests.cs` | Existing/resulting lifecycle scope; inactive role edits and re-add; durable identity/rebind/label/notification/cookie behavior; stale sign-in cannot overwrite a rebound label; last usable admin with historical empty GUID; unusable explicit/automatic assignees; atomic rule/title/copy/audit rollback and closed history; profile participation; real Development cookie/antiforgery/OIDC recovery |
| `WorkflowAuthorizationCorrectiveTests.cs` | Title and copy privileged unclaim/delete, demotion/deactivation/move, both serial orderings: 24 actor/lifecycle interleavings plus eight distinct-actor/global-claimant cleanup cases with actual SQL blocking observations |
| `ConfigurationCorrectiveJourneyTests.cs` | Stable versions and stale rejection for origins/participation/branding; physical-index order independence; settings/branding Organization-before-Staff locking; five submission template visibility/lineage modes with committed business state |
| `EmailScopeCorrectiveJourneyTests.cs` | Staff/admin/system-actor readiness owner across title/copy/hold intents, configured/unconfigured outcomes and provider-outside-lock proof; actual effective sender default/override/missing configuration at the readiness boundary |

`staff_forbidden_recovery_dotnet.test.js` adds frontend recovery/sign-out failure
and scoped-403 behavior. `settings_staff_access.test.js` covers the required
rebind label payload and own-library admin rebind control. Existing regression
tests are retained; existing rebind HTTP tests now provide the required label.
`staff_dialog_focus_dotnet.test.js` exercises the actual pinned Grid.js with a
held asynchronous data source: missing/replaced openers, title/copy return,
newer view/library/modal, deliberate input focus and sign-out (nine cases).
Each case has an isolated DOM/process lifetime, and render errors fail the test.

## Inspected Without Expansion

Migration identity input already requires explicit allowed durable bindings,
stores trimmed/normalized UPN metadata separately from NotificationEmail, and
never derives tenant/object identity from labels. No migration code changed.
Existing patron/outbox readiness and hold-resolution readiness already use the
business organization. The hold-completion call required an owner correction;
its accepted hardcoded confirmation content is not another template loader.
Hold and pickup authorization already used locked current roles; only their
nonempty identity predicates needed alignment. No other concrete stale-role
mutation was found in the bounded sweep.

## Local Validation

- Clean Release build with warnings treated as errors: passed, 0 warnings/errors.
- Focused SQL batches passed: lifecycle 4/4, identity/cookie 3/3, workflow races
  4/4 (24 interleavings), profile/invariant 4/4, owner readiness 6/6, template plus
  atomic cleanup 6/6, unusable assignees 2/2, version/lock/effective-mail 7/7.
- `dotnet clean Asap.sln -c Release`, then
  `dotnet build Asap.sln -c Release --no-incremental -warnaserror`: passed,
  0 warnings/errors, including the final sign-in recorder correction.
- `dotnet test Asap.sln -c Release --no-build --report-trx
  --report-trx-filename corrective-final-full-dotnet.trx`: 225/225 passed,
  0 skipped, including all 19 migration tests. This complete run preceded only
  the last sign-in recorder guard and its additional regression.
- Final sign-in guard plus existing cookie/metadata/recovery consumers:
  4/4 passed, 0 skipped, in `corrective-signin-binding-green2.trx`.
  `ReboundIdentityCannotBeOverwrittenByStaleSignInMetadata` increases the full
  suite to 226 cases; Astra's final isolated candidate gate must run all 226.
- `npm test`: all 171 discovered test files passed at the pre-focus checkpoint,
  receipt `TestResults/corrective-final-node.log`.
- That initial source freeze was lifted for the candidate browser focus failure
  recorded below. Final complete 226-case .NET, native, browser, source-oracle
  and independent review acceptance remain pending Astra.
- The first complete SQL run was 224/225, zero skips. Its sole failure was two
  new auto-claim cases sharing a barcode under an existing limit of one; each
  case now uses a distinct patron. This run is not claimed as a passing gate.
- Local command logs and TRX receipts are under `TestResults/corrective-*`.
  A skipped or zero-selected test run is not counted as a passing gate.

## Candidate Focus Correction

Astra's immutable `corrective-20260914a` candidate built with zero warnings/errors
but passed only 225/226 .NET tests, zero skips. Its staff browser journey timed
out returning focus after additional-copy unclaim and Escape. The candidate and
red receipts remain under `.git/s4-candidate-corrective-20260914a` and
`.artifacts/slice-04-candidate-corrective-20260914a/validation`.

The unchanged local SQL/browser journey passed, confirming timing dependence.
Two deterministic Grid.js cases then demonstrated the product failure:
`corrective-browser-focus-node-red.log` (opener absent during both old focus
attempts) and `corrective-browser-focus-replacement-red.log` (initial focus
lost when the old row is replaced). The existing browser/focus/accessibility
assertions were not changed.

Final local verification after the focus correction:

- `dotnet clean Asap.sln -c Release`, then
  `dotnet build Asap.sln -c Release --no-incremental -warnaserror`: passed,
  zero warnings/errors (`TestResults/corrective-focus-final-build.log`).
- `dotnet test tests/Asap.Tests/Asap.Tests.csproj -c Release --no-build
  --filter "Name=StaffBrowserJourneyRunsOnKestrelWithRealSqlScopeAndRecoveryBarriers"
  --report-trx --report-trx-filename corrective-browser-focus-final-green.trx`:
  1/1 passed, zero skipped. The real SQL/Kestrel desktop/mobile journey produced
  18 states with no serious/critical axe violations, viewport overflow or image
  failures at `.artifacts/browser/staff-dd3a73b0ed6747a0a78a6bb3fdfb4901`.
- `node tests/staff_dialog_focus_dotnet.test.js`: nine isolated cases passed
  (`TestResults/corrective-browser-focus-isolated-green.log`).
- Complete `npm test`: 172/172 discovered test files passed
  (`TestResults/corrective-focus-final-node2.log`). The command prepended the
  existing `C:\Program Files\Git\usr\bin` to PATH so the legacy grep/true
  regression also executed successfully.
- The prior Node run remains in `corrective-focus-final-node.log`: one new
  fixture failed during scheduled Grid.js work. Process isolation was added at
  that checkpoint and render errors remained failures. A later isolated failure
  established an additional within-case setup race, diagnosed below; isolation
  alone was not sufficient. No existing test was weakened.

That source was re-frozen for Astra's candidate `corrective-20260914b`, which
passed 226 .NET tests (138 integration, 88 unit, including 19 migration), 172
Node test files, zero-warning Release, four native fixtures and 12 published
browser modes (103 states and 14 CSP cases). These are historical candidate B
results, not certification of the following Terra Pass 1 correction.

## Terra Pass 1 Correction

Terra's P1 lifecycle finding was confirmed for a global super-admin claimant
and a distinct administrator in an out-of-resulting-scope library. Same-library
ordinary targets already serialize at their shared Organization lock. System
auto-claim administration also shares Org1 with global-SA lifecycle; no adjacent
rule-query change was required.

`StaffLifecycleService.ChangeLifecycleAsync` now reads affected open titles
with `UPDLOCK,HOLDLOCK` and stable ID order, matching additional-copy cleanup.
The existing invariant lock, Organization/Staff/dependent ordering, rowversion,
atomic rule/title/copy cleanup, events/audit and closed history are preserved.
No general retry, schema change or lock-order redesign was introduced.

`GlobalStaffCleanupSerializesWithOtherLibraryTitleMutations` adds eight real-SQL
data rows: deactivation/demotion, other-library unclaim/reassignment, and both
serial orderings. Lifecycle-first waits on a matching copy cleanup row after
reading titles; workflow-first waits at a real title-event insert. SQL DMV
observations prove the competing operation blocks, not merely that it is slow.
The assertions cover controlled stale versions, valid final claims, exactly one
event, cleanup and audit counts, and unchanged closed title/copy history.
The previous 24 actor/lifecycle interleavings remain unchanged.

Executed SQL/build receipts under `TestResults/`:

- `corrective-terra1-cleanup-red2.trx`: eight executed, four passed/four failed,
  zero skipped. All four lifecycle-first rows reproduced an actual
  `DbUpdateConcurrencyException`; workflow-first rows passed. Both operations
  were drained after releasing barriers. The earlier zero-selected filter is
  retained but is not counted as validation.
- `corrective-terra1-cleanup-green.trx`: eight/eight passed, zero skipped.
- `corrective-terra1-lifecycle-regressions.trx`: 26/27 passed, zero skipped.
  Its sole failure exposed an existing administration test seeding ordinary
  staff before lazy bootstrap. The existing `CreateClient()` call now precedes
  seeding in `AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole`;
  no helper or assertion changed. The red receipt is retained.
- `corrective-terra1-admin-standalone-green.trx`: the exact administration test
  passed standalone, one/one, zero skipped.
- `corrective-terra1-final-clean.log` and `corrective-terra1-final-build.log`:
  `dotnet clean Asap.sln -c Release`, then
  `dotnet build Asap.sln -c Release --no-incremental -warnaserror` passed with
  zero warnings/errors.
- `corrective-terra1-lifecycle-final-green.trx`: final 26/26 passed, zero
  skipped, using the lifecycle/role/scope/super-admin/reopen/global-cleanup
  filter against that clean Release build. The full suite is now 234 cases;
  its complete candidate C run remains Astra-owned and pending.

The final focused .NET commands were:

```powershell
dotnet test tests/Asap.Tests/Asap.Tests.csproj -c Release --no-build --filter "Name=AdministrationAuditAndSystemSettingsHttpScopeRespectCurrentStaffRole" --report-trx --report-trx-filename corrective-terra1-admin-standalone-green.trx
dotnet test tests/Asap.Tests/Asap.Tests.csproj -c Release --no-build --filter "Name~Lifecycle|Name~StaffRoleEditsAuthorize|Name~StaffRoleContraction|Name~StaffScopeContraction|Name~SuperAdmin|Name~AdditionalCopyReopen|Name~GlobalStaffCleanup" --report-trx --report-trx-filename corrective-terra1-lifecycle-final-green.trx
```

## Focus Fixture Follow-Up

The final local Node run was 171/172, retained in
`corrective-terra1-final-node.log`. Its isolated `replacement-user-focus`
child failed in genuine Grid.js pagination, then timed out waiting for the
replacement render. Repetition reproduced the failure on run two in
`corrective-terra1-focus-stack-diagnostic.log`. No application/vendor files
were modified for diagnostics.

The stack identified pagination's `beforeProcess` callback receiving undefined
pipeline data. `corrective-terra1-focus-pipeline-diagnostic1.log` showed the
intercepted array was valid, but unclaim started while Grid.js still held
pre-claim `Unclaimed` rows. The old opener plus application success message
did not establish completion of the preceding filter or claim render; a focus
frame did not settle that asynchronous pipeline either.

The test now waits for observable data completion: a separately claimed task
must disappear after filtering, then the selected row must show the current
claim label and `mine` state (or be filtered out with task 92 still rendered),
with replacement opener identities. The held update asserts owning grid,
array data and exact unclaim rows. No longer sleep, error suppression, vendor
patch or product change was added. The final render/error/focus assertions and
all nine navigation/authentication/focus cases remain intact.

Final local focus/Node verification:

- `node tests/staff_dialog_focus_dotnet.test.js`: all nine cases passed in
  `corrective-terra1-focus-setup-green1.log`, then 12 complete repetitions
  passed 108/108 cases with zero Grid.js errors/assertions in
  `corrective-terra1-focus-setup-repeat.log`.
- Complete `npm test`, with existing `C:\Program Files\Git\usr\bin` prepended
  to PATH: 172/172 discovered test files passed, including all nine focus
  cases with zero Grid.js errors, in `corrective-terra1-final-node2.log`.
- `git -c core.safecrlf=false diff --check`: passed.
- All owned test/build sessions ended; `dotnet build-server shutdown`
  succeeded (`corrective-terra1-build-server-shutdown.log`).

Implementation source is re-frozen for Astra's fresh candidate C full 234-case
.NET/Node/publication/native/browser gates and the retained Terra full Pass 2.
This is a corrected local checkpoint, not final Slice 4 acceptance. All red
receipts remain retained; no commit, push, branch or Slice 5 work was performed.
