# Staff UI refinements (#378–#384)

Stacked implementation on PR #344 (`codex/staff-next-parity`), starting at
`d8a59b3c9d704b23f8023844d3dc5c1d2a0840b4`. The implementation branch is
`codex/staff-ui-refinements-378-384`. The seven current issue descriptions and
discussions are the acceptance contract; #382's current placement below the
bibliographic fields supersedes its original placement proposal.

## Requirements and coverage plan

| Issue | Existing owners and bounded change | Required regression evidence |
| --- | --- | --- |
| #378 | `settings-domains.js`: retain provider identity in row state, generate a key only on Add, remove its visible editor. Existing administration persistence and override ownership remain authoritative. | System add/edit/reorder/save/reload; key collisions; library override/reset; research provider execution. |
| #379 | Patron `html.js`, `form-ui.js`, `submit.js`; `PatronConfigurationService`; DACPAC postdeployment defaults. Escaped DOM text with preserved newlines and safe HTTP(S) links. Reconcile only byte-exact known seeded HTML, including library copies. | Newlines/paragraphs, markup and URL escaping, duplicate tokens/aliases, real SQL exact-default reconciliation/idempotence/custom preservation, System/library round trips and patron rendering. |
| #380 | Small patron-only field picker in Settings, using actual patron rendering capabilities and existing scope/draft lifecycle. Email token catalog and helper remain separate. | Field-specific catalog, saved caret/selection/focus, keyboard/Escape, disabled/readonly/replaced targets, scope changes, dirty/save/reload, responsive popup. |
| #381 | Shared staff field/grid styles and existing `labeledInput`; applicable patron field styles. Audit mixed rows and helper placement before changing layout. | Browser geometry for date/select/text/number and wrapping labels/help/errors; native date behavior, narrow layout and native 200% zoom. |
| #382 | `suggestion-controller.js`, `research.js`: compact action below bibliographic fields, Title-only opening context, existing selected-title/verified-BIB application. | Empty/repeated opens, cancel, search-only drafts, selected result, delayed/stale response and ownership regressions; creation persists verified BIB. |
| #383 | `title-detail.js`, `request-editor.js`, `request-workflows.js`, existing detail host. Compact context, three tabs, one edit form and persistent footer. | Create-to-detail, drafts across tabs/search, clean/dirty/saving/error/revert, permitted actions, delayed saves/navigation/session/scope, long activity, keyboard/focus/footer geometry. |
| #384 | `queues.js` and existing workflow/research ports: eight real columns, title primary sort, row delegation plus title link, isolated searches/patron plus/workflow actions. | Grid search/filter/sort/pagination/counts, live focus return, independent nested actions, authority and stale projections, desktop/narrow/zoom/axe. |

## Ownership and constraints

Settings owns scope, population, save and domain drafts. `settings-domains.js`
owns provider collections. SQL owns persisted defaults and sparse overrides;
the DACPAC remains the only schema/deployment owner. Patron rendering supports
barcode/PIN labels in the fields that explicitly call its interpolation helper;
duplicate data tokens are resolved by the suggestion service. Status labels and
barcode/PIN labels themselves are not interpolated editors.

Suggestion Controller owns patron/configuration reads, the form and selected BIB.
Research owns the disposable Polaris dialog. Title Detail owns the authoritative
request/version and mutation recovery; Request Editor owns its draft; Request
Workflows owns actions. Detail Host owns the exclusive modal lease and focus
return. Queue owns accepted rows, Grid.js and filters. Navigation/router/session
retain departure, history and actor authority. No parallel editor or workflow
controller is introduced.

The #301 patron-code decision, #292 human email accessibility acceptance,
live Polaris/Postmark acceptance and test-IIS activation stay with their existing
owners. No parent edits, merge, release, deployment or issue closure is part of
this implementation.

## Validation record

Passed, failed and unavailable checks must be distinguished. Final counts come
from fresh discovery, preserving the parent's 1,427 cases (1,410 main and 17
browser) and 57 frontend test files, plus deliberate additions.

### Checkpoint A

- Ordinary restore and Release build passed, zero warnings/errors.
- Exact-seed reconciliation real-SQL/DACPAC test passed (1/1), including
  System/library copies, custom/case/space/blank/NULL preservation, imported
  format message seeds and repeat-deployment rowversion stability.
