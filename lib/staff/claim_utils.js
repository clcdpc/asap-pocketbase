function runClaimMutation(app, fn) {
  if (app && typeof app.runInTransaction === "function") {
    var result;
    app.runInTransaction(function (txApp) {
      result = fn(txApp);
    });
    return result;
  }
  return fn(app);
}

function staffClaimDisplayName(staff) {
  return String(
    staff.get("displayName") ||
    staff.get("username") ||
    staff.get("identityKey") ||
    "Staff"
  ).trim();
}

module.exports = {
  runClaimMutation: runClaimMutation,
  staffClaimDisplayName: staffClaimDisplayName
};
