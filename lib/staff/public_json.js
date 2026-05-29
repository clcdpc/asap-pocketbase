function staffPublicJson(record) {
  return {
    id: record.id,
    username: record.get("username") || "",
    domain: record.get("domain") || "",
    identityKey: record.get("identityKey") || "",
    displayName: record.get("displayName") || "",
    role: record.get("role") || "staff",
    active: !!record.getBool("active"),
    branchOrgId: record.get("branchOrgId") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    scope: record.get("scope") || "",
    lastLogin: record.get("lastLogin") || "",
    lastPolarisLogin: record.get("lastPolarisLogin") || "",

    weekly_action_summary_enabled: !!record.getBool("weekly_action_summary_enabled"),
    purchase_reminder_default: !!record.getBool("purchase_reminder_default"),
    additional_copy_reminder_default: !!record.getBool("additional_copy_reminder_default"),
    default_mine_unclaimed_filter: !!record.getBool("default_mine_unclaimed_filter"),
    weekly_action_summary_email: record.get("weekly_action_summary_email") || "",
  };
}

module.exports = {
  staffPublicJson
};
