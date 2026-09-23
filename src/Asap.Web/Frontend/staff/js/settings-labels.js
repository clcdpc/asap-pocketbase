import { currentLibraryContextOrgId, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, settingsReloadRequired, settingsSyncInProgress, settingsSaving, settingsLoading, settingsActionInProgress, settingsDirty, setSettingsActionInProgress, setSettingsReloadRequired } from './state.js';
import { markSettingsClean, updateSaveBarState } from './api.js';
import { checkSettingsDirty } from './settings/serialize-save.js';
import { authorizedJson } from './http.js';
import { showToast, showConfirm } from './dialogs.js';
import { loadLibrarySettings } from './settings/library-context.js';
import { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels } from './settings/duplicate-labels.js';

export { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels };

document.getElementById('btn-reset-library-settings').addEventListener('click', async () => {
  if (settingsReloadRequired || settingsSyncInProgress || settingsSaving || settingsLoading || settingsActionInProgress) {
    showToast('Reload Settings before resetting library settings.', 'error');
    return;
  }
  const actionOrganizationId = currentLibraryContextOrgId;
  const actionContextSerial = libraryContextLoadSerial;
  if (actionOrganizationId === 'system') return;
  const version = lastSavedLibrarySettingsOrgId === actionOrganizationId
    ? lastSavedLibrarySettingsSnapshot?.version
    : null;
  if (!version) {
    setSettingsReloadRequired(true);
    updateSaveBarState('reload');
    return;
  }
  if (settingsDirty || checkSettingsDirty()) {
    showToast('Save or discard Settings changes before resetting library settings.', 'error');
    return;
  }
  const confirmed = await showConfirm('Reset library settings', 'Are you sure you want to delete this library\'s overrides and revert to system defaults?');
  if (!confirmed || actionOrganizationId !== currentLibraryContextOrgId || actionContextSerial !== libraryContextLoadSerial) return;
  if (settingsReloadRequired || settingsSyncInProgress || settingsSaving || settingsLoading || settingsActionInProgress || settingsDirty || checkSettingsDirty()) return;

  setSettingsActionInProgress(true);
  updateSaveBarState('saving');
  try {
    const contextIsCurrent = () => actionOrganizationId === currentLibraryContextOrgId &&
      actionContextSerial === libraryContextLoadSerial;
    async function reloadCurrentLibrarySettings() {
      const loadSerial = libraryContextLoadSerial;
      try {
        const settings = await loadLibrarySettings(actionOrganizationId, { throwOnError: true, preserveReloadRequired: true });
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
        setSettingsReloadRequired(true);
        const reload = await reloadCurrentLibrarySettings();
        if (reload.current) {
          if (reload.loaded) {
            setSettingsReloadRequired(false);
            markSettingsClean('clean');
          } else {
            updateSaveBarState('reload');
          }
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
    const reload = await reloadCurrentLibrarySettings();
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
  } finally {
    setSettingsActionInProgress(false);
    updateSaveBarState();
  }
});
