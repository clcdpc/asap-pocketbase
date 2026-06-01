const assert = require("assert");
const Module = require("module");

const originalRequire = Module.prototype.require;

const helpersMock = {
  endpoint: () => "/mock",
  send: () => ({
    PatronBasicData: {
      PatronID: "p1",
      Barcode: "2900",
      PatronOrgID: "123",
      RequestPickupBranchID: ""
    }
  }),
  normalizePolarisId: value => String(value || "").trim(),
  firstRowValue: () => "",
  normalizeRows: () => [],
  cfg: () => ({ userId: "1", orgId: "1", workstationId: "1" }),
  buildXml: () => "<xml/>"
};

Module.prototype.require = function(moduleName) {
  if (moduleName === "./helpers.js") return helpersMock;
  if (moduleName === "./auth.js") return {};
  return originalRequire.apply(this, arguments);
};

const patron = require("../lib/polaris/patron.js");
Module.prototype.require = originalRequire;

const row = patron.lookupPatron({}, "2900");
assert.strictEqual(row.PatronOrgID, "123");
assert.strictEqual(row.RequestPickupBranchID, "");
assert.strictEqual(row.PreferredPickupBranchID, "");
assert.strictEqual(row.CurrentPreferredPickupBranchID, "");

console.log("polaris patron raw preferred pickup tests passed.");
