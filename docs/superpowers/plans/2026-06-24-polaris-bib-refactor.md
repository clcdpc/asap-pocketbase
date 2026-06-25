# polars/bib.js Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** split `lib/polaris/bib.js` (666 lines) into focused sub-modules under `lib/polaris/bib/`, keeping `lib/polaris/bib.js` as a barrel so existing consumers (`lib/polaris.js`, `tests/polaris_material_types.test.js`) continue to work.

**Architecture:** each sub-module owns one concern from the Polaris BIB lifecycle — parsing raw MARC response rows, managing the material-type cache, orchestrating multi-mode bib search, analyzing holdings and placing holds, and reconciling BIB data with local records. The barrel re-exports all public names.

**Tech Stack:** CommonJS backend modules, existing require patterns, existing Polaris helper and auth utilities in `lib/polaris/`.

---

## File Structure

### Create
- `lib/polaris/bib/parse.js` — MARC row and field-value extraction helpers
- `lib/polaris/bib/material-types.js` — material type cache, fetch, and format-icon logic
- `lib/polaris/bib/search.js` — search query building, result normalization, scoring, orchestration
- `lib/polaris/bib/holdings.js` — holdings fetch, holdability summary, hold placement, reply-to-hold
- `lib/polaris/bib/detail.js` — single-bib detail fetch, catalog value merge, record reconciliation

### Modify
- `lib/polaris/bib.js` — convert from implementation to barrel that re-exports from the five sub-modules

### No change
- `lib/polaris.js` — imports from `./polaris/bib.js` which becomes a barrel
- `tests/polaris_material_types.test.js` — imports from `../lib/polaris/bib.js` which becomes a barrel

---

### Task 1: Extract Parse Module

**Files:**
- Create: `lib/polaris/bib/parse.js`

Extract these functions and constants:

```js
const helpers = require("../helpers.js");

function bibSearchRows(payload) { /* ... */ }
function bibRows(payload) { /* ... */ }
function firstBibValue(rows, elementIds, labels) { /* ... */ }
function firstBibValueMatching(rows, elementIds, labels, acceptRow) { /* ... */ }
function bibTitleRowAllowed(row, value, elementId, label) { /* ... */ }
function padMaterialTypeId(id) { /* ... */ }

module.exports = { bibSearchRows, bibRows, firstBibValue, firstBibValueMatching, bibTitleRowAllowed, padMaterialTypeId };
```

**Current lines:** 30-89.

---

### Task 2: Extract Material-Types Module

**Files:**
- Create: `lib/polaris/bib/material-types.js`

Extract these functions and the `PRIMARY_TOM_LABELS` constant:

```js
const auth = require("../auth.js");
const config = require("../../config.js");
const normalization = require("../../config/normalization.js");
const { padMaterialTypeId } = require("./parse.js");

const PRIMARY_TOM_LABELS = { /* ... */ };

function getMARCTypeOfMaterialRows(staff) { /* ... */ }
function formatMaterialIconUrl(app, materialType) { /* ... */ }
function normalizeMaterialTypesCache(cached) { /* ... */ }
function getMaterialTypeDetailsMap(app) { /* ... */ }
function getMaterialTypeDetails(app, id) { /* ... */ }
function getMaterialTypesMap(app) { /* ... */ }
function getBibFormatLabel(app, row, bibGetFormat) { /* ... */ }

module.exports = {
  getMARCTypeOfMaterialRows, getMARCTypeOfMaterialRows, // backward compat
  formatMaterialIconUrl, normalizeMaterialTypesCache,
  getMaterialTypeDetailsMap, getMaterialTypeDetails,
  getMaterialTypesMap, getBibFormatLabel, PRIMARY_TOM_LABELS
};
```

Note: `padMaterialTypeId` is a tiny one-liner that belongs in the parse module (it operates on raw IDs). Export it from parse.js and import it here.

**Current lines:** 7-20 (PRIMARY_TOM_LABELS), 92-238.

---

### Task 3: Extract Search Module

**Files:**
- Create: `lib/polaris/bib/search.js`

Extract these functions:

