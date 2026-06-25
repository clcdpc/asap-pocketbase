import { isSuperAdminStaff, validateSmtpHostField, isPocketBaseAutoCancelError, updateSaveBarState, markSettingsClean } from '../api.js';
import { authorizedJson } from '../http.js';
import { showToast } from '../dialogs.js';
import { settingsForm, currentLibraryContextOrgId, initialSettingsSnapshot, settingsDirty, setSettingsSaving, setSettingsLoading, setInitialSettingsSnapshot, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId } from '../state.js';
import { refreshSettingsView, loadStaffConfig } from './refresh.js';
import { loadStaffUsers } from '../settings-users.js';
import { cloneLibrarySettingsSnapshot, captureSettingsBaseline, serializeSettingsState, buildSettingsPayload } from './serialize-save.js';
import { applyLibrarySettingsToForm } from './form-population.js';

export async function saveSettings(options = {}) {
  const submitBtn = settingsForm.querySelector('button[type="submit"]');
  const triggerBtn = options.button || null;
  const buttons = Array.from(new Set([submitBtn, triggerBtn].filter(Boolean)));
  const msg = document.getElementById('settings-msg');
  let saveHadError = false;
  let saveSucceeded = false;

  setSettingsSaving(true);
  updateSaveBarState('saving');
  buttons.forEach(button => {
    button.disabled = true;
  });
  msg.textContent = options.pendingText || 'Saving...';
  msg.className = 'mt-2 font-weight-bold text-info';

  try {
    if (isSuperAdminStaff() && currentLibraryContextOrgId === 'system') {
      if (!validateSmtpHostField(true)) {
        throw new Error('SMTP host is invalid.');
      }
    }
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();

    const isSystemSave = currentLibraryContextOrgId === 'system';
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      emails: payload.emails,
      ui_text: payload.ui_text,
      formatClaimRules: isSystemSave ? [] : payload.formatClaimRules,
      workflow: {
        suggestionLimit: payload.suggestionLimit,
        suggestionLimitMessage: payload.suggestionLimitMessage,
        outstandingTimeoutEnabled: payload.outstandingTimeoutEnabled,
        outstandingTimeoutDays: payload.outstandingTimeoutDays,
        outstandingTimeoutSendEmail: payload.outstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId: payload.outstandingTimeoutRejectionTemplateId,
        holdPickupTimeoutEnabled: payload.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: payload.holdPickupTimeoutDays,
        pendingHoldTimeoutEnabled: payload.pendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays: payload.pendingHoldTimeoutDays,
        additionalCopyTimeoutEnabled: payload.additionalCopyTimeoutEnabled,
        additionalCopyTimeoutDays: payload.additionalCopyTimeoutDays,
        enabledLibraryOrgIds: payload.enabledLibraryOrgIds,
        commonAuthorsEnabled: payload.commonAuthorsEnabled,
        commonAuthorsLabel: payload.commonAuthorsLabel,
        commonAuthorsHelp: payload.commonAuthorsHelp,
        commonAuthorsList: payload.commonAuthorsList,
        commonAuthorsMessage: payload.commonAuthorsMessage,
        autoPromote: payload.autoPromote,
        allowPatronAutoholdOptOut: payload.allowPatronAutoholdOptOut,
        allowAnyRegisteredCardLogin: payload.allowAnyRegisteredCardLogin,
        externalSearch1Enabled: payload.externalSearch1Enabled,
        externalSearch1Label: payload.externalSearch1Label,
        externalSearch1UrlTemplate: payload.externalSearch1UrlTemplate,
        externalSearch2Enabled: payload.externalSearch2Enabled,
        externalSearch2Label: payload.externalSearch2Label,
        externalSearch2UrlTemplate: payload.externalSearch2UrlTemplate,
        externalSearch3Enabled: payload.externalSearch3Enabled,
        externalSearch3Label: payload.externalSearch3Label,
        externalSearch3UrlTemplate: payload.externalSearch3UrlTemplate,
        externalSearch4Enabled: payload.externalSearch4Enabled,
        externalSearch4Label: payload.externalSearch4Label,
        externalSearch4UrlTemplate: payload.externalSearch4UrlTemplate
      }
    };

    if (isSystemSave) {
      libraryPayload.staffUrl = payload.staffUrl;
      libraryPayload.leapBibUrlPattern = payload.leapBibUrlPattern;
      libraryPayload.leapPatronUrlPattern = payload.leapPatronUrlPattern;
      libraryPayload.smtp = payload.smtp;
      libraryPayload.polaris = payload.polaris;
      libraryPayload.patronEmbedAllowedOrigins = payload.patronEmbedAllowedOrigins;
    }

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: libraryPayload
    });

    await libraryPromise;
    captureSettingsBaseline();
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    await refreshSettingsView({ showErrors: false });
    await loadStaffConfig();
    loadStaffUsers();
    saveSucceeded = true;
    showToast('Settings saved.', 'success');
    return true;
  } catch (err) {
    saveHadError = true;
    console.error(err);
    msg.textContent = err.message || 'Failed to save settings.';
    msg.className = 'mb-3 font-weight-bold text-danger';
    updateSaveBarState('error');
    return false;
  } finally {
    setSettingsSaving(false);
    buttons.forEach(button => {
      button.disabled = false;
    });
    updateSaveBarState(saveHadError ? 'error' : (saveSucceeded ? 'saved' : (settingsDirty ? 'dirty' : 'clean')));
  }
}

export function discardLibrarySettingsChanges() {
  if (!lastSavedLibrarySettingsSnapshot || lastSavedLibrarySettingsOrgId !== (currentLibraryContextOrgId || 'system')) return;
  setSettingsLoading(true);
  try {
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