- Extended Campaign 2 real-SQL/Kestrel browser cases passed (2/2 roles):
  provider add/update/reload with stable IDs/keys, literal newline/token saves,
  sparse library overrides and actual patron rendering. Four existing email
  editor scans remain; three patron/picker scans were added per role. Zero
  serious/critical axe findings, overflow, visible image failures, page errors
  or external requests. The first run caught Bootstrap's low-contrast link
  color; scoped patron message links were corrected and both roles reran green.
- Patron rendering/picker and provider-domain/accessibility/template frontend
  files passed. The initial full frontend run encountered three assertions
  tied to removed provider inputs/wrapping labels; updated behavioral checks
  pass individually. The full final suite remains required.
- All editable PatronSettings defaults were audited: four HTML-bearing messages
  changed; page/login/form/no-email text, barcode/PIN and status labels were
  already plain text. System availability and workflow message defaults were
  also plain text. Email templates and stopped-source migration provenance
  remain unchanged. Nonblank multiline messages retain boundary whitespace
  through both Settings save paths; existing blank-to-inheritance semantics
  are preserved.

### Checkpoint B

- Native date controls exist in Suggestion and Request Editor. The mismatch was
  caused by stretched, independent label grid tracks beside multi-line help.
  Shared three-track label/control/help subgrids now align mixed controls and
  wrapped labels; pickup help is grouped beneath its select. Settings scalar
  fields use the same track pattern; narrow layouts preserve source order.
- Polaris now opens from a compact action below bibliographic fields. Each
  opening uses the current Title only. Selection retains the existing BIB
  verification and Title reconciliation, preserving Author/Identifier drafts.
- Release build passed without warnings/errors. Research, suggestion controller,
  suggestion UI, quick-create and Settings accessibility frontend files passed.
  The real-SQL/Kestrel quick-create browser case passed (1/1), including actual
  date/pickup geometry, date/select/text/number mixed-field probes with wrapping
  labels/help/validation, narrow stacking, blank and repeated search openings,
  cancellation and unchanged creation/claim/pickup behavior. Existing axe,
  image, exception, traffic and overflow guards remain active.
- Permanent controller coverage additionally verifies selected-title-only
  population, retained verified BIB after Cancel and rejection of a delayed
  selection after the suggestion owner is replaced.

### Checkpoint C

- Title Detail keeps its existing authoritative controller, editor drafts,
  workflow commands, detail host and mutation recovery. The modal now has a
  compact contextual header/patron strip, Details/Activity/Related tabs, one
  scroll pane and a persistent footer. Editable Format/Identifier/BIB/publication
  summaries were removed; native LEAP links remain in compact research tools.
- Clean/dirty/saving/rejected states are distinct. Save is disabled when clean;
  Claim remains an explicit action. Revert affects only the editor draft. Tab
  selection survives authoritative saves, and hidden delayed assignment/pickup
  reads cannot change the selected tab or steal focus.
- Release build passed with zero warnings/errors. Controller, action and owner
  replacement frontend files passed, including tab drafts, delayed success,
  validation failure, actor replacement and concurrent inline drafts.
- The real-SQL/Kestrel request-contract and creation-handoff browser cases
  passed (2/2): rejected saves preserve SQL versions, accepted saves preserve
  custom-field history, tabs/search/Revert preserve drafts, and a forwarded
  delayed real save blocks close until its response arrives. Footer geometry
  establishes that the final field is not covered; desktop/mobile axe and
  existing traffic/error/image/overflow guards pass.
- Broader workflow/LEAP coverage is running with a new 80-event SQL history
  fixture. Its first workflow run reached an obsolete Author auto-population
  expectation; that assertion now verifies the explicitly required retained
  Author. The complete browser and frontend suites remain final gates.

### Checkpoint D

- The queue has eight actual Grid.js columns. Barcode/name and Title/Author are
  combined; the obsolete standalone Patron, Timing, Tags and Open columns and
  sort structure were removed. Bibliographic sorting uses Title. Warning tags,
  related counts and native LEAP links remain available in the combined cells.
- Title is a real link. Background row clicks open that link's request; native
  links, search buttons, patron plus and action controls retain independent
  behavior. Title/Author shortcuts use the existing Polaris controller in an
  explicitly read-only context. List actions open fresh authoritative details
  and dispatch existing workflow handlers; no mutation implementation was copied.
