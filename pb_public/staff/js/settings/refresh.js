let refreshSettingsViewHandler = null;
let loadStaffConfigHandler = null;

export function registerSettingsRefreshHandlers({ refreshSettingsView, loadStaffConfig }) {
  refreshSettingsViewHandler = refreshSettingsView;
  loadStaffConfigHandler = loadStaffConfig;
}

export async function refreshSettingsView(options = {}) {
  if (!refreshSettingsViewHandler) return;
  return refreshSettingsViewHandler(options);
}

export async function loadStaffConfig() {
  if (!loadStaffConfigHandler) return;
  return loadStaffConfigHandler();
}
