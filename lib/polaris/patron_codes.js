const helpers = require("./helpers.js");

function normalizePatronCodeRows(payload) {
  var rows = payload || [];
  if (payload && payload.PatronCodes) rows = payload.PatronCodes;
  if (payload && payload.PatronCodeRows) rows = payload.PatronCodeRows;
  if (payload && payload.PatronCodesRows) rows = payload.PatronCodesRows;
  if (payload && payload.PatronCodeGetRows) rows = payload.PatronCodeGetRows;
  if (payload && payload.PatronCodesGetRows) rows = payload.PatronCodesGetRows;
  if (rows && rows.PatronCode) rows = rows.PatronCode;
  if (rows && rows.PatronCodeRow) rows = rows.PatronCodeRow;
  if (rows && rows.PatronCodesRow) rows = rows.PatronCodesRow;
  if (rows && rows.PatronCodesGetRow) rows = rows.PatronCodesGetRow;
  if (rows && rows.PatronCodeGetRow) rows = rows.PatronCodeGetRow;
  if (!Array.isArray(rows)) rows = rows ? [rows] : [];
  return rows;
}

function patronCodes(staff, polarisConfig) {
  var c = helpers.cfg(polarisConfig);
  var systemConfig = Object.assign({}, c, { orgId: "1" });
  var ep = helpers.endpoint("public", "patroncodes", systemConfig);
  var payload = helpers.send("GET", ep, "", staff || null, "application/json", systemConfig);
  return normalizePatronCodeRows(payload);
}

module.exports = {
  normalizePatronCodeRows: normalizePatronCodeRows,
  patronCodes: patronCodes
};
