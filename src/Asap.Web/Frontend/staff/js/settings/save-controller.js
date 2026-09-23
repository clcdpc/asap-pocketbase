import { isRequestCanceledError, updateSaveBarState, markSettingsClean, getFieldValue, getFieldChecked } from '../api.js';
import { authorizedJson } from '../http.js';
import { showToast } from '../dialogs.js';
import { settingsForm, currentLibraryContextOrgId, currentSettingsSection, initialSettingsSnapshot, settingsDirty, setSettingsSaving, setSettingsLoading, setInitialSettingsSnapshot, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, deletedSettingsFormats, setDeletedSettingsFormats } from '../state.js';
import { refreshSettingsView, loadStaffConfig } from './refresh.js';
import { loadStaffUsers } from '../settings-users.js';
import { cloneLibrarySettingsSnapshot, captureSettingsBaseline, serializeSettingsState, buildSettingsPayload, buildEmailSettingsPayload } from './serialize-save.js';
import { applyLibrarySettingsToForm } from './form-population.js';
import { deleteSettingsFormatsSequentially } from './delete-formats.js';

export async function saveSettings(options = {}) {
  const submitBtn = settingsForm.querySelector('button[type="submit"]');
  const triggerBtn = options.button || null;
  const buttons = Array.from(new Set([submitBtn, triggerBtn].filter(Boolean)));
  const msg = document.getElementById('settings-msg');
  let saveHadError = false;
  let formatDeletionError = null;
  let refreshError = null;
  let saveSucceeded = false;
  let saveSuperseded = false;
  const saveContextOrgId = currentLibraryContextOrgId;
  const saveContextSerial = libraryContextLoadSerial;
  const saveContextVersion = lastSavedLibrarySettingsOrgId === saveContextOrgId
    ? lastSavedLibrarySettingsSnapshot?.version
    : null;

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

    const isSystemSave = currentLibraryContextOrgId === 'system';
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      version: lastSavedLibrarySettingsOrgId === currentLibraryContextOrgId
        ? lastSavedLibrarySettingsSnapshot?.version : null,
      emails: payload.emails,
      ...(isEmailSave ? {} : {
        ui_text: payload.ui_text,
        ...(!isSystemSave && Object.hasOwn(payload, 'formatClaimRules') ? { formatClaimRules: payload.formatClaimRules } : {}),
        ...(Object.hasOwn(payload, 'providers') ? { providers: payload.providers } : {}),
        ...(Object.hasOwn(payload, 'formats') ? { formats: payload.formats } : {}),
        ...(Object.hasOwn(payload, 'customFields') ? { customFields: payload.customFields } : {}),
        workflow: Object.fromEntries([
          'suggestionLimit', 'suggestionLimitMessage', 'outstandingTimeoutEnabled', 'outstandingTimeoutDays',
          'outstandingTimeoutSendEmail', 'outstandingTimeoutRejectionTemplateId', 'holdPickupTimeoutEnabled',
          'holdPickupTimeoutDays', 'pendingHoldTimeoutEnabled', 'pendingHoldTimeoutDays',
          'additionalCopyTimeoutEnabled', 'additionalCopyTimeoutDays', 'commonAuthorsEnabled',
          'commonAuthorsLabel', 'commonAuthorsHelp', 'commonAuthorsList', 'commonAuthorsMessage',
          'autoPromote', 'allowPatronAutoholdOptOut', 'allowAnyRegisteredCardLogin',
          'patronCodeEligibilityEnabled', 'allowedPatronCodeIds', 'patronCodeEligibilityMessage'
        ].filter(key => Object.hasOwn(payload, key)).map(key => [key, payload[key]]))
      })
    };

    if (isSystemSave && !isEmailSave) {
      libraryPayload.staffUrl = payload.staffUrl;
      libraryPayload.leapBibUrlPattern = payload.leapBibUrlPattern;
      libraryPayload.leapPatronUrlPattern = payload.leapPatronUrlPattern;
      libraryPayload.formatIconUrlPattern = payload.formatIconUrlPattern;
      libraryPayload.polaris = payload.polaris;
      libraryPayload.patronEmbedAllowedOrigins = payload.patronEmbedAllowedOrigins;
      if (Object.hasOwn(payload, 'enabledLibraryOrgIds')) {
        libraryPayload.enabledLibraryOrgIds = payload.enabledLibraryOrgIds;
      }
    }

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: libraryPayload
    });

    await libraryPromise;
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
    // Clear the data-loaded flag so library participation checkboxes re-render after save
    const libCheckboxContainer = document.getElementById('enabled-libraries-checkbox-container');
    if (libCheckboxContainer) libCheckboxContainer.removeAttribute('data-loaded');
    const refreshStartSerial = libraryContextLoadSerial;
    try {
      await refreshSettingsView({ showErrors: false, throwOnError: true });
      await loadStaffConfig();
      loadStaffUsers();
    } catch (error) {
      if (!isRequestCanceledError(error)) {
        refreshError = error;
        console.error('Settings were saved, but the refreshed values could not be loaded.', error);
      }
    }
    if (saveContextOrgId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
      saveSuperseded = true;
      return false;
    }
    if (formatDeletionError) {
      saveHadError = true;
      const detail = formatDeletionError.message || 'The custom format could not be deleted.';
      const refreshNote = refreshError ? ' Current settings could not be reloaded; reload before continuing.' : ' The format list has been reloaded.';
      msg.textContent = `Settings were saved, but custom format removal did not complete: ${detail}.${refreshNote}`;
      msg.className = 'mt-2 font-weight-bold text-warning';
      showToast('Settings were saved, but custom format removal did not complete.', 'error');
      return false;
    }
    captureSettingsBaseline();
    if (refreshError) {
      const detail = refreshError.message || 'The current values could not be reloaded.';
      msg.textContent = `Settings were saved, but the current values could not be reloaded: ${detail}`;
      msg.className = 'mt-2 font-weight-bold text-warning';
      showToast('Settings were saved, but the current values could not be reloaded.', 'error');
      return true;
    }
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    showToast('Settings saved.', 'success');
    return true;
  } catch (err) {
    saveHadError = true;
    console.error(err);
    if (saveContextOrgId !== currentLibraryContextOrgId || saveContextSerial !== libraryContextLoadSerial) {
      saveSuperseded = true;
      return false;
    }
    let message = err.message || 'Failed to save settings.';
    if (err?.response?.code === 'stale_version') {
      const refreshStartSerial = libraryContextLoadSerial;
      try {
        await refreshSettingsView({ showErrors: false, throwOnError: true });
        await loadStaffConfig();
        await loadStaffUsers();
      } catch (refreshErr) {
        if (!isRequestCanceledError(refreshErr)) {
          console.error('Settings changed in another session, but current values could not be reloaded.', refreshErr);
        }
      }
      if (saveContextOrgId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
        saveSuperseded = true;
        return false;
      }
      const refreshed = lastSavedLibrarySettingsOrgId === saveContextOrgId &&
        !!lastSavedLibrarySettingsSnapshot?.version &&
        lastSavedLibrarySettingsSnapshot.version !== saveContextVersion;
      message = refreshed
        ? 'Settings changed in another session. Current values were reloaded; review them before saving again.'
        : 'Settings changed in another session. Reload settings before trying again.';
      showToast(message, 'error');
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
      updateSaveBarState(saveHadError ? 'error' : (saveSucceeded ? 'saved' : (settingsDirty ? 'dirty' : 'clean')));
    }
  }
}

export function discardLibrarySettingsChanges() {
  if (!lastSavedLibrarySettingsSnapshot || lastSavedLibrarySettingsOrgId !== (currentLibraryContextOrgId || 'system')) return;
  setSettingsLoading(true);
  try {
    const libCheckboxContainer = document.getElementById('enabled-libraries-checkbox-container');
    if (libCheckboxContainer) libCheckboxContainer.removeAttribute('data-loaded');
    applyLibrarySettingsToForm(cloneLibrarySettingsSnapshot(lastSavedLibrarySettingsSnapshot));
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
