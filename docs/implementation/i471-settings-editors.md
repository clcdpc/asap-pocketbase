# Settings editor improvements (#471)

Baseline: `be9c7af` on `main`, including merged #454/#438 and #420/#390.
PR #469 was open and unmerged when implementation began; its branch is not an
implementation source. All work uses `codex/i471-settings-editor-improvements`.
Unrelated edits in the original checkout are preserved in that checkout.

## Initial ownership and inheritance inventory

This bounded inventory precedes constituent implementation. Sources inspected:
Staff `index.html`, `settings.js`, `settings-domains.js`, `draft-scope.js`,
`email-template-placeholders.js`, `patron-text-placeholders.js`, staff styles;
Administration settings binding/views, queries, persistence and collection
mutations; retained Settings frontend and SQL/browser fixtures. Raw accepted
data is `stored.configuredSystem` / `stored.libraryOverride`; effective data
alone does not prove override presence. The settings version covers the whole
scope; format rows also retain their own version.

| Mechanism / ownership | Accepted/effective representation and presence | Existing edit gate / reset | Save, identity and required #463 interaction |
| --- | --- | --- | --- |
| Workflow scalar overrides | Nullable columns in the library WorkflowSettings row, merged with System/effective defaults; false is present. `WORKFLOW_FIELDS` enumerates the supported fields. | Dynamically created Override checkbox per field. Timeout dependents also require their feature parent; common-creator options require Show common creators. Reset writes null. | Scoped supplied-value patch; omission preserves. Keep feature dependencies and template ID reference. Direct edit prepares only the edited field; explicit reset previews effective system fallback, tracks presence, and submits null. |
| Patron scalar text | `PATRON_FIELDS`: pageTitle, barcodeLabel, pinLabel, loginPrompt, loginNote, suggestionFormNote, noEmailMessage, successTitle, successMessage, alreadySubmittedMessage, ebookMessage, eaudiobookMessage and ten duplicate-status labels. Raw nullable columns; multiline messages preserve nonblank source whitespace. | Dynamic scalar Override checkbox; patron placeholder tools check editability. | Same scalar patch contract. Existing normalizers treat whitespace-only text as null; do not invent meaningful blank semantics here. Direct editing, status, comparisons and explicit reset must also work through placeholder insertion. |
| Email sender (scalar, duplicated UI) | fromAddress/fromName in library EmailSettings; effective fallback to System. Two editor presentations share each value. Token is separate. | Scalar Override checkboxes synchronized between Sender and Email templates. | Keep both editor presentations and draft ownership synchronized. Reset each sender column with null, preserve false/blank rules of its contract. Never compare the token. |
| Branding alt text (scalar) | Library Branding.AltText with System fallback, alongside optional library-owned logo bytes. | Scalar Override checkbox; clear-logo is a distinct image operation. | altText null restores inheritance without clearing logo. Direct-edit/reset this scalar; preserve multipart upload and clear-image ownership. |
| Built-in template content (per-field fallback within lineage row) | Five keys: suggestion_submitted, purchase_approved, already_owned, hold_placed, rejected. Library row references system SourceTemplateId; Subject/Body independently fall back when null/blank under current delivery contract. Raw content is distinct from effective text. | One Override checkbox currently gates both fields; tools also check this checkbox. Whole-template reset removes lineage row. | Sparse Subject/Body edits must retain untouched raw columns and lineage; expose actual per-field inheritance, comparisons and explicit reset without a preliminary toggle. Group reset must not delete custom templates. A blank template column currently means fallback, not an intentional empty delivered message. |
| Additional inherited templates (per-item lineage, nullable content fields) | System templates outside built-in keys plus library overrides matched by SourceTemplateId; effective displayName/Subject/Body/Enabled; raw baselines retained. | Dynamic Override checkbox per inherited row; reset:true removes the lineage row. | Preserve templateKey, source ID, row ID/version, sort order and preview UI ID. Direct-edit/reset at supported item/field granularity; Enabled remains a real visibility control. System disabled state constrains effective visibility. |
| Additional custom templates (library-owned records) | Library IsCustom rows; no system source; rejection: keys are referenced by workflow selectors. System may also create additional system templates. | No inheritance gate; library custom rows can be moved/deleted. | Preserve owned template keys, history and rejection references through inherited resets. Keep authorized ordinary editing, no misleading inheritance status/reset. |
| Common creators (whole-list override) | `{ exists, values }` from CommonCreatorSet/Term; absent set inherits; present empty set intentionally overrides with empty list. | Use system list checkbox disables inherited rows/add; Show common creators independently hides/disables options. | Full list or explicit null; one group status/reset, direct edits/add/move/delete prepare the list override. Reset removes library set/terms, not custom fields/formats/templates. |
| Allowed patron codes (whole-set override) | `{ exists, values }` from PatronCodeEligibilitySet/Member; present empty set differs from absent. Current empty-selection runtime policy allows all. | Use system IDs checkbox and searchable token picker. Require eligible patron code is independent. | Full integer set or null; preserve unknown IDs/choice failures and stale lookup guards. One group status/reset and direct selection editing. No invented per-code inheritance. |
| Publication options (whole-list override) | `{ exists, values }` from PublicationOptionSet/Option; OptionKey is public selection identity; present empty list differs from missing override. | Use system options checkbox disables rows/add; IDs/labels/enabled/order edited. | Whole list or null. Group status/reset, direct changes prepare entire override. Retain each OptionKey internally; resetting list intentionally removes library choices, without rewriting historical request selections. |
| External providers (per-item override, nullable columns) | Effective provider with `overridden`, system ID/key; raw ExternalSearchProviderOverride by library/provider ID. Nullable enabled/label/URL columns. | Dynamic Override checkbox gates each provider; reset:true/overridden:false removes that item's override. System owns provider creation. | Preserve ID/key/sort metadata. Item status/reset and direct edits; equal-value changes must not silently remove a saved override. Library cannot create a provider. Existing feature Enabled remains. |
| Material formats (per-item override plus library-owned records) | Effective formats with ownerOrganizationId/overridden/version; raw systemOverride references materialFormatId. Library custom format rows are owned records. | Dynamic Override checkbox for shared formats; owned formats editable directly; library can add/delete custom formats only. | Keep code/ID/owner/version. Item status/reset only for shared formats; preserve custom rows and historical references. Existing Shown is feature visibility. Format rule settings share this item's nullable override columns. |
| Format-rule built-in fields (same format override columns) | Message behavior/text and title/author/identifier/publication mode/label are MaterialFormat/MaterialFormatOverride properties, not separately owned rule entities. Title remains required. | Already directly editable in accordion; no separate inheritance checkbox. Existing updates compare effective values and preserve existing equal-value raw overrides on no-op, but a change back to system can currently clear a column implicitly. | Add explicit supported field/item reset/status without fabricating separate ownership. Material formats and Format rules must coordinate edits/resets to the same override. Preserve code association, expansion state, rule roster and completeness checks. |
| Custom field definitions/options and custom-field format rules (library-owned) | PatronCustomField by library, stable FieldKey/ID, option IDs; MaterialFormatCustomFieldRule by library/format/field, including disabled historical definitions. | Ordinary library editing, no system inheritance; system disallows definitions/custom rules. | Whole definitions replacement and library-owned rule configuration. Preserve keys, selections, option IDs, references and histories. `labelOverride` here is a patron-facing rule label, not an inheritance toggle. No Use system value for library-owned definitions/rules. |
| Auto-claim rules (library-owned) | FormatAutoClaimRule with materialFormatId/staffUserId/active/order and record identity. | Library-only ordinary editing; scope-valid staff/format choices. | Preserve rules and IDs across reset; Active is a feature switch, no inheritance interaction. |
| System-only configuration | System links/icon pattern/embed origins, availability/misconfiguration messages, Polaris config/secrets/participation and Postmark token. | data-system-only controls disabled in library context; API rejects library submissions. | Retain scope labels and disabled controls; library payloads omit these keys. Comparison must never expose secrets. Participation and organization catalog are separate owned mutations. |
| Global records / other operations | Staff roster, new staff account and inline account editors; Polaris test, email test, public links, logo upload/clear. | Own drafts/mutation admissions and scope/identity guards; not Settings inheritance. | Keep authorization, versioning and admission guards. #466 may attach safe destination metadata to actual draft owners, never editable values or exposed handles. |

