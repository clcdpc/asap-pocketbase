# Polaris and Requests improvements (#439)

Branch: `codex/i439-polaris-requests`; integration base:
`4cdfdf7b9d29869d66685d968ac7a955e891d7fa` (`main`). The base includes
#411, #420, #421, #453 and #454. Work uses a separate checkout to preserve
the original checkout's four unrelated browser-test edits.

Each issue is implemented serially, followed by no more than two independent
review/fix passes: requirements review, then executed adversarial scenarios.
Routine regression checks and final evidence reconciliation are not new reviews.

| Issue | Implementation | Pass 1 | Pass 2 | Technical disposition |
| --- | --- | --- | --- | --- |
| #389 Polaris search and direct actions | `7ebc90b`: compact catalog UI and scoped direct actions | Luna MAX: 2 findings fixed | Astra HIGH + Terra MAX: 1 finding fixed | Technically qualified |
| #413 Already own Bib linking | Atomic verified association/action; unrelated drafts retained | Luna MAX: 1 finding fixed | Astra HIGH: 2 findings fixed | Technically qualified |
| #423 Purchase/rejection confirmations | Pending | Pending | Pending | Pending |
| #426 Detail footer Close | Pending | Pending | Pending | Pending |
| #424 Pickup help boundary | Pending | Pending | Pending | Pending |
| #425 Patron email link | Pending | Pending | Pending | Pending |
| #396 Initial heading focus | Pending | Pending | Pending | Pending |
| #442 Authorized workflow counts | Pending | Pending | Pending | Pending |
| #428 Requests table sizing | Pending | Pending | Pending | Pending |

Implementation/reviewer models are selected explicitly per the task: Sol MAX
for #389/#413/#423/#442, Luna MAX for smaller implementations and requirements
reviews, Astra HIGH for workflow/authorization scenarios, and Terra MAX for
visual scenarios. The parent session cannot change its own model; orchestration
uses the current Codex session. No child-model substitutions are currently needed.

## Verification boundaries

- The original #389 reference PNGs are absent from the issue and its comments;
  rendered comparison uses the issue's detailed screenshot-derived specification.
- Human assistive-technology acceptance remains #398. Live Polaris acceptance
  remains #298. Deterministic automated fixtures do not establish those results.
- No issue closure, merge, deployment, host activation or production tag is part
  of this implementation.

## Focused evidence

Generated logs, browser PNGs and machine-readable receipts remain outside tracked
source under `.artifacts/i439` or the acceptance runner's normal evidence root.

### #389

Reuses #411's authoritative, atomic catalog-queue service. Adds compact cards,
all five modes, author shortcut, readable material metadata/icons, bounded
deduplicated holdings, scoped direct actions and persisted identical-command
recovery. Additional-copy confirmation explicitly discloses the automatic-hold
override. Real SQL verifies `AutoHold=true`, verified BIB, exactly one associated
task, reminder choice, audit/outbox and authoritative final status. Ordinary
Use BIB preserves the existing automatic-hold preference.

Focused staff frontend sweep: 43/43 files, zero failures/skips. Pass 1 fixed
root-relative icons and recovery Escape/focus; its focused Node/build/browser
checks pass. Pass 2 independently executed 86/86 SQL cases, three Node files,
eight integrity probes and actual Chromium mode/ownership/failure/stale/keyboard
scenarios. Terra inspected the eight rendered owner states and found one stale
count after rejected-action recovery; search now owns clearing and truthful
feedback. Final targeted Node and Release build pass (zero warnings/errors), and
the real-SQL browser owner passes 1/1 with nine captured states, zero serious or
critical axe findings, overflow, missing images, page errors or external requests.
Final receipt: `pass2-polaris-browser/polaris-search.trx`; visual/state evidence:
`polaris-search-25fced7c28714d9fa610243ab5ad5575/browser-results.json` under the local
evidence root. Failed initial fixture/focus receipts are retained. Exact role/
claim and persisted pending-operation variants received targeted lock-path
inspection in Pass 2; the final complete SQL/browser gate remains required.

### #413

Missing/invalid/unverified BIBs expose local linking guidance, existing lookup
controls and explicit completion inside the same request detail flow. Selection
alone does not mutate. The existing action saves association and workflow in one
transaction; unrelated drafts remain unsaved. Saved automatic-hold opt-out still
closes without a hold and uses truthful conditional wording.

Focused Node checks and Release build pass (zero warnings/errors); fresh SQL
passes 10/10. Pass 1 fixed a reload control that could become an action after a
lookup error; the red/green regression and actual browser prove recovery stays
read-only until authoritative refresh. Pass 2 found duplicate-recovery draft loss
and overwritten typing during delayed reload. Both are fixed, including failed
reads and Cancel during reload. The unchanged independent probe improves from
8/10 to 10/10; failed results remain retained. The expanded #413 browser owner
passes with eight clean accessibility/layout states (desktop, mobile and native
200%), including all recovery flags. Its combined checkpoint is retained as
1/2: #389 had an ownership-enrichment timing race in a test assertion. An explicit
ownership-complete setup wait fixes it; the focused #389 rerun passes 1/1.
Evidence: `i413-pass2/remediation-browser/i413-pass2-remediation-browser.trx`,
`i413-pass2/389-checkpoint/i413-pass2-389-checkpoint.trx`, and
`already-own-link-37f16f34c9a44668abff21e64c2b1fa6/browser-results.json` under the
local evidence root. Native actor/version races and UI role/scope/identity races
were executed; separate SQL role/library/claim changes were inspected only.

## Integrated qualification

Pending completion of all nine issues. Final gates use current discovered test
identities and registered acceptance suites, without fabricated counts or skips.
