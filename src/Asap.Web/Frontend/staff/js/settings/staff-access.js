import { loadStaffUsers, populateStaffLibraryOptions, showStaffAccessLoading } from '../settings-users.js';

export async function loadStaffAccessSettings(options = {}) {
  if (!showStaffAccessLoading(options)) return false;
  if (!await populateStaffLibraryOptions(options)) return false;
  return loadStaffUsers({ ...options, loadingShown: true });
}
