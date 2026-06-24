export * from './settings/utils.js';
export * from './settings/toggles.js';
export * from './settings/form-population.js';
export * from './settings/serialize-save.js';
export * from './settings/library-context.js';
export * from './settings/loader.js';
export * from './settings/duplicate-labels.js';
export * from './settings/polaris-fields.js';
export * from './settings/staff-access.js';

import { settingsForm, defaultPublicationOptions, verifiedBibId, setVerifiedBibId, currentLibraryContextOrgId, pb } from './state.js';
import { markSettingsDirty, updateAutoRejectEmailControls } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showConfirm } from './dialogs.js';
import { renderEditLeapBibLink } from './modals.js';
import { handleOptionListClick, addOptionListRow } from './settings-ui.js';
import { saveSettings, discardLibrarySettingsChanges } from './settings/serialize-save.js';
import { toggleTimeoutGroup, toggleHoldPickupTimeoutGroup, togglePendingHoldTimeoutGroup, toggleAdditionalCopyTimeoutGroup, toggleCommonAuthorsGroup } from './settings/toggles.js';
import { refreshSettingsView, loadStaffConfig } from './settings/loader.js';

settingsForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  await saveSettings();
});
document.getElementById('settings-discard-btn')?.addEventListener('click', (e) => {
  e.preventDefault();
  discardLibrarySettingsChanges();
});
settingsForm.addEventListener('input', markSettingsDirty);
settingsForm.addEventListener('change', markSettingsDirty);
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
  const label = document.querySelector('label[for="ui-logo-file"] + .custom-file-label') || document.querySelector('label[for="ui-logo-file"]');
  if (label) {
    label.textContent = file ? file.name : 'Choose image...';
  }
  if (file) {
    const reader = new FileReader();
    reader.onload = (ev) => {
      const preview = document.getElementById('ui-logo-preview');
      if (preview) preview.src = ev.target.result;
    };
    reader.readAsDataURL(file);
  }
});

document.getElementById('btn-upload-logo').addEventListener('click', async () => {
  const fileInput = document.getElementById('ui-logo-file');
  const altInput = document.getElementById('ui-logo-alt');
  const btn = document.getElementById('btn-upload-logo');

  const formData = new FormData();
  if (fileInput.files.length > 0) {
    formData.append('logo', fileInput.files[0]);
  }
  formData.append('logoAlt', altInput.value.trim());

  btn.disabled = true;
  const originalNodes = Array.from(btn.childNodes);
  const spinner = document.createElement('i');
  spinner.className = 'fa fa-spinner fa-spin mr-1';
  btn.replaceChildren(spinner, document.createTextNode(' Saving...'));

  try {
    await authorizedJson(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(currentLibraryContextOrgId)}`, {
      method: 'POST',
      body: formData
    });

    showToast('Branding updated successfully.');
    await refreshSettingsView({ showErrors: true });
    await loadStaffConfig();
  } catch (err) {
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.replaceChildren(...originalNodes);
  }
});

document.getElementById('btn-reset-logo').addEventListener('click', async () => {
  if (!await showConfirm('Reset branding?', 'This will delete the library-specific logo and fallback to the system default.')) {
    return;
  }

  const btn = document.getElementById('btn-reset-logo');
  btn.disabled = true;

  try {
    await authorizedJson(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(currentLibraryContextOrgId)}`, {
      method: 'DELETE'
    });

    showToast('Branding reset to system defaults.');
    await refreshSettingsView({ showErrors: true });
    await loadStaffConfig();
  } catch (err) {
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
  }
});
