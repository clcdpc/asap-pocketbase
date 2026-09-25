import { isRequestCanceledError, updateSaveBarState, markSettingsClean, getFieldValue, getFieldChecked } from '../api.js';
import { authorizedJson } from '../http.js';
import { showToast } from '../dialogs.js';
import { settingsForm, currentLibraryContextOrgId, currentSettingsSection, initialSettingsSnapshot, settingsDirty, settingsReloadRequired, settingsSyncInProgress, settingsSaving, settingsLoading, settingsActionInProgress, setSettingsReloadRequired, setSettingsSaving, setSettingsLoading, setInitialSettingsSnapshot, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, deletedSettingsFormats, setDeletedSettingsFormats } from '../state.js';
import { refreshSettingsView, loadStaffConfig } from './refresh.js';
import { loadStaffUsers } from '../settings-users.js';
import { cloneLibrarySettingsSnapshot, captureSettingsBaseline, serializeSettingsState, buildSettingsPayload, buildEmailSettingsPayload } from './serialize-save.js';
import { applyLibrarySettingsToForm } from './form-population.js';
import { deleteSettingsFormatsSequentially } from './delete-formats.js';
import { isAmbiguousMutationError, markAmbiguousSettingsMutation } from './mutation-outcome.js';

export async function saveSettings(options = {}) {
  if (settingsReloadRequired || settingsSyncInProgress || settingsSaving || settingsLoading || settingsActionInProgress) {
    const message = settingsSyncInProgress
      ? 'Wait for organization synchronization and the Settings reload before saving.'
      : 'Current Settings could not be reloaded. Reload Settings before saving.';
    const msg = document.getElementById('settings-msg');
    msg.textContent = message;
    msg.className = 'mt-2 font-weight-bold text-warning';
    showToast(message, 'error', 'settings-save-toast');
    return false;
  }
  if (document.getElementById('ui-logo-file')?.files?.length) {
    const message = 'Save branding or clear the selected logo file before saving Settings.';
    const msg = document.getElementById('settings-msg');
    msg.textContent = message;
    msg.className = 'mt-2 font-weight-bold text-warning';
    showToast(message, 'error', 'settings-save-toast');
    return false;
  }
  const submitBtn = settingsForm.querySelector('button[type="submit"]');
  const triggerBtn = options.button || null;
  const buttons = Array.from(new Set([submitBtn, triggerBtn].filter(Boolean)));
  const msg = document.getElementById('settings-msg');
  let saveHadError = false;
  let formatDeletionError = null;
  let refreshError = null;
  let saveSucceeded = false;
  let saveSuperseded = false;
  let mutationPending = false;
  const saveContextOrgId = currentLibraryContextOrgId;
  const saveContextSerial = libraryContextLoadSerial;
  const saveContextVersion = lastSavedLibrarySettingsOrgId === saveContextOrgId
    ? lastSavedLibrarySettingsSnapshot?.version
    : null;

  if (!saveContextVersion) {
    setSettingsReloadRequired(true);
    updateSaveBarState('reload');
    msg.textContent = 'The current Settings version is unavailable. Reload Settings before saving.';
    return false;
  }
  setSettingsSaving(true);
  updateSaveBarState('saving');
  buttons.forEach(button => {
    button.disabled = true;
  });
  msg.textContent = options.pendingText || 'Saving...';
  msg.className = 'mt-2 font-weight-bold text-info';

  async function deletePendingFormats() {
    const pending = [...deletedSettingsFormats];
    await deleteSettingsFormatsSequentially(pending, async format => {
      if (!format.id || !format.version) {
        throw new Error('The custom format version is unavailable. Reload settings before deleting it.');
      }
      await authorizedJson(`/api/asap/staff/settings/formats/${encodeURIComponent(String(format.id))}?version=${encodeURIComponent(String(format.version))}`, {
        method: 'DELETE'
      });
    }, deleted => {
      setDeletedSettingsFormats(deletedSettingsFormats.filter(format => String(format.id) !== String(deleted.id)));
    }, () => saveContextOrgId === currentLibraryContextOrgId && saveContextSerial === libraryContextLoadSerial);
  }

  try {
    const isEmailSave = currentSettingsSection === 'smtp';
    const payload = isEmailSave
      ? { emails: buildEmailSettingsPayload({ includeTemplates: false, useSmtpFields: true }) }
      : buildSettingsPayload();

    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      version: lastSavedLibrarySettingsOrgId === currentLibraryContextOrgId
        ? lastSavedLibrarySettingsSnapshot?.version : null,
      ...payload
    };

    mutationPending = true;
    const libraryPromise = authorizedJson('/api/asap/staff/legacy/settings', {
      method: 'POST',
      body: libraryPayload
    });

    await libraryPromise;
    mutationPending = false;
    saveSucceeded = true;
    if (saveContextOrgId !== currentLibraryContextOrgId || saveContextSerial !== libraryContextLoadSerial) {
      saveSuperseded = true;
      return false;
    }
    if (deletedSettingsFormats.length > 0) {
      try {
        await deletePendingFormats();
      } catch (error) {
        formatDeletionError = error;
      }
    }
    if (saveContextOrgId !== currentLibraryContextOrgId || saveContextSerial !== libraryContextLoadSerial) {
      saveSuperseded = true;
      return false;
    }
    setSettingsReloadRequired(true);
    // Clear the data-loaded flag so library participation checkboxes re-render after save
    const libCheckboxContainer = document.getElementById('enabled-libraries-checkbox-container');
    if (libCheckboxContainer) libCheckboxContainer.removeAttribute('data-loaded');
    const refreshStartSerial = libraryContextLoadSerial;
    try {
      const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true, preserveReloadRequired: true });
      if (!settings?.version) {
        throw new Error('The current Settings version was not returned.');
      }
      if (saveContextOrgId === currentLibraryContextOrgId && libraryContextLoadSerial <= refreshStartSerial + 1) {
        setSettingsReloadRequired(false);
      }
    } catch (error) {
      refreshError = error;
      if (!isRequestCanceledError(error)) {
        console.error('Settings were saved, but the refreshed values could not be loaded.', error);
      }
    }
    if (saveContextOrgId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
      saveSuperseded = true;
      return false;
    }
    if (formatDeletionError) {
      saveHadError = true;
      if (isAmbiguousMutationError(formatDeletionError)) {
        const message = refreshError
          ? 'Settings were saved, but the custom format removal result could not be confirmed. Reload Settings before further changes.'
          : 'Settings were saved, but the custom format removal result could not be confirmed. Review the reloaded format list before continuing.';
        msg.textContent = message;
        msg.className = 'mt-2 font-weight-bold text-warning';
        showToast(message, 'error', 'settings-save-toast');
        return false;
      }
      const detail = formatDeletionError.message || 'The custom format could not be deleted.';
      const refreshNote = refreshError ? ' Current settings could not be reloaded; reload before continuing.' : ' The format list has been reloaded.';
      msg.textContent = `Settings were saved, but custom format removal did not complete: ${detail}.${refreshNote}`;
      msg.className = 'mt-2 font-weight-bold text-warning';
      showToast('Settings were saved, but custom format removal did not complete.', 'error', 'settings-save-toast');
      return false;
    }
    if (refreshError) {
      const detail = refreshError.message || 'The current values could not be reloaded.';
      msg.textContent = `Settings were saved, but the current values could not be reloaded: ${detail}`;
      msg.className = 'mt-2 font-weight-bold text-warning';
      showToast('Settings were saved, but the current values could not be reloaded.', 'error', 'settings-save-toast');
      return true;
    }
    captureSettingsBaseline();
    await loadStaffConfig();
    loadStaffUsers();
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    showToast('Settings saved.', 'success', 'settings-save-toast');
    return true;
  } catch (err) {
    saveHadError = true;
    console.error(err);
    if (saveContextOrgId !== currentLibraryContextOrgId || saveContextSerial !== libraryContextLoadSerial) {
      saveSuperseded = true;
      return false;
    }
    if (mutationPending && markAmbiguousSettingsMutation(err, () =>
      saveContextOrgId === currentLibraryContextOrgId && saveContextSerial === libraryContextLoadSerial)) {
      return false;
    }
    let message = err.message || 'Failed to save settings.';
    if (err?.response?.code === 'stale_version') {
      setSettingsReloadRequired(true);
      const refreshStartSerial = libraryContextLoadSerial;
      let refreshed = false;
      try {
        const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true, preserveReloadRequired: true });
        if (!settings?.version) {
          throw new Error('The current Settings version was not returned.');
        }
        refreshed = true;
      } catch (refreshErr) {
        if (!isRequestCanceledError(refreshErr)) {
          console.error('Settings changed in another session, but current values could not be reloaded.', refreshErr);
        }
      }
      if (saveContextOrgId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
        saveSuperseded = true;
        return false;
      }
      if (refreshed) {
        setSettingsReloadRequired(false);
        await loadStaffConfig();
        loadStaffUsers();
      }
      message = refreshed
        ? 'Settings changed in another session. Current values were reloaded; review them before saving again.'
        : 'Settings changed in another session. Reload settings before trying again.';
      showToast(message, 'error', 'settings-save-toast');
    }
    msg.textContent = message;
    msg.className = 'mb-3 font-weight-bold text-danger';
    updateSaveBarState('error');
    return false;
  } finally {
    setSettingsSaving(false);
    buttons.forEach(button => {
      button.disabled = false;
    });
    if (!saveSuperseded) {
      updateSaveBarState(settingsReloadRequired ? 'reload' : (saveHadError ? 'error' : (saveSucceeded ? 'saved' : (settingsDirty ? 'dirty' : 'clean'))));
    }
  }
}

export async function discardLibrarySettingsChanges() {
  if (settingsReloadRequired || settingsSaving || settingsLoading || settingsSyncInProgress || settingsActionInProgress) return;
  if (!lastSavedLibrarySettingsSnapshot || lastSavedLibrarySettingsOrgId !== (currentLibraryContextOrgId || 'system')) return;
  setSettingsLoading(true);
  try {
    const libCheckboxContainer = document.getElementById('enabled-libraries-checkbox-container');
    if (libCheckboxContainer) libCheckboxContainer.removeAttribute('data-loaded');
    await applyLibrarySettingsToForm(cloneLibrarySettingsSnapshot(lastSavedLibrarySettingsSnapshot));
    captureSettingsBaseline();
    markSettingsClean('clean');
    const msg = document.getElementById('settings-msg');
    if (msg) {
      msg.textContent = '';
      msg.className = 'mt-2 font-weight-bold';
    }
  } finally {
    setSettingsLoading(false);
  }
}
