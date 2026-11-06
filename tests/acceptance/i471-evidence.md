# #471 acceptance evidence and integration

Baseline: PR #477 remote HEAD `854b51270fc4f97335a82f8f3dabf24723465065`,
resolved through GitHub and `git fetch origin refs/pull/477/head` on November 6,
2026. This is an independent tooling branch, not qualification of later #477
commits. Source `src/` and `database/` are unchanged.

## Criterion to evidence

The suite has **10 required scenarios**: nine write scenarios and one read-only
empty-catalog probe. Scenario results, failed receipts and actual revisions are
recorded by the runner, not inferred from this map. Existing tests listed here
are complementary evidence; their presence does not claim they ran in a receipt.

| Criterion | New `i471` runner evidence | Retained lower-level evidence / boundary |
| --- | --- | --- |
| #461 all five hidden identities; creation, rename/reorder, references, history, save/reload | `identities-and-recovery` reuses the extracted native format/field journey. Collision-seeded format/field; duplicate select labels; publication identities; template preview; real combined save; complete request/history/claim SQL digests unchanged. Normal identifier labels absent. | `settings_hidden_identities.test.js` covers generated collisions, deletion/re-addition and serialization across the full inventory. `FormatRuleDraftIdentitiesSurviveCombinedFormatAndFieldCreationThroughSqlAndReload` verifies exact SQL rows, selections, retained events, reset/owned history. Broader System creation/uniqueness matrices remain here. |
| #462 available/exhausted, repeated activation, remove/re-add, focus/dirty/persistence | `add-rule-system` and `add-rule-47106` exercise live SQL-backed editors, restore a removed format's rule once, preserve a sibling draft, save/reload in both scopes. | `format-rules.test.cjs` retains the shipped-DOM multiple-available-format and material-removal transitions, independent expansion and keyboard matrix. |
| #462 empty state | `empty-format-prerequisite` injects an empty **GET projection**, checks explicit prerequisite, disabled/repeated activation, no rule and unchanged real SQL. | Built-in formats are normally present in the DACPAC seed. This is a controlled empty-input UI probe, **not** evidence of an ordinary zero-format SQL configuration. Retained shipped-DOM empty-state assertions remain. |
| #463 scalar, template field, list/set, item ownership; equal pins, false/zero/blank/empty; resets and later defaults | `inheritance-persistence` checks raw presence, equal scalar pin before/after a System change, explicit scalar/template/list/item resets, later System following, and owned format/field/template identities and versions. `inheritance-interactions` reuses scalar/template comparison and mobile reset/Discard journey, with zero Library POSTs. | `DirectLibraryInheritancePreservesEqualPinsNullableBlanksAndExplicitResetBoundaries` covers the larger SQL nullable-column/provider/template matrix, sparse unrelated saves, real stale version and owned preservation. `LibrarySettingsProductionGetEditorPostPreservesOverrideAndOwnedMetadata` covers reset-only lineage, sibling pins, retired rows and tools. `settings_direct_inheritance.test.js` covers statuses, tools, System-only/library-only behavior and comparison safety. The full ownership inventory remains `docs/implementation/i471-settings-editors.md`. |
| #463 combined format-plus-rule regression | `identities-and-recovery` creates format, field and rule in one actual transaction and checks the old rule did not move; SQL audit delta is exactly one. | Retained `FormatRuleDrafts` native test checks physical rule identities, exact history and ownership after reset. No duplicate JavaScript SQL matrix. |
| #461/#465 compact creators/publications; actions, Save/Cancel/Discard, focus and reflow | `compact-rows-system` and `compact-rows-47105`: edit, move, add/delete creators; publication rename/reorder and stable IDs; Save/reload; Cancel/Discard with no writes; desktop, 390px mobile and verified native 200% geometry and PNGs. | Existing `settings.cjs` Common creator journey and `staff-native-zoom.cjs` remain intact. Light/dark visual judgment and screen readers remain human work. |
| #466 multiple blockers, exact owners, both drafts, no navigation writes, resolve/retry, stale/retired owners | `draft-blockers` reuses `settings-draft-blockers.cjs`, including unavailable Staff recovery, equal-display pending reset, rule/template destinations, real rejected Staff updates, uncertain Staff commit, stale context and retired/replacement owners. Native 200% uses the existing authenticated acceptance context. | `settings_draft_destinations.test.js`, `staff_draft_scope.test.js`, and retained browser/SQL metadata journey cover all supported owner kinds and identity/scope races beyond these representative paths. No native arbitrary identity headers are transplanted into acceptance. |
| Failed save, real conflict and committed response loss | Identity journey: two real 400 transaction rejections, unchanged accepted Settings/version, stable retry payload/IDs; forward a real committed POST then lose only the response; inert uncertain UI, no automatic replay, guarded authoritative reload, one SQL audit. `real-conflict`: competing actual SQL write, stale 409, truthful authoritative refresh, no losing audit and explicit retry. Staff blocker journey preserves rejected/conflicting drafts through its owning recovery flow. | Settings' existing stale-409 contract refreshes accepted values with a warning; it does not retain a losing global draft. `settings_save_completion.test.js` and `settings_mutation_scope_race_dotnet.test.js` retain late/session/concurrency recovery matrices. |
| Automated accessibility and artifacts | Checkpoints enforce zero serious/critical axe findings, loaded visible images and no overflow. Runner blocks external browser requests, page errors and unexpected native dialogs; captures reverify app/SQL/reviewer. | Human screen-reader announcements, visual acceptance, shared dark appearance and usability judgment remain #398/#456. Actual Entra, deployed-host and live-provider checks remain separate. |

