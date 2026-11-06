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
| #465 Common creator rows | Pending | Pending | Pending | Pending | Pending |
| #463 Direct edit/inheritance | Pending | Pending | Pending | Pending | Pending |
| #466 Blocking draft navigation | Pending | Pending | Pending | Pending | Pending |

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
