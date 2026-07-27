import { isSuperAdminStaff, updateOrganizationsStatusUi, setInlineResult } from '../api.js';
import { authorizedJson } from '../http.js';
import { populateLibrarySelector } from './library-context.js';
import { renderLibraryParticipationCheckboxes } from './polaris-fields.js';
import { collectAllowedPatronCodeIds, renderPatronCodeEligibilityOptions, updatePatronCodesStatusUi } from './patron-codes.js';

export async function syncPolarisOrganizations(options = {}) {
  const resultEl = document.getElementById('organizations-sync-result');
  const syncOrganizationsBtn = document.getElementById('btn-sync-organizations');
  const btn = options.button || syncOrganizationsBtn;
  if (btn) btn.disabled = true;
  updateOrganizationsStatusUi('loading', 'Organizations loading from Polaris. Organization selection will be available after this sync completes.');
  updatePatronCodesStatusUi('loading', 'Patron codes loading from Polaris.');
  setInlineResult(resultEl, 'Syncing organizations and patron codes...', 'ml-2 text-muted');

  try {
    const selectedPatronCodeIds = collectAllowedPatronCodeIds();
    const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
    const count = result.synced || 0;
    const patronCodeCount = result.patronCodesSynced || 0;
    updateOrganizationsStatusUi('loaded', `Polaris organizations loaded successfully. ${count} organization record${count === 1 ? '' : 's'} synced. Leave all libraries unchecked to enable all organizations.`);
    if (result.patronCodesError) {
      updatePatronCodesStatusUi('error', `Organizations loaded, but patron codes could not be loaded: ${result.patronCodesError}`);
      setInlineResult(resultEl, `Synced ${count} organization records. Patron code warning: ${result.patronCodesError}`, 'ml-2 text-warning font-weight-bold');
    } else {
      updatePatronCodesStatusUi('loaded', `Polaris patron codes loaded successfully. ${patronCodeCount} code${patronCodeCount === 1 ? '' : 's'} synced.`);
      setInlineResult(resultEl, `Synced ${count} organization records and ${patronCodeCount} patron codes.`, 'ml-2 text-success font-weight-bold');
      const patronCodeContainer = document.getElementById('allowed-patron-code-container');
      if (patronCodeContainer) patronCodeContainer.removeAttribute('data-loaded');
      await renderPatronCodeEligibilityOptions(selectedPatronCodeIds);
    }
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
