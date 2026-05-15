const assert = require('assert');

// Mock helpers from polaris.js
function decodeByteArray(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (value.constructor && (value.constructor.name === "Uint8Array" || value.constructor.name === "Array")) {
    var str = "";
    for (var i = 0; i < value.length; i++) {
      str += String.fromCharCode(value[i]);
    }
    return str;
  }
  return String(value);
}

function normalizeNumeric(value) {
  var str = decodeByteArray(value).trim();
  if (!str) return 0;
  if (str.indexOf(",") !== -1) {
    str = str.split(",")[0];
  }
  return parseInt(str, 10) || 0;
}

function normalizeRows(container, listName, rowName) {
  var rows = container || [];
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function booleanValue(value) {
  return !!value;
}

function summarizeHoldingsByLibrary(holdings, myLibraryOrgId, resolveParentLibrary) {
  var rows = normalizeRows(holdings, "", "");
  var summary = {
    myLibraryCount: 0,
    otherLibraryCount: 0,
    consortiumCount: 0,
    isHoldable: false,
    hasHoldableAtMyLibrary: false,
    locations: []
  };
  
  myLibraryOrgId = String(myLibraryOrgId || "").trim();

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var locationId = String(row.LocationID || row.OrganizationID || row.OrgID || "").trim();
    if (!locationId) continue;

    var barcode = decodeByteArray(row.Barcode).trim();
    var itemsTotal = 0;
    var itemsIn = 0;

    if (barcode) {
      itemsTotal = 1;
      itemsIn = normalizeNumeric(row.ItemsIn || row.AvailableItems);
      if (itemsIn > 1) itemsIn = 1;
    } else {
      itemsTotal = normalizeNumeric(row.ItemsTotal || row.ItemTotal || row.TotalItems);
      itemsIn = normalizeNumeric(row.ItemsIn || row.AvailableItems);
    }

    var holdable = booleanValue(row.Holdable);

    var owningLibraryOrgId = resolveParentLibrary ? resolveParentLibrary(locationId) : locationId;
    var isMyLibrary = owningLibraryOrgId === myLibraryOrgId;

    if (isMyLibrary) {
      summary.myLibraryCount += itemsTotal;
      if (holdable) summary.hasHoldableAtMyLibrary = true;
    } else {
      summary.otherLibraryCount += itemsTotal;
    }

    summary.consortiumCount += itemsTotal;

    if (holdable) {
      summary.isHoldable = true;
    }

    summary.locations.push({
      locationId: locationId,
      locationName: row.LocationName || row.OrganizationName || "",
      owningLibraryOrgId: owningLibraryOrgId,
      itemsTotal: itemsTotal,
      itemsIn: itemsIn,
      holdable: holdable
    });
  }
  return summary;
}

// Test cases
function testCounting() {
  console.log("Running holdings counting tests...");

  // Scenario 1: Individual item rows that also carry summary fields (the bug)
  // 3 items, each saying ItemsTotal: 531
  const buggyHoldings = [
    { LocationID: "43", Barcode: "123", ItemsTotal: 531, Holdable: true },
    { LocationID: "43", Barcode: "456", ItemsTotal: 531, Holdable: true },
    { LocationID: "43", Barcode: "789", ItemsTotal: 531, Holdable: true }
  ];
  const summary1 = summarizeHoldingsByLibrary(buggyHoldings, "10", (id) => "10");
  assert.strictEqual(summary1.myLibraryCount, 3, "Should count 3 items by barcode, not sum 531 thrice");
  console.log("  Pass: Correctly ignored summary field on item-level rows");

  // Scenario 2: Summarized rows (no barcode)
  const summarizedHoldings = [
    { LocationID: "43", ItemsTotal: 5, Holdable: true },
    { LocationID: "44", ItemsTotal: 10, Holdable: true }
  ];
  const summary2 = summarizeHoldingsByLibrary(summarizedHoldings, "10", (id) => id === "43" ? "10" : "20");
  assert.strictEqual(summary2.myLibraryCount, 5, "Should use ItemsTotal for summarized row");
  assert.strictEqual(summary2.otherLibraryCount, 10, "Should use ItemsTotal for summarized row");
  assert.strictEqual(summary2.consortiumCount, 15);
  console.log("  Pass: Correctly used ItemsTotal for summarized rows");

  // Scenario 3: Byte array issue (the "utf8 number issue")
  // ItemsTotal: 531 -> [53, 51, 49] in byte array
  // normalizeNumeric on [53, 51, 49] should give 531
  const byteArr = [53, 51, 49];
  byteArr.constructor = { name: "Uint8Array" }; // Mock Uint8Array
  assert.strictEqual(normalizeNumeric(byteArr), 531, "Should correctly decode byte array numeric value");
  console.log("  Pass: Correctly decoded byte array numeric value");

  console.log("All counting tests passed!");
}

testCounting();
