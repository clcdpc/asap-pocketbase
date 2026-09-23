import { currentLibraryContextOrgId, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, settingsReloadRequired, settingsSyncInProgress, setSettingsReloadRequired } from './state.js';
import { markSettingsClean, updateSaveBarState } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showConfirm } from './dialogs.js';
import { loadLibrarySettings } from './settings/library-context.js';
import { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels } from './settings/duplicate-labels.js';

export { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels };

document.getElementById('btn-reset-library-settings').addEventListener('click', async () => {
  if (settingsReloadRequired || settingsSyncInProgress) {
    showToast('Reload Settings before resetting library settings.', 'error');
    return;
  }
  const actionOrganizationId = currentLibraryContextOrgId;
  const actionContextSerial = libraryContextLoadSerial;
  if (actionOrganizationId === 'system') return;
  const version = lastSavedLibrarySettingsOrgId === actionOrganizationId
    ? lastSavedLibrarySettingsSnapshot?.version
    : null;
  const confirmed = await showConfirm('Reset library settings', 'Are you sure you want to delete this library\'s overrides and revert to system defaults?');
  if (!confirmed || actionOrganizationId !== currentLibraryContextOrgId || actionContextSerial !== libraryContextLoadSerial) return;

  const contextIsCurrent = () => actionOrganizationId === currentLibraryContextOrgId &&
    actionContextSerial === libraryContextLoadSerial;
  async function reloadCurrentLibrarySettings(preserveReloadRequired = false) {
    const loadSerial = libraryContextLoadSerial;
    try {
      const settings = await loadLibrarySettings(actionOrganizationId, { throwOnError: true, preserveReloadRequired });
      const current = actionOrganizationId === currentLibraryContextOrgId && libraryContextLoadSerial === loadSerial + 1;
      return { current, loaded: current && !!settings?.version };
    } catch (error) {
      const current = actionOrganizationId === currentLibraryContextOrgId && libraryContextLoadSerial === loadSerial + 1;
      return { current, loaded: false, error };
    }
  }

  try {
    await authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: { orgId: actionOrganizationId, action: 'reset', version }
    });
  } catch (error) {
    if (!contextIsCurrent()) return;
    if (error?.response?.code === 'stale_version') {
      const reload = await reloadCurrentLibrarySettings();
      if (reload.current) {
        const message = reload.loaded
          ? 'Library settings changed in another session. Current values were reloaded.'
          : `Library settings changed in another session, but current values could not be reloaded: ${reload.error?.message || 'reload failed.'}`;
        showToast(message, 'error');
      }
      return;
    }
    showToast(error.message || 'The library settings could not be reset.', 'error');
    return;
  }

  if (!contextIsCurrent()) return;
  setSettingsReloadRequired(true);
  const reload = await reloadCurrentLibrarySettings(true);
  if (reload.current) {
    if (reload.loaded) {
      setSettingsReloadRequired(false);
      markSettingsClean('clean');
      showToast('Library settings reset to system defaults', 'success');
    } else {
      updateSaveBarState('reload');
      const message = `Library settings were reset, but current values could not be reloaded: ${reload.error?.message || 'reload failed.'} Reload Settings before further changes.`;
      const settingsMessage = document.getElementById('settings-msg');
      if (settingsMessage) {
        settingsMessage.textContent = message;
        settingsMessage.className = 'mt-2 font-weight-bold text-warning';
      }
      showToast(message, 'error');
    }
  }
});
