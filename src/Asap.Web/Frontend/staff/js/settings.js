export * from './settings/utils.js';
export * from './settings/toggles.js';
export * from './settings/form-population.js';
export * from './settings/serialize-save.js';
export * from './settings/library-context.js';
export * from './settings/loader.js';
export * from './settings/duplicate-labels.js';
export * from './settings/polaris-fields.js';
export * from './settings/staff-access.js';
export * from './settings/save-controller.js';
export * from './settings/save-ui.js';
export * from './settings/polaris-sync.js';

import { settingsForm, defaultPublicationOptions, verifiedBibId, setVerifiedBibId, currentLibraryContextOrgId, currentLegacySettingsForm, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, settingsReloadRequired, settingsSyncInProgress, settingsSaving, settingsLoading, settingsActionInProgress, setSettingsActionInProgress, setSettingsReloadRequired } from './state.js';
import { markSettingsDirty, updateAutoRejectEmailControls, updateSaveBarState } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showConfirm } from './dialogs.js';
import { renderEditLeapBibLink } from './modals.js';
import { handleOptionListClick, addOptionListRow } from './settings-ui.js';
import { saveSettings, discardLibrarySettingsChanges } from './settings/save-controller.js';
import { toggleTimeoutGroup, toggleHoldPickupTimeoutGroup, togglePendingHoldTimeoutGroup, toggleAdditionalCopyTimeoutGroup, toggleCommonAuthorsGroup } from './settings/toggles.js';
import { refreshSettingsView, loadStaffConfig } from './settings/loader.js';
import { hasUnrelatedSettingsDraft } from './settings/serialize-save.js';
import { markAmbiguousSettingsMutation } from './settings/mutation-outcome.js';
import { bindPolarisSecretControls } from './settings/polaris-fields.js';
import './settings-labels.js';
import './settings-polaris.js';

function currentSettingsVersion() {
  return lastSavedLibrarySettingsOrgId === currentLibraryContextOrgId
    ? String(lastSavedLibrarySettingsSnapshot?.version || '')
    : '';
}

function settingsActionContextIsCurrent(organizationId, contextSerial) {
  return organizationId === currentLibraryContextOrgId && contextSerial === libraryContextLoadSerial;
}

async function reportSettingsActionError(error, actionOrganizationId, actionContextSerial) {
  const contextIsCurrent = () => actionOrganizationId === currentLibraryContextOrgId &&
    actionContextSerial === libraryContextLoadSerial;
  if (markAmbiguousSettingsMutation(error, contextIsCurrent)) return;
  if (error?.response?.code !== 'stale_version') {
    if (contextIsCurrent()) showToast(error.message, 'error');
    return;
  }

  setSettingsReloadRequired(true);
  updateSaveBarState('reload');
  const refreshStartSerial = libraryContextLoadSerial;
  let reloaded = false;
  try {
    const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true, preserveReloadRequired: true });
    if (!settings?.version) {
      throw new Error('The current Settings version was not returned.');
    }
    reloaded = true;
  } catch (refreshError) {
    console.error('Settings changed in another session, but current values could not be reloaded.', refreshError);
  }
  if (actionOrganizationId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) return;
  if (reloaded) {
    setSettingsReloadRequired(false);
    updateSaveBarState('clean');
    await loadStaffConfig();
  } else {
    updateSaveBarState('reload');
  }
  showToast(reloaded
    ? 'Settings changed in another session. Current values were reloaded; review them before trying again.'
    : 'Settings changed in another session. Reload settings before trying again.', 'error');
}

async function refreshAfterBrandingMutation(successMessage, actionOrganizationId) {
  setSettingsReloadRequired(true);
  updateSaveBarState('saving');
  const refreshStartSerial = libraryContextLoadSerial;
  try {
    const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true, preserveReloadRequired: true });
    if (actionOrganizationId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
      return;
    }
    if (!settings?.version) {
      throw new Error('The current Settings version was not returned.');
    }
    setSettingsReloadRequired(false);
    updateSaveBarState('clean');
    await loadStaffConfig();
    showToast(successMessage, 'success');
  } catch (error) {
    if (actionOrganizationId !== currentLibraryContextOrgId || libraryContextLoadSerial > refreshStartSerial + 1) {
      return;
    }
    console.error('Branding was changed, but current Settings could not be reloaded.', error);
    setSettingsReloadRequired(true);
    updateSaveBarState('reload');
    const message = 'Branding was changed, but current Settings could not be reloaded. Reload Settings before further changes.';
    const settingsMessage = document.getElementById('settings-msg');
    if (settingsMessage) {
      settingsMessage.textContent = message;
      settingsMessage.className = 'mt-2 font-weight-bold text-warning';
    }
    showToast(message, 'error');
  }
}

bindPolarisSecretControls();