- The real Grid.js unit journey covers 55 exact bigint identities, independent
  shortcuts, Title sort, paging retained after newly rendered refresh data,
  Author/Identifier search, claim/tag filters and detached-owner rejection.
  A focused stale-search assertion was corrected during independent review to
  inspect the actual search count, avoiding a false-positive open-count check.
- Narrow tables retain all controls in a labeled keyboard-scrollable region.
  The real SQL browser fixture now contains 55 additional requests and checks
  actual shortcut POST modes, a delayed old search, unchanged request versions,
  row/title/menu isolation and list Purchase/Reject/Claim/Unclaim outcomes.

### Checkpoint E review corrections and evidence in progress

- Independent settings review found that Add after removing a persisted draft
  provider could reuse its key and library overrides. Add now reserves accepted
  System keys as well as draft keys; a delete-then-add regression passes.
- Independent workflow review reproduced lost focus after canceled More actions
  and Additional-copy cancellation. The visible More summary now owns return
  focus. It also reproduced footer clipping under actual 200% browser zoom with
  long context. Context and tabs now share the existing middle scroll pane;
  the title and Save/Revert footer remain in the frame.
- Independent migration review identified stale current-target seed expectations
  in the no-System-source verifier. Those four expectations now match the new
  DACPAC defaults. Raw imported settings and legacy format/default oracle pins
  stay unchanged. A narrow read-only four-seed presentation mapping gives newly
  imported targets plain-text editor/config messages immediately; it neither
  rewrites imported evidence nor normalizes arbitrary custom HTML.
- SQL cleanup additionally checks DATALENGTH, preserving a customized seed with
  a trailing NUL as well as case, space, blank and NULL witnesses. The new real
  SQL/HTTP presentation test covers all four imported seeds, custom suffixes,
  library-owned/overridden format messages and unchanged source text.
- A pre-existing parent defect necessary for #383 was found during creation
  handoff: native textareas normalize CRLF to LF, which made accepted notes look
  dirty immediately. The editor compares against its native textarea baseline
  and submits exact original notes when unchanged. This correction is confined
  to this enhancement branch and has explicit regression coverage.
- A second inherited defect was independently reproduced with unmodified Grid
  in Chromium: refresh briefly rendered the retained page before its effects
  reset pagination to page 1. The title queue now supplies the current page on
  accepted refresh and clamps it to the new result count. Explicit filter/status
  changes still reset paging. Unit and browser assertions now await the real
  replacement rows and settled paginator, rather than transient DOM or the
  synchronous result-count announcement. The Node fixture exposes its native
  animation-frame APIs before Grid to match the browser scheduler.
- The retained Grid unit journey also waits for the vendored Rendered state and
  its configured pipeline throttle to drain between distinct fixture actions.
  It rejects vendor render errors and uses a changed response count to
  distinguish accepted rows from Grid's retained store. Separate real Chromium
  checks exercise actual refresh/filter transitions and native popovers.
- The full frontend gate exposed another inherited Grid 6.2 race: remounting
  first used its retained header, then registered Sort against a replacement
  header during pipeline processing, leaving an uncached sort step. The title
  and Additional Copy queues now publish the current header before remounting.
  The retained delayed-copy focus journey exposed the same race in its second
  owner; its error and focus assertions remain intact. This small correction
  stays on the enhancement branch. The real Grid journey pins that invalid-state
  boundary and retains observable sorting/pagination/filtering and zero-error
  assertions; it does not suppress the vendor error or change its scheduler.
- The delayed catalog regression uses distinct nonempty Title/Author results,
  awaits the held response's completion, proves Author results remain, and
  checks read-only results have no Use BIB controls. Notification assertions
  distinguish the intentional list-action intents from quick-create's explicit
  no-confirmation behavior; no live delivery is made.
- The staff format-message preview now shares the same plain-text/link renderer
  as the patron view, preserving clickable Libby URLs after the default cleanup.
  Existing preview tests now verify exact multiline text and literal malicious
  markup. Browser creation supplies a manual required Author and proves catalog
  selection retains it; independent list actions return through Suggestions
  after the preceding action follows its request to a different workflow stage.
- Final full-suite, native-zoom, publish/package and hosted-CI results will be
  recorded after execution. No pending check is represented as passed.
