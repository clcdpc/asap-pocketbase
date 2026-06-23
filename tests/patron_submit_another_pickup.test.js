const assert = require("assert");
const { JSDOM } = require("jsdom");

(async () => {
  const dom = new JSDOM(`<!doctype html>
    <div id="step-form" class="hidden">
      <form id="suggestion-form">
        <div id="pickup-branch-container" class="form-group row">
          <select id="preferred-pickup-branch" name="preferredPickupBranchId" required>
            <option value="">Select a pickup location...</option>
            <option value="10">Main Library</option>
            <option value="20">Branch Library</option>
          </select>
        </div>
        <select id="format" name="format">
          <option value="book">Book</option>
        </select>
        <div id="field-title"><input id="title"></div>
        <label id="lbl-title" for="title">Title</label>
        <div id="field-author"><input id="author"></div>
        <label id="lbl-creator" for="author">Author</label>
        <div id="field-identifier"><input id="isbn"></div>
        <label id="lbl-identifier" for="isbn">Identifier</label>
        <div id="field-publication"><select id="publication"></select></div>
        <label id="lbl-publication" for="publication">Publication Timing</label>
        <div id="physical-fields"></div>
        <div id="econtent-fields"></div>
        <div id="additional-fields-container"></div>
        <div id="submit-error"></div>
      </form>
    </div>
    <div id="step-login"></div>
    <div id="step-success"></div>
    <div id="step-conflict"></div>
    <div id="status-announcer"></div>
  `, { url: "http://localhost/patron/" });

  global.window = dom.window;
  global.document = dom.window.document;
  global.Option = dom.window.Option;
  global.Event = dom.window.Event;
  global.localStorage = dom.window.localStorage;
  global.sessionStorage = dom.window.sessionStorage;

  const pickup = document.getElementById("preferred-pickup-branch");
  pickup.value = "20";

  const mod = await import("../pb_public/patron/js/form-ui.js");
  mod.resetSuggestionFormUi();

  assert.strictEqual(pickup.value, "20", "submit-another reset should preserve the patron's selected pickup location");
  assert.strictEqual(document.getElementById("pickup-branch-container").classList.contains("hidden"), false, "pickup selector should remain visible for the next request");
  assert.deepStrictEqual(
    Array.from(pickup.options).map((option) => option.value),
    ["", "10", "20"],
    "pickup options should remain available after submit-another reset"
  );

  console.log("patron submit another pickup regression checks passed");
})().catch((err) => {
  console.error(err);
  process.exit(1);
});
