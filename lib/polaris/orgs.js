const helpers = require("./helpers.js");

function organizations(kind, staff) {
  kind = String(kind || "all").trim().toLowerCase();
  if (["all", "system", "library", "branch"].indexOf(kind) < 0) {
    kind = "all";
  }
  var ep = helpers.endpoint("public", "organizations/" + kind);
  var payload = helpers.send("GET", ep, "", staff || null);
  var rows = payload.OrganizationsGetRows || [];
  if (rows.OrganizationsGetRow) {
    rows = rows.OrganizationsGetRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

module.exports = {
  organizations: organizations,
};
