import { pb, settingsContainer, settingsForm, formatMap, availableFormats, setAvailableFormats, currentRejectionTemplates, verifiedBibId, publicationOptions, setPublicationOptions, workflowSettings, currentLibraryContextOrgId, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, initialSettingsSnapshot, libraryContextLoadSerial, librarySelectorBound, organizationsStatus, setOrganizationsStatus, organizationsStatusMessage, currentSettingsSection, settingsDirty, settingsSaving, settingsLoading, leapBibUrlPattern, lastWorkflowEnabledList, defaultPublicationOptions, emailTemplateDefaults, setVerifiedBibId, setCurrentLibraryContextOrgId, setCurrentFormatClaimRules, setFormatClaimStaffOptions, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId, setInitialSettingsSnapshot, setLibrarySelectorBound, setSettingsSaving, setSettingsLoading, setLeapBibUrlPattern, setLastWorkflowEnabledList, incrementLibraryContextLoadSerial, libraryOverridesSummary, setLibraryOverridesSummary } from './state.js';

import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, isPocketBaseAutoCancelError, validateSmtpHostField, setVisible, showToast, showConfirm, isSuperAdminStaff, closeOpenDialogs, updateSaveBarState, markSettingsDirty, markSettingsClean, activateSettingsSection, initSettingsNavigation, updateEmailStatusBanner, updateOrganizationsStatusUi, checkAuth, loadSetupStatus, authorizedJson, updateAutoRejectEmailControls, updateLibraryOverrideStatusVisibility } from './api.js';
import { closeActionMenu, escapeAttr } from './grid.js';
import { renderEditLeapBibLink } from './modals.js';
import { renderFormatSettings, collectFormatLabels, collectAvailableFormats, collectFormatOrder, collectFormatClaimRules, updateModalFormatDropdowns } from './settings-formats.js';
import { renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels } from './settings-labels.js';
import { collectSettingsPolaris, syncPolarisOrganizations, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds } from './settings-polaris.js';
import { populateEmailTemplateForms } from './settings-templates.js';
import { updatePublicationOptionsUi, renderPatronFormatRulesEditor, collectPatronFormatRules, renderOptionListEditor, collectOptionList, addOptionListRow, handleOptionListClick } from './settings-ui.js';
import { loadStaffUsers, populateStaffLibraryOptions } from './settings-users.js';

const adminSettingsSections = ['start', 'staff', 'templates', 'workflow', 'patron'];
const SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY = 'asap.superAdmin.settings.libraryContextOrgId';

export function normalizeExternalSearchUrlTemplate(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  return /^https?:\/\//i.test(text) ? text : `https://${text}`;
}

export function showSettingsAccessDenied() {
  settingsContainer.classList.remove('hidden');
  setVisible('settings-error', true);
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.add('hidden');
}

export function hideSettingsAccessDenied() {
  setVisible('settings-error', false);
}

function readSavedSuperAdminLibraryContext() {
  try {
    const value = window.localStorage.getItem(SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY);
    return String(value || '').trim();
  } catch (err) {
    return '';
  }
}

function saveSuperAdminLibraryContext(orgId) {
  try {
    window.localStorage.setItem(SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY, String(orgId || 'system'));
  } catch (err) {}
}

export async function fetchLibraryOverridesSummary() {
  try {
    const summary = await authorizedJson('/api/asap/staff/settings/overrides-summary');
    setLibraryOverridesSummary(summary);
  } catch (err) {
    if (!isPocketBaseAutoCancelError(err)) {
      console.error('Failed to fetch library overrides summary', err);
    }
  }
}

export function refreshLibrarySelectorIndicators() {
  const select = document.getElementById('select-library-context');
  if (!select) return;

  const summary = libraryOverridesSummary || {};
  const activeSection = currentSettingsSection;

  Array.from(select.options).forEach(opt => {
    if (opt.value === 'system') return;
    
    // Remove existing indicator if present
    let text = opt.textContent.replace(/ ●$/, '');
    
    const sections = summary[opt.value] || [];
    if (sections.includes(activeSection)) {
      text += ' ●';
    }
    
    if (opt.textContent !== text) {
      opt.textContent = text;
      // Also update the display text if this is the selected option
      if (opt.value === currentLibraryContextOrgId) {
        const display = document.getElementById('library-context-display');
        if (display) display.textContent = text;
      }
    }
  });
}

export async function loadSettings(options = {}) {
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;
  setSettingsLoading(true);

  try {
    updateSettingsSidebar(isSuper);
    ensureAllowedSettingsSection(isSuper);
    await loadLibraryContext(isSuper);

    const loadedLibrarySettings = await loadLibrarySettings(currentLibraryContextOrgId);

    if (!isSuper) {
      await loadLibraryAdminSettings();
      return;
    }

    const polaris = (loadedLibrarySettings && loadedLibrarySettings.polaris) || {};
    maybeSyncPolarisOrganizations(polaris);
    updateWorkflowSettingsSummary(loadedLibrarySettings);

    // Workflow form population is handled by loadLibrarySettings

    populateSystemSettingsForms(loadedLibrarySettings);
    await loadStaffAccessSettings();
    showSettingsForm();

  } catch (err) {
    handleLoadSettingsError(err, showErrors);
  } finally {
    setSettingsLoading(false);
    markSettingsClean('clean');
  }
}

