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