Global Reset inherited overrides removes scalar rows, whole sets, provider and
format overrides, inherited template rows and library Branding. It deliberately
preserves library custom formats, custom fields, custom-field rules, auto-claim
rules and custom templates. Global Save/Discard remains the acceptance boundary
for the new field/group/item reset drafts; no immediate field saves are added.
Existing explicit global-reset confirmation and audit ownership remain separate.

Identity constraints are owned by existing collection validation/SQL. #461 hides
five identifier controls, retains exact existing identities in editor state,
and allocates new valid keys once per new draft. No schema or generalized
identifier framework is needed. #462 uses those same format codes internally;
headings resolve the current configured label. #465 CSS is creator-scoped.

## Serial qualification record

Only one constituent implementation is active at a time. Pass 1 is an independent
requirements review; Pass 2 derives and executes adversarial scenarios. Two
specialists within Pass 2 are one pass, not additional cycles. Routine test
reruns and final receipt reconciliation do not add review passes.

| Issue (implementation order) | Implementation | Pass 1 | Pass 2 | Focused tests | Status |
| --- | --- | --- | --- | --- | --- |
| #461 Hidden identities/readable labels | `7be87eb`, `583de79`, `2990e78` | Luna MAX: 3 findings fixed | Astra HIGH + Terra MAX: clipping and history-witness gap fixed | Retained editor/collision tests; real SQL/browser save/recovery/history; desktop/mobile/native200 | Technically qualified; final integration pending |
| #462 Add rule feedback | `423c352`, `7b2cd22` | Luna MAX: 1 test gap fixed | Luna MAX: independent browser matrix, no findings | Retained domain/browser matrix and real-SQL Add/save/reload pass | Technically qualified; final integration pending |
| #465 Common creator rows | `a2501ad`, `49b3951`, `8fbe412` | Luna MAX: Move down test gap fixed | Terra MAX: narrow native-200 focus reveal fixed | Five rendered sizes; owning SQL/browser 1/1 pass | Technically qualified; final integration pending |
| #463 Direct edit/inheritance | `56c514d`, `72c7258`, `e66947c` | Luna MAX: empty template lineage fixed | Astra HIGH + Terra MAX: combined save, comparison expansion and mobile Discard fixed | Real-SQL ownership/history matrix; retained SQL/browser regressions; four frontend files; format/picker native zoom | Technically qualified; final integration pending |
| #466 Blocking draft navigation | `6618b70` | Luna MAX: unavailable Staff recovery and domain coverage fixed | Pending | Settings/draft regressions; eight-domain routes; retained SQL/browser identity and desktop/mobile/native200 | Pass 1 remediated; Pass 2 pending |

