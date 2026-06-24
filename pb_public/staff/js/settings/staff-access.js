import { loadStaffUsers, populateStaffLibraryOptions } from '../settings-users.js';

export async function loadStaffAccessSettings() {
  await populateStaffLibraryOptions();
  await loadStaffUsers();
}
