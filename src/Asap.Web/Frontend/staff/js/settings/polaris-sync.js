import { isSuperAdminStaff, updateOrganizationsStatusUi, setInlineResult, updateSaveBarState } from '../api.js';
import { authorizedJson } from '../http.js';
import { settingsDirty, settingsReloadRequired, settingsSyncInProgress, settingsSaving, settingsLoading, settingsActionInProgress, currentLibraryContextOrgId, libraryContextLoadSerial, organizationsStatus, setSettingsReloadRequired, setSettingsSyncInProgress } from '../state.js';
import { showToast } from '../dialogs.js';
import { populateLibrarySelector } from './library-context.js';
import { renderLibraryParticipationCheckboxes } from './polaris-fields.js';
import { collectAllowedPatronCodeIds, getPatronCodesStatus, renderPatronCodeEligibilityOptions, updatePatronCodesStatusUi } from './patron-codes.js';
import { refreshSettingsView } from './refresh.js';
import { markAmbiguousSettingsMutation, uncertainMutationMessage } from './mutation-outcome.js';

export async function syncPolarisOrganizations(options = {}) {
  const resultEl = document.getElementById('organizations-sync-result');
  const syncOrganizationsBtn = document.getElementById('btn-sync-organizations');
  const btn = options.button || syncOrganizationsBtn;
  if (settingsSyncInProgress || settingsSaving || settingsLoading || settingsActionInProgress || settingsReloadRequired) return null;
  if (settingsDirty) {
    setInlineResult(resultEl, 'Save or discard unsaved Settings changes before synchronizing organizations.', 'ml-2 text-warning font-weight-bold');
    return null;
  }
  setSettingsSyncInProgress(true);
  updateSaveBarState();
  if (btn) btn.disabled = true;
  updateOrganizationsStatusUi('loading', 'Organizations loading from Polaris. Organization selection will be available after this sync completes.');
  updatePatronCodesStatusUi('loading', 'Patron codes loading from Polaris.');
  setInlineResult(resultEl, 'Synchronizing organizations and loading patron code choices...', 'ml-2 text-muted');

  let syncSucceeded = false;
  let settingsReadBackSucceeded = false;
  let syncMutationPending = false;
  const syncContextOrgId = currentLibraryContextOrgId;
  let syncContextSerial = libraryContextLoadSerial;
  const contextIsCurrent = () => syncContextOrgId === currentLibraryContextOrgId &&
    syncContextSerial === libraryContextLoadSerial;
  try {
    const selectedPatronCodeIds = collectAllowedPatronCodeIds();
    syncMutationPending = true;
    const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
    syncMutationPending = false;
    if (!contextIsCurrent()) return null;
    syncSucceeded = true;
    setSettingsReloadRequired(true);
    const count = result.synced || 0;
    updateOrganizationsStatusUi('loaded', `Polaris organizations loaded successfully. ${count} organization record${count === 1 ? '' : 's'} synced. Unchecked libraries do not participate.`);
    let settingsRefreshError = null;
    const refreshStartSerial = libraryContextLoadSerial;
    try {
      if (settingsDirty) {
        throw new Error('Unsaved Settings changes appeared during synchronization.');
      }
      const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true });
      if (syncContextOrgId !== currentLibraryContextOrgId ||
          libraryContextLoadSerial !== refreshStartSerial + 1 || settings?.orgId !== syncContextOrgId) return null;
      syncContextSerial = libraryContextLoadSerial;
      if (!settings?.version) {
        throw new Error('The current Settings version was not returned.');
      }
      setSettingsReloadRequired(false);
      settingsReadBackSucceeded = true;
    } catch (error) {
      if (syncContextOrgId !== currentLibraryContextOrgId || libraryContextLoadSerial !== refreshStartSerial + 1) return null;
      syncContextSerial = libraryContextLoadSerial;
      settingsRefreshError = error;
      setSettingsReloadRequired(true);
      console.error('Organizations were synchronized, but Settings could not be reloaded.', error);
    }
    const patronCodeContainer = document.getElementById('allowed-patron-code-container');
    if (patronCodeContainer) patronCodeContainer.removeAttribute('data-loaded');
    updatePatronCodesStatusUi('not_loaded', 'Refreshing patron code choices.');
    await renderPatronCodeEligibilityOptions(settingsRefreshError
      ? selectedPatronCodeIds : collectAllowedPatronCodeIds());
    if (!contextIsCurrent()) return null;
    const patronCodesLoaded = patronCodeContainer?.getAttribute('data-loaded') === 'true';
    const container = document.getElementById('enabled-libraries-checkbox-container');
    if (container) {
      container.removeAttribute('data-loaded');
    }
    if (isSuperAdminStaff()) {
      await populateLibrarySelector();
      if (!contextIsCurrent()) return null;
    }
    await renderLibraryParticipationCheckboxes();
    if (!contextIsCurrent()) return null;
    const organizationsLoaded = organizationsStatus === 'loaded' && container?.getAttribute('data-loaded') === 'true';
    if (settingsRefreshError) {
      const message = 'Organizations were synchronized, but current Settings could not be reloaded. Reload Settings before saving.';
      setInlineResult(resultEl, message, 'ml-2 text-warning font-weight-bold');
      showToast(message, 'error', 'settings-save-toast');
    } else if (!organizationsLoaded) {
      setInlineResult(resultEl, `Synced ${count} organization records. Local organization choices could not be loaded.`, 'ml-2 text-warning font-weight-bold');
    } else if (!patronCodesLoaded) {
      setInlineResult(resultEl, `Synced ${count} organization records. Patron code choices could not be loaded.`, 'ml-2 text-warning font-weight-bold');
    } else {
      setInlineResult(resultEl, `Synced ${count} organization records and refreshed patron code choices.`, 'ml-2 text-success font-weight-bold');
    }
    return result;
  } catch (err) {
    if (!contextIsCurrent()) throw err;
    if (syncSucceeded) {
      updateOrganizationsStatusUi('not_loaded', 'Organizations were synchronized, but local reference data could not be refreshed.');
      updatePatronCodesStatusUi('not_loaded', 'Patron code choices could not be refreshed.');
      if (!settingsReadBackSucceeded) {
        setSettingsReloadRequired(true);
      }
      const message = settingsReadBackSucceeded
        ? 'Organizations were synchronized and Settings reloaded, but reference data could not be refreshed.'
        : 'Organizations were synchronized, but Settings could not be reloaded. Reload Settings before saving.';
      setInlineResult(resultEl, message, 'ml-2 text-warning font-weight-bold');
      showToast(message, 'error', 'settings-save-toast');
    } else {
      if (syncMutationPending && markAmbiguousSettingsMutation(err, contextIsCurrent)) {
        updateOrganizationsStatusUi('not_loaded', 'The organization sync result could not be confirmed. Reload Settings to refresh locally persisted organizations.');
        updatePatronCodesStatusUi('not_loaded', 'The sync result could not be confirmed. Reload Settings to refresh patron code choices.');
        setInlineResult(resultEl, `Organization sync result could not be confirmed. ${uncertainMutationMessage}`, 'ml-2 text-warning font-weight-bold');
        throw err;
      }
      updateOrganizationsStatusUi('error', 'Organization sync failed before local changes. Check the error and retry Sync.');
      updatePatronCodesStatusUi('error', 'Patron codes were not refreshed because organization sync failed.');
      setInlineResult(resultEl, 'Warning: ' + (err.message || 'Organization sync failed.'), 'ml-2 text-warning font-weight-bold');
    }
    throw err;
  } finally {
    if (organizationsStatus === 'loading') {
      updateOrganizationsStatusUi('not_loaded', 'Organization synchronization ended. Reload Settings to refresh organization choices.');
    }
    if (getPatronCodesStatus() === 'loading') {
      updatePatronCodesStatusUi('not_loaded', 'Organization synchronization ended. Reload Settings to refresh patron code choices.');
    }
    setSettingsSyncInProgress(false);
    updateSaveBarState(settingsReloadRequired ? 'reload' : undefined);
    if (btn) btn.disabled = settingsReloadRequired || settingsSaving || settingsSyncInProgress || settingsActionInProgress || settingsLoading;
  }
}