### #461 evidence

All five routine identifier controls are removed; exact existing keys, IDs,
owners, versions and template lineage are retained explicitly. New draft keys
use reserved UUID-based allocation, independent of display labels. Serializers
use stable selectors/state. Configured effective format labels drive headings;
duplicate readable names use positional context. Publication rows compact at
desktop and wrap at narrow widths; Auto-claim duplicate labels remain selectable
by distinct IDs. No server/schema behavior changed.

Pass1 (independent Luna MAX) found ambiguous Auto-claim labels, an incorrectly
constrained publication probe, and missing failed/uncertain-save identity proof.
All three were fixed. Pass2 used an independent identity transition matrix and
raw SQL/HTTP probes (Astra HIGH), plus actual rendered/keyboard pressure tests
(Terra MAX). Five independent invalid/stale probes returned400/400/400/400/409
with accepted GET, rows, references, versions and audit unchanged. Three unique
native cases passed. Terra found a pre-existing checkbox-width defect inside
the touched option editor; Astra found an unseeded historical-selection gap.
One final remediation fixed both, without another independent review.

Focused qualification includes the retained Settings sweep (initial13/15, two
supported corrections and passing affected reruns), the new collision/deletion
identity test, placeholder targeting, a5/5 corrected native rerun and the
initial passing System-rule case. Initial failed receipts are retained. Final
Pass2 remediation build passed with zero warnings/errors; the revised historical
SQL/browser case passed1/1, zero failures/skips. It uses real400/400 rollback and
stable retry payloads, a real committed response deliberately lost to the
browser, blocked replay/scope transfer, and guarded Refresh reconciliation.
Populated request/event rows, rowversion, raw selections and history match
before/save/reset; owned format/field/option/rule/claim/template rows match across
inherited reset. Desktop, actual360px container, mobile and native zoom2 geometry
pass with18px checkboxes, visible Enabled text, no overlap/overflow and zero
serious-critical axe findings. Human AT is not claimed.

