function staffPublicJson(record) {
  if (!record) return {};
  
  const getB = (key) => {
    if (typeof record.getBool === "function") return !!record.getBool(key);
    if (typeof record.get === "function") return !!record.get(key);
    return !!record[key];
  };
  
  const getV = (key) => {
    if (typeof record.get === "function") return record.get(key) || "";
    return record[key] || "";
  };

  return {
    id: record.id,
    collectionName: "staff_users",
    username: getV("username"),
    domain: getV("domain"),
    identityKey: getV("identityKey"),
    displayName: getV("displayName"),
    role: getV("role") || "staff",
    active: getB("active"),
    branchOrgId: getV("branchOrgId"),
    libraryOrgId: getV("libraryOrgId"),
    libraryOrgName: getV("libraryOrgName"),
    scope: getV("scope"),
    lastLogin: getV("lastLogin"),
    lastPolarisLogin: getV("lastPolarisLogin"),

    weekly_action_summary_enabled: getB("weekly_action_summary_enabled"),
    purchase_reminder_default: getB("purchase_reminder_default"),
    additional_copy_reminder_default: getB("additional_copy_reminder_default"),
    default_mine_unclaimed_filter: getB("default_mine_unclaimed_filter"),
    weekly_action_summary_email: getV("weekly_action_summary_email"),
  };
}

module.exports = {
  staffPublicJson
};