document.getElementById('settings-reload-btn')?.addEventListener('click', async () => {
  if (!settingsReloadRequired || settingsSaving || settingsSyncInProgress || settingsActionInProgress || settingsLoading) return;
  const button = document.getElementById('settings-reload-btn');
  const actionOrganizationId = currentLibraryContextOrgId;
  const reloadStartSerial = libraryContextLoadSerial;
  setSettingsActionInProgress(true);
  button.disabled = true;
  try {
    const settings = await refreshSettingsView({ showErrors: false, throwOnError: true, skipAutoSync: true, preserveReloadRequired: true });
    if (actionOrganizationId !== currentLibraryContextOrgId ||
        libraryContextLoadSerial !== reloadStartSerial + 1 || settings?.orgId !== actionOrganizationId) return;
    if (!settings?.version) {
      throw new Error('The current Settings version was not returned.');
    }
    const configRefreshed = await loadStaffConfig();
    if (actionOrganizationId !== currentLibraryContextOrgId || libraryContextLoadSerial !== reloadStartSerial + 1) return;
    setSettingsReloadRequired(false);
    updateSaveBarState('clean');
    if (!configRefreshed) {
      showToast('Settings reloaded, but app configuration could not be refreshed.', 'error');
    }
  } catch (error) {
    if (actionOrganizationId !== currentLibraryContextOrgId || libraryContextLoadSerial > reloadStartSerial + 1) return;
    showToast('Current Settings could not be reloaded. Try again.', 'error');
    updateSaveBarState('reload');
  } finally {
    setSettingsActionInProgress(false);
    button.disabled = settingsSaving || settingsSyncInProgress || settingsActionInProgress || settingsLoading;
  }
});

function brandingActionBlocked() {
  if (settingsReloadRequired || settingsSyncInProgress || settingsSaving || settingsActionInProgress || settingsLoading || !currentSettingsVersion()) {
    showToast('Reload Settings before changing branding.', 'error');
    return true;
  }
  if (hasUnrelatedSettingsDraft()) {
    showToast('Save or discard other Settings changes before changing branding.', 'error');
    return true;
  }
  return false;
}

settingsForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  await saveSettings();
});
document.getElementById('settings-discard-btn')?.addEventListener('click', async (e) => {
  e.preventDefault();
  await discardLibrarySettingsChanges();
});
function handleSettingsDraftInput(event) {
  if (event.target?.id === 'select-library-context') return;
  markSettingsDirty();
}
settingsForm.addEventListener('input', handleSettingsDraftInput);
settingsForm.addEventListener('change', handleSettingsDraftInput);
document.getElementById('ui-publication-options-editor')?.addEventListener('click', handleOptionListClick);
document.getElementById('btn-add-publication-option')?.addEventListener('click', () => addOptionListRow('ui-publication-options-editor', defaultPublicationOptions));
document.querySelectorAll('.patron-copy-btn').forEach(btn => {
  btn.addEventListener('click', async () => {
    const target = document.getElementById(btn.getAttribute('data-copy-target'));
    const text = target ? String(target.value || '') : '';
    if (!text) return;
    try {
      await navigator.clipboard.writeText(text);
      showToast('Copied embed code.', 'success');
    } catch (err) {
      if (target && typeof target.select === 'function') target.select();
      showToast('Select the field text and copy it.', 'info');
    }
  });
});

document.getElementById('outstanding-timeout-enabled').addEventListener('change', () => {
  toggleTimeoutGroup();
  updateAutoRejectEmailControls();
});
document.getElementById('outstanding-timeout-send-email').addEventListener('change', updateAutoRejectEmailControls);

document.getElementById('hold-pickup-timeout-enabled').addEventListener('change', toggleHoldPickupTimeoutGroup);
document.getElementById('pending-hold-timeout-enabled').addEventListener('change', togglePendingHoldTimeoutGroup);
document.getElementById('additional-copy-timeout-enabled').addEventListener('change', toggleAdditionalCopyTimeoutGroup);
document.getElementById('wf-common-authors-enabled').addEventListener('change', toggleCommonAuthorsGroup);

document.getElementById('edit-bibid').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    document.getElementById('btn-bib-lookup').click();
  }
});

document.getElementById('edit-bibid').addEventListener('input', () => {
  const bibId = document.getElementById('edit-bibid').value.trim();
  renderEditLeapBibLink(bibId);
  if (verifiedBibId && bibId !== verifiedBibId) {
    setVerifiedBibId('');
    document.getElementById('bib-info-display').classList.add('hidden');
    document.getElementById('bib-info-text').textContent = '';
  }
});