Detailed receipts outside Git:461-results.json,461-pass1-results.json,
461-pass2-data/report.md,461-pass2-visual/report.json,
461-pass2-remediation-results.json and their raw TRX/SQL/PNG companions under
the shared evidence root below. The visual report's initial `passed` refers to
its asserted publication scenarios, not its separately reported clipping defect.

### #462 evidence

Add rule uses the existing eligible-format factory. Empty and exhausted states
disable the action and provide visible live feedback; availability refreshes
after format/rule renders, before focus restoration, and on stale activation.
Successful additions retain expansion, focus, dirty-state and stable identity
without saving or creating formats. The focused domain file and retained
Chromium matrix pass, as does the one discovered rebuilt real-SQL Add-rule
save/reload journey, including the #461 recovery/history checks.

Pass 1 (fresh Luna MAX) found no functional defect and one missing material-
format removal transition. The test-only correction passes: exhausted -> add
eligible format -> delete it before creating a rule -> disabled/explained, with
original identities/accordion and clean snapshot restored, no POST. Pass 2
(different fresh Luna MAX context) independently executed a scratch browser
matrix for empty/exhausted, one/multiple eligible, duplicate readable labels with
distinct keys, forced stale/repeated activation, rule/format removal/recreation,
System/library, drafts, focus and mobile. No actionable defect; no serious or
critical axe finding, page error, external request or implicit POST; mobile
scroll width matches viewport. No tracked instrumentation. SQL save/reload was
separate initial evidence, not claimed by this static probe. Human AT remains
separate. Receipts: `462-*` and `462-pass2/report.md` / matrix JSON / PNGs.

### #465 evidence

Creator-specific rows place the flexible input and actions together where space
permits and wrap at narrow widths. Add/move/delete retain useful focus; a
creator-only disabled-state refresh preserves first/last restrictions under the
existing whole-list inheritance gate. Shared publication/format/rule styles and
the persistence contract remain intact. Initial source qualification caught the
old boundary re-enable bug; owning fixture corrections preserved trimming,
accepted reload waits, cleanup, temporary System seed ordering and all original
screenshot identities (three desktop states added, 14 -> 17).

Pass 1 (fresh Luna MAX) found no source/CSS defect and one gap: Move down was
reachable but never activated. The added interior move/order/focus test and its
restoration passed the actual SQL/browser case. Pass 2 (Terra MAX) independently
measured desktop row height 139.09 -> 88.09px versus the prior baseline, with
publication geometry unchanged, and executed long-list keyboard/scope/zoom
pressure. One narrow native-200 fallback put the focused input at the viewport
edge, hiding its label and top focus ring. Final remediation reveals only the
creator input fallback's row within measured visual-viewport/outline/save-bar
margins. It preserves the focus target and action order.

Final source fixture passes desktop, mobile, wider native-200, new 1000x900
native-200 (492x402 CSS pixels), and retained native-400 configurations. The new
boundary Move-to-first and Delete-last regressions prove label/full-ring
containment, exact row/value/order, no overflow and zero serious/critical axe.
Fresh Release build has zero warnings/errors; owning SQL/browser case passes
1/1 with Save/Discard and System/library round-trips. Initial failed receipts
and independent findings remain outside Git. Final receipts:
`465-pass2-final-source*`, `465-pass2-build.log`, `465-pass2-final-sql*`;
independent comparison at `465-pass2/report.md`. Static/mocked visual checks do
not claim SQL or human AT; separate actual SQL receipts support persistence.

### Inventory clarifications before #463 implementation

Runtime resolution in `PatronConfigurationService` confirms that provider and
format nullable override columns use null fallback, while scalar text has its
own blank/whitespace normalizers. Preserve accepted raw values on unchanged
fields rather than writing back display normalization. Compare against the
actual inherited/effective System default when raw System values are null;
the current library effective value is not that default. Meaningful raw blanks,
false and zero must follow their owning contract, never generic truthiness.
Built-in format-rule properties share the format override row; a reset must
coordinate both editors and preserve library-owned custom-field rules.

Profile's Weekly summary email override is a per-account preference managed by
`profile-controller.js`, outside System/library Settings inheritance. It remains
an intentional non-inheritance control, like library-owned custom-field label
rules, genuine feature switches and global staff records.

### #463 implementation ownership reconciliation

