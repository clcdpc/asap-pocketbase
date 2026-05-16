const assert = require("assert");
const fs = require("fs");
const path = require("path");

function count(source, marker) {
  return (source.match(new RegExp(
    marker.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"),
    "g"
  )) || []).length;
}

const bib = fs.readFileSync(
  path.resolve(__dirname, "../lib/staff/title_request_bib_actions.js"),
  "utf8"
);
assert.strictEqual(count(bib, "function prepareTitleRequestBibAction("), 1);
assert.strictEqual(count(bib, "function finalizeTitleRequestCloseReason("), 1);

const sidefx = fs.readFileSync(
  path.resolve(__dirname, "../lib/staff/title_request_side_effects.js"),
  "utf8"
);
assert.strictEqual(count(sidefx, "function maybeRunImmediatePromoter("), 1);
assert.strictEqual(count(sidefx, "function sendPurchaseReminderIfRequested("), 1);

console.log("staff_duplicate_source_blocks.test.js passed.");