document.getElementById('ui-logo-file').addEventListener('change', (e) => {
  const file = e.target.files[0];
  const selectedContextSerial = libraryContextLoadSerial;
  const label = document.querySelector('label[for="ui-logo-file"] + .custom-file-label') || document.querySelector('label[for="ui-logo-file"]');
  if (label) {
    label.textContent = file ? file.name : 'Choose image...';
  }
  document.getElementById('btn-clear-selected-logo').classList.toggle('hidden', !file);
  if (file) {
    const reader = new FileReader();
    reader.onload = (ev) => {
      if (selectedContextSerial !== libraryContextLoadSerial || e.target.files[0] !== file) return;
      const preview = document.getElementById('ui-logo-preview');
      if (preview) preview.src = ev.target.result;
    };
    reader.readAsDataURL(file);
  } else {
    const preview = document.getElementById('ui-logo-preview');
    if (preview?.dataset.authoritativeSrc) preview.src = preview.dataset.authoritativeSrc;
  }
});

document.getElementById('btn-clear-selected-logo').addEventListener('click', () => {
  const fileInput = document.getElementById('ui-logo-file');
  fileInput.value = '';
  document.querySelector('.custom-file-label[for="ui-logo-file"]').textContent = 'Choose image...';
  const preview = document.getElementById('ui-logo-preview');
  if (preview?.dataset.authoritativeSrc) preview.src = preview.dataset.authoritativeSrc;
  document.getElementById('btn-clear-selected-logo').classList.add('hidden');
  markSettingsDirty();
});

document.getElementById('btn-upload-logo').addEventListener('click', async () => {
  if (brandingActionBlocked()) return;
  const actionOrganizationId = currentLibraryContextOrgId;
  const actionContextSerial = libraryContextLoadSerial;
  const submittedVersion = currentSettingsVersion();
  const fileInput = document.getElementById('ui-logo-file');
  const altInput = document.getElementById('ui-logo-alt');
  const btn = document.getElementById('btn-upload-logo');
  const baselineAlt = String(currentLegacySettingsForm?.uiText?.logoAlt ?? '').trim();
  const altEdited = altInput.value.trim() !== baselineAlt;
  if (!fileInput.files.length && !altEdited) {
    showToast('No branding changes to save.', 'info');
    return;
  }

  const formData = new FormData();
  if (fileInput.files.length > 0) {
    formData.append('logo', fileInput.files[0]);
  }
  if (altEdited) formData.append('logoAlt', altInput.value.trim());
  formData.append('version', submittedVersion);

  setSettingsActionInProgress(true);
  updateSaveBarState('saving');
  btn.disabled = true;
  const originalNodes = Array.from(btn.childNodes);
  const spinner = document.createElement('i');
  spinner.className = 'fa fa-spinner fa-spin mr-1';
  btn.replaceChildren(spinner, document.createTextNode(' Saving...'));

  try {
    await authorizedJson(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(actionOrganizationId)}`, {
      method: 'POST',
      body: formData
    });

    if (!settingsActionContextIsCurrent(actionOrganizationId, actionContextSerial)) return;
    await refreshAfterBrandingMutation('Branding updated successfully.', actionOrganizationId);
  } catch (err) {
    if (settingsActionContextIsCurrent(actionOrganizationId, actionContextSerial)) {
      await reportSettingsActionError(err, actionOrganizationId, actionContextSerial);
    }
  } finally {
    setSettingsActionInProgress(false);
    updateSaveBarState();
    btn.disabled = false;
    btn.replaceChildren(...originalNodes);
  }
});

document.getElementById('btn-reset-logo').addEventListener('click', async () => {
  if (brandingActionBlocked()) return;
  if (document.getElementById('ui-logo-file').files.length) {
    showToast('Clear the selected logo file before resetting branding.', 'error');
    return;
  }
  const actionOrganizationId = currentLibraryContextOrgId;
  const actionContextSerial = libraryContextLoadSerial;
  const submittedVersion = currentSettingsVersion();
  if (!await showConfirm('Reset branding?', 'This will delete the library-specific logo and alt text and use the system defaults.')) {
    return;
  }
  if (!settingsActionContextIsCurrent(actionOrganizationId, actionContextSerial) || brandingActionBlocked()) return;

  const btn = document.getElementById('btn-reset-logo');
  setSettingsActionInProgress(true);
  updateSaveBarState('saving');
  btn.disabled = true;

  try {
    await authorizedJson(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(actionOrganizationId)}&version=${encodeURIComponent(submittedVersion)}`, {
      method: 'DELETE'
    });

    if (!settingsActionContextIsCurrent(actionOrganizationId, actionContextSerial)) return;
    await refreshAfterBrandingMutation('Branding reset to system defaults.', actionOrganizationId);
  } catch (err) {
    if (settingsActionContextIsCurrent(actionOrganizationId, actionContextSerial)) {
      await reportSettingsActionError(err, actionOrganizationId, actionContextSerial);
    }
  } finally {
    setSettingsActionInProgress(false);
    updateSaveBarState();
    btn.disabled = false;
  }
});