```js
const helpers = require("../helpers.js");
const { bibSearchRows } = require("./parse.js");
const { getMaterialTypeDetails, getBibFormatLabel, formatMaterialIconUrl } = require("./material-types.js");

function normalizeBibSearchRow(app, row) { /* ... */ }
function buildBibSearchRequests(options) { /* ... */ }
function scoreBibResult(result, options) { /* ... */ }
function searchBibs(app, staff, options) { /* ... */ }
function searchBib(app, staff, identifier) { /* ... */ }

module.exports = { normalizeBibSearchRow, buildBibSearchRequests, scoreBibResult, searchBibs, searchBib };
```

**Current lines:** 240-438.

---

### Task 4: Extract Holdings Module

**Files:**
- Create: `lib/polaris/bib/holdings.js`

Extract these functions and the `HOLD_REPLY_STATE_BY_STATUS_VALUE` constant:

```js
const helpers = require("../helpers.js");
const { config } = require("../../config.js");

const HOLD_REPLY_STATE_BY_STATUS_VALUE = { /* ... */ };

function getBibHoldings(staff, bibId) { /* ... */ }
function summarizeHoldability(holdings) { /* ... */ }
function summarizeHoldingsByLibrary(holdings, myLibraryOrgId, resolveParentLibrary) { /* ... */ }
function placeHold(staff, bibId, patronId, options) { /* ... */ }
function replyToHold(staff, holdPayload, state, options) { /* ... */ }

module.exports = { getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary, placeHold, replyToHold };
```

Note: `HOLD_REPLY_STATE_BY_STATUS_VALUE` is a private constant (not exported), used only inside `placeHold` which references it.

**Current lines:** 22-28, 440-570.

---

### Task 5: Extract Detail Module

**Files:**
- Create: `lib/polaris/bib/detail.js`

Extract these functions:

```js
const helpers = require("../helpers.js");
const { bibRows, firstBibValue, firstBibValueMatching, bibTitleRowAllowed } = require("./parse.js");
const { getMaterialTypeDetails, getBibFormatLabel, formatMaterialIconUrl } = require("./material-types.js");

function getBib(app, staff, bibId) { /* ... */ }
function mergeCatalogValue(catalogValue, oldValue) { /* ... */ }
function reconcileRecord(app, staff, record, bibId, selectedPolarisResult) { /* ... */ }

module.exports = { getBib, mergeCatalogValue, reconcileRecord };
```

**Current lines:** 572-647.

---

### Task 6: Convert bib.js To Barrel

**Files:**
- Modify: `lib/polaris/bib.js`

Replace the full implementation with:

```js
const { getMARCTypeOfMaterialRows, normalizeMaterialTypesCache, getMaterialTypeDetailsMap, getMaterialTypeDetails, getMaterialTypesMap } = require("./bib/material-types.js");
const { normalizeBibSearchRow, searchBibs, searchBib } = require("./bib/search.js");
const { getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary, placeHold, replyToHold } = require("./bib/holdings.js");
const { getBib, reconcileRecord } = require("./bib/detail.js");

module.exports = {
  getMaterialTypeDetailsMap, getMaterialTypeDetails, getMaterialTypesMap,
  normalizeMaterialTypesCache, getMARCTypeOfMaterialRows,
  getMARCTypeOfMaterials: getMARCTypeOfMaterialRows, // backward compatibility
  normalizeBibSearchRow, searchBibs, searchBib,
  getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary,
  placeHold, replyToHold, getBib, reconcileRecord
};
```

Note: `bibSearchRows`, `bibRows`, `firstBibValue`, `firstBibValueMatching`, `bibTitleRowAllowed`, `buildBibSearchRequests`, `scoreBibResult`, `formatMaterialIconUrl`, `getBibFormatLabel`, and `mergeCatalogValue` are not exported by the original `module.exports`, so they are not imported or included in the barrel. The barrel only imports and re-exports what the original module exposed.

---

### Task 7: Verify

**Files:**
- No additional edits.

- [ ] **Step 1: Run the focused tests**

Run:

```bash
node tests/polaris_material_types.test.js
node tests/polaris.test.js
node tests/polaris_searchBib.test.js
node tests/polaris_pickup_branches.test.js
node tests/pickup_branch_cache.test.js
```

Expected: All pass.

- [ ] **Step 2: Run the full suite**

Run:

```bash
npm test
```

Expected: All tests passed.
