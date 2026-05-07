const assert = require("assert");
const fs = require("fs");
const path = require("path");

function fileText(relativePath) {
  return fs.readFileSync(path.resolve(__dirname, "..", relativePath), "utf8");
}

function runTests() {
  const gridJs = fileText("pb_public/staff/js/grid.js");
  assert.ok(gridJs.includes("renderPolarisRowSearchButton"));
  assert.ok(gridJs.includes("renderPolarisSearchButtonMarkup"));
  assert.ok(gridJs.includes("data-polaris-search-mode"));
  assert.ok(gridJs.includes("openPolarisSearch(row, mode)"));
  assert.ok(gridJs.includes("renderAuthorCell(row)"));
  assert.ok(gridJs.includes("polarisSearchValueForRow(row, mode)"));

  const modalJs = fileText("pb_public/staff/js/modals.js");
  assert.ok(modalJs.includes("export function renderPolarisSearchButtonMarkup"));
  assert.ok(modalJs.includes("Search Polaris using this title text"));
  assert.ok(modalJs.includes("Search Polaris using this author text"));
  assert.ok(modalJs.includes("Search Polaris using this identifier"));
  assert.ok(modalJs.includes("export function polarisSearchValueForRow"));
  assert.ok(modalJs.includes("export async function openPolarisSearch"));
  assert.ok(modalJs.includes("launchEditPolarisSearch('title'"));
  assert.ok(modalJs.includes("launchEditPolarisSearch('author'"));
  assert.ok(modalJs.includes("launchEditPolarisSearch('identifier'"));
  assert.ok(modalJs.includes("source: 'edit'"));
  assert.ok(modalJs.includes("query = mode === 'identifier'"));
  assert.ok(modalJs.includes("polarisSearchValueForRow(row, mode)"));
  assert.ok(modalJs.includes("returnDialog: document.getElementById('editModal')"));
  assert.ok(modalJs.includes("mode,"));
  assert.ok(modalJs.includes("query,"));
  assert.ok(modalJs.includes("title: options?.title || ''"));
  assert.ok(modalJs.includes("author: options?.author || ''"));
  assert.ok(modalJs.includes("lookupEditBibById({ bibId: result.bibId"));
  assert.ok(modalJs.includes("title_author"));

  const settingsUiJs = fileText("pb_public/staff/js/settings-ui.js");
  assert.ok(settingsUiJs.includes("export async function lookupEditBibById"));
  assert.ok(settingsUiJs.includes("body: JSON.stringify({ bibId, barcode })"));

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