The initial inventory was rechecked against the live issue, current markup,
controllers/tools, raw Administration snapshots/writers and patron runtime.
No additional System/library inheritance mechanism was found. The resulting
editors use explicit draft intent; no hidden checkbox stores inheritance.

- Workflow, Patron, duplicated sender and branding-alt scalars are directly
  editable. Presence comes from accepted raw columns. Only actual edits or
  explicit resets are submitted; population, focus and tool opening are clean.
  Timeout and Common creator feature dependencies still control their dependents.
- Built-in Subject/Body and inherited additional-template content retain their
  independent nullable fallback, source identity and raw/native-newline baseline.
  Each supported field has status/reset; additional lineage also has an item
  reset. Placeholder insertion uses the same edit path and resets retire picker
  targets. Custom templates remain owned records.
- Publication options, Common creators and patron codes have one status/reset
  per whole list/set. Direct add/edit/move/delete prepares that group's override,
  including explicit empty sets. Reset confirmation explains replacement of
  library choices and preservation of historical request selections.
- Providers retain item and nullable-column ownership. Shared material formats
  expose label/visibility and item resets; built-in format-rule properties expose
  column resets. The two format editors share one intent map and item reset,
  while library custom-field rules remain separate owned data. Owned formats,
  fields/options, templates and auto-claims receive no inheritance affordance.
- Per-account Profile weekly-summary preferences, custom-rule patron-facing
  `labelOverride`, actual feature switches, global Staff records and System-only
  controls retain their distinct contracts. Library payloads omit System-only
  configuration and comparisons never include credentials.

The existing provider/format contract accepts a narrow `pinOverrides: true`
intent with supplied columns. It distinguishes a real equal-value pin from an
unchanged effective echo; null remains an explicit column reset. Frontend writes
are sparse in both scopes, preserving untouched accepted nulls, blanks,
whitespace, false and zero under their owning contracts. No schema, setting or
scope model was added. Safe `systemValues` in the existing GET supplies actual
runtime defaults when raw System scalar columns are null; current Library
effective values are never used as the System comparison.

Ownership intent participates in dirty/load/confirmation snapshots and draft
stamps, including equal-display resets and logo admission. Reset previews remain
unsaved until Global Save succeeds; existing failed/conflicted/uncertain outcome
and explicit follow-up refresh ownership are retained. Global Discard replaces
the whole accepted snapshot. The separately confirmed Global Reset is unchanged.

Initial focused evidence (before independent Pass 1): Release builds are clean;
the retained Settings frontend selection and new ownership matrix pass. Three
discovered native cases pass: equal-pin/nullable-blank/future-System SQL matrix,
production metadata GET/editor/POST, and the populated historical identity and
uncertain-save recovery witness. Expanded qualification and final receipts are
recorded after the current focused runs; initial failed receipts are retained
under the external `463-*` evidence paths. Human visual/screen-reader acceptance
is not claimed by automated axe, geometry or browser screenshots.

Pass 1 (fresh Luna MAX) identified one supported P2: edit then field-reset on an
inherited template with no accepted Library lineage could create an empty row.
The single remediation omits absent-column resets while retaining explicit
sibling pins and all accepted-row reset semantics. Additional-template item
status now derives pending ownership from real pins, not merely the presence of
an intent object. A related mouse activation issue in that same flow was fixed:
blur refresh no longer moves an already-mounted template status/reset node.

Focused red/green proof uses the existing production metadata Browser case,
without adding a Browser identity. Red observed two physical empty lineage rows
(built-in and additional); green passes 1/1 and verifies no lineage rows, later
System fallback/status, sibling Body pins, exact accepted row IDs/versions/raw
CRLF content, and preservation of the custom template. The direct-domain and
retained template editor files pass. Both Release builds have zero warnings or
errors. Receipts remain external: `463-pass1-domain-red.log`,
`463-pass1-red/463-pass1-red.trx` (mouse target witness),
`463-pass1-sql-red/463-pass1-sql-red.trx`,
`463-pass1-green/463-pass1-green.trx`, matching build/domain/template logs and
`463-pass1-discovery.json`. These are focused remediation checks, not another
independent review or a claim of final integration qualification.

Pass 2 paired fresh Astra HIGH persisted-state verification with fresh Terra MAX
rendered interaction scenarios. The independent SQL matrix completed 12 cells;
its second test exposed the combined-save failure with exact rollback hashes.
Terra recorded 17/18 desktop assertions and 18 clean layout/accessibility scans,
with the two interaction failures below. Both continued the same unfinished
pass after a usage reset; no third review was started.

