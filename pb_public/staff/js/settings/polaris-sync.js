import { isSuperAdminStaff, updateOrganizationsStatusUi, setInlineResult } from '../api.js';
import { authorizedJson } from '../http.js';
import { populateLibrarySelector } from './library-context.js';
import { renderLibraryParticipationCheckboxes } from './polaris-fields.js';

export async function syncPolarisOrganizations(options = {}) {
  const resultEl = document.getElementById('organizations-sync-result');
  const syncOrganizationsBtn = document.getElementById('btn-sync-organizations');
  const btn = options.button || syncOrganizationsBtn;
  if (btn) btn.disabled = true;
  updateOrganizationsStatusUi('loading', 'Organizations loading from Polaris. Organization selection will be available after this sync completes.');
  setInlineResult(resultEl, 'Syncing organizations...', 'ml-2 text-muted');

  try {
    const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
    const count = result.synced || 0;
    updateOrganizationsStatusUi('loaded', `Polaris organizations loaded successfully. ${count} organization record${count === 1 ? '' : 's'} synced. Leave all libraries unchecked to enable all organizations.`);
    setInlineResult(resultEl, `Synced ${count} organization records.`, 'ml-2 text-success font-weight-bold');
    const container = document.getElementById('enabled-libraries-checkbox-container');
    if (container) {
      container.removeAttribute('data-loaded');
    }
    if (isSuperAdminStaff()) {
      await populateLibrarySelector();
    }
    await renderLibraryParticipationCheckboxes();
    return result;
  } catch (err) {
    updateOrganizationsStatusUi('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    setInlineResult(resultEl, 'Warning: ' + (err.message || 'Organization sync failed.'), 'ml-2 text-warning font-weight-bold');
    throw err;
  } finally {
    if (btn) btn.disabled = false;
  }
}
