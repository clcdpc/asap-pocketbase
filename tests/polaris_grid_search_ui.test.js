const assert = require("assert");
const fs = require("fs");
const path = require("path");

function fileText(relativePath) {
  return fs.readFileSync(path.resolve(__dirname, "..", relativePath), "utf8");
}

function runTests() {
  const gridJs = fileText("pb_public/staff/js/grid.js");
  assert.ok(gridJs.includes("renderPolarisRowSearchButton"));
  assert.ok(gridJs.includes("Search Polaris by title"));
  assert.ok(gridJs.includes("Search Polaris by author"));
  assert.ok(gridJs.includes("data-polaris-search-mode"));
  assert.ok(gridJs.includes("openPolarisSearch(row, mode)"));
  assert.ok(gridJs.includes("renderAuthorCell(row)"));
  assert.ok(gridJs.includes("polarisSearchValueForRow(row, mode)"));

  const modalJs = fileText("pb_public/staff/js/modals.js");
  assert.ok(modalJs.includes("export function polarisSearchValueForRow"));
  assert.ok(modalJs.includes("export async function openPolarisSearch"));
  assert.ok(modalJs.includes("mode,"));
  assert.ok(modalJs.includes("query,"));
  assert.ok(modalJs.includes("title: searchTitle"));
  assert.ok(modalJs.includes("author: searchAuthor"));
  assert.ok(modalJs.includes("lookupEditBibById({ bibId: result.bibId"));
  assert.ok(modalJs.includes("Search title + author"));

  const settingsUiJs = fileText("pb_public/staff/js/settings-ui.js");
  assert.ok(settingsUiJs.includes("export async function lookupEditBibById"));
  assert.ok(settingsUiJs.includes("body: JSON.stringify({ bibId, barcode })"));

  const html = fileText("pb_public/staff/index.html");
  assert.ok(html.includes("polarisSearchDialog"));
  assert.ok(html.includes("polaris-search-results"));

  const css = fileText("pb_public/staff/styles.css");
  assert.ok(css.includes(".polaris-row-search"));
  assert.ok(css.includes(".polaris-search-result"));

  console.log("Polaris grid search UI tests passed.");
}

runTests();