Pass 2 identified three supported P2s. A first shared-format Label edit and a
built-in Author edit now reuse the same pending EF override inside Global Save,
preserving its atomic transaction and existing validation/version checks.
Status refresh retains long comparison expansion for scalar, template and
dynamic field/group presentations. A footer pointer gesture retains its resolved
static position while focus enters an action, preventing mobile Discard from
moving between pointerdown and pointerup; keyboard, caret and picker rules remain.

The retained SQL matrix now submits both editors' writes together and verifies
one physical row with both values, identity/version, actor/section audit metadata,
reload behavior and unchanged owned format/custom-field rules. Its red run
reproduced SQL2627. The production metadata Browser journey now types with both
long references expanded and exercises the first mobile Discard click, Cancel,
and Accept with two drafts. Red recorded both closed comparisons and the 92px
footer shift; green retains focus/expansion, confirms on the first click, restores
both accepted values and sends zero Library POSTs. Six actual browser scans have
zero serious/critical axe findings, overflow, missing images, page errors or
external requests. The retained format/picker native-zoom fixtures each pass.

Focused receipts remain external: `463-pass2-red/463-pass2-red.trx` (both cases
failed), `463-pass2-green/463-pass2-green.trx` (SQL case passed; the new Browser
assertion initially checked before asynchronous Discard population completed),
and `463-pass2-green2/463-pass2-green2.trx` (Browser case passed after awaiting the
existing accepted-state completion signal). All failed evidence is retained.
The direct-domain, template-editor, staff-scope and accessibility-layout frontend
files pass; both Release builds have zero warnings/errors. Final discovery is
`463-pass2-final-discovery.json`: both focused identities are retained, no new
Browser method or temporary independent probe remains. `463-pass2-remediation.json`
records the exact candidate, counts and receipt selection. Broader integration
qualification and human assistive-technology acceptance remain separate.

### #466 initial implementation and evidence

Admission enumerates all actual competing owners and retains its first-kind
contract for other controllers. Optional owner metadata remains internal. The
Settings warning lists individually actionable, readable destinations outside
the inert form: new account, exact roster position, and actual dirty fields or
groups within the single global Settings owner. Account identities, editable
values and opaque owner handles never label the actions. Staff records retain
their global-record ownership; no setting, inheritance scope or schema changed.

Blocked actions focus the first navigation control. Activation re-admits the
original consumption/preservation declaration and checks current identity,
scope/generation, mounted owner and destination before using existing section
and disclosure controls. It reveals inactive rows through the roster display
preference and announces the focused destination. Draft values and explicit
pin/reset intents are preserved; navigation performs no mutation. Retired,
unavailable and uncertain owners receive a current recovery explanation and a
focusable reload path. A disconnected turn retires a handle before reinsertion;
synchronous mounted roster reorders keep their owner. The existing PUT-only
roster preservation declaration is unchanged. An explicit accepted reload
replaces a retired row with a fresh owner; preserving refreshes and former
controls cannot grant that new handle to the retired row.

The retained Staff Access file now covers a main Settings field blocking a
roster save, three simultaneous owners, exact row/new-account focus, hidden
inactive rows, failed/uncertain blocker saves, identity replacement, disposal
and detach/reinsert retirement. The retained draft-scope file checks complete
enumeration, preservation exclusions, first-kind compatibility and retirement.
Both the initial and final-source Settings selections pass 16/16 current files.
The final draft-scope file passes. The earlier shared draft selection passes
3/3 files, including 271 navigation/draft journeys; the focused Staff Access
rerun also passes after adding explicit fresh-owner recovery coverage.

The existing production metadata Browser identity passes 1/1 with real SQL:
Workflow Save -> exact Staff editor, multiple owner resolution -> original
retry, a real 409 followed by committed-response uncertainty/reconciliation,
same-section lifecycle retry, an equal-display pending reset -> explicit null
save, exact rule/template disclosure routes, stale scope actions and retired
row recovery followed by a real fresh-owner save. Seven
destinations have measured focus. Four new desktop/mobile scans and the six
retained scans are clean; two native 200% states measure 632x402 and 492x402 CSS
pixels with full focused-email containment, no CSS/pinch zoom and clean axe,
images, page/external-request and overflow guards. No Browser identity was added.