function updateSettingsSidebar(isSuper) {
  document.querySelectorAll('[data-settings-target]').forEach(el => {
    const section = el.getAttribute('data-settings-target');
    if (!isSuper && !adminSettingsSections.includes(section)) {
      el.classList.add('hidden');
    } else {
      el.classList.remove('hidden');
    }
  });
}

function ensureAllowedSettingsSection(isSuper) {
  if (!isSuper && !adminSettingsSections.includes(currentSettingsSection)) {
    activateSettingsSection('workflow', { updateHash: true });
  }
}

async function loadLibraryContext(isSuper) {
  const selector = document.getElementById('super-admin-library-selector');

  if (isSuper) {
    await populateLibrarySelector();
    selector.classList.remove('hidden');
    return;
  }

  selector.classList.add('hidden');
  setCurrentLibraryContextOrgId(pb.authStore.model.libraryOrgId || 'system');
  const libraryName = pb.authStore.model.libraryOrgName || 'My Library';
  document.getElementById('library-context-display').textContent = currentLibraryContextOrgId === 'system'
    ? libraryName
    : `${libraryName} (ID ${currentLibraryContextOrgId})`;
}

async function loadLibraryAdminSettings() {
  // Library admins can still edit library-scoped settings even without system settings access.
  showSettingsForm();
  await loadStaffAccessSettings();
  updateSaveButtonText();
}

async function loadStaffAccessSettings() {
  await populateStaffLibraryOptions();
  await loadStaffUsers();
}

function maybeSyncPolarisOrganizations(polaris) {
  const hasPolarisCredentials = !!(polaris.host && polaris.apiKey && polaris.accessId && polaris.staffDomain && polaris.adminUser && polaris.adminPassword);
  if (hasPolarisCredentials && (organizationsStatus === 'not_loaded' || organizationsStatus === 'error')) {
    syncPolarisOrganizations().catch(() => {
      // syncPolarisOrganizations updates the visible warning state.
    });
  }
}

function updateWorkflowSettingsSummary(settings) {
  const workflow = (settings && settings.workflow) || {};
  workflowSettings.outstandingTimeoutEnabled = !!workflow.outstandingTimeoutEnabled;
  workflowSettings.outstandingTimeoutDays = parseInt(workflow.outstandingTimeoutDays || '30', 10) || 30;
  workflowSettings.autoPromote = !!workflow.autoPromote;
}

function populateSystemSettingsForms(settings) {
  const smtp = (settings && settings.smtp) || {};
  const polaris = (settings && settings.polaris) || {};
  const emails = (settings && settings.emails) || {};

  populateSmtpSettingsForm(smtp, emails);
  populatePolarisSettingsForm(polaris);
}

function populateSmtpSettingsForm(smtp, emails) {
  setFieldValue('smtp-host', smtp.host || '');
  setFieldValue('smtp-port', smtp.port || 587);
  setFieldValue('smtp-username', '');
  setFieldValue('smtp-password', '');
  setVisible('smtp-username-status', !!smtp.usernameSet);
  setVisible('smtp-password-status', !!smtp.passwordSet);
  setFieldChecked('smtp-tls', smtp.tls !== false);

  // Also populate the duplicate SMTP fields with the emails value.
  setFieldValue('smtp-from', emails.fromAddress || '');
  setFieldValue('smtp-from-name', emails.fromName || '');
}

function populatePolarisSettingsForm(polaris) {
  setFieldValue('polaris-host', polaris.host || '');
  setFieldValue('polaris-api-key', polaris.apiKey || '');
  setFieldValue('polaris-access-id', polaris.accessId || '');
  setFieldValue('polaris-domain', polaris.staffDomain || '');
  setFieldValue('polaris-admin-user', polaris.adminUser || '');
  setFieldValue('polaris-admin-pass', polaris.adminPassword || '');
  setFieldValue('polaris-override-pass', polaris.overridePassword || '');
}

function showSettingsForm() {
  hideSettingsAccessDenied();
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.remove('hidden');
}

function handleLoadSettingsError(err, showErrors) {
  if (isPocketBaseAutoCancelError(err)) {
    return;
  }
  console.error('Failed to load settings', err);
  if (showErrors) {
    showSettingsAccessDenied();
  }
}



export async function populateLibrarySelector() {
  const select = document.getElementById('select-library-context');
  if (!select) return;

  try {
    const savedOrgId = readSavedSuperAdminLibraryContext();
    const selectedOrgId = savedOrgId || currentLibraryContextOrgId || select.value || 'system';
    select.disabled = true;
    select.innerHTML = '<option value="system">System Defaults</option>';

    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-library-selector'
    });

    orgs.forEach(org => {
      const opt = document.createElement('option');
      opt.value = org.organizationId;
      opt.textContent = `${org.displayName || org.name} (ID ${org.organizationId})`;
      select.appendChild(opt);
    });

    select.value = Array.from(select.options).some(option => option.value === selectedOrgId) ? selectedOrgId : 'system';
    
    if (isSuperAdminStaff()) {
      await fetchLibraryOverridesSummary();
      refreshLibrarySelectorIndicators();
    }

    setCurrentLibraryContextOrgId(select.value);

    saveSuperAdminLibraryContext(select.value);
    const selectedOption = select.options[select.selectedIndex];
    if (selectedOption) {
      document.getElementById('library-context-display').textContent = selectedOption.text;
    }

    if (!librarySelectorBound) {
      select.addEventListener('change', async (e) => {
        await switchLibraryContext(e.target.value || 'system', e.target);
      });
      setLibrarySelectorBound(true);
    }
  } catch (err) {
    if (!isPocketBaseAutoCancelError(err)) {
      console.error('Failed to populate library selector', err);
    }
  } finally {
    select.disabled = false;
  }
}

