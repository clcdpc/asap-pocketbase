import { beginStaffAccessLoad, loadStaffUsers, populateStaffLibraryOptions, showStaffAccessLoading } from '../settings-users.js';

export async function loadStaffAccessSettings(options = {}) {
  const loadOptions = beginStaffAccessLoad(options);
  if (!showStaffAccessLoading(loadOptions)) return false;
  if (!await populateStaffLibraryOptions(loadOptions)) return false;
  return loadStaffUsers({ ...loadOptions, loadingShown: true });
}