The final Release build has zero warnings/errors. Native discovery remains
1486 current tests, with the retained metadata Browser UID unchanged. No new
Browser identity or category entry was added. The final real-SQL/browser run
passes 1/1, zero failures/skips; the earlier run also passed, without being
counted as another distinct case.

Receipts remain outside Git under `466-*` in the shared evidence root:
`466-settings-final.json`, `466-draft-frontend.json`,
`466-draft-scope-final2.log`, `466-final/466-final.trx`,
`466-final-browser/settings-browser-results.json`, the PNG companions,
`466-build-final.log` and `466-final-discovery.json`. Initial failures and the
red/green retired-owner recovery receipts are retained. The first native PNG
capture was blank despite passing geometry and is not rendered visual evidence;
the passing corrected CDP rerun supplies visually inspected captures. Human
assistive-technology acceptance and both independent review passes remain
separate; this record does not self-approve the issue.

### #466 Pass 1 remediation

Fresh Luna MAX found one P2: a still-owned dirty Staff row omitted by a
preserving roster refresh explained Staff recovery but focused overall Settings
reload. Its current action now opens Staff access, expands that retained row,
focuses Cancel and announces that Cancel discards only the retained Staff draft.
Navigation preserves both drafts and keeps Save disabled for the unavailable
account. Owner/context/admission checks still reject retired or foreign owners;
the overall Settings reload target remains for the corresponding stale or
uncertain recovery messages. No admission or preservation declaration changed.

The owning real-SQL/browser regression moves the dirty account to another
library, then saves its sibling to produce the genuine preserving-refresh
omission. Red records Staff access hidden and `settings-refresh` focused. Green
retains both values with no navigation write, exposes the exact Cancel control,
allows only explicit Cancel to resolve that owner, and then saves the original
Workflow action. The captured recovery action is rejected after a scope change.
The fixture restores the moved account and cleans its owned cross-library audit
rows. The retained Browser identity passes 1/1 with zero failures/skips; its
11 normal scans and two actual native-200 states have clean accessibility,
overflow, image, page-error and external-request checks. The recovery and native
screenshots were inspected.

One compact controller fixture also exercises all eight previously source-only
domain destinations: Publication options, Common creators, Allowed patron codes,
Providers, Material formats, Custom fields, Auto-claim rules and Additional
templates. Each real edit blocks a Staff save through the single main Settings
owner; its action reaches the correct section/control or disclosure, preserves
both drafts, exposes no private values in the warning and sends no mutation.
All eight cells pass, as does the retained Staff Access file. This fixture adds
one frontend file, not eight native cases; it makes no SQL or geometry claim.

The fresh Release build has zero warnings/errors; current native discovery is
still 1486 tests with no new Browser identity/category entry. Receipts are
`466-pass1-red/466-pass1-red.trx`, `466-pass1-red-browser/`,
`466-pass1-green/466-pass1-green.trx`, `466-pass1-green-browser/`,
`466-pass1-domain-matrix3.log`, `466-pass1-staff-access.log`,
`466-pass1-green-build.log` and `466-pass1-discovery.json`. Initial fixture-load
failures remain in the earlier matrix logs. This is the single aggregate Pass 1
remediation; the independent adversarial Pass 2 and final integration remain
pending.

Implementation/reviewer models are selected explicitly on each child agent.
Orchestration remains in the parent Codex session, as in the #438 record.
Complex implementation uses Sol MAX; focused implementation and Pass 1 use
Luna MAX. Pass 2 assignments and actual executed results are recorded per issue.

Environment preparation (not implementation qualification): npm dependencies
installed; .NET SDK 10.0.303 / Node 24.19.0 and matching Chromium available.
The baseline Release build passed with zero warnings/errors. Local SQL was
verified as SQL Server 2022 Developer 16.0.1210.3. Receipts live outside Git at
`C:\Users\mfields\.codex\tmp\asap-i471\`; final counts come from final-source
native discovery, never these baseline preparation results. Runner availability
could not be read through the current token (403); no runner was activated.

Full Staff dark-mode consistency (#456), global search navigation (#467), human
assistive-technology acceptance (#398), live providers and deployment remain
outside this implementation. No merge, issue closure, deployment, production tag
or self-hosted runner activation is authorized by this record.