export function cloneLibrarySettingsSnapshot(settings) {
  return JSON.parse(JSON.stringify(settings || {}));
}

export function rememberLastSavedLibrarySettings(settings) {
  setLastSavedLibrarySettingsSnapshot(cloneLibrarySettingsSnapshot(settings));
  setLastSavedLibrarySettingsOrgId(currentLibraryContextOrgId || 'system');
}

export function applyLibrarySettingsToForm(settings) {
  settings = settings || {};
  const isOverride = !!settings.isOverride;
  const emails = settings.emails || {};
  const smtp = settings.smtp || {};
  const polaris = settings.polaris || {};
  setCurrentFormatClaimRules(settings.formatClaimRules || []);
  setFormatClaimStaffOptions(settings.formatClaimStaffOptions || []);
  setLeapBibUrlPattern(settings.leapBibUrlPattern || '');

  const resetBtn = document.getElementById('btn-reset-library-settings');
  const statusAlert = document.getElementById('library-override-status');
  const overrideMsg = document.getElementById('library-override-msg');

  if (currentLibraryContextOrgId === 'system') {
    if (resetBtn) resetBtn.classList.add('hidden');
    if (statusAlert) statusAlert.classList.add('hidden');
    if (document.getElementById('system-staff-url-group')) {
      document.getElementById('system-staff-url-group').classList.remove('hidden');
    }
    setFieldValue('system-staff-url', settings.staffUrl || '');
    setFieldValue('leap-bib-url-pattern', leapBibUrlPattern);
    if (document.getElementById('system-enabled-libraries-group')) {
      document.getElementById('system-enabled-libraries-group').classList.remove('hidden');
      renderLibraryParticipationCheckboxes();
    }
  } else {
    if (document.getElementById('system-staff-url-group')) {
      document.getElementById('system-staff-url-group').classList.add('hidden');
    }
    if (document.getElementById('system-enabled-libraries-group')) {
      document.getElementById('system-enabled-libraries-group').classList.add('hidden');
    }
    if (isOverride) {
      if (statusAlert) statusAlert.className = 'alert alert-info mb-3 d-flex justify-content-between align-items-center';
      if (overrideMsg) overrideMsg.innerHTML = '<i class="fa fa-check-circle mr-1"></i> Editing: <strong>' + escapeAttr(document.getElementById('library-context-display').textContent || 'selected library') + '</strong>. This library has custom settings.';
      if (resetBtn) resetBtn.classList.remove('hidden');
    } else {
      if (statusAlert) statusAlert.className = 'alert alert-warning mb-3 d-flex justify-content-between align-items-center';
      if (overrideMsg) overrideMsg.innerHTML = '<i class="fa fa-info-circle mr-1"></i> Editing: <strong>' + escapeAttr(document.getElementById('library-context-display').textContent || 'selected library') + '</strong>. This library is using system defaults. Saving will create a library-specific override.';
      if (resetBtn) resetBtn.classList.add('hidden');
    }
    updateLibraryOverrideStatusVisibility(currentSettingsSection);
  }

  // Only populate global fields (SMTP/Polaris) when in system context.
  // When viewing a library, these fields are hidden and should not be overwritten
  // with empty/stale data, which could cause data loss on save.
  if (currentLibraryContextOrgId === 'system') {
    setFieldValue('smtp-host', smtp.host || '');
    setFieldValue('smtp-port', smtp.port || 587);
    setFieldValue('smtp-username', '');
    setFieldValue('smtp-password', '');
    setVisible('smtp-username-status', !!smtp.usernameSet);
    setVisible('smtp-password-status', !!smtp.passwordSet);
    setFieldChecked('smtp-tls', smtp.tls !== false);
    setFieldValue('smtp-from', emails.fromAddress || '');
    setFieldValue('smtp-from-name', emails.fromName || '');
    setFieldValue('polaris-host', polaris.host || '');
    setFieldValue('polaris-api-key', polaris.apiKey || '');
    setFieldValue('polaris-access-id', polaris.accessId || '');
    setFieldValue('polaris-domain', polaris.staffDomain || '');
    setFieldValue('polaris-admin-user', polaris.adminUser || '');
    setFieldValue('polaris-admin-pass', polaris.adminPassword || '');
    setFieldValue('polaris-override-pass', polaris.overridePassword || '');

  }
  const fileInput = document.getElementById('ui-logo-file');
  if (fileInput) fileInput.value = '';

  populateEmailTemplateForms(emails);
  populatePatronUiForms(settings.ui_text || {});
  populateWorkflowForms(settings.workflow || {});
  workflowSettings.outstandingTimeoutEnabled = !!((settings.workflow || {}).outstandingTimeoutEnabled);
  workflowSettings.outstandingTimeoutDays = parseInt(((settings.workflow || {}).outstandingTimeoutDays) || '30', 10) || 30;
  workflowSettings.autoPromote = !!(settings.workflow || {}).autoPromote;
    updateEmailStatusBanner(settings.emailStatus);
    if (settings.organizationSync) {
      const state = settings.organizationSync.status || 'not_loaded';
      const message = settings.organizationSync.error || settings.organizationSync.message || organizationsStatusMessage;
      updateOrganizationsStatusUi(state, message);
    }
    updateSaveButtonText();
    // Refresh system-only section guard after settings population
    if (!settingsLoading) {
      activateSettingsSection(currentSettingsSection, { updateHash: false });
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

export async function switchLibraryContext(orgId, select = document.getElementById('select-library-context')) {
  const nextOrgId = orgId || 'system';
  const previousOrgId = currentLibraryContextOrgId || 'system';
  if (!select) return false;

  if (settingsDirty) {
    const proceed = await showConfirm('Unsaved changes', 'You have unsaved changes. Switch libraries without saving?');
    if (!proceed) {
      select.value = previousOrgId;
      return false;
    }
  }

  select.value = nextOrgId;
  setCurrentLibraryContextOrgId(nextOrgId);
  saveSuperAdminLibraryContext(nextOrgId);
  const selectedOption = select.options && select.options[select.selectedIndex];
  const contextDisplay = document.getElementById('library-context-display');
  if (selectedOption && contextDisplay) {
    contextDisplay.textContent = selectedOption.text;
  }

  await loadLibrarySettings(currentLibraryContextOrgId);
  markSettingsClean('clean');
  // Re-evaluate the active section after every context switch so system-only
  // guards and library override banners match the newly loaded scope.
  activateSettingsSection(currentSettingsSection, { updateHash: false });
  return true;
}

export async function handleLibraryContextSwitch(orgId) {
  const select = document.getElementById('select-library-context');
  return switchLibraryContext(orgId || 'system', select);
}

export async function loadLibrarySettings(orgId) {

  const requestedOrgId = orgId || 'system';
  incrementLibraryContextLoadSerial();
  const requestId = libraryContextLoadSerial;
  setCurrentLibraryContextOrgId(requestedOrgId);

  try {
    let settings = {};

    const result = await authorizedJson(`/api/asap/staff/settings/library?orgId=${encodeURIComponent(requestedOrgId)}&_=${Date.now()}`, { cache: 'no-store' });
    if (requestId !== libraryContextLoadSerial || requestedOrgId !== currentLibraryContextOrgId) {
      return; // A newer request is in flight
    }

    settings = result;
    rememberLastSavedLibrarySettings(settings);
    applyLibrarySettingsToForm(settings);
    await loadStaffAccessSettings();
    captureSettingsBaseline();
    return settings;

  } catch (err) {
    if (isPocketBaseAutoCancelError(err)) {
      return;
    }
    console.error('Error loading library settings:', err);
    showToast('Failed to load library settings', 'error');
  }
}

export function populateWorkflowForms(wf) {
  setFieldValue('suggestion-limit', wf.suggestionLimit !== undefined ? wf.suggestionLimit : '5');
  setFieldValue('suggestion-limit-msg', wf.suggestionLimitMessage || 'Weekly suggestion limit reached');
  document.getElementById('outstanding-timeout-enabled').checked = !!wf.outstandingTimeoutEnabled;
  setFieldValue('outstanding-timeout-days', wf.outstandingTimeoutDays !== undefined ? wf.outstandingTimeoutDays : '30');

  setFieldChecked('outstanding-timeout-send-email', !!wf.outstandingTimeoutSendEmail);
  workflowSettings.outstandingTimeoutSendEmail = !!wf.outstandingTimeoutSendEmail;
  workflowSettings.outstandingTimeoutRejectionTemplateId = wf.outstandingTimeoutRejectionTemplateId || '';

  document.getElementById('hold-pickup-timeout-enabled').checked = !!wf.holdPickupTimeoutEnabled;
  setFieldValue('hold-pickup-timeout-days', wf.holdPickupTimeoutDays !== undefined ? wf.holdPickupTimeoutDays : '14');
  document.getElementById('pending-hold-timeout-enabled').checked = !!wf.pendingHoldTimeoutEnabled;
  setFieldValue('pending-hold-timeout-days', wf.pendingHoldTimeoutDays !== undefined ? wf.pendingHoldTimeoutDays : '14');

  // Cache for checkbox renderer
  setLastWorkflowEnabledList((wf.enabledLibraryOrgIds || '').split(',').map(s => s.trim()).filter(s => s.length > 0));

  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (container) {
    const checkboxes = container.querySelectorAll('input[type="checkbox"]');
    checkboxes.forEach(cb => {
      cb.checked = lastWorkflowEnabledList.indexOf(cb.value) >= 0;
    });
  }

  toggleTimeoutGroup();
  updateAutoRejectEmailControls();
  toggleHoldPickupTimeoutGroup();
  togglePendingHoldTimeoutGroup();

  setFieldValue('wf-common-authors-label', wf.commonAuthorsLabel || 'Popular Creators');
  setFieldValue('wf-common-authors-help', wf.commonAuthorsHelp || 'See if this is a creator we already collect.');
  setFieldValue('wf-common-authors-list', wf.commonAuthorsList || '');
  setFieldValue('wf-common-authors-message', wf.commonAuthorsMessage || '');
  document.getElementById('wf-common-authors-enabled').checked = !!wf.commonAuthorsEnabled;
  toggleCommonAuthorsGroup();

  setFieldChecked('polaris-auto-promote', !!wf.autoPromote);
  setFieldChecked('allow-patron-autohold-opt-out', !!wf.allowPatronAutoholdOptOut);
  workflowSettings.autoPromote = !!wf.autoPromote;
  setFieldChecked('wf-external-search-1-enabled', !!wf.externalSearch1Enabled);
  setFieldValue('wf-external-search-1-label', wf.externalSearch1Label || 'Search Amazon');
  setFieldValue('wf-external-search-1-url-template', wf.externalSearch1UrlTemplate || 'https://www.amazon.com/s?k={{title}}');
  setFieldChecked('wf-external-search-2-enabled', !!wf.externalSearch2Enabled);
  setFieldValue('wf-external-search-2-label', wf.externalSearch2Label || 'Search Goodreads');
  setFieldValue('wf-external-search-2-url-template', wf.externalSearch2UrlTemplate || 'https://www.goodreads.com/search?q={{title}}');
  setFieldChecked('wf-external-search-3-enabled', !!wf.externalSearch3Enabled);
  setFieldValue('wf-external-search-3-label', wf.externalSearch3Label || 'Search WorldCat');
  setFieldValue('wf-external-search-3-url-template', wf.externalSearch3UrlTemplate || 'https://www.worldcat.org/search?q={{title}}');
  setFieldChecked('wf-external-search-4-enabled', !!wf.externalSearch4Enabled);
  setFieldValue('wf-external-search-4-label', wf.externalSearch4Label || '');
  setFieldValue('wf-external-search-4-url-template', wf.externalSearch4UrlTemplate || '');

  workflowSettings.externalSearch1Enabled = !!wf.externalSearch1Enabled;
  workflowSettings.externalSearch1Label = wf.externalSearch1Label || 'Search Amazon';
  workflowSettings.externalSearch1UrlTemplate = wf.externalSearch1UrlTemplate || 'https://www.amazon.com/s?k={{title}}';
  workflowSettings.externalSearch2Enabled = !!wf.externalSearch2Enabled;
  workflowSettings.externalSearch2Label = wf.externalSearch2Label || 'Search Goodreads';
  workflowSettings.externalSearch2UrlTemplate = wf.externalSearch2UrlTemplate || 'https://www.goodreads.com/search?q={{title}}';
  workflowSettings.externalSearch3Enabled = !!wf.externalSearch3Enabled;
  workflowSettings.externalSearch3Label = wf.externalSearch3Label || 'Search WorldCat';
  workflowSettings.externalSearch3UrlTemplate = wf.externalSearch3UrlTemplate || 'https://www.worldcat.org/search?q={{title}}';
  workflowSettings.externalSearch4Enabled = !!wf.externalSearch4Enabled;
  workflowSettings.externalSearch4Label = wf.externalSearch4Label || '';
  workflowSettings.externalSearch4UrlTemplate = wf.externalSearch4UrlTemplate || '';
}

export function populatePatronUiForms(uiText) {
  setFieldValue('ui-logo-alt', uiText.logoAlt || '');

  const preview = document.getElementById('ui-logo-preview');
  if (preview) {
    preview.src = (uiText.logoUrl || '/jpl.png') + (uiText.logoUrl && uiText.logoUrl.includes('?') ? '&' : '?') + 't=' + Date.now();
  }

  const statusBadge = document.getElementById('ui-branding-status');
  if (statusBadge) {
    const isInherited = !!uiText.brandingInherited;
    statusBadge.textContent = isInherited ? 'System Default' : 'Library Override';
    statusBadge.className = isInherited ? 'badge badge-warning' : 'badge badge-info';
  }

  const resetBtn = document.getElementById('btn-reset-logo');
  if (resetBtn) {
    resetBtn.classList.toggle('hidden', !!uiText.brandingInherited || currentLibraryContextOrgId === 'system');
  }

  // Clear file input label
  const fileLabel = document.querySelector('label[for="ui-logo-file"] + .custom-file-label') || document.querySelector('label[for="ui-logo-file"]');
  if (fileLabel) fileLabel.textContent = 'Choose image...';

  setFieldValue('ui-patron-page-title', uiText.pageTitle || '');
  setFieldValue('ui-barcode-label', uiText.barcodeLabel || '');
  setFieldValue('ui-pin-label', uiText.pinLabel || '');
  setFieldValue('ui-login-prompt', uiText.loginPrompt || 'Please enter your information below to start the suggestion process.');
  setFieldValue('ui-login-note', uiText.loginNote || 'Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.');
  setFieldValue('ui-suggestion-note', uiText.suggestionFormNote || 'If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don\'t see the email.');
  setFieldValue('ui-no-email-msg', uiText.noEmailMessage || 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.');
  const participationGroup = document.getElementById('ui-system-not-enabled-group');
  if (participationGroup) {
    participationGroup.classList.toggle('hidden', currentLibraryContextOrgId !== 'system');
  }
  setFieldValue('ui-system-not-enabled-msg', uiText.systemNotEnabledMessage || '{{library}} does not currently participate in this suggestion service.');
  const misconfiguredGroup = document.getElementById('ui-misconfigured-group');
  if (misconfiguredGroup) {
    misconfiguredGroup.classList.toggle('hidden', currentLibraryContextOrgId !== 'system');
  }
  setFieldValue('ui-misconfigured-msg', uiText.misconfiguredMessage || 'The {{library}} suggestion system is currently misconfigured. Please contact staff.');

  setFieldValue('ui-success-title', uiText.successTitle || 'Suggestion Submitted');
  setFieldValue('ui-success-msg', uiText.successMessage || 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>');
  setFieldValue('ui-already-submitted-msg', uiText.alreadySubmittedMessage || 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>');
  renderDuplicateStatusLabelSettings(uiText.duplicateStatusLabels || {}, uiText.duplicateStatusLabelsSource || '', !!uiText.duplicateStatusLabelsInherited);

  // Format Labels & Available Formats
  const labels = uiText.formatLabels || {};
  const order = Array.isArray(uiText.formatOrder) ? uiText.formatOrder.filter(k => Object.prototype.hasOwnProperty.call(labels, k)) : [];
  const orderedKeys = order.length ? order.concat(Object.keys(labels).filter(k => order.indexOf(k) < 0)) : Object.keys(labels);
  const available = uiText.availableFormats || ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];

  // Sync formatMap and availableFormats with backend data
  Object.keys(formatMap).forEach(k => delete formatMap[k]);
  orderedKeys.forEach(k => { formatMap[k] = labels[k]; });
  
  availableFormats.length = 0;
  available.forEach(k => availableFormats.push(k));

  renderFormatSettings();
  updateModalFormatDropdowns();

  renderOptionListEditor('ui-publication-options-editor', uiText.publicationOptions, defaultPublicationOptions);
  const patronScope = document.getElementById('patron-options-scope');
  if (patronScope) {
    if (currentLibraryContextOrgId === 'system') {
      patronScope.textContent = 'Editing global patron form defaults.';
      patronScope.className = 'small mt-2 mb-0 text-muted';
    } else if (uiText.patronSettingsInherited) {
      patronScope.textContent = 'Showing inherited global patron form options. Saving will create custom options for the selected library only.';
      patronScope.className = 'small mt-2 mb-0 text-warning';
    } else {
      patronScope.textContent = 'Editing custom patron form options for the selected library.';
      patronScope.className = 'small mt-2 mb-0 text-info';
    }
  }
  renderPatronFormatRulesEditor(uiText.formatRules);
  updatePublicationOptionsUi(uiText.publicationOptions);
}

function _serializeSettingsState(validate = false) {
  const isSystemContext = isSuperAdminStaff() && currentLibraryContextOrgId === 'system';

  function positiveInt(id, fallback, label) {
    const raw = getFieldValue(id, String(fallback)).trim();
    if (!raw) return fallback;
    const value = parseInt(raw, 10);
    if (!Number.isFinite(value) || value < 1) {
      if (validate) {
        throw new Error(`${label} must be a number greater than 0.`);
      }
      return value; // Return as-is for serialization if not validating
    }
    return value;
  }

  let staffUrl = '';
  let nextLeapBibUrlPattern = leapBibUrlPattern || '';
  if (isSystemContext) {
    staffUrl = getFieldValue('system-staff-url').trim();
    if (validate) {
      const staffUrlError = validateStaffUrl(staffUrl);
      if (staffUrlError) {
        throw new Error(staffUrlError);
      }
      staffUrl = normalizeStaffUrl(staffUrl);
      setFieldValue('system-staff-url', staffUrl);
    }
    nextLeapBibUrlPattern = getFieldValue('leap-bib-url-pattern').trim();
    if (validate) {
      nextLeapBibUrlPattern = normalizeLeapBibUrlPattern(nextLeapBibUrlPattern);
      setFieldValue('leap-bib-url-pattern', nextLeapBibUrlPattern);
    }
  }

  const uiText = {
    logoAlt: getFieldValue('ui-logo-alt'),
    pageTitle: getFieldValue('ui-patron-page-title'),
    barcodeLabel: getFieldValue('ui-barcode-label'),
    pinLabel: getFieldValue('ui-pin-label'),
    loginPrompt: getFieldValue('ui-login-prompt'),
    loginNote: getFieldValue('ui-login-note'),
    suggestionFormNote: getFieldValue('ui-suggestion-note'),
    noEmailMessage: getFieldValue('ui-no-email-msg'),
    systemNotEnabledMessage: isSystemContext ? getFieldValue('ui-system-not-enabled-msg') : undefined,
    misconfiguredMessage: isSystemContext ? getFieldValue('ui-misconfigured-msg') : undefined,
    successTitle: getFieldValue('ui-success-title'),
    successMessage: getFieldValue('ui-success-msg'),
    alreadySubmittedMessage: getFieldValue('ui-already-submitted-msg'),
    duplicateStatusLabels: collectDuplicateStatusLabels(),
    formatLabels: collectFormatLabels(),
    formatOrder: collectFormatOrder(),
    availableFormats: collectAvailableFormats(),
    publicationOptions: collectOptionList('ui-publication-options-editor', defaultPublicationOptions),
    formatRules: collectPatronFormatRules()
  };

  const emails = {
    fromAddress: getFieldValue('email-from-address'),
    fromName: getFieldValue('email-from-name'),
    suggestion_submitted: {
      subject: getFieldValue('email-submit-subject'),
      body: getFieldValue('email-submit-body')
    },
    already_owned: {
      subject: getFieldValue('email-owned-subject'),
      body: getFieldValue('email-owned-body')
    },
    rejected: {
      subject: getFieldValue('email-rejected-subject'),
      body: getFieldValue('email-rejected-body')
    },
    rejection_templates: JSON.parse(JSON.stringify(currentRejectionTemplates || [])),
    hold_placed: {
      subject: getFieldValue('email-hold-subject'),
      body: getFieldValue('email-hold-body')
    }
  };

  const sendAutoRejectEmail = getFieldChecked('outstanding-timeout-send-email');
  const nextAutoRejectTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');
  const externalSearch1UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-1-url-template').trim() || 'https://www.amazon.com/s?k={{title}}');
  const externalSearch2UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-2-url-template').trim() || 'https://www.goodreads.com/search?q={{title}}');
  const externalSearch3UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-3-url-template').trim() || 'https://www.worldcat.org/search?q={{title}}');
  const externalSearch4UrlTemplate = normalizeExternalSearchUrlTemplate(getFieldValue('wf-external-search-4-url-template'));

  if (validate) {
    setFieldValue('wf-external-search-1-url-template', externalSearch1UrlTemplate);
    setFieldValue('wf-external-search-2-url-template', externalSearch2UrlTemplate);
    setFieldValue('wf-external-search-3-url-template', externalSearch3UrlTemplate);
    setFieldValue('wf-external-search-4-url-template', externalSearch4UrlTemplate);
  }

  const payload = {
    ui_text: uiText, emails,
    formatClaimRules: collectFormatClaimRules(),
    suggestionLimit: positiveInt('suggestion-limit', 5, 'Suggestion limit'),
    suggestionLimitMessage: getFieldValue('suggestion-limit-msg'),
    outstandingTimeoutEnabled: getFieldChecked('outstanding-timeout-enabled'),
    outstandingTimeoutDays: positiveInt('outstanding-timeout-days', 30, 'Auto-reject stalled suggestions days'),
    outstandingTimeoutSendEmail: sendAutoRejectEmail,
    outstandingTimeoutRejectionTemplateId: nextAutoRejectTemplateId,
    holdPickupTimeoutEnabled: getFieldChecked('hold-pickup-timeout-enabled'),
    holdPickupTimeoutDays: positiveInt('hold-pickup-timeout-days', 14, 'Auto-close unpicked-up holds days'),
    pendingHoldTimeoutEnabled: getFieldChecked('pending-hold-timeout-enabled'),
    pendingHoldTimeoutDays: positiveInt('pending-hold-timeout-days', 14, 'Auto-close pending holds days'),
    commonAuthorsEnabled: getFieldChecked('wf-common-authors-enabled'),
    commonAuthorsLabel: getFieldValue('wf-common-authors-label').trim() || 'Popular Creators',
    commonAuthorsHelp: getFieldValue('wf-common-authors-help').trim() || 'See if this is a creator we already collect.',
    commonAuthorsList: sortAuthorsByLastName(getFieldValue('wf-common-authors-list')),
    commonAuthorsMessage: getFieldValue('wf-common-authors-message'),
    autoPromote: getFieldChecked('polaris-auto-promote'),
    allowPatronAutoholdOptOut: getFieldChecked('allow-patron-autohold-opt-out'),
    externalSearch1Enabled: getFieldChecked('wf-external-search-1-enabled'),
    externalSearch1Label: getFieldValue('wf-external-search-1-label').trim() || 'Search Amazon',
    externalSearch1UrlTemplate: externalSearch1UrlTemplate,
    externalSearch2Enabled: getFieldChecked('wf-external-search-2-enabled'),
    externalSearch2Label: getFieldValue('wf-external-search-2-label').trim() || 'Search Goodreads',
    externalSearch2UrlTemplate: externalSearch2UrlTemplate,
    externalSearch3Enabled: getFieldChecked('wf-external-search-3-enabled'),
    externalSearch3Label: getFieldValue('wf-external-search-3-label').trim() || 'Search WorldCat',
    externalSearch3UrlTemplate: externalSearch3UrlTemplate,
    externalSearch4Enabled: getFieldChecked('wf-external-search-4-enabled'),
    externalSearch4Label: getFieldValue('wf-external-search-4-label').trim(),
    externalSearch4UrlTemplate: externalSearch4UrlTemplate
  };

  if (isSystemContext) {
    payload.smtp = {
      host: getFieldValue('smtp-host').trim(),
      port: positiveInt('smtp-port', 587, 'SMTP port'),
      username: getFieldValue('smtp-username').trim(),
      password: getFieldValue('smtp-password'),
      tls: getFieldChecked('smtp-tls', true)
    };
    payload.polaris = collectSettingsPolaris();
    payload.staffUrl = staffUrl;
    payload.leapBibUrlPattern = nextLeapBibUrlPattern;
    payload.enabledLibraryOrgIds = collectEnabledLibraryIds();
  }

  return payload;
}

export function serializeSettingsState() {
  return _serializeSettingsState(false);
}

export function buildSettingsPayload() {
  return _serializeSettingsState(true);
}

export function captureSettingsBaseline() {
  setInitialSettingsSnapshot(JSON.stringify(serializeSettingsState()));
}

export function checkSettingsDirty() {
  if (!initialSettingsSnapshot) return false;
  const currentState = JSON.stringify(serializeSettingsState());
  return currentState !== initialSettingsSnapshot;
}

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
    // Only validate SMTP when editing system defaults (library admins can't see SMTP)
    if (isSuperAdminStaff() && currentLibraryContextOrgId === 'system') {
      if (!validateSmtpHostField(true)) {
        throw new Error('SMTP host is invalid.');
      }
    }
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();

    // Save via the library-scoped API
    // System-only fields (smtp, polaris, staffUrl, leapBibUrlPattern) are only
    // included when saving system defaults. Library saves must never send these.
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
        enabledLibraryOrgIds: payload.enabledLibraryOrgIds,
        commonAuthorsEnabled: payload.commonAuthorsEnabled,
        commonAuthorsLabel: payload.commonAuthorsLabel,
        commonAuthorsHelp: payload.commonAuthorsHelp,
        commonAuthorsList: payload.commonAuthorsList,
        commonAuthorsMessage: payload.commonAuthorsMessage,
        autoPromote: payload.autoPromote,
        allowPatronAutoholdOptOut: payload.allowPatronAutoholdOptOut,
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

    // Only include system-scoped fields when saving system defaults
    if (isSystemSave) {
      libraryPayload.staffUrl = payload.staffUrl;
      libraryPayload.leapBibUrlPattern = payload.leapBibUrlPattern;
      libraryPayload.smtp = payload.smtp;
      libraryPayload.polaris = payload.polaris;
    }

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify(libraryPayload)
    });

    await libraryPromise;
    captureSettingsBaseline();
    msg.textContent = options.successText || 'Settings saved.';
    msg.className = 'mt-2 font-weight-bold text-success';
    if (options.clearDelay !== 0) {
      setTimeout(() => msg.textContent = '', options.clearDelay || 3000);
    }
    await loadSettings({ showErrors: false }); // Sync internal state (also triggers loadLibrarySettings)
    await loadStaffConfig(); // Refresh logo and titles immediately after saving
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

export function updateSaveButtonText() {
  const saveBtn = document.getElementById('settings-save-btn');
  if (saveBtn) {
    saveBtn.textContent = currentLibraryContextOrgId === 'system'
      ? 'Save System Defaults'
      : 'Save Library Settings';
  }
}

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

export async function loadStaffConfig() {
  try {
    const res = await fetch('/api/asap/config');
    const config = await res.json();
    if (config) {
      if (config.logoUrl) {
        document.getElementById('app-icon').href = config.logoUrl;
        document.getElementById('setup-logo').src = config.logoUrl;
        document.getElementById('login-logo').src = config.logoUrl;
        document.getElementById('nav-logo').src = config.logoUrl;
      }
      if (config.logoAlt) {
        document.getElementById('setup-logo').alt = config.logoAlt;
        document.getElementById('login-logo').alt = config.logoAlt;
        document.getElementById('nav-logo').alt = config.logoAlt;
      }
      updatePublicationOptionsUi(config.publicationOptions);
    }
  } catch (err) {
    console.error('Failed to load global config');
  }
}

export async function initStaffApp() {
  closeOpenDialogs();
  closeActionMenu?.();
  initSettingsNavigation();
  await loadStaffConfig();
  await loadSetupStatus();
  checkAuth();
}


document.getElementById('outstanding-timeout-enabled').addEventListener('change', () => {
  toggleTimeoutGroup();
  updateAutoRejectEmailControls();
});
document.getElementById('outstanding-timeout-send-email').addEventListener('change', updateAutoRejectEmailControls);

export function toggleTimeoutGroup() {
  const group = document.getElementById('timeout-config-group');
  const enabled = document.getElementById('outstanding-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function toggleHoldPickupTimeoutGroup() {
  const group = document.getElementById('hold-pickup-timeout-group');
  const enabled = document.getElementById('hold-pickup-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function togglePendingHoldTimeoutGroup() {
  const group = document.getElementById('pending-hold-timeout-group');
  const enabled = document.getElementById('pending-hold-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function toggleCommonAuthorsGroup() {
  const group = document.getElementById('common-authors-config-group');
  const enabled = document.getElementById('wf-common-authors-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function sortAuthorsByLastName(authorsListStr) {
  if (!authorsListStr) return '';
  const authors = authorsListStr.split('\n').map(s => s.trim()).filter(s => s.length > 0);
  authors.sort((a, b) => {
    // Basic last name detection (everything after the first comma, or the last word)
    const getLastName = (name) => {
      if (name.includes(',')) return name.split(',')[0].trim();
      const parts = name.split(' ');
      return parts[parts.length - 1].trim();
    };
    const lastA = getLastName(a).toLowerCase();
    const lastB = getLastName(b).toLowerCase();
    return lastA.localeCompare(lastB);
  });
  return authors.join('\n');
}

document.getElementById('hold-pickup-timeout-enabled').addEventListener('change', toggleHoldPickupTimeoutGroup);
document.getElementById('pending-hold-timeout-enabled').addEventListener('change', togglePendingHoldTimeoutGroup);
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
  const originalText = btn.innerHTML;
  btn.innerHTML = '<i class="fa fa-spinner fa-spin"></i> Saving...';

  try {
    const res = await fetch(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(currentLibraryContextOrgId)}`, {
      method: 'POST',
      headers: {
        'Authorization': pb.authStore.token
      },
      body: formData
    });

    // Check if response is JSON before parsing to avoid "Unexpected token in JSON" errors on 500s
    const isJson = res.headers.get('content-type')?.includes('application/json');
    const data = isJson ? await res.json() : null;

    if (!res.ok) {
      console.error('Server Error:', data);
      throw new Error(data?.message || `Server error: ${res.status}`);
    }
    
    showToast('Branding updated successfully.');
    // Reload settings to refresh the preview and system config
    await loadSettings({ showErrors: true });
    await loadStaffConfig(); // Refresh nav logo
  } catch (err) {
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = originalText;
  }
});

document.getElementById('btn-reset-logo').addEventListener('click', async () => {
  if (!await showConfirm('Reset branding?', 'This will delete the library-specific logo and fallback to the system default.')) {
    return;
  }

  const btn = document.getElementById('btn-reset-logo');
  btn.disabled = true;

  try {
    const res = await fetch(`/api/asap/staff/settings/logo?orgId=${encodeURIComponent(currentLibraryContextOrgId)}`, {
      method: 'DELETE',
      headers: {
        'Authorization': pb.authStore.token
      }
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.message || 'Failed to reset branding');
    
    showToast('Branding reset to system defaults.');
    await loadSettings({ showErrors: true });
    await loadStaffConfig();
  } catch (err) {
    showToast(err.message, 'error');
  } finally {
    btn.disabled = false;
  }
});