## Disposable write boundary

Normal runs may exercise any operation/method within the verified local app and
SQL database. Scenario `writes` metadata selects required skips in `-ReadOnly`;
it is not a second permission system. Read-only browser writes are aborted. All
scenario setup writes use browser fetch with the application's session and
antiforgery, so they pass through the same guard. Deliberate response-loss
interceptors reverify immediately before `route.fetch`, which bypasses ordinary
route chaining. They never fabricate a successful commit.

The runtime verifies immutable app/tooling identities, Testing/Local mode, SQL
Server 2022 Developer at `localhost,1433`, `AsapReview`, the runtime SQL login,
Review fixtures, deterministic provider readiness and captured email. Only the
artifact directory is mounted. No shared database, host checkout or published
ports are used. Containers and temporary browser profiles are cleaned up after
success or failure; failed receipts remain. No published ports describes inbound
exposure, **not outbound isolation**. Browser requests are restricted to the local
origin; provider credentials/data are synthetic and Polaris is deterministic.
Other container processes retain outbound capability. This is trusted-code
acceptance tooling, not a hostile-code sandbox or new firewall project.

I471-only SQL fixtures create seven small independent libraries. The read-only
evidence endpoint adds fixed counts and SHA-256 digests for those libraries;
it exports no Settings document, credentials, raw history or email body. Shared
native helpers may retain payloads in memory for assertions; the suite exports
only selected identity/status/geometry witnesses. Cookies, antiforgery and native
Playwright network archives are never exported.

## Running and integration

From this tooling worktree, run against a full immutable application SHA:

```powershell
.\scripts\review\Invoke-AsapReview.ps1 -Commit 854b51270fc4f97335a82f8f3dabf24723465065 -Suite i471
```

`-ReadOnly` runs the empty-catalog probe and reports the nine write scenarios
NOT RUN, so acceptance is incomplete (exit 2). No extra allow-write flag is needed.
Each launcher invocation records application/tooling SHAs and tooling digest,
with per-scenario JSON, screenshots and sanitized failures under
`$HOME/ASAP-Acceptance/`. Failed runs must be kept alongside passing reruns.

The #477 owner should cherry-pick the local implementation commits, resolve
overlaps in the runner/registry/docs, tooling snapshot inputs and shared browser
helpers, then run `i471` on the settled combined source/tooling revision. Reuse
the existing full frontend/.NET/SQL/browser CI gate for final qualification;
this branch neither changes workflow policy nor starts CI. Older receipts do
not qualify the final combined commit. Do not merge, deploy, tag or close issues
as part of integrating this tooling.
