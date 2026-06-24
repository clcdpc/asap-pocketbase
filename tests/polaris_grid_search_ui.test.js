const assert = require("assert");
const fs = require("fs");
const path = require("path");

function fileText(relativePath) {
  return fs.readFileSync(path.resolve(__dirname, "..", relativePath), "utf8");
}

function runTests() {
  const gridJs = fileText("pb_public/staff/js/grid.js");
  const gridActionsJs = fileText("pb_public/staff/js/grid-actions.js");
  const gridEventsJs = fileText("pb_public/staff/js/grid-events.js");
  const gridRenderingJs = fileText("pb_public/staff/js/grid-rendering.js");
  assert.ok(gridRenderingJs.includes("renderPolarisRowSearchButton"));
  assert.ok(gridRenderingJs.includes("renderPolarisSearchButtonMarkup"));
  assert.ok(gridRenderingJs.includes("data-polaris-search-mode"));
  assert.ok(gridRenderingJs.includes("row.status === 'hold_placed'"));
  assert.ok(gridRenderingJs.includes("if (row.status === 'hold_placed') return '';"));
  assert.ok(gridRenderingJs.includes("${rowMarker(row)}"));
  assert.ok(gridEventsJs.includes("openPolarisSearch(row, mode)"));
  assert.ok(gridRenderingJs.includes("renderAuthorCell(row"));
  assert.ok(gridRenderingJs.includes("polarisSearchValueForRow(row, mode)"));
  assert.ok(gridActionsJs.includes("additionalCopyActionForRow(row)"));
  assert.ok(gridActionsJs.includes("Buy another copy"));
  assert.ok(gridActionsJs.includes("/additional-copy"));
  assert.ok(gridActionsJs.includes("Open tasks for this BIB"));

  const modalJs = fileText("pb_public/staff/js/modals.js");
  assert.ok(modalJs.includes("polarisSearchValueForRow }"));

  assert.ok(
    gridEventsJs.includes("import { openPolarisSearch } from './modals.js';"),
    "grid-events.js should use the modals.js openPolarisSearch wrapper so row-level Polaris actions receive refresh behavior"
  );

  assert.ok(
    !gridEventsJs.includes("from './modals/polaris-search.js'"),
    "grid-events.js should not import openPolarisSearch directly from the lower-level Polaris module"
  );

  assert.ok(
    modalJs.includes("import { refreshCurrentStaffView } from './grid.js';"),
    "modals.js should import refreshCurrentStaffView for modal action refresh callbacks"
  );

  assert.ok(
    modalJs.includes("return polarisSearchOpen(row, mode, options, ctx, refreshCurrentStaffView);"),
    "modals.js openPolarisSearch wrapper should pass refreshCurrentStaffView to the lower-level Polaris search module"
  );

  const editFormJs = fileText("pb_public/staff/js/modals/edit-form.js");
  assert.ok(editFormJs.includes("function applyHoldPlacedBibLock(row"));
  assert.ok(editFormJs.includes("row.status === 'hold_placed'"));
  assert.ok(editFormJs.includes("BIB ID is locked because the hold has already been placed."));
  assert.ok(editFormJs.includes("ctx.bibid.disabled = isLocked"));
  assert.ok(editFormJs.includes("bibLookupBtn.classList.toggle('hidden', isLocked)"));
  assert.ok(editFormJs.includes("document.getElementById('edit-title-polaris-search')?.classList.toggle('hidden', isLocked)"));
  assert.ok(editFormJs.includes("document.getElementById('edit-author-polaris-search')?.classList.toggle('hidden', isLocked)"));
  assert.ok(editFormJs.includes("document.getElementById('edit-identifier-polaris-search')?.classList.toggle('hidden', isLocked)"));

  const polarisSearchJs = fileText("pb_public/staff/js/modals/polaris-search.js");
  assert.ok(polarisSearchJs.includes("export function renderPolarisSearchButtonMarkup"));
  assert.ok(polarisSearchJs.includes("Search Polaris using this title text"));
  assert.ok(polarisSearchJs.includes("Search Polaris using this author text"));
  assert.ok(polarisSearchJs.includes("Search Polaris using this identifier"));
  assert.ok(polarisSearchJs.includes("export async function openPolarisSearch"));
  const eventsJs = fileText("pb_public/staff/js/modals/events.js");
  assert.ok(eventsJs.includes("launchEditPolarisSearch('title'"));
  assert.ok(eventsJs.includes("launchEditPolarisSearch('author'"));
  assert.ok(eventsJs.includes("launchEditPolarisSearch('identifier'"));
  assert.ok(polarisSearchJs.includes("query = mode === 'identifier'"));
  assert.ok(polarisSearchJs.includes("polarisSearchValueForRow(row, mode)"));
  assert.ok(polarisSearchJs.includes("returnDialog: document.getElementById(context === 'edit' ? 'editModal' : 'newSuggestionModal')"));
  assert.ok(polarisSearchJs.includes("mode,"));
  assert.ok(polarisSearchJs.includes("query,"));
  assert.ok(polarisSearchJs.includes("title: options?.title || ''"));
  assert.ok(polarisSearchJs.includes("author: options?.author || ''"));
  assert.ok(polarisSearchJs.includes("applySelectedPolarisResultToEditForm(result"));
  assert.ok(polarisSearchJs.includes("title_author"));
  assert.ok(polarisSearchJs.includes("const launchedFromEditForm = options.source === 'edit'"));
  assert.ok(polarisSearchJs.includes("Use BIB in Queue Form"));
  assert.ok(polarisSearchJs.includes("Use BIB & Queue Now"));
  assert.ok(polarisSearchJs.includes("Buy another copy + Queue Now"));
  assert.ok(polarisSearchJs.includes("confirmAdditionalCopyAction") || polarisSearchJs.includes("import { confirmAdditionalCopyAction }"));
  assert.ok(polarisSearchJs.includes("summary.consortiumCount"));
  assert.ok(polarisSearchJs.includes("buildPayload('pending_hold', 'additionalCopy')"));
  assert.ok(polarisSearchJs.includes("const isAdditionalCopyAction = action === 'additionalCopy'"));
  assert.ok(polarisSearchJs.includes("selectedPolarisPublication: isAdditionalCopyAction ? '' : result.publication"));
  const modalUtilsJs = fileText("pb_public/staff/js/modals/utils.js");
  assert.ok(modalUtilsJs.includes("export function normalizedAdditionalCopyPublication"));
  assert.ok(polarisSearchJs.includes("payload.emailPurchaseReminder = confirmResult.emailPurchaseReminder"));
  assert.ok(polarisSearchJs.indexOf("const launchedFromEditForm = options.source === 'edit'") < polarisSearchJs.indexOf("performImmediateStaffAction(row.id, payload"));

  const settingsUiJs = fileText("pb_public/staff/js/settings-ui.js");
  assert.ok(settingsUiJs.includes("export async function lookupEditBibById"));
  assert.ok(settingsUiJs.includes("body: { bibId, barcode }"));

  const titleRequestActionsJs = fileText("lib/staff/title_request_actions.js");
  assert.ok(titleRequestActionsJs.includes('context.action === "additionalCopy"'));
  assert.ok(titleRequestActionsJs.includes('context.data.publication = context.record.get("publication")'));
  assert.ok(titleRequestActionsJs.includes('context.data.selectedPolarisPublication = ""'));
  assert.ok(titleRequestActionsJs.includes("BIB ID cannot be changed after the hold has been placed."));

  const html = fileText("pb_public/staff/index.html");
  assert.ok(html.includes("polarisSearchDialog"));
  assert.ok(html.includes("polaris-search-results"));
  assert.ok(html.includes("edit-title-polaris-search"));
  assert.ok(html.includes("edit-author-polaris-search"));
  assert.ok(html.includes("edit-identifier-polaris-search"));

  const css = fileText("pb_public/staff/styles.css");
  assert.ok(css.includes(".polaris-row-search"));
  assert.ok(css.includes(".polaris-search-result"));
  assert.ok(css.includes(".field-with-action"));
  assert.ok(css.includes(".field-with-action__button"));
  assert.ok(css.includes("#grid-container .gridjs-td:has(.searchable-cell)"));
  assert.ok(css.includes("flex: 0 0 28px"));
  assert.strictEqual((css.match(/^\.polaris-row-search \{/gm) || []).length, 1);

  console.log("Polaris grid search UI tests passed.");
}

runTests();
