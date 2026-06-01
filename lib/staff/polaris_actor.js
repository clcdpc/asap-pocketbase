function resolvePolarisUpdateActor(staff, polarisConfig) {
  var role = String(staff.get("role") || "").trim();
  var polarisUserId = String(staff.get("polarisUserId") || "").trim();
  if (polarisUserId) {
    return {
      polarisUserId: polarisUserId,
      actorName: staff.get("username") || "",
      actorType: "staff",
      fallbackUsed: false
    };
  }

  if (role === "super_admin") {
    var fallbackUserId = String((polarisConfig && polarisConfig.userId) || "").trim();
    if (!fallbackUserId) {
      var missingFallback = new Error("Configured Polaris system user ID is missing. Add a Polaris user ID to your staff account or configure the system user ID.");
      missingFallback.code = 403;
      throw missingFallback;
    }
    return {
      polarisUserId: fallbackUserId,
      actorName: staff.get("username") || "super_admin",
      actorType: "staff",
      fallbackUsed: true
    };
  }

  var err = new Error("Your staff account is missing a Polaris user ID. Ask a super admin to update your staff profile before changing patron pickup preferences.");
  err.code = 403;
  throw err;
}

module.exports = {
  resolvePolarisUpdateActor: resolvePolarisUpdateActor
};
